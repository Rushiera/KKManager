using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>卡片的一条 mod 引用——部件槽粒度。</summary>
    public class ModRef
    {
        /// <summary>mod 标识——即该 zipmod 的 manifest guid（不是文件名）。</summary>
        public string ModId { get; set; }

        /// <summary>部件属性名（如 ChaFileClothes.ClothesTop）。</summary>
        public string Property { get; set; }

        /// <summary>槽位号。</summary>
        public int Slot { get; set; }

        /// <summary>本地槽位号。</summary>
        public int LocalSlot { get; set; }

        /// <summary>部件分类号。</summary>
        public int CategoryNo { get; set; }

        /// <summary>卡内记录的作者。</summary>
        public string Author { get; set; }

        /// <summary>卡内记录的来源网址。</summary>
        public string Website { get; set; }

        /// <summary>卡内记录的名称。</summary>
        public string Name { get; set; }
    }

    /// <summary>卡片解析结果。</summary>
    public class CardInfo
    {
        /// <summary>卡片绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>卡片文件名（含扩展名）。</summary>
        public string FileName { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>PNG 图片区结束偏移（IEND chunk 之后）；-1 表示未找到。</summary>
        public long ImageEnd { get; set; }

        /// <summary>卡类型标记（如 【KoiKatuClothes】/【KoiKatuChara】）。</summary>
        public string CardType { get; set; }

        /// <summary>数据区标记（如 0.0.0）。</summary>
        public string DataVersion { get; set; }

        /// <summary>Sideloader UAR 声明区出现次数。</summary>
        public int UarBlocks { get; set; }

        /// <summary>游戏内角色名（服装卡读数据区头段的角色名段；人物卡为 null——名字在 Parameter 块，由 CardName 补读；null = 未读到）。</summary>
        public string CharaName { get; set; }

        /// <summary>卡片声明的 mod 引用（部件粒度，未去重）。</summary>
        public List<ModRef> ModRefs { get; } = new List<ModRef>();

        /// <summary>去重后的 mod 标识集合（保持首次出现顺序）。</summary>
        public IReadOnlyList<string> DistinctModIds()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (ModRef r in ModRefs)
            {
                if (r.ModId != null && seen.Add(r.ModId))
                {
                    list.Add(r.ModId);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 卡片文件解析——图片区 / 数据区分界、卡类型、mod 声明区。
    /// 声明区提取走 MessagePack 锚点 fixstr(5)"ModID"（实测稳定，不依赖 ChaFile 结构全解）。
    /// </summary>
    public static class CardReader
    {
        private static readonly byte[] ModIdAnchor = { 0xA5, 0x4D, 0x6F, 0x64, 0x49, 0x44 };
        private static readonly byte[] UarMark = Encoding.ASCII.GetBytes("com.bepis.sideloader.universalautoresolver");

        /// <summary>解析一张卡片（流式，不整文件读入）。</summary>
        public static CardInfo Parse(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("卡片不存在: " + path, path);
            }

            var fi = new FileInfo(path);
            var info = new CardInfo { FilePath = path, FileName = fi.Name, Size = fi.Length };

            using (var fs = File.OpenRead(path))
            {
                info.ImageEnd = FindPngEnd(fs, fi.Length);
                if (info.ImageEnd > 0 && fs.Length - info.ImageEnd >= 5)
                {
                    fs.Position = info.ImageEnd;
                    var br = new BinaryReader(fs);
                    br.ReadInt32();
                    info.CardType = Read7BitString(br);
                    if (fs.Length - fs.Position >= 1)
                    {
                        info.DataVersion = Read7BitString(br);
                    }
                    // 服装卡：数据区头段第三段是角色名（人物卡此处是脸图长度，不能按字符串读）
                    if (info.CardType != null && info.CardType.Contains("Clothes") && fs.Length - fs.Position >= 1)
                    {
                        info.CharaName = Read7BitString(br);
                    }
                }
                CollectDeclaration(fs, info);
            }

            return info;
        }

        /// <summary>定位 PNG 的 IEND 结束偏移（图片区终点）；未找到返回 -1。</summary>
        public static long FindPngEnd(FileStream fs, long len)
        {
            byte[] hdr = new byte[8];
            long pos = 8;
            while (pos + 8 <= len)
            {
                fs.Position = pos;
                if (ReadFull(fs, hdr, 8) != 8)
                {
                    return -1;
                }
                long clen = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                string type = Encoding.ASCII.GetString(hdr, 4, 4);
                if (type == "IEND")
                {
                    return pos + 12;
                }
                if (clen < 0 || pos + 12 + clen > len)
                {
                    return -1;
                }
                pos += 12 + clen;
            }
            return -1;
        }

        /// <summary>扫描数据区，收集 ModID 引用与 UAR 标记计数。</summary>
        private static void CollectDeclaration(FileStream fs, CardInfo info)
        {
            long start = info.ImageEnd > 0 ? info.ImageEnd : 0;
            const long HeadLimit = 2L * 1024 * 1024;
            long head = Math.Min(fs.Length, start + HeadLimit);

            ScanRegion(start, head);

            if (info.ModRefs.Count == 0 && head < fs.Length)
            {
                // 前段未命中——退化为全扫（防漏检；每卡声明区实测只有一处）
                ScanRegion(head, fs.Length);
            }

            void ScanRegion(long from, long to)
            {
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 64];
                fs.Position = from;
                long remaining = to - from;
                int carry = 0;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(BufSize, remaining);
                    int read = fs.Read(buf, carry, want);
                    if (read <= 0)
                    {
                        break;
                    }
                    remaining -= read;
                    int total = carry + read;

                    for (int i = 0; i <= total - ModIdAnchor.Length; i++)
                    {
                        if (buf[i] != ModIdAnchor[0] || !MatchAt(buf, i, ModIdAnchor))
                        {
                            continue;
                        }
                        int p = i + ModIdAnchor.Length;
                        string modId = ReadMsgPackString(buf, ref p, total);
                        if (modId != null && modId.Length > 0)
                        {
                            info.ModRefs.Add(new ModRef { ModId = modId });
                        }
                    }

                    for (int i = 0; i <= total - UarMark.Length; i++)
                    {
                        if (buf[i] == UarMark[0] && MatchAt(buf, i, UarMark))
                        {
                            info.UarBlocks++;
                        }
                    }

                    carry = Math.Min(63, total);
                    Array.Copy(buf, total - carry, buf, 0, carry);
                }
            }
        }

        /// <summary>读 7-bit 长度前缀的 UTF-8 字符串（BinaryWriter.Write(string) 形态）。</summary>
        public static string Read7BitString(BinaryReader br)
        {
            int len = 0;
            int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                len |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
                if (shift > 28)
                {
                    throw new InvalidDataException("7-bit 长度前缀异常");
                }
            }
            return Encoding.UTF8.GetString(br.ReadBytes(len));
        }

        /// <summary>按 MessagePack 字符串规则读一个字符串；无法解析返回 null。</summary>
        public static string ReadMsgPackString(byte[] buf, ref int p, int total)
        {
            if (p >= total)
            {
                return null;
            }
            byte b = buf[p];
            int strLen;
            if (b >= 0xA0 && b <= 0xBF)
            {
                strLen = b & 0x1F;
                p += 1;
            }
            else if (b == 0xD9)
            {
                if (p + 2 > total)
                {
                    return null;
                }
                strLen = buf[p + 1];
                p += 2;
            }
            else if (b == 0xDA)
            {
                if (p + 3 > total)
                {
                    return null;
                }
                strLen = (buf[p + 1] << 8) | buf[p + 2];
                p += 3;
            }
            else if (b == 0xDB)
            {
                if (p + 5 > total)
                {
                    return null;
                }
                strLen = (buf[p + 1] << 24) | (buf[p + 2] << 16) | (buf[p + 3] << 8) | buf[p + 4];
                p += 5;
            }
            else
            {
                return null;
            }
            if (strLen < 0 || p + strLen > total)
            {
                return null;
            }
            string s = Encoding.UTF8.GetString(buf, p, strLen);
            p += strLen;
            return s;
        }

        /// <summary>在缓冲区指定位置匹配字节序列。</summary>
        public static bool MatchAt(byte[] buf, int at, byte[] pattern)
        {
            for (int k = 0; k < pattern.Length; k++)
            {
                if (buf[at + k] != pattern[k])
                {
                    return false;
                }
            }
            return true;
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }
            return total;
        }
    }
}
