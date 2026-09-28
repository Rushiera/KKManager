using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KKManager.Probe
{
    /// <summary>探针：一条场景插件数据条目。</summary>
    internal class SdItemProbe
    {
        /// <summary>插件键名（如 timeline / kkpe / vnge_actor）。</summary>
        public string Key;

        /// <summary>值形态（sceneInfo(XML) / main(串) / main(map N) …）。</summary>
        public string Shape;

        /// <summary>载荷字节数（0 = 未解析）。</summary>
        public long Bytes;

        /// <summary>条目值之后的 version 串（无则空）。</summary>
        public string Version;

        /// <summary>条目偏移。</summary>
        public long Offset;

        /// <summary>字符串值起点（非字符串型 -1）。</summary>
        public long ValueAt = -1;

        /// <summary>字符串值字节数。</summary>
        public long ValueLen;
    }

    /// <summary>探针：单张场景卡的侦察结果。</summary>
    internal class SdCardProbe
    {
        /// <summary>卡片路径。</summary>
        public string File;

        /// <summary>文件字节数。</summary>
        public long Size;

        /// <summary>数据区起点。</summary>
        public long DataStart;

        /// <summary>场景版本串。</summary>
        public string Version;

        /// <summary>错误（非场景卡 / 读取失败）。</summary>
        public string Error;

        /// <summary>插件条目。</summary>
        public List<SdItemProbe> Items = new List<SdItemProbe>();

        /// <summary>内嵌角色卡数据份数（lstInfo 命中）。</summary>
        public int CharaData;

        /// <summary>是否有 timeline 条目。</summary>
        public bool HasTimeline;

        /// <summary>timeline 时长（秒）。</summary>
        public double Duration;

        /// <summary>timeScale（1 = 原速）。</summary>
        public double TimeScale = 1;

        /// <summary>关键帧数。</summary>
        public int Keyframes;

        /// <summary>插值组数。</summary>
        public int Groups;

        /// <summary>最长关键帧时刻（秒）。</summary>
        public double MaxTime;

        /// <summary>sceneInfo XML 字节数。</summary>
        public long XmlLen;

        /// <summary>XML 超出读取上限。</summary>
        public bool XmlTruncated;

        /// <summary>插值组名（样本，前 12 个）。</summary>
        public List<string> GroupNames = new List<string>();

        /// <summary>kkpe 的 itemInfo 条数（-1 = 无该条目）。</summary>
        public int KkpeItems = -1;

        /// <summary>kkpe 的 itemInfo 名字（去重样本，前 40 个）。</summary>
        public List<string> KkpeNames = new List<string>();

        /// <summary>vnge_sssb 顶层对象数（-1 = 无该条目）。</summary>
        public int SssbObjects = -1;

        /// <summary>vnge_sssb 相机对象数。</summary>
        public int SssbCamera;

        /// <summary>vnge_sssb 角色对象数。</summary>
        public int SssbChara;

        /// <summary>vnge_sssb 道具对象数。</summary>
        public int SssbItem;

        /// <summary>vnge_sssb 灯光对象数。</summary>
        public int SssbLight;

        /// <summary>vnge_sssb 载荷字节数。</summary>
        public long SssbLen;
    }

    internal static partial class Program
    {
        /// <summary>场景卡批量侦察——扫目录下全部 .png 的场景卡数据（插件条目 / 内嵌角色 / timeline / kkpe / VNGE）。</summary>
        private static int SdScanCommand(string dir, string outPath, int maxMb)
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine("目录不存在: " + dir);
                return 2;
            }
            string[] files = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            var cards = new List<SdCardProbe>();
            int notScene = 0;
            int failed = 0;
            long bytes = 0;
            for (int i = 0; i < files.Length; i = i + 1)
            {
                SdCardProbe c = SdAnalyzeOne(files[i], maxMb);
                if (c.Error != null)
                {
                    if (c.Error == "不是场景卡")
                    {
                        notScene = notScene + 1;
                        continue;
                    }
                    failed = failed + 1;
                }
                bytes = bytes + c.Size;
                cards.Add(c);
                Console.WriteLine("[" + (i + 1) + "/" + files.Length + "] " + Path.GetFileName(files[i])
                    + (c.Error == null ? (" 条目 " + c.Items.Count + " · 角色 " + c.CharaData
                        + (c.HasTimeline ? (" · timeline " + FmtNum(c.Duration) + "s/" + c.Keyframes + "帧") : " · 无 timeline")) : (" 异常 " + c.Error)));
            }

            string parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# 场景卡批量侦察 —— " + dir);
                sw.WriteLine("# 枚举 " + files.Length.ToString("N0") + " 个 .png · 场景卡 " + cards.Count.ToString("N0")
                    + " · 非场景卡 " + notScene.ToString("N0") + " · 异常 " + failed.ToString("N0")
                    + " · 场景卡合计 " + bytes.ToString("N0") + " 字节");
                sw.WriteLine();

                // ① 插件键频次与版本
                var keyOrder = new List<string>();
                var keyCount = new Dictionary<string, int>();
                var keyVers = new Dictionary<string, HashSet<string>>();
                var shapeCount = new Dictionary<string, int>();
                foreach (SdCardProbe c in cards)
                {
                    foreach (SdItemProbe it in c.Items)
                    {
                        if (!keyCount.ContainsKey(it.Key))
                        {
                            keyCount[it.Key] = 0;
                            keyVers[it.Key] = new HashSet<string>();
                            keyOrder.Add(it.Key);
                        }
                        keyCount[it.Key] = keyCount[it.Key] + 1;
                        if (!string.IsNullOrEmpty(it.Version))
                        {
                            keyVers[it.Key].Add(it.Version);
                        }
                        string sk = it.Key + " → " + it.Shape;
                        if (!shapeCount.ContainsKey(sk))
                        {
                            shapeCount[sk] = 0;
                        }
                        shapeCount[sk] = shapeCount[sk] + 1;
                    }
                }
                sw.WriteLine("## ① 插件条目键频次（" + keyOrder.Count.ToString("N0") + " 种）");
                sw.WriteLine("| 键 | 出现卡数 | 版本串（样本） | 值形态（样本） |");
                sw.WriteLine("|:--|--:|:--|:--|");
                var sorted = new List<string>(keyOrder);
                sorted.Sort(delegate (string a, string b) { return keyCount[b].CompareTo(keyCount[a]); });
                foreach (string k in sorted)
                {
                    var vers = new List<string>(keyVers[k]);
                    vers.Sort(StringComparer.Ordinal);
                    string shapeSample = "";
                    foreach (KeyValuePair<string, int> kv in shapeCount)
                    {
                        if (kv.Key.StartsWith(k + " → ", StringComparison.Ordinal))
                        {
                            shapeSample = shapeSample.Length == 0 ? kv.Key.Substring(k.Length + 3) : shapeSample;
                        }
                    }
                    sw.WriteLine("| `" + k + "` | " + keyCount[k] + " | " + (vers.Count == 0 ? "—" : string.Join(" / ", vers)) + " | " + shapeSample + " |");
                }
                sw.WriteLine();

                // ② 内嵌角色数据份数分布
                var charaDist = new Dictionary<int, int>();
                foreach (SdCardProbe c in cards)
                {
                    if (!charaDist.ContainsKey(c.CharaData))
                    {
                        charaDist[c.CharaData] = 0;
                    }
                    charaDist[c.CharaData] = charaDist[c.CharaData] + 1;
                }
                sw.WriteLine("## ② 内嵌角色卡数据份数分布");
                var ck = new List<int>(charaDist.Keys);
                ck.Sort();
                foreach (int k in ck)
                {
                    sw.WriteLine("- " + k + " 份：" + charaDist[k] + " 张");
                }
                sw.WriteLine();

                // ③ timeline 画像
                int tlYes = 0;
                int tlEmpty = 0;
                int tlNo = 0;
                int tlFrames = 0;
                int tlGroups = 0;
                var groupTop = new Dictionary<string, int>();
                foreach (SdCardProbe c in cards)
                {
                    if (!c.HasTimeline)
                    {
                        tlNo = tlNo + 1;
                        continue;
                    }
                    if (c.Keyframes == 0)
                    {
                        tlEmpty = tlEmpty + 1;
                    }
                    else
                    {
                        tlYes = tlYes + 1;
                    }
                    tlFrames = tlFrames + c.Keyframes;
                    tlGroups = tlGroups + c.Groups;
                    foreach (string g in c.GroupNames)
                    {
                        if (!groupTop.ContainsKey(g))
                        {
                            groupTop[g] = 0;
                        }
                        groupTop[g] = groupTop[g] + 1;
                    }
                }
                sw.WriteLine("## ③ timeline 画像");
                sw.WriteLine("- 有实际时间轴 " + tlYes + " · 空轴 " + tlEmpty + " · 无 timeline 条目 " + tlNo
                    + " · 帧数合计 " + tlFrames.ToString("N0") + " · 组数合计 " + tlGroups.ToString("N0"));
                var gs = new List<string>(groupTop.Keys);
                gs.Sort(delegate (string a, string b) { return groupTop[b].CompareTo(groupTop[a]); });
                sw.WriteLine("- 插值组名 Top（出现卡数）：");
                for (int i = 0; i < gs.Count && i < 40; i = i + 1)
                {
                    sw.WriteLine("    - `" + gs[i] + "` × " + groupTop[gs[i]]);
                }
                sw.WriteLine();

                // ④ kkpe / vnge_sssb
                int kkpeCards = 0;
                int kkpeMax = 0;
                int kkpeSum = 0;
                int sssbCards = 0;
                int sssbMax = 0;
                int sssbCam = 0;
                int sssbChara = 0;
                int sssbItem = 0;
                int sssbLight = 0;
                var kkpeNameTop = new Dictionary<string, int>();
                foreach (SdCardProbe c in cards)
                {
                    if (c.KkpeItems >= 0)
                    {
                        kkpeCards = kkpeCards + 1;
                        kkpeSum = kkpeSum + c.KkpeItems;
                        if (c.KkpeItems > kkpeMax)
                        {
                            kkpeMax = c.KkpeItems;
                        }
                        foreach (string nm in c.KkpeNames)
                        {
                            if (!kkpeNameTop.ContainsKey(nm))
                            {
                                kkpeNameTop[nm] = 0;
                            }
                            kkpeNameTop[nm] = kkpeNameTop[nm] + 1;
                        }
                    }
                    if (c.SssbObjects >= 0)
                    {
                        sssbCards = sssbCards + 1;
                        if (c.SssbObjects > sssbMax)
                        {
                            sssbMax = c.SssbObjects;
                        }
                        sssbCam = sssbCam + c.SssbCamera;
                        sssbChara = sssbChara + c.SssbChara;
                        sssbItem = sssbItem + c.SssbItem;
                        sssbLight = sssbLight + c.SssbLight;
                    }
                }
                sw.WriteLine("## ④ kkpe / vnge_sssb");
                sw.WriteLine("- kkpe 条目 " + kkpeCards + " 张（itemInfo 合计 " + kkpeSum + " · 单卡最多 " + kkpeMax + "）");
                var kn = new List<string>(kkpeNameTop.Keys);
                kn.Sort(delegate (string a, string b) { return kkpeNameTop[b].CompareTo(kkpeNameTop[a]); });
                sw.WriteLine("- kkpe 名字样本 Top（出现卡数）：");
                for (int i = 0; i < kn.Count && i < 30; i = i + 1)
                {
                    sw.WriteLine("    - `" + kn[i] + "` × " + kkpeNameTop[kn[i]]);
                }
                sw.WriteLine("- vnge_sssb 条目 " + sssbCards + " 张（对象数最多 " + sssbMax
                    + " · 相机 " + sssbCam + " / 角色 " + sssbChara + " / 道具 " + sssbItem + " / 灯光 " + sssbLight + "）");
                sw.WriteLine();

                // ⑤ 每卡明细
                sw.WriteLine("## ⑤ 每卡明细");
                sw.WriteLine("| 卡片 | 大小 | 条目 | 键（版本） | 内嵌角色 | timeline |");
                sw.WriteLine("|:--|--:|--:|:--|--:|:--|");
                foreach (SdCardProbe c in cards)
                {
                    var keys = new List<string>();
                    foreach (SdItemProbe it in c.Items)
                    {
                        keys.Add(it.Key + (string.IsNullOrEmpty(it.Version) ? "" : "(" + it.Version + ")"));
                    }
                    string tl = !c.HasTimeline ? "无" : (c.Keyframes == 0 ? "空轴" : (FmtNum(c.Duration) + "s · " + c.Keyframes + "帧 · " + c.Groups + "组 · max " + FmtNum(c.MaxTime) + "s"));
                    sw.WriteLine("| " + Path.GetFileName(c.File) + " | " + c.Size.ToString("N0") + " | " + c.Items.Count + " | " + string.Join(" · ", keys) + " | " + c.CharaData + " | " + tl + " |");
                    if (c.Error != null)
                    {
                        sw.WriteLine("| ↑ 异常 | | | " + c.Error + " | | |");
                    }
                }
            }
            Console.WriteLine("sdscan: " + outPath);
            return 0;
        }

        /// <summary>侦察单张卡的场景数据（非场景卡返回 Error）。</summary>
        private static SdCardProbe SdAnalyzeOne(string path, int maxMb)
        {
            var r = new SdCardProbe { File = path };
            try
            {
                var fi = new FileInfo(path);
                r.Size = fi.Length;
                long len = fi.Length;
                byte[] sceneInfoNeedle = { 0xA9, 0x73, 0x63, 0x65, 0x6E, 0x65, 0x49, 0x6E, 0x66, 0x6F };
                byte[] mainNeedle = { 0xA4, 0x6D, 0x61, 0x69, 0x6E };
                byte[] lstNeedle = { 0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F };
                long dataStart;
                using (var fs = File.OpenRead(path))
                {
                    dataStart = ProbeFindPngEnd(fs, len);
                    if (dataStart < 0)
                    {
                        r.Error = "非卡片 PNG（无 IEND）";
                        return r;
                    }
                    r.DataStart = dataStart;
                    fs.Position = dataStart;
                    var br = new BinaryReader(fs);
                    string first = Read7BitString(br);
                    if (first == null || !IsVersionLike(first))
                    {
                        r.Error = "不是场景卡";
                        return r;
                    }
                    r.Version = first;
                }

                var seen = new HashSet<long>();
                using (var fs = File.OpenRead(path))
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
                                r.CharaData = r.CharaData + 1;
                            }
                        }

                        for (int i = 64; i <= total - 512; i = i + 1)
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
                            string key = SdReadKey(buf, i);
                            if (key == null)
                            {
                                continue;
                            }
                            int shapeStart = i + 3;
                            var item = new SdItemProbe { Key = key, Offset = at };
                            if (buf[i + 2] == 0x81 && MatchAt(buf, shapeStart, sceneInfoNeedle))
                            {
                                int sh = shapeStart + sceneInfoNeedle.Length;
                                long vl;
                                int hl = SdStrHeader(buf, sh, out vl);
                                if (hl > 0)
                                {
                                    item.Shape = "sceneInfo(XML)";
                                    item.Bytes = vl;
                                    item.ValueAt = at + (sh + hl - i);
                                    item.ValueLen = vl;
                                }
                                else
                                {
                                    item.Shape = "sceneInfo(头未识别)";
                                }
                            }
                            else if (buf[i + 2] == 0x82 && MatchAt(buf, shapeStart, mainNeedle))
                            {
                                int sh = shapeStart + mainNeedle.Length;
                                byte hb = buf[sh];
                                long vl;
                                int hl = SdStrHeader(buf, sh, out vl);
                                if (hb >= 0x80 && hb <= 0x8F)
                                {
                                    item.Shape = "main(map " + (hb - 0x80) + " 键)";
                                }
                                else if (hb >= 0x90 && hb <= 0x9F)
                                {
                                    item.Shape = "main(数组 " + (hb - 0x90) + " 项)";
                                }
                                else if (hl > 0)
                                {
                                    item.Shape = "main(串)";
                                    item.Bytes = vl;
                                    item.ValueAt = at + (sh + hl - i);
                                    item.ValueLen = vl;
                                }
                                else
                                {
                                    item.Shape = "main(0x" + hb.ToString("X2") + ")";
                                }
                            }
                            else
                            {
                                continue;
                            }
                            if (item.ValueAt > 0)
                            {
                                item.Version = SdReadVersion(path, item.ValueAt + item.ValueLen, len);
                            }
                            r.Items.Add(item);
                        }

                        int newCarry = total < 64 ? total : 64;
                        Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                        carry = newCarry;
                    }
                }

                // timeline 深度 + kkpe 条数 + vnge_sssb 对象数
                foreach (SdItemProbe it in r.Items)
                {
                    if (it.ValueAt <= 0)
                    {
                        continue;
                    }
                    if (it.Key == "timeline" && !r.HasTimeline)
                    {
                        r.HasTimeline = true;
                        r.XmlLen = it.ValueLen;
                        SdTimelineInfo(path, it.ValueAt, it.ValueLen, maxMb, r);
                    }
                    else if (it.Key == "kkpe" && r.KkpeItems < 0)
                    {
                        byte[] xml = SdReadValue(path, it.ValueAt, it.ValueLen, 4L * 1024 * 1024);
                        r.KkpeItems = xml == null ? 0 : CountNeedleIn(xml, Encoding.ASCII.GetBytes("<itemInfo "));
                        if (xml != null)
                        {
                            byte[] nm = Encoding.ASCII.GetBytes("name=\"");
                            for (int p = 0; p <= xml.Length - nm.Length && r.KkpeNames.Count < 40; p = p + 1)
                            {
                                if (xml[p] != nm[0] || !MatchAt(xml, p, nm))
                                {
                                    continue;
                                }
                                int start = p + nm.Length;
                                int q = start;
                                while (q < xml.Length && xml[q] != 0x22)
                                {
                                    q = q + 1;
                                }
                                string name = Encoding.UTF8.GetString(xml, start, q - start);
                                if (name.Length > 0 && !r.KkpeNames.Contains(name))
                                {
                                    r.KkpeNames.Add(name);
                                }
                            }
                        }
                    }
                    else if (it.Key == "vnge_sssb" && r.SssbObjects < 0)
                    {
                        r.SssbLen = it.ValueLen;
                        byte[] json = SdReadValue(path, it.ValueAt, it.ValueLen, 4L * 1024 * 1024);
                        if (json == null)
                        {
                            r.SssbObjects = 0;
                        }
                        else
                        {
                            // 顶层对象 = `"alias":` 出现次数（每个 Studio 对象一处）
                            r.SssbObjects = CountNeedleIn(json, Encoding.ASCII.GetBytes("\"alias\":"));
                            r.SssbCamera = CountNeedleIn(json, Encoding.ASCII.GetBytes("\"cameraNode\""));
                            r.SssbChara = CountNeedleIn(json, Encoding.ASCII.GetBytes("\"charaNode\""));
                            r.SssbItem = CountNeedleIn(json, Encoding.ASCII.GetBytes("\"itemNode\""));
                            r.SssbLight = CountNeedleIn(json, Encoding.ASCII.GetBytes("\"lightNode\""));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + " · " + ex.Message;
            }
            return r;
        }

        /// <summary>读 timeline 的 sceneInfo XML——时长 / 缩放 / 关键帧 / 插值组 / 最长关键帧 / 组名样本。</summary>
        private static void SdTimelineInfo(string path, long at, long len, int maxMb, SdCardProbe r)
        {
            long cap = maxMb > 0 ? (long)maxMb * 1024 * 1024 : 16L * 1024 * 1024;
            byte[] xml = SdReadValue(path, at, len, cap);
            if (xml == null)
            {
                return;
            }
            r.XmlTruncated = len > xml.Length;
            int n = xml.Length;
            byte[] durNeedle = Encoding.ASCII.GetBytes("duration=\"");
            byte[] scaleNeedle = Encoding.ASCII.GetBytes("timeScale=\"");
            byte[] kfNeedle = Encoding.ASCII.GetBytes("<keyframe ");
            byte[] grpNeedle = Encoding.ASCII.GetBytes("<interpolableGroup ");
            byte[] nameNeedle = Encoding.ASCII.GetBytes("name=\"");
            byte[] timeNeedle = Encoding.ASCII.GetBytes("time=\"");

            string dtext = SdQuotedText(xml, n, durNeedle, 0);
            double dur;
            if (dtext != null && double.TryParse(dtext, NumberStyles.Float, CultureInfo.InvariantCulture, out dur))
            {
                r.Duration = dur;
            }
            string stext = SdQuotedText(xml, n, scaleNeedle, 0);
            double scale;
            if (stext != null && double.TryParse(stext, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0)
            {
                r.TimeScale = scale;
            }
            r.Keyframes = CountNeedleIn(xml, kfNeedle);
            r.Groups = CountNeedleIn(xml, grpNeedle);
            double best = 0;
            for (int i = 0; i <= n - timeNeedle.Length; i = i + 1)
            {
                if (xml[i] != timeNeedle[0] || !MatchAt(xml, i, timeNeedle))
                {
                    continue;
                }
                string t = SdQuotedText(xml, n, timeNeedle, i);
                double v;
                if (t != null && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > best)
                {
                    best = v;
                }
            }
            r.MaxTime = best;
            int firstGroup = IndexOf(xml, grpNeedle);
            if (firstGroup < 0)
            {
                firstGroup = 0;
            }
            for (int i = firstGroup; i <= n - nameNeedle.Length && r.GroupNames.Count < 200; i = i + 1)
            {
                if (xml[i] != nameNeedle[0] || !MatchAt(xml, i, nameNeedle))
                {
                    continue;
                }
                string name = SdQuotedText(xml, n, nameNeedle, i);
                if (name != null && !r.GroupNames.Contains(name))
                {
                    r.GroupNames.Add(name);
                }
            }
        }

        /// <summary>读文件的一段字节（上限 cap；读不到返回 null）。</summary>
        private static byte[] SdReadValue(string path, long at, long len, long cap)
        {
            if (at <= 0 || len <= 0)
            {
                return null;
            }
            int take = (int)(len > cap ? cap : len);
            byte[] buf = new byte[take];
            using (var fs = File.OpenRead(path))
            {
                if (at >= fs.Length)
                {
                    return null;
                }
                fs.Position = at;
                int n = ReadFull(fs, buf, take);
                if (n < take)
                {
                    byte[] cut = new byte[n];
                    Array.Copy(buf, cut, n);
                    return cut;
                }
            }
            return buf;
        }

        /// <summary>读条目值之后紧跟的 version 串（`A7 "version" 串`）；无则 null。</summary>
        private static string SdReadVersion(string path, long at, long fileLen)
        {
            if (at + 2 > fileLen)
            {
                return null;
            }
            byte[] b = new byte[64];
            using (var fs = File.OpenRead(path))
            {
                fs.Position = at;
                int n = ReadFull(fs, b, b.Length);
                if (n < 2)
                {
                    return null;
                }
                for (int skip = 0; skip <= 1; skip = skip + 1)
                {
                    int p = skip;
                    if (p + 9 > n || b[p] != 0xA7 || b[p + 1] != (byte)'v')
                    {
                        continue;
                    }
                    if (Encoding.ASCII.GetString(b, p + 1, 7) != "version")
                    {
                        continue;
                    }
                    p = p + 8;
                    return SdReadStr(b, ref p, n);
                }
            }
            return null;
        }

        /// <summary>读 MessagePack 字符串头，返回头字节数；未识别返回 -1。</summary>
        private static int SdStrHeader(byte[] buf, int p, out long len)
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

        /// <summary>读 MessagePack 字符串（含头）。</summary>
        private static string SdReadStr(byte[] buf, ref int p, int end)
        {
            long len;
            int hl = SdStrHeader(buf, p, out len);
            if (hl < 0 || p + hl + len > end)
            {
                return null;
            }
            string s = Encoding.UTF8.GetString(buf, p + hl, (int)len);
            p = (int)(p + hl + len);
            return s;
        }

        /// <summary>从 0x92 位置往前回溯 fixstr 键名（探针版）。</summary>
        private static string SdReadKey(byte[] buf, int at92)
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
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
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
        private static int CountNeedleIn(byte[] buf, byte[] needle)
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

        /// <summary>找 needle 首次出现位置；无则 -1。</summary>
        private static int IndexOf(byte[] buf, byte[] needle)
        {
            for (int i = 0; i <= buf.Length - needle.Length; i = i + 1)
            {
                if (buf[i] == needle[0] && MatchAt(buf, i, needle))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>读 needle 之后带引号的文本（UTF-8 解码；fromAt &gt; 0 时从该处 needle 起读）；无则 null。</summary>
        private static string SdQuotedText(byte[] buf, int n, byte[] needle, int fromAt)
        {
            for (int i = fromAt; i <= n - needle.Length; i = i + 1)
            {
                if (buf[i] != needle[0] || !MatchAt(buf, i, needle))
                {
                    continue;
                }
                int start = i + needle.Length;
                int p = start;
                while (p < n && buf[p] != 0x22)
                {
                    p = p + 1;
                }
                return Encoding.UTF8.GetString(buf, start, p - start);
            }
            return null;
        }

        /// <summary>数值文本（去掉多余零）。</summary>
        private static string FmtNum(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
