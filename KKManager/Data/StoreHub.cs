using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KKManager.Core;

namespace KKManager.Data
{
    /// <summary>多库协调器——主库 + 各库文件（使用者添加的库根各自一个 db）：按库路由 · 跨库合并 · 跨库 mod 重算 · 存量迁移。</summary>
    public class StoreHub : IDisposable
    {
        /// <summary>主库 db 绝对路径。</summary>
        private readonly string _corePath;

        /// <summary>数据目录（主库所在目录，各库文件与它同级）。</summary>
        private readonly string _dir;

        /// <summary>已打开的库 Store（库序号 → Store）。</summary>
        private readonly Dictionary<int, Store> _libStores = new Dictionary<int, Store>();

        /// <summary>库序号 → db 路径（反查用）。</summary>
        private readonly Dictionary<string, int> _libByDb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>库根路径（小写）→ 库序号。</summary>
        private readonly Dictionary<string, int> _libOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>下一个可分配的库序号。</summary>
        private int _nextLib = 1;

        /// <summary>打开主库并载入库序号映射（不清库、不迁移——迁移走 EnsureMigrated）。</summary>
        public StoreHub(string corePath)
        {
            _corePath = Path.GetFullPath(corePath);
            string dir = Path.GetDirectoryName(_corePath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = ".";
            }
            _dir = dir;
            Core = new Store(_corePath);
            LoadLibMap();
        }

        /// <summary>主库（设置表 / mod 主表 / 预置库根数据）。</summary>
        public Store Core { get; }

        /// <summary>主库文件绝对路径。</summary>
        public string CorePath
        {
            get { return _corePath; }
        }

        /// <summary>载入库序号映射（存主库设置表，键前缀 libdb:）。</summary>
        private void LoadLibMap()
        {
            Dictionary<string, string> all = Core.LoadSettings();
            foreach (KeyValuePair<string, string> kv in all)
            {
                if (!kv.Key.StartsWith("libdb:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int n = 0;
                if (!int.TryParse(kv.Value, out n))
                {
                    continue;
                }
                _libOf[kv.Key.Substring(6)] = n;
                if (n >= _nextLib)
                {
                    _nextLib = n + 1;
                }
            }
        }

        /// <summary>取某个库根的库序号（没有则分配并落盘）。</summary>
        public int LibOf(string rootPath)
        {
            string key = (rootPath ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                return 0;
            }
            int n = 0;
            if (_libOf.TryGetValue(key, out n))
            {
                return n;
            }
            n = _nextLib;
            _nextLib = _nextLib + 1;
            _libOf[key] = n;
            Core.SetSetting("libdb:" + key, n.ToString());
            return n;
        }

        /// <summary>按库序号取 Store（0 = 主库；懒开并缓存）。</summary>
        public Store StoreByLib(int lib)
        {
            if (lib <= 0)
            {
                return Core;
            }
            Store s = null;
            if (_libStores.TryGetValue(lib, out s))
            {
                return s;
            }
            string path = Path.Combine(_dir, RootsRules.LibDbFileName(lib));
            s = new Store(path, _corePath);
            _libStores[lib] = s;
            _libByDb[s.DbPath] = lib;
            return s;
        }

        /// <summary>取某条库根对应的 Store——预置条目与 mod 缓存库走主库，使用者添加的库根各自一个库文件。</summary>
        public Store StoreFor(RootEntry entry, bool isMods)
        {
            if (RootsRules.UsesCoreDb(entry, isMods))
            {
                return Core;
            }
            return StoreByLib(LibOf(entry.path));
        }

        /// <summary>取某条库根的库序号（走主库的条目返回 0）。</summary>
        public int LibOfEntry(RootEntry entry, bool isMods)
        {
            if (entry == null || RootsRules.UsesCoreDb(entry, isMods))
            {
                return 0;
            }
            return LibOf(entry.path);
        }

        /// <summary>某 Store 对应的库序号（主库返回 0）。</summary>
        public int LibOfStore(Store store)
        {
            if (store == null || store.IsCore)
            {
                return 0;
            }
            int n = 0;
            if (_libByDb.TryGetValue(store.DbPath, out n))
            {
                return n;
            }
            return 0;
        }

        /// <summary>按库根路径取库序号（配置里找不到时按主库处理）。</summary>
        public int LibOfRootPath(RootsConfig cfg, string rootPath)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(rootPath))
            {
                return 0;
            }
            string norm = rootPath.Trim();
            foreach (RootEntry e in cfg.modRoots)
            {
                if (string.Equals((e.path ?? "").Trim(), norm, StringComparison.OrdinalIgnoreCase))
                {
                    return LibOfEntry(e, true);
                }
            }
            foreach (RootEntry e in cfg.cardRoots)
            {
                if (string.Equals((e.path ?? "").Trim(), norm, StringComparison.OrdinalIgnoreCase))
                {
                    return LibOfEntry(e, false);
                }
            }
            return 0;
        }

        /// <summary>全部参与合并的 Store——主库 + 配置里各库根对应的库文件（去重）。</summary>
        public List<Store> AllStores(RootsConfig cfg)
        {
            List<Store> list = new List<Store>();
            list.Add(Core);
            HashSet<int> seen = new HashSet<int>();
            AddLibStores(list, seen, cfg == null ? null : cfg.modRoots, true);
            AddLibStores(list, seen, cfg == null ? null : cfg.cardRoots, false);
            return list;
        }

        /// <summary>把一组库根对应的库文件加进清单（已加的跳过）。</summary>
        private void AddLibStores(List<Store> list, HashSet<int> seen, List<RootEntry> roots, bool isMods)
        {
            if (roots == null)
            {
                return;
            }
            foreach (RootEntry e in roots)
            {
                int lib = LibOfEntry(e, isMods);
                if (lib <= 0 || !seen.Add(lib))
                {
                    continue;
                }
                list.Add(StoreByLib(lib));
            }
        }

        /// <summary>全库扫描前按库根配置准备库文件（含首次迁移：把主库里的附加库 / 冷藏库数据搬进各自的库文件）。</summary>
        public void EnsureMigrated(RootsConfig cfg)
        {
            if (Core.GetSetting("dbSplit") == "1")
            {
                return;
            }
            MigrateRoots(cfg == null ? null : cfg.modRoots, true);
            MigrateRoots(cfg == null ? null : cfg.cardRoots, false);
            Core.SetSetting("dbSplit", "1");
            foreach (KeyValuePair<int, Store> kv in _libStores)
            {
                kv.Value.Vacuum();
            }
            Core.Vacuum();
            Console.WriteLine("[迁移] 按库拆分完成——各附加库 / 冷藏库已独立为库文件");
        }

        /// <summary>把一组库根的数据从主库搬进各自的库文件。</summary>
        private void MigrateRoots(List<RootEntry> roots, bool isMods)
        {
            if (roots == null)
            {
                return;
            }
            foreach (RootEntry e in roots)
            {
                if (RootsRules.UsesCoreDb(e, isMods))
                {
                    continue;
                }
                Store target = StoreByLib(LibOf(e.path));
                target.AdoptRootFromCore(e.path);
                Console.WriteLine("[迁移] " + e.path + " → " + Path.GetFileName(target.DbPath));
            }
        }

        /// <summary>彻底删除某个库根的数据——关连接、删库文件（含 WAL / SHM）、清库序号映射；主库条目（预置主库 / mod 缓存库槽位）不适用（返回 false）。</summary>
        public bool DropLib(RootEntry entry, bool isMods)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.path))
            {
                return false;
            }
            if (RootsRules.UsesCoreDb(entry, isMods))
            {
                return false;
            }
            return DropLibByPath(entry.path);
        }

        /// <summary>按路径彻底删除库文件与映射（彻底删除数据 / 原行改地址换库 共用）。</summary>
        public bool DropLibByPath(string rootPath)
        {
            string key = (rootPath ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                return false;
            }
            int lib = 0;
            if (!_libOf.TryGetValue(key, out lib))
            {
                return false;
            }
            Store s = null;
            if (_libStores.TryGetValue(lib, out s))
            {
                _libStores.Remove(lib);
                _libByDb.Remove(s.DbPath);
                s.Dispose();
            }
            _libOf.Remove(key);
            Core.DeleteSetting("libdb:" + key);
            string path = Path.Combine(_dir, RootsRules.LibDbFileName(lib));
            DeleteFileIfExists(path);
            DeleteFileIfExists(path + "-wal");
            DeleteFileIfExists(path + "-shm");
            Console.WriteLine("[库数据] 已彻底删除：" + rootPath + " → " + Path.GetFileName(path));
            return true;
        }

        /// <summary>删除文件（不存在跳过；失败出声，不静默）。</summary>
        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[库数据] 删除失败（" + path + "）：" + ex.Message);
            }
        }

        /// <summary>释放全部连接。</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<int, Store> kv in _libStores)
            {
                kv.Value.Dispose();
            }
            _libStores.Clear();
            Core.Dispose();
        }

        // ---------- 跨库合并查询 ----------

        /// <summary>库快照——mod 计数取主库（全局表），卡片计数与四色跨库累加。</summary>
        public Snapshot Snapshot(RootsConfig cfg)
        {
            Snapshot core = Core.Snapshot();
            Snapshot merged = new Snapshot();
            merged.Mods = core.Mods;
            merged.ModsTier1 = core.ModsTier1;
            merged.ModsTier2 = core.ModsTier2;
            merged.ModsTier3 = core.ModsTier3;
            merged.DupMods = core.DupMods;

            long modFiles = 0;
            foreach (Store s in AllStores(cfg))
            {
                Snapshot one = s.Snapshot();
                merged.Cards = merged.Cards + one.Cards;
                merged.CardsReady = merged.CardsReady + one.CardsReady;
                merged.CardsWithRefs = merged.CardsWithRefs + one.CardsWithRefs;
                merged.Refs = merged.Refs + one.Refs;
                merged.Colors.Green = merged.Colors.Green + one.Colors.Green;
                merged.Colors.Yellow = merged.Colors.Yellow + one.Colors.Yellow;
                merged.Colors.Red = merged.Colors.Red + one.Colors.Red;
                merged.Colors.Black = merged.Colors.Black + one.Colors.Black;
                modFiles = modFiles + s.CountModFiles();
            }
            merged.ModFiles = modFiles;
            HashSet<string> refGuids = RefGuids(cfg);

            long unused = 0;
            foreach (KeyValuePair<string, long> kv in Core.MainModSizes())
            {
                if (!refGuids.Contains(kv.Key))
                {
                    unused = unused + 1;
                }
            }
            merged.UnusedMods = unused;
            return merged;
        }

        /// <summary>全库被卡片引用过的 guid 集合（跨库合并）——「未被引用」判定的唯一基准；跨库集合一律 Ordinal 比较（大小写不敏感会少算）。</summary>
        public HashSet<string> RefGuids(RootsConfig cfg)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Store s in AllStores(cfg))
            {
                set.UnionWith(s.DistinctRefGuids());
            }
            return set;
        }

        /// <summary>缺失 guid 排行（跨库合并，按被引用卡片数倒序）。</summary>
        public List<KeyValuePair<string, long>> MissingRanking(RootsConfig cfg, int top)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Store s in AllStores(cfg))
            {
                foreach (KeyValuePair<string, long> kv in s.MissingRanking(0))
                {
                    long n = 0;
                    if (map.TryGetValue(kv.Key, out n))
                    {
                        map[kv.Key] = n + kv.Value;
                    }
                    else
                    {
                        map[kv.Key] = kv.Value;
                    }
                }
            }
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(map);
            list.Sort(CompareMissing);
            if (top > 0 && list.Count > top)
            {
                list.RemoveRange(top, list.Count - top);
            }
            return list;
        }

        /// <summary>缺失排行排序——被引用卡数倒序，同数按 guid。</summary>
        private static int CompareMissing(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
        {
            int c = b.Value.CompareTo(a.Value);
            if (c != 0)
            {
                return c;
            }
            return string.CompareOrdinal(a.Key, b.Key);
        }

        /// <summary>卡片列表——指定 root 时只查该库；否则跨库合并后排序分页（order：排序键 mtime / size；desc：方向，只对 size 生效）。</summary>
        public List<CardRow> QueryCards(RootsConfig cfg, int page, int size, string filter, string q, string folder, string root, string order, bool desc)
        {
            if (!string.IsNullOrEmpty(root))
            {
                int lib = LibOfRootPath(cfg, root);
                List<CardRow> one = StoreByLib(lib).QueryCards(page, size, filter, q, folder, root, order, desc);
                foreach (CardRow r in one)
                {
                    r.Lib = lib;
                }
                return one;
            }

            List<CardRow> list = new List<CardRow>();
            foreach (Store s in AllStores(cfg))
            {
                int lib = LibOfStore(s);
                foreach (CardRow r in s.QueryCards(1, 0, filter, q, folder, null, order, desc))
                {
                    r.Lib = lib;
                    list.Add(r);
                }
            }
            list.Sort(new CardComparer(order, desc));
            return Slice(list, page, size);
        }

        /// <summary>卡片排序器——文件夹 · 主键（修改时间倒序 / 文件大小按方向）· 库位 · 编号。</summary>
        private sealed class CardComparer : IComparer<CardRow>
        {
            /// <summary>排序键（mtime / size）。</summary>
            private readonly string _order;

            /// <summary>组内方向——真 = 降序（只对 size 生效）。</summary>
            private readonly bool _desc;

            /// <summary>构造排序器。</summary>
            public CardComparer(string order, bool desc)
            {
                _order = order;
                _desc = desc;
            }

            /// <summary>比较两行卡片（负 / 零 / 正）。</summary>
            public int Compare(CardRow a, CardRow b)
            {
                int c = string.Compare(a.Folder ?? "", b.Folder ?? "", StringComparison.OrdinalIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
                if (_order == "size")
                {
                    if (_desc)
                    {
                        c = b.Size.CompareTo(a.Size);
                    }
                    else
                    {
                        c = a.Size.CompareTo(b.Size);
                    }
                }
                else if (_order == "file")
                {
                    c = string.Compare(a.FileName ?? "", b.FileName ?? "", StringComparison.OrdinalIgnoreCase);
                    if (_desc)
                    {
                        c = -c;
                    }
                }
                else if (_order == "chara")
                {
                    bool ea = string.IsNullOrEmpty(a.CharaName);
                    bool eb = string.IsNullOrEmpty(b.CharaName);
                    if (ea != eb)
                    {
                        c = ea ? 1 : -1;
                    }
                    else if (!ea)
                    {
                        c = string.Compare(a.CharaName, b.CharaName, StringComparison.OrdinalIgnoreCase);
                        if (_desc)
                        {
                            c = -c;
                        }
                    }
                }
                else
                {
                    c = string.CompareOrdinal(b.Mtime ?? "", a.Mtime ?? "");
                }
                if (c != 0)
                {
                    return c;
                }
                c = a.Lib.CompareTo(b.Lib);
                if (c != 0)
                {
                    return c;
                }
                return a.Id.CompareTo(b.Id);
            }
        }

        /// <summary>跨库卡片合并后的分页切片。</summary>
        private static List<T> Slice<T>(List<T> list, int page, int size)
        {
            if (page <= 0)
            {
                page = 1;
            }
            if (size <= 0 || size >= list.Count)
            {
                return list;
            }
            int off = (page - 1) * size;
            if (off >= list.Count)
            {
                return new List<T>();
            }
            int take = size;
            if (take > list.Count - off)
            {
                take = list.Count - off;
            }
            return list.GetRange(off, take);
        }

        /// <summary>卡片文件夹清单（跨库拼接）。</summary>
        public List<object> QueryFolders(RootsConfig cfg)
        {
            List<object> list = new List<object>();
            foreach (Store s in AllStores(cfg))
            {
                list.AddRange(s.QueryFolders());
            }
            return list;
        }

        /// <summary>mod 列表——mod 行取主库（全局表），被引用卡数跨库汇总后排序分页。</summary>
        public List<ModRow> QueryMods(RootsConfig cfg, int page, int size, string filter, string q)
        {
            List<ModRow> rows = Core.QueryMods(1, 0, "all", q);
            Dictionary<string, long> used = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Store s in AllStores(cfg))
            {
                foreach (KeyValuePair<string, long> kv in s.RefCountsByGuid())
                {
                    long n = 0;
                    if (used.TryGetValue(kv.Key, out n))
                    {
                        used[kv.Key] = n + kv.Value;
                    }
                    else
                    {
                        used[kv.Key] = kv.Value;
                    }
                }
            }

            Dictionary<string, ModOldRecord> olds = Core.LoadModOldMap();
            List<ModRow> list = new List<ModRow>();
            foreach (ModRow m in rows)
            {
                long n = 0;
                if (used.TryGetValue(m.Guid, out n))
                {
                    m.Used = n;
                }
                else
                {
                    m.Used = 0;
                }
                ModOldRecord oldRec = null;
                if (olds.TryGetValue(m.Guid, out oldRec))
                {
                    m.HasOld = true;
                    m.OldName = oldRec.OldName;
                }
                if (!MatchModFilter(m, filter))
                {
                    continue;
                }
                list.Add(m);
            }
            list.Sort(CompareMods);
            return Slice(list, page, size);
        }

        /// <summary>mod 列表筛选（用跨库汇总后的被引用卡数判定）。</summary>
        private static bool MatchModFilter(ModRow m, string filter)
        {
            switch (filter)
            {
                case "used":
                    return m.Used > 0;
                case "unused":
                    return m.Used == 0 && m.Tier == 1;
                case "tier1":
                    return m.Tier == 1;
                case "tier2":
                    return m.Tier == 2;
                case "tier3":
                    return m.Tier == 3;
                case "dup":
                    return m.DupCount > 1;
                default:
                    return true;
            }
        }

        /// <summary>mod 排序——被引用卡数倒序 · guid。</summary>
        private static int CompareMods(ModRow a, ModRow b)
        {
            int c = b.Used.CompareTo(a.Used);
            if (c != 0)
            {
                return c;
            }
            return string.CompareOrdinal(a.Guid, b.Guid);
        }

        /// <summary>作者清单（主库聚合表 mod_author，按 mod 数量倒序）——聚合表为空而 mod 主表非空时自愈重建一次（旧库首次使用）。</summary>
        public List<AuthorRow> QueryAuthors()
        {
            List<AuthorRow> list = Core.QueryAuthors();
            if (list.Count == 0 && Core.Snapshot().Mods > 0)
            {
                Console.WriteLine("[作者索引] 聚合表为空而 mod 主表非空 → 重建一次");
                Core.RefreshModAuthors();
                list = Core.QueryAuthors();
            }
            return list;
        }

        /// <summary>某 guid 被哪些卡片引用（跨库合并）。</summary>
        public List<CardRow> QueryCardsByMod(RootsConfig cfg, string guid, int page, int size)
        {
            List<CardRow> list = new List<CardRow>();
            foreach (Store s in AllStores(cfg))
            {
                int lib = LibOfStore(s);
                foreach (CardRow r in s.QueryCardsByMod(guid, 1, 0))
                {
                    r.Lib = lib;
                    list.Add(r);
                }
            }
            list.Sort(CompareCardsByMod);
            return Slice(list, page, size);
        }

        /// <summary>反查卡片排序——库根 · 文件夹 · 文件名。</summary>
        private static int CompareCardsByMod(CardRow a, CardRow b)
        {
            int c = string.Compare(a.RootPath ?? "", b.RootPath ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
            c = string.Compare(a.Folder ?? "", b.Folder ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
            return string.Compare(a.FileName ?? "", b.FileName ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>某 guid 被引用的卡片总数（跨库）。</summary>
        public long CountCardsByMod(RootsConfig cfg, string guid)
        {
            long n = 0;
            foreach (Store s in AllStores(cfg))
            {
                n = n + s.CountCardsByMod(guid);
            }
            return n;
        }
        /// <summary>重复副本组的引用卡片（跨库合并）——每组总数 + 前 top 张；一次批量查询，不再逐组重算四色聚合。</summary>
        public Dictionary<string, DupCardRefs> QueryDupCardRefs(RootsConfig cfg, List<string> guids, int top)
        {
            Dictionary<string, DupCardRefs> map = new Dictionary<string, DupCardRefs>(StringComparer.Ordinal);
            if (guids == null || guids.Count == 0)
            {
                return map;
            }
            // [段1] 每个 guid 恒有条目——无引用的组显式记 0（消费方不靠字段缺失猜）
            foreach (string g in guids)
            {
                if (!map.ContainsKey(g))
                {
                    map[g] = new DupCardRefs();
                }
            }
            // [段2] 逐库取计数与前 top 行——轻量查询（不含四色聚合）
            foreach (Store s in AllStores(cfg))
            {
                int lib = LibOfStore(s);
                Dictionary<string, long> counts = s.CountCardsByMods(guids);
                foreach (KeyValuePair<string, long> kv in counts)
                {
                    DupCardRefs hit = null;
                    if (map.TryGetValue(kv.Key, out hit))
                    {
                        hit.Total = hit.Total + kv.Value;
                    }
                }
                foreach (CardRefRow row in s.RefCardsByMods(guids))
                {
                    DupCardRefs hit = null;
                    if (!map.TryGetValue(row.Guid, out hit))
                    {
                        continue;
                    }
                    CardRow cr = new CardRow();
                    cr.Id = row.Id;
                    cr.FileName = row.FileName;
                    cr.RootPath = row.RootPath;
                    cr.Folder = row.Folder;
                    cr.HasThumb = row.HasThumb;
                    cr.Lib = lib;
                    hit.Top.Add(cr);
                }
            }
            // [段3] 跨库合并后统一排序取前 top——与「引用卡片」弹窗同一排序口径
            foreach (KeyValuePair<string, DupCardRefs> kv in map)
            {
                kv.Value.Top.Sort(CompareCardsByMod);
                if (kv.Value.Top.Count > top)
                {
                    kv.Value.Top.RemoveRange(top, kv.Value.Top.Count - top);
                }
            }
            return map;
        }

        /// <summary>某 guid 的全部文件副本（跨库）。</summary>
        public List<RefRow> QueryModFiles(RootsConfig cfg, string guid)
        {
            List<RefRow> list = new List<RefRow>();
            foreach (Store s in AllStores(cfg))
            {
                list.AddRange(s.QueryModFiles(guid));
            }
            list.Sort(CompareByTier);
            return list;
        }
        /// <summary>目录聚合排序——条目数降序，同数按目录名。</summary>
        private static int CompareCompositionDirs(CompositionDir a, CompositionDir b)
        {
            int d = b.Count.CompareTo(a.Count);
            if (d != 0)
            {
                return d;
            }
            return string.CompareOrdinal(a.Path, b.Path);
        }
        /// <summary>文本类条目入档的字节上限（超过则不入档并出声——不静默丢内容）。</summary>
        private const long TextEntryMaxBytes = 4 * 1024 * 1024;
        /// <summary>解码条目文本——先按 UTF-8（严格，含 BOM 探测），失败回落 GBK（Koikatsu 的 csv 有本地编码形态）。</summary>
        private static string DecodeText(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "";
            }
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(data);
            }
            catch (DecoderFallbackException)
            {
                // 非 UTF-8 → 按 GBK（Koikatsu 的 csv 有本地编码形态）；编码不可用时出声并回落宽松 UTF-8，绝不抛出
                try
                {
                    Encoding gbk = Encoding.GetEncoding(936);
                    return gbk.GetString(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[组成] GBK 编码不可用（" + ex.GetType().Name + "：" + ex.Message + "）——本条按 UTF-8 宽松解码");
                    return Encoding.UTF8.GetString(data);
                }
            }
        }
        /// <summary>文本条目份数（按记录分隔符 \u0001 计——条目内容可能含换行，不能用行数当份数）。</summary>
        public static int TextEntryCount(string texts)
        {
            if (string.IsNullOrEmpty(texts))
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < texts.Length; i = i + 1)
            {
                if (texts[i] == '\u0001')
                {
                    n = n + 1;
                }
            }
            return n;
        }
        /// <summary>条目清单里是否含可读文本类条目（判档案是否缺文本内容——旧档案补捞一次用）。</summary>
        private static bool HasTextEntry(string entries)
        {
            if (string.IsNullOrEmpty(entries))
            {
                return false;
            }
            string[] lines = entries.Split('\n');
            foreach (string line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                int tab = line.IndexOf('\t');
                string path = tab > 0 ? line.Substring(0, tab) : line;
                if (Store.IsTextEntry(path))
                {
                    return true;
                }
            }
            return false;
        }
        /// <summary>解析文本条目表（\u0001 分隔记录、\t 分隔路径与内容）→ 路径 → 内容。</summary>
        public static Dictionary<string, string> ParseTexts(string texts)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(texts))
            {
                return map;
            }
            string[] recs = texts.Split('\u0001');
            foreach (string rec in recs)
            {
                if (rec.Length == 0)
                {
                    continue;
                }
                int tab = rec.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }
                map[rec.Substring(0, tab)] = rec.Substring(tab + 1);
            }
            return map;
        }
        /// <summary>读（必要时建立）某 mod 的组成档案——取该 guid 最优级别且磁盘上真实存在的副本；force=false 时按路径 / 大小 / 修改时间判失效（未变直接复用），force=true 强制重读容器；无副本时 error 具名出声。</summary>
        public ModComposition AnalyzeComposition(RootsConfig cfg, string guid, bool force, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(guid))
            {
                error = "缺少 guid";
                return null;
            }
            // [段1] 取最优级别且磁盘上存在的副本
            List<RefRow> files = QueryModFiles(cfg, guid);
            RefRow pick = null;
            foreach (RefRow f in files)
            {
                if (File.Exists(f.FilePath))
                {
                    pick = f;
                    break;
                }
            }
            if (pick == null)
            {
                error = files.Count == 0 ? "本地没有该 mod 的文件（库内无副本）" : "登记的副本都不在磁盘上（需重扫）";
                return null;
            }
            // [段2] 非强制 + 已有档案且文件未变 → 直接复用（不重读容器）；档案缺文本内容而容器有文本条目时视为陈旧，重建一次
            FileInfo fi = new FileInfo(pick.FilePath);
            string mtime = Store.StampOf(fi);
            ModComposition cached = Core.LoadComposition(guid);
            bool staleTexts = cached != null && string.IsNullOrEmpty(cached.Texts) && HasTextEntry(cached.Entries);
            if (!force && cached != null && !staleTexts
                && string.Equals(cached.FilePath, pick.FilePath, StringComparison.OrdinalIgnoreCase)
                && cached.Size == fi.Length && string.Equals(cached.Mtime, mtime, StringComparison.Ordinal))
            {
                cached.Cached = true;
                return cached;
            }
            // [段3] 读容器中央目录 → 建档落库
            string zipError;
            List<ZipEntryInfo> entries = ZipModReader.ListEntries(pick.FilePath, out zipError);
            if (entries == null)
            {
                error = "读容器失败：" + zipError;
                return null;
            }
            long totalSize = 0;
            long totalCompressed = 0;
            List<string> lines = new List<string>();
            List<string> texts = new List<string>();
            foreach (ZipEntryInfo e in entries)
            {
                totalSize = totalSize + e.Size;
                totalCompressed = totalCompressed + e.Compressed;
                lines.Add(e.Path + "\t" + e.Size.ToString() + "\t" + e.Compressed.ToString());
            }
            // [段4] 顺带捞文本类条目内容（csv / xml / txt…）——悬停预览用；读不到出声，不静默丢
            foreach (ZipEntryInfo e in entries)
            {
                if (!Store.IsTextEntry(e.Path))
                {
                    continue;
                }
                string textError;
                byte[] data = ZipModReader.ReadEntryBytes(pick.FilePath, e.Path, TextEntryMaxBytes, out textError);
                if (data == null)
                {
                    Console.WriteLine("[组成] 文本条目未入档：" + e.Path + " —— " + textError);
                    continue;
                }
                texts.Add("\u0001" + e.Path + "\t" + Store.ClampTextEntry(DecodeText(data)));
            }
            ModComposition rec = new ModComposition
            {
                Guid = guid,
                FilePath = pick.FilePath,
                FileName = pick.FileName,
                Size = fi.Length,
                Mtime = mtime,
                EntryCount = entries.Count,
                TotalSize = totalSize,
                TotalCompressed = totalCompressed,
                Entries = string.Join("\n", lines),
                Texts = string.Join("\n", texts),
                AnalyzedAt = Store.Now()
            };
            Core.SaveComposition(rec);
            Console.WriteLine("[组成] 建档：" + guid + " · " + entries.Count + " 条目 · 文本 " + texts.Count + " 份 · " + pick.FileName);
            return rec;
        }
        /// <summary>unity3d 条目读取上限（字节）——超限拒绝并出声，不无声截断。</summary>
        private const long U3dEntryMaxBytes = 536870912;

        /// <summary>读（必要时建立）某 mod 内一个 unity3d 条目的解析档案——取最优级别且磁盘上存在的副本；force=false 时按大小 / 修改时间判失效（未变直接复用），force=true 强制重读；失败 error 具名出声。</summary>
        public ModU3d AnalyzeU3d(RootsConfig cfg, string guid, string entryPath, bool force, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(entryPath))
            {
                error = "缺少 guid 或条目路径";
                return null;
            }
            if (!entryPath.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
            {
                error = "该条目不是 unity3d：" + entryPath;
                return null;
            }

            // [段1] 取最优级别且磁盘上存在的副本
            List<RefRow> files = QueryModFiles(cfg, guid);
            RefRow pick = null;
            foreach (RefRow f in files)
            {
                if (File.Exists(f.FilePath))
                {
                    pick = f;
                    break;
                }
            }
            if (pick == null)
            {
                error = files.Count == 0 ? "本地没有该 mod 的文件（库内无副本）" : "登记的副本都不在磁盘上（需重扫）";
                return null;
            }
            FileInfo fi = new FileInfo(pick.FilePath);
            string mtime = Store.StampOf(fi);

            // [段2] 非强制 + 已有档案且文件未变 → 直接复用
            ModU3d cached = Core.LoadU3d(guid, entryPath);
            if (!force && cached != null
                && string.Equals(cached.FilePath, pick.FilePath, StringComparison.OrdinalIgnoreCase)
                && cached.Size == fi.Length && string.Equals(cached.Mtime, mtime, StringComparison.Ordinal))
            {
                cached.Cached = true;
                return cached;
            }

            // [段3] 读容器内该条目 → 解析
            string readError;
            byte[] bytes = ZipModReader.ReadEntryBytes(pick.FilePath, entryPath, U3dEntryMaxBytes, out readError);
            if (bytes == null)
            {
                error = "读条目失败：" + readError;
                return null;
            }
            string parseError;
            List<Unity3dTexture> texs = Unity3dReader.ListTextures(bytes, out parseError);
            if (texs == null)
            {
                error = "解析失败：" + parseError;
                return null;
            }

            // [段4] 组装档案并落库
            List<string> lines = new List<string>();
            foreach (Unity3dTexture t in texs)
            {
                lines.Add(t.PathId.ToString() + "\t" + (t.Name ?? "") + "\t" + t.Width.ToString() + "\t" + t.Height.ToString()
                    + "\t" + t.Format.ToString() + "\t" + t.DataLength.ToString());
            }
            ModU3d rec = new ModU3d
            {
                Guid = guid,
                EntryPath = entryPath,
                FilePath = pick.FilePath,
                Size = fi.Length,
                Mtime = mtime,
                TextureCount = texs.Count,
                Textures = string.Join("\n", lines),
                ClassSummary = "",
                ParsedAt = Store.Now()
            };
            Core.SaveU3d(rec);
            Console.WriteLine("[unity3d] 建档：" + guid + " · " + entryPath + " · 贴图 " + texs.Count.ToString() + " 张");
            return rec;
        }

        /// <summary>读已有 unity3d 解析档案（不建档、不读容器）——打开组成区时优先展示；没有返回 null。</summary>
        public ModU3d LoadU3d(string guid, string entryPath)
        {
            return Core.LoadU3d(guid, entryPath);
        }

        /// <summary>读某 guid 的全部 unity3d 解析档案（条目路径 → 档案）——组成区「已解析」标蓝判定用。</summary>
        public Dictionary<string, ModU3d> LoadU3dMap(string guid)
        {
            return Core.LoadU3dMap(guid);
        }

        /// <summary>解析贴图清单文本（每行「pathID \t 名称 \t 宽 \t 高 \t 格式 \t 数据字节」）→ 结构列表。</summary>
        public static List<Unity3dTexture> ParseU3dTextures(string textures)
        {
            List<Unity3dTexture> list = new List<Unity3dTexture>();
            if (string.IsNullOrEmpty(textures))
            {
                return list;
            }
            string[] lines = textures.Split('\n');
            foreach (string line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('\t');
                if (parts.Length < 6)
                {
                    continue;
                }
                Unity3dTexture t = new Unity3dTexture();
                long pathId;
                long.TryParse(parts[0], out pathId);
                t.PathId = pathId;
                t.Name = parts[1];
                int w;
                int h;
                int fmt;
                int size;
                int.TryParse(parts[2], out w);
                int.TryParse(parts[3], out h);
                int.TryParse(parts[4], out fmt);
                int.TryParse(parts[5], out size);
                t.Width = w;
                t.Height = h;
                t.Format = fmt;
                t.DataLength = size;
                list.Add(t);
            }
            return list;
        }

        /// <summary>读已有组成档案（不建档、不读容器）——打开 mod 时优先展示；没有返回 null。</summary>
        public ModComposition LoadComposition(string guid)
        {
            return Core.LoadComposition(guid);
        }
        /// <summary>把组成档案的条目清单按目录聚合（条目清单每行「路径 \t 原始字节 \t 压缩字节」）——按条目数降序，同数按目录名；面板与 CLI 共用同一实现。</summary>
        public static List<CompositionDir> BuildDirs(string entries)
        {
            Dictionary<string, CompositionDir> map = new Dictionary<string, CompositionDir>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(entries))
            {
                string[] lines = entries.Split('\n');
                // [段1] 逐条目归入所在目录（无目录段的条目归「(容器根)」）
                foreach (string line in lines)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    string[] parts = line.Split('\t');
                    string full = parts[0];
                    long size = 0;
                    if (parts.Length > 1)
                    {
                        long.TryParse(parts[1], out size);
                    }
                    int slash = full.LastIndexOf('/');
                    string dir = slash > 0 ? full.Substring(0, slash) : "(容器根)";
                    CompositionDir item = null;
                    if (!map.TryGetValue(dir, out item))
                    {
                        item = new CompositionDir { Path = dir };
                        map[dir] = item;
                    }
                    item.Count = item.Count + 1;
                    item.Size = item.Size + size;
                }
            }
            List<CompositionDir> list = new List<CompositionDir>(map.Values);
            list.Sort(CompareCompositionDirs);
            return list;
        }

        /// <summary>副本按级别排序。</summary>
        private static int CompareByTier(RefRow a, RefRow b)
        {
            return a.Tier.CompareTo(b.Tier);
        }

        /// <summary>按库位取单张卡片（0 = 主库）。</summary>
        public CardRow GetCard(int lib, long id)
        {
            return StoreByLib(lib).GetCard(id);
        }

        /// <summary>按库位取缩略图（0 = 主库）。</summary>
        public byte[] LoadThumb(int lib, long id)
        {
            return StoreByLib(lib).LoadThumb(id);
        }

        /// <summary>按库位取某张卡的引用明细（0 = 主库）。</summary>
        public List<RefRow> QueryCardRefs(int lib, long id, int tier)
        {
            return StoreByLib(lib).QueryCardRefs(id, tier);
        }

        /// <summary>跨库重算某个 guid 的 mod 主表（副本被删 / 搬移后调用）。</summary>
        public void RecomputeMod(RootsConfig cfg, string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }
            ModFileRecord best = null;
            long count = 0;
            foreach (Store s in AllStores(cfg))
            {
                List<ModFileRecord> rows = s.QueryModFileRecords(guid);
                count = count + rows.Count;
                foreach (ModFileRecord r in rows)
                {
                    if (best == null || r.Tier < best.Tier)
                    {
                        best = r;
                    }
                }
            }
            if (best == null)
            {
                Core.DeleteModRow(guid);
                return;
            }
            Core.WriteModRow(guid, best.Tier, best.RootPath, best.FilePath, best.FileName, best.Size, best.Mtime, count);
        }

        /// <summary>把一条 mod 副本从源库搬到目标库根（缓存库→主库）或复制到目标库根（冷冻库→主库），并重算 mod 主表；newFileName 非空时改写文件名（旧版标记 / 正名用）。</summary>
        public string MoveModFile(RootsConfig cfg, string guid, string srcPath, string srcRootPath, string destPath, int toTier, string targetRootPath, string newFileName = null)
        {
            Store src = StoreByLib(LibOfRootPath(cfg, srcRootPath));
            Store dst = StoreByLib(LibOfRootPath(cfg, targetRootPath));
            ModFileRecord rec = src.RemoveModFileRow(srcPath);
            if (rec == null)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(newFileName))
            {
                rec.FileName = newFileName;
            }
            dst.InsertModFileRow(rec, toTier, targetRootPath, destPath);
            RecomputeMod(cfg, guid);
            return guid;
        }

        /// <summary>把一条 mod 副本在同一库根内改路径（按作者整理用——级别与库根不变），并重算 mod 主表；找不到源行返回 null。</summary>
        public string RenameModFilePath(RootsConfig cfg, string guid, string srcPath, string srcRootPath, string destPath)
        {
            Store store = StoreByLib(LibOfRootPath(cfg, srcRootPath));
            ModFileRecord rec = store.RemoveModFileRow(srcPath);
            if (rec == null)
            {
                return null;
            }
            store.InsertModFileRow(rec, rec.Tier, srcRootPath, destPath);
            RecomputeMod(cfg, guid);
            return destPath;
        }

        /// <summary>按路径找一条副本记录（跨库）。</summary>
        public ModFileRecord FindModFileRecord(RootsConfig cfg, string guid, string filePath)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(filePath))
            {
                return null;
            }
            foreach (Store s in AllStores(cfg))
            {
                foreach (ModFileRecord r in s.QueryModFileRecords(guid))
                {
                    if (string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return r;
                    }
                }
            }
            return null;
        }

        /// <summary>登记一条复制出来的 mod 副本（源行保留——冷冻库搬运），并重算 mod 主表；newFileName 非空时改写文件名（正名用）。</summary>
        public string AddModFileCopy(RootsConfig cfg, string guid, string srcRootPath, string srcPath, string destPath, int toTier, string targetRootPath, string newFileName = null)
        {
            ModFileRecord rec = FindModFileRecord(cfg, guid, srcPath);
            if (rec == null)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(newFileName))
            {
                rec.FileName = newFileName;
            }
            Store dst = StoreByLib(LibOfRootPath(cfg, targetRootPath));
            dst.InsertModFileRow(rec, toTier, targetRootPath, destPath);
            RecomputeMod(cfg, guid);
            return guid;
        }

        // ---------- 重复副本与旧版管理 ----------

        /// <summary>重复副本组——同 guid 有多份文件的组（跨库），按 guid 排序；版本 / 作者由消费方实时读 manifest 补。</summary>
        public List<DupGroup> ListDuplicateGroups(RootsConfig cfg)
        {
            Dictionary<string, DupGroup> map = new Dictionary<string, DupGroup>(StringComparer.Ordinal);
            foreach (Store s in AllStores(cfg))
            {
                foreach (ModFileRecord r in s.ListModFiles())
                {
                    if (string.IsNullOrEmpty(r.Guid))
                    {
                        continue;
                    }
                    DupGroup g = null;
                    if (!map.TryGetValue(r.Guid, out g))
                    {
                        g = new DupGroup { Guid = r.Guid };
                        map[r.Guid] = g;
                    }
                    g.Files.Add(r);
                    if (r.Tier == Tier.Main)
                    {
                        g.MainCount = g.MainCount + 1;
                    }
                }
            }
            List<DupGroup> list = new List<DupGroup>();
            foreach (KeyValuePair<string, DupGroup> kv in map)
            {
                if (kv.Value.Files.Count <= 1)
                {
                    continue;
                }
                kv.Value.Files.Sort(CompareFileByTier);
                list.Add(kv.Value);
            }
            list.Sort(CompareGroupByGuid);
            return list;
        }
        /// <summary>
        /// 补齐一组 mod 文件的 MD5 档案（重复副本面板打开时一次）——按 size + mtime 命中已有档案直接复用，
        /// 未命中或已失效的现算并落档；算过就忽略，重启后仍复用。返回「路径 → MD5」映射。
        /// </summary>
        public Dictionary<string, string> FillHashes(List<ModFileRecord> files, out int computed, out List<string> errors)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            errors = new List<string>();
            computed = 0;
            if (files == null || files.Count == 0)
            {
                return result;
            }

            // [段1] 读已有档案——命中判据 = 文件大小与修改时间都与建档时一致
            Dictionary<string, ModHashRecord> known = Core.LoadModHashes();

            // [段2] 逐份补齐——只算传入的重复副本文件，不扫全库
            foreach (ModFileRecord f in files)
            {
                if (string.IsNullOrEmpty(f.FilePath))
                {
                    continue;
                }
                FileInfo fi = new FileInfo(f.FilePath);
                long size = f.Size;
                string mtime = f.Mtime;
                if (fi.Exists)
                {
                    size = fi.Length;
                    mtime = Store.StampOf(fi);
                }

                ModHashRecord hit = null;
                if (known.TryGetValue(f.FilePath, out hit) && hit.Size == size && hit.Mtime == mtime && !string.IsNullOrEmpty(hit.Md5))
                {
                    result[f.FilePath] = hit.Md5;
                    continue;
                }

                string error = null;
                string md5 = FileHash.Md5(f.FilePath, out error);
                if (string.IsNullOrEmpty(md5))
                {
                    errors.Add(f.FileName + " → " + (error == null ? "算不出" : error));
                    continue;
                }
                result[f.FilePath] = md5;
                ModHashRecord rec = new ModHashRecord();
                rec.FilePath = f.FilePath;
                rec.Size = size;
                rec.Mtime = mtime;
                rec.Md5 = md5;
                rec.HashedAt = Store.Now();
                Core.PutModHash(rec);
                computed = computed + 1;
            }
            return result;
        }

        /// <summary>重复副本组数与「待确认」组数（跨库合并）——待确认 = 组内非旧版副本 ≥ 2（需要人工指定保留版本）。</summary>
        public void DupCounts(RootsConfig cfg, out long groups, out long pending)
        {
            Dictionary<string, long[]> map = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (Store s in AllStores(cfg))
            {
                foreach (ModFileCount c in s.ListModFileCounts())
                {
                    long[] v;
                    if (!map.TryGetValue(c.Guid, out v))
                    {
                        v = new long[2];
                        map[c.Guid] = v;
                    }
                    v[0] = v[0] + c.Total;
                    v[1] = v[1] + c.Live;
                }
            }
            long g = 0;
            long p = 0;
            foreach (KeyValuePair<string, long[]> kv in map)
            {
                if (kv.Value[0] <= 1)
                {
                    continue;
                }
                g = g + 1;
                if (kv.Value[1] >= 2)
                {
                    p = p + 1;
                }
            }
            groups = g;
            pending = p;
        }

        /// <summary>副本按级别排序（同级别按文件名）。</summary>
        private static int CompareFileByTier(ModFileRecord a, ModFileRecord b)
        {
            int c = a.Tier.CompareTo(b.Tier);
            if (c != 0)
            {
                return c;
            }
            return string.Compare(a.FileName ?? "", b.FileName ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>重复副本组按 guid 排序。</summary>
        private static int CompareGroupByGuid(DupGroup a, DupGroup b)
        {
            return string.CompareOrdinal(a.Guid, b.Guid);
        }

        /// <summary>某 guid 在主库的副本（nonOldOnly 为真时排除文件名带 .old 的）。</summary>
        public List<ModFileRecord> MainCopies(RootsConfig cfg, string guid, bool nonOldOnly)
        {
            List<ModFileRecord> list = new List<ModFileRecord>();
            if (string.IsNullOrEmpty(guid))
            {
                return list;
            }
            foreach (Store s in AllStores(cfg))
            {
                foreach (ModFileRecord r in s.QueryModFileRecords(guid))
                {
                    if (r.Tier != Tier.Main)
                    {
                        continue;
                    }
                    if (nonOldOnly && RootsRules.IsOldFileName(r.FileName))
                    {
                        continue;
                    }
                    list.Add(r);
                }
            }
            list.Sort(CompareFileByTier);
            return list;
        }

        /// <summary>某 guid 除指定文件外是否还有非旧版形态的副本（正名判据：无则说明新版已不在）。</summary>
        public bool HasLiveCopyBesides(RootsConfig cfg, string guid, string filePath)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }
            foreach (Store s in AllStores(cfg))
            {
                foreach (ModFileRecord r in s.QueryModFileRecords(guid))
                {
                    if (string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!RootsRules.IsOldFileName(r.FileName))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>取配置里某级别的第一条 mod 库根（无则 null）。</summary>
        private static RootEntry FindModRoot(RootsConfig cfg, int tier)
        {
            if (cfg == null)
            {
                return null;
            }
            foreach (RootEntry r in cfg.ModRootsOrdered())
            {
                if (r.tier == tier)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>把某个 guid 的一份主库副本判为旧版——改名加 .old 段并移到缓存库，落一条旧版登记；成功返回 null，否则返回原因。</summary>
        public string MarkOld(RootsConfig cfg, string guid, string filePath, out string detail)
        {
            // [段1] 前置校验：缓存库存在 · 副本在主库 · 主库留得下新版
            detail = null;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(filePath))
            {
                return "缺少 guid 或文件路径";
            }
            RootEntry cacheRoot = FindModRoot(cfg, Tier.Cache);
            if (cacheRoot == null)
            {
                return "未配置缓存库（级别 2）的 mod 库根";
            }
            ModFileRecord rec = FindModFileRecord(cfg, guid, filePath);
            if (rec == null)
            {
                return "数据库里没有这条副本行（需重扫核对）：" + filePath;
            }
            if (rec.Tier != Tier.Main)
            {
                return "只能把主库的副本判为旧版（该副本在" + Tier.Name(rec.Tier) + "）";
            }
            List<ModFileRecord> mains = MainCopies(cfg, guid, true);
            ModFileRecord keep = null;
            foreach (ModFileRecord m in mains)
            {
                if (!string.Equals(m.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    keep = m;
                    break;
                }
            }
            if (keep == null)
            {
                return "主库只剩这一份——判为旧版会让主库没有该 mod；请用「换用此版本」把另一份换上来";
            }
            if (!File.Exists(filePath))
            {
                return "文件不存在：" + filePath;
            }

            // [段2] 目标路径与判据留档（版本号实时读 manifest——人工筛的依据）
            string newName = RootsRules.MakeOldFileName(Path.GetFileName(filePath));
            string dest = Path.Combine(cacheRoot.path, newName);
            if (File.Exists(dest))
            {
                return "缓存库已有同名文件：" + dest;
            }
            ModInfo oldInfo = ZipModReader.Parse(filePath);
            ModInfo keepInfo = ZipModReader.Parse(keep.FilePath);

            // [段3] 文件移动 → 数据库行更新 → 旧版登记
            try
            {
                Directory.CreateDirectory(cacheRoot.path);
                File.Move(filePath, dest);
            }
            catch (Exception ex)
            {
                return "文件操作失败" + FileBusyHint(ex);
            }
            try
            {
                if (MoveModFile(cfg, guid, filePath, rec.RootPath, dest, Tier.Cache, cacheRoot.path, newName) == null)
                {
                    return "已移动但数据库未找到源副本行：" + filePath + "（需重扫核对）";
                }
            }
            catch (Exception ex)
            {
                return "文件已移动但数据库更新失败：" + ex.GetType().Name + " " + ex.Message;
            }
            Core.SetModOld(new ModOldRecord
            {
                OldPath = dest,
                Guid = guid,
                OldName = newName,
                OldVersion = oldInfo.Version,
                NewName = keep.FileName,
                NewVersion = keepInfo.Version,
                MarkedAt = Store.Now()
            });
            detail = dest;
            return null;
        }
        /// <summary>指定保留版本——该副本成为主库当前版本（不在主库则搬入并正名），同 guid 其余非旧版副本判为旧版（加 .old 段并移入缓存库）；成功返回 null，否则返回原因。</summary>
        public string KeepVersion(RootsConfig cfg, string guid, string filePath, out string detail)
        {
            // [段1] 前置校验：主库与缓存库都在 · 副本行存在 · 文件存在
            detail = null;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(filePath))
            {
                return "缺少 guid 或文件路径";
            }
            RootEntry mainRoot = FindModRoot(cfg, Tier.Main);
            if (mainRoot == null)
            {
                return "未配置主库（级别 1）的 mod 库根";
            }
            RootEntry cacheRoot = FindModRoot(cfg, Tier.Cache);
            if (cacheRoot == null)
            {
                return "未配置缓存库（级别 2）的 mod 库根";
            }
            ModFileRecord keep = FindModFileRecord(cfg, guid, filePath);
            if (keep == null)
            {
                return "数据库里没有这条副本行（需重扫核对）：" + filePath;
            }
            if (!File.Exists(filePath))
            {
                return "文件不存在：" + filePath;
            }
            List<ModFileRecord> all = new List<ModFileRecord>();
            foreach (Store s in AllStores(cfg))
            {
                all.AddRange(s.QueryModFileRecords(guid));
            }
            all.Sort(CompareFileByTier);
            ModInfo keepInfo = ZipModReader.Parse(filePath);
            string keepName = RootsRules.StripOldFileName(Path.GetFileName(filePath));

            // [段2] 其余副本判旧版：主库 → 加 .old 段移入缓存库 · 缓存库 → 就地加 .old 段 · 冷冻库与已带 .old 的跳过
            int demoted = 0;
            List<string> skipped = new List<string>();
            foreach (ModFileRecord r in all)
            {
                if (string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (RootsRules.IsOldFileName(r.FileName))
                {
                    continue;
                }
                if (r.Tier == Tier.Cold)
                {
                    skipped.Add("冷冻库副本保持原样（只读库不改动）：" + r.FilePath);
                    continue;
                }
                if (!File.Exists(r.FilePath))
                {
                    skipped.Add("文件不存在：" + r.FilePath);
                    continue;
                }
                string oldName = RootsRules.MakeOldFileName(Path.GetFileName(r.FilePath));
                string oldDir = r.Tier == Tier.Main ? cacheRoot.path : Path.GetDirectoryName(r.FilePath);
                string dest = Path.Combine(oldDir, oldName);
                if (File.Exists(dest))
                {
                    skipped.Add("目标已有同名文件：" + dest);
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(oldDir);
                    File.Move(r.FilePath, dest);
                }
                catch (Exception ex)
                {
                    skipped.Add("文件操作失败" + FileBusyHint(ex) + "：" + r.FilePath);
                    continue;
                }
                if (MoveModFile(cfg, guid, r.FilePath, r.RootPath, dest, Tier.Cache, oldDir, oldName) == null)
                {
                    skipped.Add("文件已移动但数据库未找到源副本行：" + r.FilePath);
                    continue;
                }
                Core.SetModOld(new ModOldRecord
                {
                    OldPath = dest,
                    Guid = guid,
                    OldName = oldName,
                    OldVersion = ZipModReader.Parse(dest).Version,
                    NewName = keepName,
                    NewVersion = keepInfo.Version,
                    MarkedAt = Store.Now()
                });
                demoted = demoted + 1;
            }

            // [段3] 保留份入主库并正名（已在主库且非旧版则不动）
            bool keepMoved = false;
            if (keep.Tier == Tier.Main)
            {
                if (RootsRules.IsOldFileName(keep.FileName))
                {
                    string renamed = Path.Combine(Path.GetDirectoryName(keep.FilePath), keepName);
                    if (File.Exists(renamed))
                    {
                        return "主库已有同名文件（正名会撞名）：" + renamed;
                    }
                    try
                    {
                        File.Move(keep.FilePath, renamed);
                    }
                    catch (Exception ex)
                    {
                        return "保留份正名失败" + FileBusyHint(ex) + " · " + keep.FilePath;
                    }
                    if (MoveModFile(cfg, guid, keep.FilePath, keep.RootPath, renamed, Tier.Main, keep.RootPath, keepName) == null)
                    {
                        return "保留份已正名但数据库未找到源副本行：" + keep.FilePath;
                    }
                    Core.DeleteModOld(keep.FilePath);
                    keepMoved = true;
                }
            }
            else
            {
                string dest = Path.Combine(mainRoot.path, keepName);
                if (File.Exists(dest))
                {
                    return "主库已有同名文件：" + dest;
                }
                bool readOnlySource = RootsRules.IsReadOnlyRoot(cfg, keep.RootPath);
                try
                {
                    Directory.CreateDirectory(mainRoot.path);
                    if (readOnlySource)
                    {
                        File.Copy(keep.FilePath, dest, false);
                    }
                    else
                    {
                        File.Move(keep.FilePath, dest);
                    }
                }
                catch (Exception ex)
                {
                    return "保留份搬入主库失败" + FileBusyHint(ex);
                }
                string moved;
                if (readOnlySource)
                {
                    moved = AddModFileCopy(cfg, guid, keep.RootPath, keep.FilePath, dest, Tier.Main, mainRoot.path, keepName);
                }
                else
                {
                    moved = MoveModFile(cfg, guid, keep.FilePath, keep.RootPath, dest, Tier.Main, mainRoot.path, keepName);
                }
                if (moved == null)
                {
                    return "保留份已搬移但数据库未找到源副本行：" + keep.FilePath;
                }
                Core.DeleteModOld(keep.FilePath);
                keepMoved = true;
            }

            // [段4] 汇总——跳过项出声（失败可见，不静默）
            detail = "保留 " + keepName + "（版本 " + (keepInfo.Version ?? "<无>") + "）· 判旧版 " + demoted + " 份";
            if (keepMoved)
            {
                detail = detail + " · 保留份已入主库";
            }
            if (skipped.Count > 0)
            {
                detail = detail + " · 跳过 " + skipped.Count + " 份：" + string.Join("；", skipped);
            }
            return null;
        }

        /// <summary>文件操作失败的统一说明——补上「谁可能占用」与「怎么办」（失败可执行化：不静默、不让人猜）。</summary>
        internal static string FileBusyHint(Exception ex)
        {
            return "（文件被其他程序占用——游戏运行中 / 资源管理器正在预览 / 杀毒软件扫描都会占用；关闭游戏或稍后重试）：" + ex.GetType().Name + " " + ex.Message;
        }

        /// <summary>把一份旧版与主库当前版本完全互换（位置 + 名字）——选错版本时的纠正路径；成功返回 null，否则返回原因。</summary>
        public string SwapVersion(RootsConfig cfg, string guid, string oldFilePath, out string detail)
        {
            // [段1] 前置校验：互换对象在主库之外 · 带 .old 形态 · 主库有可换下的版本
            detail = null;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(oldFilePath))
            {
                return "缺少 guid 或文件路径";
            }
            ModFileRecord oldRec = FindModFileRecord(cfg, guid, oldFilePath);
            if (oldRec == null)
            {
                return "数据库里没有这条副本行（需重扫核对）：" + oldFilePath;
            }
            if (oldRec.Tier == Tier.Main)
            {
                return "要互换的那一份当前在主库——互换的另一方必须在主库之外";
            }
            if (!RootsRules.IsOldFileName(oldRec.FileName))
            {
                return "该副本文件名不带 .old——它不是登记过的旧版";
            }
            List<ModFileRecord> mains = MainCopies(cfg, guid, true);
            if (mains.Count == 0)
            {
                return "主库没有该 mod 的版本——用「正名搬入主库」";
            }
            ModFileRecord main = mains[0];
            if (!File.Exists(main.FilePath))
            {
                return "主库文件不存在：" + main.FilePath;
            }
            if (!File.Exists(oldFilePath))
            {
                return "旧版文件不存在：" + oldFilePath;
            }
            string mainDir = Path.GetDirectoryName(main.FilePath);
            string oldDir = Path.GetDirectoryName(oldFilePath);
            string mainOldName = RootsRules.MakeOldFileName(Path.GetFileName(main.FilePath));
            string mainOldPath = Path.Combine(mainDir, mainOldName);
            string promotedName = RootsRules.StripOldFileName(Path.GetFileName(oldFilePath));
            string promotedPath = Path.Combine(mainDir, promotedName);
            if (File.Exists(mainOldPath))
            {
                return "主库已存在同名旧版文件：" + mainOldPath;
            }
            if (File.Exists(promotedPath))
            {
                return "主库已存在同名文件（正名后会撞名）：" + promotedPath;
            }

            // [段2] 版本留档（互换前两份 manifest——登记记录用）
            ModInfo mainInfo = ZipModReader.Parse(main.FilePath);
            ModInfo oldInfo = ZipModReader.Parse(oldFilePath);

            // [段3] 三步文件互换：主库改名腾位 → 旧版正名入主库 → 主库那份移入缓存库
            try
            {
                File.Move(main.FilePath, mainOldPath);
            }
            catch (Exception ex)
            {
                return "主库改名失败" + FileBusyHint(ex) + " · " + main.FilePath;
            }
            bool readOnlySource = RootsRules.IsReadOnlyRoot(cfg, oldRec.RootPath);
            try
            {
                if (readOnlySource)
                {
                    File.Copy(oldFilePath, promotedPath, false);
                }
                else
                {
                    File.Move(oldFilePath, promotedPath);
                }
            }
            catch (Exception ex)
            {
                string back = null;
                try
                {
                    File.Move(mainOldPath, main.FilePath);
                }
                catch (Exception ex2)
                {
                    back = "；回滚主库改名也失败" + FileBusyHint(ex2);
                }
                string tail = "";
                if (back != null)
                {
                    tail = back;
                }
                return "旧版搬入主库失败" + FileBusyHint(ex) + tail;
            }
            string finalOldPath = Path.Combine(oldDir, mainOldName);
            try
            {
                Directory.CreateDirectory(oldDir);
                File.Move(mainOldPath, finalOldPath);
            }
            catch (Exception ex)
            {
                return "主库那份移入缓存库失败" + FileBusyHint(ex) + "（主库现有两份，可重扫后手动整理）";
            }

            // [段4] 数据库：两行位置互换 + 旧版登记改指新的旧版
            Store oldStore = StoreByLib(LibOfRootPath(cfg, oldRec.RootPath));
            Store mainStore = StoreByLib(LibOfRootPath(cfg, main.RootPath));
            try
            {
                ModFileRecord recA = mainStore.RemoveModFileRow(main.FilePath);
                ModFileRecord recB = oldStore.RemoveModFileRow(oldFilePath);
                if (recA == null || recB == null)
                {
                    string miss = main.FilePath;
                    if (recA != null)
                    {
                        miss = oldFilePath;
                    }
                    return "文件已互换但数据库缺副本行（需重扫核对）：" + miss;
                }
                recA.FileName = mainOldName;
                oldStore.InsertModFileRow(recA, oldRec.Tier, oldRec.RootPath, finalOldPath);
                recB.FileName = promotedName;
                mainStore.InsertModFileRow(recB, Tier.Main, main.RootPath, promotedPath);
            }
            catch (Exception ex)
            {
                return "文件已互换但数据库更新失败：" + ex.GetType().Name + " " + ex.Message;
            }
            RecomputeMod(cfg, guid);
            Core.DeleteModOld(oldFilePath);
            Core.SetModOld(new ModOldRecord
            {
                OldPath = finalOldPath,
                Guid = guid,
                OldName = mainOldName,
                OldVersion = mainInfo.Version,
                NewName = promotedName,
                NewVersion = oldInfo.Version,
                MarkedAt = Store.Now()
            });
            detail = promotedName + " ⇄ " + mainOldName;
            return null;
        }

        /// <summary>把一份旧版副本搬入主库——主库无该 guid 版本时正名（去掉 .old 段），该 guid 仍有其他非旧版副本时保留原名；成功返回 null。</summary>
        public string PromoteOld(RootsConfig cfg, string guid, string filePath, out string detail)
        {
            // [段1] 前置校验：主库必须没有该 guid 的副本（主库有新版本时旧版不该动）
            detail = null;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(filePath))
            {
                return "缺少 guid 或文件路径";
            }
            RootEntry mainRoot = FindModRoot(cfg, Tier.Main);
            if (mainRoot == null)
            {
                return "未配置主库（级别 1）的 mod 库根";
            }
            ModFileRecord rec = FindModFileRecord(cfg, guid, filePath);
            if (rec == null)
            {
                return "数据库里没有这条副本行（需重扫核对）：" + filePath;
            }
            if (rec.Tier == Tier.Main)
            {
                return "该副本已在主库";
            }
            if (!File.Exists(filePath))
            {
                return "文件不存在：" + filePath;
            }
            List<ModFileRecord> mains = MainCopies(cfg, guid, false);
            if (mains.Count > 0)
            {
                return "主库已有该 mod 的副本——直接搬入会变成重复副本；要换版本请用「换用此版本」";
            }

            // [段2] 正名判据：该 guid 除这一份外没有别的非旧版副本（新版已不在）
            bool onlyOld = !HasLiveCopyBesides(cfg, guid, filePath);
            string fileName = Path.GetFileName(filePath);
            if (onlyOld)
            {
                fileName = RootsRules.StripOldFileName(fileName);
            }
            string dest = Path.Combine(mainRoot.path, fileName);
            if (File.Exists(dest))
            {
                return "主库已有同名文件：" + dest;
            }

            // [段3] 文件搬运（源只读则复制）→ 数据库行 → 旧版登记销账
            bool readOnlySource = RootsRules.IsReadOnlyRoot(cfg, rec.RootPath);
            try
            {
                Directory.CreateDirectory(mainRoot.path);
                if (readOnlySource)
                {
                    File.Copy(filePath, dest, false);
                }
                else
                {
                    File.Move(filePath, dest);
                }
            }
            catch (Exception ex)
            {
                return "文件操作失败" + FileBusyHint(ex);
            }
            try
            {
                string moved;
                if (readOnlySource)
                {
                    moved = AddModFileCopy(cfg, guid, rec.RootPath, filePath, dest, Tier.Main, mainRoot.path, fileName);
                }
                else
                {
                    moved = MoveModFile(cfg, guid, filePath, rec.RootPath, dest, Tier.Main, mainRoot.path, fileName);
                }
                if (moved == null)
                {
                    return "文件已处理但数据库未找到源副本行：" + filePath + "（需重扫核对）";
                }
            }
            catch (Exception ex)
            {
                return "文件已处理但数据库更新失败：" + ex.GetType().Name + " " + ex.Message;
            }
            if (onlyOld)
            {
                Core.DeleteModOld(filePath);
                detail = dest + "（已正名）";
            }
            else
            {
                detail = dest + "（保留 .old——该 guid 还有其他非旧版副本）";
            }
            return null;
        }

        /// <summary>路径是否落在某个受管 mod 库根内（打开文件前的白名单校验——按库根配置判定，不读库）。</summary>
        public static bool IsUnderModRoot(RootsConfig cfg, string path)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            if (!path.EndsWith(".zipmod", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
            foreach (RootEntry e in cfg.modRoots)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.path))
                {
                    continue;
                }
                string root = e.path.Trim().TrimEnd('\\', '/');
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>路径是否落在某个受管卡片库根内（打开卡片文件前的白名单校验——按库根配置判定，不读库）。</summary>
        public static bool IsUnderCardRoot(RootsConfig cfg, string path)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
            foreach (RootEntry e in cfg.cardRoots)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.path))
                {
                    continue;
                }
                string root = e.path.Trim().TrimEnd('\\', '/');
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
