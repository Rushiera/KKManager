using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>场景卡的 timeline 数据（Timeline 插件写在场景插件块 "timeline" 条目里）——只读快照。</summary>
    public class TimelineInfo
    {
        /// <summary>是否扫过（只有场景卡才扫）。</summary>
        public bool Scanned { get; set; }

        /// <summary>是否存在 timeline 条目（场景里没用过 Timeline 插件就没有）。</summary>
        public bool HasEntry { get; set; }

        /// <summary>空时间轴——有条目但没有任何关键帧（Timeline 的默认状态）。</summary>
        public bool IsEmpty { get; set; }

        /// <summary>timeline 长度（秒，原值——Timeline 界面上的长度）。</summary>
        public double Duration { get; set; }

        /// <summary>时间缩放（Timeline 的 timeScale 属性 = Unity 的 Time.timeScale；1 = 原速）。</summary>
        public double TimeScale { get; set; }

        /// <summary>sceneInfo XML 的字节数。</summary>
        public long XmlLength { get; set; }

        /// <summary>XML 内关键帧数（读前 4 MB 计数）。</summary>
        public int Keyframes { get; set; }

        /// <summary>XML 被读取上限截断（关键帧数只统计了已读部分）。</summary>
        public bool Truncated { get; set; }

        /// <summary>sceneInfo 里 interpolableGroup 数量（被 timeline 驱动的插值组 / 轨道——每个挂在场景对象上）。</summary>
        public int Groups { get; set; }

        /// <summary>sceneInfo 里最长关键帧时刻（秒）——**不等于 timeline 长度**（实测 67 vs 71.507 · 130 vs 242），两者分开呈现。</summary>
        public double MaxKeyframeTime { get; set; }

        /// <summary>插值组名（Timeline 轨道——被驱动的部位 / 对象；去重，最多 200 个）。</summary>
        public List<string> GroupNames { get; } = new List<string>();

        /// <summary>实际播放时长（秒）= 原时长 / timeScale——播放时钟走 Unity 缩放时间，timeScale 越大越快。</summary>
        public double RealSeconds { get; set; }

        /// <summary>命中 timeline 条目的扫描阶段（尾部 16MB / 尾部 64MB / 全量）。</summary>
        public string HitStage { get; set; }

        /// <summary>读取失败的原因（失败必须可见）。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 场景卡 timeline 长度读取——在数据区里找 Timeline 条目的 MessagePack 锚点（fixstr8 "timeline"），
    /// 读它的 sceneInfo XML，取根属性 duration / timeScale 与关键帧数。
    /// 条目恒在数据区尾段（实测距文件尾 = XML 长度 + 数 KB），故扫描自尾部起步、未命中逐级放大到全量。
    /// </summary>
    public static class TimelineReader
    {
        /// <summary>timeline 条目的 MessagePack 键锚点——fixstr8 "timeline"。</summary>
        private static readonly byte[] KeyAnchor = { 0xA8, 0x74, 0x69, 0x6D, 0x65, 0x6C, 0x69, 0x6E, 0x65 };
        /// <summary>sceneInfo XML 里原时长属性的锚点——duration="。</summary>
        private static readonly byte[] DurNeedle = Encoding.ASCII.GetBytes("duration=\"");
        /// <summary>sceneInfo XML 里时间缩放属性的锚点——timeScale="。</summary>
        private static readonly byte[] ScaleNeedle = Encoding.ASCII.GetBytes("timeScale=\"");
        /// <summary>关键帧元素锚点——&lt;keyframe 。</summary>
        private static readonly byte[] KeyframeNeedle = Encoding.ASCII.GetBytes("<keyframe ");
        /// <summary>插值组元素锚点——&lt;interpolableGroup 。</summary>
        private static readonly byte[] GroupNeedle = Encoding.ASCII.GetBytes("<interpolableGroup ");
        /// <summary>时刻属性锚点——time="（关键帧时刻）。</summary>
        private static readonly byte[] TimeNeedle = Encoding.ASCII.GetBytes("time=\"");
        /// <summary>名字属性锚点——name="（插值组名）。</summary>
        private static readonly byte[] NameNeedle = Encoding.ASCII.GetBytes("name=\"");
        /// <summary>首轮尾部窗口——16 MB（实测条目距文件尾最大约 12.5 MB）。</summary>
        private const long TailNear = 16L * 1024 * 1024;
        /// <summary>次轮尾部窗口——64 MB（首轮未命中再放大一档）。</summary>
        private const long TailFar = 64L * 1024 * 1024;
        /// <summary>sceneInfo XML 的读取上限——16 MB（超出只统计已读部分；实测最长 XML 9.6 MB）。</summary>
        private const int MaxXmlRead = 16 * 1024 * 1024;

        /// <summary>读一张场景卡的 timeline 数据（path = 卡片文件；imageEnd = 图片区结束偏移）。</summary>
        public static TimelineInfo Read(string path, long imageEnd)
        {
            return Read(path, imageEnd, null);
        }
        /// <summary>读一张场景卡的 timeline 数据（会话版——复用段内已打开的句柄，不另开文件；session 为 null 时自行开文件）。</summary>
        public static TimelineInfo Read(string path, long imageEnd, CardFileSession session)
        {
            var info = new TimelineInfo { Scanned = true, TimeScale = 1 };
            if (!File.Exists(path))
            {
                info.Error = "卡片文件不存在：" + path;
                return info;
            }
            bool own = session == null;
            if (own)
            {
                session = CardFileSession.Open(path);
            }
            if (session == null)
            {
                info.Error = "卡片文件打不开：" + path;
                return info;
            }
            try
            {
                long size = session.Length;
                long from = imageEnd > 0 ? imageEnd : 0;
                if (from >= size)
                {
                    info.Error = "数据区为空（图片区到文件尾没有字节）";
                    return info;
                }
                FileStream fs = session.Stream;
                long near = size - TailNear > from ? size - TailNear : from;
                long far = size - TailFar > from ? size - TailFar : from;
                long hit = -1;
                string stage = null;
                if (far < near)
                {
                    hit = ScanFor(fs, near, far);
                    if (hit >= 0)
                    {
                        stage = "尾部 64MB";
                    }
                }
                if (hit < 0)
                {
                    hit = ScanFor(fs, size, near);
                    if (hit >= 0)
                    {
                        stage = "尾部 16MB";
                    }
                }
                if (hit < 0 && from < far)
                {
                    hit = ScanFor(fs, far, from);
                    if (hit >= 0)
                    {
                        stage = "全量";
                    }
                }
                info.HitStage = stage;
                if (hit < 0)
                {
                    info.HasEntry = false;
                    return info;
                }

                info.HasEntry = true;
                byte[] head = new byte[512];
                fs.Position = hit;
                int n = ReadFull(fs, head, head.Length);
                if (n < 28 || head[9] != 0x92 || head[11] != 0x81 || head[12] != 0xA9 || Encoding.ASCII.GetString(head, 13, 9) != "sceneInfo")
                {
                    info.Error = "timeline 条目结构未识别（偏移 " + hit + "）";
                    return info;
                }
                byte strHead = head[22];
                int xmlLen;
                long xmlAt;
                // 条目形态：A8 "timeline" + 92 00 + 81 A9 "sceneInfo" = 22 字节，串头落在 hit+22
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
                    info.Error = "sceneInfo 字符串头未识别（0x" + strHead.ToString("X2") + "）";
                    return info;
                }
                if (xmlLen <= 0 || xmlAt + xmlLen > size)
                {
                    info.Error = "sceneInfo 长度越界（" + xmlLen.ToString(CultureInfo.InvariantCulture) + " 字节 @ " + xmlAt.ToString(CultureInfo.InvariantCulture) + "）";
                    return info;
                }
                info.XmlLength = xmlLen;
                ReadXml(fs, xmlAt, xmlLen, info);
            }
            finally
            {
                if (own)
                {
                    session.Dispose();
                }
            }
            return info;
        }

        /// <summary>读 sceneInfo XML（上限 16 MB）——取 duration / timeScale / 关键帧数 / 插值组数 / 最长关键帧时刻 / 插值组名 / 空轴标记。</summary>
        private static void ReadXml(FileStream fs, long xmlAt, int xmlLen, TimelineInfo info)
        {
            int take = xmlLen > MaxXmlRead ? MaxXmlRead : xmlLen;
            info.Truncated = xmlLen > MaxXmlRead;
            byte[] xml = new byte[take];
            fs.Position = xmlAt;
            int n = ReadFull(fs, xml, take);

            string durationText = ReadQuotedNumber(xml, n, DurNeedle);
            if (durationText == null)
            {
                info.Error = "sceneInfo 里没有 duration 属性";
                return;
            }
            double dur;
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out dur))
            {
                info.Error = "duration 不是数字：" + durationText;
                return;
            }
            info.Duration = dur;

            string scaleText = ReadQuotedNumber(xml, n, ScaleNeedle);
            double scale;
            if (scaleText != null && double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0)
            {
                info.TimeScale = scale;
            }
            else
            {
                info.TimeScale = 1;
            }
            info.RealSeconds = info.Duration / info.TimeScale;

            info.Keyframes = CountNeedle(xml, n, KeyframeNeedle);
            info.Groups = CountNeedle(xml, n, GroupNeedle);
            info.MaxKeyframeTime = MaxQuotedNumber(xml, n, TimeNeedle);
            info.IsEmpty = info.Keyframes == 0 && !info.Truncated;

            // 插值组名（Timeline 轨道）——从第一个 interpolableGroup 起取 name 属性，去重（上限 200）
            int firstGroup = IndexOfNeedle(xml, n, GroupNeedle);
            if (firstGroup < 0)
            {
                firstGroup = 0;
            }
            for (int i = firstGroup; i <= n - NameNeedle.Length && info.GroupNames.Count < 200; i = i + 1)
            {
                if (xml[i] != NameNeedle[0] || !MatchAt(xml, i, NameNeedle))
                {
                    continue;
                }
                string name = ReadQuotedText(xml, n, NameNeedle, i);
                if (!string.IsNullOrEmpty(name) && !info.GroupNames.Contains(name))
                {
                    info.GroupNames.Add(name);
                }
            }
        }

        /// <summary>在 [from, to) 区间流式找 timeline 条目锚点，返回文件偏移（未命中 -1）。</summary>
        private static long ScanFor(FileStream fs, long to, long from)
        {
            const int BufSize = 1 << 20;
            byte[] buf = new byte[BufSize + 16];
            long pos = from;
            long consumed = from;
            int carry = 0;
            while (pos < to)
            {
                long want = to - pos;
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
                for (int i = 0; i <= total - KeyAnchor.Length; i = i + 1)
                {
                    if (buf[i] == KeyAnchor[0] && MatchAt(buf, i, KeyAnchor))
                    {
                        return baseOff + i;
                    }
                }
                int newCarry = total < 15 ? total : 15;
                Array.Copy(buf, total - newCarry, buf, 0, newCarry);
                carry = newCarry;
            }
            return -1;
        }

        /// <summary>在字节缓冲里找 needle，随后读带引号的数值文本；未命中返回 null。</summary>
        private static string ReadQuotedNumber(byte[] buf, int n, byte[] needle)
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
                    bool ok = (b >= 0x30 && b <= 0x39) || b == 0x2E || b == 0x2D;
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
        /// <summary>找 needle 在缓冲里首次出现的位置；无则 -1。</summary>
        private static int IndexOfNeedle(byte[] buf, int n, byte[] needle)
        {
            for (int i = 0; i <= n - needle.Length; i = i + 1)
            {
                if (buf[i] == needle[0] && MatchAt(buf, i, needle))
                {
                    return i;
                }
            }
            return -1;
        }
        /// <summary>读 needle 之后带引号的文本（UTF-8 解码；从 fromAt 起找 needle）；无则 null。</summary>
        private static string ReadQuotedText(byte[] buf, int n, byte[] needle, int fromAt)
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
        /// <summary>统计缓冲里 needle 出现的次数。</summary>
        private static int CountNeedle(byte[] buf, int n, byte[] needle)
        {
            int count = 0;
            for (int i = 0; i <= n - needle.Length; i = i + 1)
            {
                if (buf[i] == needle[0] && MatchAt(buf, i, needle))
                {
                    count = count + 1;
                    i = i + needle.Length;
                }
            }
            return count;
        }
        /// <summary>扫全缓冲里 needle 之后带引号的数值文本，取最大值；没有返回 0。</summary>
        private static double MaxQuotedNumber(byte[] buf, int n, byte[] needle)
        {
            double best = 0;
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
                    bool ok = (b >= 0x30 && b <= 0x39) || b == 0x2E || b == 0x2D;
                    if (!ok)
                    {
                        break;
                    }
                    sb.Append((char)b);
                    p = p + 1;
                }
                double v;
                if (double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > best)
                {
                    best = v;
                }
            }
            return best;
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
                total += n;
            }
            return total;
        }

        /// <summary>秒 → mm:ss.ff（ff = 百分秒）。</summary>
        public static string FormatSeconds(double seconds)
        {
            long centis = (long)Math.Round(seconds * 100.0, MidpointRounding.AwayFromZero);
            if (centis < 0)
            {
                centis = 0;
            }
            long minutes = centis / 6000;
            long rest = (centis % 6000) / 100;
            long frac = centis % 100;
            return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString("00", CultureInfo.InvariantCulture) + "." + frac.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>秒数文本（去掉多余的零）——281.5 / 39.5 / 67。</summary>
        public static string SecondsText(double seconds)
        {
            return seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 面板与 CLI 共用的 timeline 文案：
        /// 「无实际 timeline」· 「无实际 timeline（空轴）」·「281.5(04:41.50) · 实际播放 02:25.90（timeScale 1.93）」。
        /// </summary>
        public static string Describe(TimelineInfo t)
        {
            if (t == null)
            {
                return "—";
            }
            if (t.Error != null)
            {
                return "读取失败：" + t.Error;
            }
            if (!t.HasEntry)
            {
                return "无实际 timeline";
            }
            if (t.IsEmpty)
            {
                return "无实际 timeline（空轴）";
            }
            string text = SecondsText(t.Duration) + "(" + FormatSeconds(t.Duration) + ")";
            if (Math.Abs(t.TimeScale - 1.0) > 0.0005)
            {
                text = text + " · 实际播放 " + FormatSeconds(t.RealSeconds) + "（timeScale " + SecondsText(t.TimeScale) + "）";
            }
            return text;
        }
    }
}
