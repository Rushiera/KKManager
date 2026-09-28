using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>场景卡数据区里的一条插件数据条目（键名 + 版本 + 值形态 + 字节数 + 摘要）。</summary>
    public class ScenePluginItem
    {
        /// <summary>插件键名（MessagePack map 键，如 timeline / kkpe / vnge_actor）。</summary>
        public string Key { get; set; }

        /// <summary>条目值之后的 version 串（timeline / kkpe 无此键 → 空串）。</summary>
        public string Version { get; set; }

        /// <summary>值形态（sceneInfo(XML) / main(串) / main(map N 键) 等）。</summary>
        public string Shape { get; set; }

        /// <summary>载荷字节数（0 = 未解析出长度）。</summary>
        public long Bytes { get; set; }

        /// <summary>条目在文件内的偏移。</summary>
        public long Offset { get; set; }

        /// <summary>内容摘要（按条目语义生成——kkpe 的 itemInfo 条数 / vnge_sssb 的对象数；无则空串）。</summary>
        public string Summary { get; set; }

        /// <summary>字符串值在文件内的起点（非字符串型 0）——内部用，不参与序列化。</summary>
        internal long ValueAt { get; set; }

        /// <summary>字符串值字节数（非字符串型 0）——内部用，不参与序列化。</summary>
        internal long ValueLen { get; set; }
    }

    /// <summary>场景里的一个名字及其出现次数（kkpe 记录的 itemInfo 名——道具 / 节点）。</summary>
    public class SceneNameCount
    {
        /// <summary>名字（道具 / 节点名，UTF-8）。</summary>
        public string Name { get; set; }

        /// <summary>出现次数。</summary>
        public int Count { get; set; }
    }

    /// <summary>场景里的一份内嵌角色卡数据（份头段 + 数据块目录）。</summary>
    public class SceneCharaData
    {
        /// <summary>份序号（1 起，按份头段在文件内出现顺序）。</summary>
        public int Index { get; set; }

        /// <summary>该份头段声明的数据版本串（如 0.0.0）。</summary>
        public string DataVersion { get; set; }

        /// <summary>该份卡面图在文件内的偏移。</summary>
        public long FaceOffset { get; set; }

        /// <summary>该份卡面图字节数（头段声明值）。</summary>
        public long FaceSize { get; set; }

        /// <summary>该份的数据块目录（块名 / 版本 / 位置 / 大小）。</summary>
        public List<CardBlockInfo> Blocks { get; } = new List<CardBlockInfo>();
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

        /// <summary>kkpe 记录的 itemInfo 条数（-1 = 卡里没有 kkpe 条目）。</summary>
        public int KkpeItemCount { get; set; } = -1;

        /// <summary>kkpe 记录的 itemInfo 名字（去重，按出现次数降序；上限 200）。</summary>
        public List<SceneNameCount> PropNames { get; } = new List<SceneNameCount>();

        /// <summary>vnge_sssb 场景快照里的对象数（-1 = 无该条目）。</summary>
        public int SssbObjects { get; set; } = -1;

        /// <summary>vnge_sssb 场景快照里的相机对象数。</summary>
        public int SssbCameras { get; set; }

        /// <summary>内嵌角色数据的首张图（每份一张——该份角色卡的卡面图；由 AttachCharaFaces 按数据区图片清单装配）。</summary>
        public List<CardImageInfo> CharaFaces { get; } = new List<CardImageInfo>();

        /// <summary>内嵌角色数据明细（每份：数据版本 / 卡面图 / 数据块目录）。</summary>
        public List<SceneCharaData> CharaData { get; } = new List<SceneCharaData>();

        /// <summary>内嵌角色卡数据的卡面图偏移（份头段锚点扫出——内部用，不参与序列化）。</summary>
        internal List<long> CharaFaceOffsets { get; } = new List<long>();

        /// <summary>各份头段声明的脸图字节数（与偏移一一对应——内部用）。</summary>
        internal List<long> CharaFaceSizes { get; } = new List<long>();

        /// <summary>各份头段声明的数据版本串（与偏移一一对应——内部用）。</summary>
        internal List<string> CharaVersions { get; } = new List<string>();

        /// <summary>各份数据块目录在文件内的偏移（升序——内部用）。</summary>
        internal List<long> CharaLstOffsets { get; } = new List<long>();

        /// <summary>解析告警（失败必须可见）。</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// 按份头段扫出的卡面图偏移，从数据区图片清单里取对应的那张（每份一张 = 该份角色卡的卡面图）。
        /// 图片清单来自 CardDetail.Parse（同一遍数据区扫描）；偏移对不上出声（不静默丢弃）。
        /// </summary>
        public void AttachCharaFaces(IReadOnlyList<CardImageInfo> images)
        {
            CharaFaces.Clear();
            if (images == null || images.Count == 0 || CharaFaceOffsets.Count == 0)
            {
                return;
            }
            for (int k = 0; k < CharaFaceOffsets.Count; k = k + 1)
            {
                long off = CharaFaceOffsets[k];
                CardImageInfo hit = null;
                for (int i = 0; i < images.Count; i = i + 1)
                {
                    if (images[i].Offset == off)
                    {
                        hit = images[i];
                        break;
                    }
                }
                if (hit == null)
                {
                    Warnings.Add("第 " + (k + 1) + " 份卡面图（@" + off.ToString("N0") + " · 声明 "
                        + CharaFaceSizes[k].ToString("N0") + " 字节）未出现在数据区图片清单里");
                    continue;
                }
                CharaFaces.Add(hit);
            }
        }
    }

    /// <summary>
    /// 场景卡（sd）数据区深度分析——列插件数据条目（键 / 版本 / 值形态 / 字节数 / 摘要）、数内嵌角色卡数据份数、
    /// 读 kkpe 的道具 / 节点名与 vnge_sssb 的对象数。
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

        /// <summary>version 键锚点——fixstr(7) "version"。</summary>
        private static readonly byte[] VersionKey = { 0xA7, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E };

        /// <summary>kkpe 的 itemInfo 元素锚点。</summary>
        private static readonly byte[] ItemInfoMark = Encoding.ASCII.GetBytes("<itemInfo ");

        /// <summary>name 属性锚点。</summary>
        private static readonly byte[] NameMark = Encoding.ASCII.GetBytes("name=\"");

        /// <summary>vnge_sssb 快照里的对象别名键。</summary>
        private static readonly byte[] AliasMark = Encoding.ASCII.GetBytes("\"alias\":");

        /// <summary>vnge_sssb 快照里的相机对象键。</summary>
        private static readonly byte[] CameraMark = Encoding.ASCII.GetBytes("\"cameraNode\"");

        /// <summary>扫描缓冲区（1 MB）。</summary>
        private const int BufSize = 1 << 20;

        /// <summary>跨缓冲保留字节（覆盖键名回溯 31 字节 + 份头段锚点 33 字节 + 前瞻余量）。</summary>
        private const int CarrySize = 192;

        /// <summary>条目载荷读取上限（kkpe / vnge_sssb 的 XML 与 JSON）。</summary>
        private const long ValueReadCap = 4L * 1024 * 1024;

        /// <summary>kkpe 名字清单上限。</summary>
        private const int MaxPropNames = 200;

        /// <summary>读一张场景卡的插件条目、内嵌角色数据份数、kkpe 道具名与 vnge_sssb 对象数（path = 卡片文件；imageEnd = 图片区结束偏移）。</summary>
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
            var lstSeen = new HashSet<long>();
            var faceSeen = new HashSet<long>();
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
                                long lstAt = baseOff + i;
                                if (lstSeen.Add(lstAt))
                                {
                                    r.CharaDataCount = r.CharaDataCount + 1;
                                    r.CharaLstOffsets.Add(lstAt);
                                }
                            }
                        }

                        // 份头段锚点（内嵌角色卡数据头：int32 100 + 卡类型串 + 数据版本串 + int32 脸图长度 + PNG 签名）——记卡面图偏移
                        for (int i = 0; i <= total - 45; i = i + 1)
                        {
                            if (buf[i] != 0x64 || buf[i + 1] != 0x00 || buf[i + 2] != 0x00 || buf[i + 3] != 0x00)
                            {
                                continue;
                            }
                            int p = i + 4;
                            string cardType = Read7BitInBuf(buf, ref p, total);
                            string dataVer = Read7BitInBuf(buf, ref p, total);
                            if (cardType == null || dataVer == null || cardType.IndexOf("KoiKatu", StringComparison.Ordinal) < 0)
                            {
                                continue;
                            }
                            if (p + 12 > total)
                            {
                                continue;
                            }
                            long faceLen = (long)buf[p] | ((long)buf[p + 1] << 8) | ((long)buf[p + 2] << 16) | ((long)buf[p + 3] << 24);
                            int faceAt = p + 4;
                            if (faceLen <= 0 || buf[faceAt] != 0x89 || buf[faceAt + 1] != 0x50 || buf[faceAt + 2] != 0x4E || buf[faceAt + 3] != 0x47)
                            {
                                continue;
                            }
                            long faceOff = baseOff + faceAt;
                            if (faceSeen.Add(faceOff))
                            {
                                r.CharaFaceOffsets.Add(faceOff);
                                r.CharaFaceSizes.Add(faceLen);
                                r.CharaVersions.Add(dataVer);
                            }
                        }

                        for (int i = 0; i <= total - 96; i = i + 1)
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
                                if (item.ValueAt > 0)
                                {
                                    item.Version = ReadVersion(path, item.ValueAt + item.ValueLen, size);
                                }
                                r.Plugins.Add(item);
                            }
                        }

                        int newCarry = total < CarrySize ? total : CarrySize;
                        Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                        carry = newCarry;
                    }
                }

                // 按条目语义补摘要与明细（kkpe 的道具名 / vnge_sssb 的对象数）
                foreach (ScenePluginItem it in r.Plugins)
                {
                    if (it.ValueAt <= 0)
                    {
                        continue;
                    }
                    if (it.Key == "kkpe" && r.KkpeItemCount < 0)
                    {
                        ReadKkpe(path, it, r);
                    }
                    else if (it.Key == "vnge_sssb" && r.SssbObjects < 0)
                    {
                        ReadSssb(path, it, r);
                    }
                }

                // 内嵌角色数据明细——份头段（数据版本 / 卡面图）× 数据块目录 按出现顺序配对
                int pairs = r.CharaFaceOffsets.Count < r.CharaLstOffsets.Count ? r.CharaFaceOffsets.Count : r.CharaLstOffsets.Count;
                for (int k = 0; k < pairs; k = k + 1)
                {
                    SceneCharaData cd = new SceneCharaData
                    {
                        Index = k + 1,
                        DataVersion = k < r.CharaVersions.Count ? r.CharaVersions[k] : null,
                        FaceOffset = r.CharaFaceOffsets[k],
                        FaceSize = r.CharaFaceSizes[k]
                    };
                    List<CardBlockInfo> blocks = ReadBlockTable(path, r.CharaLstOffsets[k]);
                    if (blocks.Count == 0)
                    {
                        r.Warnings.Add("第 " + (k + 1) + " 份角色数据的块表未解析出条目（@" + r.CharaLstOffsets[k].ToString("N0") + "）");
                    }
                    cd.Blocks.AddRange(blocks);
                    r.CharaData.Add(cd);
                }
                if (r.CharaFaceOffsets.Count != r.CharaLstOffsets.Count)
                {
                    r.Warnings.Add("份头段 " + r.CharaFaceOffsets.Count + " 个 · 数据块目录 " + r.CharaLstOffsets.Count
                        + " 处——数量不等，明细按 " + pairs + " 份配对");
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
        /// <summary>读一份角色数据的数据块目录（lstInfo 数组——每项 map：name / version / pos / size）。</summary>
        private static List<CardBlockInfo> ReadBlockTable(string path, long lstAt)
        {
            var list = new List<CardBlockInfo>();
            byte[] buf = ReadValue(path, lstAt + LstInfoMark.Length, 64 * 1024, 64 * 1024);
            if (buf == null)
            {
                return list;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                return list;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                int keys;
                if (!cur.TryReadMapHeader(out keys))
                {
                    break;
                }
                CardBlockInfo b = new CardBlockInfo();
                bool ok = true;
                for (int k = 0; k < keys; k = k + 1)
                {
                    string key;
                    if (!cur.TryReadString(out key))
                    {
                        ok = false;
                        break;
                    }
                    if (key == "name" || key == "version")
                    {
                        string v;
                        if (!cur.TryReadString(out v))
                        {
                            ok = false;
                            break;
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
                            ok = false;
                            break;
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
                        ok = false;
                        break;
                    }
                }
                if (!ok)
                {
                    break;
                }
                if (b.Name != null && b.Name.Length > 0)
                {
                    list.Add(b);
                }
            }
            return list;
        }

        /// <summary>读 kkpe 的 sceneInfo XML——itemInfo 条数与名字清单（按出现次数降序）。</summary>
        private static void ReadKkpe(string path, ScenePluginItem item, SceneInfoResult r)
        {
            byte[] xml = ReadValue(path, item.ValueAt, item.ValueLen, ValueReadCap);
            if (xml == null)
            {
                r.KkpeItemCount = 0;
                r.Warnings.Add("kkpe 条目读取失败（偏移 " + item.Offset + "）");
                return;
            }
            r.KkpeItemCount = CountAt(xml, ItemInfoMark);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i <= xml.Length - NameMark.Length; i = i + 1)
            {
                if (xml[i] != NameMark[0] || !MatchAt(xml, i, NameMark))
                {
                    continue;
                }
                int start = i + NameMark.Length;
                int p = start;
                while (p < xml.Length && xml[p] != 0x22)
                {
                    p = p + 1;
                }
                if (p <= start)
                {
                    continue;
                }
                string name = Encoding.UTF8.GetString(xml, start, p - start);
                int cur;
                if (counts.TryGetValue(name, out cur))
                {
                    counts[name] = cur + 1;
                }
                else if (counts.Count < MaxPropNames)
                {
                    counts[name] = 1;
                }
            }
            foreach (KeyValuePair<string, int> kv in counts)
            {
                r.PropNames.Add(new SceneNameCount { Name = kv.Key, Count = kv.Value });
            }
            r.PropNames.Sort(delegate (SceneNameCount a, SceneNameCount b)
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            item.Summary = "itemInfo " + r.KkpeItemCount.ToString("N0") + " 条 · 名字 " + r.PropNames.Count + " 种";
        }

        /// <summary>读 vnge_sssb 的场景快照 JSON——对象数与相机数。</summary>
        private static void ReadSssb(string path, ScenePluginItem item, SceneInfoResult r)
        {
            byte[] json = ReadValue(path, item.ValueAt, item.ValueLen, ValueReadCap);
            if (json == null)
            {
                r.SssbObjects = 0;
                r.Warnings.Add("vnge_sssb 条目读取失败（偏移 " + item.Offset + "）");
                return;
            }
            r.SssbObjects = CountAt(json, AliasMark);
            r.SssbCameras = CountAt(json, CameraMark);
            item.Summary = "快照对象 " + r.SssbObjects + " 个（相机 " + r.SssbCameras + "）";
        }

        /// <summary>解析 0x92 00 之后的条目值形态；不是插件条目形态返回 null。</summary>
        private static ScenePluginItem ParseItem(byte[] buf, int at92, long at, string key, SceneInfoResult r)
        {
            int shapeAt = at92 + 2;
            if (buf[shapeAt] == 0x81 && MatchAt(buf, shapeAt + 1, SceneInfoMark))
            {
                long len;
                int headLen = ReadStrHeader(buf, shapeAt + 1 + SceneInfoMark.Length, out len);
                if (headLen < 0)
                {
                    r.Warnings.Add(key + " 的 sceneInfo 字符串头未识别（偏移 " + at + "）");
                    return new ScenePluginItem { Key = key, Shape = "sceneInfo(XML) · 头未识别", Bytes = 0, Offset = at };
                }
                int valueAt = shapeAt + 1 + SceneInfoMark.Length + headLen;
                return new ScenePluginItem
                {
                    Key = key,
                    Shape = "sceneInfo(XML)",
                    Bytes = len,
                    Offset = at,
                    ValueAt = at + (valueAt - at92),
                    ValueLen = len
                };
            }
            if (buf[shapeAt] == 0x82 && MatchAt(buf, shapeAt + 1, MainMark))
            {
                int p = shapeAt + 1 + MainMark.Length;
                byte b = buf[p];
                if (b >= 0xA0 && b <= 0xBF)
                {
                    return new ScenePluginItem { Key = key, Shape = "main(短串)", Bytes = b - 0xA0, Offset = at, ValueAt = at + (p + 1 - at92), ValueLen = b - 0xA0 };
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
                int headLen = ReadStrHeader(buf, p, out len);
                if (headLen >= 0)
                {
                    return new ScenePluginItem
                    {
                        Key = key,
                        Shape = "main(串)",
                        Bytes = len,
                        Offset = at,
                        ValueAt = at + (p + headLen - at92),
                        ValueLen = len
                    };
                }
                return new ScenePluginItem { Key = key, Shape = "main(0x" + b.ToString("X2") + ")", Bytes = 0, Offset = at };
            }
            return null;
        }

        /// <summary>读条目值之后紧跟的 version 串（`A7 "version" 串`）；无则空串。</summary>
        private static string ReadVersion(string path, long at, long fileSize)
        {
            if (at + 2 > fileSize)
            {
                return "";
            }
            byte[] b = new byte[64];
            int n;
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.Position = at;
                    n = ReadFull(fs, b, b.Length);
                }
            }
            catch (IOException)
            {
                return "";
            }
            if (n < 10)
            {
                return "";
            }
            for (int skip = 0; skip <= 1; skip = skip + 1)
            {
                if (!MatchAt(b, skip, VersionKey))
                {
                    continue;
                }
                int p = skip + VersionKey.Length;
                string s = ReadStrAt(b, n, ref p);
                if (s != null)
                {
                    return s;
                }
            }
            return "";
        }

        /// <summary>读文件的一段字节（上限 cap；读不到返回 null）。</summary>
        private static byte[] ReadValue(string path, long at, long len, long cap)
        {
            if (at <= 0 || len <= 0)
            {
                return null;
            }
            int take = (int)(len > cap ? cap : len);
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (at >= fs.Length)
                    {
                        return null;
                    }
                    byte[] buf = new byte[take];
                    fs.Position = at;
                    int n = ReadFull(fs, buf, take);
                    if (n == take)
                    {
                        return buf;
                    }
                    byte[] cut = new byte[n];
                    Array.Copy(buf, cut, n);
                    return cut;
                }
            }
            catch (IOException)
            {
                return null;
            }
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
            if (b == 0xD9 && p + 2 <= buf.Length)
            {
                len = buf[p + 1];
                return 2;
            }
            if (b == 0xDA && p + 3 <= buf.Length)
            {
                len = (buf[p + 1] << 8) | buf[p + 2];
                return 3;
            }
            if (b == 0xDB && p + 5 <= buf.Length)
            {
                len = ((long)buf[p + 1] << 24) | ((long)buf[p + 2] << 16) | ((long)buf[p + 3] << 8) | buf[p + 4];
                return 5;
            }
            return -1;
        }

        /// <summary>读缓冲里 p 处的 MessagePack 字符串（含头）；失败返回 null。</summary>
        private static string ReadStrAt(byte[] buf, int end, ref int p)
        {
            long len;
            int headLen = ReadStrHeader(buf, p, out len);
            if (headLen < 0 || p + headLen + len > end)
            {
                return null;
            }
            string s = Encoding.UTF8.GetString(buf, p + headLen, (int)len);
            p = (int)(p + headLen + len);
            return s;
        }
        /// <summary>读缓冲里 p 处的 7bit 长度前缀 UTF-8 串（BinaryWriter.Write(string) 形态）；失败返回 null。</summary>
        private static string Read7BitInBuf(byte[] buf, ref int p, int end)
        {
            long len = 0;
            int shift = 0;
            while (true)
            {
                if (p >= end)
                {
                    return null;
                }
                byte b = buf[p];
                p = p + 1;
                len = len | ((long)(b & 0x7F) << shift);
                if ((b & 0x80) == 0)
                {
                    break;
                }
                shift = shift + 7;
                if (shift > 28)
                {
                    return null;
                }
            }
            if (len <= 0 || p + len > end)
            {
                return null;
            }
            string s = Encoding.UTF8.GetString(buf, p, (int)len);
            p = (int)(p + len);
            return s;
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

        /// <summary>统计 needle 在缓冲里出现的次数。</summary>
        private static int CountAt(byte[] buf, byte[] needle)
        {
            int count = 0;
            for (int i = 0; i <= buf.Length - needle.Length; i = i + 1)
            {
                if (buf[i] == needle[0] && MatchAt(buf, i, needle))
                {
                    count = count + 1;
                    i = i + needle.Length - 1;
                }
            }
            return count;
        }

        /// <summary>在缓冲区指定位置匹配字节序列。</summary>
        private static bool MatchAt(byte[] buf, int at, byte[] pattern)
        {
            if (at < 0 || at + pattern.Length > buf.Length)
            {
                return false;
            }
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
