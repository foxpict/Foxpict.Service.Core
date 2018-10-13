using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Foxpict.Service.Core.Structure;
using Foxpict.Service.Infra;
using Foxpict.Service.Infra.Core;
using Foxpict.Service.Infra.Model;
using Foxpict.Service.Infra.Repository;
using Foxpict.Service.Infra.Utils;
using Katalib.Nc.Standard.String;
using NLog;
using ProtoBuf;

namespace Foxpict.Service.Core.Vfs {
  public class FileUpdateRunner : IFileUpdateRunner {
    static Logger LOG = LogManager.GetCurrentClassLogger ();

    public static readonly string MSG_NEWCATEGORY = "Pixstock.MSG_NEWCATEGORY";

    /// <summary>
    /// カテゴリ名からラベル情報を取得するために使用するルールの最大数
    /// </summary>
    public static readonly int MAX_CATEGORYPARSEREGE = 1000;

    public static readonly string CategoryNameParserPropertyKey = "FullBuildCategoryNameParser";

    public static readonly string CategoryLabelNameParserPropertyKey = "FullBuildCategoryLabelNameParser";

    readonly IAppAppMetaInfoRepository mAppAppMetaInfoRepository;

    readonly IFileMappingInfoRepository mFileMappingInfoRepository;

    readonly ICategoryRepository mCategoryRepository;

    readonly IContentRepository mContentRepository;

    readonly ILabelRepository mLabelRepository;

    readonly IThumbnailBuilder mTumbnailBuilder;

    readonly IMessagingManager mMessagingManager;

    readonly IEventLogRepository mEventLogRepository;

    readonly IVirtualFileSystemService mVirtualFileSystemService;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="fileMappingInfoRepository"></param>
    /// <param name="categoryRepository"></param>
    /// <param name="contentRepository"></param>
    /// <param name="thumbnailBuilder"></param>
    public FileUpdateRunner (IFileMappingInfoRepository fileMappingInfoRepository, ICategoryRepository categoryRepository, IContentRepository contentRepository, IThumbnailBuilder thumbnailBuilder, IAppAppMetaInfoRepository appAppMetaInfoRepository, ILabelRepository labelRepository, IMessagingManager messagingManager,
      IEventLogRepository eventLogRepository,
      IVirtualFileSystemService virtualFileSystemService) {
      this.mFileMappingInfoRepository = fileMappingInfoRepository;
      this.mCategoryRepository = categoryRepository;
      this.mContentRepository = contentRepository;
      this.mTumbnailBuilder = thumbnailBuilder;
      this.mAppAppMetaInfoRepository = appAppMetaInfoRepository;
      this.mLabelRepository = labelRepository;
      this.mMessagingManager = messagingManager;
      this.mEventLogRepository = eventLogRepository;
      this.mVirtualFileSystemService = virtualFileSystemService;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="item"></param>
    /// <param name="workspace"></param>
    public void file_create_acl (FileUpdateQueueItem item, IWorkspace workspace) {
      // 1. 対象ファイルが存在するかチェック
      if (!item.Target.Exists)
        throw new ApplicationException ("対象ファイルが指定位置に存在しません。");

      // 3. ACLファイルから、ACLハッシュを取得する
      var aclbin = ReadACLFile (new FileInfo (item.Target.FullName));
      var aclhash = aclbin.FindKeyValue ("ACLHASH");

      // 4. データベースを参照し、ACLハッシュとファイルマッピング情報(AclHash)を突き合わせる
      IFileMappingInfo entity = mFileMappingInfoRepository.LoadByAclHash (aclhash);
      if (entity == null) throw new ApplicationException ();
      if (entity.GetWorkspace () == null || entity.GetWorkspace ().Id != workspace.Id)
        throw new ApplicationException ();

      // 5. ファイルマッピング情報を参照し、物理ファイルが存在するかチェックする
      var phyFileInfo = new FileInfo (Path.Combine (workspace.PhysicalPath, entity.MappingFilePath));
      if (!phyFileInfo.Exists) throw new ApplicationException ();

      // 6. 物理ファイルを、ACLファイルのパスに対応する物理空間のパスへ移動する
      //    移動先が、同じ場所となる場合は処理しない。
      var aclfileLocalPath_Update = workspace.TrimWorekspacePath (item.Target.FullName);
      var extFilePath = Path.Combine (Path.GetDirectoryName (aclfileLocalPath_Update), Path.GetFileNameWithoutExtension (aclfileLocalPath_Update));
      var toFileInfo = new FileInfo (Path.Combine (workspace.PhysicalPath, extFilePath));
      if (phyFileInfo.FullName != toFileInfo.FullName) {
        Directory.CreateDirectory (toFileInfo.Directory.FullName);
        File.Move (phyFileInfo.FullName, toFileInfo.FullName);

        // 7. ファイルマッピング情報をDBに書き込む(コンテキスト初期化)
        entity = mFileMappingInfoRepository.LoadByAclHash (aclhash);
        entity.MappingFilePath = extFilePath; // 新しいファイルマップパス
      }

      mFileMappingInfoRepository.Save ();
    }

    /// <summary>
    /// [LLD-03-05-01:01-01-01]
    /// </summary>
    public void file_create_normal (FileUpdateQueueItem item, IWorkspace workspace) {
      // 1. 対象ファイルが存在するかチェック
      if (!item.Target.Exists)
        throw new ApplicationException ("対象ファイルが指定位置に存在しません。");
      if (!(item.Target is FileInfo))
        throw new ApplicationException ("ファイル以外は処理できません");

      var aclfileLocalPath_Update = workspace.TrimWorekspacePath (item.Target.FullName);

      var fileMappingInfo = mVirtualFileSystemService.RegisterFile ((FileInfo) item.Target, workspace);

      // 6. コンテンツ情報の作成(Category作成→Content作成→タグ・ラベル作成)
      var content = UpdateContentFromFileMapping (fileMappingInfo);

      // 7. サムネイル作成
      GenerateArtifact (content, workspace);
    }

    /// <summary>
    /// LLD-03-05-01:01-02-01
    /// </summary>
    /// <param name="item"></param>
    /// <param name="workspace"></param>
    public void file_remove_acl (FileUpdateQueueItem item, IWorkspace workspace) {
      var aclfileLocalPath_Remove = workspace.TrimWorekspacePath (item.Target.FullName);
      var vrPath_Remove = Path.Combine (Path.GetDirectoryName (aclfileLocalPath_Remove), Path.GetFileNameWithoutExtension (aclfileLocalPath_Remove));

      // 1. 削除したファイルパスと一致するファイルマッピング情報を取得する
      var fmi = mFileMappingInfoRepository.LoadByPath (vrPath_Remove);

      // 2. ファイルマッピング情報から、物理空間のファイルを特定する
      var phyFilePath = Path.Combine (workspace.PhysicalPath, vrPath_Remove);

      // 3. 物理空間のファイルを削除する
      File.Delete (phyFilePath);

      // 4. ファイルマッピング情報をデータベースから削除する
      mFileMappingInfoRepository.Delete (fmi);
      mFileMappingInfoRepository.Save ();
    }

    /// <summary>
    /// [LLD-03-05-01:01-03-01]
    /// </summary>
    /// <param name="item"></param>
    /// <param name="workspace"></param>
    public void file_rename_acl (FileUpdateQueueItem item, IWorkspace workspace) {
      // 1. 対象ファイルが存在するかチェック
      if (!item.Target.Exists)
        throw new ApplicationException ("対象ファイルが指定位置に存在しません。");

      // 3. ACLファイルから、ACLハッシュを取得する
      var aclbin = ReadACLFile (new FileInfo (item.Target.FullName));
      var aclhash = aclbin.FindKeyValue ("ACLHASH");

      // 4. データベースを参照し、ACLハッシュとファイルマッピング情報(AclHash)を突き合わせる
      IFileMappingInfo entity = mFileMappingInfoRepository.LoadByAclHash (aclhash);
      if (entity == null) throw new ApplicationException ();
      if (entity.GetWorkspace () == null || entity.GetWorkspace ().Id != workspace.Id)
        throw new ApplicationException ();

      // 5. ファイルマッピング情報を参照し、物理ファイルが存在するかチェックする
      var phyFileInfo = new FileInfo (Path.Combine (workspace.PhysicalPath, entity.MappingFilePath));
      if (!phyFileInfo.Exists) throw new ApplicationException ();

      // 6. 物理空間のファイルを、リネーム後のACLファイル名と同じ名前に変更する
      var aclfileLocalPath_Update = workspace.TrimWorekspacePath (item.Target.FullName);
      var extFilePath = Path.Combine (Path.GetDirectoryName (aclfileLocalPath_Update), Path.GetFileNameWithoutExtension (aclfileLocalPath_Update));
      var toFileInfo = new FileInfo (Path.Combine (workspace.PhysicalPath, extFilePath));
      if (phyFileInfo.FullName != toFileInfo.FullName) {
        Directory.CreateDirectory (toFileInfo.Directory.FullName);
        File.Move (phyFileInfo.FullName, toFileInfo.FullName);

        // 7. ファイルマッピング情報をDBに書き込む(コンテキスト初期化)
        entity = mFileMappingInfoRepository.LoadByAclHash (aclhash);
        entity.MappingFilePath = extFilePath; // 新しいファイルマップパス
        mFileMappingInfoRepository.Save ();
      }
    }

    /// <summary>
    /// ファイルマッピング情報から、コンテント情報の作成または更新を行う。
    /// </summary>
    /// <param name="fileMappingInfo">ファイルマッピング情報</param>
    private IContent UpdateContentFromFileMapping (IFileMappingInfo fileMappingInfo) {
      // FileMappingInfoがContentとの関連が存在する場合、新規のArtifactは作成できないので例外を投げる。
      if (fileMappingInfo.Id != 0L) {
        var a = mContentRepository.Load (fileMappingInfo);
        if (a != null) throw new ApplicationException ("既にコンテント情報が作成済みのFileMappingInfoです。");
      }

      //---
      //!+ パス文字列から、階層構造を持つカテゴリを取得／作成を行うロジック
      //---

      /* DUMMY */
      var appcat = mCategoryRepository.Load (1L); // ルートカテゴリを取得する(ルートカテゴリ取得に使用するIDをハードコートで指定しているが、これは暫定対応)
      if (appcat == null) throw new ApplicationException ("ルートカテゴリが見つかりません");

      //処理内容
      //   ・パスに含まれるカテゴリすべてが永続化されること
      //   ・Contentが永続化されること

      // パス文字列を、トークン区切りでキュー配列に詰めるロジック
      string pathText = fileMappingInfo.MappingFilePath;

      // 下記のコードは、Akalibへユーティリティとして実装する(パスを区切ってQueueを作成するユーティリティ)
      // 有効なパス区切り文字は、下記のコードでチェックしてください。
      // LOG.Info("トークン文字列1:　Token: {}", Path.AltDirectorySeparatorChar);
      // LOG.Info("トークン文字列2:　Token: {}", Path.DirectorySeparatorChar);
      // LOG.Info("トークン文字列3:　Token: {}", Path.PathSeparator);
      // LOG.Info("トークン文字列4:　Token: {}", Path.VolumeSeparatorChar);
      //
      // Windows環境: Path.DirectorySeparatorChar
      // Unix環境: Path.AltDirectorySeparatorChar
      var sttokens = new Stack<string> (pathText.Split (Path.DirectorySeparatorChar, StringSplitOptions.None));
      var title = sttokens.Pop ();
      var qutokens = new Queue<string> (sttokens.Reverse<string> ());

      // 各トークン（パス文字列）のカテゴリを取得、または存在しない場合はカテゴリを新規作成する。
      while (qutokens.Count > 0) {
        string parsedCategoryName;
        bool categoryCreatedFlag = false;
        bool parseSuccessFlag = false;
        var oneText = qutokens.Dequeue ();
        parseSuccessFlag = AttachParsedCategoryName (oneText, out parsedCategoryName);
        appcat = CreateOrSelectCategory (appcat, parsedCategoryName, out categoryCreatedFlag);

        if (categoryCreatedFlag && parseSuccessFlag) {
          mMessagingManager.Dispatcher (MSG_NEWCATEGORY, appcat.Id);
        }

        AttachParsedLabel (oneText, appcat);
      }

      // **コンテント情報を保持するエンティティの作成**
      //    現Verでは画像のみ、メタ情報を生成できる (それ以外のファイルは、例外を投げる)
      if (fileMappingInfo.Mimetype == "image/png") {
        var entity = mContentRepository.New ();
        entity.Name = title;
        entity.SetFileMappingInfo (fileMappingInfo);
        entity.SetCategory (appcat);
        mContentRepository.Save ();

        return entity;
      } else {
        throw new ApplicationException ("処理不能なMIMEタイプです");
      }
      // --------------------
      // ここまで
      // --------------------
    }

    /// <summary>
    /// 子階層に名称が一致するカテゴリがある場合は、そのカテゴリ情報を返します。
    /// ない場合は、新しいカテゴリを作成し、作成したカテゴリ情報を返します。
    /// </summary>
    /// <param name="parentCategory"></param>
    /// <param name="categoryName"></param>
    /// <returns></returns>
    private ICategory CreateOrSelectCategory (ICategory parentCategory, string categoryName, out bool createdFlag) {
      ICategory category = null;
      foreach (var child in mCategoryRepository.FindChildren (parentCategory)) {
        if (child.Name == categoryName) {
          category = child;
          break;
        }
      }

      if (category == null) {
        category = mCategoryRepository.New ();
        category.Name = categoryName;
        category.SetParentCategory (parentCategory);
        mCategoryRepository.Save ();

        // EventLog登録
        var eventLog = mEventLogRepository.New ();
        eventLog.Message = string.Format ("カテゴリ({})を新規登録しました", categoryName);
        eventLog.EventDate = DateTime.Now;
        eventLog.Sender = "Core";
        eventLog.EventId = (int) EventLogType.REGISTERCONTENT_VFSWATCH;
        mEventLogRepository.Save ();

        createdFlag = true;
      } else {
        createdFlag = false;
      }

      return category;
    }

    /// <summary>
    /// サムネイルを生成します。
    /// </summary>
    /// <param name="content"></param>
    /// <param name="workspace"></param>
    private void GenerateArtifact (IContent content, IWorkspace workspace) {
      var fullpath = System.IO.Path.Combine (workspace.PhysicalPath, content.GetFileMappingInfo ().MappingFilePath);
      if (string.IsNullOrEmpty (content.ThumbnailKey)) {
        // サムネイル新規作成
        var thumbnailKey = mTumbnailBuilder.BuildThumbnail (null, fullpath);
        content.ThumbnailKey = thumbnailKey;
        mContentRepository.Save ();
      } else {
        // サムネイルリビルド
        mTumbnailBuilder.BuildThumbnail (content.ThumbnailKey, fullpath);
      }
    }

    /// <summary>
    /// カテゴリ名をパースし結果を取得する。
    /// パースルールは、アプリケーション設定情報から取得する
    /// </summary>
    /// <param name="categoryName">パース前のカテゴリ名</param>
    /// <param name="parsedCategoryName"></param>
    /// <returns>カテゴリ名がパースルールに適応したかどうか</returns>
    private bool AttachParsedCategoryName (string categoryName, out string parsedCategoryName) {
      // 最大でMAX_CATEGORYPARSEREGE個数の正規表現からラベルのパースを行う。
      // パースに使用する正規表現を定義するキーは、「CategoryNameParserPropertyKey + i」(iは、0〜MAX_CATEGORYPARSEREGE)という名称です。
      // ・連番でなければなりません。
      // ・最初にマッチした正規表現でループは停止します。
      for (int i = 0; i < MAX_CATEGORYPARSEREGE; i++) {
        Regex parserRegex = null;
        var parserApp = mAppAppMetaInfoRepository.LoadByKey (CategoryNameParserPropertyKey + i);
        if (parserApp != null) {
          parserRegex = new Regex (parserApp.Value);

          if (parserRegex != null) {
            var match = parserRegex.Match (categoryName);
            if (!match.Success) continue;
            foreach (Group group in match.Groups) {
              if (group.Name == "CategoryName") {
                parsedCategoryName = group.Value;
                return true;
              }
            }
          }
        } else {
          parsedCategoryName = categoryName;
          return false;
        }
      }

      parsedCategoryName = categoryName;
      return false;
    }

    /// <summary>
    /// カテゴリ名をパースし、ラベル情報との関連付けを行う。必要があれば、ラベル情報を新規作成する。
    /// </summary>
    /// <param name="category"></param>
    /// <returns></returns>
    private bool AttachParsedLabel (string parseBase, ICategory category) {
      string categoryName = parseBase;

      // 最大でMAX_CATEGORYPARSEREGE個数の正規表現からラベルのパースを行う。
      // パースに使用する正規表現を定義するキーは、「CategoryLabelNameParserPropertyKey + i」(iは、0〜MAX_CATEGORYPARSEREGE)という名称です。
      // ・連番でなければなりません。
      // ・最初にマッチした正規表現でループは停止します。
      for (int i = 0; i < MAX_CATEGORYPARSEREGE; i++) {
        // アプリケーション設定情報から、ラベルパース用の正規表現を取得する
        // 0から順番に取得し、取得できなかった場合は処理失敗とする。
        Regex parserRegex = null;
        var parserApp = mAppAppMetaInfoRepository.LoadByKey (CategoryLabelNameParserPropertyKey + i);
        if (parserApp != null) {
          parserRegex = new Regex (parserApp.Value);
        } else {
          return false;
        }

        if (parserRegex != null) {
          var match = parserRegex.Match (categoryName);
          if (!match.Success) continue;

          int currentGroupNumber = 0;
          foreach (Group group in match.Groups) {
            if (currentGroupNumber == 0) {
              currentGroupNumber++;
              continue;
            }

            var label = this.mLabelRepository.LoadByName (group.ToString (), "Vfs");
            if (label == null) {
              label = this.mLabelRepository.New ();
              label.Name = group.ToString ();
              label.MetaType = group.Name == currentGroupNumber.ToString () ? "" : group.Name;
              mLabelRepository.UpdateNormalizeName(label);
              this.mLabelRepository.Save ();
            } else {
              label.MetaType = group.Name == currentGroupNumber.ToString () ? "" : group.Name;
              this.mLabelRepository.Save ();
            }

            category.AddLabelRelation (label, LabelCauseType.EXTENTION);
            currentGroupNumber++;
          }

          return true;
        }
      }
      return false;
    }

    private AclFileStructure ReadACLFile (FileInfo aclFillePath) {
      using (var file = File.OpenRead (aclFillePath.FullName)) {
        return Serializer.Deserialize<AclFileStructure> (file);
      }
    }
  }
}
