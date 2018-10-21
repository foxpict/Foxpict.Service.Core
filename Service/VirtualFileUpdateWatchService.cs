using Foxpict.Service.Infra.Model;
using NLog;
using SimpleInjector;

namespace Foxpict.Service.Core.Service {
  public class VirtualFileUpdateWatchService {
    private readonly Logger mLogger;

    FileUpdateWatchServiceBase mFileUpdateWatchImpl;

    public VirtualFileUpdateWatchService (Container container) {
      mFileUpdateWatchImpl = new FileUpdateWatchServiceBase (container);
    }

    public void StartWatch (IWorkspace workspace) {
      mFileUpdateWatchImpl.StartWatchByVirtualPath (workspace);
    }
  }
}
