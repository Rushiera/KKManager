using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>扫描范围——All 全部库根 · Preset 预置条目（主库 / 缓存库槽位）· Extra 使用者添加的库根。</summary>
    public enum ScanScope
    {
        /// <summary>全部库根。</summary>
        All,

        /// <summary>预置条目——mod 主库 + 缓存库槽位 + 卡片 3 条锁定主库。</summary>
        Preset,

        /// <summary>使用者添加的库根——mod 冷冻库 + 卡片附加库。</summary>
        Extra
    }

    /// <summary>扫描统计。</summary>
    public class ScanResult
    {
        /// <summary>枚举到的文件数。</summary>
        public int Seen { get; set; }

        /// <summary>新增 / 更新数。</summary>
        public int Added { get; set; }

        /// <summary>因 size+mtime 未变而跳过数。</summary>
        public int Skipped { get; set; }

        /// <summary>解析失败数。</summary>
        public int Failed { get; set; }

        /// <summary>无卡类型标记、不算卡片的文件数。</summary>
        public int NonCard { get; set; }

        /// <summary>本次入库的引用条目数。</summary>
        public long RefEntries { get; set; }

        /// <summary>缩略图字节总量。</summary>
        public long ThumbBytes { get; set; }

        /// <summary>本次扫描新读到角色名的卡片数。</summary>
        public int NamesRead { get; set; }

        /// <summary>本次扫描为存量卡片补读到角色名的数。</summary>
        public int NamesFilled { get; set; }

        /// <summary>本次扫描为存量卡片补正卡类型的数（本版之前场景卡的 card_type 记成了数据区头段的乱码）。</summary>
        public int TypesFixed { get; set; }

        /// <summary>本次扫描因磁盘上已不存在而清理的记录数——卡片行 / mod 副本涉及的 guid 数 / 旧版登记条数之和。</summary>
        public int Removed { get; set; }

        /// <summary>耗时。</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>错误明细（上限 200 条）。</summary>
        public List<string> Errors { get; } = new List<string>();
        /// <summary>并入另一轮扫描的计数（「扫描主要 / 追加库扫描」是两轮扫描，面板合并展示）——耗时相加，错误明细续接。</summary>
        public void Merge(ScanResult other)
        {
            if (other == null)
            {
                return;
            }
            Seen += other.Seen;
            Added += other.Added;
            Skipped += other.Skipped;
            Failed += other.Failed;
            NonCard += other.NonCard;
            RefEntries += other.RefEntries;
            ThumbBytes += other.ThumbBytes;
            NamesRead += other.NamesRead;
            NamesFilled += other.NamesFilled;
            TypesFixed += other.TypesFixed;
            Removed += other.Removed;
            Elapsed += other.Elapsed;
            foreach (string e in other.Errors)
            {
                if (Errors.Count < 200)
                {
                    Errors.Add(e);
                }
            }
        }

        /// <summary>本次扫描判定为「该离线」的库根路径——目录不存在或枚举到 0 个东西（预置条目不在此列）；由调用方置位配置并生成待办。</summary>
        public List<string> OfflineRoots { get; } = new List<string>();
    }

    /// <summary>库扫描——按级别 1→2→3 顺序扫描；增量判据：文件 size + mtime 未变则跳过。</summary>
    public static class Scanner
    {
        private const int MaxErrors = 200;
        private const int BatchSize = 500;

        /// <summary>库里记的卡类型是否需要补正——空值，或既不是场景卡标记也不是游戏内标记串（「【…】」形态，本版之前场景卡记的是数据区头段乱码）。</summary>
        private static bool NeedsTypeFix(string storedType)
        {
            if (string.IsNullOrEmpty(storedType))
            {
                return true;
            }
            if (storedType == CardReader.SceneCardType)
            {
                return false;
            }
            return storedType.IndexOf('【') < 0;
        }

        /// <summary>扫描 mod 库（按级别升序）——按库根路由到对应库文件；库根不存在时出声跳过，不代建目录。</summary>
        public static ScanResult ScanMods(StoreHub hub, RootsConfig cfg, bool force, Action<string> log)
        {
            return ScanMods(hub, cfg, null, ScanScope.All, force, log);
        }

        /// <summary>扫描 mod 库——only 非空时只扫该条库根（面板「更新本库」）· scope 限定预置条目 / 使用者添加的库根（面板「扫描主要 / 追加库扫描」）；离线库一律跳过；目录不存在或枚举为空 → 记入「该离线」清单。</summary>
        public static ScanResult ScanMods(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, Action<string> log)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            List<RootEntry> roots = cfg.ModRootsOrdered();

            foreach (RootEntry root in roots)
            {
                if (only != null && !SamePath(root.path, only.path))
                {
                    continue;
                }
                if (!InScope(root, scope, true))
                {
                    continue;
                }
                if (log != null)
                {
                    log("级别 " + root.tier + "（" + Tier.Name(root.tier) + "） · " + root.path);
                }

                if (root.offline)
                {
                    if (log != null)
                    {
                        log("  离线库，已跳过（内容取本库数据库）");
                    }
                    continue;
                }

                if (!Directory.Exists(root.path))
                {
                    AddError(result, "目录不存在，已跳过：" + root.path);
                    MarkOffline(result, root, true, log);
                    continue;
                }

                Store store = hub.StoreFor(root, true);
                Dictionary<string, string[]> stamps = force
                    ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    : store.LoadModStamps();

                bool enumFailed = false;
                List<string> files = Enumerate(root.path, "*.zipmod", root.recurse, log, out enumFailed);
                result.Seen += files.Count;
                if (files.Count == 0 && !enumFailed)
                {
                    MarkOffline(result, root, true, log);
                }
                int i = 0;
                int batchCount = 0;
                store.Begin();
                try
                {
                    foreach (string f in files)
                    {
                        i++;
                        FileInfo fi = new FileInfo(f);
                        string mtime = Store.StampOf(fi);
                        string[] old = null;
                        if (stamps.TryGetValue(f, out old) && old[0] == fi.Length.ToString() && old[1] == mtime)
                        {
                            result.Skipped++;
                            continue;
                        }

                        ModInfo m = ZipModReader.Parse(f);
                        if (!string.IsNullOrEmpty(m.Error) || string.IsNullOrEmpty(m.Guid))
                        {
                            result.Failed++;
                            AddError(result, m.FileName + " → " + (m.Error ?? "无 guid"));
                            continue;
                        }

                        store.UpsertModFile(m, root, mtime);
                        store.FillModMeta(m);
                        result.Added++;

                        batchCount++;
                        if (batchCount >= BatchSize)
                        {
                            store.Commit();
                            store.Begin();
                            batchCount = 0;
                        }

                        if (log != null && i % 2000 == 0)
                        {
                            log("  " + i + "/" + files.Count);
                        }
                    }
                    store.Commit();
                }
                catch
                {
                    store.Rollback();
                    throw;
                }

                // 清扫：本次枚举集合中已不存在的行——离线库 / 枚举失败 / 枚举为空一律不清扫（枚举失败不等于文件消失）
                if (!enumFailed && files.Count > 0)
                {
                    HashSet<string> present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                    List<string> goneGuids = store.DeleteModFilesMissingUnderRoot(root.path, present);
                    foreach (string guid in goneGuids)
                    {
                        hub.RecomputeMod(cfg, guid);
                    }
                    int goneOld = hub.Core.DeleteModOldMissingUnderRoot(root.path, present);
                    if (goneGuids.Count > 0 || goneOld > 0)
                    {
                        result.Removed += goneGuids.Count + goneOld;
                        if (log != null)
                        {
                            log("  已清理 " + goneGuids.Count + " 个 guid 的已消失副本记录 · 旧版登记 " + goneOld + " 条（磁盘上已不存在）");
                        }
                    }
                }
            }

            ApplyOfflineMarks(hub, cfg, result, true, log);

            // 作者聚合表整表重建（mod 主表是唯一真相源，聚合表只是读侧缓存）——扫描结束即刷新，面板「按作者筛选」直接读它
            hub.Core.RefreshModAuthors();
            if (log != null)
            {
                log("作者索引已重建：" + hub.Core.QueryAuthors().Count + " 位作者");
            }

            result.Elapsed = watch.Elapsed;
            return result;
        }

        /// <summary>扫描卡片库（按级别升序），提取 mod 声明与缩略图——按库根路由到对应库文件。</summary>
        public static ScanResult ScanCards(StoreHub hub, RootsConfig cfg, bool force, int thumbWidth, int thumbQuality, Action<string> log)
        {
            return ScanCards(hub, cfg, null, ScanScope.All, force, thumbWidth, thumbQuality, log);
        }

        /// <summary>扫描卡片库——only 非空时只扫该条库根（面板「更新本库」）· scope 限定预置条目 / 使用者添加的库根（面板「扫描主要 / 追加库扫描」）；离线库一律跳过；目录不存在或枚举为空 → 记入「该离线」清单。</summary>
        public static ScanResult ScanCards(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, int thumbWidth, int thumbQuality, Action<string> log)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            List<RootEntry> roots = cfg.CardRootsOrdered();

            foreach (RootEntry root in roots)
            {
                if (only != null && !SamePath(root.path, only.path))
                {
                    continue;
                }
                if (!InScope(root, scope, false))
                {
                    continue;
                }
                if (log != null)
                {
                    log("级别 " + root.tier + " · " + root.path + (root.recurse ? "（含子目录）" : "（仅本目录）"));
                }

                if (root.offline)
                {
                    if (log != null)
                    {
                        log("  离线库，已跳过（内容取本库数据库）");
                    }
                    continue;
                }

                if (!Directory.Exists(root.path))
                {
                    AddError(result, "目录不存在，已跳过：" + root.path);
                    MarkOffline(result, root, false, log);
                    continue;
                }

                Store store = hub.StoreFor(root, false);
                Dictionary<string, string[]> stamps = force
                    ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    : store.LoadCardStamps();

                string rootNorm = root.path.TrimEnd('\\', '/');
                bool enumFailed = false;
                List<string> files = Enumerate(root.path, "*.png", root.recurse, log, out enumFailed);
                result.Seen += files.Count;
                if (files.Count == 0 && !enumFailed)
                {
                    MarkOffline(result, root, false, log);
                }
                int i = 0;
                int batchCount = 0;
                store.Begin();
                try
                {
                    foreach (string f in files)
                    {
                        i++;
                        FileInfo fi = new FileInfo(f);
                        string mtime = Store.StampOf(fi);
                        string[] old = null;
                        if (stamps.TryGetValue(f, out old) && old[0] == fi.Length.ToString() && old[1] == mtime)
                        {
                            result.Skipped++;
                            if (old.Length > 3 && NeedsTypeFix(old[3]))
                            {
                                // 存量类型补正：本版之前场景卡的 card_type 记成了数据区头段的乱码——只读文件头段重判一次
                                string fixedType;
                                string fixedVersion;
                                if (CardReader.ReadHeadOnly(f, out fixedType, out fixedVersion) && fixedType != old[3])
                                {
                                    store.UpdateCardType(f, fixedType, fixedVersion);
                                    result.TypesFixed++;
                                }
                            }
                            if (old.Length > 2 && old[2] == "1")
                            {
                                // 存量补名：人物卡的角色名列还空着（本版新列）——只读 Parameter 块补上，不重扫声明区
                                store.UpdateCardName(f, CardName.ReadCharacter(f));
                                result.NamesFilled++;
                            }
                            continue;
                        }

                        CardInfo c;
                        try
                        {
                            c = CardReader.Parse(f);
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            AddError(result, Path.GetFileName(f) + " → " + ex.GetType().Name + ": " + ex.Message);
                            continue;
                        }

                        if (string.IsNullOrEmpty(c.CardType))
                        {
                            result.NonCard++;
                            continue;
                        }
                        if (c.CharaName == null && c.CardType.Contains("Chara"))
                        {
                            // 人物卡：角色名在数据区 Parameter 块（服装卡的名字已由 CardReader 从头段顺带读出）
                            c.CharaName = CardName.ReadCharacter(f);
                            result.NamesRead++;
                        }

                        byte[] thumb = null;
                        if (thumbWidth > 0)
                        {
                            thumb = Thumbnail.FromCard(f, c.ImageEnd, thumbWidth, thumbQuality);
                            if (thumb != null)
                            {
                                result.ThumbBytes += thumb.Length;
                            }
                            else if (Thumbnail.LastError != null)
                            {
                                AddError(result, Path.GetFileName(f) + " 缩略图 → " + Thumbnail.LastError);
                            }
                        }

                        string folder = FolderOf(f, rootNorm);
                        long id = store.UpsertCard(c, root, folder, thumb, mtime);
                        store.ReplaceCardRefs(id, c.ModRefs);
                        result.Added++;
                        result.RefEntries += c.ModRefs.Count;

                        batchCount++;
                        if (batchCount >= BatchSize)
                        {
                            store.Commit();
                            store.Begin();
                            batchCount = 0;
                            if (log != null)
                            {
                                log("  已入库 " + result.Added + " / 枚举 " + i + " / 共 " + files.Count);
                            }
                        }

                        if (log != null && i % 200 == 0)
                        {
                            log("  " + i + "/" + files.Count + "  " + c.FileName);
                        }
                    }
                    store.Commit();
                }
                catch
                {
                    store.Rollback();
                    throw;
                }

                // 清扫：本次枚举集合中已不存在的行——离线库 / 枚举失败 / 枚举为空一律不清扫（枚举失败不等于文件消失）
                if (!enumFailed && files.Count > 0)
                {
                    HashSet<string> present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                    int goneCards = store.DeleteCardsMissingUnderRoot(root.path, present);
                    if (goneCards > 0)
                    {
                        result.Removed += goneCards;
                        if (log != null)
                        {
                            log("  已清理 " + goneCards + " 条已消失的卡片记录（磁盘上已不存在）");
                        }
                    }
                }
            }

            ApplyOfflineMarks(hub, cfg, result, false, log);
            result.Elapsed = watch.Elapsed;
            return result;
        }

        /// <summary>把扫描结果里的「该离线」库根落进配置并登记待办——同库只留一条待办（重复自动离线不叠加）；预置条目已在扫描侧排除。</summary>
        private static void ApplyOfflineMarks(StoreHub hub, RootsConfig cfg, ScanResult result, bool isMods, Action<string> log)
        {
            if (result == null || result.OfflineRoots.Count == 0)
            {
                return;
            }
            List<RootEntry> list = isMods ? cfg.modRoots : cfg.cardRoots;
            if (list == null)
            {
                return;
            }
            bool changed = false;
            foreach (string path in result.OfflineRoots)
            {
                foreach (RootEntry e in list)
                {
                    if (!string.Equals((e.path ?? "").Trim(), (path ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!e.offline)
                    {
                        e.offline = true;
                        changed = true;
                        hub.Core.AddTodo("offline", e.path.Trim().ToLowerInvariant(), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        if (log != null)
                        {
                            log("  已自动离线并登记待办：" + e.path);
                        }
                    }
                    break;
                }
            }
            if (changed)
            {
                hub.Core.SaveRoots(cfg);
            }
        }

        /// <summary>计算文件相对库根的文件夹（根目录返回空串）。</summary>
        private static string FolderOf(string filePath, string rootNorm)
        {
            string dir = Path.GetDirectoryName(filePath) ?? "";
            if (dir.Length == 0)
            {
                return "";
            }
            if (dir.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase))
            {
                return dir.Substring(rootNorm.Length).TrimStart('\\', '/');
            }
            return dir;
        }

        private static void AddError(ScanResult result, string message)
        {
            if (result.Errors.Count < MaxErrors)
            {
                result.Errors.Add(message);
            }
        }

        /// <summary>标记「该离线」——预置条目（主库 / mod 缓存库槽位）不适用离线语义，只出声不出档。</summary>
        private static void MarkOffline(ScanResult result, RootEntry root, bool isMods, Action<string> log)
        {
            if (!RootsRules.CanOffline(root, isMods))
            {
                if (log != null)
                {
                    log("  目录不存在或为空——预置条目不适用离线，仅跳过");
                }
                return;
            }
            result.OfflineRoots.Add(root.path);
            if (log != null)
            {
                log("  目录不存在或为空——已判为离线库（待办 +1）");
            }
        }

        /// <summary>两条库根是否同一条（路径归一后比较）。</summary>
        private static bool SamePath(string a, string b)
        {
            string x = (a ?? "").Trim().TrimEnd('\\', '/');
            string y = (b ?? "").Trim().TrimEnd('\\', '/');
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>库根是否落在本次扫描范围内——预置条目 = 锁定主库 / mod 缓存库槽位（不可离线的那些），其余为使用者添加的库根。</summary>
        private static bool InScope(RootEntry root, ScanScope scope, bool isMods)
        {
            if (scope == ScanScope.All)
            {
                return true;
            }
            bool preset = !RootsRules.CanOffline(root, isMods);
            if (scope == ScanScope.Preset)
            {
                return preset;
            }
            return !preset;
        }

        /// <summary>枚举文件——failed 为真表示枚举本身失败（此时空结果不代表目录为空）。</summary>
        private static List<string> Enumerate(string dir, string pattern, bool recurse, Action<string> log, out bool failed)
        {
            failed = false;
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, pattern,
                    recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    set.Add(Path.GetFullPath(f));
                }
            }
            catch (Exception ex)
            {
                failed = true;
                if (log != null)
                {
                    log("  枚举失败：" + ex.Message);
                }
            }
            return new List<string>(set);
        }
    }
}
