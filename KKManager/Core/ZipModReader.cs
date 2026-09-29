using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace KKManager.Core
{
    /// <summary>zipmod 解析结果分类——扫描侧据此区分「不是 mod 的文件」（静默跳过）与「坏 mod」（出声）。</summary>
    public enum ModErrorKind
    {
        /// <summary>解析成功。</summary>
        None = 0,

        /// <summary>打不开为 zip 容器——不是 mod 文件（扫描面按「非 mod」跳过）。</summary>
        NotContainer = 1,

        /// <summary>是 zip 容器但根部没有 manifest.xml——不是 zipmod（同上，跳过）。</summary>
        NoManifest = 2,

        /// <summary>有 manifest.xml 但 XML 解析失败——坏 mod（出声）。</summary>
        BadManifest = 3,

        /// <summary>manifest 缺 guid——坏 mod（出声）。</summary>
        NoGuid = 4,

        /// <summary>文件本身读不到（不存在 / 无权限 / IO 错误）——出声。</summary>
        Io = 5
    }

    /// <summary>zipmod 元数据——取自容器根部 manifest.xml。</summary>
    public class ModInfo
    {
        /// <summary>manifest guid——与文件名无关的稳定标识（关联主键）。</summary>
        public string Guid { get; set; }

        /// <summary>manifest name。</summary>
        public string Name { get; set; }

        /// <summary>manifest version。</summary>
        public string Version { get; set; }

        /// <summary>manifest author。</summary>
        public string Author { get; set; }

        /// <summary>manifest website。</summary>
        public string Website { get; set; }

        /// <summary>manifest description。</summary>
        public string Description { get; set; }

        /// <summary>manifest schema-ver 属性。</summary>
        public string SchemaVer { get; set; }

        /// <summary>zipmod 文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>zipmod 文件名（含扩展名）——仅供人定位，不作关联依据。</summary>
        public string FileName { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>容器内条目数。</summary>
        public int EntryCount { get; set; }

        /// <summary>解析诊断——成功为 null。</summary>
        public string Error { get; set; }
        /// <summary>解析结果的分类——None = 成功；其余见 ModErrorKind。</summary>
        public ModErrorKind ErrorKind { get; set; }

        /// <summary>文件名（去扩展名）与 guid 是否一致。</summary>
        public bool FileNameMatchesGuid
        {
            get
            {
                if (string.IsNullOrEmpty(Guid) || string.IsNullOrEmpty(FileName))
                {
                    return false;
                }
                string stem = Path.GetFileNameWithoutExtension(FileName);
                return string.Equals(stem, Guid, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>zipmod 容器内的一个条目——只读中央目录所得（路径 / 原始大小 / 压缩大小），不解包内容。</summary>
    public class ZipEntryInfo
    {
        /// <summary>条目在容器内的相对路径。</summary>
        public string Path { get; set; }

        /// <summary>原始（解压后）字节数。</summary>
        public long Size { get; set; }

        /// <summary>压缩后字节数。</summary>
        public long Compressed { get; set; }
    }

    /// <summary>zipmod 解析——只读容器中央目录中的 manifest.xml，不解包。</summary>
    public static class ZipModReader
    {
        /// <summary>解析一个 zipmod（失败时返回带 Error / ErrorKind 的 ModInfo，不抛异常）。</summary>
        public static ModInfo Parse(string path)
        {
            var fi = new FileInfo(path);
            var info = new ModInfo
            {
                FilePath = path,
                FileName = fi.Name,
                Size = fi.Exists ? fi.Length : 0
            };

            if (!fi.Exists)
            {
                info.ErrorKind = ModErrorKind.Io;
                info.Error = "文件不存在";
                return info;
            }

            // [段1] 打开容器——打不开 = 不是 zip 容器（扫描侧按「非 mod 文件」跳过，不算失败）
            ZipArchive zip = null;
            try
            {
                zip = ZipFile.OpenRead(path);
            }
            catch (InvalidDataException ex)
            {
                info.ErrorKind = ModErrorKind.NotContainer;
                info.Error = "不是 zip 容器：" + ex.Message;
                return info;
            }
            catch (Exception ex)
            {
                info.ErrorKind = ModErrorKind.Io;
                info.Error = ex.GetType().Name + ": " + ex.Message;
                return info;
            }

            // [段2] 读根部 manifest.xml——无此条目 = 不是 zipmod（跳过）；条目坏了 = 坏 mod（出声）
            try
            {
                using (zip)
                {
                    info.EntryCount = zip.Entries.Count;
                    ZipArchiveEntry entry = zip.GetEntry("manifest.xml");
                    if (entry == null)
                    {
                        info.ErrorKind = ModErrorKind.NoManifest;
                        info.Error = "容器内无 manifest.xml";
                        return info;
                    }
                    using (Stream stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        info.Error = ParseManifest(reader.ReadToEnd(), info);
                    }
                }
            }
            catch (Exception ex)
            {
                info.ErrorKind = ModErrorKind.Io;
                info.Error = ex.GetType().Name + ": " + ex.Message;
            }

            return info;
        }
        /// <summary>读容器内全部条目清单（只读中央目录，不解包）；失败返回 null 并在 error 给出诊断。</summary>
        public static List<ZipEntryInfo> ListEntries(string path, out string error)
        {
            error = null;
            List<ZipEntryInfo> list = new List<ZipEntryInfo>();
            if (!File.Exists(path))
            {
                error = "文件不存在";
                return null;
            }
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    // [段1] 逐条目取路径与大小——中央目录已含，无解压开销
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        list.Add(new ZipEntryInfo
                        {
                            Path = e.FullName,
                            Size = e.Length,
                            Compressed = e.CompressedLength
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            return list;
        }
        /// <summary>取容器内单个条目的字节（按路径精确匹配；超上限拒绝，失败返回 null 并给诊断）——白名单由调用方按档案条目清单把关。</summary>
        public static byte[] ReadEntryBytes(string path, string entryPath, long maxBytes, out string error)
        {
            error = null;
            if (!File.Exists(path))
            {
                error = "文件不存在";
                return null;
            }
            if (string.IsNullOrEmpty(entryPath))
            {
                error = "缺少条目路径";
                return null;
            }
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    // [段1] 定位条目 + 大小闸（超上限不出声地截断是不允许的，直接拒绝并说明）
                    ZipArchiveEntry entry = zip.GetEntry(entryPath);
                    if (entry == null)
                    {
                        error = "容器内没有该条目：" + entryPath;
                        return null;
                    }
                    if (maxBytes > 0 && entry.Length > maxBytes)
                    {
                        error = "条目过大（" + entry.Length.ToString() + " 字节，上限 " + maxBytes.ToString() + "）";
                        return null;
                    }
                    using (Stream s = entry.Open())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>解析 manifest 文本——容忍注释 / BOM / 字段缺失；成功返回 null，失败返回诊断并置 ErrorKind。</summary>
        private static string ParseManifest(string xml, ModInfo info)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                XmlElement root = doc.DocumentElement;
                if (root == null)
                {
                    info.ErrorKind = ModErrorKind.BadManifest;
                    return "manifest 无根节点";
                }
                info.SchemaVer = root.GetAttribute("schema-ver");
                info.Guid = Text(doc, "//guid");
                info.Name = Text(doc, "//name");
                info.Version = Text(doc, "//version");
                info.Author = Text(doc, "//author");
                info.Website = Text(doc, "//website");
                info.Description = Text(doc, "//description");
                if (string.IsNullOrEmpty(info.Guid))
                {
                    info.ErrorKind = ModErrorKind.NoGuid;
                    return "manifest 缺 guid";
                }
                return null;
            }
            catch (XmlException ex)
            {
                info.ErrorKind = ModErrorKind.BadManifest;
                return "manifest XML 解析失败: " + ex.Message;
            }
        }

        private static string Text(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return node == null ? null : node.InnerText;
        }
    }
}
