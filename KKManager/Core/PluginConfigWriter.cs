using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>cfg 值写回的结果——成败 / 原因 / 改动前后的事实（字节数与修改时间对照）。</summary>
    public class PluginConfigSaveResult
    {
        /// <summary>是否写入成功（值本来就没变也算成功，但不写盘）。</summary>
        public bool ok { get; set; }

        /// <summary>失败原因（成功时为空串）。</summary>
        public string error { get; set; } = "";

        /// <summary>写入的 cfg 绝对路径。</summary>
        public string filePath { get; set; } = "";

        /// <summary>写入的 cfg 文件名。</summary>
        public string fileName { get; set; } = "";

        /// <summary>分节名（文件开头无分节的键为「(无分节)」）。</summary>
        public string section { get; set; } = "";

        /// <summary>配置键。</summary>
        public string key { get; set; } = "";

        /// <summary>改动前的值。</summary>
        public string oldValue { get; set; } = "";

        /// <summary>改动后的值。</summary>
        public string newValue { get; set; } = "";

        /// <summary>写入前的文件字节数。</summary>
        public long sizeBefore { get; set; }

        /// <summary>写入后的文件字节数。</summary>
        public long sizeAfter { get; set; }

        /// <summary>写入前的修改时间戳（UTC，"o" 格式）。</summary>
        public string mtimeBefore { get; set; } = "";

        /// <summary>写入后的修改时间戳（UTC，"o" 格式）。</summary>
        public string mtimeAfter { get; set; } = "";
    }

    /// <summary>
    /// BepInEx 配置文件（config/*.cfg）的值写回——最小改动：只替换目标键那一行等号右侧的值段，
    /// 其余字节（BOM / 行尾 / 作者注释 / 分节顺序 / 空行 / 缩进）逐字节搬运。
    /// 边界：只改已存在的键，不新增键、不删键、不动注释；键不存在或同节出现多次一律拒绝并出声。
    /// </summary>
    public static class PluginConfigWriter
    {
        /// <summary>文件开头无分节的键在读面归入的虚拟分节名（与 PluginConfigReader 同源）——写面同样接受这个名字。</summary>
        public const string NoSectionName = "(无分节)";

        /// <summary>把某个配置项的值写回 cfg（最小改动——只换等号右侧的值段）。</summary>
        /// <param name="cfgPath">cfg 绝对路径。</param>
        /// <param name="section">分节名（文件开头无分节的键给「(无分节)」）。</param>
        /// <param name="key">配置键。</param>
        /// <param name="newValue">新值（可为空串；不得含换行）。</param>
        /// <returns>写入结果（失败时 ok 为 false 且 error 说明原因）。</returns>
        public static PluginConfigSaveResult Save(string cfgPath, string section, string key, string newValue)
        {
            PluginConfigSaveResult result = new PluginConfigSaveResult();
            result.filePath = cfgPath == null ? "" : cfgPath;
            result.fileName = cfgPath == null ? "" : Path.GetFileName(cfgPath);
            result.section = section == null ? "" : section;
            result.key = key == null ? "" : key;
            result.newValue = newValue == null ? "" : newValue;
            // [段1] 入口校验——键非空 / 值不含换行 / 文件在位
            if (string.IsNullOrWhiteSpace(cfgPath))
            {
                result.error = "配置文件路径为空";
                return result;
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                result.error = "配置键为空";
                return result;
            }
            if (result.newValue.IndexOf('\r') >= 0 || result.newValue.IndexOf('\n') >= 0)
            {
                result.error = "值里不能含换行——会破坏 cfg 的行结构";
                return result;
            }
            if (!File.Exists(cfgPath))
            {
                result.error = "配置文件不存在：" + cfgPath;
                return result;
            }
            // [段2] 读原始字节——探测 BOM 与编码（BOM 单独搬运，不参与正文编码）
            byte[] before;
            try
            {
                before = File.ReadAllBytes(cfgPath);
            }
            catch (Exception ex)
            {
                result.error = "读取失败：" + ex.Message;
                return result;
            }
            int bomLength = 0;
            Encoding body = new UTF8Encoding(false);
            if (before.Length >= 3 && before[0] == 0xEF && before[1] == 0xBB && before[2] == 0xBF)
            {
                bomLength = 3;
                body = new UTF8Encoding(false);
            }
            else if (before.Length >= 2 && before[0] == 0xFF && before[1] == 0xFE)
            {
                bomLength = 2;
                body = new UnicodeEncoding(false, false);
            }
            else if (before.Length >= 2 && before[0] == 0xFE && before[1] == 0xFF)
            {
                bomLength = 2;
                body = new UnicodeEncoding(true, false);
            }
            result.sizeBefore = before.Length;
            result.mtimeBefore = File.GetLastWriteTimeUtc(cfgPath).ToString("o");
            string text = body.GetString(before, bomLength, before.Length - bomLength);
            // [段3] 切行（保留每行行尾）→ 定位目标键
            List<string> lines = new List<string>();
            List<string> eols = new List<string>();
            SplitLines(text, lines, eols);
            int eqPos;
            int hitCount;
            int hitLine = FindKeyLine(lines, result.section, key, out eqPos, out hitCount);
            if (hitCount == 0)
            {
                result.error = "没找到这个配置项（分节 [" + result.section + "] · 键 " + key + "）——只改已存在的键，不新增";
                return result;
            }
            if (hitCount > 1)
            {
                result.error = "同一分节里这个键出现了 " + hitCount + " 次（cfg 异常）——不猜改哪一处，请先手工确认文件";
                return result;
            }
            // [段4] 只换值段——等号与其后的前导空白原样保留
            string origin = lines[hitLine];
            string head = origin.Substring(0, eqPos + 1);
            string tail = origin.Substring(eqPos + 1);
            int leadEnd = 0;
            while (leadEnd < tail.Length && (tail[leadEnd] == ' ' || tail[leadEnd] == '\t'))
            {
                leadEnd = leadEnd + 1;
            }
            string lead = tail.Substring(0, leadEnd);
            string rest = tail.Substring(leadEnd);
            // 尾随空白原样保留（最小改动——不吞任何既有字节）
            int trailStart = rest.Length;
            while (trailStart > 0 && (rest[trailStart - 1] == ' ' || rest[trailStart - 1] == '\t'))
            {
                trailStart = trailStart - 1;
            }
            result.oldValue = rest.Substring(0, trailStart);
            string updated = head + lead + result.newValue + rest.Substring(trailStart);
            if (string.Equals(updated, origin, StringComparison.Ordinal))
            {
                // 值没变——不写盘（不制造无谓的 mtime 变动）
                result.ok = true;
                result.sizeAfter = before.Length;
                result.mtimeAfter = result.mtimeBefore;
                return result;
            }
            List<string> changed = new List<string>(lines);
            changed[hitLine] = updated;
            // [段5] 最小改动自检——新旧行数组必须只差目标那一行
            int diff = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.Equals(lines[i], changed[i], StringComparison.Ordinal))
                {
                    diff = diff + 1;
                }
            }
            if (diff != 1)
            {
                result.error = "内部自检失败——改动不止一行（" + diff + " 行），拒绝写盘";
                return result;
            }
            // [段6] 重组 + 写盘（BOM 前缀原样搬运；行尾逐行还原）
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < changed.Count; i++)
            {
                sb.Append(changed[i]);
                sb.Append(eols[i]);
            }
            byte[] payload = body.GetBytes(sb.ToString());
            byte[] after = new byte[bomLength + payload.Length];
            Array.Copy(before, 0, after, 0, bomLength);
            Array.Copy(payload, 0, after, bomLength, payload.Length);
            try
            {
                File.WriteAllBytes(cfgPath, after);
            }
            catch (Exception ex)
            {
                result.error = "写盘失败：" + ex.Message;
                return result;
            }
            // [段7] 读回验证——磁盘字节与预期逐字节全等（落盘实锤）
            byte[] verify;
            try
            {
                verify = File.ReadAllBytes(cfgPath);
            }
            catch (Exception ex)
            {
                result.error = "写回后重读失败：" + ex.Message;
                return result;
            }
            if (verify.Length != after.Length)
            {
                result.error = "写回后字节数不符（预期 " + after.Length + " · 实际 " + verify.Length + "）";
                return result;
            }
            for (int i = 0; i < after.Length; i++)
            {
                if (verify[i] != after[i])
                {
                    result.error = "写回后第 " + i + " 字节与预期不符";
                    return result;
                }
            }
            result.ok = true;
            result.sizeAfter = verify.Length;
            result.mtimeAfter = File.GetLastWriteTimeUtc(cfgPath).ToString("o");
            return result;
        }

        /// <summary>把 cfg 正文切成行——每行不含行尾符，行尾符单独收在 eols（重组时逐行还原）。</summary>
        /// <param name="text">cfg 正文（已去掉 BOM）。</param>
        /// <param name="lines">行内容（输出）。</param>
        /// <param name="eols">每行行尾（"\r\n" / "\n" / ""——最后一行可能没有行尾）。</param>
        private static void SplitLines(string text, List<string> lines, List<string> eols)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }
                int end = i;
                string eol = "\n";
                if (end > start && text[end - 1] == '\r')
                {
                    end = end - 1;
                    eol = "\r\n";
                }
                lines.Add(text.Substring(start, end - start));
                eols.Add(eol);
                start = i + 1;
            }
            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
                eols.Add("");
            }
        }

        /// <summary>定位目标键所在的行（判据与读面 PluginConfigReader 同构：分节行 / 注释 / 空行 / 「键 = 值」）。</summary>
        /// <param name="lines">行内容。</param>
        /// <param name="section">目标分节名（「(无分节)」= 文件开头未遇任何 [节] 的键）。</param>
        /// <param name="key">目标键。</param>
        /// <param name="eqPos">命中行的等号位置（输出；未命中为 -1）。</param>
        /// <param name="hitCount">命中次数（输出；0 = 未命中，>1 = 同节重复）。</param>
        /// <returns>首个命中行下标（未命中为 -1）。</returns>
        private static int FindKeyLine(List<string> lines, string section, string key, out int eqPos, out int hitCount)
        {
            eqPos = -1;
            hitCount = 0;
            int hit = -1;
            string current = NoSectionName;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].TrimEnd();
                // [段1] 分节行——[名]
                if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
                {
                    current = t.Substring(1, t.Length - 2).Trim();
                    continue;
                }
                // [段2] 注释与空行——跳过（## 作者注释 / # 属性行一并跳过）
                if (t.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                if (t.Length == 0)
                {
                    continue;
                }
                // [段3] 键值行——「键 = 值」
                int eq = t.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.Ordinal))
                {
                    continue;
                }
                hitCount = hitCount + 1;
                if (hit < 0)
                {
                    hit = i;
                    eqPos = eq;
                }
            }
            return hit;
        }
    }
}
