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

        /// <summary>本次扫描枚举到的插件 dll 数（插件库连带扫描——主要库扫描）。</summary>
        public int PluginDlls { get; set; }

        /// <summary>本次扫描落库的插件项数（有 guid 的插件行）。</summary>
        public int Plugins { get; set; }

        /// <summary>本次扫描读到的插件配置文件（cfg）数。</summary>
        public int PluginConfigs { get; set; }

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
        /// <summary>并行 worker 共享的已完成文件计数（Interlocked 累加）——StepDone 的并行口径，仅供进度显示，不参与结果合并。</summary>
        public int SharedDone;

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
            PluginDlls += other.PluginDlls;
            Plugins += other.Plugins;
            PluginConfigs += other.PluginConfigs;
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

    /// <summary>插件库扫描计数——dll 数 / 插件项数 / 配置文件数。</summary>
    public class PluginScanResult
    {
        /// <summary>枚举到的 dll 数（含未解析出插件特性的非插件 dll）。</summary>
        public int Dlls { get; set; }

        /// <summary>落库的插件项数（有 guid 的插件行）。</summary>
        public int Plugins { get; set; }

        /// <summary>读到的配置文件（cfg）数。</summary>
        public int Configs { get; set; }
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
        /// <summary>并行分片库（卡片侧）——每分片一个独立 db（**常驻**，不再合并）；文件按路径哈希恒落同一分片，跨扫描稳定。非卡片侧为 null。</summary>
        public List<Store> Shards;

        /// <summary>分片库对应的完成戳索引（与 Shards 同序）——每片各读自己的 scan_state。</summary>
        public List<Dictionary<string, Dictionary<string, ScanStepRow>>> ShardSteps;
    }

    /// <summary>库扫描——按步骤编排：同一段内共用一个文件打开；每步独立完成戳（scan_state）。</summary>
    public static class Scanner
    {
        private const int MaxErrors = 200;
        private const int BatchSize = 500;

        /// <summary>先导段的小批量提交间隔——比常规段小得多，让面板轮询时能看见卡片陆续出现。</summary>
        private const int PreludeBatchSize = 8;
        /// <summary>并行 worker 数上限——分片库方案（每 worker 一个独立 db，末尾一次合并）；多连接写同一库已证伪
        /// （451 张 14.9 s → 81.3 s，SQLite 写锁竞争），故并行只在分片库上做。</summary>
        private const int MaxScanWorkers = 32;

        /// <summary>并行 worker 数的默认值（无环境变量 / 库设置时的回落——与分片数目标同值，把分片铺满）。</summary>
        private const int DefaultScanWorkers = 32;

        /// <summary>启用并行的最小文件数——小库不值得为分片库付建库成本。</summary>
        private const int MinFilesForParallel = 200;

        /// <summary>进度出声的文件间隔（并行时按全局计数，串行时按本 worker 计数）——面板进度条按日志行解析，本值即进度刷新粒度。</summary>
        private const int ProgressEvery = 5;

        /// <summary>mod 侧进度出声的文件间隔——mod 库动辄上万文件，出声过密会拖慢扫描（mod 行步骤几乎零耗时）。</summary>
        private const int ModProgressEvery = 200;

        /// <summary>插件库进度出声的 dll 间隔——dll 数百个，出声过密会把日志刷成瀑布。</summary>
        private const int PluginProgressEvery = 50;

        /// <summary>逐文件诊断出声的默认值（无环境变量 / 库设置时的回落——并行卡点定位用）。</summary>
        private const bool DefaultScanTrace = false;

        /// <summary>
        /// 统一编排入口——按计划的段顺序执行（跨侧交错：卡片行 → mod 总数 → 声明区 …）。
        /// 两侧库根各枚举一次；收尾（清扫 / 作者索引 / 离线标记）在全部段跑完后执行。
        /// 全量扫描（only 为空）时，人物卡主库先跑一段「先导」——只扫该库根**本目录**（不含子文件夹）
        /// 的卡片，只建卡片行（不解析、不出缩略图），按修改时间倒序，让面板在其余扫描开始前就能看见文件名与卡片原图；
        /// 其余步骤照旧（含子文件夹），先导已写完成戳的会被跳过。
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
            bool withPlugins = scope == ScanScope.Preset && only == null;
            ScanRootState preludeRoot = only == null ? FindFemaleRoot(cardStates) : null;
            List<ScanFile> preludeFiles = preludeRoot == null ? null : PreludeFiles(preludeRoot);
            result.StepCount = segs.Count + (withPlugins ? 1 : 0) + (preludeRoot == null ? 0 : 1);
            int workers = ResolveWorkers(hub);
            bool trace = ResolveTrace(hub);
            if (log != null && workers > 1)
            {
                log("并行扫描：卡片侧 worker 数 " + workers + "（每 worker 一个分片库，卡片侧跑完一次并入）");
            }

            int index = 0;
            if (preludeRoot != null)
            {
                index = 1;
                result.StepIndex = index;
                result.StepName = PreludeStepName;
                result.StepTotal = preludeFiles.Count;
                result.StepDone = 0;
                Report(log, "步骤 " + index + "/" + result.StepCount + "：" + PreludeStepName + "（" + preludeFiles.Count + " 个文件）");
                RunPrelude(hub, preludeRoot, preludeFiles, force, result, log);
            }
            foreach (ScanSegment seg in segs)
            {
                index = index + 1;
                result.StepIndex = index;
                result.StepName = seg.Label;
                List<ScanRootState> states = seg.Mods ? modStates : cardStates;
                result.StepTotal = TotalFiles(states);
                result.StepDone = 0;
                Report(log, "步骤 " + index + "/" + result.StepCount + "：" + seg.Label + "（" + result.StepTotal + " 个文件）");
                foreach (ScanRootState st in states)
                {
                    if (seg.Mods)
                    {
                        RunModSegment(hub, cfg, st, seg, force, result, log);
                    }
                    else
                    {
                        RunCardSegment(hub, st, seg, force, thumbWidth, thumbQuality, result, log, workers, trace);
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

            // [插件库] 主要库扫描的连带段——插件库根（BepInEx）只读解析 dll 元数据与 cfg（数据落主库）；
            // 追加库扫描（extra）与单库更新（only）不连带——插件库只有预置锁定一条，随主要库走
            if (withPlugins)
            {
                index = index + 1;
                result.StepIndex = index;
                result.StepName = "插件库（dll 元数据 + 配置文件）";
                result.StepTotal = CountPluginDlls(cfg, result);
                result.StepDone = 0;
                Report(log, "步骤 " + index + "/" + result.StepCount + "：插件库（dll 元数据 + 配置文件）（" + result.StepTotal + " 个文件）");
                List<string> pluginErrors = new List<string>();
                PluginScanResult scan = ScanPlugins(hub.Core, cfg, pluginErrors, log);
                foreach (string e in pluginErrors)
                {
                    AddError(result, e);
                }
                result.PluginDlls = scan.Dlls;
                result.Plugins = scan.Plugins;
                result.PluginConfigs = scan.Configs;
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
            int workers = ResolveWorkers(hub);
            bool trace = ResolveTrace(hub);
            if (log != null && workers > 1)
            {
                log("并行扫描：卡片侧 worker 数 " + workers + "（每 worker 一个分片库，卡片侧跑完一次并入）");
            }

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
                    RunCardSegment(hub, st, seg, force, thumbWidth, thumbQuality, result, log, workers, trace);
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
        /// <summary>解析一个计数字符串（空 / 非法 / 非正一律返回 0）。</summary>
        private static int ReadCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }
            int v;
            if (!int.TryParse(text.Trim(), out v))
            {
                return 0;
            }
            if (v <= 0)
            {
                return 0;
            }
            return v;
        }
        /// <summary>并行 worker 数——优先级：环境变量 KKM_SCAN_WORKERS → 库设置 scan_workers → 默认值；上限 MaxScanWorkers。</summary>
        private static int ResolveWorkers(StoreHub hub)
        {
            int n = ReadCount(Environment.GetEnvironmentVariable("KKM_SCAN_WORKERS"));
            if (n <= 0 && hub != null)
            {
                n = ReadCount(hub.Core.GetSetting("scan_workers"));
            }
            if (n <= 0)
            {
                n = DefaultScanWorkers;
            }
            if (n > MaxScanWorkers)
            {
                n = MaxScanWorkers;
            }
            return n;
        }
        /// <summary>并行扫描的逐文件诊断出声（每文件 + 步名，定位卡点用）——环境变量 KKM_SCAN_TRACE=1 或库设置 scan_trace=1 打开。</summary>
        private static bool ResolveTrace(StoreHub hub)
        {
            if (ReadCount(Environment.GetEnvironmentVariable("KKM_SCAN_TRACE")) > 0)
            {
                return true;
            }
            if (hub != null && ReadCount(hub.Core.GetSetting("scan_trace")) > 0)
            {
                return true;
            }
            return DefaultScanTrace;
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
                st.Files = BuildFiles(hub, root, files, mods, st.Store);
                st.Steps = LoadSteps(hub, root, mods, st.Store);
                list.Add(st);
            }
            return list;
        }

        /// <summary>读完成戳索引——卡片侧跨分片合并（分片常驻后每片各存自己的戳），mod 侧读本库。</summary>
        private static Dictionary<string, Dictionary<string, ScanStepRow>> LoadSteps(StoreHub hub, RootEntry root, bool mods, Store store)
        {
            if (mods)
            {
                return store.LoadScanSteps();
            }
            Dictionary<string, Dictionary<string, ScanStepRow>> map =
                new Dictionary<string, Dictionary<string, ScanStepRow>>(StringComparer.OrdinalIgnoreCase);
            int baseLib = hub.CardBaseOf(root);
            foreach (Store s in hub.StoresOfBase(baseLib))
            {
                foreach (KeyValuePair<string, Dictionary<string, ScanStepRow>> kv in s.LoadScanSteps())
                {
                    map[kv.Key] = kv.Value;
                }
            }
            return map;
        }

        /// <summary>建文件清单内存态——从库里的索引补 guid / 卡片 id / 图片区终点 / 旧类型（卡片侧跨分片合并，分片库常驻）。</summary>
        private static List<ScanFile> BuildFiles(StoreHub hub, RootEntry root, List<string> files, bool mods, Store store)
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

            // 卡片侧——分片库常驻，索引跨分片合并（每个文件只在一片，后写者覆盖无冲突）
            Dictionary<string, string[]> stamps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            int baseLib = hub.CardBaseOf(root);
            foreach (Store s in hub.StoresOfBase(baseLib))
            {
                foreach (KeyValuePair<string, string[]> kv in s.LoadCardStamps())
                {
                    stamps[kv.Key] = kv.Value;
                }
            }
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

        /// <summary>卡片侧一段的执行——按库根循环文件；段内步骤共享一次文件打开（阶段 2 起）；并行时文件按路径哈希落分片库（常驻，不合并）。</summary>
        private static void RunCardSegment(StoreHub hub, ScanRootState st, ScanSegment seg, bool force, int thumbWidth, int thumbQuality, ScanResult result, Action<string> log, int workers = 1, bool trace = false)
        {
            int total = st.Files.Count;
            // [段1] 分片布局——**与并发度解耦**：片数由库根定一次并常驻（跨扫描稳定，行按路径哈希恒落同片），
            // 并发度只决定同时跑几片。实测：合并占并行总耗时 60%（30 片 ≈ 15 分钟级）⇒ 分片库不再合并。
            // （多连接写同一库已证伪：451 张 14.9 s → 81.3 s，SQLite 写锁竞争）
            EnsureShards(hub, st, total, log);
            int shards = st.Shards.Count;

            // [段2] 按路径哈希分桶——同一文件恒落同一片
            List<ScanFile>[] buckets = new List<ScanFile>[shards];
            for (int k = 0; k < shards; k = k + 1)
            {
                buckets[k] = new List<ScanFile>();
            }
            foreach (ScanFile f in st.Files)
            {
                buckets[StoreHub.ShardOfPath(f.Path, shards)].Add(f);
            }

            // [段3] 并发度 = min(worker 设置, 片数)；片多于并发度时每线程领多片
            int threads = workers;
            if (threads > shards)
            {
                threads = shards;
            }
            if (threads < 1)
            {
                threads = 1;
            }
            result.SharedDone = 0;
            if (threads <= 1)
            {
                // 串行——逐片跑（布局与并行时完全一致）；`one` 跨片复用，StepDone 因而是全程累加的
                ScanResult one = new ScanResult();
                for (int k = 0; k < shards; k = k + 1)
                {
                    RunCardRange(st, seg, force, thumbWidth, thumbQuality, one, log, buckets[k], st.Shards[k], st.ShardSteps[k], null, false);
                }
                result.Merge(one);
                return;
            }

            ScanResult[] locals = new ScanResult[threads];
            Task[] tasks = new Task[threads];
            for (int t = 0; t < threads; t = t + 1)
            {
                int index = t;
                locals[index] = new ScanResult();
                tasks[index] = Task.Run(delegate
                {
                    // 该线程领分片 t, t+threads, t+2*threads …（片与线程按模分配，均衡）
                    for (int k = index; k < shards; k = k + threads)
                    {
                        RunCardRange(st, seg, force, thumbWidth, thumbQuality, locals[index], log, buckets[k], st.Shards[k], st.ShardSteps[k], result, trace);
                    }
                });
            }
            Task.WaitAll(tasks);
            foreach (ScanResult local in locals)
            {
                result.Merge(local);
            }
        }
        /// <summary>卡片侧一段的执行（分片版）——按给定的文件子集与目标库跑；steps 为该分片自己的完成戳索引（并行时非空）。</summary>
        private static void RunCardRange(ScanRootState st, ScanSegment seg, bool force, int thumbWidth, int thumbQuality, ScanResult result, Action<string> log, List<ScanFile> files, Store store, Dictionary<string, Dictionary<string, ScanStepRow>> steps, ScanResult progress = null, bool trace = false)
        {
            int i = 0;
            int batch = 0;
            int total = st.Files.Count;
            bool parallel = progress != null;
            store.Begin();
            try
            {
                foreach (ScanFile f in files)
                {
                    i = i + 1;
                    result.StepDone = result.StepDone + 1;
                    int done = result.StepDone;
                    if (progress != null)
                    {
                        // 并行进度——全局共享计数（Interlocked）；面板 / CLI 按「已完成 / 总数」显示
                        done = System.Threading.Interlocked.Increment(ref progress.SharedDone);
                        progress.StepDone = done;
                    }
                    if (log != null && done % ProgressEvery == 0)
                    {
                        // 出声条件挂在「全局已完成数」上——每分片文件少时（30 分片 × 31 张）也能看见推进
                        log("  " + seg.Label + " " + done + "/" + total);
                    }
                    CardFileSession session = null;
                    if (seg.Tier != ScanReadTier.None)
                    {
                        session = CardFileSession.Open(f.Path);
                    }
                    try
                    {
                        foreach (ScanStepDef step in seg.Steps)
                        {
                            if (!force && StepDoneIn(steps, f, step.Id))
                            {
                                result.Skipped = result.Skipped + 1;
                                continue;
                            }
                            if (parallel && trace && log != null)
                            {
                                // 逐文件诊断出声——卡住时最后一行即「哪个分片 / 哪张卡 / 哪一步」
                                log("  [" + Path.GetFileNameWithoutExtension(store.DbPath) + "] " + step.Id + " " + Path.GetFileName(f.Path));
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
                }
                store.Commit();
            }
            catch
            {
                store.Rollback();
                throw;
            }
        }
        // [段2b] 先导段（人物卡主库）

        /// <summary>先导段步骤名（面板进度行显示）。</summary>
        public const string PreludeStepName = "人物卡主库先导（只建行 · 不含子文件夹）";

        /// <summary>取人物卡主库那条库根状态（按推荐子路径后缀认定；找不到返回 null）。</summary>
        private static ScanRootState FindFemaleRoot(List<ScanRootState> states)
        {
            string suffix = RootsRules.CardFemaleSub.Replace('/', '\\');
            foreach (ScanRootState st in states)
            {
                if (st.Entry == null || st.Entry.tier != Tier.Main)
                {
                    continue;
                }
                string path = (st.Entry.path ?? "").TrimEnd('\\', '/');
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return st;
                }
            }
            return null;
        }

        /// <summary>先导段排序——修改时间倒序（面板「按游戏内顺序」即此序）；同刻按路径。</summary>
        private static int CompareByMtimeDesc(ScanFile a, ScanFile b)
        {
            int c = string.CompareOrdinal(b.Mtime ?? "", a.Mtime ?? "");
            if (c != 0)
            {
                return c;
            }
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>先导段文件清单——只取该库根**本目录**下的文件（不含子文件夹）：f.Folder 为空即本目录（根目录）。</summary>
        private static List<ScanFile> PreludeFiles(ScanRootState st)
        {
            List<ScanFile> list = new List<ScanFile>();
            foreach (ScanFile f in st.Files)
            {
                if (string.IsNullOrEmpty(f.Folder))
                {
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>
        /// 先导段——人物卡主库本目录（不含子文件夹）先只建「卡片行」，按修改时间倒序、小批量提交。
        /// 只建行、不做任何解析（不读头段、不出缩略图）——面板先能看见文件名与卡片原图（原图由面板直接贴源文件）；
        /// 其余解析照旧走后面的步骤；行按路径哈希落各自分片库（与常规段同一路由），不新建第二份记录。
        /// </summary>
        private static void RunPrelude(StoreHub hub, ScanRootState st, List<ScanFile> files, bool force, ScanResult result, Action<string> log)
        {
            files.Sort(CompareByMtimeDesc);
            // 分片数按**整库**文件数推导（与常规段同源）——先导只跑其中一部分，不能拿它把分片数缩小
            EnsureShards(hub, st, st.Files.Count, log);
            List<Store> shards = st.Shards;
            List<Dictionary<string, Dictionary<string, ScanStepRow>>> steps = st.ShardSteps;
            ScanStepDef rowDef = ScanPlanCatalog.Find("row");
            int total = files.Count;
            int done = 0;
            int[] batch = new int[shards.Count];
            for (int k = 0; k < shards.Count; k = k + 1)
            {
                shards[k].Begin();
            }
            try
            {
                foreach (ScanFile f in files)
                {
                    done = done + 1;
                    result.StepDone = done;
                    if (log != null && done % ProgressEvery == 0)
                    {
                        log("  " + PreludeStepName + " " + done + "/" + total);
                    }
                    int k = StoreHub.ShardOfPath(f.Path, shards.Count);
                    Store store = shards[k];
                    RunPreludeStep(store, st.Entry, f, rowDef, force, steps[k], result);
                    batch[k] = batch[k] + 1;
                    if (batch[k] >= PreludeBatchSize)
                    {
                        store.Commit();
                        store.Begin();
                        batch[k] = 0;
                    }
                }
                for (int k = 0; k < shards.Count; k = k + 1)
                {
                    shards[k].Commit();
                }
            }
            catch
            {
                for (int k = 0; k < shards.Count; k = k + 1)
                {
                    shards[k].Rollback();
                }
                throw;
            }
        }

        /// <summary>先导段单步——判步跳过 / 执行 / 落完成戳 / 计数（与常规段同一套语义，只是批次更小）。</summary>
        private static void RunPreludeStep(Store store, RootEntry root, ScanFile f, ScanStepDef step, bool force, Dictionary<string, Dictionary<string, ScanStepRow>> index, ScanResult result)
        {
            if (step == null)
            {
                return;
            }
            if (!force && StepDoneIn(index, f, step.Id))
            {
                result.Skipped = result.Skipped + 1;
                return;
            }
            if (RunCardStep(store, root, f, null, step, 0, 0, result))
            {
                store.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime);
                result.Added = result.Added + 1;
                return;
            }
            result.Failed = result.Failed + 1;
        }

        /// <summary>准备分片库——片数首次按「分片目标」定下并落设置（受每片至少 MinFilesPerWorker 约束裁剪），此后稳定不变；缺片则用「模板建一次 + 文件复制」补齐；分片库**常驻**（不合并、不删），各片各读自己的完成戳索引。</summary>
        private static void EnsureShards(StoreHub hub, ScanRootState st, int total, Action<string> log = null)
        {
            int baseLib = hub.CardBaseOf(st.Entry);
            if (baseLib <= 0)
            {
                return;
            }
            // [段1] 片数——首次按「分片目标」定下并落盘；此后稳定不变（固定片数，不随文件数变化）。
            // 🔴 片数与并发度解耦（布局稳定，并发度随时可调）；变更片数会让已落库的行留在错片（行按路径哈希落片）⇒ 改数需整库重扫
            int shards = hub.ShardCountOf(baseLib);
            if (shards <= 0)
            {
                shards = hub.ShardTarget();
                if (shards < 1)
                {
                    shards = 1;
                }
                hub.SetShardCount(baseLib, shards);
                if (log != null && shards > 1)
                {
                    log("  [分片] 本库根分片数定为 " + shards + "（文件 " + total + " 个）——此后稳定不变");
                }
            }
            if (st.Shards != null && st.Shards.Count == shards)
            {
                // 分片库跨段常驻——但完成戳索引必须每段重读（上一段刚写的戳要能被本段看见）
                st.ShardSteps = new List<Dictionary<string, Dictionary<string, ScanStepRow>>>();
                foreach (Store s in st.Shards)
                {
                    st.ShardSteps.Add(s.LoadScanSteps());
                }
                return;
            }
            List<int> libs = hub.LibsOfBase(baseLib);
            Stopwatch watch = Stopwatch.StartNew();
            // [段2] 首建——模板建一次（一次建表事务）→ WAL 归位 → 文件复制，省掉每片各跑一遍 DDL
            string dir = Path.GetDirectoryName(st.Store.DbPath);
            string tpl = Path.Combine(dir, "lib_" + baseLib + "_tpl.db");
            bool anyNew = false;
            foreach (int lib in libs)
            {
                string p = Path.Combine(dir, RootsRules.LibDbFileName(lib));
                if (!File.Exists(p))
                {
                    anyNew = true;
                    break;
                }
            }
            long built = 0;
            if (anyNew)
            {
                DeleteShardFiles(tpl, log);
                using (Store tplStore = new Store(tpl))
                {
                    tplStore.Checkpoint();
                }
                built = watch.ElapsedMilliseconds;
                foreach (int lib in libs)
                {
                    string p = Path.Combine(dir, RootsRules.LibDbFileName(lib));
                    if (File.Exists(p))
                    {
                        continue;
                    }
                    DeleteShardFiles(p, log);
                    File.Copy(tpl, p, true);
                }
                DeleteShardFiles(tpl, log);
            }
            // [段3] 打开分片库——结构已就绪，跳过建表；分片库**常驻**（不合并、不删）
            List<Store> list = hub.OpenShardStores(baseLib, shards);
            List<Dictionary<string, Dictionary<string, ScanStepRow>>> steps =
                new List<Dictionary<string, Dictionary<string, ScanStepRow>>>();
            foreach (Store s in list)
            {
                steps.Add(s.LoadScanSteps());
            }
            st.Shards = list;
            st.ShardSteps = steps;
            if (log != null && anyNew)
            {
                log("  [分片] 首建 " + list.Count + " 个分片库：模板 " + built + " ms · 建齐 " + watch.ElapsedMilliseconds + " ms（此后常驻，不再合并）");
            }
        }
        /// <summary>删除分片库的临时中间文件（模板）——分片库本身常驻，不在此列；删除失败不阻断扫描，但出声（失败必须可见）。</summary>
        private static void DeleteShardFiles(string path, Action<string> log = null)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                // 删除失败不阻断扫描——分片库是临时中间产物，下次扫描会覆盖重建
                if (log != null)
                {
                    log("  临时文件删除失败（可忽略）：" + path + " · " + ex.Message);
                }
            }
            try
            {
                File.Delete(path + "-wal");
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("  临时文件删除失败（可忽略）：" + path + "-wal · " + ex.Message);
                }
            }
            try
            {
                File.Delete(path + "-shm");
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("  临时文件删除失败（可忽略）：" + path + "-shm · " + ex.Message);
                }
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
                    if (log != null && i % ModProgressEvery == 0)
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

        /// <summary>卡片侧清扫——删除磁盘上已消失的卡片行与步骤记录（分片常驻后逐片清扫）。</summary>
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
            List<Store> targets = new List<Store>();
            if (st.Shards != null && st.Shards.Count > 0)
            {
                targets.AddRange(st.Shards);
            }
            else
            {
                targets.Add(st.Store);
            }
            int goneCards = 0;
            int goneSteps = 0;
            int goneNon = 0;
            foreach (Store s in targets)
            {
                s.Begin();
                try
                {
                    goneCards = goneCards + s.DeleteCardsMissingUnderRoot(st.Entry.path, present);
                    goneSteps = goneSteps + s.DeleteScanStepsMissingUnderRoot(st.Entry.path, present);
                    goneNon = goneNon + s.DeleteNonCardsMissingUnderRoot(st.Entry.path);
                    s.Commit();
                }
                catch
                {
                    s.Rollback();
                    throw;
                }
            }
            if (goneCards > 0 || goneSteps > 0 || goneNon > 0)
            {
                result.Removed += goneCards;
                if (log != null)
                {
                    log("  已清理 " + goneCards + " 条已消失的卡片记录 · 步骤记录 " + goneSteps + " 条 · 非卡登记 " + goneNon + " 条");
                }
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

        /// <summary>该文件该步骤是否已完成（给定完成戳索引版）——size + mtime 双等且步骤记录在案。</summary>
        private static bool StepDoneIn(Dictionary<string, Dictionary<string, ScanStepRow>> index, ScanFile f, string stepId)
        {
            if (index == null)
            {
                return false;
            }
            Dictionary<string, ScanStepRow> steps;
            if (!index.TryGetValue(f.Path, out steps))
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

        /// <summary>该文件该步骤是否已完成——size + mtime 双等且步骤记录在案。</summary>
        private static bool StepDone(ScanRootState st, ScanFile f, string stepId)
        {
            return StepDoneIn(st.Steps, f, stepId);
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
        /// <summary>插件库根下的 dll 总数（进度分母——枚举失败按已得计数，失败原因并入扫描错误清单出声）。</summary>
        private static int CountPluginDlls(RootsConfig cfg, ScanResult result)
        {
            int n = 0;
            if (cfg == null || cfg.pluginRoots == null)
            {
                return 0;
            }
            foreach (RootEntry root in cfg.pluginRoots)
            {
                if (string.IsNullOrWhiteSpace(root.path) || !Directory.Exists(root.path))
                {
                    continue;
                }
                try
                {
                    SearchOption option = root.recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    n = n + Directory.GetFiles(root.path, "*.dll", option).Length;
                }
                catch (Exception ex)
                {
                    AddError(result, "插件库枚举失败（" + root.path + "）：" + ex.Message);
                }
            }
            return n;
        }

        /// <summary>扫描插件库根下的全部 dll 与配置文件并落库（只读解析，不改动任何文件）。</summary>
        /// <param name="store">主库（插件库数据落主库）。</param>
        /// <param name="cfg">库根配置。</param>
        /// <param name="errors">解析失败 / 枚举失败清单（出声用）。</param>
        /// <param name="log">日志出声——进度行「插件库 done/total」由面板进度条解析；可为 null。</param>
        /// <returns>本次扫描的计数（dll / 插件项 / 配置文件）。</returns>
        public static PluginScanResult ScanPlugins(Store store, RootsConfig cfg, List<string> errors, Action<string> log)
        {
            PluginScanResult scan = new PluginScanResult();
            List<PluginRow> old = store.LoadPlugins();
            foreach (RootEntry root in cfg.pluginRoots)
            {
                if (string.IsNullOrWhiteSpace(root.path) || !Directory.Exists(root.path))
                {
                    errors.Add("插件库根不存在：" + root.path);
                    continue;
                }
                if (log != null)
                {
                    log("插件库 · " + root.path);
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
                scan.Dlls = scan.Dlls + files.Count;
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
                int done = 0;
                Parallel.ForEach(files, file =>
                {
                    int seen = System.Threading.Interlocked.Increment(ref done);
                    if (log != null && (seen % PluginProgressEvery == 0 || seen == files.Count))
                    {
                        log("  插件库 " + seen + "/" + files.Count);
                    }
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
                    PluginReader.AssemblyMeta meta;
                    List<PluginInfo> plugins = PluginReader.ReadDll(file, out meta, out error);
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
                            bad.Note = error;
                            bad.Title = meta.title;
                            bad.Description = meta.description;
                            bad.Company = meta.company;
                            bad.Copyright = meta.copyright;
                            bad.Product = meta.product;
                            bad.FileVersion = meta.fileVersion;
                            bad.TargetFramework = meta.targetFramework;
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
                                row.Title = info.title;
                                row.Description = info.description;
                                row.Company = info.company;
                                row.Copyright = info.copyright;
                                row.Product = info.product;
                                row.FileVersion = info.fileVersion;
                                row.TargetFramework = info.targetFramework;
                                store.SavePlugin(row);
                            }
                        }
                    }
                });
                // [段4] 配置文件全量读取——config/*.cfg 逐个解析（分节 / 选项 / 作者注释）落库；
                // cfg 数量有限（本机 185 个），整段重读不留增量判据——改了立刻反映，不会读到半新半旧
                store.DeletePluginConfigsUnderRoot(root.path);
                List<string> cfgErrors = new List<string>();
                List<PluginConfigFile> cfgs = PluginConfigReader.ReadAll(root.path, cfgErrors);
                foreach (string e in cfgErrors)
                {
                    errors.Add(e);
                }
                foreach (PluginConfigFile cfgFile in cfgs)
                {
                    PluginConfigRow row = new PluginConfigRow();
                    row.FilePath = cfgFile.filePath;
                    row.FileName = cfgFile.fileName;
                    row.PluginName = cfgFile.pluginName;
                    row.PluginVersion = cfgFile.pluginVersion;
                    row.Guid = cfgFile.guid;
                    row.Size = cfgFile.size;
                    row.Mtime = cfgFile.mtime;
                    row.Sections = PluginConfigReader.ToJson(cfgFile);
                    long sections = 0;
                    long options = 0;
                    foreach (PluginConfigSection s in cfgFile.sections)
                    {
                        sections = sections + 1;
                        options = options + s.options.Count;
                    }
                    row.SectionCount = sections;
                    row.OptionCount = options;
                    row.Error = cfgFile.error;
                    store.SavePluginConfig(row);
                }
                scan.Configs = scan.Configs + cfgs.Count;
            }
            List<PluginRow> rows = store.LoadPlugins();
            foreach (PluginRow row in rows)
            {
                if (row.Guid.Length > 0)
                {
                    scan.Plugins = scan.Plugins + 1;
                }
            }
            return scan;
        }
    }
}
