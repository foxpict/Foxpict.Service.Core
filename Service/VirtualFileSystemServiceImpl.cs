using System;
using System.Collections.Generic;
using System.IO;
using Foxpict.Service.Core.Structure;
using Foxpict.Service.Infra;
using Foxpict.Service.Infra.Model;
using Foxpict.Service.Infra.Repository;
using Foxpict.Service.Infra.Utils;
using NLog;
using ProtoBuf;

namespace Foxpict.Service.Core.Service {
  public class VirtualFileSystemServiceImpl : IVirtualFileSystemService {
    private static Logger mLogger = LogManager.GetCurrentClassLogger ();

    readonly IFileMappingInfoRepository mFileMappingInfoRepository;

    public VirtualFileSystemServiceImpl (IFileMappingInfoRepository fileMappingInfoRepository) {
      this.mFileMappingInfoRepository = fileMappingInfoRepository;
    }

    public IFileMappingInfo PersistentFileMapping (IWorkspace workspace, FileInfo file) {
      string aclhash = VfsLogicUtils.GenerateACLHash ();
      var fileMappingInfo = CreateFileMappingInfo (aclhash, workspace, file);
      return fileMappingInfo;
    }

    public IFileMappingInfo RegisterFile (FileInfo file, IFileMappingInfo fileMappingInfo) {
      var workspace = fileMappingInfo.GetWorkspace ();
      var aclhash = fileMappingInfo.AclHash;

      var aclfileLocalPath_Update = workspace.TrimWorekspacePath (file.FullName);
      mLogger.Info ($"aclfileLocalPath_Update={aclfileLocalPath_Update}");

      // 移動先のディレクトリがサブディレクトリを含む場合、存在しないサブディレクトリを作成します。
      var newFileInfo = new FileInfo (Path.Combine (workspace.PhysicalPath, aclfileLocalPath_Update));
      Directory.CreateDirectory (newFileInfo.Directory.FullName);

      // ファイル移動
      var fromFile = file;
      var toFile = new FileInfo (Path.Combine (workspace.PhysicalPath, aclfileLocalPath_Update + ".tmp"));
      mLogger.Info ($"[ファイル移動] {file.FullName}を、{toFile.FullName}へ移動します。");
      File.Move (fromFile.FullName, toFile.FullName);

      // 仮想領域にACLファイルの作成
      var aclFileInfo = new FileInfo (Path.Combine (workspace.VirtualPath, aclfileLocalPath_Update) + ".aclgene");
      // インポート処理の場合は、仮想領域にフォルダがないためディレクトリを作成する。
      // 仮想領域での処理の場合は、フォルダが既に存在するはずなので、なにもしない。
      var dir = aclFileInfo.Directory.FullName;
      Directory.CreateDirectory (aclFileInfo.Directory.FullName);

      var data = new AclFileStructure () {
        Version = AclFileStructure.CURRENT_VERSION,
        LastUpdate = DateTime.Now,
        Data = new KeyValuePair<string, string>[] {
        new KeyValuePair<string, string> ("ACLHASH", aclhash)
        }
      };

      using (var aclFile = File.Create (aclFileInfo.FullName)) {
        Serializer.Serialize (aclFile, data);
      }

      CleanAclFile (toFile);

      return fileMappingInfo;
    }

    private void CleanAclFile (FileInfo file) {
      if (!File.Exists (file.FullName)) throw new ApplicationException (file.FullName + "が見つかりません");
      var extFileName = Path.GetFileNameWithoutExtension (file.Name);
      file.MoveTo (Path.Combine (file.DirectoryName, extFileName));
    }

    private IFileMappingInfo CreateFileMappingInfo (string aclHash, IWorkspace workspace, FileInfo file) {
      var aclfileLocalPath_Update = workspace.TrimWorekspacePath (file.FullName);

      string mimetype = "";
      switch (file.Extension) {
        case ".png":
          mimetype = "image/png";
          break;
        case ".jpg":
        case ".jpeg":
          mimetype = "image/jpg";
          break;
        case ".gif":
          mimetype = "image/gif";
          break;
      }

      var entity = mFileMappingInfoRepository.New ();
      entity.AclHash = aclHash;
      entity.SetWorkspace (workspace);
      entity.Mimetype = mimetype;
      entity.MappingFilePath = aclfileLocalPath_Update;
      entity.LostFileFlag = false;
      mFileMappingInfoRepository.Save ();
      return entity;
    }
  }
}
