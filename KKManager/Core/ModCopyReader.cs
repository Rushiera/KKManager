using System;
using System.IO;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>一份 mod 副本的参考信息——manifest 实时读 + 创建时间实时读（「重复副本」窗口与「按作者整理」冲突小窗、CLI 共用同一装配）。</summary>
    public class ModCopyInfo
    {
        /// <summary>manifest 版本（实时读）。</summary>
        public string Version { get; set; }

        /// <summary>manifest 作者（实时读）。</summary>
        public string Author { get; set; }

        /// <summary>manifest 名称（实时读）。</summary>
        public string Name { get; set; }

        /// <summary>manifest 读不到时的原因（空 = 读到了）。</summary>
        public string Error { get; set; }

        /// <summary>创建时间（文件系统；读不到为空串）。</summary>
        public string Ctime { get; set; }
    }

    /// <summary>mod 副本参考信息的唯一装配处——副本窗口 / 冲突小窗 / CLI 都从这里取，字段加一处三处一起变。</summary>
    public static class ModCopyReader
    {
        /// <summary>读一份副本的 manifest（版本 / 作者 / 名称 / 读取失败原因）与创建时间；读不到出声，不静默。</summary>
        public static ModCopyInfo Read(string filePath)
        {
            ModCopyInfo info = new ModCopyInfo();
            info.Ctime = "";
            ModInfo manifest = ZipModReader.Parse(filePath);
            info.Version = manifest.Version;
            info.Author = manifest.Author;
            info.Name = manifest.Name;
            info.Error = manifest.Error;
            try
            {
                FileInfo fi = new FileInfo(filePath);
                if (fi.Exists)
                {
                    info.Ctime = Store.CreatedStampOf(fi);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[副本] 创建时间读不到：" + filePath + " → " + ex.Message);
            }
            return info;
        }
    }
}
