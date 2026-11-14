// 「按作者整理」——计划生成（平铺 + 作者文件夹 + 冲突检测）与执行（同库改路径 + 清空目录）。
//
// 契约对齐 C# Core/SortOrganizer.cs：面板与 CLI 共用同一实现，状态字符串 / 文件夹命名 /
// 冲突判定口径逐条对齐。表操作在 internal/store/sortplan.go。
package core

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"kkgo/internal/store"
)

// UnknownFolder 未标注作者的 mod 归入的专用文件夹名（软件理解的归类，不是作者名）。
const UnknownFolder = "Unknown"

// 条目状态常量（与 C# SortOrganizer 同名同值）。
const (
	// SortStatePending 待搬。
	SortStatePending = "pending"
	// SortStateConflict 冲突（目标已存在 / 计划内撞车），必须解决才能执行。
	SortStateConflict = "conflict"
	// SortStateMoved 已就位（快照位置既有文件，√）。
	SortStateMoved = "moved"
	// SortStateFailed 失败（原因在备注）。
	SortStateFailed = "failed"
	// SortStateSkipped 跳过（源不在 / 库根只读）。
	SortStateSkipped = "skipped"
)

// SortJobState 按作者整理任务的状态（生成 / 执行共用）——面板与 CLI 读它展示进度。
type SortJobState struct {
	Running       bool
	StopRequested bool
	Phase         string
	Message       string
	Total         int64
	Done          int64
	Moved         int64
	Skipped       int64
	Failed        int64
	Conflicts     int64
	Current       string
	StartedAt     string
	FinishedAt    string
	Errors        []string
	PlanID        int64
	PrunedDirs    int64
}

// SortConflictCandidate 整理冲突里的一份候选副本（只带计划侧事实）。
type SortConflictCandidate struct {
	Seq      int64
	Tier     int
	RootPath string
	FilePath string
	FileName string
	Size     int64
	Mtime    string
	State    string
	Note     string
}

// SortConflictGroup 撞到同一目标路径的一组候选副本。
type SortConflictGroup struct {
	Guid     string
	DestPath string
	Items    []SortConflictCandidate
}

// FolderOfAuthor 作者名 → 文件夹名：空作者归 Unknown；Windows 非法字符换下划线；
// 去尾部点与空格；超长截断；保留设备名加前缀。
func FolderOfAuthor(author string) string {
	if strings.TrimSpace(author) == "" {
		return UnknownFolder
	}
	var b strings.Builder
	for _, c := range strings.TrimSpace(author) {
		if c < 32 || strings.ContainsRune(`\/:*?"<>|`, c) {
			b.WriteRune('_')
		} else {
			b.WriteRune(c)
		}
	}
	name := strings.TrimRight(strings.TrimSpace(b.String()), ". ")
	if name == "" {
		return UnknownFolder
	}
	if len(name) > 80 {
		name = strings.TrimRight(name[:80], ". ")
	}
	if isReservedName(name) {
		name = "_" + name
	}
	return name
}

// isReservedName Windows 保留设备名（建目录必定失败——先加前缀避坑）。
func isReservedName(name string) bool {
	upper := strings.ToUpper(name)
	switch upper {
	case "CON", "PRN", "AUX", "NUL":
		return true
	}
	if len(upper) == 4 {
		if strings.HasPrefix(upper, "COM") || strings.HasPrefix(upper, "LPT") {
			if upper[3] >= '1' && upper[3] <= '9' {
				return true
			}
		}
	}
	return false
}

// storeOfLib 取某库序号的库句柄（lib <= 0 = 主库）。
func storeOfLib(hub *store.Hub, lib int) (*store.DB, error) {
	if lib <= 0 {
		return hub.Core(), nil
	}
	return hub.StoreByLib(lib)
}

// BuildPlan 生成平铺计划——枚举所选库根下全部 mod 文件，算作者文件夹与目标路径，
// 检出冲突后落主库表（计划头 + 条目）。
func BuildPlan(hub *store.Hub, cfg *RootsConfig, rootPaths []string, job *SortJobState) error {
	job.Phase = "生成计划"
	job.Message = "读取库文件清单"
	planID, err := hub.Core().AddSortPlan(strings.Join(rootPaths, "\n"))
	if err != nil {
		return err
	}
	job.PlanID = planID
	authors, err := hub.Core().ModAuthorMap()
	if err != nil {
		return err
	}

	var rows []store.SortPlanItemRow
	for _, root := range rootPaths {
		if job.StopRequested {
			break
		}
		lib, err := hub.LibOf(root)
		if err != nil {
			return err
		}
		db, err := storeOfLib(hub, lib)
		if err != nil {
			return err
		}
		files, err := db.QueryModFileRecsByRoot(root)
		if err != nil {
			return err
		}
		for _, f := range files {
			author := authors[f.Guid]
			rows = append(rows, store.SortPlanItemRow{
				PlanID:   planID,
				Lib:      lib,
				Tier:     f.Tier,
				RootPath: root,
				Guid:     f.Guid,
				Author:   author,
				Folder:   FolderOfAuthor(author),
				SrcPath:  f.FilePath,
				DestPath: filepath.Join(root, FolderOfAuthor(author), filepath.Base(f.FilePath)),
				Size:     f.Size,
				Mtime:    f.Mtime,
				State:    SortStatePending,
			})
		}
		job.Total = int64(len(rows))
		job.Message = "已读 " + itoaSmall(len(rows)) + " 条"
	}

	job.Message = "检测冲突"
	conflicts := detectConflicts(rows)

	for i := range rows {
		rows[i].Seq = int64(i)
	}
	if err := hub.Core().Begin(); err != nil {
		return err
	}
	for i := range rows {
		if err := hub.Core().AddSortPlanItem(&rows[i]); err != nil {
			hub.Core().Rollback()
			return err
		}
		if (i+1)%2000 == 0 {
			if err := hub.Core().Commit(); err != nil {
				return err
			}
			if err := hub.Core().Begin(); err != nil {
				return err
			}
			job.Message = "写入计划 " + itoaSmall(i+1) + " / " + itoaSmall(len(rows))
			if job.StopRequested {
				break
			}
		}
	}
	if err := hub.Core().Commit(); err != nil {
		return err
	}

	if job.StopRequested {
		if err := hub.Core().FinishSortPlan(planID, "outdated", int64(len(rows)), conflicts, "生成被停止"); err != nil {
			return err
		}
		job.Message = "生成已停止"
		return nil
	}
	if err := hub.Core().FinishSortPlan(planID, "ready", int64(len(rows)), conflicts, ""); err != nil {
		return err
	}
	job.Conflicts = conflicts
	if conflicts > 0 {
		job.Message = "计划完成——" + itoaSmall(int(conflicts)) + " 条冲突待解决"
	} else {
		job.Message = "计划完成——无冲突"
	}
	return nil
}

// detectConflicts 冲突检测——目标已存在同名文件 · 计划内同目标路径撞车 · 已在目标位置（标 √）。
func detectConflicts(rows []store.SortPlanItemRow) int64 {
	var conflicts int64
	destCount := map[string]int{}
	dirCache := map[string]map[string]bool{}
	for i := range rows {
		row := &rows[i]
		if equalFold(row.SrcPath, row.DestPath) {
			row.State = SortStateMoved
			row.Note = "已在目标位置"
			continue
		}
		if !fileAt(row.SrcPath, dirCache) {
			row.State = SortStateSkipped
			row.Note = "源文件不存在"
			continue
		}
		dir := filepath.Dir(row.DestPath)
		if dirFileNames(dir, dirCache)[filepath.Base(row.DestPath)] {
			row.State = SortStateConflict
			row.Note = "目标已存在同名文件"
			conflicts++
			continue
		}
		destCount[strings.ToLower(row.DestPath)]++
	}
	for i := range rows {
		row := &rows[i]
		if row.State != SortStatePending {
			continue
		}
		if n := destCount[strings.ToLower(row.DestPath)]; n > 1 {
			row.State = SortStateConflict
			row.Note = "计划内目标路径撞车（" + itoaSmall(n) + " 份同名）"
			conflicts++
		}
	}
	return conflicts
}

// dirFileNames 目标文件夹内的文件名集合（按目录缓存；目录不存在返回空集合）。
func dirFileNames(dir string, cache map[string]map[string]bool) map[string]bool {
	if dir == "" {
		return map[string]bool{}
	}
	if set, ok := cache[dir]; ok {
		return set
	}
	set := map[string]bool{}
	entries, err := os.ReadDir(dir)
	if err == nil {
		for _, e := range entries {
			if !e.IsDir() {
				set[strings.ToLower(e.Name())] = true
			}
		}
	}
	cache[dir] = set
	return set
}

// fileAt 某路径的文件当前是否存在（按所在目录缓存枚举）。
func fileAt(path string, cache map[string]map[string]bool) bool {
	if path == "" {
		return false
	}
	dir := filepath.Dir(path)
	if dir == "." || dir == "" {
		return false
	}
	return dirFileNames(dir, cache)[strings.ToLower(filepath.Base(path))]
}

// ExecutePlan 执行计划（只搬待办条目）——逐条核对快照位置后同库改路径，末尾清理空目录；
// 中断安全（每条状态即时落表）。
func ExecutePlan(hub *store.Hub, cfg *RootsConfig, planID int64, job *SortJobState) error {
	job.Phase = "搬运"
	job.Message = "开始搬运"
	rows, err := hub.Core().QuerySortPlanItems(planID)
	if err != nil {
		return err
	}
	job.Total = int64(len(rows))
	touched := map[string]bool{}

	for i := range rows {
		row := &rows[i]
		if job.StopRequested {
			break
		}
		if row.State != SortStatePending {
			touched[strings.ToLower(row.RootPath)] = true
			continue
		}
		job.Current = row.SrcPath
		touched[strings.ToLower(row.RootPath)] = true

		if isPlaced(row) {
			if err := setState(hub, job, planID, row, SortStateMoved, "已在快照位置"); err != nil {
				return err
			}
			continue
		}
		if fileExists(row.DestPath) {
			if err := setState(hub, job, planID, row, SortStateConflict, "目标已存在同名文件"); err != nil {
				return err
			}
			continue
		}
		if !fileExists(row.SrcPath) {
			if err := setState(hub, job, planID, row, SortStateSkipped, "源文件不存在"); err != nil {
				return err
			}
			continue
		}
		if IsReadOnlyRoot(cfg, row.RootPath) {
			if err := setState(hub, job, planID, row, SortStateSkipped, "库根只读，不搬运"); err != nil {
				return err
			}
			continue
		}

		reason := moveOne(hub, cfg, row)
		if reason == "" {
			if err := setState(hub, job, planID, row, SortStateMoved, ""); err != nil {
				return err
			}
		} else {
			if err := setState(hub, job, planID, row, SortStateFailed, reason); err != nil {
				return err
			}
		}
	}

	if job.StopRequested {
		job.Message = "已停止（已完成的行已落表，下次打开按快照核对续传）"
		return nil
	}
	roots := make([]string, 0, len(touched))
	for root := range touched {
		roots = append(roots, root)
	}
	sort.Strings(roots)
	for _, lower := range roots {
		root := lower
		// 还原原始大小写（touched 键是小写）——用计划里的原值更稳。
		for _, row := range rows {
			if strings.ToLower(row.RootPath) == lower {
				root = row.RootPath
				break
			}
		}
		if root == "" || IsReadOnlyRoot(cfg, root) {
			continue
		}
		job.PrunedDirs += PruneEmptyDirs(root)
	}
	job.Message = "搬运完成"
	return nil
}

// setState 写一条条目的状态并同步进度计数（失败明细 ≤50 条）。
func setState(hub *store.Hub, job *SortJobState, planID int64, row *store.SortPlanItemRow, state, note string) error {
	if err := hub.Core().SetSortItemState(planID, row.Seq, state, note); err != nil {
		return err
	}
	row.State, row.Note = state, note
	job.Done++
	switch state {
	case SortStateMoved:
		job.Moved++
	case SortStateSkipped:
		job.Skipped++
	case SortStateConflict:
		job.Conflicts++
	case SortStateFailed:
		job.Failed++
		if len(job.Errors) < 50 {
			job.Errors = append(job.Errors, row.SrcPath+"："+note)
		}
	}
	return nil
}

// isPlaced 快照位置判据——目标已在、源不在（或源就是目标）= 这份已经就位。
func isPlaced(row *store.SortPlanItemRow) bool {
	if equalFold(row.SrcPath, row.DestPath) {
		return fileExists(row.DestPath)
	}
	return fileExists(row.DestPath) && !fileExists(row.SrcPath)
}

// moveOne 搬一条——建目标目录 → 改名（同库内移动）→ 库同步；成功返回空串，失败返回原因。
func moveOne(hub *store.Hub, cfg *RootsConfig, row *store.SortPlanItemRow) string {
	dir := filepath.Dir(row.DestPath)
	if dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o777); err != nil {
			return "建目标目录失败：" + err.Error()
		}
	}
	if err := os.Rename(row.SrcPath, row.DestPath); err != nil {
		return "文件移动失败：" + err.Error()
	}
	if err := RenameModFilePath(hub, cfg, row.Guid, row.SrcPath, row.RootPath, row.DestPath); err != nil {
		return "文件已移动但数据库更新失败：" + err.Error()
	}
	return ""
}

// RenameModFilePath 把一条 mod 副本在同一库根内改路径（级别与库根不变），并重算 mod 主表。
func RenameModFilePath(hub *store.Hub, cfg *RootsConfig, guid, srcPath, srcRootPath, destPath string) error {
	lib, err := hub.LibOf(srcRootPath)
	if err != nil {
		return err
	}
	db, err := storeOfLib(hub, lib)
	if err != nil {
		return err
	}
	rec, err := db.RemoveModFileRow(srcPath)
	if err != nil {
		return err
	}
	if rec == nil {
		return errNoModFileRow(srcPath)
	}
	if _, err = db.InsertModFileRow(rec, rec.Tier, srcRootPath, destPath); err != nil {
		return err
	}
	stores := make([]*store.DB, 0, 8)
	for _, ref := range AllStoreRefs(hub, cfg) {
		stores = append(stores, ref.DB)
	}
	return hub.RecomputeMod(stores, guid)
}

// PruneEmptyDirs 删掉某库根下的空目录（自下而上逐级判断；库根本身不动）；返回清掉的目录数。
func PruneEmptyDirs(root string) int64 {
	if root == "" || !dirExists(root) {
		return 0
	}
	var removed int64
	entries, err := os.ReadDir(root)
	if err != nil {
		return 0
	}
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		removed += PruneEmptyDirs(filepath.Join(root, e.Name()))
	}
	if left, err := os.ReadDir(root); err == nil && len(left) == 0 {
		if err = os.Remove(root); err == nil {
			removed++
		}
	}
	return removed
}

// RecheckPlan 核对计划条目在文件系统上的实况——把已就位的行改标 √，返回该计划的冲突数现状。
func RecheckPlan(hub *store.Hub, planID int64) (int64, error) {
	var conflicts int64
	rows, err := hub.Core().QuerySortPlanItems(planID)
	if err != nil {
		return 0, err
	}
	dirCache := map[string]map[string]bool{}
	destCount := map[string]int{}
	for i := range rows {
		row := &rows[i]
		if equalFold(row.SrcPath, row.DestPath) {
			continue
		}
		if !fileAt(row.SrcPath, dirCache) {
			continue
		}
		destCount[strings.ToLower(row.DestPath)]++
	}
	for i := range rows {
		row := &rows[i]
		if row.State == SortStateMoved || row.State == SortStateSkipped {
			continue
		}
		state, note := SortStatePending, ""
		if equalFold(row.SrcPath, row.DestPath) {
			state, note = SortStateMoved, "已在快照位置"
			if !fileAt(row.SrcPath, dirCache) {
				state, note = SortStateSkipped, "源文件不存在"
			}
		} else {
			destHere := fileAt(row.DestPath, dirCache)
			srcHere := fileAt(row.SrcPath, dirCache)
			count := destCount[strings.ToLower(row.DestPath)]
			switch {
			case destHere && !srcHere:
				state, note = SortStateMoved, "已在快照位置"
			case destHere:
				state, note = SortStateConflict, "目标已存在同名文件"
			case !srcHere:
				state, note = SortStateSkipped, "源文件不存在"
			case count > 1:
				state, note = SortStateConflict, "计划内目标路径撞车（"+itoaSmall(count)+" 份同名）"
			}
		}
		if state == SortStateConflict {
			conflicts++
		}
		if state != row.State || note != row.Note {
			if err = hub.Core().SetSortItemState(planID, row.Seq, state, note); err != nil {
				return 0, err
			}
		}
	}
	return conflicts, nil
}

// ConflictGroup 整理冲突的一组候选——按 seq 定位那条冲突，取同目标路径的几份。
func ConflictGroup(hub *store.Hub, planID, seq int64) (*SortConflictGroup, string) {
	group := &SortConflictGroup{}
	rows, err := hub.Core().QuerySortPlanItems(planID)
	if err != nil {
		return group, err.Error()
	}
	var hit *store.SortPlanItemRow
	for i := range rows {
		if rows[i].Seq == seq {
			hit = &rows[i]
			break
		}
	}
	if hit == nil {
		return group, "计划 #" + itoaSmall64(planID) + " 里没有 seq " + itoaSmall64(seq) + " 的条目"
	}
	group.Guid, group.DestPath = hit.Guid, hit.DestPath
	for i := range rows {
		row := &rows[i]
		if !equalFold(row.DestPath, hit.DestPath) {
			continue
		}
		group.Items = append(group.Items, SortConflictCandidate{
			Seq:      row.Seq,
			Tier:     row.Tier,
			RootPath: row.RootPath,
			FilePath: row.SrcPath,
			FileName: filepath.Base(row.SrcPath),
			Size:     row.Size,
			Mtime:    row.Mtime,
			State:    row.State,
			Note:     row.Note,
		})
	}
	return group, ""
}

// CountState 计划表里某状态的条目数。
func CountState(hub *store.Hub, planID int64, state string) (int64, error) {
	return hub.Core().CountSortPlanItems(planID, state)
}

// fileExists 路径是否为已存在的文件。
func fileExists(p string) bool {
	info, err := os.Stat(p)
	return err == nil && !info.IsDir()
}

// dirExists 路径是否为已存在的目录。
func dirExists(p string) bool {
	info, err := os.Stat(p)
	return err == nil && info.IsDir()
}

// equalFold 忽略大小写的路径比较（Windows 口径）。
func equalFold(a, b string) bool { return strings.EqualFold(a, b) }

// itoaSmall64 十进制 int64 文本。
func itoaSmall64(v int64) string { return strconv.FormatInt(v, 10) }

// errNoModFileRow 库表里找不到源副本行（文件动了而数据库没找到，按失败上报）。
func errNoModFileRow(path string) error {
	return fmt.Errorf("数据库未找到源副本行：%s（需重扫核对）", path)
}

// ResolveConflict 解决一条整理冲突——保留这一份：其余同 guid 非旧版副本判旧版入缓存库
// （复用重复副本同一实现），保留份留在原位、判旧份的计划条目改指缓存库新位置，
// 随后重核冲突数并写回计划头。成功返回（detail, ""），失败返回（"", 原因）。
func ResolveConflict(hub *store.Hub, cfg *RootsConfig, planID, seq int64) (string, string) {
	plan, err := hub.Core().SortPlanByID(planID)
	if err != nil {
		return "", "诊断 " + err.Error()
	}
	if plan == nil {
		return "", "计划不存在——重新生成后再试"
	}
	if plan.State != "ready" {
		return "", "计划状态是 " + plan.State + "（不是 ready）——无法处理冲突"
	}
	rows, err := hub.Core().QuerySortPlanItems(planID)
	if err != nil {
		return "", "诊断 " + err.Error()
	}
	var keep *store.SortPlanItemRow
	for i := range rows {
		if rows[i].Seq == seq {
			keep = &rows[i]
			break
		}
	}
	if keep == nil {
		return "", "计划 #" + itoaSmall64(planID) + " 里没有 seq " + itoaSmall64(seq) + " 的条目"
	}
	if keep.Guid == "" {
		return "", "这条条目没有 guid——判不掉其余副本，请手动处理"
	}
	if !fileExists(keep.SrcPath) {
		return "", "文件不在了：" + keep.SrcPath + "——换一份保留，或重新生成计划"
	}

	// [段1] 判旧其余副本（与重复副本同一条实现；保留份不进主库，留在原位等整理）
	moves := []DemoteMove{}
	demote, demoteErr := MarkOldOthersHub(hub, cfg, keep.Guid, keep.SrcPath, false, &moves)
	if demoteErr != nil {
		return "", demoteErr.Error()
	}

	// [段2] 判旧出的条目改指缓存库新位置（仍按作者归位，不留散文件）
	authors, authorErr := hub.Core().ModAuthorMap()
	if authorErr != nil {
		return "", "诊断 " + authorErr.Error()
	}
	demoted := map[string]bool{}
	for _, mv := range moves {
		demoted[strings.ToLower(mv.NewPath)] = true
		for _, r := range rows {
			if !strings.EqualFold(r.SrcPath, mv.OldPath) {
				continue
			}
			folder := FolderOfAuthor(authors[r.Guid])
			mvLib, libErr := hub.LibOf(mv.RootPath)
			if libErr != nil {
				return "", "诊断 " + libErr.Error()
			}
			mvDest := filepath.Join(mv.RootPath, folder, mv.NewName)
			if err = hub.Core().SetSortItemLocation(planID, r.Seq, mvLib, mv.Tier, mv.RootPath, folder, mv.NewPath, mvDest, SortStatePending, ""); err != nil {
				return "", "诊断 " + err.Error()
			}
		}
	}
	if err = hub.Core().SetSortItemState(planID, keep.Seq, SortStatePending, ""); err != nil {
		return "", "诊断 " + err.Error()
	}

	// [段3] 重核冲突现状——判旧备注在重核后再落一次（重核只写状态与冲突原因）
	conflicts, recheckErr := RecheckPlan(hub, planID)
	if recheckErr != nil {
		return "", "诊断 " + recheckErr.Error()
	}
	after, listErr := hub.Core().QuerySortPlanItems(planID)
	if listErr != nil {
		return "", "诊断 " + listErr.Error()
	}
	for _, r := range after {
		if r.State != SortStatePending || !demoted[strings.ToLower(r.SrcPath)] {
			continue
		}
		_ = hub.Core().SetSortItemState(planID, r.Seq, SortStatePending, "已判旧版 → 缓存库")
	}
	if err = hub.Core().FinishSortPlan(planID, plan.State, plan.ItemCount, conflicts, plan.Note); err != nil {
		return "", "诊断 " + err.Error()
	}
	detail := demote
	if len(moves) == 0 {
		detail = "已指定保留 " + filepath.Base(keep.SrcPath) + "——同 guid 没有其它待判旧副本（这一处冲突应已消失）"
	}
	return detail, ""
}
