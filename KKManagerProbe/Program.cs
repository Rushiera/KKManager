using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KKManager.Core;
using KKManager.Data;

namespace KKManager.Probe
{
    /// <summary>
    /// 卡片文件结构侦察。命令：
    ///   scan &lt;card.png&gt; &lt;outDir&gt;                —— PNG chunk 表 + 尾部/末尾 hex + 全文件字符串表 + 关键词命中
    ///   hex  &lt;any file&gt; &lt;out.txt&gt; &lt;start&gt; &lt;len&gt;  —— 指定偏移区间 hex+ascii dump
    /// 只读，不改源文件。
    /// </summary>
    /// <summary>SerializedFile 的 TypeTree 节点（v17 布局：24 字节）。</summary>
    internal class SfNode
    {
        /// <summary>节点版本（1 / 2）。</summary>
        public int Version;

        /// <summary>层级深度（0 = 根节点）。</summary>
        public int Level;

        /// <summary>类型标志位。</summary>
        public int TypeFlags;

        /// <summary>类型名偏移——高位 0x80000000 表示 CommonString 索引，否则指向本类型字符串表。</summary>
        public long TypeStrOffset;

        /// <summary>字段名偏移——编码同 TypeStrOffset。</summary>
        public long NameStrOffset;

        /// <summary>字段字节数（-1 = 变长）。</summary>
        public int ByteSize;

        /// <summary>节点序号。</summary>
        public int Index;

        /// <summary>元标志（0x4000 = 其后按 4 字节对齐）。</summary>
        public int MetaFlag;
    }

    /// <summary>SerializedFile 的类型条目（classID + TypeTree + 内嵌字符串表）。</summary>
    internal class SfType
    {
        /// <summary>Unity classID（28 = Texture2D · 1 = GameObject · 43 = Mesh · 21 = Material）。</summary>
        public int ClassID;

        /// <summary>TypeTree 节点数。</summary>
        public int NodeCount;

        /// <summary>TypeTree 节点。</summary>
        public List<SfNode> Nodes = new List<SfNode>();

        /// <summary>本类型内嵌字符串表（字段名池）。</summary>
        public byte[] StringBuffer;

        /// <summary>字符串表字节数。</summary>
        public int StringBufferSize;
    }

    /// <summary>SerializedFile 的对象条目。</summary>
    internal class SfObject
    {
        /// <summary>对象路径 ID。</summary>
        public long PathID;

        /// <summary>对象数据相对数据区的起点。</summary>
        public int ByteStart;

        /// <summary>对象数据字节数。</summary>
        public int ByteSize;

        /// <summary>类型下标（指向类型表）。</summary>
        public int TypeID;
    }

    internal static partial class Program
    {
        private static readonly string[] ScanKeywords =
        {
            "zipmod", ".zipmod", "KKEx", "VioletNocturne", "KoiKatu", "Modding", "manifest", "GeBo"
        };

        private static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: scan <card.png> <outDir> | hex <file> <out.txt> <start> <len> | find <file> <out.txt> <text> <before> <after> | copy <src> <dest> | cmp <a> <b> <offA> <offB> <len> | u3ddump <zipmod> <entry> <outFile> | htmlcheck <html> <out.txt> | extractjs <html> <out.js> | tlinfo <file|目录> <out.txt> [最大MB] | tlscan <card.png> <out.txt>");
                return 2;
            }

            switch (args[0])
            {
                case "scan":
                    return Scan(args[1], args[2]);
                case "hex":
                    return HexRange(args[1], args[2], long.Parse(args[3]), int.Parse(args[4]));
                case "mods":
                    return ExtractModIds(args[1], args[2]);
                case "anchors":
                    return Anchors(args[1], args[2]);
                case "images":
                    return Images(args[1], args[2]);
                case "link":
                    return Link(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 0);
                case "stat":
                    return Stat(args[1], args[2]);
                case "mkcard":
                    return MakeCard(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
                case "unityfs":
                    return UnityFs(args[1], args[2]);
                case "unitysf":
                    return UnitySf(args[1], args[2]);
                case "sfscan":
                    return SfScan(args[1], args[2]);
                case "sfobj":
                    return SfObj(args[1], args[2]);
                case "sftex":
                    return SfTex(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 0, args.Length > 4 ? int.Parse(args[4]) : 0);
                case "find":
                    if (args.Length < 6)
                    {
                        Console.Error.WriteLine("用法: find <file> <out.txt> <text> <before> <after>");
                        return 2;
                    }
                    return FindText(args[1], args[2], args[3], int.Parse(args[4]), int.Parse(args[5]));
                case "u3ddump":
                    return U3dDump(args[1], args[2], args[3]);
                case "copy":
                    return CopyFile(args[1], args[2]);
                case "cmp":
                    if (args.Length < 6)
                    {
                        Console.Error.WriteLine("用法: cmp <a> <b> <offA> <offB> <len>");
                        return 2;
                    }
                    return CompareRange(args[1], args[2], long.Parse(args[3]), long.Parse(args[4]), long.Parse(args[5]));
                case "tlinfo":
                    return TlInfo(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 0);
                case "mp":
                    if (args.Length < 6)
                    {
                        Console.Error.WriteLine("用法: mp <file> <out.txt> <start> <len> [最大深度]");
                        return 2;
                    }
                    return MpDump(args[1], args[2], long.Parse(args[3]), long.Parse(args[4]), args.Length > 5 ? int.Parse(args[5]) : 6);
                case "htmlcheck":
                    return ProbeHtml(args[1], args[2]);
                case "extractjs":
                    return ExtractJs(args[1], args[2]);
                case "sditems":
                    return SdItems(args[1], args[2]);
                case "sdscan":
                    return SdScanCommand(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 16);
                case "moduse":
                    return ModUseCommand(args[1], int.Parse(args[2]));
                case "tlscan":
                    return TlScanCommand(args[1], args[2]);
                default:
                    Console.Error.WriteLine("未知命令: " + args[0]);
                    return 2;
            }
        }

        /// <summary>合成一张最小卡片——PNG（IHDR + IDAT + IEND）+ 数据区（标记 + 卡类型 + 版本 + 填充）。仅作结构测试夹具。</summary>
        private static int MakeCard(string path, int dataBytes)
        {
            if (dataBytes < 0)
            {
                dataBytes = 0;
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // [段1] 签名 + IHDR
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                byte[] ihdr = new byte[13];
                WriteInt32Big(ihdr, 0, 16);
                WriteInt32Big(ihdr, 4, 16);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(fs, "IHDR", ihdr);

                // [段2] 一个 IDAT（内容随意——本夹具不追求可解码像素）
                byte[] idat = new byte[64];
                for (int i = 0; i < idat.Length; i = i + 1)
                {
                    idat[i] = (byte)(i * 7);
                }
                WriteChunk(fs, "IDAT", idat);

                // [段3] IEND
                WriteChunk(fs, "IEND", new byte[0]);

                // [段4] 数据区——int32 标记 + 7bit 串（卡类型 / 版本）+ 分块填充
                byte[] marker = new byte[4];
                WriteInt32Big(marker, 0, 100);
                fs.Write(marker, 0, 4);
                Write7BitString(fs, "【KoiKatuClothes】");
                Write7BitString(fs, "0.0.0");
                const int BlockSize = 1 << 20;
                byte[] block = new byte[BlockSize];
                for (int i = 0; i < BlockSize; i = i + 1)
                {
                    block[i] = (byte)(i & 0xFF);
                }
                int remaining = dataBytes;
                while (remaining > 0)
                {
                    int take = remaining < BlockSize ? remaining : BlockSize;
                    fs.Write(block, 0, take);
                    remaining = remaining - take;
                }
            }
            Console.WriteLine("已合成: " + path + "（数据区填充 " + dataBytes + " 字节）");
            return 0;
        }

        /// <summary>写一个 PNG 块（长度 + 类型 + 数据 + CRC）。</summary>
        private static void WriteChunk(FileStream fs, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] hdr = new byte[8];
            WriteInt32Big(hdr, 0, data.Length);
            Array.Copy(typeBytes, 0, hdr, 4, 4);
            fs.Write(hdr, 0, 8);
            fs.Write(data, 0, data.Length);
            uint crc = Crc32Update(0xFFFFFFFFu, typeBytes, 0, typeBytes.Length);
            crc = Crc32Update(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;
            byte[] crcBytes = new byte[4];
            WriteInt32Big(crcBytes, 0, unchecked((int)crc));
            fs.Write(crcBytes, 0, 4);
        }

        /// <summary>CRC32 累计（PNG 用的反射多项式）。</summary>
        private static uint Crc32Update(uint crc, byte[] buf, int offset, int count)
        {
            uint c = crc;
            for (int i = 0; i < count; i = i + 1)
            {
                c = c ^ buf[offset + i];
                for (int k = 0; k < 8; k = k + 1)
                {
                    if ((c & 1u) != 0)
                    {
                        c = 0xEDB88320u ^ (c >> 1);
                    }
                    else
                    {
                        c = c >> 1;
                    }
                }
            }
            return c;
        }

        /// <summary>写大端 32 位整数。</summary>
        private static void WriteInt32Big(byte[] buf, int at, int value)
        {
            buf[at] = (byte)((value >> 24) & 0xFF);
            buf[at + 1] = (byte)((value >> 16) & 0xFF);
            buf[at + 2] = (byte)((value >> 8) & 0xFF);
            buf[at + 3] = (byte)(value & 0xFF);
        }

        /// <summary>写 7bit 长度前缀的 UTF-8 字符串（BinaryWriter.Write(string) 形态）。</summary>
        private static void Write7BitString(FileStream fs, string s)
        {
            byte[] body = Encoding.UTF8.GetBytes(s);
            int len = body.Length;
            while (len >= 0x80)
            {
                fs.WriteByte((byte)((len & 0x7F) | 0x80));
                len = len >> 7;
            }
            fs.WriteByte((byte)len);
            fs.Write(body, 0, body.Length);
        }

        /// <summary>结构扫描——chunk 表 / 尾部 hex / 末尾 hex / 字符串表 / 关键词命中。</summary>
        private static int Scan(string src, string outDir)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            Directory.CreateDirectory(outDir);
            string reportPath = Path.Combine(outDir, "card-scan.txt");
            string stringsPath = Path.Combine(outDir, "card-strings.txt");

            using (var fs = File.OpenRead(src))
            using (var sw = new StreamWriter(reportPath, false, new UTF8Encoding(true)))
            {
                long len = fs.Length;
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + len.ToString("N0") + " 字节");
                sw.WriteLine();

                long iendEnd = DumpPngChunks(fs, len, sw);
                sw.WriteLine();

                if (iendEnd > 0)
                {
                    sw.WriteLine("## 二、IEND 之后前 256 字节");
                    HexInto(fs, sw, iendEnd, 256);
                    sw.WriteLine();
                }

                sw.WriteLine("## 三、文件末尾 1024 字节");
                HexInto(fs, sw, Math.Max(0, len - 1024), 1024);
                sw.WriteLine();

                sw.WriteLine("## 四、字符串表（长度 ≥8 且含字母或方括号）");
                sw.WriteLine("- 明细落: " + stringsPath);
                var hits = new List<string>();
                long strings = WriteStrings(fs, stringsPath, sw, hits);
                sw.WriteLine("- 串条目数: " + strings.ToString("N0"));
                sw.WriteLine();

                sw.WriteLine("## 五、关键词命中");
                foreach (string k in ScanKeywords)
                {
                    sw.WriteLine("- 关键词 " + k + ":");
                    bool any = false;
                    foreach (string h in hits)
                    {
                        if (h.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sw.WriteLine("    " + h);
                            any = true;
                        }
                    }
                    if (!any)
                    {
                        sw.WriteLine("    (无命中)");
                    }
                }
            }

            Console.WriteLine("报告: " + reportPath);
            return 0;
        }

        /// <summary>遍历 PNG chunk 表，返回 IEND 结束偏移（无则 -1）。</summary>
        private static long DumpPngChunks(FileStream fs, long len, StreamWriter sw)
        {
            sw.WriteLine("## 一、PNG chunk 表");
            fs.Position = 0;
            byte[] sig = new byte[8];
            if (ReadFull(fs, sig, 8) != 8)
            {
                sw.WriteLine("- 读取签名失败");
                return -1;
            }
            sw.WriteLine("- 签名: " + Convert.ToHexString(sig));
            long pos = 8;
            byte[] hdr = new byte[8];
            for (int i = 0; i < 1000; i++)
            {
                if (pos + 8 > len)
                {
                    sw.WriteLine("- 到达文件尾，未遇 IEND");
                    return -1;
                }
                fs.Position = pos;
                if (ReadFull(fs, hdr, 8) != 8)
                {
                    sw.WriteLine("- 读取 chunk 头失败");
                    return -1;
                }
                long clen = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                string type = Encoding.ASCII.GetString(hdr, 4, 4);
                if (type != "IDAT" || i < 3 || clen != 8192)
                {
                    sw.WriteLine("- @" + pos.ToString("N0") + "  type=" + type + "  len=" + clen.ToString("N0"));
                }
                if (type == "IEND")
                {
                    long end = pos + 12;
                    sw.WriteLine("- IEND 结束于 " + end.ToString("N0") + " 字节（图片部分占 " + end.ToString("N0") + " 字节；其余 " + (len - end).ToString("N0") + " 字节为卡片数据）");
                    return end;
                }
                if (clen < 0 || pos + 12 + clen > len)
                {
                    sw.WriteLine("- !! chunk 长度越界，停止遍历");
                    return -1;
                }
                pos += 12 + clen;
            }
            return -1;
        }

        /// <summary>流式提取字符串写入明细文件，返回条目数；同时把含关键词的串收集到 hits。</summary>
        private static long WriteStrings(FileStream fs, string path, StreamWriter sw, List<string> hits)
        {
            long count = 0;
            fs.Position = 0;
            long offset = 0;
            var sb = new StringBuilder();
            long start = 0;
            byte[] buf = new byte[1 << 20];
            using (var outSw = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        byte b = buf[i];
                        if (b >= 0x20 && b <= 0x7E)
                        {
                            if (sb.Length == 0)
                            {
                                start = offset + i;
                            }
                            sb.Append((char)b);
                        }
                        else
                        {
                            count += Flush(sb, start, outSw, hits);
                        }
                    }
                    offset += read;
                }
                count += Flush(sb, start, outSw, hits);
            }
            return count;
        }

        private static long Flush(StringBuilder sb, long start, StreamWriter outSw, List<string> hits)
        {
            long n = 0;
            if (sb.Length >= 8 && HasLetterOrBracket(sb))
            {
                string s = sb.ToString();
                outSw.WriteLine(start.ToString() + "\t" + s);
                n = 1;
                foreach (string k in ScanKeywords)
                {
                    if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits.Add("@" + start.ToString("N0") + "  \"" + s + "\"");
                        break;
                    }
                }
            }
            sb.Clear();
            return n;
        }

        private static bool HasLetterOrBracket(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '[' || c == ']')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>区间 hex dump 命令。</summary>
        private static int HexRange(string src, string outPath, long start, int length)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            using (var fs = File.OpenRead(src))
            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# " + src + "  @ " + start.ToString("N0") + " .. +" + length.ToString("N0"));
                HexInto(fs, sw, start, length);
            }
            Console.WriteLine("dump: " + outPath);
            return 0;
        }

        /// <summary>从指定偏移 dump 指定字节数的 hex+ascii。</summary>
        private static void HexInto(FileStream fs, StreamWriter sw, long start, int length)
        {
            if (start < 0)
            {
                start = 0;
            }
            fs.Position = start;
            byte[] buf = new byte[length];
            int n = ReadFull(fs, buf, length);
            for (int off = 0; off < n; off += 32)
            {
                int take = Math.Min(32, n - off);
                sw.WriteLine(HexLine(buf, off, take, start));
            }
            if (n < length)
            {
                sw.WriteLine("  (仅读到 " + n + " 字节)");
            }
        }

        private static string HexLine(byte[] buf, int off, int take, long baseOffset)
        {
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                hex.Append(buf[off + i].ToString("X2")).Append(' ');
                char c = (char)buf[off + i];
                ascii.Append(c >= 0x20 && c <= 0x7E ? c : '.');
            }
            return "  @" + (baseOffset + off).ToString("N0") + "  " + hex.ToString().PadRight(32 * 3) + " |" + ascii + "|";
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

        /// <summary>
        /// 提取卡片声明的 mod 标识——以 MessagePack 锚点 fixstr(5)"ModID" 定位，读紧随其后的字符串值。
        /// 同时统计 Sideloader UAR 标记出现次数（判断卡片内含几处 mod 声明区）。
        /// </summary>
        private static int ExtractModIds(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }

            byte[] anchor = { 0xA5, 0x4D, 0x6F, 0x64, 0x49, 0x44 };
            byte[] uarMark = Encoding.ASCII.GetBytes("com.bepis.sideloader.universalautoresolver");

            using (var fs = File.OpenRead(src))
            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                long len = fs.Length;
                long iendEnd = FindIendEnd(fs, len);
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + len.ToString("N0") + " 字节");
                sw.WriteLine("# 图片区结束(IEND): " + iendEnd.ToString("N0"));
                sw.WriteLine();

                long uarCount = 0;
                int failed = 0;
                var order = new List<string>();
                var set = new HashSet<string>();

                // 只扫数据区（图片区不含 ModID）——起点归位到 IEND 之后，并作为偏移基准
                fs.Position = iendEnd > 0 ? iendEnd : 0;
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 64];
                long baseOffset = fs.Position;
                int carry = 0;
                int read;
                while ((read = fs.Read(buf, carry, BufSize)) > 0)
                {
                    int total = carry + read;
                    for (int i = 0; i <= total - anchor.Length; i++)
                    {
                        if (buf[i] != anchor[0] || !MatchAt(buf, i, anchor))
                        {
                            continue;
                        }
                        int p = i + anchor.Length;
                        string v = ReadMsgPackString(buf, ref p, total);
                        if (v == null)
                        {
                            failed++;
                            sw.WriteLine("- @" + (baseOffset + i).ToString("N0") + "  ModID = <读取失败>");
                            continue;
                        }
                        sw.WriteLine("- @" + (baseOffset + i).ToString("N0") + "  ModID = \"" + v + "\"");
                        if (set.Add(v))
                        {
                            order.Add(v);
                        }
                    }
                    for (int i = 0; i <= total - uarMark.Length; i++)
                    {
                        if (buf[i] == uarMark[0] && MatchAt(buf, i, uarMark))
                        {
                            uarCount++;
                        }
                    }
                    carry = Math.Min(63, total);
                    Array.Copy(buf, total - carry, buf, 0, carry);
                    baseOffset += total - carry;
                }

                sw.WriteLine();
                sw.WriteLine("## 汇总");
                sw.WriteLine("- ModID 条目数: " + (order.Count + failed).ToString("N0") + "（读取失败 " + failed + "）");
                sw.WriteLine("- 去重后 mod 数: " + order.Count);
                sw.WriteLine("- Sideloader UAR 标记出现次数: " + uarCount);
                sw.WriteLine();
                sw.WriteLine("## 去重 mod 标识");
                foreach (string m in order)
                {
                    sw.WriteLine("- " + m);
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>定位 PNG 的 IEND 结束偏移（图片区终点）；未找到返回 -1。</summary>
        private static long FindIendEnd(FileStream fs, long len)
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

        /// <summary>在缓冲区指定位置匹配字节序列。</summary>
        private static bool MatchAt(byte[] buf, int at, byte[] pattern)
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

        /// <summary>按 MessagePack 字符串规则读取一个字符串；无法解析返回 null。</summary>
        private static string ReadMsgPackString(byte[] buf, ref int p, int total)
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

        /// <summary>读取 zipmod 容器内 manifest.xml 的 guid / name / author；失败返回 null。</summary>
        private static string ReadZipModGuid(string zipPath, out string name, out string author)
        {
            name = null;
            author = null;
            try
            {
                using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.GetEntry("manifest.xml");
                    if (entry == null)
                    {
                        return null;
                    }
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var doc = new System.Xml.XmlDocument();
                        doc.LoadXml(reader.ReadToEnd());
                        var guidNode = doc.SelectSingleNode("//guid");
                        var nameNode = doc.SelectSingleNode("//name");
                        var authorNode = doc.SelectSingleNode("//author");
                        if (nameNode != null)
                        {
                            name = nameNode.InnerText;
                        }
                        if (authorNode != null)
                        {
                            author = authorNode.InnerText;
                        }
                        return guidNode != null ? guidNode.InnerText : null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("manifest 读取失败 " + zipPath + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>把卡片声明的全部 ModID 收进集合，返回新增条数。</summary>
        private static int CollectModIds(string src, HashSet<string> set)
        {
            byte[] anchor = { 0xA5, 0x4D, 0x6F, 0x64, 0x49, 0x44 };
            int before = set.Count;
            using (var fs = File.OpenRead(src))
            {
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 64];
                int carry = 0;
                int read;
                while ((read = fs.Read(buf, carry, BufSize)) > 0)
                {
                    int total = carry + read;
                    for (int i = 0; i <= total - anchor.Length; i++)
                    {
                        if (buf[i] != anchor[0] || !MatchAt(buf, i, anchor))
                        {
                            continue;
                        }
                        int p = i + anchor.Length;
                        string v = ReadMsgPackString(buf, ref p, total);
                        if (v != null)
                        {
                            set.Add(v);
                        }
                    }
                    carry = Math.Min(63, total);
                    Array.Copy(buf, total - carry, buf, 0, carry);
                }
            }
            return set.Count - before;
        }

        /// <summary>目录级关联冒烟——mod 库建 guid 索引，卡片逐个取 ModID 集合，输出命中/缺失。</summary>
        private static int Link(string cardDir, string modDir, string outPath, int limit)
        {
            if (!Directory.Exists(cardDir) || !Directory.Exists(modDir))
            {
                Console.Error.WriteLine("目录不存在: " + cardDir + " / " + modDir);
                return 2;
            }

            var modByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            var modRows = new List<string>();
            string[] zipMods = Directory.GetFiles(modDir, "*.zipmod", SearchOption.AllDirectories);
            Array.Sort(zipMods, StringComparer.Ordinal);

            var cards = new List<string>(Directory.GetFiles(cardDir, "*.png", SearchOption.AllDirectories));
            cards.Sort(StringComparer.Ordinal);
            bool limited = limit > 0 && cards.Count > limit;
            if (limited)
            {
                cards = cards.GetRange(0, limit);
            }

            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 卡片目录: " + cardDir + "（共 " + Directory.GetFiles(cardDir, "*.png", SearchOption.AllDirectories).Length + " 张，本次处理 " + cards.Count + "）");
                sw.WriteLine("# mod 目录: " + modDir);
                sw.WriteLine();
                sw.WriteLine("## 一、mod 库索引（" + zipMods.Length + " 个 zipmod）");
                foreach (string z in zipMods)
                {
                    string name;
                    string author;
                    string guid = ReadZipModGuid(z, out name, out author);
                    if (guid == null)
                    {
                        sw.WriteLine("- <manifest 缺失或不可解析>: " + Path.GetFileName(z));
                        continue;
                    }
                    if (modByGuid.ContainsKey(guid))
                    {
                        sw.WriteLine("- <guid 重复> " + guid + "  已有: " + modByGuid[guid] + "  本次: " + Path.GetFileName(z));
                        continue;
                    }
                    modByGuid[guid] = Path.GetFileName(z);
                    modRows.Add(guid);
                    sw.WriteLine("- " + guid + "  <=  " + Path.GetFileName(z) + "   (name=" + name + ", author=" + author + ")");
                }
                sw.WriteLine();

                sw.WriteLine("## 二、卡片关联");
                int totalRefs = 0;
                int totalHit = 0;
                int cardsWithMods = 0;
                var missingCount = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string card in cards)
                {
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    CollectModIds(card, set);
                    if (set.Count == 0)
                    {
                        sw.WriteLine("### " + Path.GetFileName(card) + "  —— 无 ModID（未用 mod 或无声明区）");
                        continue;
                    }
                    cardsWithMods++;
                    var ordered = new List<string>(set);
                    ordered.Sort(StringComparer.Ordinal);
                    sw.WriteLine("### " + Path.GetFileName(card) + "  (" + ordered.Count + " mod)");
                    foreach (string g in ordered)
                    {
                        totalRefs++;
                        if (modByGuid.ContainsKey(g))
                        {
                            totalHit++;
                            sw.WriteLine("- [OK] " + g + "  <= " + modByGuid[g]);
                        }
                        else
                        {
                            int c;
                            missingCount.TryGetValue(g, out c);
                            missingCount[g] = c + 1;
                            sw.WriteLine("- [缺] " + g);
                        }
                    }
                }

                sw.WriteLine();
                sw.WriteLine("## 三、汇总");
                sw.WriteLine("- 卡片处理数: " + cards.Count + "（含 mod 引用 " + cardsWithMods + "）");
                sw.WriteLine("- mod 库 guid 数: " + modByGuid.Count);
                sw.WriteLine("- 引用条目数: " + totalRefs + "  命中 " + totalHit + "  缺失 " + (totalRefs - totalHit));
                sw.WriteLine("- 涉及缺失 guid 数: " + missingCount.Count);
                sw.WriteLine();
                sw.WriteLine("## 四、缺失 guid 排行（按被引用卡数）");
                var missList = new List<KeyValuePair<string, int>>(missingCount);
                missList.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in missList)
                {
                    sw.WriteLine("- " + kv.Key + "  (" + kv.Value + " 张卡引用)");
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>目录统计——按扩展名分组统计文件数与总字节数（递归），并给出一级子目录分布。</summary>
        private static int Stat(string dir, string outPath)
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine("目录不存在: " + dir);
                return 2;
            }

            var count = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var topCount = new Dictionary<string, long>(StringComparer.Ordinal);
            var topBytes = new Dictionary<string, long>(StringComparer.Ordinal);
            long totalFiles = 0;
            long totalBytes = 0;

            var root = new DirectoryInfo(dir);
            foreach (FileInfo fi in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string ext = fi.Extension;
                if (ext.Length == 0)
                {
                    ext = "(无扩展名)";
                }
                long len = fi.Length;
                long c;
                count.TryGetValue(ext, out c);
                count[ext] = c + 1;
                long b;
                bytes.TryGetValue(ext, out b);
                bytes[ext] = b + len;
                totalFiles++;
                totalBytes += len;

                string rel = fi.FullName.Length > dir.Length
                    ? fi.FullName.Substring(dir.Length).TrimStart('\\', '/')
                    : fi.Name;
                int sep = rel.IndexOf('\\');
                string top = sep >= 0 ? rel.Substring(0, sep) : "(根目录文件)";
                long tc;
                topCount.TryGetValue(top, out tc);
                topCount[top] = tc + 1;
                long tb;
                topBytes.TryGetValue(top, out tb);
                topBytes[top] = tb + len;
            }

            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 目录: " + dir);
                sw.WriteLine("# 文件总数: " + totalFiles.ToString("N0") + "  总大小: " + totalBytes.ToString("N0") + " 字节 (" + (totalBytes / 1048576.0).ToString("N1") + " MB)");
                sw.WriteLine();
                sw.WriteLine("## 按扩展名（按总大小倒序）");
                var exts = new List<string>(count.Keys);
                exts.Sort((a, b) => bytes[b].CompareTo(bytes[a]));
                foreach (string e in exts)
                {
                    sw.WriteLine("- " + e + "  文件 " + count[e].ToString("N0") + "  大小 " + bytes[e].ToString("N0") + " 字节 (" + (bytes[e] / 1048576.0).ToString("N1") + " MB)");
                }
                sw.WriteLine();
                sw.WriteLine("## 一级子目录分布（按文件数倒序）");
                var tops = new List<string>(topCount.Keys);
                tops.Sort((a, b) => topCount[b].CompareTo(topCount[a]));
                foreach (string t in tops)
                {
                    sw.WriteLine("- " + t + "  文件 " + topCount[t].ToString("N0") + "  大小 " + (topBytes[t] / 1048576.0).ToString("N1") + " MB");
                }
            }

            Console.WriteLine("报告: " + outPath + "  文件数 " + totalFiles.ToString("N0"));
            return 0;
        }
        /// <summary>十六进制文本转字节数组（每两字符一字节）。</summary>
        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i = i + 1)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        /// <summary>
        /// 数据区锚点扫描——在 IEND 之后的数据区里找一组关键标记（PNG 签名 / 插件块 / 声明区等），
        /// 输出各标记的命中次数与前若干偏移。只读，不改源文件。
        /// </summary>
        private static int Anchors(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }

            // [段1] 锚点表——名字 + 十六进制字节
            string[][] specs = new string[][]
            {
                        new string[] { "PNG签名", "89504E470D0A1A0A" },
                        new string[] { "KKEx", "4B4B4578" },
                        new string[] { "abdata", "616264617461" },
                        new string[] { "ChaFileCustom", "43686146696C65437573746F6D" },
                        new string[] { "FaceImage", "46616365496D616765" },
                        new string[] { "UAR声明区", "636F6D2E62657069732E736964656C6F616465722E756E6976657273616C6175746F7265736F6C766572" },
                        new string[] { "KoiKatu", "4B6F694B617475" },
                        new string[] { "ExtendedSaveData", "457874656E6465645361766544617461" },
                        new string[] { "TextureDictionary", "5465787475726544696374696F6E617279" },
                        new string[] { "KKABMX", "4B4B41424D58" },
                        new string[] { "madevil", "6D61646576696C" },
                        new string[] { "zipmod", "7A69706D6F64" }
            };
            byte[][] pats = new byte[specs.Length][];
            int maxLen = 0;
            for (int i = 0; i < specs.Length; i = i + 1)
            {
                pats[i] = HexToBytes(specs[i][1]);
                if (pats[i].Length > maxLen)
                {
                    maxLen = pats[i].Length;
                }
            }

            long[] counts = new long[pats.Length];
            List<long>[] samples = new List<long>[pats.Length];
            for (int i = 0; i < pats.Length; i = i + 1)
            {
                samples[i] = new List<long>();
            }

            long size = 0;
            long start = 0;
            using (FileStream fs = File.OpenRead(src))
            {
                size = fs.Length;
                long iendEnd = FindIendEnd(fs, size);
                start = iendEnd > 0 ? iendEnd : 0;

                // [段2] 单遍流式扫描——数据区起点归位到 IEND 之后
                fs.Position = start;
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 64];
                long baseOffset = start;
                int carry = 0;
                int read;
                while ((read = fs.Read(buf, carry, BufSize)) > 0)
                {
                    int total = carry + read;
                    int limit = total - maxLen;
                    for (int i = 0; i <= limit; i = i + 1)
                    {
                        for (int a = 0; a < pats.Length; a = a + 1)
                        {
                            if (buf[i] != pats[a][0])
                            {
                                continue;
                            }
                            if (!MatchAt(buf, i, pats[a]))
                            {
                                continue;
                            }
                            counts[a] = counts[a] + 1;
                            if (samples[a].Count < 8)
                            {
                                samples[a].Add(baseOffset + i);
                            }
                        }
                    }
                    carry = maxLen - 1;
                    if (carry > total)
                    {
                        carry = total;
                    }
                    Array.Copy(buf, total - carry, buf, 0, carry);
                    baseOffset += total - carry;
                }
            }

            // [段3] 报告
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + size.ToString("N0") + " 字节");
                sw.WriteLine("# 数据区起点(IEND 之后): " + start.ToString("N0"));
                sw.WriteLine();
                for (int a = 0; a < specs.Length; a = a + 1)
                {
                    StringBuilder pos = new StringBuilder();
                    for (int k = 0; k < samples[a].Count; k = k + 1)
                    {
                        if (k > 0)
                        {
                            pos.Append(", ");
                        }
                        pos.Append(samples[a][k].ToString("N0"));
                    }
                    if (counts[a] > 0)
                    {
                        sw.WriteLine("- " + specs[a][0] + ": " + counts[a].ToString("N0") + " 次  前几处 @" + pos.ToString());
                    }
                    else
                    {
                        sw.WriteLine("- " + specs[a][0] + ": 0 次");
                    }
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }
        /// <summary>
        /// 数据区内嵌图片清单——扫 PNG 签名并逐张解析（宽高 / 位深 / 色型 / 字节数 / 块数），
        /// 回答「卡片体积被什么占掉」。只读，不改源文件。
        /// </summary>
        private static int Images(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }

            byte[] sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            List<long> hits = new List<long>();
            long size = 0;
            long start = 0;

            // [段1] 扫数据区收集 PNG 签名偏移
            using (FileStream fs = File.OpenRead(src))
            {
                size = fs.Length;
                long iendEnd = FindIendEnd(fs, size);
                start = iendEnd > 0 ? iendEnd : 0;
                fs.Position = start;
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 64];
                long baseOffset = start;
                int carry = 0;
                int read;
                while ((read = fs.Read(buf, carry, BufSize)) > 0)
                {
                    int total = carry + read;
                    int limit = total - sig.Length;
                    for (int i = 0; i <= limit; i = i + 1)
                    {
                        if (buf[i] != sig[0])
                        {
                            continue;
                        }
                        if (!MatchAt(buf, i, sig))
                        {
                            continue;
                        }
                        hits.Add(baseOffset + i);
                    }
                    carry = sig.Length - 1;
                    if (carry > total)
                    {
                        carry = total;
                    }
                    Array.Copy(buf, total - carry, buf, 0, carry);
                    baseOffset += total - carry;
                }
            }

            // [段2] 逐张解析块表
            using (FileStream fs = File.OpenRead(src))
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + size.ToString("N0") + " 字节 · 数据区起点: " + start.ToString("N0"));
                sw.WriteLine("# 数据区内 PNG 签名: " + hits.Count.ToString("N0") + " 处");
                sw.WriteLine();
                long totalBytes = 0;
                byte[] hdr = new byte[8];
                for (int k = 0; k < hits.Count; k = k + 1)
                {
                    long off = hits[k];
                    long pos = off + 8;
                    long imgBytes = 8;
                    int width = 0;
                    int height = 0;
                    int bitDepth = 0;
                    int colorType = 0;
                    int chunks = 0;
                    bool ended = false;
                    while (chunks < 20000)
                    {
                        fs.Position = pos;
                        if (ReadFull(fs, hdr, 8) != 8)
                        {
                            break;
                        }
                        long clen = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                        string type = Encoding.ASCII.GetString(hdr, 4, 4);
                        if (clen < 0 || pos + 12 + clen > size)
                        {
                            break;
                        }
                        if (type == "IHDR" && clen == 13)
                        {
                            byte[] ih = new byte[13];
                            fs.Position = pos + 8;
                            if (ReadFull(fs, ih, 13) == 13)
                            {
                                width = (ih[0] << 24) | (ih[1] << 16) | (ih[2] << 8) | ih[3];
                                height = (ih[4] << 24) | (ih[5] << 16) | (ih[6] << 8) | ih[7];
                                bitDepth = ih[8];
                                colorType = ih[9];
                            }
                        }
                        chunks = chunks + 1;
                        imgBytes = pos + 12 + clen - off;
                        if (type == "IEND")
                        {
                            ended = true;
                            break;
                        }
                        pos = pos + 12 + clen;
                    }
                    totalBytes += imgBytes;
                    sw.WriteLine("- #" + (k + 1) + " @" + off.ToString("N0") + "  " + imgBytes.ToString("N0") + " 字节  "
                        + width + "×" + height + "  位深 " + bitDepth + " 色型 " + colorType + "  块 " + chunks
                        + (ended ? "" : "  [未闭合]"));
                }
                sw.WriteLine();
                sw.WriteLine("# 内嵌图片合计: " + totalBytes.ToString("N0") + " 字节（占数据区 "
                    + (start > 0 ? (totalBytes * 100.0 / (size - start)).ToString("F1") : "0") + "%）");
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>UnityFS（AssetBundle）结构侦察——头部字段 + 块表 + 文件清单 + 数据区头部试读；只读。</summary>
        /// <summary>把 zipmod 容器内的一个条目解出来落盘（侦察用——供 unityfs / sftex 等命令直接吃原始文件）。</summary>
        private static int U3dDump(string zipPath, string entryPath, string outPath)
        {
            if (!File.Exists(zipPath))
            {
                Console.Error.WriteLine("源文件不存在: " + zipPath);
                return 2;
            }
            try
            {
                using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    System.IO.Compression.ZipArchiveEntry entry = zip.GetEntry(entryPath);
                    if (entry == null)
                    {
                        Console.Error.WriteLine("容器内没有该条目: " + entryPath);
                        return 2;
                    }
                    using (Stream stream = entry.Open())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        byte[] bytes = ms.ToArray();
                        string dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.WriteAllBytes(outPath, bytes);
                        Console.WriteLine("解出 " + outPath + " · " + bytes.Length.ToString("N0") + " 字节（容器内声明 " + entry.Length.ToString("N0") + " 字节）");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("解条目失败: " + ex.Message);
                return 2;
            }
        }

        private static int UnityFs(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            using (FileStream fs = File.OpenRead(src))
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                long len = fs.Length;
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + len.ToString("N0") + " 字节");
                sw.WriteLine();

                // [段1] 头部字段
                byte[] sig = new byte[8];
                if (ReadFull(fs, sig, 8) != 8)
                {
                    sw.WriteLine("- 读取签名失败");
                    return 2;
                }
                sw.WriteLine("## 一、头部");
                sw.WriteLine("- 签名: " + Encoding.ASCII.GetString(sig).Replace('\0', '.'));
                int version = ReadInt32Big(fs);
                sw.WriteLine("- 容器格式版本: " + version.ToString());
                sw.WriteLine("- Unity 版本: " + ReadCString(fs));
                sw.WriteLine("- Unity 修订: " + ReadCString(fs));
                sw.WriteLine("- 声明总大小: " + ReadInt64Big(fs).ToString("N0"));
                int compInfoSize = ReadInt32Big(fs);
                int uncompInfoSize = ReadInt32Big(fs);
                int flags = ReadInt32Big(fs);
                sw.WriteLine("- blocksInfo 压缩后: " + compInfoSize.ToString("N0") + " 字节");
                sw.WriteLine("- blocksInfo 解压后: " + uncompInfoSize.ToString("N0") + " 字节");
                sw.WriteLine("- flags: 0x" + flags.ToString("X8") + " · 压缩方式 " + CompressionName(flags & 0x3F));
                if ((flags & 0x80) != 0)
                {
                    sw.WriteLine("- blocksInfo 位于文件尾: 是");
                }
                else
                {
                    sw.WriteLine("- blocksInfo 位于文件尾: 否");
                }
                if (version >= 7)
                {
                    sw.WriteLine("- 数据对齐: " + ReadInt32Big(fs).ToString());
                }
                long headerEnd = fs.Position;
                long infoPos = headerEnd;
                if ((flags & 0x80) != 0)
                {
                    infoPos = len - compInfoSize;
                }
                sw.WriteLine("- 头部结束: " + headerEnd.ToString("N0") + " · blocksInfo 位置: " + infoPos.ToString("N0"));
                sw.WriteLine();

                // [段2] blocksInfo 读取与解压
                byte[] rawInfo = new byte[compInfoSize];
                fs.Position = infoPos;
                if (ReadFull(fs, rawInfo, compInfoSize) != compInfoSize)
                {
                    sw.WriteLine("- !! blocksInfo 读取不足");
                    return 2;
                }
                byte[] info = rawInfo;
                int infoComp = flags & 0x3F;
                if (infoComp == 1)
                {
                    sw.WriteLine("- !! blocksInfo 为 LZMA——本探针未实现");
                    return 2;
                }
                if (infoComp == 2 || infoComp == 3)
                {
                    byte[] unpacked = new byte[uncompInfoSize];
                    string lz4Err;
                    int got = Lz4BlockDecompress(rawInfo, compInfoSize, unpacked, uncompInfoSize, out lz4Err);
                    if (lz4Err != null)
                    {
                        sw.WriteLine("- !! blocksInfo LZ4 解压失败: " + lz4Err);
                        return 2;
                    }
                    info = new byte[got];
                    Array.Copy(unpacked, 0, info, 0, got);
                }
                sw.WriteLine("## 二、blocksInfo（解压后 " + info.Length.ToString("N0") + " 字节）");
                if (info.Length < 20)
                {
                    sw.WriteLine("- !! blocksInfo 过短");
                    return 2;
                }
                sw.WriteLine("- 前 16 字节: " + Convert.ToHexString(info, 0, 16));
                sw.WriteLine("- 前 96 字节 dump:");
                int dumpN = info.Length < 96 ? info.Length : 96;
                for (int off = 0; off < dumpN; off = off + 32)
                {
                    int take = Math.Min(32, dumpN - off);
                    sw.WriteLine(HexLine(info, off, take, off));
                }
                int p = 16;
                int blockCount = ReadInt32Big(info, p);
                p = p + 4;
                sw.WriteLine("- 数据块数: " + blockCount.ToString());
                int[] blockUncomp = new int[blockCount];
                int[] blockComp = new int[blockCount];
                int[] blockFlags = new int[blockCount];
                for (int i = 0; i < blockCount; i = i + 1)
                {
                    blockUncomp[i] = ReadInt32Big(info, p);
                    p = p + 4;
                    blockComp[i] = ReadInt32Big(info, p);
                    p = p + 4;
                    blockFlags[i] = (info[p] << 8) | info[p + 1];
                    p = p + 2;
                    sw.WriteLine("  · #" + i.ToString() + " 解压 " + blockUncomp[i].ToString("N0") + " / 压缩 " + blockComp[i].ToString("N0") + " 字节 · " + CompressionName(blockFlags[i] & 0x3F));
                }
                int nodeCount = ReadInt32Big(info, p);
                p = p + 4;
                sw.WriteLine();
                sw.WriteLine("## 三、文件清单（" + nodeCount.ToString() + " 项）");
                for (int i = 0; i < nodeCount; i = i + 1)
                {
                    long off = ReadInt64Big(info, p);
                    p = p + 8;
                    long size = ReadInt64Big(info, p);
                    p = p + 8;
                    ReadInt32Big(info, p);
                    p = p + 4;
                    string path = ReadCString(info, ref p);
                    sw.WriteLine("  · " + path + "  @ " + off.ToString("N0") + "  " + size.ToString("N0") + " 字节");
                }

                // [段3] 首块解压 + 数据区头部 hex
                sw.WriteLine();
                long dataStart = infoPos + compInfoSize;
                if ((flags & 0x80) != 0)
                {
                    dataStart = headerEnd;
                }
                sw.WriteLine("## 四、数据区（起点 " + dataStart.ToString("N0") + "）");
                if (blockCount == 0)
                {
                    sw.WriteLine("- 无数据块");
                    return 0;
                }
                byte[] blockSrc = new byte[blockComp[0]];
                fs.Position = dataStart;
                if (ReadFull(fs, blockSrc, blockComp[0]) != blockComp[0])
                {
                    sw.WriteLine("- !! 首块读取不足");
                    return 2;
                }
                int firstLen = 0;
                byte[] first = new byte[blockUncomp[0]];
                int firstComp = blockFlags[0] & 0x3F;
                if (firstComp == 0)
                {
                    firstLen = blockComp[0] < blockUncomp[0] ? blockComp[0] : blockUncomp[0];
                    Array.Copy(blockSrc, first, firstLen);
                }
                else if (firstComp == 2 || firstComp == 3)
                {
                    string firstErr;
                    firstLen = Lz4BlockDecompress(blockSrc, blockComp[0], first, blockUncomp[0], out firstErr);
                    if (firstErr != null)
                    {
                        sw.WriteLine("- !! 首块解压失败: " + firstErr);
                        return 2;
                    }
                }
                else
                {
                    sw.WriteLine("- !! 首块压缩方式 " + CompressionName(firstComp) + " 未实现");
                    return 2;
                }
                sw.WriteLine("- 首块解压后 " + firstLen.ToString("N0") + " 字节 · 前 256 字节:");
                int limit = firstLen < 256 ? firstLen : 256;
                for (int off = 0; off < limit; off = off + 32)
                {
                    int take = Math.Min(32, limit - off);
                    sw.WriteLine(HexLine(first, off, take, off));
                }

                // [段4] SerializedFile 头试读（字段布局待实测校准）
                sw.WriteLine();
                sw.WriteLine("## 五、SerializedFile 头试读");
                try
                {
                    int sp = 0;
                    sw.WriteLine("- 元数据大小: " + ReadInt32Big(first, sp).ToString("N0"));
                    sp = sp + 4;
                    sw.WriteLine("- 文件大小: " + ReadInt32Big(first, sp).ToString("N0"));
                    sp = sp + 4;
                    sw.WriteLine("- 序列化版本: " + ReadInt32Big(first, sp).ToString());
                    sp = sp + 4;
                    sw.WriteLine("- 数据偏移: " + ReadInt32Big(first, sp).ToString("N0"));
                    sp = sp + 4;
                    sw.WriteLine("- 字节序标志: " + first[sp].ToString());
                    sp = sp + 4;
                    sw.WriteLine("- Unity 版本串: " + ReadCString(first, ref sp));
                    sw.WriteLine("- 目标平台: " + ReadInt32Big(first, sp).ToString());
                    sp = sp + 4;
                    sw.WriteLine("- enableTypeTree: " + first[sp].ToString());
                    sp = sp + 1;
                    sw.WriteLine("- 类型数: " + ReadInt32Big(first, sp).ToString());
                }
                catch (Exception ex)
                {
                    sw.WriteLine("- !! 试读越界: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>LZ4 块解压（Unity 使用的 block 格式，无帧头）；成功返回写出字节数，失败返回 0 并在 error 给诊断。</summary>
        private static int Lz4BlockDecompress(byte[] src, int srcLen, byte[] dst, int dstLen, out string error)
        {
            error = null;
            int ip = 0;
            int op = 0;
            while (ip < srcLen)
            {
                int token = src[ip];
                ip = ip + 1;

                // [段1] 字面量段
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b = 255;
                    while (b == 255)
                    {
                        if (ip >= srcLen)
                        {
                            error = "字面量长度越界";
                            return 0;
                        }
                        b = src[ip];
                        ip = ip + 1;
                        litLen = litLen + b;
                    }
                }
                if (ip + litLen > srcLen)
                {
                    error = "字面量读取越界（ip=" + ip.ToString() + " lit=" + litLen.ToString() + "）";
                    return 0;
                }
                if (op + litLen > dstLen)
                {
                    error = "字面量写出越界（op=" + op.ToString() + " lit=" + litLen.ToString() + "）";
                    return 0;
                }
                Array.Copy(src, ip, dst, op, litLen);
                ip = ip + litLen;
                op = op + litLen;

                // [段2] 匹配段（块尾可无）
                if (ip >= srcLen)
                {
                    break;
                }
                if (ip + 2 > srcLen)
                {
                    error = "匹配偏移读取越界";
                    return 0;
                }
                int offset = src[ip] | (src[ip + 1] << 8);
                ip = ip + 2;
                if (offset == 0 || offset > op)
                {
                    error = "匹配偏移非法（offset=" + offset.ToString() + " op=" + op.ToString() + "）";
                    return 0;
                }
                int matchLen = token & 0x0F;
                if (matchLen == 15)
                {
                    int b = 255;
                    while (b == 255)
                    {
                        if (ip >= srcLen)
                        {
                            error = "匹配长度越界";
                            return 0;
                        }
                        b = src[ip];
                        ip = ip + 1;
                        matchLen = matchLen + b;
                    }
                }
                matchLen = matchLen + 4;
                if (op + matchLen > dstLen)
                {
                    error = "匹配写出越界（op=" + op.ToString() + " len=" + matchLen.ToString() + "）";
                    return 0;
                }
                int from = op - offset;
                for (int i = 0; i < matchLen; i = i + 1)
                {
                    dst[op + i] = dst[from + i];
                }
                op = op + matchLen;
            }
            return op;
        }

        /// <summary>压缩方式代号转名称（UnityFS flags 低 6 位）。</summary>
        private static string CompressionName(int code)
        {
            if (code == 0)
            {
                return "无压缩";
            }
            if (code == 1)
            {
                return "LZMA";
            }
            if (code == 2)
            {
                return "LZ4";
            }
            if (code == 3)
            {
                return "LZ4HC";
            }
            return "未知(" + code.ToString() + ")";
        }

        /// <summary>读大端 32 位整数（流）。</summary>
        private static int ReadInt32Big(FileStream fs)
        {
            byte[] b = new byte[4];
            if (ReadFull(fs, b, 4) != 4)
            {
                return 0;
            }
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        /// <summary>读大端 64 位整数（流）。</summary>
        private static long ReadInt64Big(FileStream fs)
        {
            byte[] b = new byte[8];
            if (ReadFull(fs, b, 8) != 8)
            {
                return 0;
            }
            long v = 0;
            for (int i = 0; i < 8; i = i + 1)
            {
                v = (v << 8) | b[i];
            }
            return v;
        }

        /// <summary>读 null 终止字符串（流）。</summary>
        private static string ReadCString(FileStream fs)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while ((b = fs.ReadByte()) > 0)
            {
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>读大端 32 位整数（字节数组）。</summary>
        private static int ReadInt32Big(byte[] buf, int at)
        {
            return (buf[at] << 24) | (buf[at + 1] << 16) | (buf[at + 2] << 8) | buf[at + 3];
        }

        /// <summary>读大端 64 位整数（字节数组）。</summary>
        private static long ReadInt64Big(byte[] buf, int at)
        {
            long v = 0;
            for (int i = 0; i < 8; i = i + 1)
            {
                v = (v << 8) | buf[at + i];
            }
            return v;
        }

        /// <summary>读 null 终止字符串（字节数组，指针前移）。</summary>
        private static string ReadCString(byte[] buf, ref int at)
        {
            int start = at;
            while (at < buf.Length && buf[at] != 0)
            {
                at = at + 1;
            }
            string s = Encoding.UTF8.GetString(buf, start, at - start);
            at = at + 1;
            return s;
        }

        /// <summary>读小端 32 位整数（字节数组）。</summary>
        private static int ReadInt32Little(byte[] buf, int at)
        {
            return buf[at] | (buf[at + 1] << 8) | (buf[at + 2] << 16) | (buf[at + 3] << 24);
        }

        /// <summary>读小端 64 位整数（字节数组）。</summary>
        private static long ReadInt64Little(byte[] buf, int at)
        {
            long v = 0;
            for (int i = 7; i >= 0; i = i - 1)
            {
                v = (v << 8) | buf[at + i];
            }
            return v;
        }

        /// <summary>读小端 16 位整数（字节数组）。</summary>
        private static int ReadInt16Little(byte[] buf, int at)
        {
            return buf[at] | (buf[at + 1] << 8);
        }

        /// <summary>读 UnityFS 容器数据区——解压全部数据块并拼成连续字节；失败返回 null 并给诊断。</summary>
        private static byte[] LoadBundleData(string path, out string error)
        {
            error = null;
            using (FileStream fs = File.OpenRead(path))
            {
                long len = fs.Length;
                byte[] sig = new byte[8];
                if (ReadFull(fs, sig, 8) != 8)
                {
                    error = "读取签名失败";
                    return null;
                }
                int version = ReadInt32Big(fs);
                ReadCString(fs);
                ReadCString(fs);
                ReadInt64Big(fs);
                int compInfoSize = ReadInt32Big(fs);
                int uncompInfoSize = ReadInt32Big(fs);
                int flags = ReadInt32Big(fs);
                if (version >= 7)
                {
                    ReadInt32Big(fs);
                }
                long headerEnd = fs.Position;
                long infoPos = headerEnd;
                if ((flags & 0x80) != 0)
                {
                    infoPos = len - compInfoSize;
                }

                // [段1] blocksInfo 读取与解压
                byte[] rawInfo = new byte[compInfoSize];
                fs.Position = infoPos;
                if (ReadFull(fs, rawInfo, compInfoSize) != compInfoSize)
                {
                    error = "blocksInfo 读取不足";
                    return null;
                }
                byte[] info = rawInfo;
                int infoComp = flags & 0x3F;
                if (infoComp == 2 || infoComp == 3)
                {
                    byte[] unpacked = new byte[uncompInfoSize];
                    string lz4Err;
                    int got = Lz4BlockDecompress(rawInfo, compInfoSize, unpacked, uncompInfoSize, out lz4Err);
                    if (lz4Err != null)
                    {
                        error = "blocksInfo 解压失败: " + lz4Err;
                        return null;
                    }
                    info = new byte[got];
                    Array.Copy(unpacked, 0, info, 0, got);
                }
                else if (infoComp == 1)
                {
                    error = "blocksInfo 为 LZMA——未实现";
                    return null;
                }

                // [段2] 块表
                int p = 16;
                int blockCount = ReadInt32Big(info, p);
                p = p + 4;
                int[] blockUncomp = new int[blockCount];
                int[] blockComp = new int[blockCount];
                int[] blockFlags = new int[blockCount];
                for (int i = 0; i < blockCount; i = i + 1)
                {
                    blockUncomp[i] = ReadInt32Big(info, p);
                    p = p + 4;
                    blockComp[i] = ReadInt32Big(info, p);
                    p = p + 4;
                    blockFlags[i] = (info[p] << 8) | info[p + 1];
                    p = p + 2;
                }
                long dataStart = infoPos + compInfoSize;
                if ((flags & 0x80) != 0)
                {
                    dataStart = headerEnd;
                }

                // [段3] 逐块解压拼接
                int total = 0;
                for (int i = 0; i < blockCount; i = i + 1)
                {
                    total = total + blockUncomp[i];
                }
                byte[] data = new byte[total];
                long pos = dataStart;
                int at = 0;
                for (int i = 0; i < blockCount; i = i + 1)
                {
                    byte[] srcBlock = new byte[blockComp[i]];
                    fs.Position = pos;
                    if (ReadFull(fs, srcBlock, blockComp[i]) != blockComp[i])
                    {
                        error = "数据块 #" + i.ToString() + " 读取不足";
                        return null;
                    }
                    pos = pos + blockComp[i];
                    int comp = blockFlags[i] & 0x3F;
                    if (comp == 0)
                    {
                        Array.Copy(srcBlock, 0, data, at, blockComp[i]);
                    }
                    else if (comp == 2 || comp == 3)
                    {
                        byte[] tmp = new byte[blockUncomp[i]];
                        string be;
                        int got = Lz4BlockDecompress(srcBlock, blockComp[i], tmp, blockUncomp[i], out be);
                        if (be != null)
                        {
                            error = "数据块 #" + i.ToString() + " 解压失败: " + be;
                            return null;
                        }
                        Array.Copy(tmp, 0, data, at, got);
                    }
                    else
                    {
                        error = "数据块 #" + i.ToString() + " 压缩方式 " + CompressionName(comp) + " 未实现";
                        return null;
                    }
                    at = at + blockUncomp[i];
                }
                return data;
            }
        }

        /// <summary>SerializedFile 结构侦察——头 / 类型表（含 TypeTree 节点）/ 对象表试读；只读。</summary>
        private static int UnitySf(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            string loadErr;
            byte[] data = LoadBundleData(src, out loadErr);
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                if (loadErr != null)
                {
                    sw.WriteLine("- !! 数据区读取失败: " + loadErr);
                    return 2;
                }
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 数据区: " + data.Length.ToString("N0") + " 字节");
                sw.WriteLine();

                // [段1] SerializedFile 头（小端）
                sw.WriteLine("## 一、SerializedFile 头（小端）");
                int metadataSize = ReadInt32Little(data, 0);
                int fileSize = ReadInt32Little(data, 4);
                int sfVersion = ReadInt32Little(data, 8);
                int dataOffset = ReadInt32Little(data, 12);
                int at = 16;
                int endian = data[at];
                at = at + 4;
                string unityVer = ReadCString(data, ref at);
                int targetPlatform = ReadInt32Little(data, at);
                at = at + 4;
                int enableTypeTree = data[at];
                at = at + 1;
                int typeCount = ReadInt32Little(data, at);
                at = at + 4;
                sw.WriteLine("- 元数据大小: " + metadataSize.ToString("N0"));
                sw.WriteLine("- 文件大小: " + fileSize.ToString("N0"));
                sw.WriteLine("- 序列化版本: " + sfVersion.ToString());
                sw.WriteLine("- 数据偏移: " + dataOffset.ToString("N0"));
                sw.WriteLine("- 字节序标志: " + endian.ToString());
                sw.WriteLine("- Unity 版本串: " + unityVer);
                sw.WriteLine("- 目标平台: " + targetPlatform.ToString());
                sw.WriteLine("- enableTypeTree: " + enableTypeTree.ToString());
                sw.WriteLine("- 类型数: " + typeCount.ToString());
                sw.WriteLine();

                // [段2] 类型表（version 17 布局试读——节点按 24 字节）
                sw.WriteLine("## 二、类型表");
                for (int i = 0; i < typeCount; i = i + 1)
                {
                    int classID = ReadInt32Little(data, at);
                    at = at + 4;
                    int stripped = data[at];
                    at = at + 1;
                    int scriptTypeIndex = ReadInt16Little(data, at);
                    at = at + 2;
                    if (classID == 114)
                    {
                        at = at + 16;
                        if (sfVersion >= 17)
                        {
                            at = at + 16;
                        }
                    }
                    at = at + 16;
                    int nodeCount = 0;
                    if (enableTypeTree != 0)
                    {
                        nodeCount = ReadInt32Little(data, at);
                        at = at + 4;
                        sw.WriteLine("- 类 " + i.ToString() + " · classID " + classID.ToString() + " · stripped " + stripped.ToString() + " · scriptTypeIndex " + scriptTypeIndex.ToString() + " · TypeTree 节点 " + nodeCount.ToString());
                        for (int k = 0; k < nodeCount; k = k + 1)
                        {
                            int nVersion = ReadInt16Little(data, at);
                            int level = data[at + 2];
                            int typeFlags = data[at + 3];
                            long typeStrOffset = (long)(uint)ReadInt32Little(data, at + 4);
                            long nameStrOffset = (long)(uint)ReadInt32Little(data, at + 8);
                            int byteSize = ReadInt32Little(data, at + 12);
                            int index = ReadInt32Little(data, at + 16);
                            int metaFlag = ReadInt32Little(data, at + 20);
                            at = at + 24;
                            if (k < 8)
                            {
                                sw.WriteLine("    · 节点 " + k.ToString() + " v" + nVersion.ToString() + " lv" + level.ToString() + " tf" + typeFlags.ToString()
                                    + " type@" + typeStrOffset.ToString() + " name@" + nameStrOffset.ToString()
                                    + " size " + byteSize.ToString() + " idx " + index.ToString() + " meta " + metaFlag.ToString());
                            }
                        }
                    }
                    else
                    {
                        sw.WriteLine("- 类 " + i.ToString() + " · classID " + classID.ToString() + " · 无 TypeTree");
                    }
                }
                sw.WriteLine("- 类型表结束于偏移: " + at.ToString("N0"));
                sw.WriteLine();

                // [段3] 类型表之后 256 字节 hex（对象表起点判读）
                sw.WriteLine("## 三、类型表之后 256 字节");
                int limit = data.Length - at;
                if (limit > 256)
                {
                    limit = 256;
                }
                for (int off = 0; off < limit; off = off + 32)
                {
                    int take = Math.Min(32, limit - off);
                    sw.WriteLine(HexLine(data, at + off, take, at + off));
                }
                sw.WriteLine();

                // [段4] 对象表试读（version 17：int64 pathID + uint32 byteStart + uint32 byteSize + int32 typeID = 20 字节）
                sw.WriteLine("## 四、对象表试读（20 字节/条）");
                int objCount = ReadInt32Little(data, at);
                sw.WriteLine("- 对象数: " + objCount.ToString());
                int op = at + 4;
                int show = objCount < 24 ? objCount : 24;
                for (int i = 0; i < show; i = i + 1)
                {
                    long pathId = ReadInt64Little(data, op);
                    op = op + 8;
                    int byteStart = ReadInt32Little(data, op);
                    op = op + 4;
                    int byteSize = ReadInt32Little(data, op);
                    op = op + 4;
                    int typeId = ReadInt32Little(data, op);
                    op = op + 4;
                    sw.WriteLine("  · #" + i.ToString() + " pathID " + pathId.ToString() + " · 偏移 " + byteStart.ToString("N0") + " · " + byteSize.ToString("N0") + " 字节 · typeID " + typeId.ToString());
                }
                sw.WriteLine("- 对象表试读结束于偏移: " + op.ToString("N0"));
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>SerializedFile 定位侦察——字符串表定位 + 类型表节点布局约束求解 + 节点树；只读。</summary>
        private static int SfScan(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            string loadErr;
            byte[] data = LoadBundleData(src, out loadErr);
            if (loadErr != null)
            {
                Console.Error.WriteLine("数据区读取失败: " + loadErr);
                return 2;
            }
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                // [段1] 头（前 4 字段大端，其后小端）
                int metadataSize = ReadInt32Big(data, 0);
                int fileSize = ReadInt32Big(data, 4);
                int sfVersion = ReadInt32Big(data, 8);
                int dataOffset = ReadInt32Big(data, 12);
                sw.WriteLine("# " + src);
                sw.WriteLine("- 元数据大小 " + metadataSize.ToString("N0") + " · 文件大小 " + fileSize.ToString("N0") + " · 序列化版本 " + sfVersion.ToString() + " · 数据偏移 " + dataOffset.ToString("N0"));
                int typeCount = ReadInt32Little(data, 33);
                sw.WriteLine("- 类型数 " + typeCount.ToString());
                sw.WriteLine();

                // [段2] 字符串表定位——尾部 size 字段自洽 + 首字节可读
                sw.WriteLine("## 一、字符串表定位");
                int strStart = -1;
                int strSize = 0;
                for (int s = metadataSize - 4; s >= 64; s = s - 1)
                {
                    int sz = ReadInt32Little(data, s - 4);
                    if (sz != metadataSize - s)
                    {
                        continue;
                    }
                    if (data[s] < 0x20 || data[s] > 0x7E)
                    {
                        continue;
                    }
                    strStart = s;
                    strSize = sz;
                    break;
                }
                sw.WriteLine("- 起点 " + strStart.ToString("N0") + " · 大小 " + strSize.ToString("N0"));
                if (strStart > 0)
                {
                    int q = strStart;
                    int end = strStart + strSize;
                    int idx = 0;
                    while (q < end && idx < 60)
                    {
                        int st = q;
                        while (q < end && data[q] != 0)
                        {
                            q = q + 1;
                        }
                        sw.WriteLine("  [" + (st - strStart).ToString() + "] " + Encoding.UTF8.GetString(data, st, q - st));
                        q = q + 1;
                        idx = idx + 1;
                    }
                }
                sw.WriteLine();

                // [段3] 类型 0 节点布局求解——候选起点 × 名字偏移落点自洽计分
                sw.WriteLine("## 二、类型 0 节点布局求解");
                int bestOff = -1;
                int bestScore = -1;
                int bestCount = 0;
                for (int off = 56; off <= 76; off = off + 1)
                {
                    int nc = ReadInt32Little(data, off - 4);
                    if (nc <= 0 || nc > 300)
                    {
                        continue;
                    }
                    int score = 0;
                    for (int k = 0; k < nc; k = k + 1)
                    {
                        int np = off + k * 24;
                        long nameOff = (long)(uint)ReadInt32Little(data, np + 8);
                        if (nameOff < strSize)
                        {
                            int abs = strStart + (int)nameOff;
                            if (abs > strStart && data[abs - 1] == 0)
                            {
                                score = score + 1;
                            }
                        }
                    }
                    sw.WriteLine("  · 起点 " + off.ToString() + "（nodeCount " + nc.ToString() + "）名字落点自洽 " + score.ToString() + "/" + nc.ToString());
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestOff = off;
                        bestCount = nc;
                    }
                }
                sw.WriteLine("- 最优: 起点 " + bestOff.ToString() + " · 节点 " + bestCount.ToString() + " · 自洽 " + bestScore.ToString());
                sw.WriteLine();

                // [段4] 最优布局下的节点树
                sw.WriteLine("## 三、节点树（最优布局）");
                if (bestOff > 0)
                {
                    for (int k = 0; k < bestCount; k = k + 1)
                    {
                        int np = bestOff + k * 24;
                        int lv = data[np + 2];
                        int tf = data[np + 3];
                        long to = (long)(uint)ReadInt32Little(data, np + 4);
                        long no = (long)(uint)ReadInt32Little(data, np + 8);
                        int bs = ReadInt32Little(data, np + 12);
                        int ix = ReadInt32Little(data, np + 16);
                        int mf = ReadInt32Little(data, np + 20);
                        string tn = TypeName(data, strStart, strSize, to);
                        string nn = TypeName(data, strStart, strSize, no);
                        StringBuilder pad = new StringBuilder();
                        for (int z = 0; z < lv; z = z + 1)
                        {
                            pad.Append("  ");
                        }
                        sw.WriteLine("  " + k.ToString() + " " + pad.ToString() + nn + " : " + tn + "  size " + bs.ToString() + " idx " + ix.ToString() + " tf " + tf.ToString() + " meta " + mf.ToString());
                    }
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>解析 TypeTree 字符串偏移——高位 0x80 为 CommonString 索引（本探针无表，输出索引号），否则取 SerializedFile 字符串表。</summary>
        private static string TypeName(byte[] data, int strStart, int strSize, long offset)
        {
            if ((offset & 0x80000000L) != 0)
            {
                return "#" + (offset & 0x7FFFFFFFL).ToString();
            }
            if (strStart <= 0 || offset >= strSize)
            {
                return "?";
            }
            int abs = strStart + (int)offset;
            if (abs >= data.Length)
            {
                return "?";
            }
            int q = abs;
            while (q < data.Length && data[q] != 0)
            {
                q = q + 1;
            }
            return Encoding.UTF8.GetString(data, abs, q - abs);
        }

        /// <summary>解析 SerializedFile 类型表——at 前移到类型表之后；失败返回 null 并给诊断。</summary>
        private static List<SfType> ParseSfTypes(byte[] data, int enableTypeTree, int typeCount, ref int at, out string error)
        {
            error = null;
            List<SfType> types = new List<SfType>();
            for (int i = 0; i < typeCount; i++)
            {
                if (at + 23 > data.Length)
                {
                    error = "类型 #" + i.ToString() + " 越界";
                    return null;
                }
                SfType t = new SfType();
                t.ClassID = ReadInt32Little(data, at);
                at = at + 4;
                at = at + 1;
                at = at + 2;
                if (t.ClassID == 114)
                {
                    at = at + 16;
                }
                at = at + 16;
                if (enableTypeTree != 0)
                {
                    t.NodeCount = ReadInt32Little(data, at);
                    at = at + 4;
                    t.StringBufferSize = ReadInt32Little(data, at);
                    at = at + 4;
                    if (t.NodeCount < 0 || t.NodeCount > 20000 || t.StringBufferSize < 0 || t.StringBufferSize > 1048576)
                    {
                        error = "类型 #" + i.ToString() + " 节点数 / 字段名池异常（" + t.NodeCount.ToString() + " / " + t.StringBufferSize.ToString() + "）";
                        return null;
                    }
                    for (int k = 0; k < t.NodeCount; k++)
                    {
                        SfNode n = new SfNode();
                        n.Version = ReadInt16Little(data, at);
                        n.Level = data[at + 2];
                        n.TypeFlags = data[at + 3];
                        n.TypeStrOffset = (long)(uint)ReadInt32Little(data, at + 4);
                        n.NameStrOffset = (long)(uint)ReadInt32Little(data, at + 8);
                        n.ByteSize = ReadInt32Little(data, at + 12);
                        n.Index = ReadInt32Little(data, at + 16);
                        n.MetaFlag = ReadInt32Little(data, at + 20);
                        at = at + 24;
                        t.Nodes.Add(n);
                    }
                    t.StringBuffer = new byte[t.StringBufferSize];
                    Array.Copy(data, at, t.StringBuffer, 0, t.StringBufferSize);
                    at = at + t.StringBufferSize;
                }
                types.Add(t);
            }
            return types;
        }

        /// <summary>解析 TypeTree 名字——高位 0x80 为 CommonString 索引（本探针无表，输出索引号），否则取本类型字段名池。</summary>
        private static string SfName(SfType t, long offset)
        {
            if ((offset & 0x80000000L) != 0)
            {
                return "#" + (offset & 0x7FFFFFFFL).ToString();
            }
            if (t.StringBuffer == null || offset >= t.StringBuffer.Length)
            {
                return "?";
            }
            int start = (int)offset;
            int q = start;
            while (q < t.StringBuffer.Length && t.StringBuffer[q] != 0)
            {
                q = q + 1;
            }
            return Encoding.UTF8.GetString(t.StringBuffer, start, q - start);
        }

        /// <summary>读 Unity 字符串（int32 长度 + 字节 + 4 字节对齐）。</summary>
        private static string ReadSfString(byte[] data, ref int at)
        {
            int len = ReadInt32Little(data, at);
            at = at + 4;
            if (len < 0 || len > 4096)
            {
                return "<长度异常 " + len.ToString() + ">";
            }
            string s = Encoding.UTF8.GetString(data, at, len);
            at = at + len;
            int pad = (4 - (len % 4)) % 4;
            at = at + pad;
            return s;
        }

        /// <summary>SerializedFile 对象清单侦察——类型表 / 对象表 / Texture2D 字段；只读。</summary>
        private static int SfObj(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            string loadErr;
            byte[] data = LoadBundleData(src, out loadErr);
            if (loadErr != null)
            {
                Console.Error.WriteLine("数据区读取失败: " + loadErr);
                return 2;
            }
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                int metadataSize = ReadInt32Big(data, 0);
                int dataOffset = ReadInt32Big(data, 12);
                int at = 20;
                string unityVer = ReadCString(data, ref at);
                int targetPlatform = ReadInt32Little(data, at);
                at = at + 4;
                int enableTypeTree = data[at];
                at = at + 1;
                int typeCount = ReadInt32Little(data, at);
                at = at + 4;
                sw.WriteLine("# " + src);
                sw.WriteLine("- Unity " + unityVer + " · 目标平台 " + targetPlatform.ToString() + " · 类型 " + typeCount.ToString()
                    + " · 元数据 " + metadataSize.ToString("N0") + " · 数据偏移 " + dataOffset.ToString("N0"));

                // [段1] 类型表（内联解析——失败时保留已解析部分与位置，便于校准）
                sw.WriteLine();
                sw.WriteLine("## 一、类型表（" + typeCount.ToString() + " 个）");
                List<SfType> types = new List<SfType>();
                for (int i = 0; i < typeCount; i++)
                {
                    int start = at;
                    SfType t = new SfType();
                    t.ClassID = ReadInt32Little(data, at);
                    at = at + 4;
                    at = at + 1;
                    at = at + 2;
                    if (t.ClassID == 114)
                    {
                        at = at + 16;
                    }
                    at = at + 16;
                    if (enableTypeTree != 0)
                    {
                        t.NodeCount = ReadInt32Little(data, at);
                        at = at + 4;
                        t.StringBufferSize = ReadInt32Little(data, at);
                        at = at + 4;
                        if (t.NodeCount < 0 || t.NodeCount > 20000 || t.StringBufferSize < 0 || t.StringBufferSize > 1048576)
                        {
                            sw.WriteLine("- #" + i.ToString() + " !! 异常：起点 " + start.ToString("N0") + " · classID " + t.ClassID.ToString()
                                + " · 节点数 " + t.NodeCount.ToString() + " · 字段名池 " + t.StringBufferSize.ToString());
                            return 2;
                        }
                        for (int k = 0; k < t.NodeCount; k++)
                        {
                            SfNode n = new SfNode();
                            n.Version = ReadInt16Little(data, at);
                            n.Level = data[at + 2];
                            n.TypeFlags = data[at + 3];
                            n.TypeStrOffset = (long)(uint)ReadInt32Little(data, at + 4);
                            n.NameStrOffset = (long)(uint)ReadInt32Little(data, at + 8);
                            n.ByteSize = ReadInt32Little(data, at + 12);
                            n.Index = ReadInt32Little(data, at + 16);
                            n.MetaFlag = ReadInt32Little(data, at + 20);
                            at = at + 24;
                            t.Nodes.Add(n);
                        }
                        t.StringBuffer = new byte[t.StringBufferSize];
                        Array.Copy(data, at, t.StringBuffer, 0, t.StringBufferSize);
                        at = at + t.StringBufferSize;
                    }
                    types.Add(t);
                    sw.WriteLine("- #" + i.ToString() + " classID " + t.ClassID.ToString() + " · 节点 " + t.NodeCount.ToString()
                        + " · 字段名池 " + t.StringBufferSize.ToString() + " · 起 " + start.ToString("N0") + " 止 " + at.ToString("N0"));
                }
                sw.WriteLine();
                sw.WriteLine("## 一之二、类型 #0（Texture2D）节点表");
                SfType t0 = types[0];
                for (int k = 0; k < t0.Nodes.Count; k++)
                {
                    SfNode n0 = t0.Nodes[k];
                    sw.WriteLine("  " + k.ToString() + " lv" + n0.Level.ToString() + " " + SfName(t0, n0.NameStrOffset)
                        + " · 类型 " + SfName(t0, n0.TypeStrOffset) + " · size " + n0.ByteSize.ToString()
                        + " idx " + n0.Index.ToString() + " meta 0x" + n0.MetaFlag.ToString("X"));
                }

                // [段2] 对象表（objectCount 之后按 4 字节对齐——Unity 写入侧行为）
                int objectCount = ReadInt32Little(data, at);
                at = at + 4;
                at = (at + 3) / 4 * 4;
                List<SfObject> objects = new List<SfObject>();
                for (int i = 0; i < objectCount; i++)
                {
                    SfObject o = new SfObject();
                    o.PathID = ReadInt64Little(data, at);
                    at = at + 8;
                    o.ByteStart = ReadInt32Little(data, at);
                    at = at + 4;
                    o.ByteSize = ReadInt32Little(data, at);
                    at = at + 4;
                    o.TypeID = ReadInt32Little(data, at);
                    at = at + 4;
                    objects.Add(o);
                }
                sw.WriteLine();
                sw.WriteLine("## 二、对象表（" + objectCount.ToString() + " 个 · 表结束于 " + at.ToString("N0") + "）");
                int dumpLimit = objects.Count < 6 ? objects.Count : 6;
                for (int i = 0; i < dumpLimit; i++)
                {
                    int rawAt = 55891 + i * 20;
                    StringBuilder hex = new StringBuilder();
                    for (int b = 0; b < 20; b = b + 1)
                    {
                        hex.Append(data[rawAt + b].ToString("X2")).Append(' ');
                    }
                    sw.WriteLine("  raw #" + i.ToString() + " @ " + rawAt.ToString("N0") + " : " + hex.ToString());
                }
                for (int i = 0; i < objects.Count; i++)
                {
                    SfObject o = objects[i];
                    string cn = "classID ?";
                    if (o.TypeID >= 0 && o.TypeID < types.Count)
                    {
                        cn = "classID " + types[o.TypeID].ClassID.ToString();
                    }
                    sw.WriteLine("- #" + i.ToString() + " pathID " + o.PathID.ToString() + " · " + cn + " · 数据 @ " + o.ByteStart.ToString("N0") + " · " + o.ByteSize.ToString("N0") + " 字节 · typeID " + o.TypeID.ToString());
                }

                // [段3] Texture2D 字段读取
                sw.WriteLine();
                sw.WriteLine("## 三、Texture2D 字段");
                for (int i = 0; i < objects.Count; i++)
                {
                    SfObject o = objects[i];
                    if (o.TypeID < 0 || o.TypeID >= types.Count)
                    {
                        continue;
                    }
                    SfType t = types[o.TypeID];
                    if (t.ClassID != 28)
                    {
                        continue;
                    }
                    sw.WriteLine("- 对象 #" + i.ToString() + " pathID " + o.PathID.ToString());
                    int op = dataOffset + o.ByteStart;
                    string texErr;
                    ReadTexture2D(data, ref op, t, sw, out texErr);
                    if (texErr != null)
                    {
                        sw.WriteLine("  !! " + texErr);
                    }
                }
            }

            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>读取一个 Texture2D 对象——按 TypeTree 节点顺序、以字段名驱动的简化读取。</summary>
        private static void ReadTexture2D(byte[] data, ref int at, SfType t, StreamWriter sw, out string error)
        {
            error = null;
            int width = 0;
            int height = 0;
            int format = 0;
            int dataSize = 0;
            int dataAt = 0;
            int k = 1;
            while (k < t.Nodes.Count)
            {
                SfNode n = t.Nodes[k];
                string name = SfName(t, n.NameStrOffset);
                int next = k + 1;
                while (next < t.Nodes.Count && t.Nodes[next].Level > n.Level)
                {
                    next = next + 1;
                }

                // [段1] 变长字段（string / 字节数组）：长度 + 数据 + 对齐，整段连子节点一起跳过
                if (name == "m_Name" || name == "image data")
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    if (name == "m_Name")
                    {
                        int safe = len;
                        if (safe < 0 || safe > 4096)
                        {
                            safe = 0;
                        }
                        sw.WriteLine("  · m_Name = \"" + Encoding.UTF8.GetString(data, at, safe) + "\"");
                    }
                    else
                    {
                        dataAt = at;
                        dataSize = len;
                        sw.WriteLine("  · image data 长度 = " + len.ToString("N0"));
                    }
                    if (len > 0 && len < 200000000)
                    {
                        at = at + len;
                    }
                    at = (at + 3) / 4 * 4;
                    k = next;
                    continue;
                }

                // [段2] 流式数据（结构体）：offset + size + path
                if (name == "m_StreamData")
                {
                    at = at + 4;
                    at = at + 4;
                    string p = ReadSfString(data, ref at);
                    sw.WriteLine("  · m_StreamData.path = \"" + p + "\"");
                    k = next;
                    continue;
                }

                // [段3] 已知简单字段
                if (name == "m_Width" || name == "m_Height" || name == "m_CompleteImageSize" || name == "m_TextureFormat"
                    || name == "m_MipCount" || name == "m_ImageCount" || name == "m_TextureDimension"
                    || name == "m_LightmapFormat" || name == "m_ColorSpace" || name == "m_ForcedFallbackFormat")
                {
                    int v = ReadInt32Little(data, at);
                    at = at + 4;
                    if (name == "m_Width")
                    {
                        width = v;
                    }
                    if (name == "m_Height")
                    {
                        height = v;
                    }
                    if (name == "m_TextureFormat")
                    {
                        format = v;
                    }
                    if (name == "m_CompleteImageSize")
                    {
                        dataSize = v;
                    }
                    sw.WriteLine("  · " + name + " = " + v.ToString());
                    k = k + 1;
                    continue;
                }
                if (name == "m_DownscaleFallback" || name == "m_IsReadable")
                {
                    int v = data[at];
                    at = at + 1;
                    at = (at + 3) / 4 * 4;
                    sw.WriteLine("  · " + name + " = " + v.ToString());
                    k = k + 1;
                    continue;
                }

                // [段4] 其余按结构规则跳过——变长整段跳；容器（有子节点）不占数据；基础类型按声明字节数跳
                if (n.ByteSize == -1)
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    if (len > 0 && len < 200000000)
                    {
                        at = at + len;
                    }
                    at = (at + 3) / 4 * 4;
                    k = next;
                    continue;
                }
                if (n.ByteSize > 0 && next > k + 1)
                {
                    k = k + 1;
                    continue;
                }
                if (n.ByteSize > 0)
                {
                    at = at + n.ByteSize;
                    k = k + 1;
                    continue;
                }
                k = k + 1;
            }
            sw.WriteLine("  · 合计：宽 " + width.ToString() + " × 高 " + height.ToString() + " · 格式 " + format.ToString()
                + " · 像素数据 " + dataSize.ToString("N0") + " 字节 @ " + dataAt.ToString("N0"));
        }

        /// <summary>读取 Texture2D 的关键字段（宽 / 高 / 格式 / 像素数据位置与长度 / 名称）——不输出报告。</summary>
        private static void ReadTexture2DInfo(byte[] data, ref int at, SfType t, ref int width, ref int height, ref int format,
            ref int texAt, ref int texLen, ref string name, out string error)
        {
            error = null;
            int k = 1;
            while (k < t.Nodes.Count)
            {
                SfNode n = t.Nodes[k];
                string fname = SfName(t, n.NameStrOffset);
                int next = k + 1;
                while (next < t.Nodes.Count && t.Nodes[next].Level > n.Level)
                {
                    next = next + 1;
                }
                if (fname == "m_Name")
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    int safe = len;
                    if (safe < 0 || safe > 4096)
                    {
                        safe = 0;
                    }
                    name = Encoding.UTF8.GetString(data, at, safe);
                    if (len > 0 && len < 200000000)
                    {
                        at = at + len;
                    }
                    at = (at + 3) / 4 * 4;
                    k = next;
                    continue;
                }
                if (fname == "image data")
                {
                    texLen = ReadInt32Little(data, at);
                    at = at + 4;
                    texAt = at;
                    if (texLen > 0 && texLen < 200000000)
                    {
                        at = at + texLen;
                    }
                    at = (at + 3) / 4 * 4;
                    k = next;
                    continue;
                }
                if (fname == "m_StreamData")
                {
                    at = at + 8;
                    ReadSfString(data, ref at);
                    k = next;
                    continue;
                }
                if (fname == "m_Width" || fname == "m_Height" || fname == "m_CompleteImageSize" || fname == "m_TextureFormat"
                    || fname == "m_MipCount" || fname == "m_ImageCount" || fname == "m_TextureDimension"
                    || fname == "m_LightmapFormat" || fname == "m_ColorSpace" || fname == "m_ForcedFallbackFormat")
                {
                    int v = ReadInt32Little(data, at);
                    at = at + 4;
                    if (fname == "m_Width")
                    {
                        width = v;
                    }
                    if (fname == "m_Height")
                    {
                        height = v;
                    }
                    if (fname == "m_TextureFormat")
                    {
                        format = v;
                    }
                    k = k + 1;
                    continue;
                }
                if (fname == "m_DownscaleFallback" || fname == "m_IsReadable")
                {
                    at = at + 1;
                    at = (at + 3) / 4 * 4;
                    k = k + 1;
                    continue;
                }
                if (n.ByteSize == -1)
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    if (len > 0 && len < 200000000)
                    {
                        at = at + len;
                    }
                    at = (at + 3) / 4 * 4;
                    k = next;
                    continue;
                }
                if (n.ByteSize > 0 && next > k + 1)
                {
                    k = k + 1;
                    continue;
                }
                if (n.ByteSize > 0)
                {
                    at = at + n.ByteSize;
                    k = k + 1;
                    continue;
                }
                k = k + 1;
            }
        }

        /// <summary>导出 bundle 内全部 Texture2D 为 PNG（第三参数为最多张数，0 = 全部）。</summary>
        private static int SfTex(string src, string outDir, int maxCount, int maxSide)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            string loadErr;
            byte[] data = LoadBundleData(src, out loadErr);
            if (loadErr != null)
            {
                Console.Error.WriteLine("数据区读取失败: " + loadErr);
                return 2;
            }
            Directory.CreateDirectory(outDir);
            int dataOffset = ReadInt32Big(data, 12);
            int at = 20;
            ReadCString(data, ref at);
            at = at + 4;
            int enableTypeTree = data[at];
            at = at + 1;
            int typeCount = ReadInt32Little(data, at);
            at = at + 4;
            string typeErr;
            List<SfType> types = ParseSfTypes(data, enableTypeTree, typeCount, ref at, out typeErr);
            if (typeErr != null)
            {
                Console.Error.WriteLine("类型表解析失败: " + typeErr);
                return 2;
            }
            int objectCount = ReadInt32Little(data, at);
            at = at + 4;
            at = (at + 3) / 4 * 4;
            List<SfObject> objects = new List<SfObject>();
            for (int i = 0; i < objectCount; i++)
            {
                SfObject o = new SfObject();
                o.PathID = ReadInt64Little(data, at);
                at = at + 8;
                o.ByteStart = ReadInt32Little(data, at);
                at = at + 4;
                o.ByteSize = ReadInt32Little(data, at);
                at = at + 4;
                o.TypeID = ReadInt32Little(data, at);
                at = at + 4;
                objects.Add(o);
            }

            // [段1] 逐对象导出 Texture2D
            int exported = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                SfObject o = objects[i];
                if (o.TypeID < 0 || o.TypeID >= types.Count || types[o.TypeID].ClassID != 28)
                {
                    continue;
                }
                if (maxCount > 0 && exported >= maxCount)
                {
                    break;
                }
                int op = dataOffset + o.ByteStart;
                int w = 0;
                int h = 0;
                int fmt = 0;
                int texAt = 0;
                int texLen = 0;
                string name = "";
                string readErr;
                ReadTexture2DInfo(data, ref op, types[o.TypeID], ref w, ref h, ref fmt, ref texAt, ref texLen, ref name, out readErr);
                if (readErr != null)
                {
                    Console.WriteLine("跳过 #" + i.ToString() + "：" + readErr);
                    continue;
                }
                string decErr;
                byte[] rgba = DecodeTexture(data, texAt, texLen, w, h, fmt, out decErr);
                if (decErr != null)
                {
                    Console.WriteLine("跳过 #" + i.ToString() + "（" + name + "）：" + decErr);
                    continue;
                }
                if (maxSide > 0 && (w > maxSide || h > maxSide))
                {
                    int nw = w;
                    int nh = h;
                    if (w >= h)
                    {
                        nh = h * maxSide / w;
                        nw = maxSide;
                    }
                    else
                    {
                        nw = w * maxSide / h;
                        nh = maxSide;
                    }
                    if (nw < 1)
                    {
                        nw = 1;
                    }
                    if (nh < 1)
                    {
                        nh = 1;
                    }
                    rgba = BoxDownscale(rgba, w, h, nw, nh);
                    w = nw;
                    h = nh;
                }
                string file = Path.Combine(outDir, "tex_" + o.PathID.ToString() + ".png");
                WritePng(file, w, h, rgba);
                exported = exported + 1;
                Console.WriteLine("导出 " + file + " · " + w.ToString() + "×" + h.ToString() + " · 格式 " + fmt.ToString() + " · " + name);
            }
            Console.WriteLine("共导出 " + exported.ToString() + " 张");
            return 0;
        }

        /// <summary>按 Unity 贴图格式解码为 RGBA32（支持 RGBA32 / RGB24 / BGRA32 / ARGB32 / RGB565 / DXT1 / DXT5）。</summary>
        private static byte[] DecodeTexture(byte[] data, int at, int len, int width, int height, int format, out string error)
        {
            error = null;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                error = "尺寸异常 " + width.ToString() + "×" + height.ToString();
                return null;
            }
            if (at <= 0 || at + len > data.Length)
            {
                error = "像素数据越界（@ " + at.ToString() + " · " + len.ToString() + " 字节）";
                return null;
            }
            byte[] src = new byte[len];
            Array.Copy(data, at, src, 0, len);
            if (format == 4)
            {
                return DecodeRgba32(src, width, height);
            }
            if (format == 3)
            {
                return DecodeRgb24(src, width, height);
            }
            if (format == 14)
            {
                return DecodeBgra32(src, width, height);
            }
            if (format == 5)
            {
                return DecodeArgb32(src, width, height);
            }
            if (format == 7)
            {
                return DecodeRgb565(src, width, height);
            }
            if (format == 10)
            {
                return DecodeDxt1(src, width, height);
            }
            if (format == 12)
            {
                return DecodeDxt5(src, width, height);
            }
            error = "暂不支持的贴图格式 " + format.ToString();
            return null;
        }

        /// <summary>RGBA32 直读。</summary>
        private static byte[] DecodeRgba32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int n = dst.Length;
            if (src.Length < n)
            {
                n = src.Length;
            }
            Array.Copy(src, dst, n);
            return dst;
        }

        /// <summary>RGB24 展开为 RGBA32。</summary>
        private static byte[] DecodeRgb24(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 3 + 2 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 3];
                dst[i * 4 + 1] = src[i * 3 + 1];
                dst[i * 4 + 2] = src[i * 3 + 2];
                dst[i * 4 + 3] = 255;
            }
            return dst;
        }

        /// <summary>BGRA32 换序为 RGBA32。</summary>
        private static byte[] DecodeBgra32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 4 + 3 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 4 + 2];
                dst[i * 4 + 1] = src[i * 4 + 1];
                dst[i * 4 + 2] = src[i * 4];
                dst[i * 4 + 3] = src[i * 4 + 3];
            }
            return dst;
        }

        /// <summary>ARGB32 换序为 RGBA32。</summary>
        private static byte[] DecodeArgb32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 4 + 3 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 4 + 1];
                dst[i * 4 + 1] = src[i * 4 + 2];
                dst[i * 4 + 2] = src[i * 4 + 3];
                dst[i * 4 + 3] = src[i * 4];
            }
            return dst;
        }

        /// <summary>RGB565 展开为 RGBA32。</summary>
        private static byte[] DecodeRgb565(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 2 + 1 >= src.Length)
                {
                    break;
                }
                int c = src[i * 2] | (src[i * 2 + 1] << 8);
                dst[i * 4] = (byte)(((c >> 11) & 0x1F) * 255 / 31);
                dst[i * 4 + 1] = (byte)(((c >> 5) & 0x3F) * 255 / 63);
                dst[i * 4 + 2] = (byte)((c & 0x1F) * 255 / 31);
                dst[i * 4 + 3] = 255;
            }
            return dst;
        }

        /// <summary>DXT1（BC1）解码为 RGBA32。</summary>
        private static byte[] DecodeDxt1(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int at = 0;
            for (int by = 0; by < bh; by = by + 1)
            {
                for (int bx = 0; bx < bw; bx = bx + 1)
                {
                    if (at + 8 > src.Length)
                    {
                        return dst;
                    }
                    int c0 = src[at] | (src[at + 1] << 8);
                    int c1 = src[at + 2] | (src[at + 3] << 8);
                    uint bits = (uint)(src[at + 4] | (src[at + 5] << 8) | (src[at + 6] << 16) | (src[at + 7] << 24));
                    at = at + 8;
                    int r0 = ((c0 >> 11) & 0x1F) * 255 / 31;
                    int g0 = ((c0 >> 5) & 0x3F) * 255 / 63;
                    int b0 = (c0 & 0x1F) * 255 / 31;
                    int r1 = ((c1 >> 11) & 0x1F) * 255 / 31;
                    int g1 = ((c1 >> 5) & 0x3F) * 255 / 63;
                    int b1 = (c1 & 0x1F) * 255 / 31;
                    for (int py = 0; py < 4; py = py + 1)
                    {
                        for (int px = 0; px < 4; px = px + 1)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height)
                            {
                                continue;
                            }
                            int code = (int)((bits >> (2 * (py * 4 + px))) & 3);
                            int r;
                            int g;
                            int b;
                            int a = 255;
                            if (code == 0)
                            {
                                r = r0;
                                g = g0;
                                b = b0;
                            }
                            else if (code == 1)
                            {
                                r = r1;
                                g = g1;
                                b = b1;
                            }
                            else if (code == 2)
                            {
                                r = (2 * r0 + r1) / 3;
                                g = (2 * g0 + g1) / 3;
                                b = (2 * b0 + b1) / 3;
                            }
                            else
                            {
                                if (c0 > c1)
                                {
                                    r = (r0 + 2 * r1) / 3;
                                    g = (g0 + 2 * g1) / 3;
                                    b = (b0 + 2 * b1) / 3;
                                }
                                else
                                {
                                    r = 0;
                                    g = 0;
                                    b = 0;
                                    a = 0;
                                }
                            }
                            int di = (y * width + x) * 4;
                            dst[di] = (byte)r;
                            dst[di + 1] = (byte)g;
                            dst[di + 2] = (byte)b;
                            dst[di + 3] = (byte)a;
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>DXT5（BC3）解码为 RGBA32。</summary>
        private static byte[] DecodeDxt5(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int at = 0;
            for (int by = 0; by < bh; by = by + 1)
            {
                for (int bx = 0; bx < bw; bx = bx + 1)
                {
                    if (at + 16 > src.Length)
                    {
                        return dst;
                    }
                    int a0 = src[at];
                    int a1 = src[at + 1];
                    int c0 = src[at + 8] | (src[at + 9] << 8);
                    int c1 = src[at + 10] | (src[at + 11] << 8);
                    uint bits = (uint)(src[at + 12] | (src[at + 13] << 8) | (src[at + 14] << 16) | (src[at + 15] << 24));
                    ulong abits = 0;
                    for (int i = 0; i < 6; i = i + 1)
                    {
                        abits = abits | ((ulong)src[at + 2 + i] << (8 * i));
                    }
                    at = at + 16;

                    int[] alpha = new int[8];
                    alpha[0] = a0;
                    alpha[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i < 7; i = i + 1)
                        {
                            alpha[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        }
                    }
                    else
                    {
                        for (int i = 1; i < 5; i = i + 1)
                        {
                            alpha[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                        }
                        alpha[6] = 0;
                        alpha[7] = 255;
                    }

                    int r0 = ((c0 >> 11) & 0x1F) * 255 / 31;
                    int g0 = ((c0 >> 5) & 0x3F) * 255 / 63;
                    int b0 = (c0 & 0x1F) * 255 / 31;
                    int r1 = ((c1 >> 11) & 0x1F) * 255 / 31;
                    int g1 = ((c1 >> 5) & 0x3F) * 255 / 63;
                    int b1 = (c1 & 0x1F) * 255 / 31;

                    for (int py = 0; py < 4; py = py + 1)
                    {
                        for (int px = 0; px < 4; px = px + 1)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height)
                            {
                                continue;
                            }
                            int idx = py * 4 + px;
                            int code = (int)((bits >> (2 * idx)) & 3);
                            int r;
                            int g;
                            int b;
                            if (code == 0)
                            {
                                r = r0;
                                g = g0;
                                b = b0;
                            }
                            else if (code == 1)
                            {
                                r = r1;
                                g = g1;
                                b = b1;
                            }
                            else if (code == 2)
                            {
                                r = (2 * r0 + r1) / 3;
                                g = (2 * g0 + g1) / 3;
                                b = (2 * b0 + b1) / 3;
                            }
                            else
                            {
                                r = (r0 + 2 * r1) / 3;
                                g = (g0 + 2 * g1) / 3;
                                b = (b0 + 2 * b1) / 3;
                            }
                            int acode = (int)((abits >> (3 * idx)) & 7);
                            int di = (y * width + x) * 4;
                            dst[di] = (byte)r;
                            dst[di + 1] = (byte)g;
                            dst[di + 2] = (byte)b;
                            dst[di + 3] = (byte)alpha[acode];
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>HTML 标签配平检查——统计各标签开闭数量并报告差异（交付自检项）。</summary>
        private static int ProbeHtml(string htmlPath, string outPath)
        {
            if (!File.Exists(htmlPath))
            {
                Console.Error.WriteLine("源文件不存在: " + htmlPath);
                return 2;
            }
            string html = File.ReadAllText(htmlPath, Encoding.UTF8);
            string[] tags = { "div", "span", "table", "tbody", "thead", "tr", "td", "button", "select", "label", "section", "header", "footer", "textarea" };
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                int bad = 0;
                foreach (string tag in tags)
                {
                    int open = CountOf(html, "<" + tag + " ") + CountOf(html, "<" + tag + ">");
                    int close = CountOf(html, "</" + tag + ">");
                    if (open == close)
                    {
                        sw.WriteLine("- " + tag + ": 开 " + open.ToString() + " / 闭 " + close.ToString() + "  OK");
                    }
                    else
                    {
                        bad = bad + 1;
                        sw.WriteLine("- " + tag + ": 开 " + open.ToString() + " / 闭 " + close.ToString() + "  !! 不配平");
                    }
                }
                sw.WriteLine(bad == 0 ? "配平检查通过" : "配平检查发现 " + bad.ToString() + " 处不配平");
            }
            Console.WriteLine("报告: " + outPath);
            return 0;
        }

        /// <summary>统计子串出现次数。</summary>
        private static int CountOf(string text, string needle)
        {
            int n = 0;
            int at = 0;
            while (true)
            {
                int i = text.IndexOf(needle, at, StringComparison.Ordinal);
                if (i < 0)
                {
                    break;
                }
                n = n + 1;
                at = i + needle.Length;
            }
            return n;
        }

        /// <summary>从 HTML 中抽出 script 块写到 .js 文件（供 node --check 语法校验）。</summary>
        private static int ExtractJs(string htmlPath, string outPath)
        {
            if (!File.Exists(htmlPath))
            {
                Console.Error.WriteLine("源文件不存在: " + htmlPath);
                return 2;
            }
            string html = File.ReadAllText(htmlPath, Encoding.UTF8);
            int start = html.IndexOf("<script>", StringComparison.Ordinal);
            if (start < 0)
            {
                Console.Error.WriteLine("没有找到 script 块");
                return 2;
            }
            start = start + 8;
            int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                Console.Error.WriteLine("没有找到 script 结束标签");
                return 2;
            }
            string js = html.Substring(start, end - start);
            File.WriteAllText(outPath, js, new UTF8Encoding(false));
            Console.WriteLine("已写出: " + outPath + "（" + js.Length.ToString("N0") + " 字符）");
            return 0;
        }

        /// <summary>等比缩小 RGBA32 位图（盒式平均——与产品侧 Downscale 同算法，供像素级对照）。</summary>
        private static byte[] BoxDownscale(byte[] src, int sw, int sh, int dw, int dh)
        {
            byte[] dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y = y + 1)
            {
                int sy0 = y * sh / dh;
                int sy1 = (y + 1) * sh / dh;
                if (sy1 <= sy0)
                {
                    sy1 = sy0 + 1;
                }
                for (int x = 0; x < dw; x = x + 1)
                {
                    int sx0 = x * sw / dw;
                    int sx1 = (x + 1) * sw / dw;
                    if (sx1 <= sx0)
                    {
                        sx1 = sx0 + 1;
                    }
                    long r = 0;
                    long g = 0;
                    long b = 0;
                    long a = 0;
                    int cnt = 0;
                    for (int sy = sy0; sy < sy1 && sy < sh; sy = sy + 1)
                    {
                        for (int sx = sx0; sx < sx1 && sx < sw; sx = sx + 1)
                        {
                            int si = (sy * sw + sx) * 4;
                            r = r + src[si];
                            g = g + src[si + 1];
                            b = b + src[si + 2];
                            a = a + src[si + 3];
                            cnt = cnt + 1;
                        }
                    }
                    if (cnt <= 0)
                    {
                        cnt = 1;
                    }
                    int di = (y * dw + x) * 4;
                    dst[di] = (byte)(r / cnt);
                    dst[di + 1] = (byte)(g / cnt);
                    dst[di + 2] = (byte)(b / cnt);
                    dst[di + 3] = (byte)(a / cnt);
                }
            }
            return dst;
        }

        /// <summary>写 PNG（8 位 RGBA）——签名 + IHDR + IDAT（zlib）+ IEND。</summary>
        private static void WritePng(string path, int width, int height, byte[] rgba)
        {
            using (FileStream fs = File.Create(path))
            {
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                byte[] ihdr = new byte[13];
                WriteInt32Big(ihdr, 0, width);
                WriteInt32Big(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(fs, "IHDR", ihdr);

                int stride = width * 4;
                byte[] raw = new byte[(stride + 1) * height];
                for (int y = 0; y < height; y = y + 1)
                {
                    raw[y * (stride + 1)] = 0;
                    Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
                }
                byte[] comp;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (System.IO.Compression.ZLibStream zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    {
                        zs.Write(raw, 0, raw.Length);
                    }
                    comp = ms.ToArray();
                }
                WriteChunk(fs, "IDAT", comp);
                WriteChunk(fs, "IEND", new byte[0]);
            }
        }
        /// <summary>把内存缓冲区的一段写成 hex+ascii 行（start 为缓冲区下标，输出行号即该下标）。</summary>
        private static void DumpBuffer(byte[] buf, StreamWriter sw, long start, int length)
        {
            for (int off = 0; off < length; off = off + 32)
            {
                int take = length - off;
                if (take > 32)
                {
                    take = 32;
                }
                sw.WriteLine(HexLine(buf, (int)start + off, take, 0));
            }
        }
        /// <summary>按 UTF-8 字节在文件里查找文本——命中点附前后上下文 hex，用于定位卡片数据区里的字符串字段。</summary>
        private static int FindText(string src, string outPath, string text, int before, int after)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            byte[] needle = Encoding.UTF8.GetBytes(text);
            if (needle.Length == 0)
            {
                Console.Error.WriteLine("搜索文本为空");
                return 2;
            }
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            byte[] all = File.ReadAllBytes(src);
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 源文件: " + src);
                sw.WriteLine("# 大小: " + all.Length.ToString("N0") + " 字节");
                sw.WriteLine("# 搜索文本: \"" + text + "\"（UTF-8 " + needle.Length + " 字节）");
                sw.WriteLine();
                long hits = 0;
                for (int i = 0; i <= all.Length - needle.Length; i = i + 1)
                {
                    if (all[i] != needle[0] || !MatchAt(all, i, needle))
                    {
                        continue;
                    }
                    hits = hits + 1;
                    sw.WriteLine("## 命中 #" + hits + "  @" + i.ToString("N0"));
                    long from = i - before;
                    if (from < 0)
                    {
                        from = 0;
                    }
                    long to = i + needle.Length + after;
                    if (to > all.Length)
                    {
                        to = all.Length;
                    }
                    DumpBuffer(all, sw, from, (int)(to - from));
                    sw.WriteLine();
                }
                sw.WriteLine("# 命中总数: " + hits);
            }
            Console.WriteLine("find: " + outPath);
            return 0;
        }
        /// <summary>复制文件——试验夹具用（把受控根外的样本卡搬进 CatTemp 做写面实测）。</summary>
        private static int CopyFile(string src, string dest)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("源文件不存在: " + src);
                return 2;
            }
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.Copy(src, dest, true);
            Console.WriteLine("copy: " + dest);
            return 0;
        }
        /// <summary>比较两个文件的指定区间（写面实测用——验证「只改了该改的」）：cmp &lt;a&gt; &lt;b&gt; &lt;offA&gt; &lt;offB&gt; &lt;len&gt;</summary>
        private static int CompareRange(string a, string b, long offA, long offB, long len)
        {
            if (!File.Exists(a) || !File.Exists(b))
            {
                Console.Error.WriteLine("文件不存在：" + (File.Exists(a) ? b : a));
                return 2;
            }
            using (FileStream fa = File.OpenRead(a))
            using (FileStream fb = File.OpenRead(b))
            {
                if (offA + len > fa.Length || offB + len > fb.Length)
                {
                    Console.Error.WriteLine("区间越界：A 长 " + fa.Length + " · B 长 " + fb.Length + " · 要比较 " + len + " 字节");
                    return 2;
                }
                fa.Position = offA;
                fb.Position = offB;
                byte[] ba = new byte[1 << 20];
                byte[] bb = new byte[1 << 20];
                long left = len;
                long diff = 0;
                long first = -1;
                while (left > 0)
                {
                    int take = left < ba.Length ? (int)left : ba.Length;
                    int na = ReadFull(fa, ba, take);
                    int nb = ReadFull(fb, bb, take);
                    if (na != take || nb != take)
                    {
                        Console.Error.WriteLine("读取不足：A " + na + " / B " + nb + "（要 " + take + "）");
                        return 3;
                    }
                    for (int i = 0; i < take; i = i + 1)
                    {
                        if (ba[i] != bb[i])
                        {
                            if (first < 0)
                            {
                                first = (len - left) + i;
                            }
                            diff = diff + 1;
                        }
                    }
                    left = left - take;
                }
                if (diff == 0)
                {
                    Console.WriteLine("相同：" + len.ToString("N0") + " 字节逐字节一致");
                    return 0;
                }
                Console.WriteLine("差异：" + diff.ToString("N0") + " 字节不同 · 首个差异 @" + first.ToString("N0"));
                return 1;
            }
        }

        /// <summary>
        /// Studio 场景卡 timeline 长度侦察——tlinfo &lt;file.png|目录&gt; &lt;out.txt&gt; [最大文件 MB · 0=不限]。
        /// 数据区（IEND 之后）流式扫描 Timeline 插件条目锚点（MessagePack 键 fixstr8 "timeline"），
        /// 取该条目内的 XML 根，读 duration 属性（秒）。
        /// </summary>
        private static int TlInfo(string src, string outPath, int maxMb)
        {
            bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src))
            {
                Console.Error.WriteLine("路径不存在: " + src);
                return 2;
            }
            string parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            var files = new List<string>();
            if (isDir)
            {
                string[] found = Directory.GetFiles(src, "*.png", SearchOption.TopDirectoryOnly);
                Array.Sort(found, StringComparer.Ordinal);
                for (int i = 0; i < found.Length; i = i + 1)
                {
                    files.Add(found[i]);
                }
            }
            else
            {
                files.Add(src);
            }

            int withTimeline = 0;
            int withoutTimeline = 0;
            int skipped = 0;
            int failed = 0;

            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# timeline 长度侦察 —— " + src);
                sw.WriteLine("# 文件上限: " + (maxMb > 0 ? maxMb.ToString("N0") + " MB" : "不限"));
                sw.WriteLine();
                for (int i = 0; i < files.Count; i = i + 1)
                {
                    var fi = new FileInfo(files[i]);
                    if (maxMb > 0 && fi.Length > (long)maxMb * 1024L * 1024L)
                    {
                        skipped = skipped + 1;
                        sw.WriteLine("## " + fi.Name);
                        sw.WriteLine("- 跳过：超出文件上限（" + fi.Length.ToString("N0") + " 字节）");
                        sw.WriteLine();
                        continue;
                    }
                    try
                    {
                        if (TlProbeOne(files[i], fi, sw))
                        {
                            withTimeline = withTimeline + 1;
                        }
                        else
                        {
                            withoutTimeline = withoutTimeline + 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed = failed + 1;
                        sw.WriteLine("## " + fi.Name);
                        sw.WriteLine("- 异常: " + ex.GetType().Name + " · " + ex.Message);
                        sw.WriteLine();
                    }
                }
                sw.WriteLine("## 汇总");
                sw.WriteLine("- 文件 " + files.Count.ToString("N0") + " 个：有 timeline " + withTimeline.ToString("N0") + " · 无 timeline " + withoutTimeline.ToString("N0") + " · 跳过 " + skipped.ToString("N0") + " · 异常 " + failed.ToString("N0"));
            }
            Console.WriteLine("tlinfo: " + outPath);
            return 0;
        }

        /// <summary>单文件 timeline 侦察——命中 timeline 条目返回 true；明细写入报告。</summary>
        private static bool TlProbeOne(string file, FileInfo fi, StreamWriter sw)
        {
            byte[] keyNeedle = { 0xA8, 0x74, 0x69, 0x6D, 0x65, 0x6C, 0x69, 0x6E, 0x65 };
            byte[] durNeedle = Encoding.ASCII.GetBytes("duration=\"");
            byte[] kfNeedle = Encoding.ASCII.GetBytes("<keyframe ");
            byte[] timeNeedle = Encoding.ASCII.GetBytes("time=\"");

            sw.WriteLine("## " + fi.Name);
            sw.WriteLine("- 大小: " + fi.Length.ToString("N0") + " 字节");

            long dataStart;
            long scanStart;
            string head1 = null;
            string head2 = null;
            bool isScene = false;
            var hits = new List<long>();
            int durSeen = 0;

            using (var fs = File.OpenRead(file))
            {
                dataStart = ProbeFindPngEnd(fs, fi.Length);
                if (dataStart < 0)
                {
                    sw.WriteLine("- 未找到 IEND——非卡片 PNG");
                    sw.WriteLine();
                    return false;
                }
                fs.Position = dataStart;
                var br = new BinaryReader(fs);
                string first = Read7BitString(br);
                if (first != null && IsVersionLike(first))
                {
                    isScene = true;
                    head1 = first;
                    head2 = null;
                }
                else
                {
                    fs.Position = dataStart;
                    br = new BinaryReader(fs);
                    br.ReadInt32();
                    head1 = Read7BitString(br);
                    if (fs.Length - fs.Position >= 1)
                    {
                        head2 = Read7BitString(br);
                    }
                }
                scanStart = fs.Position;
                sw.WriteLine("- 数据区起点 " + dataStart.ToString("N0") + " · 数据区 " + (fi.Length - dataStart).ToString("N0") + " 字节 · 扫描起点 " + scanStart.ToString("N0"));
                if (isScene)
                {
                    sw.WriteLine("- 类型判定 场景卡（数据区首个 7bit 串是版本号 " + head1 + "，不是 int32 标记 + 卡类型）");
                }
                else
                {
                    sw.WriteLine("- 类型判定 卡片（int32 标记 + 卡类型）· 卡类型 " + (head1 == null ? "(未读到)" : head1) + " · 数据版本 " + (head2 == null ? "(未读到)" : head2));
                }

                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 128];
                long remain = fi.Length - scanStart;
                long consumed = scanStart;
                int carry = 0;
                while (remain > 0)
                {
                    int want = (int)Math.Min(BufSize, remain);
                    int read = fs.Read(buf, carry, want);
                    if (read <= 0)
                    {
                        break;
                    }
                    remain = remain - read;
                    long baseOff = consumed - carry;
                    consumed = consumed + read;
                    int total = carry + read;
                    for (int i = 0; i <= total - keyNeedle.Length; i = i + 1)
                    {
                        if (buf[i] == keyNeedle[0] && MatchAt(buf, i, keyNeedle))
                        {
                            long at = baseOff + i;
                            if (hits.Count == 0 || at > hits[hits.Count - 1] + 9)
                            {
                                hits.Add(at);
                            }
                        }
                    }
                    for (int i = 0; i <= total - durNeedle.Length; i = i + 1)
                    {
                        if (buf[i] == durNeedle[0] && MatchAt(buf, i, durNeedle))
                        {
                            durSeen = durSeen + 1;
                            i = i + durNeedle.Length;
                        }
                    }
                    int newCarry = Math.Min(64, total);
                    Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                    carry = newCarry;
                }
            }

            sw.WriteLine("- timeline 键命中 " + hits.Count.ToString("N0") + " 处 · 数据区 duration=\" 出现 " + durSeen.ToString("N0") + " 次");
            for (int h = 0; h < hits.Count; h = h + 1)
            {
                long at = hits[h];
                byte[] head = new byte[4096];
                int n;
                using (var fs2 = File.OpenRead(file))
                {
                    fs2.Position = at;
                    n = ReadFull(fs2, head, head.Length);
                }
                string shape = "结构未识别";
                int xmlLen = -1;
                long xmlAt = at + 27;
                if (n > 24 && head[9] == 0x92 && head[11] == 0x81 && head[12] == 0xA9 && Encoding.ASCII.GetString(head, 13, 9) == "sceneInfo")
                {
                    byte hb = head[22];
                    if (hb == 0xD9)
                    {
                        xmlLen = head[23];
                        xmlAt = at + 25;
                    }
                    else if (hb == 0xDA)
                    {
                        xmlLen = (head[23] << 8) | head[24];
                        xmlAt = at + 26;
                    }
                    else if (hb == 0xDB)
                    {
                        xmlLen = (head[23] << 24) | (head[24] << 16) | (head[25] << 8) | head[26];
                        xmlAt = at + 27;
                    }
                    shape = "条目版本字节 " + head[10].ToString("X2") + " · XML 字符串头 " + hb.ToString("X2");
                }
                sw.WriteLine("  · #" + (h + 1) + " @" + at.ToString("N0") + "（距文件尾 " + (fi.Length - at).ToString("N0") + " 字节 · " + shape + "）");
                if (xmlAt + 200 <= at + n)
                {
                    string xmlHead = Encoding.UTF8.GetString(head, (int)(xmlAt - at), 200);
                    xmlHead = xmlHead.Replace("\r", " ").Replace("\n", " ");
                    sw.WriteLine("     XML 头: " + xmlHead);
                }
                if (xmlLen > 0)
                {
                    sw.WriteLine("     XML 长度 " + xmlLen.ToString("N0") + " 字节");
                    DumpTimelineXml(file, xmlAt, xmlLen, durNeedle, kfNeedle, timeNeedle, sw);
                }
                else
                {
                    sw.WriteLine("     => XML 长度不可读（结构未识别），未取 duration");
                }
            }
            sw.WriteLine();
            return hits.Count > 0;
        }

        /// <summary>读 Timeline 的 sceneInfo XML（上限 4 MB），统计 duration / 关键帧数 / 最大关键帧时间。</summary>
        private static void DumpTimelineXml(string file, long xmlAt, int xmlLen, byte[] durNeedle, byte[] kfNeedle, byte[] timeNeedle, StreamWriter sw)
        {
            int take = xmlLen;
            if (take > 4 * 1024 * 1024)
            {
                take = 4 * 1024 * 1024;
                sw.WriteLine("     （XML 超过 4 MB——只读前 4 MB）");
            }
            byte[] xml = new byte[take];
            int n;
            using (var fs = File.OpenRead(file))
            {
                fs.Position = xmlAt;
                n = ReadFull(fs, xml, take);
            }
            string durationText = ReadQuotedNumber(xml, n, durNeedle, true);
            int keyframes = 0;
            double maxTime = -1;
            for (int i = 0; i <= n - kfNeedle.Length; i = i + 1)
            {
                if (xml[i] == kfNeedle[0] && MatchAt(xml, i, kfNeedle))
                {
                    keyframes = keyframes + 1;
                }
            }
            for (int i = 0; i <= n - timeNeedle.Length; i = i + 1)
            {
                if (xml[i] == timeNeedle[0] && MatchAt(xml, i, timeNeedle))
                {
                    double v = ReadNumber(xml, i + timeNeedle.Length, n);
                    if (v > maxTime)
                    {
                        maxTime = v;
                    }
                }
            }
            string rootTag = FirstRootTag(xml, n);
            bool emptyRoot = false;
            if (rootTag != null && rootTag.EndsWith("/>", StringComparison.Ordinal))
            {
                emptyRoot = true;
            }
            string line = "     => ";
            if (durationText == null)
            {
                line = line + "未读到 duration 属性";
            }
            else
            {
                line = line + "duration = " + durationText + " 秒 → " + FormatSeconds(durationText);
            }
            line = line + " · 关键帧 " + keyframes.ToString("N0") + " 个";
            if (maxTime >= 0)
            {
                line = line + " · 最大关键帧时间 " + maxTime.ToString("0.###") + " 秒";
            }
            if (emptyRoot)
            {
                line = line + " · 空时间轴（root 自闭合、无关键帧内容）";
            }
            if (rootTag != null)
            {
                line = line + " · root 标签: " + rootTag;
            }
            sw.WriteLine(line);
        }

        /// <summary>取 XML 里第一个 root 标签（从 &lt;root 到最近的 &gt;）。</summary>
        private static string FirstRootTag(byte[] xml, int n)
        {
            byte[] needle = Encoding.ASCII.GetBytes("<root");
            for (int i = 0; i <= n - needle.Length; i = i + 1)
            {
                if (xml[i] == needle[0] && MatchAt(xml, i, needle))
                {
                    int end = i;
                    while (end < n && xml[end] != 0x3E)
                    {
                        end = end + 1;
                    }
                    if (end >= n)
                    {
                        return null;
                    }
                    return Encoding.UTF8.GetString(xml, i, end - i + 1);
                }
            }
            return null;
        }

        /// <summary>在字节缓冲里找 needle，随后读带引号的数值文本。</summary>
        private static string ReadQuotedNumber(byte[] buf, int n, byte[] needle, bool allowMinus)
        {
            for (int i = 0; i <= n - needle.Length; i = i + 1)
            {
                if (buf[i] != needle[0] || !MatchAt(buf, i, needle))
                {
                    continue;
                }
                var sb = new StringBuilder();
                int p = i + needle.Length;
                while (p < n)
                {
                    byte b = buf[p];
                    bool ok = (b >= 0x30 && b <= 0x39) || b == 0x2E || (allowMinus && b == 0x2D);
                    if (!ok)
                    {
                        break;
                    }
                    sb.Append((char)b);
                    p = p + 1;
                }
                return sb.ToString();
            }
            return null;
        }

        /// <summary>从缓冲指定位置读一个十进制数字（含小数点），无数字返回 -1。</summary>
        private static double ReadNumber(byte[] buf, int at, int n)
        {
            int p = at;
            double value = 0;
            bool frac = false;
            double scale = 0.1;
            int digits = 0;
            while (p < n)
            {
                byte b = buf[p];
                if (b == 0x2E)
                {
                    frac = true;
                    p = p + 1;
                    continue;
                }
                if (b < 0x30 || b > 0x39)
                {
                    break;
                }
                double d = b - 0x30;
                if (!frac)
                {
                    value = value * 10 + d;
                }
                else
                {
                    value = value + d * scale;
                    scale = scale / 10;
                }
                digits = digits + 1;
                p = p + 1;
            }
            if (digits == 0)
            {
                return -1;
            }
            return value;
        }

        /// <summary>判断字符串是否是「版本号」形态（只含数字与点，且至少一个点）。</summary>
        private static bool IsVersionLike(string s)
        {
            if (s == null || s.Length == 0 || s.Length > 16)
            {
                return false;
            }
            bool dot = false;
            for (int i = 0; i < s.Length; i = i + 1)
            {
                char c = s[i];
                if (c == '.')
                {
                    dot = true;
                    continue;
                }
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return dot;
        }

        /// <summary>定位 PNG 的 IEND 结束偏移（图片区终点）；未找到返回 -1。</summary>
        private static long ProbeFindPngEnd(FileStream fs, long len)
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

        /// <summary>读 7-bit 长度前缀的 UTF-8 字符串（BinaryWriter.Write(string) 形态）。</summary>
        private static string Read7BitString(BinaryReader br)
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

        /// <summary>秒数文本 → mm:ss.00 显示形态。</summary>
        private static string FormatSeconds(string secondsText)
        {
            int seconds;
            if (!int.TryParse(secondsText, out seconds))
            {
                return "(非整数秒)";
            }
            int minutes = seconds / 60;
            int rest = seconds % 60;
            return minutes.ToString("00") + ":" + rest.ToString("00") + ".00";
        }

        /// <summary>MessagePack 结构 dump——把文件指定区间按 MessagePack 递归展开成文本（只读侦察用）。</summary>
        private static int MpDump(string src, string outPath, long start, long len, int maxDepth)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("文件不存在: " + src);
                return 2;
            }
            long fileLen = new FileInfo(src).Length;
            if (start < 0 || start >= fileLen)
            {
                Console.Error.WriteLine("起点超出文件范围: " + start);
                return 2;
            }
            long take = len <= 0 ? fileLen - start : len;
            if (take > 4L * 1024L * 1024L)
            {
                take = 4L * 1024L * 1024L;
            }
            byte[] buf = new byte[(int)take];
            int n;
            using (var fs = File.OpenRead(src))
            {
                fs.Position = start;
                n = ReadFull(fs, buf, buf.Length);
            }
            string parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# MessagePack dump —— " + src);
                sw.WriteLine("# 区间 @" + start.ToString("N0") + " + " + n.ToString("N0") + " 字节 · 最大深度 " + maxDepth);
                sw.WriteLine();
                int pos = 0;
                MpDumpValue(buf, ref pos, n, 0, maxDepth, sw, "", start);
                sw.WriteLine();
                sw.WriteLine("# 解析结束于 @" + (start + pos).ToString("N0") + " · 区间内剩余 " + (n - pos).ToString("N0") + " 字节");
            }
            Console.WriteLine("mp: " + outPath);
            return 0;
        }

        /// <summary>
        /// 场景卡插件数据侦察——数据区里的插件条目清单（键名 + 值形态 + 长度），
        /// 另统计内嵌角色数据份数（lstInfo 命中）与 timeline 深度（关键帧 / 最大时刻 / 插值组数）。
        /// </summary>
        private static int SdItems(string src, string outPath)
        {
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("文件不存在: " + src);
                return 2;
            }
            long len = new FileInfo(src).Length;
            long dataStart;
            string version = null;
            byte[] sceneInfoNeedle = { 0xA9, 0x73, 0x63, 0x65, 0x6E, 0x65, 0x49, 0x6E, 0x66, 0x6F };
            byte[] mainNeedle = { 0xA4, 0x6D, 0x61, 0x69, 0x6E };
            byte[] lstNeedle = { 0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F };
            byte[] keyframeNeedle = Encoding.ASCII.GetBytes("<keyframe ");
            byte[] groupNeedle = Encoding.ASCII.GetBytes("<interpolableGroup ");
            byte[] timeNeedle = Encoding.ASCII.GetBytes("time=\"");

            using (var fs = File.OpenRead(src))
            {
                dataStart = ProbeFindPngEnd(fs, len);
                if (dataStart < 0)
                {
                    Console.Error.WriteLine("未找到 IEND——非卡片 PNG");
                    return 2;
                }
                fs.Position = dataStart;
                var br = new BinaryReader(fs);
                string first = Read7BitString(br);
                if (first == null || !IsVersionLike(first))
                {
                    Console.Error.WriteLine("不是场景卡（数据区首段不是版本号）");
                    return 2;
                }
                version = first;
            }

            var seen = new HashSet<long>();
            var names = new List<string>();
            var shapes = new List<string>();
            var offs = new List<long>();
            var sizes = new List<long>();
            int lstCount = 0;
            long timelineXmlAt = 0;
            int timelineXmlLen = 0;

            using (var fs = File.OpenRead(src))
            {
                const int BufSize = 1 << 20;
                byte[] buf = new byte[BufSize + 256];
                long pos = dataStart;
                long consumed = dataStart;
                int carry = 0;
                while (pos < len)
                {
                    long want = len - pos;
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

                    for (int i = 0; i <= total - lstNeedle.Length; i = i + 1)
                    {
                        if (buf[i] == lstNeedle[0] && MatchAt(buf, i, lstNeedle))
                        {
                            lstCount = lstCount + 1;
                        }
                    }

                    for (int i = 64; i <= total - 96; i = i + 1)
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
                        string name = ReadKeyBack(buf, i);
                        if (name == null)
                        {
                            continue;
                        }
                        int shapeStart = i + 3;
                        string shape = null;
                        long payloadAt = 0;
                        long payloadLen = 0;
                        if (MatchAt(buf, shapeStart, sceneInfoNeedle))
                        {
                            shape = "sceneInfo(XML)";
                            int sh = shapeStart + sceneInfoNeedle.Length;
                            byte hb = buf[sh];
                            if (hb == 0xD9)
                            {
                                payloadLen = buf[sh + 1];
                                payloadAt = at + (sh + 2 - i);
                            }
                            else if (hb == 0xDA)
                            {
                                payloadLen = (buf[sh + 1] << 8) | buf[sh + 2];
                                payloadAt = at + (sh + 3 - i);
                            }
                            else if (hb == 0xDB)
                            {
                                payloadLen = ((long)buf[sh + 1] << 24) | ((long)buf[sh + 2] << 16) | ((long)buf[sh + 3] << 8) | buf[sh + 4];
                                payloadAt = at + (sh + 5 - i);
                            }
                            else
                            {
                                shape = "sceneInfo(未识别头 0x" + hb.ToString("X2") + ")";
                            }
                        }
                        else if (MatchAt(buf, shapeStart, mainNeedle))
                        {
                            int sh = shapeStart + mainNeedle.Length;
                            byte hb = buf[sh];
                            if (hb >= 0xA0 && hb <= 0xBF)
                            {
                                shape = "main(短串)";
                                payloadLen = hb - 0xA0;
                            }
                            else if (hb == 0xD9)
                            {
                                shape = "main(JSON)";
                                payloadLen = buf[sh + 1];
                            }
                            else if (hb == 0xDA)
                            {
                                shape = "main(JSON)";
                                payloadLen = (buf[sh + 1] << 8) | buf[sh + 2];
                            }
                            else if (hb == 0xDB)
                            {
                                shape = "main(JSON)";
                                payloadLen = ((long)buf[sh + 1] << 24) | ((long)buf[sh + 2] << 16) | ((long)buf[sh + 3] << 8) | buf[sh + 4];
                            }
                            else
                            {
                                shape = "main(其它 0x" + hb.ToString("X2") + ")";
                            }
                        }
                        if (shape == null)
                        {
                            continue;
                        }
                        offs.Add(at);
                        names.Add(name);
                        shapes.Add(shape);
                        sizes.Add(payloadLen);
                        if (name == "timeline" && timelineXmlLen == 0)
                        {
                            timelineXmlAt = payloadAt;
                            timelineXmlLen = (int)payloadLen;
                        }
                    }

                    int newCarry = total < 64 ? total : 64;
                    Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                    carry = newCarry;
                }
            }

            int keys = 0;
            int groups = 0;
            double maxTime = 0;
            if (timelineXmlLen > 0 && timelineXmlLen <= 16 * 1024 * 1024)
            {
                byte[] xml = new byte[timelineXmlLen];
                int n;
                using (var fs = File.OpenRead(src))
                {
                    fs.Position = timelineXmlAt;
                    n = ReadFull(fs, xml, xml.Length);
                }
                for (int i = 0; i <= n - keyframeNeedle.Length; i = i + 1)
                {
                    if (xml[i] == keyframeNeedle[0] && MatchAt(xml, i, keyframeNeedle))
                    {
                        keys = keys + 1;
                        i = i + keyframeNeedle.Length;
                    }
                }
                for (int i = 0; i <= n - groupNeedle.Length; i = i + 1)
                {
                    if (xml[i] == groupNeedle[0] && MatchAt(xml, i, groupNeedle))
                    {
                        groups = groups + 1;
                        i = i + groupNeedle.Length;
                    }
                }
                for (int i = 0; i <= n - timeNeedle.Length; i = i + 1)
                {
                    if (xml[i] != timeNeedle[0] || !MatchAt(xml, i, timeNeedle))
                    {
                        continue;
                    }
                    var sb = new StringBuilder();
                    int p = i + timeNeedle.Length;
                    while (p < n)
                    {
                        byte b = xml[p];
                        if (!((b >= 0x30 && b <= 0x39) || b == 0x2E))
                        {
                            break;
                        }
                        sb.Append((char)b);
                        p = p + 1;
                    }
                    double v;
                    if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > maxTime)
                    {
                        maxTime = v;
                    }
                }
            }

            string parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 场景卡插件数据侦察 —— " + src);
                sw.WriteLine("# 大小 " + len.ToString("N0") + " 字节 · 数据区起点 " + dataStart.ToString("N0") + " · 场景版本 " + version);
                sw.WriteLine();
                sw.WriteLine("## 插件数据条目（" + offs.Count.ToString("N0") + " 条）");
                for (int i = 0; i < offs.Count; i = i + 1)
                {
                    sw.WriteLine("- @" + offs[i].ToString("N0") + "  " + names[i] + "  " + shapes[i] + "  " + (sizes[i] > 0 ? (sizes[i].ToString("N0") + " 字节") : "长度未解析"));
                }
                sw.WriteLine();
                sw.WriteLine("## 内嵌角色数据（lstInfo 命中）: " + lstCount.ToString("N0") + " 份");
                sw.WriteLine("## timeline 深度: 关键帧 " + keys.ToString("N0") + " · 插值组 " + groups.ToString("N0")
                    + " · 最大关键帧时刻 " + maxTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " 秒 · XML " + timelineXmlLen.ToString("N0") + " 字节");
            }
            Console.WriteLine("sditems: " + outPath);
            return 0;
        }

        /// <summary>从 0x92 位置往前回溯相邻的 fixstr 键名；不是 ASCII 名字返回 null。</summary>
        private static string ReadKeyBack(byte[] buf, int at)
        {
            for (int n = 1; n <= 31; n = n + 1)
            {
                int hp = at - 1 - n;
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
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
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

        /// <summary>递归 dump 一个 MessagePack 值（prefix 为行首前缀，用于 map 键）。</summary>
        private static void MpDumpValue(byte[] buf, ref int pos, int end, int depth, int maxDepth, StreamWriter sw, string prefix, long baseOff)
        {
            string indent = new string(' ', depth * 2);
            if (pos >= end)
            {
                sw.WriteLine(indent + prefix + "<越界>");
                return;
            }
            int at = pos;
            byte b = buf[pos];
            if (b <= 0x7F)
            {
                pos = pos + 1;
                sw.WriteLine(indent + prefix + b + "(@" + (baseOff + at) + ")");
                return;
            }
            if (b >= 0xE0)
            {
                pos = pos + 1;
                sw.WriteLine(indent + prefix + (b - 256) + "(@" + (baseOff + at) + ")");
                return;
            }
            if (b >= 0x80 && b <= 0x8F)
            {
                pos = pos + 1;
                MpDumpMap(buf, ref pos, end, b - 0x80, depth, maxDepth, sw, prefix, baseOff, at);
                return;
            }
            if (b >= 0x90 && b <= 0x9F)
            {
                pos = pos + 1;
                MpDumpArray(buf, ref pos, end, b - 0x90, depth, maxDepth, sw, prefix, baseOff, at);
                return;
            }
            if (b >= 0xA0 && b <= 0xBF)
            {
                pos = pos + 1;
                string s = MpReadStrBody(buf, ref pos, end, b - 0xA0);
                sw.WriteLine(indent + prefix + "\"" + s + "\"(@" + (baseOff + at) + " len " + (b - 0xA0) + ")");
                return;
            }
            switch (b)
            {
                case 0xC0:
                    pos = pos + 1;
                    sw.WriteLine(indent + prefix + "nil(@" + (baseOff + at) + ")");
                    return;
                case 0xC2:
                    pos = pos + 1;
                    sw.WriteLine(indent + prefix + "false(@" + (baseOff + at) + ")");
                    return;
                case 0xC3:
                    pos = pos + 1;
                    sw.WriteLine(indent + prefix + "true(@" + (baseOff + at) + ")");
                    return;
                case 0xCA:
                    pos = pos + 1;
                    if (pos + 4 <= end)
                    {
                        float f = MpFloat32(buf, pos);
                        pos = pos + 4;
                        sw.WriteLine(indent + prefix + "float " + f.ToString("0.#####") + "(@" + (baseOff + at) + ")");
                    }
                    else
                    {
                        pos = end;
                    }
                    return;
                case 0xCB:
                    pos = pos + 1;
                    if (pos + 8 <= end)
                    {
                        double d = MpFloat64(buf, pos);
                        pos = pos + 8;
                        sw.WriteLine(indent + prefix + "double " + d.ToString("0.#####") + "(@" + (baseOff + at) + ")");
                    }
                    else
                    {
                        pos = end;
                    }
                    return;
                case 0xCC:
                case 0xCD:
                case 0xCE:
                case 0xCF:
                    {
                        int width = 1 << (b - 0xCC);
                        pos = pos + 1;
                        long v = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            v = (v << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        sw.WriteLine(indent + prefix + "uint " + v + "(@" + (baseOff + at) + ")");
                        return;
                    }
                case 0xD0:
                case 0xD1:
                case 0xD2:
                case 0xD3:
                    {
                        int width = 1 << (b - 0xD0);
                        pos = pos + 1;
                        long v = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            v = (v << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        int shift = 64 - 8 * width;
                        long sv = (v << shift) >> shift;
                        sw.WriteLine(indent + prefix + "int " + sv + "(@" + (baseOff + at) + ")");
                        return;
                    }
                case 0xD9:
                case 0xDA:
                case 0xDB:
                    {
                        int width = b == 0xD9 ? 1 : (b == 0xDA ? 2 : 4);
                        pos = pos + 1;
                        long slen = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            slen = (slen << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        string s2 = MpReadStrBody(buf, ref pos, end, slen);
                        sw.WriteLine(indent + prefix + "\"" + s2 + "\"(@" + (baseOff + at) + " len " + slen + ")");
                        return;
                    }
                case 0xDC:
                case 0xDD:
                    {
                        int width = b == 0xDC ? 2 : 4;
                        pos = pos + 1;
                        long cnt = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            cnt = (cnt << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        MpDumpArray(buf, ref pos, end, cnt, depth, maxDepth, sw, prefix, baseOff, at);
                        return;
                    }
                case 0xDE:
                case 0xDF:
                    {
                        int width = b == 0xDE ? 2 : 4;
                        pos = pos + 1;
                        long cnt = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            cnt = (cnt << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        MpDumpMap(buf, ref pos, end, cnt, depth, maxDepth, sw, prefix, baseOff, at);
                        return;
                    }
                case 0xC4:
                case 0xC5:
                case 0xC6:
                    {
                        int width = b == 0xC4 ? 1 : (b == 0xC5 ? 2 : 4);
                        pos = pos + 1;
                        long cnt = 0;
                        for (int i = 0; i < width && pos < end; i = i + 1)
                        {
                            cnt = (cnt << 8) | buf[pos];
                            pos = pos + 1;
                        }
                        sw.WriteLine(indent + prefix + "bin[" + cnt + "]@" + (baseOff + at));
                        pos = pos + (int)Math.Min(cnt, end - pos);
                        return;
                    }
                default:
                    pos = pos + 1;
                    sw.WriteLine(indent + prefix + "?(0x" + b.ToString("X2") + ")@" + (baseOff + at));
                    return;
            }
        }

        /// <summary>dump 一个 MessagePack map（键按字符串读，值递归）。</summary>
        private static void MpDumpMap(byte[] buf, ref int pos, int end, long count, int depth, int maxDepth, StreamWriter sw, string prefix, long baseOff, int at)
        {
            string indent = new string(' ', depth * 2);
            sw.WriteLine(indent + prefix + "map " + count + "(@" + (baseOff + at) + ")");
            if (depth >= maxDepth)
            {
                sw.WriteLine(indent + "  <深度截断>");
                MpSkipMap(buf, ref pos, end, count);
                return;
            }
            for (long i = 0; i < count; i = i + 1)
            {
                if (pos >= end)
                {
                    sw.WriteLine(indent + "  <越界>");
                    return;
                }
                string key = MpReadKey(buf, ref pos, end);
                if (key == null)
                {
                    sw.WriteLine(indent + "  <键读取失败 @" + (baseOff + pos) + ">");
                    return;
                }
                MpDumpValue(buf, ref pos, end, depth + 1, maxDepth, sw, key + " = ", baseOff);
            }
        }

        /// <summary>dump 一个 MessagePack 数组（浮点数组折叠成一行）。</summary>
        private static void MpDumpArray(byte[] buf, ref int pos, int end, long count, int depth, int maxDepth, StreamWriter sw, string prefix, long baseOff, int at)
        {
            string indent = new string(' ', depth * 2);
            if (count >= 6 && pos < end && (buf[pos] == 0xCA || buf[pos] == 0xCB))
            {
                bool f32 = buf[pos] == 0xCA;
                int width = f32 ? 4 : 8;
                if (pos + 1 + count * width <= end)
                {
                    pos = pos + 1;
                    var sb = new StringBuilder();
                    for (long i = 0; i < count; i = i + 1)
                    {
                        if (i > 0)
                        {
                            sb.Append(" ");
                        }
                        if (f32)
                        {
                            sb.Append(MpFloat32(buf, pos).ToString("0.###"));
                        }
                        else
                        {
                            sb.Append(MpFloat64(buf, pos).ToString("0.###"));
                        }
                        pos = pos + width;
                    }
                    sw.WriteLine(indent + prefix + (f32 ? "float32" : "float64") + "[" + count + "] " + sb + "(@" + (baseOff + at) + ")");
                    return;
                }
            }
            sw.WriteLine(indent + prefix + "array " + count + "(@" + (baseOff + at) + ")");
            if (depth >= maxDepth)
            {
                sw.WriteLine(indent + "  <深度截断>");
                MpSkipArray(buf, ref pos, end, count);
                return;
            }
            for (long i = 0; i < count; i = i + 1)
            {
                if (pos >= end)
                {
                    sw.WriteLine(indent + "  <越界>");
                    return;
                }
                MpDumpValue(buf, ref pos, end, depth + 1, maxDepth, sw, "#" + i + " = ", baseOff);
            }
        }

        /// <summary>读一个 map 的键（字符串）——非字符串返回 null。</summary>
        private static string MpReadKey(byte[] buf, ref int pos, int end)
        {
            if (pos >= end)
            {
                return null;
            }
            byte b = buf[pos];
            long len;
            if (b >= 0xA0 && b <= 0xBF)
            {
                len = b - 0xA0;
                pos = pos + 1;
            }
            else if (b == 0xD9 || b == 0xDA || b == 0xDB)
            {
                int width = b == 0xD9 ? 1 : (b == 0xDA ? 2 : 4);
                pos = pos + 1;
                len = 0;
                for (int i = 0; i < width && pos < end; i = i + 1)
                {
                    len = (len << 8) | buf[pos];
                    pos = pos + 1;
                }
            }
            else
            {
                return null;
            }
            return MpReadStrBody(buf, ref pos, end, len);
        }

        /// <summary>读字符串体（超长截断显示，指针按实际长度前进）。</summary>
        private static string MpReadStrBody(byte[] buf, ref int pos, int end, long len)
        {
            if (len < 0 || pos + len > end)
            {
                len = Math.Max(0, end - pos);
            }
            string s = Encoding.UTF8.GetString(buf, pos, (int)len);
            pos = pos + (int)len;
            if (s.Length > 120)
            {
                s = s.Substring(0, 120) + "…";
            }
            return s;
        }

        /// <summary>读大端 float32（MessagePack 的浮点是网络字节序）。</summary>
        private static float MpFloat32(byte[] buf, int at)
        {
            int bits = (buf[at] << 24) | (buf[at + 1] << 16) | (buf[at + 2] << 8) | buf[at + 3];
            return BitConverter.Int32BitsToSingle(bits);
        }

        /// <summary>读大端 float64。</summary>
        private static double MpFloat64(byte[] buf, int at)
        {
            long bits = ((long)buf[at] << 56) | ((long)buf[at + 1] << 48) | ((long)buf[at + 2] << 40) | ((long)buf[at + 3] << 32)
                | ((long)buf[at + 4] << 24) | ((long)buf[at + 5] << 16) | ((long)buf[at + 6] << 8) | buf[at + 7];
            return BitConverter.Int64BitsToDouble(bits);
        }

        /// <summary>跳过一个 MessagePack 值（不输出——深度截断处用，保证同层后续键仍可见）。</summary>
        private static void MpSkipValue(byte[] buf, ref int pos, int end)
        {
            if (pos >= end)
            {
                return;
            }
            byte b = buf[pos];
            if (b <= 0x7F || b >= 0xE0)
            {
                pos = pos + 1;
                return;
            }
            if (b >= 0x80 && b <= 0x8F)
            {
                pos = pos + 1;
                MpSkipMap(buf, ref pos, end, b - 0x80);
                return;
            }
            if (b >= 0x90 && b <= 0x9F)
            {
                pos = pos + 1;
                MpSkipArray(buf, ref pos, end, b - 0x90);
                return;
            }
            if (b >= 0xA0 && b <= 0xBF)
            {
                long slen = b - 0xA0;
                pos = pos + 1;
                pos = (int)Math.Min(end, pos + slen);
                return;
            }
            switch (b)
            {
                case 0xC0:
                case 0xC2:
                case 0xC3:
                    pos = pos + 1;
                    return;
                case 0xCA:
                    pos = (int)Math.Min(end, pos + 5L);
                    return;
                case 0xCB:
                    pos = (int)Math.Min(end, pos + 9L);
                    return;
                case 0xCC:
                case 0xD0:
                    pos = (int)Math.Min(end, pos + 2L);
                    return;
                case 0xCD:
                case 0xD1:
                    pos = (int)Math.Min(end, pos + 3L);
                    return;
                case 0xCE:
                case 0xD2:
                    pos = (int)Math.Min(end, pos + 5L);
                    return;
                case 0xCF:
                case 0xD3:
                    pos = (int)Math.Min(end, pos + 9L);
                    return;
                case 0xD9:
                case 0xDA:
                case 0xDB:
                    {
                        int width = b == 0xD9 ? 1 : (b == 0xDA ? 2 : 4);
                        pos = pos + 1;
                        long slen = MpSkipUInt(buf, ref pos, end, width);
                        pos = (int)Math.Min(end, pos + slen);
                        return;
                    }
                case 0xC4:
                case 0xC5:
                case 0xC6:
                    {
                        int width = b == 0xC4 ? 1 : (b == 0xC5 ? 2 : 4);
                        pos = pos + 1;
                        long blen = MpSkipUInt(buf, ref pos, end, width);
                        pos = (int)Math.Min(end, pos + blen);
                        return;
                    }
                case 0xC7:
                case 0xC8:
                case 0xC9:
                    {
                        int width = b == 0xC7 ? 1 : (b == 0xC8 ? 2 : 4);
                        pos = pos + 1;
                        long elen = MpSkipUInt(buf, ref pos, end, width);
                        pos = (int)Math.Min(end, pos + elen + 1);
                        return;
                    }
                case 0xD4:
                case 0xD5:
                case 0xD6:
                case 0xD7:
                case 0xD8:
                    pos = (int)Math.Min(end, pos + 2L + (1L << (b - 0xD4)));
                    return;
                case 0xDC:
                case 0xDD:
                    {
                        int width = b == 0xDC ? 2 : 4;
                        pos = pos + 1;
                        long cnt = MpSkipUInt(buf, ref pos, end, width);
                        MpSkipArray(buf, ref pos, end, cnt);
                        return;
                    }
                case 0xDE:
                case 0xDF:
                    {
                        int width = b == 0xDE ? 2 : 4;
                        pos = pos + 1;
                        long cnt = MpSkipUInt(buf, ref pos, end, width);
                        MpSkipMap(buf, ref pos, end, cnt);
                        return;
                    }
                default:
                    pos = pos + 1;
                    return;
            }
        }

        /// <summary>跳过一个 MessagePack map 的内容（count 个键值对）。</summary>
        private static void MpSkipMap(byte[] buf, ref int pos, int end, long count)
        {
            for (long i = 0; i < count && pos < end; i = i + 1)
            {
                MpSkipValue(buf, ref pos, end);
                MpSkipValue(buf, ref pos, end);
            }
        }

        /// <summary>跳过一个 MessagePack 数组的内容。</summary>
        private static void MpSkipArray(byte[] buf, ref int pos, int end, long count)
        {
            for (long i = 0; i < count && pos < end; i = i + 1)
            {
                MpSkipValue(buf, ref pos, end);
            }
        }

        /// <summary>读大端无符号整数（1..4 字节，跳过用）。</summary>
        private static long MpSkipUInt(byte[] buf, ref int pos, int end, int width)
        {
            long v = 0;
            for (int i = 0; i < width && pos < end; i = i + 1)
            {
                v = (v << 8) | buf[pos];
                pos = pos + 1;
            }
            return v;
        }
        /// <summary>验证用：打印 mod 列表的被引用卡数（总数 + 角色卡 / 服装卡 / 场景卡分列），并对被引用最多的若干 guid 列出引用卡片的类型明细——供人工核对三列与明细一致。</summary>
        private static int ModUseCommand(string dbPath, int detail)
        {
            using (StoreHub hub = new StoreHub(dbPath))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                List<ModRow> rows = hub.QueryMods(cfg, 1, 0, "all", "");
                long withUse = 0;
                long over = 0;
                foreach (ModRow m in rows)
                {
                    if (m.Used > 0)
                    {
                        withUse = withUse + 1;
                    }
                    if (m.UsedChara + m.UsedClothes + m.UsedSd > m.Used)
                    {
                        over = over + 1;
                    }
                }
                Console.WriteLine("mod 行 " + rows.Count.ToString() + " · 被引用 " + withUse.ToString() + " · 分列之和超总数 " + over.ToString());
                List<ModRow> top = new List<ModRow>(rows);
                top.Sort(delegate (ModRow a, ModRow b) { return b.Used.CompareTo(a.Used); });
                int n = detail < top.Count ? detail : top.Count;
                for (int i = 0; i < n; i = i + 1)
                {
                    ModRow m = top[i];
                    Console.WriteLine(m.Guid + "  used=" + m.Used.ToString() + " 角色卡=" + m.UsedChara.ToString()
                        + " 服装卡=" + m.UsedClothes.ToString() + " 场景卡=" + m.UsedSd.ToString());
                    List<CardRow> cards = hub.QueryCardsByMod(cfg, m.Guid, 1, 0);
                    Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (CardRow c in cards)
                    {
                        string t = c.CardType == null ? "<null>" : c.CardType;
                        int k = 0;
                        byType.TryGetValue(t, out k);
                        byType[t] = k + 1;
                    }
                    foreach (KeyValuePair<string, int> kv in byType)
                    {
                        Console.WriteLine("    卡类型 " + kv.Key + " : " + kv.Value.ToString());
                    }
                }
            }
            return 0;
        }
    }
}
