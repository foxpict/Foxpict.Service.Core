using Foxpict.Service.Infra.Model;

namespace Foxpict.Service.Core.Vfs {
    public interface IFileUpdateRunner
    {
        void file_create_acl(FileUpdateQueueItem item, IWorkspace workspace);
        void file_create_normal(FileUpdateQueueItem item, IWorkspace workspace);
        void file_remove_acl(FileUpdateQueueItem item, IWorkspace workspace);
        void file_rename_acl(FileUpdateQueueItem item, IWorkspace workspace);
    }
}