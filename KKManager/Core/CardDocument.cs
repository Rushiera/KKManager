using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>PNG 文件里的一个块（chunk）——偏移 / 长度 / 类型 / CRC 校验结果。</summary>
    public class PngChunk
    {
        /// <summary>块类型（4 个 ASCII 字符，如 IHDR / IDAT / IEND / tEXt）。</summary>
        public string Type { get; set; }

        /// <summary>块起始偏移（长度字段的第一个字节）。</summary>
        public long Offset { get; set; }

        /// <summary>块数据起始偏移（长度与类型字段之后）。</summary>
        public long DataOffset { get; set; }

        /// <summary>块数据字节数（不含长度、类型与 CRC）。</summary>
        public long Length { get; set; }

        /// <summary>块结束偏移（CRC 之后，即下一块的起点）。</summary>
        public long End { get; set; }

        /// <summary>CRC 是否通过（未校验的块恒为真，另看 CrcChecked）。</summary>
        public bool CrcOk { get; set; }

        /// <summary>是否真的校验过 CRC——数据超过 64 KB 的块不校验（大块读一遍代价高）。</summary>
        public bool CrcChecked { get; set; }
    }

    /// <summary>卡片文件结构——PNG 块表 + 数据区起点与头部字段（只读快照，不改文件）。</summary>
    public class CardStructure
    {
        /// <summary>文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>文件名（含扩展名）。</summary>
        public string FileName { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间（本地时间文本）。</summary>
        public string Mtime { get; set; }

        /// <summary>PNG 块表（按文件中出现顺序）。</summary>
        public List<PngChunk> Chunks { get; } = new List<PngChunk>();

        /// <summary>图片区结束偏移（IEND 块结束处）；-1 表示未找到。</summary>
        public long ImageEnd { get; set; }

        /// <summary>数据区字节数（文件尾 - 图片区结束）。</summary>
        public long DataSize { get; set; }

        /// <summary>卡类型标记（如 【KoiKatuClothes】）。</summary>
        public string CardType { get; set; }

        /// <summary>数据区版本标记（如 0.0.0）。</summary>
        public string DataVersion { get; set; }

        /// <summary>数据区头部前 64 字节的十六进制。</summary>
        public string DataHeadHex { get; set; }

        /// <summary>解析错误（null 表示结构完整读出）。</summary>
        public string Error { get; set; }

        /// <summary>非致命告警（如数据区头部解析失败）——失败必须可见。</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 卡片文件结构解析——只读：PNG 块表 + 图片区 / 数据区分界 + 数据区头部分段。
    /// 边界：只做「结构呈现」，不写文件、不解析数据区内部（ChaFile 字段级解析未立项）。
    /// </summary>
    public static class CardDocument
    {
        /// <summary>CRC 校验的块大小上限——超过则不校验（大块读一遍代价高）。</summary>
        private const long CrcCheckLimit = 65536;

        /// <summary>CRC32 查表（PNG 用的反射多项式 0xEDB88320）。</summary>
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>解析一张卡片的文件结构（只读，不整文件读入）。</summary>
        public static CardStructure Parse(string path)
        {
            CardStructure st = new CardStructure();
            st.ImageEnd = -1;
            st.FilePath = path;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                st.Error = "文件不存在";
                return st;
            }

            FileInfo fi = new FileInfo(path);
            st.FilePath = fi.FullName;
            st.FileName = fi.Name;
            st.Size = fi.Length;
            st.Mtime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

            using (FileStream fs = File.OpenRead(path))
            {
                // [段1] 读签名
                byte[] sig = new byte[8];
                if (ReadFull(fs, sig, 8) != 8 || sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47)
                {
                    st.Error = "不是 PNG 文件（签名不符）";
                    return st;
                }

                // [段2] 遍历块表
                byte[] hdr = new byte[8];
                long pos = 8;
                while (pos + 12 <= fs.Length)
                {
                    fs.Position = pos;
                    if (ReadFull(fs, hdr, 8) != 8)
                    {
                        st.Error = "块头读取失败（@" + pos + "）";
                        break;
                    }
                    long clen = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                    string type = Encoding.ASCII.GetString(hdr, 4, 4);
                    if (clen < 0 || pos + 12 + clen > fs.Length)
                    {
                        st.Error = "块长度越界（@" + pos + " type=" + type + " len=" + clen + "）";
                        break;
                    }
                    PngChunk ck = new PngChunk();
                    ck.Type = type;
                    ck.Offset = pos;
                    ck.DataOffset = pos + 8;
                    ck.Length = clen;
                    ck.End = pos + 12 + clen;
                    if (clen <= CrcCheckLimit)
                    {
                        ck.CrcChecked = true;
                        ck.CrcOk = CheckChunkCrc(fs, ck);
                    }
                    else
                    {
                        ck.CrcChecked = false;
                        ck.CrcOk = true;
                    }
                    st.Chunks.Add(ck);
                    if (type == "IEND")
                    {
                        st.ImageEnd = ck.End;
                        break;
                    }
                    pos = ck.End;
                }

                // [段3] 读数据区头部（卡类型 / 数据版本 / 首 64 字节）
                if (st.ImageEnd > 0)
                {
                    st.DataSize = st.Size - st.ImageEnd;
                    ReadDataHead(fs, st);
                }
            }

            if (st.ImageEnd <= 0 && st.Error == null)
            {
                st.Error = "未找到 IEND 块——不是可识别的卡片 PNG";
            }
            return st;
        }

        /// <summary>读数据区头部——卡类型（场景卡 sd / 人物卡 / 服装卡）+ 数据版本 + 首 64 字节 hex。</summary>
        private static void ReadDataHead(FileStream fs, CardStructure st)
        {
            byte[] head = new byte[256];
            fs.Position = st.ImageEnd;
            int n = ReadFull(fs, head, head.Length);
            if (n <= 0)
            {
                st.Warnings.Add("数据区为空（IEND 之后没有字节）");
                return;
            }
            int hexLen = n < 64 ? n : 64;
            StringBuilder hex = new StringBuilder(hexLen * 3);
            for (int i = 0; i < hexLen; i = i + 1)
            {
                hex.Append(head[i].ToString("X2"));
                if (i + 1 < hexLen)
                {
                    hex.Append(' ');
                }
            }
            st.DataHeadHex = hex.ToString();

            if (n < 6)
            {
                st.Warnings.Add("数据区头部不足 6 字节，卡类型未读出");
                return;
            }
            try
            {
                using (MemoryStream ms = new MemoryStream(head, 0, n, false))
                using (BinaryReader br = new BinaryReader(ms))
                {
                    CardReader.ReadCardHead(br, out string cardType, out string dataVersion);
                    st.CardType = cardType;
                    st.DataVersion = dataVersion;
                }
            }
            catch (Exception ex)
            {
                st.Warnings.Add("数据区头部解析失败：" + ex.Message);
                st.CardType = null;
                st.DataVersion = null;
            }
        }

        /// <summary>校验一个块的 CRC（类型 + 数据）。</summary>
        private static bool CheckChunkCrc(FileStream fs, PngChunk ck)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(ck.Type);
            byte[] data = new byte[(int)ck.Length];
            fs.Position = ck.DataOffset;
            if (ReadFull(fs, data, data.Length) != data.Length)
            {
                return false;
            }
            uint crc = Crc32Update(0xFFFFFFFFu, typeBytes, 0, typeBytes.Length);
            crc = Crc32Update(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;

            byte[] crcBytes = new byte[4];
            fs.Position = ck.DataOffset + ck.Length;
            if (ReadFull(fs, crcBytes, 4) != 4)
            {
                return false;
            }
            uint stored = ((uint)crcBytes[0] << 24) | ((uint)crcBytes[1] << 16) | ((uint)crcBytes[2] << 8) | crcBytes[3];
            return stored == crc;
        }

        /// <summary>在既有 CRC 中间态上继续累计。</summary>
        private static uint Crc32Update(uint crc, byte[] buf, int offset, int count)
        {
            uint c = crc;
            for (int i = 0; i < count; i = i + 1)
            {
                c = CrcTable[(c ^ buf[offset + i]) & 0xFF] ^ (c >> 8);
            }
            return c;
        }

        /// <summary>构建 CRC32 查表。</summary>
        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (int n = 0; n < 256; n = n + 1)
            {
                uint c = (uint)n;
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
                table[n] = c;
            }
            return table;
        }

        /// <summary>读满指定字节数（不足返回实际字节数）。</summary>
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
                total = total + n;
            }
            return total;
        }
    }
}
