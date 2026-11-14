// 扫描链（G4）——mod 侧先落：库根收集 → 段循环（modrow）→ 清扫 → 离线标记 → 作者索引。
//
// 对齐 C# Core/Scanner.cs 的同名编排（§ScanMods / §CollectRoots / §RunModSegment / §CleanupMods /
// §ApplyOfflineMarks）。卡片侧（行 / 头段 / 声明区 / 缩略图 / 分项分析）留 scan-cards 段。
package core

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"kkgo/internal/store"
)

// ScanScope 扫描范围（对齐 C# ScanScope）。
type ScanScope int

const (
	// ScanScopeAll 全部库根。
	ScanScopeAll ScanScope = iota
	// ScanScopePreset 预置条目（主库 / 缓存库槽位 / 卡片锁定主库）。
	ScanScopePreset
	// ScanScopeExtra 使用者添加的库根。
	ScanScopeExtra
)

// ScanBatchSize 扫描写事务的批提交间隔（对齐 C# Scanner.BatchSize）。
const ScanBatchSize = 500

// ModProgressEvery mod 侧进度出声间隔（对齐 C# Scanner.ModProgressEvery）。
const ModProgressEvery = 200

// ScanMaxErrors 错误明细上限（对齐 C# Scanner.MaxErrors）。
const ScanMaxErrors = 200

// ScanResult 扫描统计（对齐 C# ScanResult 的 mod 侧字段）。
type ScanResult struct {
	// Seen 枚举到的文件数。
	Seen int
	// Added 步骤实际完成数。
	Added int
	// Skipped 步骤跳过数。
	Skipped int
	// Failed 步骤失败数。
	Failed int
	// NonCard 非卡文件数（卡片侧）。
	NonCard int
	// NonMod mod 库内读不到 manifest.xml 的文件数。
	NonMod int
	// Removed 清理掉的记录数。
	Removed int
	// RefEntries 本次入库的引用条目数。
	RefEntries int64
	// ThumbBytes 缩略图字节总量。
	ThumbBytes int64
	// NamesRead 本次扫描新读到角色名的卡片数。
	NamesRead int
	// NamesFilled 本次扫描为存量卡片补读到角色名的数。
	NamesFilled int
	// TypesFixed 本次扫描补正卡类型的数。
	TypesFixed int
	// TimelineRead 本次扫描读到 timeline 的场景卡数。
	TimelineRead int
	// PluginDlls 本次扫描枚举到的插件 dll 数。
	PluginDlls int
	// Plugins 本次扫描落库的插件项数。
	Plugins int
	// PluginConfigs 本次扫描读到的插件配置文件数。
	PluginConfigs int
	// StepIndex 当前步骤序号（1 起）。
	StepIndex int
	// StepCount 步骤总数。
	StepCount int
	// StepName 当前步骤名。
	StepName string
	// StepTotal 当前步骤文件总数。
	StepTotal int
	// StepDone 当前步骤已处理文件数。
	StepDone int
	// Elapsed 耗时。
	Elapsed time.Duration
	// Errors 错误明细。
	Errors []string
	// OfflineRoots 本次判定为「该离线」的库根路径。
	OfflineRoots []string
}

// addError 记一条错误明细（上限截断）。
func (r *ScanResult) addError(message string) {
	if len(r.Errors) < ScanMaxErrors {
		r.Errors = append(r.Errors, message)
	}
}

// ScanFile 一轮扫描里一个文件的内存态（跨步骤复用）。
type ScanFile struct {
	// Path 文件绝对路径。
	Path string
	// RootPath 所属库根路径。
	RootPath string
	// Folder 相对库根的文件夹（根目录为空串）。
	Folder string
	// Size 文件字节数。
	Size int64
	// Mtime 修改时间戳（UTC 文本）。
	Mtime string
	// ModGuid mod 副本的 guid（mod 行步写入或建索引时读得）。
	ModGuid string
	// CardId 卡片 id（行步写入或建索引时读得；0 = 尚未建行）。
	CardId int64
	// ImageEnd 图片区终点（头段步或建索引时读得）。
	ImageEnd int64
	// CardType 卡类型（头段步或建索引时读得）。
	CardType string
	// OldCardType 库里原先记的卡类型（供类型补正出声）。
	OldCardType string
	// HeadRead 本轮会话内头段是否已解析。
	HeadRead bool
	// HeadFailed 本轮会话内头段读取失败（失败后不再重试）。
	HeadFailed bool
	// NonCardFile 本轮判定的非卡文件（已登记 non_card，后续读步一律跳过）。
	NonCardFile bool
	// NonCardReason 非卡判定理由（登记清单时落库）。
	NonCardReason string
	// IsNew 本轮之前库里是否已有该文件的行。
	IsNew bool
	// NonModFile 已判定为非 mod 文件（登记进 non_card）。
	NonModFile bool
}

// ScanRootState 一个库根在本轮扫描中的状态。
type ScanRootState struct {
	// Entry 库根条目。
	Entry RootEntry
	// Mods true = mod 侧。
	Mods bool
	// Store 该库根对应的库文件（主库条目走主库）。
	Store *store.DB
	// Files 枚举所得的文件清单。
	Files []*ScanFile
	// Steps 已完成步骤索引——file_path → （步骤 id → 记录）。
	Steps map[string]map[string]store.ScanStepRow
	// EnumFailed 枚举是否失败（失败时不清扫）。
	EnumFailed bool
	// Shards 卡片侧的常驻分片库（每片一个独立 db；mod 侧为 nil）。
	Shards []*store.DB
	// ShardSteps 分片库各自的完成戳索引（与 Shards 同序）。
	ShardSteps []map[string]map[string]store.ScanStepRow
}

// ScanLog 扫描出声回调（可为 nil）。
type ScanLog func(message string)

// report 出声（log 为 nil 时静默）。
func report(log ScanLog, message string) {
	if log != nil {
		log(message)
	}
}

// SegmentsFor 取某一侧的执行段（顺序来自计划——跨侧交错的段按顺序过滤）。
func SegmentsFor(plan *ScanPlan, mods bool) []ScanSegment {
	list := make([]ScanSegment, 0, 4)
	for _, seg := range plan.Segments() {
		if seg.Mods == mods {
			list = append(list, seg)
		}
	}
	return list
}

// totalFiles 各库根文件数合计。
func totalFiles(states []ScanRootState) int {
	n := 0
	for _, st := range states {
		n += len(st.Files)
	}
	return n
}

// libRoots 把配置里的库根转成 store 侧的布局视图（mod 侧 / 卡片侧）。
func libRoots(entries []RootEntry) []store.LibRoot {
	list := make([]store.LibRoot, 0, len(entries))
	for _, e := range entries {
		list = append(list, store.LibRoot{Path: e.Path, Tier: e.Tier, Locked: e.Locked})
	}
	return list
}

// ModLibRoots 配置里的 mod 库根（布局视图）。
func ModLibRoots(cfg *RootsConfig) []store.LibRoot {
	if cfg == nil {
		return nil
	}
	return libRoots(cfg.ModRoots)
}

// CardLibRoots 配置里的卡片库根（布局视图）。
func CardLibRoots(cfg *RootsConfig) []store.LibRoot {
	if cfg == nil {
		return nil
	}
	return libRoots(cfg.CardRoots)
}

// CanOffline 该库根是否适用离线语义（预置条目 —— 锁定主库 / mod 缓存库槽位 —— 不适用）。
func CanOffline(entry RootEntry, isMods bool) bool {
	if entry.Locked {
		return false
	}
	if isMods && entry.Tier == TierCache {
		return false
	}
	return true
}

// InScope 库根是否落在本次扫描范围内——预置条目 = 不可离线的那些，其余为使用者添加的库根。
func InScope(entry RootEntry, scope ScanScope, isMods bool) bool {
	if scope == ScanScopeAll {
		return true
	}
	preset := !CanOffline(entry, isMods)
	if scope == ScanScopePreset {
		return preset
	}
	return !preset
}

// samePath 两条库根是否同一条（路径归一后比较）。
func samePath(a, b string) bool {
	x := strings.TrimRight(strings.TrimSpace(a), `\/`)
	y := strings.TrimRight(strings.TrimSpace(b), `\/`)
	return strings.EqualFold(x, y)
}

// folderOf 计算文件相对库根的文件夹（根目录返回空串）。
func folderOf(filePath, rootNorm string) string {
	dir := filepath.Dir(filePath)
	if dir == "" || dir == "." {
		return ""
	}
	if len(dir) >= len(rootNorm) && strings.EqualFold(dir[:len(rootNorm)], rootNorm) {
		return strings.TrimLeft(dir[len(rootNorm):], `\/`)
	}
	return dir
}

// enumerateFiles 枚举文件（pattern 过滤；失败时 failed 为真——空结果不代表目录为空）。
// 结果按大小写不敏感排序（对齐 C# SortedSet<string>(OrdinalIgnoreCase)）。
func enumerateFiles(dir, pattern string, recurse bool, log ScanLog) ([]string, bool) {
	failed := false
	list := make([]string, 0, 64)
	walkErr := filepath.WalkDir(dir, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			failed = true
			report(log, "  枚举失败："+err.Error())
			if d != nil && d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if d.IsDir() {
			if !recurse && path != dir {
				return filepath.SkipDir
			}
			return nil
		}
		if pattern != "" && pattern != "*" {
			ok, matchErr := filepath.Match(pattern, filepath.Base(path))
			if matchErr != nil || !ok {
				return nil
			}
		}
		abs, absErr := filepath.Abs(path)
		if absErr != nil {
			abs = path
		}
		list = append(list, abs)
		return nil
	})
	if walkErr != nil {
		failed = true
		report(log, "  枚举失败："+walkErr.Error())
	}
	sort.Slice(list, func(i, j int) bool {
		return strings.ToLower(list[i]) < strings.ToLower(list[j])
	})
	return list, failed
}

// CollectModRoots 收集本次扫描的 mod 库根状态——跳过离线库与不存在的目录；枚举一次并缓存（全枚举，不限扩展名）。
func CollectModRoots(hub *store.Hub, cfg *RootsConfig, only *RootEntry, scope ScanScope, result *ScanResult, log ScanLog) ([]ScanRootState, error) {
	list := make([]ScanRootState, 0, len(cfg.ModRoots))
	for _, root := range cfg.ModRootsOrdered() {
		if only != nil && !samePath(root.Path, only.Path) {
			continue
		}
		if !InScope(root, scope, true) {
			continue
		}
		report(log, "级别 "+itoaInt(root.Tier)+"（"+TierName(root.Tier)+"） · "+root.Path)
		if root.Offline {
			report(log, "  离线库，已跳过（内容取本库数据库）")
			continue
		}
		if info, statErr := os.Stat(root.Path); statErr != nil || !info.IsDir() {
			result.addError("目录不存在，已跳过：" + root.Path)
			markOffline(result, root, true, log)
			continue
		}
		st := ScanRootState{Entry: root, Mods: true}
		db, storeErr := hub.StoreFor(root.Tier, root.Locked, root.Path, true)
		if storeErr != nil {
			result.addError("库文件打开失败（" + root.Path + "）：" + storeErr.Error())
			continue
		}
		st.Store = db
		files, enumFailed := enumerateFiles(root.Path, "*", root.Recurse, log)
		st.EnumFailed = enumFailed
		result.Seen += len(files)
		if len(files) == 0 && !enumFailed {
			markOffline(result, root, true, log)
		}
		built, buildErr := buildModFiles(root, files, db)
		if buildErr != nil {
			return nil, buildErr
		}
		st.Files = built
		steps, stepErr := db.LoadScanSteps()
		if stepErr != nil {
			return nil, stepErr
		}
		st.Steps = steps
		list = append(list, st)
	}
	return list, nil
}

// buildModFiles 建 mod 侧文件清单内存态——从库里的副本索引补 guid（缺 = 新文件）。
func buildModFiles(root RootEntry, files []string, db *store.DB) ([]*ScanFile, error) {
	guids, err := db.LoadModFileGuids()
	if err != nil {
		return nil, err
	}
	// 路径索引按大小写不敏感归一（对齐 C# Dictionary(OrdinalIgnoreCase)）
	lower := make(map[string]string, len(guids))
	for path, guid := range guids {
		lower[strings.ToLower(path)] = guid
	}
	rootNorm := strings.TrimRight(root.Path, `\/`)
	list := make([]*ScanFile, 0, len(files))
	for _, f := range files {
		sf := ScanFile{Path: f, RootPath: root.Path, Folder: folderOf(f, rootNorm)}
		if info, statErr := os.Stat(f); statErr == nil {
			sf.Size = info.Size()
			sf.Mtime = store.StampOf(info.ModTime())
		}
		if guid, ok := lower[strings.ToLower(f)]; ok {
			sf.ModGuid = guid
		} else {
			sf.IsNew = true
		}
		list = append(list, &sf)
	}
	return list, nil
}

// stepDone 该文件该步骤是否已完成——size + mtime 双等且步骤记录在案。
func stepDone(st *ScanRootState, f *ScanFile, stepID string) bool {
	if st.Steps == nil {
		return false
	}
	steps, ok := st.Steps[f.Path]
	if !ok {
		steps, ok = st.Steps[strings.ToLower(f.Path)]
	}
	if !ok {
		return false
	}
	rec, ok := steps[stepID]
	if !ok {
		return false
	}
	return rec.Size == f.Size && rec.Mtime == f.Mtime
}

// ScanMods 扫描 mod 库（plan 为 nil 时用默认计划；only 非空时只扫该条库根；离线库一律跳过）。
// 对齐 C# Scanner.ScanMods(hub, cfg, only, scope, force, plan, log)。
func ScanMods(hub *store.Hub, cfg *RootsConfig, only *RootEntry, scope ScanScope, force bool, plan *ScanPlan, log ScanLog) (*ScanResult, error) {
	start := time.Now()
	result := &ScanResult{}
	if plan == nil {
		plan = DefaultScanPlan()
	}
	states, err := CollectModRoots(hub, cfg, only, scope, result, log)
	if err != nil {
		return nil, err
	}
	segs := SegmentsFor(plan, true)
	result.StepCount = len(segs)
	index := 0
	for _, seg := range segs {
		index++
		result.StepIndex = index
		result.StepName = seg.Label()
		result.StepDone = 0
		result.StepTotal = totalFiles(states)
		report(log, fmt.Sprintf("步骤 %d/%d：%s（%d 个文件）", index, len(segs), seg.Label(), result.StepTotal))
		for i := range states {
			if err = runModSegment(hub, cfg, &states[i], seg, force, result, log); err != nil {
				return nil, err
			}
		}
	}
	for i := range states {
		if err = cleanupMods(hub, cfg, &states[i], result, log); err != nil {
			return nil, err
		}
	}
	if err = applyOfflineMarks(hub, cfg, result, true, log); err != nil {
		return nil, err
	}
	authors, authorErr := hub.Core().RefreshModAuthors()
	if authorErr != nil {
		return nil, authorErr
	}
	report(log, fmt.Sprintf("作者索引已重建：%d 位作者", authors))
	result.Elapsed = time.Since(start)
	return result, nil
}

// runModSegment mod 侧一段的执行（每 ScanBatchSize 个文件一次提交）。
func runModSegment(hub *store.Hub, cfg *RootsConfig, st *ScanRootState, seg ScanSegment, force bool, result *ScanResult, log ScanLog) error {
	db := st.Store
	total := len(st.Files)
	batch := 0
	if err := db.Begin(); err != nil {
		return err
	}
	for i := range st.Files {
		f := st.Files[i]
		result.StepDone++
		for _, step := range seg.Steps {
			if !force && stepDone(st, f, step.Id) {
				result.Skipped++
				continue
			}
			ok, skip, stepErr := runModStep(hub, cfg, db, st, f, step, force, result)
			if stepErr != nil {
				db.Rollback()
				return stepErr
			}
			if ok {
				if err := db.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime); err != nil {
					db.Rollback()
					return err
				}
				result.Added++
			} else if skip {
				if err := db.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime); err != nil {
					db.Rollback()
					return err
				}
				result.NonMod++
			} else {
				result.Failed++
			}
		}
		batch++
		if batch >= ScanBatchSize {
			if err := db.Commit(); err != nil {
				return err
			}
			if err := db.Begin(); err != nil {
				return err
			}
			batch = 0
		}
		if log != nil && (i+1)%ModProgressEvery == 0 {
			log("  " + seg.Label() + " " + itoaInt(i+1) + "/" + itoaInt(total))
		}
	}
	return db.Commit()
}

// runModStep mod 侧单步执行——skip 为真表示该文件不是 mod（读不到 manifest.xml），已判定为跳过。
func runModStep(hub *store.Hub, cfg *RootsConfig, db *store.DB, st *ScanRootState, f *ScanFile, step ScanStepDef, force bool, result *ScanResult) (bool, bool, error) {
	switch step.Id {
	case "modrow":
		info := ParseZipMod(f.Path)
		if info.ErrorKind == ModErrNotContainer || info.ErrorKind == ModErrNoManifest {
			f.NonModFile = true
			reason := "不是 zip 容器"
			if info.ErrorKind == ModErrNoManifest {
				reason = "容器内无 manifest.xml"
			}
			if err := db.UpsertNonCard(f.Path, f.RootPath, "mod", reason, f.Size, f.Mtime, nil); err != nil {
				return false, true, err
			}
			return false, true, nil
		}
		if info.Error != "" || info.Guid == "" {
			detail := info.Error
			if detail == "" {
				detail = "无 guid"
			}
			result.addError(info.FileName + " → " + detail)
			return false, false, nil
		}
		f.ModGuid = info.Guid
		input := store.ModFileInput{
			Guid: info.Guid, FilePath: info.FilePath, FileName: info.FileName, Size: info.Size,
			Tier: st.Entry.Tier, RootPath: st.Entry.Path, Mtime: f.Mtime,
			Name: info.Name, Version: info.Version, Author: info.Author, Website: info.Website,
			Description: info.Description, SchemaVer: info.SchemaVer, EntryCount: int64(info.EntryCount),
			Error: info.Error, Found: info.Found,
		}
		if err := db.UpsertModFile(input); err != nil {
			return false, false, err
		}
		if err := db.FillModMeta(input); err != nil {
			return false, false, err
		}
		if err := db.DeleteNonCardByPath(f.Path); err != nil {
			return false, false, err
		}
		return true, false, nil
	case "composition":
		// 组成档案段（默认计划不启用）——归档到 G4 余项：需要 hub.AnalyzeComposition 的 Go 版
		result.addError(filepath.Base(f.Path) + " → composition 步骤尚未移植（Go 侧 G4 余项）")
		return false, false, nil
	default:
		return false, false, nil
	}
}

// cleanupMods mod 侧清扫——删除已消失的副本与旧版登记，并跨库重算受影响的 guid。
func cleanupMods(hub *store.Hub, cfg *RootsConfig, st *ScanRootState, result *ScanResult, log ScanLog) error {
	if st.EnumFailed || len(st.Files) == 0 {
		return nil
	}
	present := make(map[string]bool, len(st.Files))
	for _, f := range st.Files {
		present[f.Path] = true
	}
	db := st.Store
	if err := db.Begin(); err != nil {
		return err
	}
	goneGuids, err := db.DeleteModFilesMissingUnderRoot(st.Entry.Path, present)
	if err != nil {
		db.Rollback()
		return err
	}
	goneOld, err := hub.Core().DeleteModOldMissingUnderRoot(st.Entry.Path, present)
	if err != nil {
		db.Rollback()
		return err
	}
	goneSteps, err := db.DeleteScanStepsMissingUnderRoot(st.Entry.Path, present)
	if err != nil {
		db.Rollback()
		return err
	}
	goneNon, err := db.DeleteNonCardsMissingUnderRoot(st.Entry.Path)
	if err != nil {
		db.Rollback()
		return err
	}
	if err = db.Commit(); err != nil {
		return err
	}
	if len(goneGuids) > 0 {
		stores := hub.AllStores(ModLibRoots(cfg), CardLibRoots(cfg))
		for _, guid := range goneGuids {
			if err = hub.RecomputeMod(stores, guid); err != nil {
				return err
			}
		}
	}
	if len(goneGuids) > 0 || goneOld > 0 || goneNon > 0 {
		result.Removed += len(goneGuids) + goneOld
		report(log, fmt.Sprintf("  已清理 %d 个 guid 的已消失副本记录 · 旧版登记 %d 条 · 步骤记录 %d 条 · 非卡登记 %d 条",
			len(goneGuids), goneOld, goneSteps, goneNon))
	}
	return nil
}

// markOffline 标记「该离线」——预置条目不适用离线语义，只出声不出档。
func markOffline(result *ScanResult, root RootEntry, isMods bool, log ScanLog) {
	if !CanOffline(root, isMods) {
		report(log, "  目录不存在或为空——预置条目不适用离线，仅跳过")
		return
	}
	result.OfflineRoots = append(result.OfflineRoots, root.Path)
	report(log, "  目录不存在或为空——已判为离线库（待办 +1）")
}

// applyOfflineMarks 把扫描结果里的「该离线」库根落进配置并登记待办（同库只留一条待办）。
func applyOfflineMarks(hub *store.Hub, cfg *RootsConfig, result *ScanResult, isMods bool, log ScanLog) error {
	if len(result.OfflineRoots) == 0 {
		return nil
	}
	list := cfg.CardRoots
	if isMods {
		list = cfg.ModRoots
	}
	if list == nil {
		return nil
	}
	changed := false
	for _, path := range result.OfflineRoots {
		for i := range list {
			if !strings.EqualFold(strings.TrimSpace(list[i].Path), strings.TrimSpace(path)) {
				continue
			}
			if !list[i].Offline {
				list[i].Offline = true
				changed = true
				if err := hub.Core().AddOfflineTodo(strings.ToLower(strings.TrimSpace(list[i].Path))); err != nil {
					return err
				}
				report(log, "  已自动离线并登记待办："+list[i].Path)
			}
			break
		}
	}
	if !changed {
		return nil
	}
	if isMods {
		cfg.ModRoots = list
	} else {
		cfg.CardRoots = list
	}
	text, err := RootsConfigJSON(cfg)
	if err != nil {
		return err
	}
	return hub.Core().SetSetting("roots", text)
}

// itoaInt 十进制整数转文本（扫描出声用）。
func itoaInt(v int) string { return strconv.Itoa(v) }

// ---------- 卡片侧（scan-cards） ----------

// mergeResult 把一轮子扫描的计数并入（对齐 C# ScanResult.Merge）。
func mergeResult(dst, src *ScanResult) {
	if src == nil {
		return
	}
	dst.Seen += src.Seen
	dst.Added += src.Added
	dst.Skipped += src.Skipped
	dst.Failed += src.Failed
	dst.NonCard += src.NonCard
	dst.NonMod += src.NonMod
	dst.RefEntries += src.RefEntries
	dst.ThumbBytes += src.ThumbBytes
	dst.NamesRead += src.NamesRead
	dst.NamesFilled += src.NamesFilled
	dst.TypesFixed += src.TypesFixed
	dst.TimelineRead += src.TimelineRead
	dst.Removed += src.Removed
	for _, e := range src.Errors {
		dst.addError(e)
	}
	dst.Elapsed += src.Elapsed
}

// CollectCardRoots 收集卡片库根状态——跳过离线库与不存在的目录；枚举一次并缓存（只收 *.png）。
func CollectCardRoots(hub *store.Hub, cfg *RootsConfig, only *RootEntry, scope ScanScope, result *ScanResult, log ScanLog) ([]ScanRootState, error) {
	list := make([]ScanRootState, 0, len(cfg.CardRoots))
	for _, root := range cfg.CardRootsOrdered() {
		if only != nil && !samePath(root.Path, only.Path) {
			continue
		}
		if !InScope(root, scope, false) {
			continue
		}
		tag := "（仅本目录）"
		if root.Recurse {
			tag = "（含子目录）"
		}
		report(log, "级别 "+itoaInt(root.Tier)+" · "+root.Path+tag)
		if root.Offline {
			report(log, "  离线库，已跳过（内容取本库数据库）")
			continue
		}
		if info, statErr := os.Stat(root.Path); statErr != nil || !info.IsDir() {
			result.addError("目录不存在，已跳过：" + root.Path)
			markOffline(result, root, false, log)
			continue
		}
		st := ScanRootState{Entry: root}
		files, enumFailed := enumerateFiles(root.Path, "*.png", root.Recurse, log)
		st.EnumFailed = enumFailed
		result.Seen += len(files)
		if len(files) == 0 && !enumFailed {
			markOffline(result, root, false, log)
		}
		base, baseErr := hub.CardBaseOf(root.Path)
		if baseErr != nil {
			result.addError("库文件分配失败（" + root.Path + "）：" + baseErr.Error())
			continue
		}
		first, storeErr := hub.StoreFor(root.Tier, root.Locked, root.Path, false)
		if storeErr != nil {
			result.addError("库文件打开失败（" + root.Path + "）：" + storeErr.Error())
			continue
		}
		st.Store = first
		stamps, stampErr := loadCardStampsCross(hub, base)
		if stampErr != nil {
			return nil, stampErr
		}
		st.Files = buildCardFiles(root, files, stamps)
		steps, stepErr := loadScanStepsCross(hub, base)
		if stepErr != nil {
			return nil, stepErr
		}
		st.Steps = steps
		list = append(list, st)
	}
	return list, nil
}

// loadCardStampsCross 跨分片合并卡片索引快照（每个文件只在一片，后写者覆盖无冲突）。
func loadCardStampsCross(hub *store.Hub, baseLib int) (map[string]store.CardStamp, error) {
	merged := make(map[string]store.CardStamp, 256)
	libs, err := hub.LibsOfBase(baseLib)
	if err != nil {
		return nil, err
	}
	for _, lib := range libs {
		db, libErr := hub.StoreByLib(lib)
		if libErr != nil {
			continue
		}
		one, stampErr := db.LoadCardStamps()
		if stampErr != nil {
			return nil, stampErr
		}
		for k, v := range one {
			merged[k] = v
		}
	}
	return merged, nil
}

// loadScanStepsCross 跨分片合并完成戳索引（分片常驻后每片各存自己的戳）。
func loadScanStepsCross(hub *store.Hub, baseLib int) (map[string]map[string]store.ScanStepRow, error) {
	merged := make(map[string]map[string]store.ScanStepRow, 256)
	libs, err := hub.LibsOfBase(baseLib)
	if err != nil {
		return nil, err
	}
	for _, lib := range libs {
		db, libErr := hub.StoreByLib(lib)
		if libErr != nil {
			continue
		}
		one, stepErr := db.LoadScanSteps()
		if stepErr != nil {
			return nil, stepErr
		}
		for k, v := range one {
			merged[k] = v
		}
	}
	return merged, nil
}

// buildCardFiles 建卡片侧文件清单内存态——从库里的索引补卡片 id / 图片区终点 / 旧卡类型。
func buildCardFiles(root RootEntry, files []string, stamps map[string]store.CardStamp) []*ScanFile {
	lower := make(map[string]store.CardStamp, len(stamps))
	for path, stamp := range stamps {
		lower[strings.ToLower(path)] = stamp
	}
	rootNorm := strings.TrimRight(root.Path, `\/`)
	list := make([]*ScanFile, 0, len(files))
	for _, f := range files {
		sf := ScanFile{Path: f, RootPath: root.Path, Folder: folderOf(f, rootNorm)}
		if info, statErr := os.Stat(f); statErr == nil {
			sf.Size = info.Size()
			sf.Mtime = store.StampOf(info.ModTime())
		}
		if stamp, ok := lower[strings.ToLower(f)]; ok {
			sf.CardId = stamp.CardId
			sf.ImageEnd = stamp.ImageEnd
			sf.OldCardType = stamp.CardType
		} else {
			sf.IsNew = true
		}
		list = append(list, &sf)
	}
	return list
}

// ensureShards 准备分片库——片数首次按分片目标定下并落设置，此后稳定不变；缺片从模板释放。
func ensureShards(hub *store.Hub, st *ScanRootState, total int, log ScanLog) error {
	base, err := hub.CardBaseOf(st.Entry.Path)
	if err != nil || base <= 0 {
		return err
	}
	shards, err := hub.ShardCountOf(base)
	if err != nil {
		return err
	}
	if shards <= 0 {
		shards = hub.ShardTarget()
		if shards < 1 {
			shards = 1
		}
		if err = hub.SetShardCount(base, shards); err != nil {
			return err
		}
		if log != nil && shards > 1 {
			log("  [分片] 本库根分片数定为 " + itoaInt(shards) + "（文件 " + itoaInt(total) + " 个）——此后稳定不变")
		}
	}
	if len(st.Shards) == shards {
		// 分片库跨段常驻——完成戳索引每段重读（上一段刚写的戳要能被本段看见）
		st.ShardSteps = make([]map[string]map[string]store.ScanStepRow, shards)
		for k, db := range st.Shards {
			steps, stepErr := db.LoadScanSteps()
			if stepErr != nil {
				return stepErr
			}
			st.ShardSteps[k] = steps
		}
		return nil
	}
	dbs, err := hub.ShardsOf(base, shards)
	if err != nil {
		return err
	}
	st.Shards = dbs
	st.ShardSteps = make([]map[string]map[string]store.ScanStepRow, shards)
	for k, db := range dbs {
		steps, stepErr := db.LoadScanSteps()
		if stepErr != nil {
			return stepErr
		}
		st.ShardSteps[k] = steps
	}
	return nil
}

// runCardSegment 卡片侧一段的执行——按路径哈希分桶落常驻分片库（串行逐片；布局与并行一致）。
func runCardSegment(hub *store.Hub, st *ScanRootState, seg ScanSegment, force bool, thumbWidth, thumbQuality int, result *ScanResult, log ScanLog) error {
	if err := ensureShards(hub, st, len(st.Files), log); err != nil {
		return err
	}
	shards := len(st.Shards)
	if shards == 0 {
		return nil
	}
	buckets := make([][]*ScanFile, shards)
	for _, f := range st.Files {
		k := store.ShardOfPath(f.Path, shards)
		buckets[k] = append(buckets[k], f)
	}
	agg := &ScanResult{}
	for k := 0; k < shards; k++ {
		if err := runCardRange(st, seg, force, thumbWidth, thumbQuality, agg, log, buckets[k], st.Shards[k], st.ShardSteps[k]); err != nil {
			return err
		}
	}
	mergeResult(result, agg)
	return nil
}

// runCardRange 卡片侧一段的执行（分片版）——按给定的文件子集与目标库跑。
func runCardRange(st *ScanRootState, seg ScanSegment, force bool, thumbWidth, thumbQuality int, result *ScanResult, log ScanLog, files []*ScanFile, db *store.DB, steps map[string]map[string]store.ScanStepRow) error {
	batch := 0
	total := len(st.Files)
	if err := db.Begin(); err != nil {
		return err
	}
	for i := range files {
		f := files[i]
		result.StepDone++
		if log != nil && result.StepDone%ProgressEvery == 0 {
			log("  " + seg.Label() + " " + itoaInt(result.StepDone) + "/" + itoaInt(total))
		}
		for _, step := range seg.Steps {
			if !force && stepDoneIn(steps, *f, step.Id) {
				result.Skipped++
				continue
			}
			ok, stepErr := runCardStep(db, st.Entry, f, step, thumbWidth, thumbQuality, result)
			if stepErr != nil {
				db.Rollback()
				return stepErr
			}
			if ok {
				if err := db.MarkScanStep(f.Path, step.Id, f.Size, f.Mtime); err != nil {
					db.Rollback()
					return err
				}
				result.Added++
			} else {
				result.Failed++
			}
		}
		batch++
		if batch >= ScanBatchSize {
			if err := db.Commit(); err != nil {
				return err
			}
			if err := db.Begin(); err != nil {
				return err
			}
			batch = 0
		}
	}
	return db.Commit()
}

// ProgressEvery 卡片侧进度出声的文件间隔（对齐 C# Scanner.ProgressEvery）。
const ProgressEvery = 5

// stepDoneIn 该文件该步骤是否已完成（给定完成戳索引版）——size + mtime 双等且步骤记录在案。
func stepDoneIn(index map[string]map[string]store.ScanStepRow, f ScanFile, stepID string) bool {
	if index == nil {
		return false
	}
	steps, ok := index[f.Path]
	if !ok {
		return false
	}
	rec, ok := steps[stepID]
	if !ok {
		return false
	}
	return rec.Size == f.Size && rec.Mtime == f.Mtime
}

// runCardStep 卡片侧单步执行——返回 true 表示该步对这一文件已完成（可落完成戳）。
func runCardStep(db *store.DB, root RootEntry, f *ScanFile, step ScanStepDef, thumbWidth, thumbQuality int, result *ScanResult) (bool, error) {
	// 非卡文件——头段步已登记进 non_card 清单，后续读步一律跳过
	if f.NonCardFile {
		return true, nil
	}
	switch step.Id {
	case "row":
		id, err := db.UpsertCardRow(f.Path, filepath.Base(f.Path), f.Size, f.Mtime, root.Tier, root.Path, f.Folder)
		if err != nil {
			return false, err
		}
		f.CardId = id
		return id > 0, nil
	case "head":
		return stepCardHead(db, f, result)
	case "refs":
		return stepCardRefs(db, f, result)
	case "name":
		return stepCardName(db, f, result)
	case "timeline":
		return stepCardTimeline(db, f, result)
	case "thumb":
		return stepCardThumb(db, f, thumbWidth, thumbQuality, result)
	case "coord", "detail", "scene":
		if ok, err := ensureHead(db, f, result); !ok || err != nil {
			return ok, err
		}
		if f.CardId <= 0 {
			id, err := db.CardIdOf(f.Path)
			if err != nil {
				return false, err
			}
			f.CardId = id
		}
		if f.CardId <= 0 {
			return false, nil
		}
		res, done, analysisErr := AnalyzeScanFile(f, step.Id)
		if analysisErr != nil {
			result.addError(filepath.Base(f.Path) + " → " + step.Id + "：" + analysisErr.Error())
			return false, nil
		}
		if !done {
			return false, nil
		}
		if res != nil {
			if err := db.SaveCardAnalysis(f.CardId, res.Kind, f.Path, f.Size, f.Mtime, res.JSON); err != nil {
				return false, err
			}
		}
		return true, nil
	default:
		return false, nil
	}
}

// stepCardHead 卡头段步——写卡类型 / 数据版本 / 图片区终点；非卡文件登记进清单并核销卡片行。
func stepCardHead(db *store.DB, f *ScanFile, result *ScanResult) (bool, error) {
	head := ReadCardHeadState(f.Path)
	if head.State == CardHeadIo {
		f.HeadFailed = true
		result.addError(filepath.Base(f.Path) + " 头段读取失败")
		return false, nil
	}
	if head.State != CardHeadOk {
		f.NonCardFile = true
		f.NonCardReason = head.Reason
		if err := db.DeleteCardByPath(f.Path); err != nil {
			return false, err
		}
		if err := db.UpsertNonCard(f.Path, f.RootPath, "card", head.Reason, f.Size, f.Mtime, buildNonCardThumb(f.Path, f.Size)); err != nil {
			return false, err
		}
		result.NonCard++
		return true, nil
	}
	if f.OldCardType != "" && f.OldCardType != head.CardType {
		result.TypesFixed++
	}
	f.CardType = head.CardType
	f.ImageEnd = head.ImageEnd
	f.HeadRead = true
	if err := db.DeleteNonCardByPath(f.Path); err != nil {
		return false, err
	}
	if err := db.UpdateCardHead(f.Path, head.CardType, head.DataVersion, head.ImageEnd); err != nil {
		return false, err
	}
	return true, nil
}

// 非卡文件缩略图参数（对齐 C# Scanner 同名常量）。
const (
	// nonCardThumbMaxBytes 非卡文件缩略图体积上限。
	nonCardThumbMaxBytes = 64 * 1024 * 1024
	// nonCardThumbWidth 非卡文件缩略图宽度（像素）。
	nonCardThumbWidth = 256
	// nonCardThumbQuality 非卡文件缩略图 JPEG 质量。
	nonCardThumbQuality = 82
)

// buildNonCardThumb 非卡文件的缩略图——只对可解码图片（且体积在上限内）生成；失败返回 nil。
func buildNonCardThumb(path string, size int64) []byte {
	if path == "" || size <= 0 || size > nonCardThumbMaxBytes {
		return nil
	}
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp":
	default:
		return nil
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	return ThumbnailFromBytes(raw, nonCardThumbWidth, nonCardThumbQuality)
}

// stepCardRefs 声明区步——取 mod 引用 + UAR 块数并落库。
func stepCardRefs(db *store.DB, f *ScanFile, result *ScanResult) (bool, error) {
	if ok, err := ensureHead(db, f, result); !ok || err != nil {
		return ok, err
	}
	if f.CardId <= 0 {
		id, err := db.CardIdOf(f.Path)
		if err != nil {
			return false, err
		}
		f.CardId = id
	}
	refs, uar := CollectCardRefs(f.Path, f.ImageEnd)
	guids := make([]string, 0, len(refs))
	for _, r := range refs {
		guids = append(guids, r.ModId)
	}
	if err := db.ReplaceCardRefs(f.CardId, guids); err != nil {
		return false, err
	}
	distinct := map[string]bool{}
	for _, g := range guids {
		if g != "" {
			distinct[g] = true
		}
	}
	if err := db.UpdateCardRefsMeta(f.Path, len(distinct), uar); err != nil {
		return false, err
	}
	result.RefEntries += int64(len(refs))
	return true, nil
}

// stepCardName 角色名步——人物卡读 Parameter 的姓 / 名（其余卡型直接算完成）。
func stepCardName(db *store.DB, f *ScanFile, result *ScanResult) (bool, error) {
	if ok, err := ensureHead(db, f, result); !ok || err != nil {
		return ok, err
	}
	if !strings.Contains(f.CardType, "Chara") {
		return true, nil
	}
	name := ReadCardName(f.Path)
	if err := db.UpdateCardName(f.Path, name); err != nil {
		return false, err
	}
	if name != "" {
		if f.IsNew {
			result.NamesRead++
		} else {
			result.NamesFilled++
		}
	}
	return true, nil
}

// stepCardTimeline 时间轴步——场景卡读 Timeline 条目（其余卡型直接算完成）。
func stepCardTimeline(db *store.DB, f *ScanFile, result *ScanResult) (bool, error) {
	if ok, err := ensureHead(db, f, result); !ok || err != nil {
		return ok, err
	}
	if f.CardType != SceneCardType {
		return true, nil
	}
	if f.CardId <= 0 {
		id, err := db.CardIdOf(f.Path)
		if err != nil {
			return false, err
		}
		f.CardId = id
	}
	tl := ReadTimeline(f.Path, f.ImageEnd)
	if tl.Error != "" {
		result.addError(filepath.Base(f.Path) + " timeline → " + tl.Error)
		return false, nil
	}
	values := &store.TimelineValues{
		HasEntry: tl.HasEntry, IsEmpty: tl.IsEmpty, Duration: tl.Duration, TimeScale: tl.TimeScale,
		Keyframes: tl.Keyframes, XmlLength: tl.XmlLength, HitStage: tl.HitStage, Error: tl.Error,
	}
	if err := db.SaveCardTimeline(f.CardId, f.Path, f.Size, f.Mtime, values); err != nil {
		return false, err
	}
	result.TimelineRead++
	return true, nil
}

// stepCardThumb 缩略图步——图片区取图并缩为 JPEG。
func stepCardThumb(db *store.DB, f *ScanFile, thumbWidth, thumbQuality int, result *ScanResult) (bool, error) {
	if ok, err := ensureHead(db, f, result); !ok || err != nil {
		return ok, err
	}
	if thumbWidth <= 0 {
		return true, nil
	}
	thumb := ThumbnailFromCard(f.Path, f.ImageEnd, thumbWidth, thumbQuality)
	if len(thumb) == 0 {
		reason := ThumbnailLastError()
		if reason == "" {
			reason = "未知原因"
		}
		result.addError(filepath.Base(f.Path) + " 缩略图 → " + reason)
		return false, nil
	}
	if err := db.UpdateCardThumb(f.Path, thumb); err != nil {
		return false, err
	}
	result.ThumbBytes += int64(len(thumb))
	return true, nil
}

// ensureHead 按需前置——本步需要图片区终点时，若本轮尚未解析则就地读头段（顺序自由的关键）。
func ensureHead(db *store.DB, f *ScanFile, result *ScanResult) (bool, error) {
	if f.HeadRead && f.ImageEnd > 0 {
		return true, nil
	}
	if f.HeadFailed {
		return false, nil
	}
	ok, err := stepCardHead(db, f, result)
	if err != nil || !ok {
		return false, err
	}
	if err = db.MarkScanStep(f.Path, "head", f.Size, f.Mtime); err != nil {
		return false, err
	}
	return f.ImageEnd > 0, nil
}

// cleanupCards 卡片侧清扫——删除磁盘上已消失的卡片行与步骤记录（分片常驻后逐片清扫）。
func cleanupCards(st *ScanRootState, result *ScanResult, log ScanLog) error {
	if st.EnumFailed || len(st.Files) == 0 {
		return nil
	}
	present := make(map[string]bool, len(st.Files))
	for _, f := range st.Files {
		present[f.Path] = true
	}
	targets := st.Shards
	if len(targets) == 0 && st.Store != nil {
		targets = []*store.DB{st.Store}
	}
	goneCards, goneSteps, goneNon := 0, 0, 0
	for _, db := range targets {
		if err := db.Begin(); err != nil {
			return err
		}
		cards, err := db.DeleteCardsMissingUnderRoot(st.Entry.Path, present)
		if err != nil {
			db.Rollback()
			return err
		}
		steps, err := db.DeleteScanStepsMissingUnderRoot(st.Entry.Path, present)
		if err != nil {
			db.Rollback()
			return err
		}
		non, err := db.DeleteNonCardsMissingUnderRoot(st.Entry.Path)
		if err != nil {
			db.Rollback()
			return err
		}
		if err = db.Commit(); err != nil {
			return err
		}
		goneCards += cards
		goneSteps += steps
		goneNon += non
	}
	if goneCards > 0 || goneSteps > 0 || goneNon > 0 {
		result.Removed += goneCards
		report(log, "  已清理 "+itoaInt(goneCards)+" 条已消失的卡片记录 · 步骤记录 "+itoaInt(goneSteps)+" 条 · 非卡登记 "+itoaInt(goneNon)+" 条")
	}
	return nil
}

// ScanCards 扫描卡片库（plan 为 nil 时用默认计划；only 非空时只扫该条库根；离线库一律跳过）。
// 对齐 C# Scanner.ScanCards(hub, cfg, only, scope, force, thumbWidth, thumbQuality, plan, log)。
func ScanCards(hub *store.Hub, cfg *RootsConfig, only *RootEntry, scope ScanScope, force bool, thumbWidth, thumbQuality int, plan *ScanPlan, log ScanLog) (*ScanResult, error) {
	start := time.Now()
	result := &ScanResult{}
	if plan == nil {
		plan = DefaultScanPlan()
	}
	states, err := CollectCardRoots(hub, cfg, only, scope, result, log)
	if err != nil {
		return nil, err
	}
	segs := SegmentsFor(plan, false)
	result.StepCount = len(segs)
	index := 0
	for _, seg := range segs {
		index++
		result.StepIndex = index
		result.StepName = seg.Label()
		result.StepDone = 0
		result.StepTotal = totalFiles(states)
		report(log, fmt.Sprintf("步骤 %d/%d：%s（%d 个文件）", index, len(segs), seg.Label(), result.StepTotal))
		for i := range states {
			if err = runCardSegment(hub, &states[i], seg, force, thumbWidth, thumbQuality, result, log); err != nil {
				return nil, err
			}
		}
	}
	for i := range states {
		if err = cleanupCards(&states[i], result, log); err != nil {
			return nil, err
		}
	}
	if err = applyOfflineMarks(hub, cfg, result, false, log); err != nil {
		return nil, err
	}
	result.Elapsed = time.Since(start)
	return result, nil
}

// ---------- 统一扫描（scan-main / scan-extra） ----------

// PreludeStepName 先导段步骤名（面板进度行显示）。
const PreludeStepName = "人物卡主库先导（只建行 · 不含子文件夹）"

// preludeBatchSize 先导段的小批量提交间隔（让面板轮询时能看见卡片陆续出现）。
const preludeBatchSize = 8

// findFemaleRoot 取人物卡主库那条库根状态（按推荐子路径后缀认定；找不到返回 nil）。
func findFemaleRoot(states []ScanRootState) *ScanRootState {
	suffix := strings.ReplaceAll(CardFemaleSub, "/", `\`)
	for i := range states {
		if states[i].Entry.Tier != TierMain {
			continue
		}
		path := strings.TrimRight(states[i].Entry.Path, `\/`)
		if len(path) >= len(suffix) && strings.EqualFold(path[len(path)-len(suffix):], suffix) {
			return &states[i]
		}
	}
	return nil
}

// preludeFiles 先导段文件清单——只取该库根本目录下的文件（不含子文件夹）。
func preludeFiles(st *ScanRootState) []*ScanFile {
	list := make([]*ScanFile, 0, len(st.Files))
	for _, f := range st.Files {
		if f.Folder == "" {
			list = append(list, f)
		}
	}
	return list
}

// runPrelude 先导段——人物卡主库本目录先只建「卡片行」，按修改时间倒序、小批量提交。
func runPrelude(hub *store.Hub, st *ScanRootState, files []*ScanFile, force bool, result *ScanResult, log ScanLog) error {
	sort.SliceStable(files, func(i, j int) bool {
		if files[i].Mtime != files[j].Mtime {
			return files[i].Mtime > files[j].Mtime
		}
		return strings.ToLower(files[i].Path) < strings.ToLower(files[j].Path)
	})
	if err := ensureShards(hub, st, len(st.Files), log); err != nil {
		return err
	}
	rowDef := FindScanStep("row")
	if rowDef == nil {
		return nil
	}
	shards := len(st.Shards)
	if shards == 0 {
		return nil
	}
	total := len(files)
	done := 0
	batches := make([]int, shards)
	for _, db := range st.Shards {
		if err := db.Begin(); err != nil {
			return err
		}
	}
	rollbackAll := func() {
		for _, db := range st.Shards {
			db.Rollback()
		}
	}
	for _, f := range files {
		done++
		result.StepDone = done
		if log != nil && done%ProgressEvery == 0 {
			log("  " + PreludeStepName + " " + itoaInt(done) + "/" + itoaInt(total))
		}
		k := store.ShardOfPath(f.Path, shards)
		db := st.Shards[k]
		if !force && stepDoneIn(st.ShardSteps[k], *f, "row") {
			result.Skipped++
			continue
		}
		ok, err := runCardStep(db, st.Entry, f, *rowDef, 0, 0, result)
		if err != nil {
			rollbackAll()
			return err
		}
		if ok {
			if err = db.MarkScanStep(f.Path, "row", f.Size, f.Mtime); err != nil {
				rollbackAll()
				return err
			}
			result.Added++
		} else {
			result.Failed++
		}
		batches[k]++
		if batches[k] >= preludeBatchSize {
			if err = db.Commit(); err != nil {
				rollbackAll()
				return err
			}
			if err = db.Begin(); err != nil {
				return err
			}
			batches[k] = 0
		}
	}
	for _, db := range st.Shards {
		if err := db.Commit(); err != nil {
			return err
		}
	}
	return nil
}

// countPluginDlls 插件库根下的 dll 总数（进度分母——枚举失败按已得计数并出声）。
func countPluginDlls(cfg *RootsConfig, result *ScanResult) int {
	n := 0
	for _, root := range cfg.PluginRoots {
		if strings.TrimSpace(root.Path) == "" {
			continue
		}
		if info, err := os.Stat(root.Path); err != nil || !info.IsDir() {
			continue
		}
		files, failed := enumerateFiles(root.Path, "*.dll", root.Recurse, nil)
		if failed {
			result.addError("插件库枚举失败（" + root.Path + "）")
		}
		n += len(files)
	}
	return n
}

// PluginProgressEvery 插件库进度出声间隔（对齐 C# Scanner.PluginProgressEvery）。
const PluginProgressEvery = 50

// oldPluginStamp 旧插件行的增量判据索引（一个 dll 多行时取首行——各行 size / mtime 相同）。
type oldPluginStamp struct {
	// path 原始文件路径（保大小写——清理时按它删行）。
	path string
	// size 登记时的文件字节数。
	size int64
	// mtime 登记时的修改时间戳（UTC 文本）。
	mtime string
}

// ScanPlugins 扫描插件库根下的全部 dll 与配置文件并落库（只读解析，不改动任何文件）。
// 对齐 C# Scanner.ScanPlugins：旧戳索引（size + mtime 双等跳解析）→ 清理已消失的 dll →
// 逐 dll 解析落库（未解析出插件特性也落一行 guid 空的，让「装了但不是插件」可见）→ cfg 全量重读。
func ScanPlugins(db *store.DB, cfg *RootsConfig, result *ScanResult, log ScanLog) (dlls, plugins, configs int) {
	if cfg == nil || db == nil {
		return 0, 0, 0
	}
	old, loadErr := db.LoadPlugins()
	if loadErr != nil {
		result.addError("插件库旧行读取失败：" + loadErr.Error())
		return 0, 0, 0
	}
	for _, root := range cfg.PluginRoots {
		if strings.TrimSpace(root.Path) == "" {
			result.addError("插件库根不存在：" + root.Path)
			continue
		}
		if info, statErr := os.Stat(root.Path); statErr != nil || !info.IsDir() {
			result.addError("插件库根不存在：" + root.Path)
			continue
		}
		report(log, "插件库 · "+root.Path)
		files, enumFailed := enumerateFiles(root.Path, "*.dll", root.Recurse, log)
		if enumFailed {
			result.addError("插件库枚举失败（" + root.Path + "）")
			continue
		}
		dlls += len(files)
		// [段1] 建本次清单与旧戳索引——未变的 dll 跳过解析（增量判据 = size + mtime 双等）
		keep := make(map[string]bool, len(files))
		for _, f := range files {
			keep[strings.ToLower(f)] = true
		}
		oldIndex := make(map[string]oldPluginStamp, len(old))
		for i := range old {
			row := &old[i]
			if !strings.EqualFold(row.RootPath, root.Path) {
				continue
			}
			key := strings.ToLower(row.FilePath)
			if _, ok := oldIndex[key]; !ok {
				oldIndex[key] = oldPluginStamp{path: row.FilePath, size: row.Size, mtime: row.Mtime}
			}
		}
		// [段2] 清理已消失的 dll——本次清单里没有的旧行直接删
		for _, st := range oldIndex {
			if keep[strings.ToLower(st.path)] {
				continue
			}
			if err := db.DeletePluginFile(st.path); err != nil {
				result.addError("插件行清理失败（" + st.path + "）：" + err.Error())
			}
		}
		// [段3] 逐 dll 解析（C# 侧并行、落库串行；Go 串行）——未变的跳过
		done := 0
		total := len(files)
		for _, filePath := range files {
			done++
			if log != nil && (done%PluginProgressEvery == 0 || done == total) {
				log("  插件库 " + itoaInt(done) + "/" + itoaInt(total))
			}
			var size int64
			var mtime string
			if info, statErr := os.Stat(filePath); statErr == nil {
				size = info.Size()
				mtime = pluginFileStamp(info.ModTime())
			}
			if st, ok := oldIndex[strings.ToLower(filePath)]; ok && st.size == size && st.mtime == mtime {
				continue
			}
			list, meta, readErr := ReadPluginDll(filePath)
			if err := db.DeletePluginFile(filePath); err != nil {
				result.addError("插件行清理失败（" + filePath + "）：" + err.Error())
			}
			if len(list) == 0 {
				// 未解析出插件特性（原生 dll / 无特性）——仍落一行（guid 空），让「装了但不是插件」可见
				bad := &store.PluginRow{
					FilePath: filePath, RootPath: root.Path, FileName: filepath.Base(filePath),
					Size: size, Mtime: mtime, Note: readErr,
					Title: meta.Title, Description: meta.Description, Company: meta.Company,
					Copyright: meta.Copyright, Product: meta.Product, FileVersion: meta.FileVersion,
					TargetFramework: meta.TargetFramework,
				}
				if err := db.SavePlugin(bad); err != nil {
					result.addError("插件行写入失败（" + filePath + "）：" + err.Error())
				}
				continue
			}
			for i := range list {
				info := &list[i]
				row := &store.PluginRow{
					FilePath: filePath, Guid: info.Guid, RootPath: root.Path, FileName: info.FileName,
					Name: info.Name, Version: info.Version,
					Processes:    strings.Join(info.Processes, ","),
					Dependencies: strings.Join(info.Dependencies, ","),
					IsIpa:        info.IsIpa, Size: size, Mtime: mtime,
					Title: info.Title, Description: info.Description, Company: info.Company,
					Copyright: info.Copyright, Product: info.Product, FileVersion: info.FileVersion,
					TargetFramework: info.TargetFramework,
				}
				if err := db.SavePlugin(row); err != nil {
					result.addError("插件行写入失败（" + filePath + "）：" + err.Error())
				}
			}
		}
		// [段4] 配置文件全量读取——config/*.cfg 逐个解析（分节 / 选项 / 作者注释）落库；
		// cfg 数量有限，整段重读不留增量判据——改了立刻反映，不会读到半新半旧
		if err := db.DeletePluginConfigsUnderRoot(root.Path); err != nil {
			result.addError("配置行清理失败（" + root.Path + "）：" + err.Error())
		}
		cfgErrors := make([]string, 0, 4)
		cfgs := ReadPluginConfigDir(root.Path, &cfgErrors)
		for _, e := range cfgErrors {
			result.addError(e)
		}
		for i := range cfgs {
			cf := &cfgs[i]
			row := &store.PluginConfigRow{
				FilePath: cf.FilePath, FileName: cf.FileName, PluginName: cf.PluginName,
				PluginVersion: cf.PluginVersion, Guid: cf.Guid, Size: cf.Size, Mtime: cf.Mtime,
				Sections: ToPluginConfigJSON(cf), Error: cf.Error,
			}
			var sections, options int64
			for k := range cf.Sections {
				sections++
				options += int64(len(cf.Sections[k].Options))
			}
			row.SectionCount = sections
			row.OptionCount = options
			if err := db.SavePluginConfig(row); err != nil {
				result.addError("配置行写入失败（" + cf.FilePath + "）：" + err.Error())
			}
		}
		configs += len(cfgs)
	}
	rows, err := db.LoadPlugins()
	if err != nil {
		result.addError("插件行回读失败：" + err.Error())
		return dlls, 0, configs
	}
	for i := range rows {
		if rows[i].Guid != "" {
			plugins++
		}
	}
	return dlls, plugins, configs
}

// ScanAll 统一编排入口——按计划的段顺序执行（跨侧交错），收尾做清扫 / 插件段 / 离线标记 / 作者索引。
// 对齐 C# Scanner.ScanAll（先导段 + 插件段）。
func ScanAll(hub *store.Hub, cfg *RootsConfig, only *RootEntry, scope ScanScope, force bool, thumbWidth, thumbQuality int, plan *ScanPlan, log ScanLog) (*ScanResult, error) {
	start := time.Now()
	result := &ScanResult{}
	if plan == nil {
		plan = DefaultScanPlan()
	}
	modStates, err := CollectModRoots(hub, cfg, only, scope, result, log)
	if err != nil {
		return nil, err
	}
	cardStates, err := CollectCardRoots(hub, cfg, only, scope, result, log)
	if err != nil {
		return nil, err
	}
	segs := plan.Segments()
	withPlugins := scope == ScanScopePreset && only == nil
	var preludeRoot *ScanRootState
	var preludeList []*ScanFile
	if only == nil {
		preludeRoot = findFemaleRoot(cardStates)
		if preludeRoot != nil {
			preludeList = preludeFiles(preludeRoot)
		}
	}
	result.StepCount = len(segs)
	if withPlugins {
		result.StepCount++
	}
	if preludeRoot != nil {
		result.StepCount++
	}
	index := 0
	if preludeRoot != nil {
		index = 1
		result.StepIndex = index
		result.StepName = PreludeStepName
		result.StepTotal = len(preludeList)
		result.StepDone = 0
		report(log, fmt.Sprintf("步骤 %d/%d：%s（%d 个文件）", index, result.StepCount, PreludeStepName, len(preludeList)))
		if err = runPrelude(hub, preludeRoot, preludeList, force, result, log); err != nil {
			return nil, err
		}
	}
	for _, seg := range segs {
		index++
		result.StepIndex = index
		result.StepName = seg.Label()
		var states []ScanRootState
		if seg.Mods {
			states = modStates
		} else {
			states = cardStates
		}
		result.StepTotal = totalFiles(states)
		result.StepDone = 0
		report(log, fmt.Sprintf("步骤 %d/%d：%s（%d 个文件）", index, result.StepCount, seg.Label(), result.StepTotal))
		for i := range states {
			if seg.Mods {
				if err = runModSegment(hub, cfg, &states[i], seg, force, result, log); err != nil {
					return nil, err
				}
			} else if err = runCardSegment(hub, &states[i], seg, force, thumbWidth, thumbQuality, result, log); err != nil {
				return nil, err
			}
		}
	}
	for i := range cardStates {
		if err = cleanupCards(&cardStates[i], result, log); err != nil {
			return nil, err
		}
	}
	for i := range modStates {
		if err = cleanupMods(hub, cfg, &modStates[i], result, log); err != nil {
			return nil, err
		}
	}
	if withPlugins {
		index++
		result.StepIndex = index
		result.StepName = "插件库（dll 元数据 + 配置文件）"
		result.StepTotal = countPluginDlls(cfg, result)
		result.StepDone = 0
		report(log, fmt.Sprintf("步骤 %d/%d：插件库（dll 元数据 + 配置文件）（%d 个文件）", index, result.StepCount, result.StepTotal))
		dlls, plugins, configs := ScanPlugins(hub.Core(), cfg, result, log)
		result.PluginDlls = dlls
		result.Plugins = plugins
		result.PluginConfigs = configs
	}
	if err = applyOfflineMarks(hub, cfg, result, false, log); err != nil {
		return nil, err
	}
	if err = applyOfflineMarks(hub, cfg, result, true, log); err != nil {
		return nil, err
	}
	authors, authorErr := hub.Core().RefreshModAuthors()
	if authorErr != nil {
		return nil, authorErr
	}
	report(log, fmt.Sprintf("作者索引已重建：%d 位作者", authors))
	result.Elapsed = time.Since(start)
	return result, nil
}
