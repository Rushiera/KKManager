using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KKManager.Core
{
    /// <summary>cfg 里的一项配置（键 / 当前值 / 作者写的说明 / 类型与默认值——配置管理器的页面就是按这个结构渲染的）。</summary>
    public class PluginConfigOption
    {
        /// <summary>配置键（cfg 等号左边的名字）。</summary>
        public string key { get; set; } = "";

        /// <summary>当前值（cfg 等号右边的文本）。</summary>
        public string value { get; set; } = "";

        /// <summary>作者写的说明（cfg 里键上方的 ## 注释，可多行合成一段）。</summary>
        public string comment { get; set; } = "";

        /// <summary>设置类型（cfg 的「# Setting type:」——如 Boolean / Int32 / Single / String / KeyboardShortcut）。</summary>
        public string type { get; set; } = "";

        /// <summary>默认值（cfg 的「# Default value:」）。</summary>
        public string def { get; set; } = "";

        /// <summary>可选值（cfg 的「# Acceptable values:」——枚举型才有）。</summary>
        public string acceptable { get; set; } = "";
    }

    /// <summary>cfg 里的一个分节（配置管理器里的一栏）。</summary>
    public class PluginConfigSection
    {
        /// <summary>节名（cfg 的 [方括号] 里的名字）。</summary>
        public string name { get; set; } = "";

        /// <summary>本节的全部配置项（保持文件顺序）。</summary>
        public List<PluginConfigOption> options { get; set; } = new List<PluginConfigOption>();
    }

    /// <summary>一个插件配置文件（BepInEx/config 下的一个 .cfg）——文件头给出插件名与 GUID，正文是分节与配置项。</summary>
    public class PluginConfigFile
    {
        /// <summary>cfg 绝对路径。</summary>
        public string filePath { get; set; } = "";

        /// <summary>cfg 文件名。</summary>
        public string fileName { get; set; } = "";

        /// <summary>插件显示名（文件头「Settings file was created by plugin &lt;名&gt; v&lt;版本&gt;」）。</summary>
        public string pluginName { get; set; } = "";

        /// <summary>插件版本（文件头读出）。</summary>
        public string pluginVersion { get; set; } = "";

        /// <summary>插件 GUID（文件头「## Plugin GUID:」——老配置文件可能没有）。</summary>
        public string guid { get; set; } = "";

        /// <summary>cfg 字节数。</summary>
        public long size { get; set; }

        /// <summary>cfg 修改时间戳文本（UTC）。</summary>
        public string mtime { get; set; } = "";

        /// <summary>分节清单（保持文件顺序）。</summary>
        public List<PluginConfigSection> sections { get; set; } = new List<PluginConfigSection>();

        /// <summary>解析失败原因（空 = 成功）。</summary>
        public string error { get; set; } = "";
    }

    /// <summary>BepInEx 配置文件（config/*.cfg）只读解析——配置管理器在游戏内展示的插件设置页，就是这个文件的结构。</summary>
    public static class PluginConfigReader
    {
        /// <summary>JSON 序列化选项（缩进 + 键名大小写不敏感）。</summary>
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>把一个 cfg 解析结果序列化为 JSON（落库用）。</summary>
        /// <param name="file">解析结果。</param>
        /// <returns>JSON 文本。</returns>
        public static string ToJson(PluginConfigFile file)
        {
            if (file == null)
            {
                return "";
            }
            return JsonSerializer.Serialize(file, Options);
        }

        /// <summary>从 JSON 反序列化（空 / 非法返回空对象——解析失败出声）。</summary>
        /// <param name="json">JSON 文本。</param>
        /// <returns>解析结果（失败时 error 非空）。</returns>
        public static PluginConfigFile FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PluginConfigFile();
            }
            try
            {
                PluginConfigFile file = JsonSerializer.Deserialize<PluginConfigFile>(json, Options);
                if (file == null)
                {
                    return new PluginConfigFile();
                }
                if (file.sections == null)
                {
                    file.sections = new List<PluginConfigSection>();
                }
                return file;
            }
            catch (JsonException ex)
            {
                PluginConfigFile bad = new PluginConfigFile();
                bad.error = "配置 JSON 解析失败：" + ex.Message;
                return bad;
            }
        }

        /// <summary>读一个 cfg 文件（失败时 error 出声，不抛）。</summary>
        /// <param name="path">cfg 绝对路径。</param>
        /// <returns>解析结果。</returns>
        public static PluginConfigFile Read(string path)
        {
            PluginConfigFile file = new PluginConfigFile();
            file.filePath = path;
            file.fileName = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                file.error = "配置文件不存在";
                return file;
            }
            try
            {
                FileInfo fi = new FileInfo(path);
                file.size = fi.Length;
                file.mtime = fi.LastWriteTimeUtc.ToString("o");
                List<string> comments = new List<string>();
                string type = "";
                string def = "";
                string acceptable = "";
                PluginConfigSection section = null;
                using (StreamReader reader = new StreamReader(path, System.Text.Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string text = line.TrimEnd();
                        // [段1] 文件头——插件名 / 版本 / GUID（老格式没有 GUID 行）
                        if (text.StartsWith("## Settings file was created by plugin ", StringComparison.Ordinal))
                        {
                            ParseHeader(text.Substring("## Settings file was created by plugin ".Length), file);
                            continue;
                        }
                        if (text.StartsWith("## Plugin GUID:", StringComparison.Ordinal))
                        {
                            file.guid = text.Substring("## Plugin GUID:".Length).Trim();
                            continue;
                        }
                        // [段2] 分节——[名]
                        if (text.Length > 2 && text[0] == '[' && text[text.Length - 1] == ']')
                        {
                            section = new PluginConfigSection();
                            section.name = text.Substring(1, text.Length - 2).Trim();
                            file.sections.Add(section);
                            comments.Clear();
                            type = "";
                            def = "";
                            acceptable = "";
                            continue;
                        }
                        // [段3] 说明与属性——「## 注释」「# Setting type: / Default value: / Acceptable values:」
                        if (text.StartsWith("## ", StringComparison.Ordinal))
                        {
                            comments.Add(text.Substring(3).Trim());
                            continue;
                        }
                        if (text.StartsWith("# Setting type:", StringComparison.Ordinal))
                        {
                            type = text.Substring("# Setting type:".Length).Trim();
                            continue;
                        }
                        if (text.StartsWith("# Default value:", StringComparison.Ordinal))
                        {
                            def = text.Substring("# Default value:".Length).Trim();
                            continue;
                        }
                        if (text.StartsWith("# Acceptable values:", StringComparison.Ordinal))
                        {
                            acceptable = text.Substring("# Acceptable values:".Length).Trim();
                            continue;
                        }
                        if (text.StartsWith("#", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        // [段4] 配置项——「键 = 值」（空行忽略）
                        if (text.Length == 0)
                        {
                            continue;
                        }
                        int eq = text.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }
                        if (section == null)
                        {
                            // 没有分节的键（老配置 / 简单配置）——归入「(无分节)」
                            section = new PluginConfigSection();
                            section.name = "(无分节)";
                            file.sections.Add(section);
                        }
                        PluginConfigOption option = new PluginConfigOption();
                        option.key = text.Substring(0, eq).Trim();
                        option.value = text.Substring(eq + 1).Trim();
                        option.comment = string.Join(" ", comments.ToArray());
                        option.type = type;
                        option.def = def;
                        option.acceptable = acceptable;
                        section.options.Add(option);
                        comments.Clear();
                        type = "";
                        def = "";
                        acceptable = "";
                    }
                }
            }
            catch (Exception ex)
            {
                file.error = ex.Message;
            }
            return file;
        }

        /// <summary>读一个插件库下的全部 cfg（只读顶层 *.cfg——子目录是插件自己的数据，不是配置）。</summary>
        /// <param name="pluginRoot">插件库根（BepInEx 目录）。</param>
        /// <param name="errors">读取失败清单（出声用）。</param>
        /// <returns>配置清单（按文件名排序）。</returns>
        public static List<PluginConfigFile> ReadAll(string pluginRoot, List<string> errors)
        {
            List<PluginConfigFile> list = new List<PluginConfigFile>();
            if (string.IsNullOrWhiteSpace(pluginRoot))
            {
                return list;
            }
            string dir = Path.Combine(pluginRoot, "config");
            if (!Directory.Exists(dir))
            {
                if (errors != null)
                {
                    errors.Add("配置目录不存在：" + dir);
                }
                return list;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.cfg", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                if (errors != null)
                {
                    errors.Add("配置目录枚举失败（" + dir + "）：" + ex.Message);
                }
                return list;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                PluginConfigFile cfg = Read(file);
                if (cfg.error.Length > 0 && errors != null)
                {
                    errors.Add("配置读取失败（" + file + "）：" + cfg.error);
                }
                list.Add(cfg);
            }
            return list;
        }

        /// <summary>解析文件头里的插件名与版本（形态「&lt;名&gt; v&lt;版本&gt;」——版本段可能不存在）。</summary>
        /// <param name="text">文件头去掉固定前缀后的文本。</param>
        /// <param name="file">写入目标。</param>
        private static void ParseHeader(string text, PluginConfigFile file)
        {
            string head = text.Trim();
            int at = head.LastIndexOf(" v", StringComparison.Ordinal);
            if (at > 0)
            {
                file.pluginName = head.Substring(0, at).Trim();
                file.pluginVersion = head.Substring(at + 1).Trim();
            }
            else
            {
                file.pluginName = head;
            }
        }
    }
}
