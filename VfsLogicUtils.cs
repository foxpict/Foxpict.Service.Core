using System;
using System.IO;
using ProtoBuf;
using Foxpict.Service.Core.Structure;

namespace Foxpict.Service.Core
{
    public static class VfsLogicUtils
    {
        /// <summary>
        /// 生成したACLハッシュを取得する
        /// </summary>
        /// <returns></returns>
        public static string GenerateACLHash()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="aclFillePath"></param>
        /// <returns></returns>
        public static AclFileStructure ReadACLFile(FileInfo aclFillePath)
        {
            using (var file = File.OpenRead(aclFillePath.FullName))
            {
                return Serializer.Deserialize<AclFileStructure>(file);
            }
        }
    }
}