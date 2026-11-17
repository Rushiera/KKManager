using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>数据区里的一件服装部件（parts 数组元素）。</summary>
    public class CardPart
    {
        /// <summary>部件序号（1 起）。</summary>
        public int Index { get; set; }

        /// <summary>部件槽位 id（数组元素 id 字段；缺失为 -1）。</summary>
        public long Id { get; set; }

        /// <summary>该部件记录的键名清单（如 id / colorInfo / ExtendedSaveData / emblemeId）。</summary>
        public List<string> Keys { get; } = new List<string>();
    }

    /// <summary>声明区（Sideloader UAR）里的一条 mod 引用——部件粒度。</summary>
    public class CardModDetail
    {
        /// <summary>mod 标识（manifest guid）。</summary>
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

    /// <summary>数据区里的一张内嵌图片（PNG）。</summary>
    public class CardImageInfo
    {
        /// <summary>序号（1 起，按文件内出现顺序）。</summary>
        public int Index { get; set; }

        /// <summary>文件内绝对偏移。</summary>
        public long Offset { get; set; }

        /// <summary>图片字节数。</summary>
        public long Size { get; set; }

        /// <summary>宽（像素）。</summary>
        public int Width { get; set; }

        /// <summary>高（像素）。</summary>
        public int Height { get; set; }

        /// <summary>位深。</summary>
        public int BitDepth { get; set; }

        /// <summary>色型（0 灰度 / 2 RGB / 6 RGBA）。</summary>
        public int ColorType { get; set; }

        /// <summary>PNG 块数。</summary>
        public int Chunks { get; set; }

        /// <summary>是否读到 IEND（未闭合 = 数据被截断，或不是完整 PNG）。</summary>
        public bool Ended { get; set; }

        /// <summary>是否卡面脸图（数据区头段直接引用的那一张）。</summary>
        public bool IsFace { get; set; }
    }

    /// <summary>数据块目录（lstInfo）里的一条——块名 / 版本 / 位置 / 大小。</summary>
    public class CardBlockInfo
    {
        /// <summary>块名（如 KKEx / Custom / Coordinate）。</summary>
        public string Name { get; set; }

        /// <summary>块版本。</summary>
        public string Version { get; set; }

        /// <summary>块在数据区里的位置（相对数据区起点）。</summary>
        public long Pos { get; set; }

        /// <summary>块字节数。</summary>
        public long Size { get; set; }
    }

    /// <summary>数据区深度解析结果（只读快照）。</summary>
    public class CardDetailResult
    {
        /// <summary>角色名（服装卡头段记录；人物卡为空）。</summary>
        public string CharName { get; set; }

        /// <summary>卡面脸图字节数（人物卡头段记录的 int32；无则 0）。</summary>
        public long FaceImageSize { get; set; }

        /// <summary>卡面脸图在文件内的偏移（无则 0）。</summary>
        public long FaceImageOffset { get; set; }

        /// <summary>服装部件（parts 数组）。</summary>
        public List<CardPart> Parts { get; } = new List<CardPart>();

        /// <summary>声明区的 mod 引用（部件粒度）。</summary>
        public List<CardModDetail> ModRefs { get; } = new List<CardModDetail>();

        /// <summary>内嵌图片清单。</summary>
        public List<CardImageInfo> Images { get; } = new List<CardImageInfo>();

        /// <summary>数据块目录（lstInfo）。</summary>
        public List<CardBlockInfo> Blocks { get; } = new List<CardBlockInfo>();

        /// <summary>插件块里记录的插件名（KKEx map 的键）。</summary>
        public List<string> PluginNames { get; } = new List<string>();

        /// <summary>插件块（KKEx）在文件内的偏移。</summary>
        public List<long> KkxOffsets { get; } = new List<long>();

        /// <summary>内嵌图片字节合计。</summary>
        public long ImageBytesTotal { get; set; }

        /// <summary>解析告警（失败必须可见）。</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>内存里的 MessagePack 顺序读取器——只读与跳过，用于声明区 / 目录 / 部件解析。</summary>
    internal class MsgPackCursor
    {
        /// <summary>读取缓冲区（局部读入的字节）。</summary>
        private readonly byte[] _buf;
        /// <summary>当前读取位置。</summary>
        private int _pos;

        /// <summary>在指定缓冲区与起点上建立读取器。</summary>
        public MsgPackCursor(byte[] buf, int pos)
        {
            _buf = buf;
            _pos = pos;
        }

        /// <summary>当前偏移。</summary>
        public int Position
        {
            get { return _pos; }
        }

        /// <summary>是否已到缓冲末尾。</summary>
        public bool Eof
        {
            get { return _pos >= _buf.Length; }
        }

        /// <summary>读 map 头（键数）；失败返回 false。</summary>
        public bool TryReadMapHeader(out int count)
        {
            count = 0;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b >= 0x80 && b <= 0x8F)
            {
                count = b & 0x0F;
                _pos = _pos + 1;
                return true;
            }
            if (b == 0xDE && _pos + 3 <= _buf.Length)
            {
                count = (_buf[_pos + 1] << 8) | _buf[_pos + 2];
                _pos = _pos + 3;
                return true;
            }
            if (b == 0xDF && _pos + 5 <= _buf.Length)
            {
                count = (_buf[_pos + 1] << 24) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 8) | _buf[_pos + 4];
                _pos = _pos + 5;
                return true;
            }
            return false;
        }

        /// <summary>读数组头（元素数）；失败返回 false。</summary>
        public bool TryReadArrayHeader(out int count)
        {
            count = 0;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b >= 0x90 && b <= 0x9F)
            {
                count = b & 0x0F;
                _pos = _pos + 1;
                return true;
            }
            if (b == 0xDC && _pos + 3 <= _buf.Length)
            {
                count = (_buf[_pos + 1] << 8) | _buf[_pos + 2];
                _pos = _pos + 3;
                return true;
            }
            if (b == 0xDD && _pos + 5 <= _buf.Length)
            {
                count = (_buf[_pos + 1] << 24) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 8) | _buf[_pos + 4];
                _pos = _pos + 5;
                return true;
            }
            return false;
        }

        /// <summary>读字符串；失败返回 false。</summary>
        public bool TryReadString(out string value)
        {
            value = null;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            int len;
            if (b >= 0xA0 && b <= 0xBF)
            {
                len = b & 0x1F;
                _pos = _pos + 1;
            }
            else if (b == 0xD9 && _pos + 2 <= _buf.Length)
            {
                len = _buf[_pos + 1];
                _pos = _pos + 2;
            }
            else if (b == 0xDA && _pos + 3 <= _buf.Length)
            {
                len = (_buf[_pos + 1] << 8) | _buf[_pos + 2];
                _pos = _pos + 3;
            }
            else if (b == 0xDB && _pos + 5 <= _buf.Length)
            {
                len = (_buf[_pos + 1] << 24) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 8) | _buf[_pos + 4];
                _pos = _pos + 5;
            }
            else
            {
                return false;
            }
            if (len < 0 || _pos + len > _buf.Length)
            {
                return false;
            }
            value = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos = _pos + len;
            return true;
        }

        /// <summary>读整数（各宽度整型）；失败返回 false。</summary>
        public bool TryReadLong(out long value)
        {
            value = 0;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b <= 0x7F)
            {
                value = b;
                _pos = _pos + 1;
                return true;
            }
            if (b >= 0xE0)
            {
                value = (sbyte)b;
                _pos = _pos + 1;
                return true;
            }
            if (b == 0xCC && _pos + 2 <= _buf.Length)
            {
                value = _buf[_pos + 1];
                _pos = _pos + 2;
                return true;
            }
            if (b == 0xCD && _pos + 3 <= _buf.Length)
            {
                value = (_buf[_pos + 1] << 8) | _buf[_pos + 2];
                _pos = _pos + 3;
                return true;
            }
            if (b == 0xCE && _pos + 5 <= _buf.Length)
            {
                value = ((long)_buf[_pos + 1] << 24) | ((long)_buf[_pos + 2] << 16) | ((long)_buf[_pos + 3] << 8) | _buf[_pos + 4];
                _pos = _pos + 5;
                return true;
            }
            if (b == 0xD0 && _pos + 2 <= _buf.Length)
            {
                value = (sbyte)_buf[_pos + 1];
                _pos = _pos + 2;
                return true;
            }
            if (b == 0xD1 && _pos + 3 <= _buf.Length)
            {
                value = (short)((_buf[_pos + 1] << 8) | _buf[_pos + 2]);
                _pos = _pos + 3;
                return true;
            }
            if (b == 0xD2 && _pos + 5 <= _buf.Length)
            {
                value = (int)(((long)_buf[_pos + 1] << 24) | ((long)_buf[_pos + 2] << 16) | ((long)_buf[_pos + 3] << 8) | _buf[_pos + 4]);
                _pos = _pos + 5;
                return true;
            }
            return false;
        }
        /// <summary>读浮点（float32 / float64）；失败返回 false。</summary>
        public bool TryReadDouble(out double value)
        {
            value = 0;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b == 0xCA && _pos + 5 <= _buf.Length)
            {
                int bits = (_buf[_pos + 1] << 24) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 8) | _buf[_pos + 4];
                value = BitConverter.Int32BitsToSingle(bits);
                _pos = _pos + 5;
                return true;
            }
            if (b == 0xCB && _pos + 9 <= _buf.Length)
            {
                long bits = ((long)_buf[_pos + 1] << 56) | ((long)_buf[_pos + 2] << 48) | ((long)_buf[_pos + 3] << 40)
                    | ((long)_buf[_pos + 4] << 32) | ((long)_buf[_pos + 5] << 24) | ((long)_buf[_pos + 6] << 16)
                    | ((long)_buf[_pos + 7] << 8) | _buf[_pos + 8];
                value = BitConverter.Int64BitsToDouble(bits);
                _pos = _pos + 9;
                return true;
            }
            return false;
        }
        /// <summary>读布尔（MessagePack true / false）；失败返回 false。</summary>
        public bool TryReadBool(out bool value)
        {
            value = false;
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b == 0xC2)
            {
                value = false;
                _pos = _pos + 1;
                return true;
            }
            if (b == 0xC3)
            {
                value = true;
                _pos = _pos + 1;
                return true;
            }
            return false;
        }

        /// <summary>按类型跳过一个值（含嵌套容器）；失败返回 false。</summary>
        public bool TrySkipValue()
        {
            return TrySkipValue(0);
        }

        /// <summary>按类型跳过一个值（带嵌套深度保护）。</summary>
        private bool TrySkipValue(int depth)
        {
            if (depth > 64 || _pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b <= 0x7F || b >= 0xE0)
            {
                _pos = _pos + 1;
                return true;
            }
            if (b >= 0xA0 && b <= 0xBF)
            {
                int len = b & 0x1F;
                _pos = _pos + 1 + len;
                return _pos <= _buf.Length;
            }
            if (b >= 0x90 && b <= 0x9F)
            {
                int n = b & 0x0F;
                _pos = _pos + 1;
                return SkipItems(n, depth);
            }
            if (b >= 0x80 && b <= 0x8F)
            {
                int n = (b & 0x0F) * 2;
                _pos = _pos + 1;
                return SkipItems(n, depth);
            }
            if (b == 0xC0 || b == 0xC2 || b == 0xC3)
            {
                _pos = _pos + 1;
                return true;
            }
            if (b == 0xC4 || b == 0xC5 || b == 0xC6)
            {
                int width = b == 0xC4 ? 1 : (b == 0xC5 ? 2 : 4);
                return SkipPayload(width);
            }
            if (b == 0xCA)
            {
                _pos = _pos + 5;
                return _pos <= _buf.Length;
            }
            if (b == 0xCB)
            {
                _pos = _pos + 9;
                return _pos <= _buf.Length;
            }
            if (b == 0xCC || b == 0xD0)
            {
                _pos = _pos + 2;
                return _pos <= _buf.Length;
            }
            if (b == 0xCD || b == 0xD1)
            {
                _pos = _pos + 3;
                return _pos <= _buf.Length;
            }
            if (b == 0xCE || b == 0xD2)
            {
                _pos = _pos + 5;
                return _pos <= _buf.Length;
            }
            if (b == 0xCF || b == 0xD3)
            {
                _pos = _pos + 9;
                return _pos <= _buf.Length;
            }
            if (b == 0xD9 || b == 0xDA || b == 0xDB)
            {
                int width = b == 0xD9 ? 1 : (b == 0xDA ? 2 : 4);
                return SkipPayload(width);
            }
            if (b == 0xDC || b == 0xDD)
            {
                int n;
                if (!TryReadArrayHeader(out n))
                {
                    return false;
                }
                return SkipItems(n, depth + 1);
            }
            if (b == 0xDE || b == 0xDF)
            {
                int n;
                if (!TryReadMapHeader(out n))
                {
                    return false;
                }
                return SkipItems(n * 2, depth + 1);
            }
            return false;
        }

        /// <summary>按长度前缀（1 / 2 / 4 字节）跳过负载。</summary>
        private bool SkipPayload(int width)
        {
            if (_pos + 1 + width > _buf.Length)
            {
                return false;
            }
            long len;
            if (width == 1)
            {
                len = _buf[_pos + 1];
            }
            else if (width == 2)
            {
                len = (_buf[_pos + 1] << 8) | _buf[_pos + 2];
            }
            else
            {
                len = ((long)_buf[_pos + 1] << 24) | ((long)_buf[_pos + 2] << 16) | ((long)_buf[_pos + 3] << 8) | _buf[_pos + 4];
            }
            _pos = _pos + 1 + width;
            if (len < 0 || _pos + len > _buf.Length)
            {
                return false;
            }
            _pos = _pos + (int)len;
            return true;
        }

        /// <summary>连续跳过 n 个值。</summary>
        private bool SkipItems(int n, int depth)
        {
            for (int i = 0; i < n; i = i + 1)
            {
                if (!TrySkipValue(depth + 1))
                {
                    return false;
                }
            }
            return true;
        }
        /// <summary>看当前字节（不前进；到末尾返回 0）。</summary>
        public byte PeekByte()
        {
            if (_pos >= _buf.Length)
            {
                return 0;
            }
            return _buf[_pos];
        }
        /// <summary>读 bin 头（bin8 / bin16 / bin32）——只前进头部，负载由调用方按需继续解析。</summary>
        public bool TrySkipBinHeader()
        {
            if (_pos >= _buf.Length)
            {
                return false;
            }
            byte b = _buf[_pos];
            if (b == 0xC4 && _pos + 2 <= _buf.Length)
            {
                _pos = _pos + 2;
                return true;
            }
            if (b == 0xC5 && _pos + 3 <= _buf.Length)
            {
                _pos = _pos + 3;
                return true;
            }
            if (b == 0xC6 && _pos + 5 <= _buf.Length)
            {
                _pos = _pos + 5;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 卡片数据区内容解析——一次流式扫描定位关键标记，再局部读入逐类解析。
    /// 只读：不写文件、不整文件读入；解析失败只记告警，不抛异常。
    /// </summary>
    public static class CardDetail
    {
        /// <summary>局部读入窗口（锚点之后最多读这么多字节）。</summary>
        private const int LocalWindow = 512 * 1024;

        /// <summary>PNG 签名。</summary>
        private static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Sideloader UAR 声明区标记。</summary>
        private static readonly byte[] UarMark = Encoding.ASCII.GetBytes("com.bepis.sideloader.universalautoresolver");

        /// <summary>数据块目录标记（fixstr(7) "lstInfo"）。</summary>
        private static readonly byte[] LstInfoMark = { 0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F };

        /// <summary>插件块标记 "KKEx"。</summary>
        private static readonly byte[] KkxMark = { 0x4B, 0x4B, 0x45, 0x78 };

        /// <summary>服装部件表标记（fixstr(5) "parts"）。</summary>
        private static readonly byte[] PartsMark = { 0xA5, 0x70, 0x61, 0x72, 0x74, 0x73 };

        /// <summary>解析数据区内容（path = 卡片文件；imageEnd = 图片区结束偏移）。</summary>
        public static CardDetailResult Parse(string path, long imageEnd)
        {
            return Parse(path, imageEnd, null);
        }

        /// <summary>解析数据区内容（会话版——主扫描复用段内已打开的句柄，不另开文件；session 为 null 时自行开文件）。</summary>
        public static CardDetailResult Parse(string path, long imageEnd, CardFileSession session)
        {
            CardDetailResult r = new CardDetailResult();
            if (!File.Exists(path))
            {
                r.Warnings.Add("卡片文件不存在——跳过数据区解析");
                return r;
            }
            if (imageEnd <= 0)
            {
                r.Warnings.Add("图片区终点未知——跳过数据区解析");
                return r;
            }

            List<long> pngHits = new List<long>();
            List<long> kkxHits = new List<long>();
            long uarHit = -1;
            long lstHit = -1;
            long partsHit = -1;
            long size = 0;

            bool own = session == null;
            if (own)
            {
                session = CardFileSession.Open(path);
            }
            if (session == null)
            {
                r.Warnings.Add("卡片文件打不开——跳过数据区解析");
                return r;
            }

            try
            {
                FileStream fs = session.Stream;
                size = session.Length;
                ReadHead(fs, imageEnd, r);
                ScanMarks(fs, imageEnd, size, pngHits, kkxHits, ref uarHit, ref lstHit, ref partsHit);

                BuildImages(fs, size, pngHits, r);
                if (uarHit > 0)
                {
                    ParseDeclaration(fs, size, uarHit, r);
                }
                if (lstHit > 0)
                {
                    ParseLstInfo(fs, size, lstHit, r);
                }
                if (partsHit > 0)
                {
                    ParseParts(fs, size, partsHit, r);
                }
                for (int i = 0; i < kkxHits.Count; i = i + 1)
                {
                    r.KkxOffsets.Add(kkxHits[i]);
                }
                if (uarHit > 0)
                {
                    ParsePlugins(fs, size, uarHit, r);
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add("数据区解析异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (own)
                {
                    session.Dispose();
                }
            }

            return r;
        }

        /// <summary>读数据区头段——卡类型（场景卡 / 人物卡 / 服装卡）与角色名（服装卡）或脸图长度（人物卡）。</summary>
        private static void ReadHead(FileStream fs, long imageEnd, CardDetailResult r)
        {
            fs.Position = imageEnd;
            BinaryReader br = new BinaryReader(fs);
            CardReader.ReadCardHead(br, out string cardType, out _);
            if (cardType == CardReader.SceneCardType)
            {
                // 场景卡：头段只有场景数据版本，其后就是场景数据——没有角色名段、没有脸图
                return;
            }
            long afterHead = fs.Position;

            if (cardType != null && cardType.IndexOf("Clothes", StringComparison.Ordinal) >= 0)
            {
                r.CharName = CardReader.Read7BitString(br);
                return;
            }

            // 人物卡：int32 脸图长度 + 脸图 PNG
            if (fs.Length - afterHead < 16)
            {
                return;
            }
            fs.Position = afterHead;
            byte[] head = new byte[16];
            if (ReadFull(fs, head, 16) != 16)
            {
                return;
            }
            long len = (long)head[0] | ((long)head[1] << 8) | ((long)head[2] << 16) | ((long)head[3] << 24);
            bool isPng = true;
            for (int i = 0; i < PngSig.Length; i = i + 1)
            {
                if (head[4 + i] != PngSig[i])
                {
                    isPng = false;
                    break;
                }
            }
            if (isPng && len > 0 && afterHead + 4 + len <= fs.Length)
            {
                r.FaceImageOffset = afterHead + 4;
                r.FaceImageSize = len;
            }
            else if (!isPng)
            {
                r.Warnings.Add("人物卡头段之后不是 PNG 签名——脸图长度未确认");
            }
        }

        /// <summary>流式扫描数据区，收集 PNG 签名 / 声明区 / 数据块目录 / 插件块 / 服装部件表的位置。</summary>
        private static void ScanMarks(FileStream fs, long from, long size, List<long> pngHits, List<long> kkxHits, ref long uarHit, ref long lstHit, ref long partsHit)
        {
            if (from < 0 || from >= size)
            {
                return;
            }
            const int BufSize = 1 << 20;
            const int MaxPng = 2000;
            int maxLen = PngSig.Length;
            if (UarMark.Length > maxLen)
            {
                maxLen = UarMark.Length;
            }
            if (LstInfoMark.Length > maxLen)
            {
                maxLen = LstInfoMark.Length;
            }

            byte[] buf = new byte[BufSize + 64];
            fs.Position = from;
            long baseOffset = from;
            int carry = 0;
            int read;
            while ((read = fs.Read(buf, carry, BufSize)) > 0)
            {
                int total = carry + read;
                int limit = total - maxLen;
                if (limit < 0)
                {
                    carry = maxLen - 1;
                    if (carry > total)
                    {
                        carry = total;
                    }
                    Array.Copy(buf, total - carry, buf, 0, carry);
                    continue;
                }
                // 标记定位走 Span.IndexOf（SIMD）——逐字节 × 5 种标记在全数据区上是 CPU 主成本
                Span<byte> span = buf.AsSpan(0, limit + 1);
                int searchFrom = 0;
                while (searchFrom <= limit)
                {
                    int hit = span.Slice(searchFrom).IndexOf(PngSig);
                    if (hit < 0)
                    {
                        break;
                    }
                    int abs = searchFrom + hit;
                    if (pngHits.Count < MaxPng)
                    {
                        pngHits.Add(baseOffset + abs);
                    }
                    searchFrom = abs + 1;
                }
                if (uarHit < 0)
                {
                    int hit = span.IndexOf(UarMark);
                    if (hit >= 0)
                    {
                        uarHit = baseOffset + hit;
                    }
                }
                if (lstHit < 0)
                {
                    int hit = span.IndexOf(LstInfoMark);
                    if (hit >= 0)
                    {
                        lstHit = baseOffset + hit;
                    }
                }
                searchFrom = 0;
                while (searchFrom <= limit)
                {
                    int hit = span.Slice(searchFrom).IndexOf(KkxMark);
                    if (hit < 0)
                    {
                        break;
                    }
                    int abs = searchFrom + hit;
                    kkxHits.Add(baseOffset + abs);
                    searchFrom = abs + 1;
                }
                if (partsHit < 0)
                {
                    int hit = span.IndexOf(PartsMark);
                    if (hit >= 0)
                    {
                        partsHit = baseOffset + hit;
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

        /// <summary>逐张解析内嵌 PNG（块表 → 宽高 / 位深 / 色型 / 字节数）。</summary>
        private static void BuildImages(FileStream fs, long size, List<long> hits, CardDetailResult r)
        {
            byte[] hdr = new byte[8];
            for (int k = 0; k < hits.Count; k = k + 1)
            {
                long off = hits[k];
                CardImageInfo img = new CardImageInfo();
                img.Index = k + 1;
                img.Offset = off;
                long pos = off + PngSig.Length;
                long bytes = PngSig.Length;
                int chunks = 0;
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
                            img.Width = (ih[0] << 24) | (ih[1] << 16) | (ih[2] << 8) | ih[3];
                            img.Height = (ih[4] << 24) | (ih[5] << 16) | (ih[6] << 8) | ih[7];
                            img.BitDepth = ih[8];
                            img.ColorType = ih[9];
                        }
                    }
                    chunks = chunks + 1;
                    bytes = pos + 12 + clen - off;
                    if (type == "IEND")
                    {
                        img.Ended = true;
                        break;
                    }
                    pos = pos + 12 + clen;
                }
                img.Chunks = chunks;
                img.Size = bytes;
                r.ImageBytesTotal = r.ImageBytesTotal + bytes;
                r.Images.Add(img);
            }

            if (r.FaceImageOffset > 0)
            {
                for (int i = 0; i < r.Images.Count; i = i + 1)
                {
                    if (r.Images[i].Offset == r.FaceImageOffset)
                    {
                        r.Images[i].IsFace = true;
                        break;
                    }
                }
            }
        }

        /// <summary>解析声明区——Sideloader UAR 的 info 数组（部件级 mod 引用）。</summary>
        private static void ParseDeclaration(FileStream fs, long size, long uarAt, CardDetailResult r)
        {
            byte[] buf = ReadWindow(fs, uarAt + UarMark.Length, size, LocalWindow);
            if (buf == null)
            {
                r.Warnings.Add("声明区读取失败——mod 明细未解析");
                return;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int n;
            if (!cur.TryReadArrayHeader(out n))
            {
                r.Warnings.Add("声明区结构不符（不是数组）——mod 明细未解析");
                return;
            }
            if (n >= 1 && !cur.TrySkipValue())
            {
                r.Warnings.Add("声明区首元素跳过失败——mod 明细未解析");
                return;
            }
            if (!cur.TryReadMapHeader(out n))
            {
                r.Warnings.Add("声明区结构不符（不是 map）——mod 明细未解析");
                return;
            }
            bool foundInfo = false;
            for (int i = 0; i < n; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    break;
                }
                if (key == "info")
                {
                    foundInfo = true;
                    int count;
                    if (!cur.TryReadArrayHeader(out count))
                    {
                        r.Warnings.Add("声明区 info 不是数组——mod 明细未解析");
                        return;
                    }
                    for (int j = 0; j < count; j = j + 1)
                    {
                        if (!ReadModEntry(cur, r))
                        {
                            r.Warnings.Add("声明区第 " + (j + 1) + " 条解析中断——已解析 " + r.ModRefs.Count + " 条");
                            break;
                        }
                    }
                    break;
                }
                if (!cur.TrySkipValue())
                {
                    break;
                }
            }
            if (!foundInfo)
            {
                r.Warnings.Add("声明区未找到 info 段——mod 明细未解析");
            }
        }

        /// <summary>读一条声明区条目（map：ModID / Property / Slot / LocalSlot / CategoryNo / Author / Website / Name）。</summary>
        private static bool ReadModEntry(MsgPackCursor cur, CardDetailResult r)
        {
            // 条目常被 bin8 / bin16 包裹（实测 C4 AB …）——剥掉 bin 头后即 map
            byte head = cur.PeekByte();
            if (head == 0xC4 || head == 0xC5 || head == 0xC6)
            {
                if (!cur.TrySkipBinHeader())
                {
                    return false;
                }
            }
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                return false;
            }
            CardModDetail d = new CardModDetail();
            d.Slot = -1;
            d.LocalSlot = -1;
            d.CategoryNo = -1;
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                if (key == "ModID" || key == "Property" || key == "Author" || key == "Website" || key == "Name")
                {
                    string v;
                    if (!cur.TryReadString(out v))
                    {
                        return false;
                    }
                    if (key == "ModID")
                    {
                        d.ModId = v;
                    }
                    else if (key == "Property")
                    {
                        d.Property = v;
                    }
                    else if (key == "Author")
                    {
                        d.Author = v;
                    }
                    else if (key == "Website")
                    {
                        d.Website = v;
                    }
                    else
                    {
                        d.Name = v;
                    }
                }
                else if (key == "Slot" || key == "LocalSlot" || key == "CategoryNo")
                {
                    long v;
                    if (!cur.TryReadLong(out v))
                    {
                        return false;
                    }
                    if (key == "Slot")
                    {
                        d.Slot = (int)v;
                    }
                    else if (key == "LocalSlot")
                    {
                        d.LocalSlot = (int)v;
                    }
                    else
                    {
                        d.CategoryNo = (int)v;
                    }
                }
                else if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            if (d.ModId != null && d.ModId.Length > 0)
            {
                r.ModRefs.Add(d);
            }
            return true;
        }

        /// <summary>解析数据块目录（lstInfo）——块名 / 版本 / 位置 / 大小。</summary>
        private static void ParseLstInfo(FileStream fs, long size, long lstAt, CardDetailResult r)
        {
            byte[] buf = ReadWindow(fs, lstAt + LstInfoMark.Length, size, LocalWindow);
            if (buf == null)
            {
                r.Warnings.Add("数据块目录读取失败——目录未解析");
                return;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                r.Warnings.Add("数据块目录不是数组——目录未解析");
                return;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                if (!ReadBlockEntry(cur, r))
                {
                    r.Warnings.Add("数据块目录第 " + (i + 1) + " 条解析中断——已解析 " + r.Blocks.Count + " 条");
                    break;
                }
            }
        }

        /// <summary>读一条数据块目录条目（map：name / version / pos / size）。</summary>
        private static bool ReadBlockEntry(MsgPackCursor cur, CardDetailResult r)
        {
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                return false;
            }
            CardBlockInfo b = new CardBlockInfo();
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                if (key == "name" || key == "version")
                {
                    string v;
                    if (!cur.TryReadString(out v))
                    {
                        return false;
                    }
                    if (key == "name")
                    {
                        b.Name = v;
                    }
                    else
                    {
                        b.Version = v;
                    }
                }
                else if (key == "pos" || key == "size")
                {
                    long v;
                    if (!cur.TryReadLong(out v))
                    {
                        return false;
                    }
                    if (key == "pos")
                    {
                        b.Pos = v;
                    }
                    else
                    {
                        b.Size = v;
                    }
                }
                else if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            if (b.Name != null && b.Name.Length > 0)
            {
                r.Blocks.Add(b);
            }
            return true;
        }

        /// <summary>解析服装部件表（parts 数组）——件数 + 每件的 id 与键名。</summary>
        private static void ParseParts(FileStream fs, long size, long partsAt, CardDetailResult r)
        {
            byte[] buf = ReadWindow(fs, partsAt + PartsMark.Length, size, LocalWindow);
            if (buf == null)
            {
                r.Warnings.Add("服装部件表读取失败——部件未解析");
                return;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                r.Warnings.Add("服装部件表不是数组——部件未解析");
                return;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                CardPart p = new CardPart();
                p.Index = i + 1;
                p.Id = -1;
                int keys;
                if (!cur.TryReadMapHeader(out keys))
                {
                    r.Warnings.Add("服装部件表第 " + (i + 1) + " 件解析中断");
                    break;
                }
                bool ok = true;
                for (int k = 0; k < keys; k = k + 1)
                {
                    string key;
                    if (!cur.TryReadString(out key))
                    {
                        ok = false;
                        break;
                    }
                    p.Keys.Add(key);
                    if (key == "id")
                    {
                        long v;
                        if (cur.TryReadLong(out v))
                        {
                            p.Id = v;
                            continue;
                        }
                        ok = false;
                        break;
                    }
                    if (!cur.TrySkipValue())
                    {
                        ok = false;
                        break;
                    }
                }
                r.Parts.Add(p);
                if (!ok)
                {
                    r.Warnings.Add("服装部件表第 " + (i + 1) + " 件解析不完整");
                    break;
                }
            }
        }

        /// <summary>解析插件块（KKEx）——取 map 键名（插件标识）。</summary>
        private static void ParsePlugins(FileStream fs, long size, long uarAt, CardDetailResult r)
        {
            if (uarAt <= 0)
            {
                return;
            }
            long from = uarAt - 64;
            if (from < 0)
            {
                from = 0;
            }
            byte[] buf = ReadWindow(fs, from, size, LocalWindow);
            if (buf == null)
            {
                return;
            }

            // 声明区所在的插件 map——键即插件标识；map 头落在 UAR 名（str8）之前最多 8 字节内
            int at = (int)(uarAt - from);
            for (int back = 1; back <= 8; back = back + 1)
            {
                int idx = at - back;
                if (idx < 0)
                {
                    break;
                }
                byte b = buf[idx];
                bool isMap = (b >= 0x80 && b <= 0x8F) || b == 0xDE || b == 0xDF;
                if (!isMap)
                {
                    continue;
                }
                MsgPackCursor cur = new MsgPackCursor(buf, idx);
                int keys;
                if (!cur.TryReadMapHeader(out keys))
                {
                    continue;
                }
                string firstName;
                if (!cur.TryReadString(out firstName))
                {
                    continue;
                }
                if (firstName != "com.bepis.sideloader.universalautoresolver")
                {
                    continue;
                }
                r.PluginNames.Add(firstName);

                // 键 1 的值（声明区数据）先跳过——否则后续键名会被当成值解析
                if (!cur.TrySkipValue())
                {
                    r.Warnings.Add("插件名列表不完整（声明区数据越出读取窗口）");
                    return;
                }
                for (int k = 1; k < keys; k = k + 1)
                {
                    string name;
                    if (!cur.TryReadString(out name))
                    {
                        r.Warnings.Add("插件名列表可能不完整（第 " + (k + 1) + " 个键越出读取窗口）");
                        break;
                    }
                    if (name != null && name.Length > 0 && !r.PluginNames.Contains(name))
                    {
                        r.PluginNames.Add(name);
                    }
                    if (!cur.TrySkipValue())
                    {
                        r.Warnings.Add("插件名列表可能不完整（第 " + (k + 1) + " 个键的值越出读取窗口）");
                        break;
                    }
                }
                break;
            }
        }

        /// <summary>从指定偏移读入一个局部窗口（会话流——不足则按实际长度返回；读失败返回 null）。</summary>
        private static byte[] ReadWindow(FileStream fs, long from, long size, int want)
        {
            if (from < 0 || from >= size)
            {
                return null;
            }
            long remain = size - from;
            int len = (int)Math.Min(want, remain);
            byte[] buf = new byte[len];
            try
            {
                fs.Position = from;
                int n = ReadFull(fs, buf, len);
                if (n <= 0)
                {
                    return null;
                }
                if (n < len)
                {
                    byte[] trim = new byte[n];
                    Array.Copy(buf, trim, n);
                    return trim;
                }
            }
            catch (IOException)
            {
                return null;
            }
            return buf;
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
