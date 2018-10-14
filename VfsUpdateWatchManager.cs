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

namespace Foxpict.Service.Core {
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

  /// <summary>
  /// 仮想ファイル監視マネージャ
  /// </summary>
  public class VspFileUpdateWatchManager {
    private readonly Logger mLogger;

    readonly Container container;

    /// <summary>
    /// CPU使用率をOSから取得するためのオブジェクト
    /// </summary>
    //readonly System.Diagnostics.PerformanceCounter _CpuCounter = null;

    /// <summary>
    /// 定期的にインデックス作成処理を実行するためのタイマー
    /// </summary>
    readonly System.Timers.Timer _IndexQueueTimer;

    /// <summary>
    /// WindowsAPIを使用したファイルシステム監視ロジック
    /// </summary>
    readonly FileSystemWatcher _Watcher;

    /// <summary>
    /// FS監視対象のワークスペース情報
    /// </summary>
    IWorkspace _Workspace;

    /// <summary>
    /// 更新イベント除外ファイルパスリスト
    /// </summary>
    /// <remarks>
    /// 更新イベントの内部処理により、別の更新イベントが発生する可能性があるファイルのコレクションです。
    /// 値には更新イベントが発生する可能性がある仮想ディレクトリ空間のファイルパス(フルパス)が含まれます。
    /// </remarks>
    ConcurrentQueue<string> _IgnoreUpdateFiles = new ConcurrentQueue<string> ();

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
    ConcurrentDictionary<string, FileUpdateQueueItem> _UpdatesWatchFiles = new ConcurrentDictionary<string, FileUpdateQueueItem> ();

    /// <summary>
    /// ファイルシステム変更通知により、変更があったディレクトリです。
    /// </summary>
    /// <typeparam name="string"></typeparam>
    /// <typeparam name="DirectoryInfo"></typeparam>
    /// <returns></returns>
    ConcurrentDictionary<string, DirectoryInfo> _UpdateDirectory = new ConcurrentDictionary<string, DirectoryInfo> ();

    /// <summary>
    /// ディレクトリ移動の同一判定で、移動を行ったディレクトリ名を格納する
    /// </summary>
    /// <remarks><pre>
    /// 空文字の場合は、ディレクトリの移動が行われていないことを示す。
    /// ディレクトリ移動の同一判定では、「Delete→Create」が短時間で発生しているかチェックする。
    /// 最長期限は、インデックス生成タイマー実行までで、Createイベントまたはインデックス生成タイマーにより
    /// この変数はクリア(空文字)される。
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
    public VspFileUpdateWatchManager (Container container) {
      this.mLogger = LogManager.GetCurrentClassLogger ();
      this.container = container;

      //this._CpuCounter = RuntimePerformanceUtility.CreateCpuCounter();
      this._Watcher = new FileSystemWatcher ();

      // 検索対象
      this._Watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Security | NotifyFilters.DirectoryName;

      // サブディレクトリも監視対象。
      this._Watcher.IncludeSubdirectories = true;

      this._Watcher.Changed += OnWatcherChanged;
      this._Watcher.Created += OnWatcherCreated;
      this._Watcher.Deleted += OnWatcherDeleted;
      this._Watcher.Renamed += OnWatcherRenamed;

      // タイマー設定
      this._IndexQueueTimer = new System.Timers.Timer (30000); // 30sec
      this._IndexQueueTimer.Elapsed += OnIndexTimerElapsed;
      this._IndexQueueTimer.Enabled = true;
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
      sb.AppendLine ("イベント発生数:" + _UpdatesWatchFiles.Count);
      foreach (var prop in _UpdatesWatchFiles) {
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
    public void StartWatch (IWorkspace workspace) {
      this._Workspace = workspace;

      try {
        _Watcher.Path = _Workspace.VirtualPath;
        _Watcher.EnableRaisingEvents = true;
        mLogger.Info ("[{0}]のファイル監視を開始します", _Watcher.Path);
      } catch (Exception expr) {
        mLogger.Warn ("ファイルシステムの監視に失敗しました({0})", expr.Message);
      }
    }

    /// <summary>
    /// ファイルシステムの監視を停止します
    /// </summary>
    public void StopWatch () {
      _Watcher.EnableRaisingEvents = false;
    }

    /// <summary>
    /// 定期的に、ファイル操作があったファイルに対するVFS処理を行う
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
          var relativeDirPath = _Workspace.TrimWorekspacePath (sameDirectoryOperation_FullPath);
          using (AsyncScopedLifestyle.BeginScope (container)) {
            var fileMappingInfoRepository = container.GetInstance<IFileMappingInfoRepository> ();

            foreach (var prop in fileMappingInfoRepository.FindPathWithStart (relativeDirPath)) {
              var fileUpdateQueueItem = new FileUpdateQueueItem { Target = new FileInfo (Path.Combine (_Workspace.VirtualPath, prop.MappingFilePath + ".aclgene")) };
              fileUpdateQueueItem.Recents.Add (new RecentInfo { EventType = WatcherChangeTypes.Deleted });
              _UpdatesWatchFiles.AddOrUpdate (prop.MappingFilePath, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            }
          }
        }
      }

      //
      foreach (var @pair in _UpdatesWatchFiles.ToList ()) {
        // 最後のファイル監視状態から、一定時間経過している場合のみ処理を行う。
        var @diff = DateTime.Now - @pair.Value.LastUpdate;

        if (@diff.Seconds >= 10) // 10秒 以上経過
        {
          FileUpdateQueueItem item; // work
          if (_UpdatesWatchFiles.TryRemove (@pair.Key, out item)) {
            var @lastItem = item.Recents.LastOrDefault ();

            // NOTE: UpdateVirtualSpaceFlowワークフローを呼び出す
            mLogger.Info ("ワークフロー実行 [{1}] 対象ファイルパス={0}", item.Target.FullName, @lastItem.EventType);

            // ワークフロー処理中に発生するファイル更新イベントにより、更新キューに項目が追加されてしまうことを防ぐため、
            // 処理中のファイルを更新キューから除外するための除外リストに、処理中のファイルを追加する。
            //
            // ※処理中のファイルがACLファイル以外の場合、対象ファイルのACLファイル名も除外リストに追加する
            _IgnoreUpdateFiles.Enqueue (item.Target.FullName);
            if (item.Target.Extension != ".aclgene")
              _IgnoreUpdateFiles.Enqueue (item.Target.FullName + ".aclgene");

            using (var scope = FoxpictAsyncScopedLifestyle.BeginScope (container)) {
              try {
                var workspaceRepository = container.GetInstance<IWorkspaceRepository> ();
                var workspace = workspaceRepository.Load (_Workspace.Id);
                if (workspace == null) workspace = _Workspace;

                var fileUpdateRunner = container.GetInstance<IFileUpdateRunner> ();

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
            _IgnoreUpdateFiles.TryDequeue (out ignoreUpdateFile);
            if (item.Target.Extension != ".aclgene")
              _IgnoreUpdateFiles.TryDequeue (out ignoreUpdateFile);
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

      // 走査対象のディレクトリに含まれるファイル一覧を検索する
      foreach (var pair in _UpdateDirectory.ToList ()) {
        foreach (var fileInfo in pair.Value.GetFiles ("*", SearchOption.AllDirectories)) {
          // LOG.Debug("\t FileInfo:{0}", @fis.FullName);

          // システムファイルに対しては処理を行わない。
          if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            return;
          if ((fileInfo.Attributes & FileAttributes.Temporary) == FileAttributes.Temporary)
            return;
          if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
            return;

          // 除外リストに含まれていない場合は、処理対象ファイルとして登録する
          if (!_IgnoreUpdateFiles.Contains (fileInfo.FullName)) {
            UpdateOrInsertUpdatedFileQueueItem (fileInfo, WatcherChangeTypes.Created, null);
          }

          DirectoryInfo dummy;
          _UpdateDirectory.TryRemove (pair.Key, out dummy);
        }
      }

      timer.Enabled = true;
    }

    /// <summary>
    /// FileSystemWatcher.Changedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherChanged (object sender, FileSystemEventArgs e) {
      _IndexQueueTimer.Stop ();
      try {
        mLogger.Info ("OnWatcherChanged  FullPath:{0}", e.FullPath);

        // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
        // 以降はFileInfoを使用して処理を進めるところがキモ。
        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (_IgnoreUpdateFiles.Contains (fileInfo.FullName)) {
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
        _IndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Createdイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherCreated (object sender, FileSystemEventArgs e) {
      _IndexQueueTimer.Stop ();
      try {

        mLogger.Info ("OnWatcherCreated  FullPath:{0}", e.FullPath);

        // FS更新イベント対象別処理：ファイル or ディレクトリ
        if (File.Exists (e.FullPath)) {
          // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
          // 以降はFileInfoを使用して処理を進めるところがキモ。
          FileInfo fileInfo = new FileInfo (e.FullPath);
          if (_IgnoreUpdateFiles.Contains (fileInfo.FullName)) {
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
          if (_IgnoreUpdateFiles.Contains (directoryInfo.FullName)) {
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

          _UpdateDirectory.AddOrUpdate (directoryInfo.FullName, directoryInfo, (key, value) => directoryInfo);
        }
      } finally {
        _IndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Deletedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherDeleted (object sender, FileSystemEventArgs e) {
      _IndexQueueTimer.Stop ();

      try {
        // 2016/4/23 処理見直し
        //           Deleteイベントは、ファイルシステムからファイル(ディレクトリ)が削除済みなので、
        //           FileInfoを取得できません。
        //           また、ディレクトリの削除の場合、ディレクトリ内のファイル一覧を取得できません。

        mLogger.Info ("OnWatcherDeleted  FullPath:{0}", e.FullPath);

        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (_IgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
          return;
        }

        // 削除イベントに関しては、ACLファイル以外は更新キューには入れない
        if (fileInfo.Extension == ".aclgene") {
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
        _IndexQueueTimer.Start ();
      }
    }

    /// <summary>
    /// FileSystemWatcher.Renamedイベントのハンドラ
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    void OnWatcherRenamed (object sender, RenamedEventArgs e) {
      _IndexQueueTimer.Stop ();
      try {
        mLogger.Info ("OnWatcherRenamed\n  FullPath:{0}\n   OldPath:{1}", e.FullPath, e.OldFullPath);

        // eを手放してイベントの呼び出し元に返すために、e.FullPathからFileInfoを作成し、
        // 以降はFileInfoを使用して処理を進めるところがキモ。
        FileInfo fileInfo = new FileInfo (e.FullPath);
        if (_IgnoreUpdateFiles.Contains (fileInfo.FullName)) {
          mLogger.Info ("{0}は除外リストに含まれるため、イベントコレクションには追加しない", fileInfo.FullName);
          return;
        }

        var oldFullPath_relatived = _Workspace.TrimWorekspacePath (e.OldFullPath);

        if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory) {
          // ディレクトリのリネームでは、そのディレクトリ構造に属するすべてのファイルの情報を更新する。

          var @dir = new DirectoryInfo (e.FullPath);
          //LOG.Debug("\t DirectoryInfo:{0}", @dir.FullName);

          foreach (var @fis in @dir.GetFiles ("*", SearchOption.AllDirectories)) {
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
        _IndexQueueTimer.Start ();
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
            var deletedFileRelativePath = this._Workspace.TrimWorekspacePath (watchTarget.FullName);
            var r = from u in _UpdatesWatchFiles
            where u.Value.OldRenameNamePath == deletedFileRelativePath
            select u;
            var prop = r.FirstOrDefault ();
            if (prop.Key != null) {
              _UpdatesWatchFiles.TryGetValue (prop.Key, out fileUpdateQueueItem);
            } else {

              fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

              _UpdatesWatchFiles.AddOrUpdate (deletedFileRelativePath,
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

            if (!_UpdatesWatchFiles.ContainsKey (aclhash)) {
              fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

              _UpdatesWatchFiles.AddOrUpdate (aclhash, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            } else {
              // キューから情報を取得
              _UpdatesWatchFiles.TryGetValue (aclhash, out fileUpdateQueueItem);
              fileUpdateQueueItem.Target = watchTarget; // 登録している物理ファイル情報を最新のオブジェクトにする
            }

            // 最後に更新イベントが発生した時のファイルパスを格納しておく(Deleteイベント用)
            fileUpdateQueueItem.OldRenameNamePath = this._Workspace.TrimWorekspacePath (watchTarget.FullName);
          }
        } else {
          if (watcherChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrEmpty (beforeRenamedName)) {

            // 変更内容がリネームの場合、名前変更前のファイルパスで登録済みの項目を取得し、
            // 名前変更前の項目はキューから削除します。
            // 名前変更後の項目として、新たにキューに再登録を行います。
            var renamedFullName = watchTarget.FullName.Replace (watchTarget.FullName, beforeRenamedName);

            var oldkey = this._Workspace.TrimWorekspacePath (renamedFullName);
            key = this._Workspace.TrimWorekspacePath (watchTarget.FullName);

            if (_UpdatesWatchFiles.ContainsKey (oldkey)) {
              // 古いキーの項目をキューから削除します。
              // 新しいキーで、キューに情報を再登録します。
              _UpdatesWatchFiles.TryRemove (oldkey, out fileUpdateQueueItem);
              _UpdatesWatchFiles.AddOrUpdate (key, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
            }
          } else if (watcherChangeType == WatcherChangeTypes.Created) {
            key = this._Workspace.TrimWorekspacePath (watchTarget.FullName);
          } else {
            key = this._Workspace.TrimWorekspacePath (watchTarget.FullName);
          }

          // 更新通知があったファイルが処理キューに未登録の場合、キューに更新通知情報を新規登録します
          if (!_UpdatesWatchFiles.ContainsKey (key)) {
            fileUpdateQueueItem = new FileUpdateQueueItem { Target = watchTarget };

            _UpdatesWatchFiles.AddOrUpdate (key, fileUpdateQueueItem, (_key, _value) => fileUpdateQueueItem);
          } else {
            // キューから情報を取得
            _UpdatesWatchFiles.TryGetValue (key, out fileUpdateQueueItem);
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
  }
}
