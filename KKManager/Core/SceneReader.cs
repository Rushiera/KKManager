using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>场景卡数据区里的一条插件数据条目（键名 + 值形态 + 字节数）。</summary>
    public class ScenePluginItem
    {
        /// <summary>插件键名（MessagePack map 键，如 timeline / kkpe / vnge_actor）。</summary>
        public string Key { get; set; }

        /// <summary>值形态（sceneInfo(XML) / main(JSON) / main(短串) / main(map N) 等）。</summary>
        public string Shape { get; set; }

        /// <summary>载荷字节数（0 = 未解析出长度）。</summary>
        public long Bytes { get; set; }

        /// <summary>条目在文件内的偏移。</summary>
        public long Offset { get; set; }
    }

    /// <summary>场景卡（sd）数据区深度分析结果——只读快照。</summary>
    public class SceneInfoResult
    {
        /// <summary>是否完成扫描。</summary>
        public bool Scanned { get; set; }

        /// <summary>失败原因（失败必须可见）。</summary>
        public string Error { get; set; }

        /// <summary>插件数据条目（按文件内出现顺序）。</summary>
        public List<ScenePluginItem> Plugins { get; } = new List<ScenePluginItem>();

        /// <summary>场景内嵌角色卡数据份数（数据块目录 lstInfo 命中数）。</summary>
        public int CharaDataCount { get; set; }

        /// <summary>数据区已扫描字节数。</summary>
        public long ScannedBytes { get; set; }

        /// <summary>解析告警（失败必须可见）。</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 场景卡（sd）数据区深度分析——列插件数据条目、数内嵌角色卡数据份数。
    /// 条目形态（实测）：`fixstr 键名 92 00 81 A9 "sceneInfo" &lt;str&gt;`（XML 型）或
    /// `fixstr 键名 92 00 82 A4 "main" &lt;值&gt;`（VNGE 型——值多为 JSON 串），值 = [版本, {字段…}]。
    /// 只读：流式扫描，不整文件读入；解析失败只记告警，不抛异常。
    /// </summary>
    public static class SceneReader
    {
        /// <summary>数据块目录锚点——fixstr(7) "lstInfo"。</summary>
        private static readonly byte[] LstInfoMark = { 0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F };

        /// <summary>sceneInfo 字段锚点——fixstr(9) "sceneInfo"。</summary>
        private static readonly byte[] SceneInfoMark = { 0xA9, 0x73, 0x63, 0x65, 0x6E, 0x65, 0x49, 0x6E, 0x66, 0x6F };

        /// <summary>main 字段锚点——fixstr(4) "main"。</summary>
        private static readonly byte[] MainMark = { 0xA4, 0x6D, 0x61, 0x69, 0x6E };

        /// <summary>扫描缓冲区（1 MB）。</summary>
        private const int BufSize = 1 << 20;

        /// <summary>跨缓冲保留字节（覆盖键名回溯 31 字节 + 前瞻余量）。</summary>
        private const int CarrySize = 64;

        /// <summary>读一张场景卡的插件条目与内嵌角色数据份数（path = 卡片文件；imageEnd = 图片区结束偏移）。</summary>
        public static SceneInfoResult Read(string path, long imageEnd)
        {
            SceneInfoResult r = new SceneInfoResult { Scanned = true };
            if (!File.Exists(path))
            {
                r.Error = "卡片文件不存在：" + path;
                return r;
            }
            var fi = new FileInfo(path);
            long size = fi.Length;
            long from = imageEnd > 0 ? imageEnd : 0;
            if (from >= size)
            {
                r.Error = "数据区为空（图片区到文件尾没有字节）";
                return r;
            }

            var seen = new HashSet<long>();
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] buf = new byte[BufSize + 256];
                    long pos = from;
                    long consumed = from;
                    int carry = 0;
                    while (pos < size)
                    {
                        long want = size - pos;
                        if (want > BufSize)
                        {
                            want = BufSize;
                        }
                        fs.Position = pos;
                        int read = fs.Read(buf, carry, (int)want);
                        if (read <= 0)
                        {
                            break;
                        }
                        pos = pos + read;
                        long baseOff = consumed - carry;
                        consumed = consumed + read;
                        int total = carry + read;
                        r.ScannedBytes = r.ScannedBytes + read;

                        for (int i = 0; i <= total - LstInfoMark.Length; i = i + 1)
                        {
                            if (buf[i] == LstInfoMark[0] && MatchAt(buf, i, LstInfoMark))
                            {
                                r.CharaDataCount = r.CharaDataCount + 1;
                            }
                        }

                        for (int i = CarrySize; i <= total - 96; i = i + 1)
                        {
                            if (buf[i] != 0x92 || buf[i + 1] != 0x00)
                            {
                                continue;
                            }
                            long at = baseOff + i;
                            if (!seen.Add(at))
                            {
                                continue;
                            }
                            string key = ReadKeyAt(buf, i);
                            if (key == null)
                            {
                                continue;
                            }
                            ScenePluginItem item = ParseItem(buf, i, at, key, r);
                            if (item != null)
                            {
                                r.Plugins.Add(item);
                            }
                        }

                        int newCarry = total < CarrySize ? total : CarrySize;
                        Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                        carry = newCarry;
                    }
                }
            }
            catch (IOException ex)
            {
                r.Error = "读取失败：" + ex.Message;
                return r;
            }
            if (r.Plugins.Count == 0)
            {
                r.Warnings.Add("未识别到插件数据条目（数据区里没有 timeline / kkpe / vnge_* 形态的条目）");
            }
            return r;
        }

        /// <summary>解析 0x92 00 之后的条目值形态；不是插件条目形态返回 null。</summary>
        private static ScenePluginItem ParseItem(byte[] buf, int at92, long at, string key, SceneInfoResult r)
        {
            int shapeAt = at92 + 2;
            if (buf[shapeAt] == 0x81 && MatchAt(buf, shapeAt + 1, SceneInfoMark))
            {
                long len;
                if (ReadStrHeader(buf, shapeAt + 1 + SceneInfoMark.Length, out len) < 0)
                {
                    r.Warnings.Add(key + " 的 sceneInfo 字符串头未识别（偏移 " + at + "）");
                    return new ScenePluginItem { Key = key, Shape = "sceneInfo(XML) · 头未识别", Bytes = 0, Offset = at };
                }
                return new ScenePluginItem { Key = key, Shape = "sceneInfo(XML)", Bytes = len, Offset = at };
            }
            if (buf[shapeAt] == 0x82 && MatchAt(buf, shapeAt + 1, MainMark))
            {
                int p = shapeAt + 1 + MainMark.Length;
                byte b = buf[p];
                if (b >= 0xA0 && b <= 0xBF)
                {
                    return new ScenePluginItem { Key = key, Shape = "main(短串)", Bytes = b - 0xA0, Offset = at };
                }
                if (b >= 0x80 && b <= 0x8F)
                {
                    return new ScenePluginItem { Key = key, Shape = "main(map " + (b - 0x80) + " 键)", Bytes = 0, Offset = at };
                }
                if (b >= 0x90 && b <= 0x9F)
                {
                    return new ScenePluginItem { Key = key, Shape = "main(数组 " + (b - 0x90) + " 项)", Bytes = 0, Offset = at };
                }
                long len;
                if (ReadStrHeader(buf, p, out len) >= 0)
                {
                    return new ScenePluginItem { Key = key, Shape = "main(JSON)", Bytes = len, Offset = at };
                }
                return new ScenePluginItem { Key = key, Shape = "main(0x" + b.ToString("X2") + ")", Bytes = 0, Offset = at };
            }
            return null;
        }

        /// <summary>读 MessagePack 字符串头（fixstr / str8 / str16 / str32），返回头字节数；未识别返回 -1。</summary>
        private static int ReadStrHeader(byte[] buf, int p, out long len)
        {
            len = 0;
            if (p >= buf.Length)
            {
                return -1;
            }
            byte b = buf[p];
            if (b >= 0xA0 && b <= 0xBF)
            {
                len = b - 0xA0;
                return 1;
            }
            if (b == 0xD9)
            {
                len = buf[p + 1];
                return 2;
            }
            if (b == 0xDA)
            {
                len = (buf[p + 1] << 8) | buf[p + 2];
                return 3;
            }
            if (b == 0xDB)
            {
                len = ((long)buf[p + 1] << 24) | ((long)buf[p + 2] << 16) | ((long)buf[p + 3] << 8) | buf[p + 4];
                return 5;
            }
            return -1;
        }

        /// <summary>从 0x92 位置往前回溯相邻的 fixstr 键名；不是 ASCII 名字返回 null。</summary>
        private static string ReadKeyAt(byte[] buf, int at92)
        {
            for (int n = 1; n <= 31; n = n + 1)
            {
                int hp = at92 - 1 - n;
                if (hp < 0)
                {
                    return null;
                }
                if (buf[hp] != (byte)(0xA0 | n))
                {
                    continue;
                }
                var sb = new StringBuilder();
                for (int k = 0; k < n; k = k + 1)
                {
                    byte c = buf[hp + 1 + k];
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                        || c == '_' || c == '.' || c == '-';
                    if (!ok)
                    {
                        return null;
                    }
                    sb.Append((char)c);
                }
                return sb.ToString();
            }
            return null;
        }

        /// <summary>在缓冲区指定位置匹配字节序列。</summary>
        private static bool MatchAt(byte[] buf, int at, byte[] pattern)
        {
            for (int k = 0; k < pattern.Length; k = k + 1)
            {
                if (buf[at + k] != pattern[k])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
