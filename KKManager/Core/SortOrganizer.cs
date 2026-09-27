using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>按作者整理任务的状态（生成 / 执行共用）——面板与 CLI 读它展示进度。</summary>
    public class SortJobState
    {
        /// <summary>是否运行中。</summary>
        public bool Running { get; set; }

        /// <summary>是否已请求停止（协作式，循环里检查）。</summary>
        public bool StopRequested { get; set; }

        /// <summary>阶段（生成计划 / 搬运）。</summary>
        public string Phase { get; set; } = "";

        /// <summary>消息（准备 / 进行中 / 完成 / 拒绝原因）。</summary>
        public string Message { get; set; } = "";

        /// <summary>条目总数。</summary>
        public long Total { get; set; }

        /// <summary>已处理。</summary>
        public long Done { get; set; }

        /// <summary>已就位 / 已搬。</summary>
        public long Moved { get; set; }

        /// <summary>跳过。</summary>
        public long Skipped { get; set; }

        /// <summary>失败。</summary>
        public long Failed { get; set; }

        /// <summary>冲突数（生成阶段统计；执行阶段累计）。</summary>
        public long Conflicts { get; set; }

        /// <summary>当前处理的文件。</summary>
        public string Current { get; set; } = "";

        /// <summary>开始时刻。</summary>
        public string StartedAt { get; set; } = "";

        /// <summary>结束时刻。</summary>
        public string FinishedAt { get; set; } = "";

        /// <summary>失败明细（≤50 条）。</summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>本轮计划 id。</summary>
        public long PlanId { get; set; }

        /// <summary>执行末尾清掉的空目录数。</summary>
        public long PrunedDirs { get; set; }
    }

    /// <summary>按作者整理——计划生成（平铺 + 作者文件夹 + 冲突检测）与执行（同库改路径 + 清空目录）的唯一实现，面板与 CLI 共用。</summary>
    public static class SortOrganizer
    {
        /// <summary>未标注作者的 mod 归入的专用文件夹名（软件理解的归类，不是作者名）。</summary>
        public const string UnknownFolder = "Unknown";

        /// <summary>条目状态——待搬。</summary>
        public const string StatePending = "pending";

        /// <summary>条目状态——冲突（目标已存在 / 计划内撞车），必须解决才能执行。</summary>
        public const string StateConflict = "conflict";

        /// <summary>条目状态——已就位（快照位置既有文件，√）。</summary>
        public const string StateMoved = "moved";

        /// <summary>条目状态——失败（原因在备注）。</summary>
        public const string StateFailed = "failed";

        /// <summary>条目状态——跳过（源不在 / 库根只读）。</summary>
        public const string StateSkipped = "skipped";

        /// <summary>作者名 → 文件夹名——空作者归 Unknown；Windows 非法字符换下划线；去尾部点与空格；超长截断；保留设备名加前缀。</summary>
        public static string FolderOfAuthor(string author)
        {
            if (string.IsNullOrWhiteSpace(author))
            {
                return UnknownFolder;
            }
            string src = author.Trim();
            StringBuilder sb = new StringBuilder(src.Length);
            foreach (char c in src)
            {
                if (c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            string name = sb.ToString().Trim().TrimEnd('.', ' ');
            if (name.Length == 0)
            {
                return UnknownFolder;
            }
            if (name.Length > 80)
            {
                name = name.Substring(0, 80).TrimEnd('.', ' ');
            }
            if (IsReservedName(name))
            {
                name = "_" + name;
            }
            return name;
        }

        /// <summary>Windows 保留设备名（建目录必定失败——先加前缀避坑）。</summary>
        private static bool IsReservedName(string name)
        {
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
            {
                return true;
            }
            if (upper.Length == 4)
            {
                if (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                {
                    if (upper[3] >= '1' && upper[3] <= '9')
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>生成平铺计划——枚举所选库根下全部 mod 文件，算作者文件夹与目标路径，检出冲突后落主库表（计划头 + 条目）。</summary>
        public static void BuildPlan(StoreHub hub, RootsConfig cfg, List<string> rootPaths, SortJobState job)
        {
            job.Phase = "生成计划";
            job.Message = "读取库文件清单";
            long planId = hub.Core.AddSortPlan(string.Join("\n", rootPaths));
            job.PlanId = planId;
            Dictionary<string, string> authors = hub.Core.ModAuthorMap();

            List<SortPlanItemRow> rows = new List<SortPlanItemRow>();
            foreach (string root in rootPaths)
            {
                if (job.StopRequested)
                {
                    break;
                }
                int lib = hub.LibOfRootPath(cfg, root);
                List<ModFileRecord> files = hub.StoreByLib(lib).QueryModFilesByRoot(root);
                foreach (ModFileRecord f in files)
                {
                    string author = "";
                    authors.TryGetValue(f.Guid ?? "", out author);
                    SortPlanItemRow row = new SortPlanItemRow();
                    row.PlanId = planId;
                    row.Lib = lib;
                    row.Tier = f.Tier;
                    row.RootPath = root;
                    row.Guid = f.Guid;
                    row.Author = author == null ? "" : author;
                    row.Folder = FolderOfAuthor(author);
                    row.SrcPath = f.FilePath;
                    row.DestPath = Path.Combine(root, row.Folder, Path.GetFileName(f.FilePath));
                    row.Size = f.Size;
                    row.Mtime = f.Mtime;
                    row.State = StatePending;
                    row.Note = "";
                    rows.Add(row);
                }
                job.Total = rows.Count;
                job.Message = "已读 " + rows.Count.ToString() + " 条";
            }

            job.Message = "检测冲突";
            long conflicts = DetectConflicts(rows);

            long seq = 0;
            hub.Core.Begin();
            try
            {
                foreach (SortPlanItemRow row in rows)
                {
                    row.Seq = seq;
                    seq = seq + 1;
                    WriteRow(hub, planId, row);
                    if (seq % 2000 == 0)
                    {
                        hub.Core.Commit();
                        hub.Core.Begin();
                        job.Message = "写入计划 " + seq.ToString() + " / " + rows.Count.ToString();
                        if (job.StopRequested)
                        {
                            break;
                        }
                    }
                }
                hub.Core.Commit();
            }
            catch (Exception)
            {
                hub.Core.Rollback();
                throw;
            }

            if (job.StopRequested)
            {
                hub.Core.FinishSortPlan(planId, "outdated", seq, conflicts, "生成被停止");
                job.Message = "生成已停止";
                return;
            }
            hub.Core.FinishSortPlan(planId, "ready", rows.Count, conflicts, "");
            job.Conflicts = conflicts;
            job.Message = conflicts > 0
                ? "计划完成——" + conflicts.ToString() + " 条冲突待解决"
                : "计划完成——无冲突";
        }

        /// <summary>把一条条目写进计划表（seq 已在调用方补齐）。</summary>
        private static void WriteRow(StoreHub hub, long planId, SortPlanItemRow row)
        {
            row.PlanId = planId;
            hub.Core.AddSortPlanItem(row);
        }

        /// <summary>冲突检测——目标已存在同名文件 · 计划内同目标路径撞车 · 已在目标位置（标 √）；返回冲突条数。</summary>
        private static long DetectConflicts(List<SortPlanItemRow> rows)
        {
            long conflicts = 0;
            Dictionary<string, int> destCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> dirCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SortPlanItemRow row in rows)
            {
                if (string.Equals(row.SrcPath, row.DestPath, StringComparison.OrdinalIgnoreCase))
                {
                    row.State = StateMoved;
                    row.Note = "已在目标位置";
                    continue;
                }
                if (!FileAt(row.SrcPath, dirCache))
                {
                    row.State = StateSkipped;
                    row.Note = "源文件不存在";
                    continue;
                }
                string dir = Path.GetDirectoryName(row.DestPath);
                HashSet<string> present = DirFileNames(dir, dirCache);
                string name = Path.GetFileName(row.DestPath);
                if (present.Contains(name))
                {
                    row.State = StateConflict;
                    row.Note = "目标已存在同名文件";
                    conflicts = conflicts + 1;
                    continue;
                }
                int n = 0;
                destCount.TryGetValue(row.DestPath, out n);
                destCount[row.DestPath] = n + 1;
            }
            foreach (SortPlanItemRow row in rows)
            {
                if (row.State != StatePending)
                {
                    continue;
                }
                int n = 0;
                destCount.TryGetValue(row.DestPath, out n);
                if (n > 1)
                {
                    row.State = StateConflict;
                    row.Note = "计划内目标路径撞车（" + n.ToString() + " 份同名）";
                    conflicts = conflicts + 1;
                }
            }
            return conflicts;
        }
        /// <summary>目标文件夹内的文件名集合（按目录缓存——目录不存在返回空集合，避免逐条 stat 目标路径）。</summary>
        private static HashSet<string> DirFileNames(string dir, Dictionary<string, HashSet<string>> cache)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            HashSet<string> set = null;
            if (cache.TryGetValue(dir, out set))
            {
                return set;
            }
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir);
                    foreach (string f in files)
                    {
                        set.Add(Path.GetFileName(f));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[整理] 枚举目标目录失败 " + dir + "：" + ex.Message);
            }
            cache[dir] = set;
            return set;
        }
        /// <summary>某路径的文件当前是否存在（按所在目录缓存枚举——批量核对时避免逐条 stat）。</summary>
        private static bool FileAt(string path, Dictionary<string, HashSet<string>> cache)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            return DirFileNames(Path.GetDirectoryName(path), cache).Contains(Path.GetFileName(path));
        }

        /// <summary>执行计划（只搬待办条目）——逐条核对快照位置后同库改路径，末尾清理空目录；中断安全（每条状态即时落表）。</summary>
        public static void ExecutePlan(StoreHub hub, RootsConfig cfg, long planId, SortJobState job)
        {
            job.Phase = "搬运";
            job.Message = "开始搬运";
            List<SortPlanItemRow> rows = hub.Core.QuerySortPlanItems(planId);
            job.Total = rows.Count;
            HashSet<string> touchedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SortPlanItemRow row in rows)
            {
                if (job.StopRequested)
                {
                    break;
                }
                if (row.State != StatePending)
                {
                    // 已就位 / 已失败 / 已跳过的行不再重做（中断后重入时天然续传）
                    touchedRoots.Add(row.RootPath ?? "");
                    continue;
                }
                job.Current = row.SrcPath;
                touchedRoots.Add(row.RootPath ?? "");

                if (IsPlaced(row))
                {
                    SetState(hub, job, planId, row, StateMoved, "已在快照位置");
                    continue;
                }
                if (File.Exists(row.DestPath))
                {
                    SetState(hub, job, planId, row, StateConflict, "目标已存在同名文件");
                    continue;
                }
                if (!File.Exists(row.SrcPath))
                {
                    SetState(hub, job, planId, row, StateSkipped, "源文件不存在");
                    continue;
                }
                if (RootsRules.IsReadOnlyRoot(cfg, row.RootPath))
                {
                    SetState(hub, job, planId, row, StateSkipped, "库根只读，不搬运");
                    continue;
                }

                string error = MoveOne(hub, cfg, row);
                if (error == null)
                {
                    SetState(hub, job, planId, row, StateMoved, "");
                }
                else
                {
                    SetState(hub, job, planId, row, StateFailed, error);
                }
            }

            bool stopped = job.StopRequested;
            if (!stopped)
            {
                foreach (string root in touchedRoots)
                {
                    if (string.IsNullOrEmpty(root) || RootsRules.IsReadOnlyRoot(cfg, root))
                    {
                        continue;
                    }
                    long n = PruneEmptyDirs(root);
                    if (n > 0)
                    {
                        Console.WriteLine("[整理] 清掉空目录 " + n.ToString() + " 个：" + root);
                    }
                    job.PrunedDirs = job.PrunedDirs + n;
                }
                job.Message = "搬运完成";
            }
            else
            {
                job.Message = "已停止（已完成的行已落表，下次打开按快照核对续传）";
            }
        }

        /// <summary>写一条条目的状态并同步进度计数（失败明细 ≤50 条）。</summary>
        private static void SetState(StoreHub hub, SortJobState job, long planId, SortPlanItemRow row, string state, string note)
        {
            hub.Core.SetSortItemState(planId, row.Seq, state, note);
            job.Done = job.Done + 1;
            if (state == StateMoved)
            {
                job.Moved = job.Moved + 1;
            }
            else if (state == StateSkipped)
            {
                job.Skipped = job.Skipped + 1;
            }
            else if (state == StateConflict)
            {
                job.Conflicts = job.Conflicts + 1;
            }
            else if (state == StateFailed)
            {
                job.Failed = job.Failed + 1;
                if (job.Errors.Count < 50)
                {
                    job.Errors.Add(row.SrcPath + "：" + note);
                }
            }
        }

        /// <summary>快照位置判据——目标已在、源不在（或源就是目标）= 这份已经就位（下次打开按此标 √）。</summary>
        private static bool IsPlaced(SortPlanItemRow row)
        {
            if (string.Equals(row.SrcPath, row.DestPath, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(row.DestPath);
            }
            return File.Exists(row.DestPath) && !File.Exists(row.SrcPath);
        }

        /// <summary>搬一条——建目标目录 → 改名（同库内移动）→ 库同步；成功返回 null，失败返回原因（文件动了而数据库没找到源行也按失败上报，不静默）。</summary>
        private static string MoveOne(StoreHub hub, RootsConfig cfg, SortPlanItemRow row)
        {
            try
            {
                string dir = Path.GetDirectoryName(row.DestPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.Move(row.SrcPath, row.DestPath);
            }
            catch (Exception ex)
            {
                return "文件移动失败" + StoreHub.FileBusyHint(ex);
            }
            try
            {
                if (hub.RenameModFilePath(cfg, row.Guid, row.SrcPath, row.RootPath, row.DestPath) == null)
                {
                    return "已移动但数据库未找到源副本行：" + row.SrcPath + "（需重扫核对）";
                }
            }
            catch (Exception ex)
            {
                return "文件已移动但数据库更新失败：" + ex.GetType().Name + " " + ex.Message;
            }
            return null;
        }

        /// <summary>删掉某库根下的空目录（自下而上逐级判断；库根本身不动）；返回清掉的目录数。</summary>
        public static long PruneEmptyDirs(string root)
        {
            long removed = 0;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return 0;
            }
            string[] subs;
            try
            {
                subs = Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[整理] 枚举子目录失败 " + root + "：" + ex.Message);
                return 0;
            }
            foreach (string sub in subs)
            {
                removed = removed + PruneEmptyDirs(sub);
            }
            try
            {
                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, false);
                    removed = removed + 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[整理] 删除空目录失败 " + root + "：" + ex.Message);
            }
            return removed;
        }

        /// <summary>核对计划条目在文件系统上的实况——把已就位的行改标 √（moved），返回该计划的冲突数现状。</summary>
        public static long RecheckPlan(StoreHub hub, long planId)
        {
            long conflicts = 0;
            List<SortPlanItemRow> rows = hub.Core.QuerySortPlanItems(planId);
            Dictionary<string, HashSet<string>> dirCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> destCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SortPlanItemRow row in rows)
            {
                if (string.Equals(row.SrcPath, row.DestPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!FileAt(row.SrcPath, dirCache))
                {
                    continue;
                }
                int c = 0;
                destCount.TryGetValue(row.DestPath, out c);
                destCount[row.DestPath] = c + 1;
            }
            foreach (SortPlanItemRow row in rows)
            {
                if (row.State == StateMoved || row.State == StateSkipped)
                {
                    continue;
                }
                string state = StatePending;
                string note = "";
                if (string.Equals(row.SrcPath, row.DestPath, StringComparison.OrdinalIgnoreCase))
                {
                    state = StateMoved;
                    note = "已在快照位置";
                    if (!FileAt(row.SrcPath, dirCache))
                    {
                        state = StateSkipped;
                        note = "源文件不存在";
                    }
                }
                else
                {
                    bool destHere = FileAt(row.DestPath, dirCache);
                    bool srcHere = FileAt(row.SrcPath, dirCache);
                    int c = 0;
                    destCount.TryGetValue(row.DestPath, out c);
                    if (destHere && !srcHere)
                    {
                        state = StateMoved;
                        note = "已在快照位置";
                    }
                    else if (destHere)
                    {
                        state = StateConflict;
                        note = "目标已存在同名文件";
                    }
                    else if (!srcHere)
                    {
                        state = StateSkipped;
                        note = "源文件不存在";
                    }
                    else if (c > 1)
                    {
                        state = StateConflict;
                        note = "计划内目标路径撞车（" + c.ToString() + " 份同名）";
                    }
                }
                if (state == StateConflict)
                {
                    conflicts = conflicts + 1;
                }
                if (state != row.State || note != (row.Note == null ? "" : row.Note))
                {
                    hub.Core.SetSortItemState(planId, row.Seq, state, note);
                }
            }
            return conflicts;
        }

        /// <summary>计划表里某状态的条目数（冲突 / 待搬等的快速统计）。</summary>
        public static long CountState(StoreHub hub, long planId, string state)
        {
            return hub.Core.CountSortPlanItems(planId, state);
        }
    }
}
