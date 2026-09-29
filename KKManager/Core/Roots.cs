using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KKManager.Core
{
    /// <summary>库根配置项——一个受管目录及其级别与扫描规则。</summary>
    public class RootEntry
    {
        /// <summary>级别：1=主库（游戏读取）· 2=缓存库（游戏不读）· 3=冷冻库（只读归档）。</summary>
        public int tier { get; set; } = 1;

        /// <summary>目录绝对路径。</summary>
        public string path { get; set; } = "";

        /// <summary>是否包含子目录。</summary>
        public bool recurse { get; set; } = true;

        /// <summary>是否只读（冷冻库——搬运时用复制，源文件保留）。</summary>
        public bool readOnly { get; set; }
        /// <summary>是否为预置主库条目（锁定）——路径 / 含子目录 / 只读 / 存在性均由规范裁决，使用者不可改。</summary>
        public bool locked { get; set; }

        /// <summary>是否离线——离线库不参与扫描（内容只读自己的库文件）；由使用者手动切换，或在更新本库时因目录不存在 / 枚举为空而自动置位。</summary>
        public bool offline { get; set; }

        /// <summary>离线备注——描述「东西放哪了」；仅离线库使用，界面在离线态用备注框代替路径框（路径本身保留为库身份键）。</summary>
        public string note { get; set; } = "";

        /// <summary>请求面字段（不落盘）——本行替换掉的旧路径；保存配置时用于判定「在原来的那一行直接改地址 = 用新地址完全替换原来的」。</summary>
        public string replacedFrom { get; set; } = "";
    }

    /// <summary>库根配置——游戏根地址 + mod 库与卡片库两组。</summary>
    public class RootsConfig
    {
        /// <summary>游戏根地址——4 条级别 1 库根（mod 主库 + 卡片 3 库）由它派生，关系固定；空 = 未设置（派生回落默认地址，面板据此提示「第一次使用请先设置」）。</summary>
        public string gameRoot { get; set; } = "";

        /// <summary>mod 库根（级别 1 派生 · 级别 2 缓存库唯一 · 级别 3 由使用者添加）。</summary>
        public List<RootEntry> modRoots { get; set; } = new List<RootEntry>();

        /// <summary>卡片库根（级别 1 三条派生 · 级别 2 及以后为使用者添加的附加库）。</summary>
        public List<RootEntry> cardRoots { get; set; } = new List<RootEntry>();
        /// <summary>是否已预置过推荐库根——首次规范化填入推荐值，之后一律以使用者的列表为准（清空即不再回填）。</summary>
        public bool seeded { get; set; }

        /// <summary>JSON 序列化选项（缩进 + 键名大小写不敏感）。</summary>
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>序列化为 JSON。</summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, Options);
        }

        /// <summary>从 JSON 反序列化（空 / 非法返回空配置；非法时出声，不静默）。</summary>
        public static RootsConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new RootsConfig();
            }
            try
            {
                RootsConfig cfg = JsonSerializer.Deserialize<RootsConfig>(json, Options);
                if (cfg == null)
                {
                    return new RootsConfig();
                }
                if (cfg.modRoots == null)
                {
                    cfg.modRoots = new List<RootEntry>();
                }
                if (cfg.cardRoots == null)
                {
                    cfg.cardRoots = new List<RootEntry>();
                }
                return cfg;
            }
            catch (JsonException ex)
            {
                // 失败必须可见——读不出来时出声（静默回落空配置会让「配置损坏」伪装成「未设置」）
                Console.WriteLine("[配置] 库根配置解析失败（按空配置处理）：" + ex.Message);
                return new RootsConfig();
            }
        }

        /// <summary>按级别升序排序后的 mod 根（同级别保持配置顺序）。</summary>
        public List<RootEntry> ModRootsOrdered()
        {
            List<RootEntry> list = NonEmpty(modRoots);
            return new List<RootEntry>(System.Linq.Enumerable.OrderBy(list, e => e.tier));
        }

        /// <summary>按级别升序排序后的卡片根（同级别保持配置顺序）。</summary>
        public List<RootEntry> CardRootsOrdered()
        {
            List<RootEntry> list = NonEmpty(cardRoots);
            return new List<RootEntry>(System.Linq.Enumerable.OrderBy(list, e => e.tier));
        }

        /// <summary>取出路径非空的条目（空列表安全）。</summary>
        private static List<RootEntry> NonEmpty(List<RootEntry> source)
        {
            List<RootEntry> list = new List<RootEntry>();
            if (source == null)
            {
                return list;
            }
            foreach (RootEntry e in source)
            {
                if (!string.IsNullOrWhiteSpace(e.path))
                {
                    list.Add(e);
                }
            }
            return list;
        }
    }

    /// <summary>级别语义（供服务端 / 前端共用）。</summary>
    public static class Tier
    {
        /// <summary>主库——游戏实际读取。</summary>
        public const int Main = 1;

        /// <summary>缓存库——游戏不读，可一键搬入主库。</summary>
        public const int Cache = 2;

        /// <summary>冷冻库——只读，仅用于标红提示。</summary>
        public const int Cold = 3;

        /// <summary>级别名。</summary>
        public static string Name(int tier)
        {
            switch (tier)
            {
                case 1: return "主库";
                case 2: return "缓存库";
                case 3: return "冷冻库";
                default: return "级别" + tier;
            }
        }
    }

    /// <summary>库根规则——库根由使用者自己添加与掌管（不做全库盲扫）；游戏根仅用于生成推荐条目，不反向覆盖配置。</summary>
    public static class RootsRules
    {
        /// <summary>默认游戏根地址（未设置时的预填值）。</summary>
        public const string DefaultGameRoot = "D:\\SteamLibrary\\GameGeneral\\Koikatu";

        /// <summary>mod 主库相对子路径。</summary>
        public const string ModMainSub = "mods";

        /// <summary>mod 缓存库相对子路径（初次设置游戏根时创建）。</summary>
        public const string ModCacheSub = "mods_cache";

        /// <summary>人物卡主库相对子路径（含子目录）。</summary>
        public const string CardFemaleSub = "UserData\\chara\\female";

        /// <summary>服装卡主库相对子路径（含子目录）。</summary>
        public const string CardCoordinateSub = "UserData\\coordinate";

        /// <summary>场景卡主库相对子路径（含子目录）。</summary>
        public const string CardStudioSub = "UserData\\Studio";

        /// <summary>判断某个 mod 库根是否只读（无记录时保守视为只读，避免误删源文件）——搬运 / 互换 / 正名的源侧判定唯一实现。</summary>
        public static bool IsReadOnlyRoot(RootsConfig cfg, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }
            if (cfg == null || cfg.modRoots == null)
            {
                return true;
            }
            string norm = path.TrimEnd('\\', '/');
            foreach (RootEntry r in cfg.modRoots)
            {
                if (string.Equals((r.path ?? "").TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase))
                {
                    return r.readOnly;
                }
            }
            return true;
        }

        /// <summary>规范化游戏根地址（去空白与尾部斜杠；空则回落默认地址）。</summary>
        public static string NormalizeGameRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DefaultGameRoot;
            }
            return path.Trim().TrimEnd('\\', '/');
        }

        /// <summary>拼出游戏根下的一条路径。</summary>
        public static string UnderGameRoot(string gameRoot, string sub)
        {
            return NormalizeGameRoot(gameRoot) + "\\" + sub;
        }

        /// <summary>旧版文件名的中缀——加在扩展名前（如 `xxx.old.zipmod` / `xxx.old.zip`）；仍属 mod 扫描面，库内可见。</summary>
        public const string OldInfix = ".old";

        /// <summary>文件名是否带旧版中缀（扩展名前的 .old 段）——「结尾是 old」即旧版引用的判据。</summary>
        public static bool IsOldFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }
            string stem = Path.GetFileNameWithoutExtension(fileName);
            return stem.EndsWith(OldInfix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把文件名标成旧版形态（扩展名前加 .old）；已是旧版形态则原样返回。</summary>
        public static string MakeOldFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || IsOldFileName(fileName))
            {
                return fileName;
            }
            string ext = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            return stem + OldInfix + ext;
        }

        /// <summary>把旧版文件名正名（去掉扩展名前的 .old 段）；不带旧版中缀则原样返回。</summary>
        public static string StripOldFileName(string fileName)
        {
            if (!IsOldFileName(fileName))
            {
                return fileName;
            }
            string ext = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            return stem.Substring(0, stem.Length - OldInfix.Length) + ext;
        }

        /// <summary>配置里是否已有可用的 mod 缓存库（判定「初次设置游戏根」用）。</summary>
        public static bool HasModCache(RootsConfig cfg)
        {
            if (cfg == null || cfg.modRoots == null)
            {
                return false;
            }
            foreach (RootEntry e in cfg.modRoots)
            {
                if (e.tier == Tier.Cache && !string.IsNullOrWhiteSpace(e.path))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>取出配置里的 mod 缓存库路径（无则返回空串）。</summary>
        public static string ModCachePath(RootsConfig cfg)
        {
            if (cfg == null || cfg.modRoots == null)
            {
                return "";
            }
            foreach (RootEntry e in cfg.modRoots)
            {
                if (e.tier == Tier.Cache && !string.IsNullOrWhiteSpace(e.path))
                {
                    return e.path;
                }
            }
            return "";
        }
        /// <summary>推荐 mod 库根——游戏根\mods（级别 1 主库）+ 游戏根\mods_cache（级别 2 缓存库）。</summary>
        public static List<RootEntry> RecommendModRoots(string gameRoot)
        {
            List<RootEntry> list = new List<RootEntry>();
            list.Add(new RootEntry { tier = Tier.Main, path = UnderGameRoot(gameRoot, ModMainSub), recurse = true, locked = true });
            list.Add(new RootEntry { tier = Tier.Cache, path = UnderGameRoot(gameRoot, ModCacheSub), recurse = true });
            return list;
        }
        /// <summary>推荐卡片库根——UserData 下 female / coordinate / Studio（均含子目录，级别 1）。</summary>
        public static List<RootEntry> RecommendCardRoots(string gameRoot)
        {
            List<RootEntry> list = new List<RootEntry>();
            list.Add(new RootEntry { tier = Tier.Main, path = UnderGameRoot(gameRoot, CardFemaleSub), recurse = true, locked = true });
            list.Add(new RootEntry { tier = Tier.Main, path = UnderGameRoot(gameRoot, CardCoordinateSub), recurse = true, locked = true });
            list.Add(new RootEntry { tier = Tier.Main, path = UnderGameRoot(gameRoot, CardStudioSub), recurse = true, locked = true });
            return list;
        }
        /// <summary>清洗使用者列表——去空路径、级别夹在合法区间、同路径去重（保留首条，其余设置原样不动）。</summary>
        private static List<RootEntry> Clean(List<RootEntry> source, int minTier, int maxTier)
        {
            List<RootEntry> list = new List<RootEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return list;
            }
            foreach (RootEntry e in source)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.path))
                {
                    continue;
                }
                string path = e.path.Trim();
                if (!seen.Add(path))
                {
                    continue;
                }
                int tier = e.tier;
                if (tier < minTier)
                {
                    tier = minTier;
                }
                if (tier > maxTier)
                {
                    tier = maxTier;
                }
                list.Add(new RootEntry { tier = tier, path = path, recurse = e.recurse, readOnly = e.readOnly, locked = e.locked, offline = e.offline, note = e.note });
            }
            return list;
        }

        /// <summary>规范化库根配置——库根由使用者添加：首次填入推荐值；之后以使用者列表为准（仅去空路径 / 同路径去重 / 级别夹在合法区间）；预置条目（主库 · 缓存库）按预置路径认定并压回规范形态，不可改、不可删。</summary>
        public static void Normalize(RootsConfig cfg)
        {
            if (cfg == null)
            {
                return;
            }
            // 游戏根地址：空 = 未设置——保留空值（面板据此提示「第一次使用请先设置」）；
            // 派生路径一律经 NormalizeGameRoot 回落默认地址，故预置条目形态不受空值影响
            cfg.gameRoot = cfg.gameRoot == null ? "" : cfg.gameRoot.Trim().TrimEnd('\\', '/');
            if (cfg.modRoots == null)
            {
                cfg.modRoots = new List<RootEntry>();
            }
            if (cfg.cardRoots == null)
            {
                cfg.cardRoots = new List<RootEntry>();
            }

            // 库根由使用者自行添加（不做全库盲扫）——首次（从未预置过）填推荐值；之后一律以使用者的列表为准，清空不回填
            if (!cfg.seeded)
            {
                if (cfg.modRoots.Count == 0)
                {
                    cfg.modRoots = RecommendModRoots(cfg.gameRoot);
                }
                if (cfg.cardRoots.Count == 0)
                {
                    cfg.cardRoots = RecommendCardRoots(cfg.gameRoot);
                }
                cfg.seeded = true;
            }

            cfg.modRoots = Clean(cfg.modRoots, Tier.Main, Tier.Cold);
            cfg.cardRoots = Clean(cfg.cardRoots, Tier.Main, Tier.Cache);

            // 预置条目锁定：路径与预置一致的条目认定并压回规范形态（主库 = mods / female / coordinate / Studio，缓存库 = mods_cache）——不可改、不可删
            LockPresets(cfg.modRoots, RecommendModRoots(cfg.gameRoot));
            LockPresets(cfg.cardRoots, RecommendCardRoots(cfg.gameRoot));
        }
        /// <summary>按预置值认定并压回锁定条目——路径与预置一致者锁定（存量迁移 + 防篡改），形态一律取预置值（主库 1 / 缓存库 2 · 均含子目录 · 非只读）。</summary>
        private static void LockPresets(List<RootEntry> list, List<RootEntry> recommended)
        {
            foreach (RootEntry e in list)
            {
                bool matched = false;
                foreach (RootEntry r in recommended)
                {
                    if (!r.locked || !string.Equals(e.path, r.path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    e.locked = true;
                    e.tier = r.tier;
                    e.recurse = r.recurse;
                    e.readOnly = r.readOnly;
                    matched = true;
                    break;
                }
                if (e.locked && !matched)
                {
                    // 预置路径已变的存量锁定条目——保留锁定与级别，形态仍压回「含子目录 · 非只读」
                    e.recurse = true;
                    e.readOnly = false;
                }
            }
        }
        /// <summary>游戏根变更的预置条目处置——锁定条目（mods / female / coordinate / Studio）按新根重派生（旧根的条目不复活）；mod 缓存库槽位「未被使用者改过」（路径仍等于旧根派生值）时跟随新根。返回出声清单（cacheMoved = 槽位是否被移动）。</summary>
        public static List<string> ApplyGameRootChange(RootsConfig cfg, string previousGameRoot, out bool cacheMoved)
        {
            List<string> warnings = new List<string>();
            cacheMoved = false;
            if (cfg == null)
            {
                return warnings;
            }
            if (cfg.modRoots == null)
            {
                cfg.modRoots = new List<RootEntry>();
            }
            if (cfg.cardRoots == null)
            {
                cfg.cardRoots = new List<RootEntry>();
            }
            // [段1] 锁定条目按新根重派生——先清旧根的，再补新根的（只补锁定条目；缓存库槽位另行处置）
            cfg.modRoots.RemoveAll(e => e.locked);
            cfg.cardRoots.RemoveAll(e => e.locked);
            foreach (RootEntry r in RecommendModRoots(cfg.gameRoot))
            {
                if (r.locked)
                {
                    cfg.modRoots.Add(r);
                }
            }
            cfg.cardRoots.AddRange(RecommendCardRoots(cfg.gameRoot));
            // [段2] 缓存库槽位跟随——路径仍等于旧根派生值即「从未被改过」（首次设置时旧根为空，派生值即默认根）
            string oldCache = UnderGameRoot(previousGameRoot, ModCacheSub);
            string newCache = UnderGameRoot(cfg.gameRoot, ModCacheSub);
            foreach (RootEntry e in cfg.modRoots)
            {
                if (e.tier != Tier.Cache || string.IsNullOrWhiteSpace(e.path))
                {
                    continue;
                }
                if (!string.Equals(e.path.Trim(), oldCache, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                e.path = newCache;
                cacheMoved = true;
                warnings.Add("mod 缓存库槽位随游戏根移到「" + newCache + "」（槽位未被改过时跟随新根）");
                break;
            }
            return warnings;
        }
        /// <summary>该库根的数据是否落主库文件——预置条目（主库 · 缓存库槽位）与全局索引同处主库 db；使用者添加的库根各自一个 db。</summary>
        public static bool UsesCoreDb(RootEntry entry, bool isMods)
        {
            if (entry == null)
            {
                return true;
            }
            return entry.locked || (isMods && entry.tier == Tier.Cache);
        }

        /// <summary>该库根是否适用离线语义——预置条目（锁定主库 / mod 缓存库槽位）不适用（其存在性由规范裁决，路径不可改也不可删）。</summary>
        public static bool CanOffline(RootEntry entry, bool isMods)
        {
            if (entry == null)
            {
                return false;
            }
            if (entry.locked)
            {
                return false;
            }
            if (isMods && entry.tier == Tier.Cache)
            {
                return false;
            }
            return true;
        }

        /// <summary>库文件序号对应的文件名——主库目录下 lib_&lt;序号&gt;.db。</summary>
        public static string LibDbFileName(int lib)
        {
            return "lib_" + lib + ".db";
        }

        /// <summary>清掉一组列表里的锁定标记。</summary>
        private static void ClearLockList(List<RootEntry> list)
        {
            if (list == null)
            {
                return;
            }
            foreach (RootEntry e in list)
            {
                if (e != null)
                {
                    e.locked = false;
                }
            }
        }
        /// <summary>清掉外部输入带来的锁定标记——主库身份只由规范按路径认定，不信任请求。</summary>
        public static void ClearLockFlags(RootsConfig cfg)
        {
            if (cfg == null)
            {
                return;
            }
            ClearLockList(cfg.modRoots);
            ClearLockList(cfg.cardRoots);
        }
        /// <summary>还原一组列表里的锁定条目——以磁盘现状为准（不可删、不可改），被拒的改动出声。</summary>
        private static void RestoreMainList(List<RootEntry> previous, List<RootEntry> current, string label, List<string> warnings)
        {
            if (previous == null || current == null)
            {
                return;
            }
            foreach (RootEntry p in previous)
            {
                if (!p.locked)
                {
                    continue;
                }
                int index = current.FindIndex(e => string.Equals(e.path, p.path, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    current.Add(new RootEntry { tier = p.tier, path = p.path, recurse = p.recurse, readOnly = p.readOnly, locked = true, offline = false });
                    warnings.Add(label + "「" + p.path + "」不可删除——已按原样保留");
                    continue;
                }
                RootEntry c = current[index];
                if (c.tier != p.tier || c.recurse != p.recurse || c.readOnly != p.readOnly)
                {
                    warnings.Add(label + "「" + p.path + "」由规范锁定——本次改动已忽略");
                }
                current[index] = new RootEntry { tier = p.tier, path = p.path, recurse = p.recurse, readOnly = p.readOnly, locked = true, offline = false, note = c.note };
            }
        }
        /// <summary>缓存库槽位不可删——请求里丢了缓存库时按磁盘现状补回（路径与勾选归使用者，故只保证存在性），并出声。</summary>
        private static void EnsureCacheSlot(List<RootEntry> previous, List<RootEntry> current, string label, List<string> warnings)
        {
            if (previous == null || current == null)
            {
                return;
            }
            foreach (RootEntry cached in previous)
            {
                if (cached.tier != Tier.Cache || string.IsNullOrWhiteSpace(cached.path))
                {
                    continue;
                }
                bool found = false;
                foreach (RootEntry e in current)
                {
                    if (e != null && e.tier == Tier.Cache && !string.IsNullOrWhiteSpace(e.path))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    current.Add(new RootEntry { tier = cached.tier, path = cached.path, recurse = cached.recurse, readOnly = cached.readOnly, offline = false, note = cached.note });
                    warnings.Add(label + "「" + cached.path + "」是缓存库槽位、不可删除——已按原样保留");
                }
                return;
            }
        }
        /// <summary>还原请求里的预置条目——主库（含卡片主库）：改动忽略、缺失补回；缓存库：只保证槽位存在（路径与勾选归使用者）。返回出声清单。</summary>
        public static List<string> RestorePresets(RootsConfig previous, RootsConfig current)
        {
            List<string> warnings = new List<string>();
            if (previous == null || current == null)
            {
                return warnings;
            }
            RestoreMainList(previous.modRoots, current.modRoots, "mod 预置库根", warnings);
            RestoreMainList(previous.cardRoots, current.cardRoots, "卡片预置库根", warnings);
            EnsureCacheSlot(previous.modRoots, current.modRoots, "mod 缓存库", warnings);
            return warnings;
        }
    }
}
