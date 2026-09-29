using System;
using System.Collections.Generic;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>场景卡插件键 ↔ 已装插件的对照——键是卡片数据区里的简名（vnge_sss / kkpe / timeline），插件是 GUID / 名称。</summary>
    public static class PluginMatcher
    {
        /// <summary>按场景插件键找匹配的已装插件（大小写不敏感）。</summary>
        /// <param name="plugins">已装插件清单（插件库扫描所得）。</param>
        /// <param name="key">场景卡插件键。</param>
        /// <returns>命中的插件（可能多项——如 vnge_* 同属一个插件；空 = 未装）。</returns>
        public static List<PluginRow> Match(List<PluginRow> plugins, string key)
        {
            List<PluginRow> hits = new List<PluginRow>();
            if (plugins == null || string.IsNullOrWhiteSpace(key))
            {
                return hits;
            }
            string text = key.Trim().ToLowerInvariant();
            // 键的首段（vnge_sss → vnge）——同一插件的多个数据区键都归到该插件
            string head = text;
            int under = text.IndexOf('_');
            if (under > 0)
            {
                head = text.Substring(0, under);
            }
            foreach (PluginRow plugin in plugins)
            {
                if (plugin.Guid.Length == 0)
                {
                    continue;
                }
                string guid = plugin.Guid.ToLowerInvariant();
                string name = (plugin.Name == null ? "" : plugin.Name).ToLowerInvariant();
                bool hit = guid.Contains(text) || name.Contains(text);
                if (!hit && head.Length >= 4)
                {
                    hit = guid.Contains(head) || name.Contains(head);
                }
                if (hit)
                {
                    hits.Add(plugin);
                }
            }
            return hits;
        }

        /// <summary>把一组插件键逐个对照（保持输入顺序，便于面板逐行展示）。</summary>
        /// <param name="plugins">已装插件清单。</param>
        /// <param name="keys">插件键清单。</param>
        /// <returns>「键 → 命中插件清单」的有序结果。</returns>
        public static List<KeyValuePair<string, List<PluginRow>>> MatchAll(List<PluginRow> plugins, List<string> keys)
        {
            List<KeyValuePair<string, List<PluginRow>>> list = new List<KeyValuePair<string, List<PluginRow>>>();
            if (keys == null)
            {
                return list;
            }
            foreach (string key in keys)
            {
                list.Add(new KeyValuePair<string, List<PluginRow>>(key, Match(plugins, key)));
            }
            return list;
        }
    }
}
