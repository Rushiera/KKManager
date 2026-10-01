using System;
using System.Collections.Generic;
using System.IO;

namespace KKManager.Core
{
    /// <summary>一次 BepInEx 启动的插件加载实况（只读解析 LogOutput.log 所得——日志是运行期产物，不入库）。</summary>
    public class PluginLogSummary
    {
        /// <summary>日志文件绝对路径。</summary>
        public string filePath { get; set; } = "";

        /// <summary>日志修改时间（本次启动时刻）。</summary>
        public string fileTime { get; set; } = "";

        /// <summary>日志字节数。</summary>
        public long fileSize { get; set; }

        /// <summary>「N plugins to load」里的 N（0 = 未读到）。</summary>
        public int toLoad { get; set; }

        /// <summary>实际加载的插件（「Loading [名 版本]」逐行）。</summary>
        public List<string> loaded { get; set; } = new List<string>();

        /// <summary>被进程过滤跳过的插件（「名 版本 → 原因」）。</summary>
        public List<string> skipped { get; set; } = new List<string>();
        /// <summary>已加载插件的名称集合（去版本——供行级标识查表）。</summary>
        public HashSet<string> loadedNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>被进程过滤跳过插件的名称集合（去版本——供行级标识查表）。</summary>
        public HashSet<string> skippedNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>错误行里点名的插件名集合（「[Error :名] ...」——供行级标识查表）。</summary>
        public HashSet<string> errorNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>错误行（[Error ...] 原文收下，不做二次解释）。</summary>
        public List<string> errors { get; set; } = new List<string>();

        /// <summary>解析失败原因（空 = 成功）。</summary>
        public string error { get; set; } = "";
    }

    /// <summary>BepInEx 日志（LogOutput.log）只读解析——插件加载 / 进程过滤跳过 / 错误三类事实。</summary>
    public static class PluginLogReader
    {
        /// <summary>插件库根下的日志路径（无插件库根返回空串）。</summary>
        /// <param name="cfg">库根配置。</param>
        /// <returns>日志绝对路径或空串。</returns>
        public static string LogPathOf(RootsConfig cfg)
        {
            if (cfg == null || cfg.pluginRoots == null)
            {
                return "";
            }
            foreach (RootEntry e in cfg.pluginRoots)
            {
                if (!string.IsNullOrWhiteSpace(e.path))
                {
                    return Path.Combine(e.path, "LogOutput.log");
                }
            }
            return "";
        }

        /// <summary>解析日志——逐行扫关键行（待加载总数 / 加载 / 进程过滤跳过 / 错误）。</summary>
        /// <param name="path">日志绝对路径。</param>
        /// <returns>加载实况（失败时 error 出声，其余字段为空）。</returns>
        public static PluginLogSummary Read(string path)
        {
            PluginLogSummary summary = new PluginLogSummary();
            summary.filePath = path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                summary.error = "日志不存在（先启动一次游戏，BepInEx 才会写日志）：" + path;
                return summary;
            }
            try
            {
                FileInfo fi = new FileInfo(path);
                summary.fileSize = fi.Length;
                summary.fileTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                using (StreamReader reader = new StreamReader(path, System.Text.Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // [段1] 待加载总数——「262 plugins to load」
                        int at = line.IndexOf("plugins to load", StringComparison.Ordinal);
                        if (at > 0)
                        {
                            int end = at;
                            while (end > 0 && line[end - 1] == ' ')
                            {
                                end = end - 1;
                            }
                            int start = end;
                            while (start > 0 && line[start - 1] >= '0' && line[start - 1] <= '9')
                            {
                                start = start - 1;
                            }
                            int value;
                            if (start < end && int.TryParse(line.Substring(start, end - start), out value))
                            {
                                summary.toLoad = value;
                            }
                            continue;
                        }
                        // [段2] 进程过滤跳过——「Skipping [名 版本] because of process filters (...)」
                        int skipAt = line.IndexOf("Skipping [", StringComparison.Ordinal);
                        if (skipAt >= 0)
                        {
                            int open = skipAt + "Skipping [".Length;
                            int close = line.IndexOf(']', open);
                            string name = close > open ? line.Substring(open, close - open) : "";
                            string reason = "原因未记";
                            int paren = line.IndexOf("process filters (", StringComparison.Ordinal);
                            if (paren >= 0)
                            {
                                reason = "进程过滤：" + line.Substring(paren + "process filters (".Length).TrimEnd(')', ' ');
                            }
                            summary.skipped.Add(name + "  →  " + reason);
                            string skipName = NameOf(name);
                            if (skipName.Length > 0)
                            {
                                summary.skippedNames.Add(skipName);
                            }
                            continue;
                        }
                        // [段3] 实际加载——「Loading [名 版本]」
                        int loadAt = line.IndexOf("Loading [", StringComparison.Ordinal);
                        if (loadAt >= 0)
                        {
                            int open = loadAt + "Loading [".Length;
                            int close = line.IndexOf(']', open);
                            if (close > open)
                            {
                                string item = line.Substring(open, close - open);
                                summary.loaded.Add(item);
                                string loadName = NameOf(item);
                                if (loadName.Length > 0)
                                {
                                    summary.loadedNames.Add(loadName);
                                }
                            }
                            continue;
                        }
                        // [段4] 错误行——原文收下（不二次解释）+ 记下被点名的来源（「[Error  :名] ...」）
                        int errAt = line.IndexOf("[Error", StringComparison.Ordinal);
                        if (errAt >= 0)
                        {
                            summary.errors.Add(line);
                            string src = ErrorSourceName(line, errAt);
                            if (src.Length > 0)
                            {
                                summary.errorNames.Add(src);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                summary.error = ex.Message;
            }
            return summary;
        }
        /// <summary>日志条目（「名 版本」）取名称部分——去掉最后一个空格段（BepInEx 会规范化版本号，故不按版本比）。</summary>
        /// <param name="item">日志条目文本。</param>
        /// <returns>名称部分。</returns>
        private static string NameOf(string item)
        {
            if (string.IsNullOrEmpty(item))
            {
                return "";
            }
            int at = item.LastIndexOf(' ');
            return at > 0 ? item.Substring(0, at) : item;
        }
        /// <summary>错误行取被点名的来源名——「[Error  :名] 正文」里冒号与「]」之间那段。</summary>
        /// <param name="line">错误行原文。</param>
        /// <param name="errAt">「[Error」在行内的起点。</param>
        /// <returns>来源名（取不到返回空串）。</returns>
        private static string ErrorSourceName(string line, int errAt)
        {
            int i = errAt + "[Error".Length;
            while (i < line.Length && (line[i] == ' ' || line[i] == ':'))
            {
                i = i + 1;
            }
            int close = line.IndexOf(']', i);
            if (close <= i)
            {
                return "";
            }
            return line.Substring(i, close - i).Trim();
        }
    }
}
