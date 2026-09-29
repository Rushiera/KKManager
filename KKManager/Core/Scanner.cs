using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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

    /// <summary>扫描统计（按步骤累计计数——同一步骤跨库根累加）。</summary>
    public class ScanResult
    {
        /// <summary>枚举到的文件数。</summary>
        public int Seen { get; set; }

        /// <summary>步骤实际完成数（按文件 × 步骤计）。</summary>
        public int Added { get; set; }

        /// <summary>步骤跳过数（文件未变且该步已完成）。</summary>
        public int Skipped { get; set; }

        /// <summary>步骤失败数。</summary>
        public int Failed { get; set; }

        /// <summary>无卡类型标记、不算卡片的文件数。</summary>
        public int NonCard { get; set; }
        /// <summary>mod 库内读不到 manifest.xml、不算 mod 的文件数（mod 侧全扫面下的非 mod 文件）。</summary>
        public int NonMod { get; set; }

        /// <summary>本次入库的引用条目数。</summary>
        public long RefEntries { get; set; }

        /// <summary>缩略图字节总量。</summary>
        public long ThumbBytes { get; set; }

        /// <summary>本次扫描新读到角色名的卡片数（库里原先没有该行）。</summary>
        public int NamesRead { get; set; }

        /// <summary>本次扫描为存量卡片补读到角色名的数。</summary>
        public int NamesFilled { get; set; }

        /// <summary>本次扫描补正卡类型的数。</summary>
        public int TypesFixed { get; set; }

        /// <summary>本次扫描读到 timeline 的场景卡数。</summary>
        public int TimelineRead { get; set; }

        /// <summary>提交的写事务批次（同一步骤跨文件 × 500 条一批）。</summary>
        public int Removed { get; set; }

        /// <summary>当前步骤序号（1 起）。</summary>
        public int StepIndex { get; set; }

        /// <summary>步骤总数。</summary>
        public int StepCount { get; set; }

        /// <summary>当前步骤名（段名）。</summary>
        public string StepName { get; set; }

        /// <summary>当前步骤内已处理文件数。</summary>
        public int StepDone { get; set; }

        /// <summary>当前步骤内文件总数（各库根之和）。</summary>
        public int StepTotal { get; set; }

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
            NonMod += other.NonMod;
            RefEntries += other.RefEntries;
            ThumbBytes += other.ThumbBytes;
            NamesRead += other.NamesRead;
            NamesFilled += other.NamesFilled;
            TypesFixed += other.TypesFixed;
            TimelineRead += other.TimelineRead;
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

    /// <summary>一轮扫描里一个文件的内存态——跨步骤复用（省重复 FileInfo 与重复查库）。</summary>
    public class ScanFile
    {
        /// <summary>文件绝对路径。</summary>
        public string Path { get; set; }

        /// <summary>所属库根路径。</summary>
        public string RootPath { get; set; }

        /// <summary>相对库根的文件夹（根目录为空串）。</summary>
        public string Folder { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间戳（UTC 文本）。</summary>
        public string Mtime { get; set; }

        /// <summary>卡片 id（row 步写入或建索引时读得；0 = 尚未建行）。</summary>
        public long CardId { get; set; }

        /// <summary>图片区终点（head 步或建索引时读得）。</summary>
        public long ImageEnd { get; set; }

        /// <summary>卡类型（head 步或建索引时读得）。</summary>
        public string CardType { get; set; }

        /// <summary>库里原先记的卡类型（供类型补正出声）。</summary>
        public string OldCardType { get; set; }

        /// <summary>本轮会话内头段是否已解析（true 时 ImageEnd / CardType 可直接用，不再读文件）。</summary>
        public bool HeadRead { get; set; }

        /// <summary>本轮会话内头段读取失败（失败后不再重试——避免同段内多步各试一次）。</summary>
        public bool HeadFailed { get; set; }

        /// <summary>本轮判定的非卡文件（不是 PNG / 无数据区 / 头段无法识别）——已登记 non_card 清单，后续读步一律跳过。</summary>
        public bool NonCardFile { get; set; }

        /// <summary>非卡判定理由（人读文案——登记清单时落库）。</summary>
        public string NonCardReason { get; set; }

        /// <summary>mod 侧判定的非 mod 文件（读不到 manifest.xml）——已登记 non_card 清单。</summary>
        public bool NonModFile { get; set; }

        /// <summary>mod 副本的 guid（mod 行步写入或建索引时读得）。</summary>
        public string ModGuid { get; set; }

        /// <summary>本轮之前库里是否已有该文件的行（false = 新文件——供「新读 / 补读」区分）。</summary>
        public bool IsNew { get; set; }
    }

    /// <summary>一个库根在本轮扫描中的状态——库文件句柄 / 文件清单 / 已完成步骤索引。</summary>
    public class ScanRootState
    {
        /// <summary>库根条目。</summary>
        public RootEntry Entry { get; set; }

        /// <summary>true = mod 侧。</summary>
        public bool Mods { get; set; }

        /// <summary>该库根对应的库文件（主库条目走主库）。</summary>
        public Store Store { get; set; }

        /// <summary>枚举所得的文件清单（本轮只枚举一次）。</summary>
        public List<ScanFile> Files { get; set; }

        /// <summary>已完成步骤索引——file_path → （步骤 id → 记录）。</summary>
        public Dictionary<string, Dictionary<string, ScanStepRow>> Steps { get; set; }

        /// <summary>枚举是否失败（失败时不清扫——空结果不等于文件消失）。</summary>
        public bool EnumFailed { get; set; }
    }

    /// <summary>库扫描——按步骤编排：同一段内共用一个文件打开；每步独立完成戳（scan_state）。</summary>
    public static class Scanner
    {
        private const int MaxErrors = 200;
        private const int BatchSize = 500;

        /// <summary>
        /// 统一编排入口——按计划的段顺序执行（跨侧交错：卡片行 → mod 总数 → 声明区 …）。
        /// 两侧库根各枚举一次；收尾（清扫 / 作者索引 / 离线标记）在全部段跑完后执行。
        /// </summary>
        public static ScanResult ScanAll(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, int thumbWidth, int thumbQuality, ScanPlan plan, Action<string> log)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            if (plan == null)
            {
                plan = ScanPlanCatalog.Default();
            }
            List<ScanRootState> modStates = CollectRoots(hub, cfg, only, scope, true, result, log);
            List<ScanRootState> cardStates = CollectRoots(hub, cfg, only, scope, false, result, log);
            List<ScanSegment> segs = plan.Segments();
            result.StepCount = segs.Count;

            int index = 0;
            foreach (ScanSegment seg in segs)
            {
                index = index + 1;
                result.StepIndex = index;
                result.StepName = seg.Label;
                List<ScanRootState> states = seg.Mods ? modStates : cardStates;
                result.StepTotal = TotalFiles(states);
                result.StepDone = 0;
                Report(log, "步骤 " + index + "/" + segs.Count + "：" + seg.Label + "（" + result.StepTotal + " 个文件）");
                foreach (ScanRootState st in states)
                {
                    if (seg.Mods)
                    {
                        RunModSegment(hub, cfg, st, seg, force, result, log);
                    }
                    else
                    {
                        RunCardSegment(st, seg, force, thumbWidth, thumbQuality, result, log);
                    }
                }
            }

            foreach (ScanRootState st in cardStates)
            {
                CleanupCards(st, result, log);
            }
            foreach (ScanRootState st in modStates)
            {
                CleanupMods(hub, cfg, st, result, log);
            }

            ApplyOfflineMarks(hub, cfg, result, false, log);
            ApplyOfflineMarks(hub, cfg, result, true, log);
            hub.Core.RefreshModAuthors();
            if (log != null)
            {
                log("作者索引已重建：" + hub.Core.QueryAuthors().Count + " 位作者");
            }

            result.Elapsed = watch.Elapsed;
            return result;
        }

        /// <summary>扫描 mod 库（全部库根）。</summary>
        public static ScanResult ScanMods(StoreHub hub, RootsConfig cfg, bool force, Action<string> log)
        {
            return ScanMods(hub, cfg, null, ScanScope.All, force, null, log);
        }

        /// <summary>扫描 mod 库（默认计划）。</summary>
        public static ScanResult ScanMods(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, Action<string> log)
        {
            return ScanMods(hub, cfg, only, scope, force, null, log);
        }

        /// <summary>扫描 mod 库——plan 为 null 时用默认计划；only 非空时只扫该条库根；离线库一律跳过。</summary>
        public static ScanResult ScanMods(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, ScanPlan plan, Action<string> log)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            if (plan == null)
            {
                plan = ScanPlanCatalog.Default();
            }
            List<ScanRootState> states = CollectRoots(hub, cfg, only, scope, true, result, log);
            List<ScanSegment> segs = SegmentsFor(plan, true);
            result.StepCount = segs.Count;

            int index = 0;
            foreach (ScanSegment seg in segs)
            {
                index = index + 1;
                result.StepIndex = index;
                result.StepName = seg.Label;
                result.StepDone = 0;
                result.StepTotal = TotalFiles(states);
                Report(log, "步骤 " + index + "/" + segs.Count + "：" + seg.Label + "（" + result.StepTotal + " 个文件）");
                foreach (ScanRootState st in states)
                {
                    RunModSegment(hub, cfg, st, seg, force, result, log);
                }
            }

            foreach (ScanRootState st in states)
            {
                CleanupMods(hub, cfg, st, result, log);
            }

            ApplyOfflineMarks(hub, cfg, result, true, log);
            hub.Core.RefreshModAuthors();
            if (log != null)
            {
                log("作者索引已重建：" + hub.Core.QueryAuthors().Count + " 位作者");
            }

            result.Elapsed = watch.Elapsed;
            return result;
        }

        /// <summary>扫描卡片库（全部库根 · 默认计划）。</summary>
        public static ScanResult ScanCards(StoreHub hub, RootsConfig cfg, bool force, int thumbWidth, int thumbQuality, Action<string> log)
        {
            return ScanCards(hub, cfg, null, ScanScope.All, force, thumbWidth, thumbQuality, null, log);
        }

        /// <summary>扫描卡片库（默认计划）。</summary>
        public static ScanResult ScanCards(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, int thumbWidth, int thumbQuality, Action<string> log)
        {
            return ScanCards(hub, cfg, only, scope, force, thumbWidth, thumbQuality, null, log);
        }

        /// <summary>扫描卡片库——plan 为 null 时用默认计划；only 非空时只扫该条库根；离线库一律跳过。</summary>
        public static ScanResult ScanCards(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool force, int thumbWidth, int thumbQuality, ScanPlan plan, Action<string> log)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            if (plan == null)
            {
                plan = ScanPlanCatalog.Default();
            }
            List<ScanRootState> states = CollectRoots(hub, cfg, only, scope, false, result, log);
            List<ScanSegment> segs = SegmentsFor(plan, false);
            result.StepCount = segs.Count;

            int index = 0;
            foreach (ScanSegment seg in segs)
            {
                index = index + 1;
                result.StepIndex = index;
                result.StepName = seg.Label;
                result.StepDone = 0;
                result.StepTotal = TotalFiles(states);
                Report(log, "步骤 " + index + "/" + segs.Count + "：" + seg.Label + "（" + result.StepTotal + " 个文件）");
                result.StepDone = 0;
                foreach (ScanRootState st in states)
                {
                    RunCardSegment(st, seg, force, thumbWidth, thumbQuality, result, log);
                }
            }

            foreach (ScanRootState st in states)
            {
                CleanupCards(st, result, log);
            }

            ApplyOfflineMarks(hub, cfg, result, false, log);
            result.Elapsed = watch.Elapsed;
            return result;
        }

        /// <summary>取某一侧的执行段（顺序来自计划——跨侧交错的段按顺序过滤）。</summary>
        private static List<ScanSegment> SegmentsFor(ScanPlan plan, bool mods)
        {
            List<ScanSegment> list = new List<ScanSegment>();
            foreach (ScanSegment seg in plan.Segments())
            {
                if (seg.Mods == mods)
                {
                    list.Add(seg);
                }
            }
            return list;
        }

        private static int TotalFiles(List<ScanRootState> states)
        {
            int n = 0;
            foreach (ScanRootState st in states)
            {
                n += st.Files.Count;
            }
            return n;
        }

        // [段1] 库根收集与文件清单（枚举只做一次，各段复用）

        /// <summary>收集本次扫描的库根状态——跳过离线库与不存在的目录；枚举一次并缓存（mod 侧全枚举，不限扩展名）。</summary>
        private static List<ScanRootState> CollectRoots(StoreHub hub, RootsConfig cfg, RootEntry only, ScanScope scope, bool mods, ScanResult result, Action<string> log)
        {
            List<ScanRootState> list = new List<ScanRootState>();
            List<RootEntry> roots = mods ? cfg.ModRootsOrdered() : cfg.CardRootsOrdered();
            foreach (RootEntry root in roots)
            {
                if (only != null && !SamePath(root.path, only.path))
                {
                    continue;
                }
                if (!InScope(root, scope, mods))
                {
                    continue;
                }
                if (log != null)
                {
                    if (mods)
                    {
                        log("级别 " + root.tier + "（" + Tier.Name(root.tier) + "） · " + root.path);
                    }
                    else
                    {
                        log("级别 " + root.tier + " · " + root.path + (root.recurse ? "（含子目录）" : "（仅本目录）"));
                    }
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
                    MarkOffline(result, root, mods, log);
                    continue;
                }

                ScanRootState st = new ScanRootState();
                st.Entry = root;
                st.Mods = mods;
                st.Store = hub.StoreFor(root, mods);
                bool enumFailed = false;
                // mod 侧全枚举（不限扩展名）——是不是 mod 由 modrow 步按「能读到 manifest.xml」判定
                List<string> files = Enumerate(root.path, mods ? "*" : "*.png", root.recurse, log, out enumFailed);
                st.EnumFailed = enumFailed;
                result.Seen += files.Count;
                if (files.Count == 0 && !enumFailed)
                {
                    MarkOffline(result, root, mods, log);
                }
                st.Files = BuildFiles(root, files, mods, st.Store);
                st.Steps = st.Store.LoadScanSteps();
                list.Add(st);
            }
            return list;
        }

        /// <summary>建文件清单内存态——从库里的索引补 guid / 卡片 id / 图片区终点 / 旧类型。</summary>
        private static List<ScanFile> BuildFiles(RootEntry root, List<string> files, bool mods, Store store)
        {
            List<ScanFile> list = new List<ScanFile>(files.Count);
            string rootNorm = (root.path ?? "").TrimEnd('\\', '/');
            if (mods)
            {
                Dictionary<string, string> guids = store.LoadModFileGuids();
                foreach (string f in files)
                {
                    FileInfo fi = new FileInfo(f);
                    ScanFile sf = new ScanFile();
                    sf.Path = f;
                    sf.RootPath = root.path;
                    sf.Folder = FolderOf(f, rootNorm);
                    sf.Size = fi.Exists ? fi.Length : 0;
                    sf.Mtime = Store.StampOf(fi);
                    string guid;
                    if (guids.TryGetValue(f, out guid))
                    {
                        sf.ModGuid = guid;
                    }
                    else
                    {
                        sf.IsNew = true;
                    }
                    list.Add(sf);
                }
                return list;
            }

            Dictionary<string, string[]> stamps = store.LoadCardStamps();
            foreach (string f in files)
            {
                FileInfo fi = new FileInfo(f);
                ScanFile sf = new ScanFile();
                sf.Path = f;
                sf.RootPath = root.path;
                sf.Folder = FolderOf(f, rootNorm);
                sf.Size = fi.Exists ? fi.Length : 0;
                sf.Mtime = Store.StampOf(fi);
                string[] old;
                if (stamps.TryGetValue(f, out old))
                {
                    if (old.Length > 3)
                    {
                        sf.OldCardType = old[3];
                    }
                    if (old.Length > 5)
                    {
                        sf.CardId = ParseLong(old[5]);
                    }
                    if (old.Length > 6)
                    {
                        sf.ImageEnd = ParseLong(old[6]);
                    }
                }
                else
                {
                    sf.IsNew = true;
                }
                list.Add(sf);
            }
            return list;
        }

        // [段2] 卡片侧执行

        /// <summary>卡片侧一段的执行——按库根循环文件；段内步骤共享一次文件打开（阶段 2 起）。</summary>
        private static void RunCardSegment(ScanRootState st, ScanSegment seg, bool force, int thumbWidth, int thumbQuality, ScanResult result, Action<string> log)
        {
            Store store = st.Store;
            int i = 0;
            int batch = 0;
            int total = st.Files.Count;
            store.Begin();
            try
            {
                foreach (ScanFile f in st.Files)
                {
                    i = i + 1;
                    result.StepDone = result.StepDone + 1;
                    CardFileSession session = null;
                    if (seg.Tier != ScanReadTier.None)
                    {
                        session = CardFileSession.Open(f.Path);
                    }
                    try
                    {
                        foreach (ScanStepDef step in seg.Steps)
                        {
                            if (!force && StepDone(st, f, step.Id))
                            {
                                result.Skipped = result.Skipped + 1;
                                continue;
                            }
                            bool ok = RunCardStep(store, st.Entry, f, session, step, thumbWidth, thumbQuality, result);
                            if (ok)
                            {
                                store.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime);
                                result.Added = result.Added + 1;
                            }
                            else
                            {
                                result.Failed = result.Failed + 1;
                            }
                        }
                    }
                    finally
                    {
                        if (session != null)
                        {
                            session.Dispose();
                        }
                    }
                    batch = batch + 1;
                    if (batch >= BatchSize)
                    {
                        store.Commit();
                        store.Begin();
                        batch = 0;
                    }
                    if (log != null && i % 200 == 0)
                    {
                        log("  " + seg.Label + " " + i + "/" + total);
                    }
                }
                store.Commit();
            }
            catch
            {
                store.Rollback();
                throw;
            }
        }

        /// <summary>卡片侧单步执行——返回 true 表示该步对这一文件已完成（可落完成戳）。</summary>
        private static bool RunCardStep(Store store, RootEntry root, ScanFile f, CardFileSession session, ScanStepDef step, int thumbWidth, int thumbQuality, ScanResult result)
        {
            // 非卡文件——头段步已登记进 non_card 清单，后续读步一律跳过（不建卡行 / 不读引用 / 不出图）
            if (f.NonCardFile)
            {
                return true;
            }
            if (step.Id == "row")
            {
                f.CardId = store.UpsertCardRow(f.Path, Path.GetFileName(f.Path), f.Size, f.Mtime, root, f.Folder);
                return f.CardId > 0;
            }
            if (step.Id == "head")
            {
                return StepCardHead(store, f, session, result);
            }
            if (step.Id == "refs")
            {
                return StepCardRefs(store, f, session, result);
            }
            if (step.Id == "name")
            {
                return StepCardName(store, f, session, result);
            }
            if (step.Id == "timeline")
            {
                return StepCardTimeline(store, f, session, result);
            }
            if (step.Id == "thumb")
            {
                return StepCardThumb(store, f, session, thumbWidth, thumbQuality, result);
            }
            if (step.Id == "coord" || step.Id == "detail" || step.Id == "scene")
            {
                return StepCardAnalysis(store, f, step.Id, session, result);
            }
            return false;
        }

        /// <summary>卡头段步——写卡类型 / 数据版本 / 图片区终点；非卡文件（不是 PNG / PNG 不完整 / 无数据区 / 头段无法识别）登记进非卡清单（理由 + 缩略图）并核销卡片行，不计失败（后续读步由 RunCardStep 跳过）。</summary>
        private static bool StepCardHead(Store store, ScanFile f, CardFileSession session, ScanResult result)
        {
            string type;
            string ver;
            long imageEnd;
            string reason;
            CardHeadState state;
            if (session != null)
            {
                state = CardReader.ReadHeadState(session, out type, out ver, out imageEnd, out reason);
            }
            else
            {
                state = CardReader.ReadHeadState(f.Path, out type, out ver, out imageEnd, out reason);
            }
            if (state == CardHeadState.Io)
            {
                f.HeadFailed = true;
                AddError(result, Path.GetFileName(f.Path) + " 头段读取失败");
                return false;
            }
            if (state != CardHeadState.Ok)
            {
                // 非卡文件——登记清单（理由 + 缩略图）、核销卡片行；不计失败，后续读步由 RunCardStep 跳过
                f.NonCardFile = true;
                f.NonCardReason = reason;
                store.DeleteCardByPath(f.Path);
                store.UpsertNonCard(f.Path, f.RootPath, "card", reason, f.Size, f.Mtime, BuildNonCardThumb(f.Path, f.Size));
                result.NonCard = result.NonCard + 1;
                return true;
            }
            if (f.OldCardType != null && f.OldCardType.Length > 0 && f.OldCardType != type)
            {
                result.TypesFixed = result.TypesFixed + 1;
            }
            f.CardType = type;
            f.ImageEnd = imageEnd;
            f.HeadRead = true;
            store.DeleteNonCardByPath(f.Path);
            store.UpdateCardHead(f.Path, type, ver, imageEnd);
            return true;
        }

        /// <summary>非卡文件缩略图体积上限（超过不出图——避免把大文件整读进内存）。</summary>
        private const long NonCardThumbMaxBytes = 64L * 1024 * 1024;

        /// <summary>非卡文件缩略图宽度（像素）。</summary>
        private const int NonCardThumbWidth = 256;

        /// <summary>非卡文件缩略图 JPEG 质量（1-100）。</summary>
        private const int NonCardThumbQuality = 82;

        /// <summary>非卡文件的缩略图——只对可解码图片（且体积在上限内）生成；失败或非图片返回 null（界面显示占位）。</summary>
        private static byte[] BuildNonCardThumb(string path, long size)
        {
            if (string.IsNullOrEmpty(path) || size <= 0 || size > NonCardThumbMaxBytes)
            {
                return null;
            }
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                return null;
            }
            string lower = ext.ToLowerInvariant();
            if (lower != ".png" && lower != ".jpg" && lower != ".jpeg" && lower != ".bmp" && lower != ".gif" && lower != ".webp")
            {
                return null;
            }
            try
            {
                return Thumbnail.FromBytes(File.ReadAllBytes(path), NonCardThumbWidth, NonCardThumbQuality);
            }
            catch (Exception)
            {
                // 图片坏 / 解码不支持——无图（界面显示占位），不阻断扫描
                return null;
            }
        }

        /// <summary>声明区步——取 mod 引用 + UAR 块数并落库。</summary>
        private static bool StepCardRefs(Store store, ScanFile f, CardFileSession session, ScanResult result)
        {
            if (!EnsureHead(store, f, session, result))
            {
                return false;
            }
            if (f.CardId <= 0)
            {
                f.CardId = store.CardIdOf(f.Path);
            }
            int uar = 0;
            List<ModRef> refs;
            if (session != null)
            {
                refs = CardReader.CollectRefs(session, out uar);
            }
            else
            {
                refs = CardReader.CollectRefs(f.Path, f.ImageEnd, out uar);
            }
            store.ReplaceCardRefs(f.CardId, refs);
            store.UpdateCardRefsMeta(f.Path, CountDistinct(refs), uar);
            result.RefEntries += refs.Count;
            return true;
        }

        /// <summary>角色名步——人物卡读 Parameter 的姓 / 名（其余卡型直接算完成）。</summary>
        private static bool StepCardName(Store store, ScanFile f, CardFileSession session, ScanResult result)
        {
            if (!EnsureHead(store, f, session, result))
            {
                return false;
            }
            if (f.CardType == null || f.CardType.IndexOf("Chara", StringComparison.Ordinal) < 0)
            {
                return true;
            }
            string name;
            if (session != null)
            {
                name = CardName.ReadCharacter(session);
            }
            else
            {
                name = CardName.ReadCharacter(f.Path);
            }
            store.UpdateCardName(f.Path, name);
            if (name != null)
            {
                if (f.IsNew)
                {
                    result.NamesRead = result.NamesRead + 1;
                }
                else
                {
                    result.NamesFilled = result.NamesFilled + 1;
                }
            }
            return true;
        }

        /// <summary>时间轴步——场景卡读 Timeline 条目（其余卡型直接算完成）。</summary>
        private static bool StepCardTimeline(Store store, ScanFile f, CardFileSession session, ScanResult result)
        {
            if (!EnsureHead(store, f, session, result))
            {
                return false;
            }
            if (f.CardType != CardReader.SceneCardType)
            {
                return true;
            }
            if (f.CardId <= 0)
            {
                f.CardId = store.CardIdOf(f.Path);
            }
            TimelineInfo tl = TimelineReader.Read(f.Path, f.ImageEnd, session);
            if (tl.Error != null)
            {
                AddError(result, Path.GetFileName(f.Path) + " timeline → " + tl.Error);
                return false;
            }
            store.SaveCardTimeline(f.CardId, f.Path, f.Size, f.Mtime, tl);
            result.TimelineRead = result.TimelineRead + 1;
            return true;
        }

        /// <summary>缩略图步——图片区取图并缩为 JPEG。</summary>
        private static bool StepCardThumb(Store store, ScanFile f, CardFileSession session, int thumbWidth, int thumbQuality, ScanResult result)
        {
            if (!EnsureHead(store, f, session, result))
            {
                return false;
            }
            if (thumbWidth <= 0)
            {
                return true;
            }
            byte[] thumb;
            if (session != null)
            {
                thumb = Thumbnail.FromCard(session, f.ImageEnd, thumbWidth, thumbQuality);
            }
            else
            {
                thumb = Thumbnail.FromCard(f.Path, f.ImageEnd, thumbWidth, thumbQuality);
            }
            if (thumb == null)
            {
                AddError(result, Path.GetFileName(f.Path) + " 缩略图 → " + (Thumbnail.LastError ?? "未知原因"));
                return false;
            }
            store.UpdateCardThumb(f.Path, thumb);
            result.ThumbBytes += thumb.Length;
            return true;
        }

        /// <summary>分项步（服装槽位 / 卡片分析 / 场景深度）——落库（兑现见阶段 3）。</summary>
        private static bool StepCardAnalysis(Store store, ScanFile f, string stepId, CardFileSession session, ScanResult result)
        {
            if (!EnsureHead(store, f, session, result))
            {
                return false;
            }
            return CardAnalysis.Run(store, f, stepId, result, session);
        }

        /// <summary>按需前置——本步需要图片区终点时，若本轮尚未解析则就地读头段（顺序自由的关键）。</summary>
        private static bool EnsureHead(Store store, ScanFile f, CardFileSession session, ScanResult result)
        {
            if (f.HeadRead && f.ImageEnd > 0)
            {
                return true;
            }
            if (f.HeadFailed)
            {
                return false;
            }
            if (!StepCardHead(store, f, session, result))
            {
                return false;
            }
            store.MarkScanStep(f.Path, "head", f.Size, f.Mtime);
            return f.ImageEnd > 0;
        }

        // [段3] mod 侧执行

        /// <summary>mod 侧一段的执行。</summary>
        private static void RunModSegment(StoreHub hub, RootsConfig cfg, ScanRootState st, ScanSegment seg, bool force, ScanResult result, Action<string> log)
        {
            Store store = st.Store;
            int i = 0;
            int batch = 0;
            int total = st.Files.Count;
            store.Begin();
            try
            {
                foreach (ScanFile f in st.Files)
                {
                    i = i + 1;
                    result.StepDone = result.StepDone + 1;
                    foreach (ScanStepDef step in seg.Steps)
                    {
                        if (!force && StepDone(st, f, step.Id))
                        {
                            result.Skipped = result.Skipped + 1;
                            continue;
                        }
                        bool skip;
                        bool ok = RunModStep(hub, cfg, store, st, f, step, force, result, out skip);
                        if (ok)
                        {
                            store.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime);
                            result.Added = result.Added + 1;
                        }
                        else if (skip)
                        {
                            // 非 mod 文件——判定完也落戳（下次不再重复解析），单独计数出声
                            store.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime);
                            result.NonMod = result.NonMod + 1;
                        }
                        else
                        {
                            result.Failed = result.Failed + 1;
                        }
                    }
                    batch = batch + 1;
                    if (batch >= BatchSize)
                    {
                        store.Commit();
                        store.Begin();
                        batch = 0;
                    }
                    if (log != null && i % 2000 == 0)
                    {
                        log("  " + seg.Label + " " + i + "/" + total);
                    }
                }
                store.Commit();
            }
            catch
            {
                store.Rollback();
                throw;
            }
        }

        /// <summary>mod 侧单步执行——skip 为真表示该文件不是 mod（读不到 manifest.xml），已判定为跳过。</summary>
        private static bool RunModStep(StoreHub hub, RootsConfig cfg, Store store, ScanRootState st, ScanFile f, ScanStepDef step, bool force, ScanResult result, out bool skip)
        {
            skip = false;
            if (step.Id == "modrow")
            {
                ModInfo m = ZipModReader.Parse(f.Path);
                if (m.ErrorKind == ModErrorKind.NotContainer || m.ErrorKind == ModErrorKind.NoManifest)
                {
                    // 读不到 manifest.xml 的文件——不是 mod：登记进非卡清单（跳过，不算失败）
                    skip = true;
                    f.NonModFile = true;
                    string skipReason = "不是 zip 容器";
                    if (m.ErrorKind == ModErrorKind.NoManifest)
                    {
                        skipReason = "容器内无 manifest.xml";
                    }
                    store.UpsertNonCard(f.Path, f.RootPath, "mod", skipReason, f.Size, f.Mtime, BuildNonCardThumb(f.Path, f.Size));
                    return false;
                }
                if (!string.IsNullOrEmpty(m.Error) || string.IsNullOrEmpty(m.Guid))
                {
                    AddError(result, m.FileName + " → " + (m.Error ?? "无 guid"));
                    return false;
                }
                f.ModGuid = m.Guid;
                store.UpsertModFile(m, st.Entry, f.Mtime);
                store.FillModMeta(m);
                store.DeleteNonCardByPath(f.Path);
                return true;
            }
            if (step.Id == "composition")
            {
                if (string.IsNullOrEmpty(f.ModGuid))
                {
                    ModInfo again = ZipModReader.Parse(f.Path);
                    if (again.ErrorKind == ModErrorKind.NotContainer || again.ErrorKind == ModErrorKind.NoManifest)
                    {
                        skip = true;
                        return false;
                    }
                    f.ModGuid = again.Guid;
                }
                if (string.IsNullOrEmpty(f.ModGuid))
                {
                    AddError(result, Path.GetFileName(f.Path) + " → 无 guid，跳过组成建档");
                    return false;
                }
                string error;
                ModComposition rec = hub.AnalyzeComposition(cfg, f.ModGuid, force, out error);
                if (rec == null)
                {
                    AddError(result, Path.GetFileName(f.Path) + " 组成 → " + error);
                    return false;
                }
                return true;
            }
            return false;
        }

        // [段4] 收尾——清扫与索引

        /// <summary>卡片侧清扫——删除磁盘上已消失的卡片行与步骤记录。</summary>
        private static void CleanupCards(ScanRootState st, ScanResult result, Action<string> log)
        {
            if (st.EnumFailed || st.Files == null || st.Files.Count == 0)
            {
                return;
            }
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScanFile f in st.Files)
            {
                present.Add(f.Path);
            }
            st.Store.Begin();
            try
            {
                int goneCards = st.Store.DeleteCardsMissingUnderRoot(st.Entry.path, present);
                int goneSteps = st.Store.DeleteScanStepsMissingUnderRoot(st.Entry.path, present);
                int goneNon = st.Store.DeleteNonCardsMissingUnderRoot(st.Entry.path);
                st.Store.Commit();
                if (goneCards > 0 || goneSteps > 0 || goneNon > 0)
                {
                    result.Removed += goneCards;
                    if (log != null)
                    {
                        log("  已清理 " + goneCards + " 条已消失的卡片记录 · 步骤记录 " + goneSteps + " 条 · 非卡登记 " + goneNon + " 条");
                    }
                }
            }
            catch
            {
                st.Store.Rollback();
                throw;
            }
        }

        /// <summary>mod 侧清扫——删除已消失的副本与旧版登记，并跨库重算受影响的 guid。</summary>
        private static void CleanupMods(StoreHub hub, RootsConfig cfg, ScanRootState st, ScanResult result, Action<string> log)
        {
            if (st.EnumFailed || st.Files == null || st.Files.Count == 0)
            {
                return;
            }
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScanFile f in st.Files)
            {
                present.Add(f.Path);
            }
            st.Store.Begin();
            List<string> goneGuids;
            int goneOld;
            int goneSteps;
            int goneNon;
            try
            {
                goneGuids = st.Store.DeleteModFilesMissingUnderRoot(st.Entry.path, present);
                goneOld = hub.Core.DeleteModOldMissingUnderRoot(st.Entry.path, present);
                goneSteps = st.Store.DeleteScanStepsMissingUnderRoot(st.Entry.path, present);
                goneNon = st.Store.DeleteNonCardsMissingUnderRoot(st.Entry.path);
                st.Store.Commit();
            }
            catch
            {
                st.Store.Rollback();
                throw;
            }
            foreach (string guid in goneGuids)
            {
                hub.RecomputeMod(cfg, guid);
            }
            if ((goneGuids.Count > 0 || goneOld > 0 || goneNon > 0) && log != null)
            {
                result.Removed += goneGuids.Count + goneOld;
                log("  已清理 " + goneGuids.Count + " 个 guid 的已消失副本记录 · 旧版登记 " + goneOld + " 条 · 步骤记录 " + goneSteps + " 条 · 非卡登记 " + goneNon + " 条");
            }
        }

        // [段5] 辅助

        /// <summary>该文件该步骤是否已完成——size + mtime 双等且步骤记录在案。</summary>
        private static bool StepDone(ScanRootState st, ScanFile f, string stepId)
        {
            if (st.Steps == null)
            {
                return false;
            }
            Dictionary<string, ScanStepRow> steps;
            if (!st.Steps.TryGetValue(f.Path, out steps))
            {
                return false;
            }
            ScanStepRow rec;
            if (!steps.TryGetValue(stepId, out rec))
            {
                return false;
            }
            return rec.Size == f.Size && string.Equals(rec.Mtime, f.Mtime, StringComparison.Ordinal);
        }

        private static int CountDistinct(List<ModRef> refs)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModRef r in refs)
            {
                if (!string.IsNullOrEmpty(r.ModId))
                {
                    seen.Add(r.ModId);
                }
            }
            return seen.Count;
        }

        private static long ParseLong(string text)
        {
            long v;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return 0;
        }

        private static void Report(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }

        /// <summary>把扫描结果里的「该离线」库根落进配置并登记待办——同库只留一条待办；预置条目已在扫描侧排除。</summary>
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
        /// <summary>扫描插件库根下的全部 dll 并落库（全量重扫——预置只读一条，dll 数量有限，无需步级增量）。</summary>
        /// <param name="store">主库（插件库数据落主库）。</param>
        /// <param name="cfg">库根配置。</param>
        /// <param name="errors">解析失败 / 枚举失败清单（出声用）。</param>
        /// <returns>落库的 dll 项数（含未解析出插件特性的非插件 dll）。</returns>
        public static int ScanPlugins(Store store, RootsConfig cfg, List<string> errors)
        {
            int total = 0;
            List<PluginRow> old = store.LoadPlugins();
            foreach (RootEntry root in cfg.pluginRoots)
            {
                if (string.IsNullOrWhiteSpace(root.path) || !Directory.Exists(root.path))
                {
                    errors.Add("插件库根不存在：" + root.path);
                    continue;
                }
                List<string> files = new List<string>();
                SearchOption option = root.recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                try
                {
                    files.AddRange(Directory.GetFiles(root.path, "*.dll", option));
                }
                catch (Exception ex)
                {
                    errors.Add("插件库枚举失败（" + root.path + "）：" + ex.Message);
                    continue;
                }
                total = total + files.Count;
                // [段1] 建本次清单与旧戳索引——未变的 dll 跳过解析（增量判据 = size + mtime 双等）
                HashSet<string> keep = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, long> oldSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> oldMtime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (PluginRow row in old)
                {
                    if (!string.Equals(row.RootPath, root.path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!oldSize.ContainsKey(row.FilePath))
                    {
                        oldSize[row.FilePath] = row.Size;
                        oldMtime[row.FilePath] = row.Mtime;
                    }
                }
                // [段2] 清理已消失的 dll——本次清单里没有的旧行直接删
                foreach (string oldPath in oldSize.Keys)
                {
                    if (!keep.Contains(oldPath))
                    {
                        store.DeletePluginFile(oldPath);
                    }
                }
                // [段3] 并行解析（每文件独立只读）——未变的跳过；落库串行（SQLite 单写，锁保护）
                object gate = new object();
                Parallel.ForEach(files, file =>
                {
                    FileInfo fi = new FileInfo(file);
                    long size = fi.Exists ? fi.Length : 0;
                    string mtime = fi.Exists ? fi.LastWriteTimeUtc.ToString("o") : "";
                    long prevSize;
                    string prevMtime;
                    if (oldSize.TryGetValue(file, out prevSize) && oldMtime.TryGetValue(file, out prevMtime)
                        && prevSize == size && string.Equals(prevMtime, mtime, StringComparison.Ordinal))
                    {
                        return;
                    }
                    string error;
                    List<PluginInfo> plugins = PluginReader.ReadAll(file, out error);
                    lock (gate)
                    {
                        store.DeletePluginFile(file);
                        if (plugins.Count == 0)
                        {
                            // 未解析出插件特性（原生 dll / 无特性）——仍落一行（guid 空），让「装了但不是插件」可见
                            PluginRow bad = new PluginRow();
                            bad.FilePath = file;
                            bad.RootPath = root.path;
                            bad.FileName = Path.GetFileName(file);
                            bad.Size = size;
                            bad.Mtime = mtime;
                            store.SavePlugin(bad);
                        }
                        else
                        {
                            foreach (PluginInfo info in plugins)
                            {
                                PluginRow row = new PluginRow();
                                row.FilePath = file;
                                row.Guid = info.guid;
                                row.RootPath = root.path;
                                row.FileName = info.fileName;
                                row.Name = info.name;
                                row.Version = info.version;
                                row.Processes = string.Join(",", info.processes);
                                row.Dependencies = string.Join(",", info.dependencies);
                                row.IsIpa = info.isIpa;
                                row.Size = size;
                                row.Mtime = mtime;
                                store.SavePlugin(row);
                            }
                        }
                    }
                });
            }
            return total;
        }
    }
}
