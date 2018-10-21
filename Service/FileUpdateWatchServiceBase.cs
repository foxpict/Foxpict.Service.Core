using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Foxpict.Service.Core.Structure;
using Foxpict.Service.Core.Vfs;
using Foxpict.Service.Infra;
using Foxpict.Service.Infra.Model;
using Foxpict.Service.Infra.Repository;
using Microsoft.Extensions.Logging;
using NLog;
using ProtoBuf;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Foxpict.Service.Core.Service {

  /// <summary>
  /// 仮想ファイル監視マネージャ
  /// </summary>
  public class FileUpdateWatchServiceBase {
    private readonly Logger mLogger;

    /// <summary>
    /// DIコンテナ
    /// </summary>
    protected readonly Container container;

    /// <summary>
    /// CPU使用率をOSから取得するためのオブジェクト
    /// </summary>
    //readonly System.Diagnostics.PerformanceCounter _CpuCounter = null;

    /// <summary>
    /// 定期的にインデックス作成処理を実行するためのタイマー
    /// </summary>
    readonly System.Timers.Timer mIndexQueueTimer;

    /// <summary>
    /// WindowsAPIを使用したファイルシステム監視ロジック
    /// </summary>
    readonly FileSystemWatcher mWatcherVirtual; // 仮想領域用

    readonly FileSystemWatcher mWatcherImport; // インポート領域用

    /// <summary>
    /// FS監視対象のワークスペース情報
    /// </summary>
    IWorkspace mWorkspace;

    /// <summary>
    /// 更新イベント除外ファイルパスリスト
    /// </summary>
    /// <remarks>
    /// 更新イベントの内部処理により、別の更新イベントが発生する可能性があるファイルのコレクションです。
    /// 値には更新イベントが発生する可能性がある仮想ディレクトリ空間のファイルパス(フルパス)が含まれます。
    /// </remarks>
    ConcurrentQueue<string> mIgnoreUpdateFiles = new ConcurrentQueue<string> ();

    /// <summary>
    /// タイマーを使用した、インデックス作成処理実行機能のON/OFF
    /// </summary>
    private bool _IsSuspendIndex;

    /// <summary>
    /// ファイルシステムの変更通知により変更があった可能性のあるファイル一覧です。
    /// マップのキーにはファイルへのフルパスが含まれます。
    /// </summary>
    /// <remarks>
    /// ファイルシステムを監視しているスレッド以外から安全に辞書にアクセスできるように
    /// スレッドセーフな辞書クラスを使用しています。
    /// </remarks>
    /// <typeparam name="string">ファイルフルパス</typeparam>
    /// <typeparam name="FileUpdateQueueItem"></typeparam>
    ConcurrentDictionary<string, FileUpdateQueueItem> mUpdatesWatchFiles = new ConcurrentDictionary<string, FileUpdateQueueItem> ();

    /// <summary>
    /// ファイルシステム変更通知により、変更があったディレクトリです。
    /// </summary>
    /// <typeparam name="string"></typeparam>
    /// <typeparam name="DirectoryInfo"></typeparam>
    /// <returns></returns>
    ConcurrentDictionary<string, DirectoryInfo> mUpdateDirectory = new ConcurrentDictionary<string, DirectoryInfo> ();

    /// <summary>
    /// ディレクトリ移動の同一判定で、移動を行ったディレクトリ名を格納する
    /// </summary>
    /// <remarks><pre>
    /// 空文字の場合は、ディレクトリの移動が行われていないことを示す。
    /// ディレクトリ移動の同一判定では、「Delete→Create」が短時間で発生しているかチェックする。
    /// 最長期限はインデックス生成タイマー実行までで、Createイベントまたはインデックス生成タイマー処理の実行で
    /// この変数をクリア(空文字)する。
    /// </pre></remarks>
    string sameDirectoryOperation_FullPath = "";

    /// <summary>
    /// ディレクトリ移動の同一判定関連の変数に対するクリティカルセクション
    /// </summary>
    object sameDirectoryOperation_Locker = new object ();

    /// <summary>
    /// ディレクトリ移動の同一判定で、同一と判断するためのディレクトリ名
    /// </summary>
    string sameDirectoryOperation_Name = "";

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="container"></param>
    public FileUpdateWatchServiceBase (Container container) {
      this.mLogger = LogManager.GetCurrentClassLogger ();
      this.container = container;

      //this._CpuCounter = RuntimePerformanceUtility.CreateCpuCounter();
      mWatcherVirtual = new FileSystemWatcher ();
      mWatcherImport = new FileSystemWatcher ();

      SetupWatcher (mWatcherVirtual, true, true, true, true);
      SetupWatcher (mWatcherImport, false, true, false, false);

      // タイマー設定
      this.mIndexQueueTimer = new System.Timers.Timer (30000); // 30sec
      this.mIndexQueueTimer.Elapsed += OnIndexTimerElapsed;
      this.mIndexQueueTimer.Enabled = true;
    }

    private void SetupWatcher (FileSystemWatcher watcher, bool enableChange, bool enableCreate, bool enableDelete, bool enableRename) {
      watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Security | NotifyFilters.DirectoryName;

      // サブディレクトリも監視対象。
      watcher.IncludeSubdirectories = true;

      if (enableChange)
        watcher.Changed += OnWatcherChanged;
      if (enableCreate)
        watcher.Created += OnWatcherCreated;
      if (enableDelete)
        watcher.Deleted += OnWatcherDeleted;
      if (enableRename)
        watcher.Renamed += OnWatcherRenamed;
    }

    /// <summary>
    /// インデックス作成処理実行機能のON/OFFを取得、または設定します。
    /// </summary>
    public bool IsSuspendIndex {
      get { return _IsSuspendIndex; }
      set {
        if (_IsSuspendIndex == value)
          return;
        _IsSuspendIndex = value;
      }
    }

    /// <summary>
    /// イベントが発生した更新情報のダンプ文字列を返します
    /// </summary>
    /// <returns></returns>
    public string DumpUpdateWatchedFile () {
      const string indentText = "  ";
      StringBuilder sb = new StringBuilder ();
      sb.AppendLine ("イベント発生数:" + mUpdatesWatchFiles.Count);
      foreach (var prop in mUpdatesWatchFiles) {
        sb.Append ("★Key=").Append (prop.Key).AppendLine ();
        sb.Append (indentText).Append ("更新回数=").Append (prop.Value.Recents.Count);
        sb.AppendLine ();
        sb.Append (indentText).Append ("  Target=").Append (prop.Value.Target.FullName);

        foreach (var recent in prop.Value.Recents) {
          sb.Append (indentText).Append (indentText).Append (recent.EventType);
        }

        if (prop.Value.Recents.Count > 0) sb.Append ("*");

        // 更新情報に古いパスを含む場合のみ、古いパスを出力します
        if (!string.IsNullOrEmpty (prop.Value.OldRenameNamePath))
          sb.AppendLine ().Append (indentText).Append ("OldPath=").Append (indentText).Append (prop.Value.OldRenameNamePath);

        sb.AppendLine ();
      }

      return sb.ToString ();
    }

    /// <summary>
    /// ファイルシステムの監視を開始します
    /// </summary>
    /// <param name="workspace">監視対象のディレクトリパスを含むワークスペース情報</param>
    public void StartWatchByVirtualPath (IWorkspace workspace) {
      this.mWorkspace = workspace;

      try {
        mWatcherVirtual.Path = mWorkspace.VirtualPath;
        mWatcherVirtual.EnableRaisingEvents = true;
        mLogger.Info ("[{0}]のファイル監視を開始します", mWatcherVirtual.Path);

        mWatcherImport.Path = mWorkspace.ImportPath;
        mWatcherImport.EnableRaisingEvents = true;
        mLogger.Info ("[{0}]のファイル監視を開始します", mWatcherImport.Path);

      } catch (Exception expr) {
        mLogger.Warn ("ファイルシステムの監視に失敗しました({0})", expr.Message);
        throw new ApplicationException ();
      }
    }

    /// <summary>
    /// ファイルシステムの監視を停止します
    /// </summary>
    public void StopWatch () {
      mWatcherVirtual.EnableRaisingEvents = false;
    }

    /// <summary>
    /// ファイルに対するVFS処理を定期実行します
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnIndexTimerElapsed (object sender, System.Timers.ElapsedEventArgs e) {
      if (this.IsSuspendIndex) return; // サスペンド時はインデックス生成処理はスキップする

      // インデックス生成処理中は、このメソッドを呼び出すタイマーは停止しておきます。
      var timer = sender as System.Timers.Timer;
      timer.Enabled = false;

      mLogger.Info ("タイマー処理の実行");

      // ディレクトリ削除イベントが発生している場合、
      // 削除したディレクトリに含まれていたファイルを、削除したパスから見つけ出して削除処理を行うキューに追加する
      lock (sameDirectoryOperation_Locker) {
        if (sameDirectoryOperation_Name != "") {
          sameDirectoryOperation_Name = "";
          var relativeDirPath = mWorkspace.TrimWorekspacePath (sameDirectoryOperation_FullPath);
          using (AsyncScopedLifestyle.BeginScope (container)) {
            var fileMappingInfoRepository = container.GetInstance<IFileMappingInfoRepository> ();

            foreach (var prop in fileMappingInfoRepository.FindPathWithStart (relativeDirPath)) {
              var fileUpdateQueueItem = new FileUpdateQueueItem { Target = new FileInfo (Path.Combine (mWatcherVirtual.Path, prop.MappingFilePath + ".aclgene")) };
              fileUpdateQueueItem.Recents.Add (new RecentInfo { EventType = WatcherChangeTypes.Deleted });
              mUpdatesWatchFiles.AddOrUpdate (prop.MappingFilePath, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            }
          }
        }
      }

      //
      foreach (var @pair in mUpdatesWatchFiles.ToList ()) {
        // 最後のファイル監視状態から、一定時間経過している場合のみ処理を行う。
        var @diff = DateTime.Now - @pair.Value.LastUpdate;

        if (@diff.Seconds >= 10) // 10秒 以上経過
        {
          FileUpdateQueueItem item; // work
          if (mUpdatesWatchFiles.TryRemove (@pair.Key, out item)) {
            var @lastItem = item.Recents.LastOrDefault ();

            // NOTE: UpdateVirtualSpaceFlowワークフローを呼び出す
            mLogger.Info ("ワークフロー実行 [{1}] 対象ファイルパス={0}", item.Target.FullName, @lastItem.EventType);

            // ワークフロー処理中に発生するファイル更新イベントにより、更新キューに項目が追加されてしまうことを防ぐため、
            // 処理中のファイルを更新キューから除外するための除外リストに、処理中のファイルを追加する。
            //
            // ※処理中のファイルがACLファイル以外の場合、対象ファイルのACLファイル名も除外リストに追加する
            mIgnoreUpdateFiles.Enqueue (item.Target.FullName);
            if (item.Target.Extension != ".aclgene") {
              var localPath = mWorkspace.TrimWorekspacePath (item.Target.FullName);
              var vpath = Path.Combine (mWorkspace.VirtualPath, localPath);
              mIgnoreUpdateFiles.Enqueue (vpath + ".aclgene"); // 仮想領域に作成するACLファイルを除外リストに追加
            }

            using (var scope = FoxpictAsyncScopedLifestyle.BeginScope (container)) {
              try {
                var workspaceRepository = container.GetInstance<IWorkspaceRepository> ();
                var workspace = workspaceRepository.Load (mWorkspace.Id);
                if (workspace == null) workspace = mWorkspace;

                // VFS機能のサービスを取得する
                var fileUpdateRunner = container.GetInstance<IFileUpdateRunner> ();
                if (workspace.ContainsImport (item.Target)) {
                  // インポート領域のファイルの場合は、カテゴリのパース処理を実施する。
                  fileUpdateRunner.EnableCategoryParse = true;
                }

                // 処理対象のファイルがACLファイルか、物理ファイルかで処理を切り分けます
                // ■ACLファイルの場合
                //    リネーム更新イベントに対応します。
                // ■物理ファイルの場合
                //    リネーム更新イベントも、UPDATEイベントとして処理します。
                if (item.Target.Extension == ".aclgene") {
                  var fileNameWithputExtension = item.Target.Name.Replace (item.Target.Extension, "");
                  switch (@lastItem.EventType) {
                    case WatcherChangeTypes.Renamed:
                      fileUpdateRunner.file_rename_acl (item.Target, workspace);
                      break;
                    case WatcherChangeTypes.Changed:
                    case WatcherChangeTypes.Created:
                      fileUpdateRunner.file_create_acl (item.Target, workspace);
                      break;
                    case WatcherChangeTypes.Deleted:
                      fileUpdateRunner.file_remove_acl (item.Target, workspace);
                      break;
                  }
                } else {
                  if (File.Exists (item.Target.FullName)) {
                    switch (@lastItem.EventType) {
                      case WatcherChangeTypes.Renamed:
                      case WatcherChangeTypes.Changed:
                      case WatcherChangeTypes.Created:
                        fileUpdateRunner.file_create_normal (item.Target, workspace);
                        break;
                      case WatcherChangeTypes.Deleted:
                        break;
                    }
                  } else {
                    mLogger.Info ("「{0}」は存在しない物理ファイルのため、処理をスキップします。", item.Target.FullName);
                  }
                }
                scope.Complete ();
              } catch (Exception expr) {
                mLogger.Error (expr, "VFSの処理に失敗しました。");
                mLogger.Debug (expr.StackTrace);

                if (expr.InnerException != null) {
                  mLogger.Error (expr.InnerException.Message);
                  mLogger.Error (expr.InnerException.StackTrace);
                }
              }
            }

            // 処理を終了したファイルを、除外リストから削除します
            string ignoreUpdateFile;
            mIgnoreUpdateFiles.TryDequeue (out ignoreUpdateFile);
            if (item.Target.Extension != ".aclgene")
              mIgnoreUpdateFiles.TryDequeue (out ignoreUpdateFile);
          }

        }

        // [CPU使用率に対するループ遅延を行う]
        // var cpuPer = _CpuCounter.NextValue();
        // if (cpuPer > 90.0)
        // {
        // 	await Task.Delay(100); // 100msec待機
        // }
        // else if (cpuPer > 30.0)
        // {
        // 	//await Task.Delay(10); // 10msec待機
        // }
      }

      // [FileWatcherコンポーネントの、ファイルイベント通知漏れ対策]
      // 1つでもファイルイベントが発生しているフォルダは、そのフォルダに属するファイルを処理キューに「新規作成アイテム」として登録する。
      // ただし、処理済みのファイルまで登録してしまうと2度処理を行うことになるため、除外リストに含まれているファイルは処理キューへの登録は行わない。
      foreach (var pair in mUpdateDirectory.ToList ()) {
        var itemDirectory = new DirectoryInfo (pair.Value.FullName);

        if (mWorkspace.ContainsImport (itemDirectory)) {
          // フォルダが、インポート領域の場合
          if (!itemDirectory.Exists) {
            // インポート領域では、Deleteイベントは受け取らないため、
            // フォルダ有無を検証し、フォルダが削除済みの場合は更新ディレクトリキューから削除する。
            DirectoryInfo dummy;
            mUpdateDirectory.TryRemove (pair.Key, out dummy);
            mLogger.Info ($"キューから処理対象のディレクトリ({dummy.Name})を削除");
            continue;
          }

          RegisterDirectoryFiles (itemDirectory);

          // インポート領域内で、かつ空フォルダの場合は、フォルダを削除する
          if (itemDirectory.GetFiles ("*", SearchOption.AllDirectories).Length == 0) {
            try {
              itemDirectory.Delete (true);
            } catch (Exception expr) {
              mLogger.Error (expr, $"フォルダの削除({itemDirectory.Name})に失敗しました。");
              throw expr;
            }
            mLogger.Info ($"インポート領域内のディレクトリ({itemDirectory.Name})を削除しました。");

            DirectoryInfo dummy;
            mUpdateDirectory.TryRemove (pair.Key, out dummy);
            mLogger.Info ($"キューから処理対象のディレクトリ({dummy.Name})を削除");
          }
        } else {
          // フォルダが、仮想領域の場合
          RegisterDirectoryFiles (itemDirectory);

          DirectoryInfo dummy;
          mUpdateDirectory.TryRemove (itemDirectory.FullName, out dummy);
        }
      }

      timer.Enabled = true;
    }

    /// <summary>
    /// ディレクトリ内のファイルを、処理キューに登録する
    /// </summary>
    /// <param name="directoryInfo">対象ディレクトリ</param>
    void RegisterDirectoryFiles (DirectoryInfo directoryInfo) {
      foreach (var fileInfo in directoryInfo.GetFiles ("*", SearchOption.AllDirectories)) {
        // LOG.Debug("\t FileInfo:{0}", @fis.FullName);

        // システムファイルに対しては処理を行わない。
        if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
          return;
        if ((fileInfo.Attributes & FileAttributes.Temporary) == FileAttributes.Temporary)
          return;
        if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
          return;

        // 除外リストに含まれていない場合は、処理対象ファイルとして登録する
        if (!mIgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          UpdateOrInsertUpdatedFileQueueItem (fileInfo, WatcherChangeTypes.Created, null);
        }
      }
    }

    /// <summary>
    /// FileSystemWatcher.Changedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherChanged (object sender, FileSystemEventArgs e) {
      mIndexQueueTimer.Stop ();
      try {
        mLogger.Info ("OnWatcherChanged  FullPath:{0}", e.FullPath);

        // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
        // 以降はFileInfoを使用して処理を進めるところがキモ。
        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (mIgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
          return;
        }

        // システムファイルに対しては処理を行わない。
        if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
          return;
        if ((fileInfo.Attributes & FileAttributes.Temporary) == FileAttributes.Temporary)
          return;
        if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
          return;

        if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory) {
          // ディレクトリのChangeイベントは、特に意味は無いため何もしない
          // ⇒ディレクトリ自体の最終更新日時を更新するために、キューに追加したほうがよいかも
          //LOG.Debug("\t「{0}」はディレクトリのため処理しません。", e.FullPath);
          return;
        }

        UpdateOrInsertUpdatedFileQueueItem (fileInfo, e.ChangeType, null);
      } finally {
        mIndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Createdイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherCreated (object sender, FileSystemEventArgs e) {
      mIndexQueueTimer.Stop ();
      try {

        mLogger.Info ("OnWatcherCreated  FullPath:{0}", e.FullPath);

        // FS更新イベント対象別処理：ファイル or ディレクトリ
        if (File.Exists (e.FullPath)) {
          // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
          // 以降はFileInfoを使用して処理を進めるところがキモ。
          FileInfo fileInfo = new FileInfo (e.FullPath);
          if (mIgnoreUpdateFiles.Contains (fileInfo.FullName)) {
            mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
            return;
          }

          if (!fileInfo.Exists) mLogger.Debug ("\tパス「{0}」が処理前に消滅しました", e.FullPath);

          // システムファイルに対しては処理を行わない。
          if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            return;
          if ((fileInfo.Attributes & FileAttributes.Temporary) == FileAttributes.Temporary)
            return;
          if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
            return;

          UpdateOrInsertUpdatedFileQueueItem (fileInfo, e.ChangeType, null);
        } else {
          var directoryInfo = new DirectoryInfo (e.FullPath);
          if (mIgnoreUpdateFiles.Contains (directoryInfo.FullName)) {
            mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", directoryInfo.FullName);
            return;
          }

          if (!directoryInfo.Exists) mLogger.Info ("\tパス「{0}」が処理前に消滅しました", e.FullPath);
          lock (sameDirectoryOperation_Locker) {
            // ディレクトリ移動操作であるかチェックする
            var dir = new DirectoryInfo (e.Name);
            if (sameDirectoryOperation_Name == dir.Name) {
              mLogger.Info ("ディレクトリ移動操作として処理します");
              sameDirectoryOperation_Name = "";
            }
          }

          mLogger.Info ($"処理対象のディレクトリ({directoryInfo.Name})を追加");
          mUpdateDirectory.AddOrUpdate (directoryInfo.FullName, directoryInfo, (key, value) => directoryInfo);
        }
      } finally {
        mIndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Deletedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherDeleted (object sender, FileSystemEventArgs e) {
      mIndexQueueTimer.Stop ();

      try {
        // 2016/4/23 処理見直し
        //           Deleteイベントは、ファイルシステムからファイル(ディレクトリ)が削除済みなので、
        //           FileInfoを取得できません。
        //           また、ディレクトリの削除の場合、ディレクトリ内のファイル一覧を取得できません。

        mLogger.Info ("OnWatcherDeleted  FullPath:{0}", e.FullPath);

        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (mIgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
          return;
        }

        if (fileInfo.Extension == ".aclgene") {
          // 削除イベントは、ACLファイル以外は更新キューに追加しない
          UpdateOrInsertUpdatedFileQueueItem (fileInfo, e.ChangeType, null);
        } else {
          lock (sameDirectoryOperation_Locker) {
            // e.Nameからディレクトリ名だけを取得するために、DirectoryInfoを使用する
            var dir = new DirectoryInfo (e.Name);

            if (sameDirectoryOperation_Name != dir.Name) {
              sameDirectoryOperation_Name = dir.Name;
              sameDirectoryOperation_FullPath = e.FullPath;
            }
          }
        }
      } finally {
        mIndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Renamedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherRenamed (object sender, RenamedEventArgs e) {
      mIndexQueueTimer.Stop ();
      try {
        mLogger.Info ("OnWatcherRenamed\n  FullPath:{0}\n   OldPath:{1}", e.FullPath, e.OldFullPath);

        // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
        // 以降はFileInfoを使用して処理を進めるところがキモ。
        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (mIgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
          return;
        }

        var oldFullPath_relatived = mWorkspace.TrimWorekspacePath (e.OldFullPath);

        if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory) {
          // ディレクトリのリネームでは、そのディレクトリ構造に属するすべてのファイルの情報を更新する。

          var @dir = new DirectoryInfo (e.FullPath);
          //LOG.Debug("\t DirectoryInfo:{0}", @dir.FullName);

          var files = @dir.GetFiles ("*", SearchOption.AllDirectories);
          foreach (var @fis in files) {
            //LOG.Debug("\t FileInfo:{0}", @fis.FullName);
            UpdateOrInsertUpdatedFileQueueItem (@fis, WatcherChangeTypes.Created,
              Path.Combine (oldFullPath_relatived, @fis.Name)
            );
          }
          return;
        } else {
          // システムファイルに対しては処理を行わない。
          if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            return;
          if ((fileInfo.Attributes & FileAttributes.Temporary) == FileAttributes.Temporary)
            return;
          if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
            return;

          UpdateOrInsertUpdatedFileQueueItem (fileInfo, WatcherChangeTypes.Renamed, oldFullPath_relatived);
        }
      } finally {
        mIndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// ファイル更新情報キューに、ファイル更新情報を追加する
    /// </summary>
    /// <remarks>
    /// 対象パスがキューに登録済みの場合は、登録情報の更新を行います。
    /// 未登録の場合は、情報を新規登録します。
    /// </remarks>
    /// <param name="watchTarget">変更通知が発生した、ファイル情報</param>
    /// <param name="watcherChangeType">変更内容区分</param>
    /// <param name="beforeRenamedName">変更内容区分がリネームの場合、リネーム前のファイル名を入力してください</param>
    FileUpdateQueueItem UpdateOrInsertUpdatedFileQueueItem (FileInfo watchTarget, WatcherChangeTypes watcherChangeType, string beforeRenamedName) {
      FileUpdateQueueItem fileUpdateQueueItem;
      string key;

      lock (this) {
        // 更新イベントの対象ファイルが、ACLファイルか物理ファイルか更新キューに使用するキーが異なる。
        // ACLファイルでは、ACLハッシュをキーに使用します。
        // 物理ファイルでは、ファイルパスをキーに使用します。
        if (watchTarget.Extension == ".aclgene") {
          // ACLファイルの場合、更新イベントを追わなくてもACLハッシュで常にどのファイルが更新キュー内のどこにあるかがわかる
          // ※ただし、ファイル削除を除く

          if (watcherChangeType == WatcherChangeTypes.Deleted) {
            var deletedFileRelativePath = this.mWorkspace.TrimWorekspacePath (watchTarget.FullName);
            var r = from u in mUpdatesWatchFiles
            where u.Value.OldRenameNamePath == deletedFileRelativePath
            select u;
            var prop = r.FirstOrDefault ();
            if (prop.Key != null) {
              mUpdatesWatchFiles.TryGetValue (prop.Key, out fileUpdateQueueItem);
            } else {

              fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

              mUpdatesWatchFiles.AddOrUpdate (deletedFileRelativePath,
                fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem
              );

              // 発生はありえないが、発生した場合は処理せず終了
              // →発生はありうる。
              //LOG.Warn("更新キューに登録されていないACLファイルの削除イベント");
              //return null;
            }
          } else {
            // ACLファイルからACLデータを取得
            AclFileStructure aclFileData;
            using (var file = File.OpenRead (watchTarget.FullName)) {
              aclFileData = Serializer.Deserialize<AclFileStructure> (file);
            }

            var aclhash = aclFileData.FindKeyValue ("ACLHASH");

            if (!mUpdatesWatchFiles.ContainsKey (aclhash)) {
              fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

              mUpdatesWatchFiles.AddOrUpdate (aclhash, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            } else {
              // キューから情報を取得
              mUpdatesWatchFiles.TryGetValue (aclhash, out fileUpdateQueueItem);
              fileUpdateQueueItem.Target = watchTarget; // 登録している物理ファイル情報を最新のオブジェクトにする
            }

            // 最後に更新イベントが発生した時のファイルパスを格納しておく(Deleteイベント用)
            fileUpdateQueueItem.OldRenameNamePath = this.mWorkspace.TrimWorekspacePath (watchTarget.FullName);
          }
        } else {
          if (watcherChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrEmpty (beforeRenamedName)) {

            // 変更内容がリネームの場合、名前変更前のファイルパスで登録済みの項目を取得し、
            // 名前変更前の項目はキューから削除します。
            // 名前変更後の項目として、新たにキューに再登録を行います。
            var renamedFullName = watchTarget.FullName.Replace (watchTarget.FullName, beforeRenamedName);

            var oldkey = this.mWorkspace.TrimWorekspacePath (renamedFullName);
            key = this.mWorkspace.TrimWorekspacePath (watchTarget.FullName);

            if (mUpdatesWatchFiles.ContainsKey (oldkey)) {
              // 古いキーの項目をキューから削除します。
              // 新しいキーで、キューに情報を再登録します。
              mUpdatesWatchFiles.TryRemove (oldkey, out fileUpdateQueueItem);
              mUpdatesWatchFiles.AddOrUpdate (key, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            }
          } else if (watcherChangeType == WatcherChangeTypes.Created) {
            key = this.mWorkspace.TrimWorekspacePath (watchTarget.FullName);
          } else {
            key = this.mWorkspace.TrimWorekspacePath (watchTarget.FullName);
          }

          // 更新通知があったファイルが処理キューに未登録の場合、キューに更新通知情報を新規登録します
          if (!mUpdatesWatchFiles.ContainsKey (key)) {
            fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

            mUpdatesWatchFiles.AddOrUpdate (key, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
          } else {
            // キューから情報を取得
            mUpdatesWatchFiles.TryGetValue (key, out fileUpdateQueueItem);
            fileUpdateQueueItem.Target = watchTarget; // 登録している物理ファイル情報を最新のオブジェクトにする
          }

          // 更新通知のイベント区分が『リネーム』の場合、元のファイル名も保存しておく。
          // イベント処理前は物理ディレクトリ空間のファイルパスはリネーム前のパスのままなので、
          // リネーム前のファイル名のみを保存します。
          if (watcherChangeType == WatcherChangeTypes.Renamed &&
            string.IsNullOrEmpty (fileUpdateQueueItem.OldRenameNamePath))
            fileUpdateQueueItem.OldRenameNamePath = beforeRenamedName;

        }

        // 情報に、履歴を追加
        var now = DateTime.Now;
        fileUpdateQueueItem.LastUpdate = now;
        var rec = new RecentInfo {
          EventType = watcherChangeType,
          RecentDate = now
        };
        fileUpdateQueueItem.Recents.Add (rec);

        return fileUpdateQueueItem;
      }
    }

    /// <summary>
    /// キューに登録するための、更新のあった物理ファイル情報のクラスです
    /// </summary>
    public class FileUpdateQueueItem {

      /// <summary>
      /// 同一ファイルで複数回の更新があった場合に時系列で更新イベントを並べるコレクション
      /// </summary>
      //public ObservableSynchronizedCollection<RecentInfo> Recents = new ObservableSynchronizedCollection<RecentInfo>();
      public List<RecentInfo> Recents = new List<RecentInfo> ();

      /// <summary>
      /// 最後にFileUpdateQueueItemを更新した時刻
      /// </summary>
      public DateTime LastUpdate { get; set; }

      /// <summary>
      /// 変更前の名称を取得します。
      /// </summary>
      /// <remarks>
      /// 名称変更更新があった場合に、変更前の名称を設定します。
      /// 名称変更更新が複数回行われても、最初に名称変更した際の元のファイル名(ディレクトリ名)を格納します。
      /// </remarks>
      public string OldRenameNamePath { get; set; }

      /// <summary>
      /// ウォッチした更新対象
      /// </summary>
      public FileSystemInfo Target { get; set; }

    }

    /// <summary>
    /// 対象のファイルへのイベントが同時に複数発生した場合の発生順序を記録する
    /// </summary>
    public class RecentInfo {
      /// <summary>
      ///
      /// </summary>
      public WatcherChangeTypes EventType { get; set; }

      /// <summary>
      /// 記録日時
      /// </summary>
      public DateTime RecentDate { get; set; }
    }
  }
}
