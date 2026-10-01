using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>插件中文说明表——名称 / GUID → 一句中文用途（随软件内嵌，离线可用）。</summary>
    public static class PluginNoteReader
    {
        /// <summary>内嵌资源名（Web\wwwroot\plugin-notes.txt 的清单名）。</summary>
        private const string ResourceName = "KKManager.Web.wwwroot.plugin-notes.txt";

        /// <summary>已载入的说明表（GUID → 说明 与 名称 → 说明 两张表；首次访问时载入一次）。</summary>
        private static Dictionary<string, string> _byGuid;

        /// <summary>名称 → 说明（小写键）。</summary>
        private static Dictionary<string, string> _byName;

        /// <summary>归一化键 → 说明（去掉 KK_ 前缀与标点后的小写串——兜住「KK_Autostart」对「Autostart」这类同义写法）。</summary>
        private static Dictionary<string, string> _byNorm;

        /// <summary>dll 文件名 → 说明（非插件 dll 用）。</summary>
        private static Dictionary<string, string> _byFile;

        /// <summary>载入失败原因（空 = 成功；失败出声一次，不静默）。</summary>
        private static string _error = "";

        /// <summary>说明表载入失败原因（空 = 正常）。</summary>
        public static string Error
        {
            get
            {
                EnsureLoaded();
                return _error;
            }
        }

        /// <summary>说明表条目数。</summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _byGuid == null ? 0 : _byGuid.Count;
            }
        }
        /// <summary>把名称归一化——小写、去掉空白 / 下划线 / 短横线 / 点，再去掉开头的 KK 前缀（同义写法归一到一起）。</summary>
        /// <param name="name">插件名。</param>
        /// <returns>归一化键（空名返回空串）。</returns>
        private static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }
            StringBuilder sb = new StringBuilder();
            string text = name.Trim().ToLowerInvariant();
            foreach (char c in text)
            {
                if (c == ' ' || c == '_' || c == '-' || c == '.')
                {
                    continue;
                }
                sb.Append(c);
            }
            string key = sb.ToString();
            if (key.StartsWith("kk", StringComparison.Ordinal) && key.Length > 2)
            {
                key = key.Substring(2);
            }
            return key;
        }

        /// <summary>非插件 dll 的说明——按文件名归一化键匹配（框架自带 / 依赖库 / 翻译器 / 补丁器）。</summary>
        /// <param name="fileName">dll 文件名。</param>
        /// <returns>中文说明（未收录返回空串）。</returns>
        public static string LookupFile(string fileName)
        {
            EnsureLoaded();
            if (_byGuid == null || string.IsNullOrWhiteSpace(fileName))
            {
                return "";
            }
            string hit;
            if (_byFile.TryGetValue(fileName.Trim().ToLowerInvariant(), out hit))
            {
                return hit;
            }
            string norm = Normalize(fileName);
            if (norm.Length > 0 && _byNorm.TryGetValue(norm, out hit))
            {
                return hit;
            }
            return "";
        }

        /// <summary>查一项说明——先按 GUID 精确匹配，再按插件名匹配（都大小写不敏感）；没有返回空串。</summary>
        /// <param name="guid">插件 GUID。</param>
        /// <param name="name">插件显示名。</param>
        /// <returns>中文说明（未收录返回空串）。</returns>
        public static string Lookup(string guid, string name)
        {
            EnsureLoaded();
            if (_byGuid == null)
            {
                return "";
            }
            string hit;
            if (!string.IsNullOrWhiteSpace(guid) && _byGuid.TryGetValue(guid.Trim().ToLowerInvariant(), out hit))
            {
                return hit;
            }
            if (!string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim().ToLowerInvariant(), out hit))
            {
                return hit;
            }
            // 归一化兜底——去 KK 前缀与标点后比对（「KK_SliderHighlight」↔「SliderHighlight」）
            string norm = Normalize(name);
            if (norm.Length > 0 && _byNorm.TryGetValue(norm, out hit))
            {
                return hit;
            }
            return "";
        }

        /// <summary>首次访问时从内嵌资源载入说明表（每行「键<TAB>说明」；# 开头为注释）。</summary>
        private static void EnsureLoaded()
        {
            if (_byGuid != null)
            {
                return;
            }
            _byGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byNorm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var asm = typeof(PluginNoteReader).Assembly;
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s == null)
                    {
                        _error = "内嵌说明表缺失：" + ResourceName;
                        Console.WriteLine("[插件] " + _error);
                        return;
                    }
                    using (StreamReader reader = new StreamReader(s, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string text = line.Trim();
                            if (text.Length == 0 || text[0] == '#')
                            {
                                continue;
                            }
                            int tab = text.IndexOf('\t');
                            if (tab <= 0)
                            {
                                continue;
                            }
                            string key = text.Substring(0, tab).Trim();
                            string note = text.Substring(tab + 1).Trim();
                            if (key.Length == 0 || note.Length == 0)
                            {
                                continue;
                            }
                            // 键同时进四张表——GUID / 原名 / 归一化名 / 文件名（查询时按此优先级）
                            if (!_byGuid.ContainsKey(key))
                            {
                                _byGuid[key] = note;
                            }
                            if (!_byName.ContainsKey(key))
                            {
                                _byName[key] = note;
                            }
                            if (!_byFile.ContainsKey(key))
                            {
                                _byFile[key] = note;
                            }
                            string norm = Normalize(key);
                            if (norm.Length > 0 && !_byNorm.ContainsKey(norm))
                            {
                                _byNorm[norm] = note;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _error = "说明表载入失败：" + ex.Message;
                Console.WriteLine("[插件] " + _error);
            }
        }
    }
}
