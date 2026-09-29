using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace KKManager.Probe
{
    /// <summary>探针：timeline 轨道视图侦察——sceneInfo XML 的组树 / 轨道清单 / id 族 / 时刻分布。</summary>
    internal static partial class Program
    {
        /// <summary>timeline 条目的 MessagePack 键锚点——fixstr8 "timeline"。</summary>
        private static readonly byte[] TlKeyAnchor = { 0xA8, 0x74, 0x69, 0x6D, 0x65, 0x6C, 0x69, 0x6E, 0x65 };

        /// <summary>tlscan &lt;card.png&gt; &lt;out.txt&gt;——把 sceneInfo XML 解析成轨道视图所需的结构摘要。</summary>
        private static int TlScanCommand(string card, string outPath)
        {
            if (!File.Exists(card))
            {
                Console.Error.WriteLine("卡片不存在: " + card);
                return 2;
            }
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("# timeline 轨道侦察 —— " + card);
                TlScanOne(card, sw);
            }
            Console.WriteLine("tlscan: " + outPath);
            return 0;
        }

        /// <summary>单张卡的轨道侦察——定位 XML → 解析 → 组树 / 轨道 / 统计。</summary>
        private static void TlScanOne(string card, StreamWriter sw)
        {
            // [段1] 定位 timeline 条目与 XML 区间
            FileInfo fi = new FileInfo(card);
            sw.WriteLine("- 大小 " + fi.Length.ToString("N0") + " 字节");
            long hit = TlFindAnchor(card);
            if (hit < 0)
            {
                sw.WriteLine("- timeline 键零命中");
                return;
            }
            sw.WriteLine("- timeline 键 @" + hit.ToString("N0") + "（距文件尾 " + (fi.Length - hit).ToString("N0") + " 字节）");
            long xmlAt;
            int xmlLen;
            if (!TlReadXmlSpan(card, hit, out xmlAt, out xmlLen))
            {
                sw.WriteLine("- XML 区间未识别");
                return;
            }
            sw.WriteLine("- XML @" + xmlAt.ToString("N0") + " · " + xmlLen.ToString("N0") + " 字节");

            // [段2] 解析 XML
            string text = TlReadText(card, xmlAt, xmlLen);
            if (text == null)
            {
                sw.WriteLine("- XML 读取失败");
                return;
            }
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (Exception ex)
            {
                sw.WriteLine("- XML 解析失败：" + ex.GetType().Name + " · " + ex.Message);
                return;
            }
            XElement root = doc.Root;
            if (root == null)
            {
                sw.WriteLine("- XML 无根元素");
                return;
            }
            sw.WriteLine("- root: <" + root.Name.LocalName + "> " + TlAttrText(root));

            // [段3] 组树
            sw.WriteLine();
            sw.WriteLine("## 组树（interpolableGroup 层级，最多 400 节点）");
            int printed = 0;
            TlDumpGroups(root, 0, sw, ref printed);
            if (printed >= 400)
            {
                sw.WriteLine("- （组树输出截断）");
            }

            // [段4] 轨道清单与统计
            List<XElement> tracks = new List<XElement>();
            foreach (XElement t in root.Descendants("interpolable"))
            {
                tracks.Add(t);
            }
            sw.WriteLine();
            sw.WriteLine("## 轨道（interpolable）共 " + tracks.Count.ToString("N0") + " 条 · 清单最多 200 行");
            Dictionary<string, int> idCount = new Dictionary<string, int>();
            Dictionary<string, int> aliasCount = new Dictionary<string, int>();
            Dictionary<string, int> ownerCount = new Dictionary<string, int>();
            Dictionary<string, int> pathHeadCount = new Dictionary<string, int>();
            Dictionary<double, int> timeCount = new Dictionary<double, int>();
            int enabledTrue = 0;
            int multiFrame = 0;
            int shown = 0;
            for (int i = 0; i < tracks.Count; i = i + 1)
            {
                XElement t = tracks[i];
                string id = TlAttr(t, "id");
                string alias = TlAttr(t, "alias");
                string owner = TlAttr(t, "owner");
                string path = TlAttr(t, "guideObjectPath");
                string objIndex = TlAttr(t, "objectIndex");
                string enabled = TlAttr(t, "enabled");
                TlBump(idCount, id);
                TlBump(aliasCount, alias);
                TlBump(ownerCount, owner);
                TlBump(pathHeadCount, TlPathHead(path));
                if (enabled == "true")
                {
                    enabledTrue = enabledTrue + 1;
                }
                double first;
                double last;
                int frames = TlFrameSpan(t, timeCount, out first, out last);
                if (frames > 2)
                {
                    multiFrame = multiFrame + 1;
                }
                if (shown < 200)
                {
                    string parent = t.Parent == null ? "" : TlAttr(t.Parent, "name");
                    sw.WriteLine("- #" + (i + 1) + " 组=" + parent + " · obj=" + objIndex + " · id=" + id + " · alias=" + alias
                        + " · enabled=" + enabled + " · 帧=" + frames.ToString(CultureInfo.InvariantCulture)
                        + " [" + TlNum(first) + "," + TlNum(last) + "] · 路径=" + TlClip(path, 70));
                    shown = shown + 1;
                }
            }
            sw.WriteLine("- enabled=true 的轨道 " + enabledTrue.ToString("N0") + " / " + tracks.Count.ToString("N0"));
            sw.WriteLine("- 关键帧多于 2 个的轨道 " + multiFrame.ToString("N0") + " 条");

            // [段5] 统计
            sw.WriteLine();
            sw.WriteLine("## id 族");
            TlDumpCounts(idCount, sw, 40);
            sw.WriteLine();
            sw.WriteLine("## owner 族");
            TlDumpCounts(ownerCount, sw, 20);
            sw.WriteLine();
            sw.WriteLine("## alias 族（前 40）");
            TlDumpCounts(aliasCount, sw, 40);
            sw.WriteLine();
            sw.WriteLine("## 路径首段族（前 30）");
            TlDumpCounts(pathHeadCount, sw, 30);
            sw.WriteLine();
            sw.WriteLine("## 关键帧时刻分布");
            sw.WriteLine("- 不同时刻数 " + timeCount.Count.ToString("N0"));
            List<double> keys = new List<double>(timeCount.Keys);
            keys.Sort();
            int tShown = 0;
            for (int i = 0; i < keys.Count && tShown < 40; i = i + 1)
            {
                sw.WriteLine("- t=" + TlNum(keys[i]) + " → " + timeCount[keys[i]].ToString("N0") + " 帧");
                tShown = tShown + 1;
            }
            if (keys.Count > 40)
            {
                sw.WriteLine("- （时刻清单截断，共 " + keys.Count.ToString("N0") + " 个）");
            }
        }

        /// <summary>递归输出组树（只走 interpolableGroup）。</summary>
        private static void TlDumpGroups(XElement el, int depth, StreamWriter sw, ref int printed)
        {
            foreach (XElement child in el.Elements("interpolableGroup"))
            {
                if (printed >= 400)
                {
                    return;
                }
                int subs = 0;
                int tracks = 0;
                foreach (XElement c2 in child.Elements())
                {
                    if (c2.Name.LocalName == "interpolableGroup")
                    {
                        subs = subs + 1;
                    }
                    else if (c2.Name.LocalName == "interpolable")
                    {
                        tracks = tracks + 1;
                    }
                }
                string pad = new string(' ', depth * 2);
                sw.WriteLine(pad + "- " + TlAttr(child, "name") + "（子组 " + subs.ToString(CultureInfo.InvariantCulture)
                    + " · 轨道 " + tracks.ToString(CultureInfo.InvariantCulture) + "）");
                printed = printed + 1;
                TlDumpGroups(child, depth + 1, sw, ref printed);
            }
        }

        /// <summary>统计一条轨道的 keyframe 帧数与首末时刻，并把时刻计入分布表。</summary>
        private static int TlFrameSpan(XElement track, Dictionary<double, int> timeCount, out double first, out double last)
        {
            first = 0;
            last = 0;
            int frames = 0;
            foreach (XElement kf in track.Elements("keyframe"))
            {
                double t;
                if (!double.TryParse(TlAttr(kf, "time"), NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                {
                    continue;
                }
                if (frames == 0)
                {
                    first = t;
                }
                last = t;
                frames = frames + 1;
                if (timeCount.ContainsKey(t))
                {
                    timeCount[t] = timeCount[t] + 1;
                }
                else
                {
                    timeCount[t] = 1;
                }
            }
            return frames;
        }

        /// <summary>计数加一。</summary>
        private static void TlBump(Dictionary<string, int> table, string key)
        {
            if (key == null || key.Length == 0)
            {
                key = "(无)";
            }
            if (table.ContainsKey(key))
            {
                table[key] = table[key] + 1;
            }
            else
            {
                table[key] = 1;
            }
        }

        /// <summary>按计数降序输出前 n 项。</summary>
        private static void TlDumpCounts(Dictionary<string, int> table, StreamWriter sw, int top)
        {
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(table);
            list.Sort(TlCompareCount);
            int n = 0;
            for (int i = 0; i < list.Count && n < top; i = i + 1)
            {
                sw.WriteLine("- " + list[i].Key + " → " + list[i].Value.ToString("N0"));
                n = n + 1;
            }
            if (list.Count > top)
            {
                sw.WriteLine("- （共 " + list.Count.ToString("N0") + " 种，截断）");
            }
        }

        /// <summary>计数降序比较（同数按名升序）。</summary>
        private static int TlCompareCount(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            if (a.Value != b.Value)
            {
                return b.Value - a.Value;
            }
            return string.CompareOrdinal(a.Key, b.Key);
        }

        /// <summary>取元素属性文本（无则空串）。</summary>
        private static string TlAttr(XElement el, string name)
        {
            XAttribute a = el.Attribute(name);
            if (a == null)
            {
                return "";
            }
            return a.Value;
        }

        /// <summary>把元素全部属性拼成一行。</summary>
        private static string TlAttrText(XElement el)
        {
            StringBuilder sb = new StringBuilder();
            foreach (XAttribute a in el.Attributes())
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(a.Name.LocalName);
                sb.Append('=');
                sb.Append(a.Value);
            }
            return sb.ToString();
        }

        /// <summary>路径首段（第一个 / 之前）。</summary>
        private static string TlPathHead(string path)
        {
            if (path == null || path.Length == 0)
            {
                return "(无)";
            }
            int i = path.IndexOf('/');
            if (i < 0)
            {
                return path;
            }
            return path.Substring(0, i);
        }

        /// <summary>截断文本（超长加省略标记）。</summary>
        private static string TlClip(string text, int max)
        {
            if (text == null)
            {
                return "";
            }
            if (text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max) + "…";
        }

        /// <summary>秒数文本（去掉多余零）。</summary>
        private static string TlNum(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>流式找 timeline 条目锚点（未命中 -1）。</summary>
        private static long TlFindAnchor(string path)
        {
            const int BufSize = 1 << 20;
            byte[] buf = new byte[BufSize + 16];
            using (FileStream fs = File.OpenRead(path))
            {
                long pos = 0;
                long consumed = 0;
                int carry = 0;
                while (pos < fs.Length)
                {
                    long want = fs.Length - pos;
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
                    for (int i = 0; i <= total - TlKeyAnchor.Length; i = i + 1)
                    {
                        if (buf[i] == TlKeyAnchor[0] && MatchAt(buf, i, TlKeyAnchor))
                        {
                            return baseOff + i;
                        }
                    }
                    int newCarry = 15;
                    if (total < 15)
                    {
                        newCarry = total;
                    }
                    Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                    carry = newCarry;
                }
            }
            return -1;
        }

        /// <summary>读条目头，解出 sceneInfo XML 的起点与长度（形态未识别返回 false）。</summary>
        private static bool TlReadXmlSpan(string path, long hit, out long xmlAt, out int xmlLen)
        {
            xmlAt = 0;
            xmlLen = 0;
            byte[] head = new byte[512];
            int n;
            using (FileStream fs = File.OpenRead(path))
            {
                if (hit + head.Length > fs.Length)
                {
                    return false;
                }
                fs.Position = hit;
                n = ReadFull(fs, head, head.Length);
            }
            if (n < 28 || head[9] != 0x92 || head[11] != 0x81 || head[12] != 0xA9)
            {
                return false;
            }
            if (Encoding.ASCII.GetString(head, 13, 9) != "sceneInfo")
            {
                return false;
            }
            byte strHead = head[22];
            if (strHead == 0xD9)
            {
                xmlLen = head[23];
                xmlAt = hit + 24;
            }
            else if (strHead == 0xDA)
            {
                xmlLen = (head[23] << 8) | head[24];
                xmlAt = hit + 25;
            }
            else if (strHead == 0xDB)
            {
                xmlLen = (head[23] << 24) | (head[24] << 16) | (head[25] << 8) | head[26];
                xmlAt = hit + 27;
            }
            else
            {
                return false;
            }
            return xmlLen > 0;
        }

        /// <summary>读文件一段字节并按 UTF-8 解码（失败返回 null）。</summary>
        private static string TlReadText(string path, long at, int len)
        {
            if (len <= 0)
            {
                return null;
            }
            byte[] buf = new byte[len];
            using (FileStream fs = File.OpenRead(path))
            {
                if (at + len > fs.Length)
                {
                    return null;
                }
                fs.Position = at;
                int n = ReadFull(fs, buf, len);
                if (n <= 0)
                {
                    return null;
                }
            }
            return Encoding.UTF8.GetString(buf);
        }
    }
}
