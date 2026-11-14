// 旧版管理与版本搬运（重复副本 / 按作者整理共用）——对齐 C# Data/StoreHub.cs
// §MarkOld / §MarkOldOthers / §KeepVersion / §SwapVersion / §PromoteOld 与
// §MoveModFile / §RenameModFilePath / §FindModFileRecord / §AddModFileCopy。
//
// 分层：本包负责跨库编排（AllStoreRefs / RecomputeMod），store 只做单库读写。
// 返回约定：C# 用「返回 null = 成功 + out detail」；Go 侧统一为 (detail string, err error)——
// err == nil 即成功，detail 为面向使用者的说明。
package core

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"kkgo/internal/store"
)

// FileBusyHint 文件操作失败的统一说明——补上「谁可能占用」与「怎么办」（失败可执行化，不让人猜）。
func FileBusyHint(err error) string {
	if err == nil {
		return ""
	}
	return "（文件被其他程序占用——游戏运行中 / 资源管理器正在预览 / 杀毒软件扫描都会占用；关闭游戏或稍后重试）：" + err.Error()
}

// MakeOldFileName 把文件名标成旧版形态（扩展名前加 .old）；已是旧版形态则原样返回。
func MakeOldFileName(fileName string) string {
	if fileName == "" || IsOldFileName(fileName) {
		return fileName
	}
	ext := filepath.Ext(fileName)
	stem := strings.TrimSuffix(fileName, ext)
	return stem + OldInfix + ext
}

// StripOldFileName 把旧版文件名正名（去掉扩展名前的 .old 段）；不带旧版中缀则原样返回。
func StripOldFileName(fileName string) string {
	if !IsOldFileName(fileName) {
		return fileName
	}
	ext := filepath.Ext(fileName)
	stem := strings.TrimSuffix(fileName, ext)
	return stem[:len(stem)-len(OldInfix)] + ext
}

// FindModRoot 取配置里某级别的第一条 mod 库根（无则 nil）。
func FindModRoot(cfg *RootsConfig, tier int) *RootEntry {
	if cfg == nil {
		return nil
	}
	for i := range cfg.ModRoots {
		if cfg.ModRoots[i].Tier == tier {
			return &cfg.ModRoots[i]
		}
	}
	return nil
}

// allDBs 全部参与合并的库句柄（RecomputeMod 用）。
func allDBs(hub *store.Hub, cfg *RootsConfig) []*store.DB {
	refs := AllStoreRefs(hub, cfg)
	list := make([]*store.DB, 0, len(refs))
	for _, r := range refs {
		list = append(list, r.DB)
	}
	return list
}

// FindModFileRec 跨库按路径找一条副本记录（路径比较不区分大小写）——找不到返回 nil。
func FindModFileRec(hub *store.Hub, cfg *RootsConfig, guid, filePath string) *store.ModFileRec {
	if guid == "" || filePath == "" {
		return nil
	}
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryModFileRecsByGuid(guid)
		if err != nil {
			continue
		}
		for i := range rows {
			if strings.EqualFold(rows[i].FilePath, filePath) {
				rec := rows[i]
				return &rec
			}
		}
	}
	return nil
}

// MainCopies 某 guid 在主库的副本（nonOldOnly 为真时排除文件名带 .old 的），按级别 + 文件名排序。
func MainCopies(hub *store.Hub, cfg *RootsConfig, guid string, nonOldOnly bool) []store.ModFileRec {
	list := []store.ModFileRec{}
	if guid == "" {
		return list
	}
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryModFileRecsByGuid(guid)
		if err != nil {
			continue
		}
		for _, r := range rows {
			if r.Tier != TierMain {
				continue
			}
			if nonOldOnly && IsOldFileName(r.FileName) {
				continue
			}
			list = append(list, r)
		}
	}
	CompareFileByTierList(list)
	return list
}

// CompareFileByTierList 副本按级别排序（同级别按文件名）——原地排序。
func CompareFileByTierList(list []store.ModFileRec) {
	// 规模小（一个 guid 的副本），插入排序足够且稳定。
	for i := 1; i < len(list); i++ {
		for j := i; j > 0; j-- {
			a, b := list[j-1], list[j]
			if a.Tier < b.Tier || (a.Tier == b.Tier && strings.ToLower(a.FileName) <= strings.ToLower(b.FileName)) {
				break
			}
			list[j-1], list[j] = list[j], list[j-1]
		}
	}
}

// HasLiveCopyBesides 某 guid 除指定文件外是否还有非旧版形态的副本（正名判据：无则说明新版已不在）。
func HasLiveCopyBesides(hub *store.Hub, cfg *RootsConfig, guid, filePath string) bool {
	if guid == "" {
		return false
	}
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryModFileRecsByGuid(guid)
		if err != nil {
			continue
		}
		for _, r := range rows {
			if strings.EqualFold(r.FilePath, filePath) {
				continue
			}
			if !IsOldFileName(r.FileName) {
				return true
			}
		}
	}
	return false
}

// MoveModFileHub 把一条 mod 副本从源库搬到目标库根（或复制到目标库根），并重算 mod 主表。
// newFileName 非空时改写文件名（旧版标记 / 正名用）。found 为假 = 源副本行不存在。
func MoveModFileHub(hub *store.Hub, cfg *RootsConfig, guid, srcPath, srcRootPath, destPath string, toTier int, targetRootPath, newFileName string) (bool, error) {
	srcLib, err := hub.LibOf(srcRootPath)
	if err != nil {
		return false, err
	}
	dstLib, err := hub.LibOf(targetRootPath)
	if err != nil {
		return false, err
	}
	src, err := hub.StoreByLib(srcLib)
	if err != nil {
		return false, err
	}
	dst, err := hub.StoreByLib(dstLib)
	if err != nil {
		return false, err
	}
	rec, err := src.RemoveModFileRow(srcPath)
	if err != nil {
		return false, err
	}
	if rec == nil {
		return false, nil
	}
	if newFileName != "" {
		rec.FileName = newFileName
	}
	if _, err = dst.InsertModFileRow(rec, toTier, targetRootPath, destPath); err != nil {
		return false, err
	}
	if err = hub.RecomputeMod(allDBs(hub, cfg), guid); err != nil {
		return false, err
	}
	return true, nil
}

// AddModFileCopyHub 登记一条复制出来的 mod 副本（源行保留——冷冻库搬运），并重算 mod 主表。
func AddModFileCopyHub(hub *store.Hub, cfg *RootsConfig, guid, srcRootPath, srcPath, destPath string, toTier int, targetRootPath, newFileName string) (bool, error) {
	rec := FindModFileRec(hub, cfg, guid, srcPath)
	if rec == nil {
		return false, nil
	}
	copied := *rec
	if newFileName != "" {
		copied.FileName = newFileName
	}
	dstLib, err := hub.LibOf(targetRootPath)
	if err != nil {
		return false, err
	}
	dst, err := hub.StoreByLib(dstLib)
	if err != nil {
		return false, err
	}
	if _, err = dst.InsertModFileRow(&copied, toTier, targetRootPath, destPath); err != nil {
		return false, err
	}
	if err = hub.RecomputeMod(allDBs(hub, cfg), guid); err != nil {
		return false, err
	}
	return true, nil
}

// moveOrCopy 源只读则复制、否则移动（对齐 C# 的 readOnlySource 分支）。
func moveOrCopy(src, dest string, readOnlySource bool) error {
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		return err
	}
	if readOnlySource {
		in, err := os.Open(src)
		if err != nil {
			return err
		}
		defer in.Close()
		out, err := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
		if err != nil {
			return err
		}
		_, copyErr := copyStream(out, in)
		closeErr := out.Close()
		if copyErr != nil {
			os.Remove(dest)
			return copyErr
		}
		if closeErr != nil {
			os.Remove(dest)
			return closeErr
		}
		return nil
	}
	return os.Rename(src, dest)
}

// markOldRecord 落一条旧版登记（版本号实时读 manifest）。
func markOldRecord(hub *store.Hub, guid, oldPath, newName, newVersion string) {
	p := ParseZipMod(oldPath)
	oldVersion := ""
	if p != nil {
		oldVersion = p.Version
	}
	_ = hub.Core().SetModOld(store.ModOldRecord{
		OldPath:    oldPath,
		Guid:       guid,
		OldName:    filepath.Base(oldPath),
		OldVersion: oldVersion,
		NewName:    newName,
		NewVersion: newVersion,
		MarkedAt:   nowStampUTC(),
	})
}

// MarkOldHub 把某个 guid 的一份主库副本判为旧版——改名加 .old 段并移到缓存库，落一条旧版登记。
func MarkOldHub(hub *store.Hub, cfg *RootsConfig, guid, filePath string) (string, error) {
	if guid == "" || filePath == "" {
		return "", fmt.Errorf("缺少 guid 或文件路径")
	}
	cacheRoot := FindModRoot(cfg, TierCache)
	if cacheRoot == nil {
		return "", fmt.Errorf("未配置缓存库（级别 2）的 mod 库根")
	}
	rec := FindModFileRec(hub, cfg, guid, filePath)
	if rec == nil {
		return "", fmt.Errorf("数据库里没有这条副本行（需重扫核对）：%s", filePath)
	}
	if rec.Tier != TierMain {
		return "", fmt.Errorf("只能把主库的副本判为旧版（该副本在%s）", TierName(rec.Tier))
	}
	var keep *store.ModFileRec
	for _, m := range MainCopies(hub, cfg, guid, true) {
		if !strings.EqualFold(m.FilePath, filePath) {
			one := m
			keep = &one
			break
		}
	}
	if keep == nil {
		return "", fmt.Errorf("主库只剩这一份——判为旧版会让主库没有该 mod；请用「换用此版本」把另一份换上来")
	}
	if !fileExists(filePath) {
		return "", fmt.Errorf("文件不存在：%s", filePath)
	}
	newName := MakeOldFileName(filepath.Base(filePath))
	dest := filepath.Join(cacheRoot.Path, newName)
	if fileExists(dest) {
		return "", fmt.Errorf("缓存库已有同名文件：%s", dest)
	}
	keepInfo := ParseZipMod(keep.FilePath)
	keepVersion := ""
	if keepInfo != nil {
		keepVersion = keepInfo.Version
	}
	if err := os.MkdirAll(cacheRoot.Path, 0o755); err != nil {
		return "", fmt.Errorf("文件操作失败%s", FileBusyHint(err))
	}
	if err := os.Rename(filePath, dest); err != nil {
		return "", fmt.Errorf("文件操作失败%s", FileBusyHint(err))
	}
	found, err := MoveModFileHub(hub, cfg, guid, filePath, rec.RootPath, dest, TierCache, cacheRoot.Path, newName)
	if err != nil {
		return "", fmt.Errorf("文件已移动但数据库更新失败：%s", err.Error())
	}
	if !found {
		return "", fmt.Errorf("已移动但数据库未找到源副本行：%s（需重扫核对）", filePath)
	}
	p := ParseZipMod(dest)
	oldVersion := ""
	if p != nil {
		oldVersion = p.Version
	}
	_ = hub.Core().SetModOld(store.ModOldRecord{
		OldPath:    dest,
		Guid:       guid,
		OldName:    newName,
		OldVersion: oldVersion,
		NewName:    keep.FileName,
		NewVersion: keepVersion,
		MarkedAt:   nowStampUTC(),
	})
	return dest, nil
}

// DemoteMove 一条判旧后的位置变更（按作者整理要跟着改指计划条目）。
type DemoteMove struct {
	// OldPath 原路径。
	OldPath string
	// NewPath 新路径。
	NewPath string
	// NewName 新文件名。
	NewName string
	// RootPath 新库根。
	RootPath string
	// Tier 新级别。
	Tier int
}

// MarkOldOthersHub 判旧其余副本——除保留份外，同 guid 的非旧版副本加 .old 段
// （主库那份移入缓存库、缓存库那份就地改名）并落新旧版本登记；
// promoteKeep 为真时保留份搬入主库并正名（重复副本口径），为假时保留份留在原位（按作者整理口径）。
func MarkOldOthersHub(hub *store.Hub, cfg *RootsConfig, guid, keepFilePath string, promoteKeep bool, moves *[]DemoteMove) (string, error) {
	if guid == "" || keepFilePath == "" {
		return "", fmt.Errorf("缺少 guid 或文件路径")
	}
	var mainRoot *RootEntry
	if promoteKeep {
		mainRoot = FindModRoot(cfg, TierMain)
		if mainRoot == nil {
			return "", fmt.Errorf("未配置主库（级别 1）的 mod 库根")
		}
	}
	cacheRoot := FindModRoot(cfg, TierCache)
	if cacheRoot == nil {
		return "", fmt.Errorf("未配置缓存库（级别 2）的 mod 库根")
	}
	keep := FindModFileRec(hub, cfg, guid, keepFilePath)
	if keep == nil {
		return "", fmt.Errorf("数据库里没有这条副本行（需重扫核对）：%s", keepFilePath)
	}
	if !fileExists(keepFilePath) {
		return "", fmt.Errorf("文件不存在：%s", keepFilePath)
	}
	all := []store.ModFileRec{}
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryModFileRecsByGuid(guid)
		if err != nil {
			continue
		}
		all = append(all, rows...)
	}
	CompareFileByTierList(all)
	keepInfo := ParseZipMod(keepFilePath)
	keepVersion := ""
	if keepInfo != nil {
		keepVersion = keepInfo.Version
	}
	keepName := StripOldFileName(filepath.Base(keepFilePath))

	demoted := 0
	skipped := []string{}
	for _, r := range all {
		if strings.EqualFold(r.FilePath, keepFilePath) {
			continue
		}
		if IsOldFileName(r.FileName) {
			continue
		}
		if r.Tier == TierCold {
			skipped = append(skipped, "冷冻库副本保持原样（只读库不改动）："+r.FilePath)
			continue
		}
		if !fileExists(r.FilePath) {
			skipped = append(skipped, "文件不存在："+r.FilePath)
			continue
		}
		oldName := MakeOldFileName(filepath.Base(r.FilePath))
		oldDir := filepath.Dir(r.FilePath)
		if r.Tier == TierMain {
			oldDir = cacheRoot.Path
		}
		dest := filepath.Join(oldDir, oldName)
		if fileExists(dest) {
			skipped = append(skipped, "目标已有同名文件："+dest)
			continue
		}
		if err := os.MkdirAll(oldDir, 0o755); err != nil {
			skipped = append(skipped, "文件操作失败"+FileBusyHint(err)+"："+r.FilePath)
			continue
		}
		if err := os.Rename(r.FilePath, dest); err != nil {
			skipped = append(skipped, "文件操作失败"+FileBusyHint(err)+"："+r.FilePath)
			continue
		}
		found, err := MoveModFileHub(hub, cfg, guid, r.FilePath, r.RootPath, dest, TierCache, oldDir, oldName)
		if err != nil || !found {
			skipped = append(skipped, "文件已移动但数据库未找到源副本行："+r.FilePath)
			continue
		}
		p := ParseZipMod(dest)
		oldVersion := ""
		if p != nil {
			oldVersion = p.Version
		}
		_ = hub.Core().SetModOld(store.ModOldRecord{
			OldPath:    dest,
			Guid:       guid,
			OldName:    oldName,
			OldVersion: oldVersion,
			NewName:    keepName,
			NewVersion: keepVersion,
			MarkedAt:   nowStampUTC(),
		})
		if moves != nil {
			*moves = append(*moves, DemoteMove{OldPath: r.FilePath, NewPath: dest, NewName: oldName, RootPath: oldDir, Tier: TierCache})
		}
		demoted++
	}

	keepMoved := false
	if promoteKeep {
		if keep.Tier == TierMain {
			if IsOldFileName(keep.FileName) {
				renamed := filepath.Join(filepath.Dir(keep.FilePath), keepName)
				if fileExists(renamed) {
					return "", fmt.Errorf("主库已有同名文件（正名会撞名）：%s", renamed)
				}
				if err := os.Rename(keep.FilePath, renamed); err != nil {
					return "", fmt.Errorf("保留份正名失败%s · %s", FileBusyHint(err), keep.FilePath)
				}
				found, err := MoveModFileHub(hub, cfg, guid, keep.FilePath, keep.RootPath, renamed, TierMain, keep.RootPath, keepName)
				if err != nil || !found {
					return "", fmt.Errorf("保留份已正名但数据库未找到源副本行：%s", keep.FilePath)
				}
				_ = hub.Core().DeleteModOld(keep.FilePath)
				keepMoved = true
			}
		} else {
			dest := filepath.Join(mainRoot.Path, keepName)
			if fileExists(dest) {
				return "", fmt.Errorf("主库已有同名文件：%s", dest)
			}
			readOnlySource := IsReadOnlyRoot(cfg, keep.RootPath)
			if err := moveOrCopy(keep.FilePath, dest, readOnlySource); err != nil {
				return "", fmt.Errorf("保留份搬入主库失败%s", FileBusyHint(err))
			}
			var found bool
			var err error
			if readOnlySource {
				found, err = AddModFileCopyHub(hub, cfg, guid, keep.RootPath, keep.FilePath, dest, TierMain, mainRoot.Path, keepName)
			} else {
				found, err = MoveModFileHub(hub, cfg, guid, keep.FilePath, keep.RootPath, dest, TierMain, mainRoot.Path, keepName)
			}
			if err != nil || !found {
				return "", fmt.Errorf("保留份已搬移但数据库未找到源副本行：%s", keep.FilePath)
			}
			_ = hub.Core().DeleteModOld(keep.FilePath)
			keepMoved = true
		}
	}

	version := keepVersion
	if version == "" {
		version = "<无>"
	}
	detail := "保留 " + keepName + "（版本 " + version + "）· 判旧版 " + itoaSmall64(int64(demoted)) + " 份"
	if keepMoved {
		detail += " · 保留份已入主库"
	}
	if len(skipped) > 0 {
		detail += " · 跳过 " + itoaSmall64(int64(len(skipped))) + " 份：" + strings.Join(skipped, "；")
	}
	return detail, nil
}

// KeepVersionHub 指定保留版本——该副本成为主库当前版本（不在主库则搬入并正名），同 guid 其余非旧版副本判为旧版。
func KeepVersionHub(hub *store.Hub, cfg *RootsConfig, guid, filePath string) (string, error) {
	return MarkOldOthersHub(hub, cfg, guid, filePath, true, nil)
}

// SwapVersionHub 把一份旧版与主库当前版本完全互换（位置 + 名字）——选错版本时的纠正路径。
func SwapVersionHub(hub *store.Hub, cfg *RootsConfig, guid, oldFilePath string) (string, error) {
	if guid == "" || oldFilePath == "" {
		return "", fmt.Errorf("缺少 guid 或文件路径")
	}
	oldRec := FindModFileRec(hub, cfg, guid, oldFilePath)
	if oldRec == nil {
		return "", fmt.Errorf("数据库里没有这条副本行（需重扫核对）：%s", oldFilePath)
	}
	if oldRec.Tier == TierMain {
		return "", fmt.Errorf("要互换的那一份当前在主库——互换的另一方必须在主库之外")
	}
	if !IsOldFileName(oldRec.FileName) {
		return "", fmt.Errorf("该副本文件名不带 .old——它不是登记过的旧版")
	}
	mains := MainCopies(hub, cfg, guid, true)
	if len(mains) == 0 {
		return "", fmt.Errorf("主库没有该 mod 的版本——用「正名搬入主库」")
	}
	mainRec := mains[0]
	if !fileExists(mainRec.FilePath) {
		return "", fmt.Errorf("主库文件不存在：%s", mainRec.FilePath)
	}
	if !fileExists(oldFilePath) {
		return "", fmt.Errorf("旧版文件不存在：%s", oldFilePath)
	}
	mainDir := filepath.Dir(mainRec.FilePath)
	oldDir := filepath.Dir(oldFilePath)
	mainOldName := MakeOldFileName(filepath.Base(mainRec.FilePath))
	mainOldPath := filepath.Join(mainDir, mainOldName)
	promotedName := StripOldFileName(filepath.Base(oldFilePath))
	promotedPath := filepath.Join(mainDir, promotedName)
	if fileExists(mainOldPath) {
		return "", fmt.Errorf("主库已存在同名旧版文件：%s", mainOldPath)
	}
	if fileExists(promotedPath) {
		return "", fmt.Errorf("主库已存在同名文件（正名后会撞名）：%s", promotedPath)
	}
	mainInfo := ParseZipMod(mainRec.FilePath)
	oldInfo := ParseZipMod(oldFilePath)
	mainVersion := ""
	if mainInfo != nil {
		mainVersion = mainInfo.Version
	}
	oldVersion := ""
	if oldInfo != nil {
		oldVersion = oldInfo.Version
	}

	if err := os.Rename(mainRec.FilePath, mainOldPath); err != nil {
		return "", fmt.Errorf("主库改名失败%s · %s", FileBusyHint(err), mainRec.FilePath)
	}
	readOnlySource := IsReadOnlyRoot(cfg, oldRec.RootPath)
	if err := moveOrCopy(oldFilePath, promotedPath, readOnlySource); err != nil {
		back := ""
		if rbErr := os.Rename(mainOldPath, mainRec.FilePath); rbErr != nil {
			back = "；回滚主库改名也失败" + FileBusyHint(rbErr)
		}
		return "", fmt.Errorf("旧版搬入主库失败%s%s", FileBusyHint(err), back)
	}
	finalOldPath := filepath.Join(oldDir, mainOldName)
	if err := os.MkdirAll(oldDir, 0o755); err == nil {
		if err2 := os.Rename(mainOldPath, finalOldPath); err2 != nil {
			return "", fmt.Errorf("主库那份移入缓存库失败%s（主库现有两份，可重扫后手动整理）", FileBusyHint(err2))
		}
	}

	oldLib, err := hub.LibOf(oldRec.RootPath)
	if err != nil {
		return "", err
	}
	mainLib, err := hub.LibOf(mainRec.RootPath)
	if err != nil {
		return "", err
	}
	oldStore, err := hub.StoreByLib(oldLib)
	if err != nil {
		return "", err
	}
	mainStore, err := hub.StoreByLib(mainLib)
	if err != nil {
		return "", err
	}
	recA, err := mainStore.RemoveModFileRow(mainRec.FilePath)
	if err != nil {
		return "", fmt.Errorf("文件已互换但数据库更新失败：%s", err.Error())
	}
	recB, err := oldStore.RemoveModFileRow(oldFilePath)
	if err != nil {
		return "", fmt.Errorf("文件已互换但数据库更新失败：%s", err.Error())
	}
	if recA == nil || recB == nil {
		miss := mainRec.FilePath
		if recA != nil {
			miss = oldFilePath
		}
		return "", fmt.Errorf("文件已互换但数据库缺副本行（需重扫核对）：%s", miss)
	}
	recA.FileName = mainOldName
	if _, err = oldStore.InsertModFileRow(recA, oldRec.Tier, oldRec.RootPath, finalOldPath); err != nil {
		return "", fmt.Errorf("文件已互换但数据库更新失败：%s", err.Error())
	}
	recB.FileName = promotedName
	if _, err = mainStore.InsertModFileRow(recB, TierMain, mainRec.RootPath, promotedPath); err != nil {
		return "", fmt.Errorf("文件已互换但数据库更新失败：%s", err.Error())
	}
	if err = hub.RecomputeMod(allDBs(hub, cfg), guid); err != nil {
		return "", err
	}
	_ = hub.Core().DeleteModOld(oldFilePath)
	_ = hub.Core().SetModOld(store.ModOldRecord{
		OldPath:    finalOldPath,
		Guid:       guid,
		OldName:    mainOldName,
		OldVersion: mainVersion,
		NewName:    promotedName,
		NewVersion: oldVersion,
		MarkedAt:   nowStampUTC(),
	})
	return promotedName + " ⇄ " + mainOldName, nil
}

// PromoteOldHub 把一份旧版副本搬入主库——主库无该 guid 版本时正名（去掉 .old 段），
// 该 guid 仍有其他非旧版副本时保留原名。
func PromoteOldHub(hub *store.Hub, cfg *RootsConfig, guid, filePath string) (string, error) {
	if guid == "" || filePath == "" {
		return "", fmt.Errorf("缺少 guid 或文件路径")
	}
	mainRoot := FindModRoot(cfg, TierMain)
	if mainRoot == nil {
		return "", fmt.Errorf("未配置主库（级别 1）的 mod 库根")
	}
	rec := FindModFileRec(hub, cfg, guid, filePath)
	if rec == nil {
		return "", fmt.Errorf("数据库里没有这条副本行（需重扫核对）：%s", filePath)
	}
	if rec.Tier == TierMain {
		return "", fmt.Errorf("该副本已在主库")
	}
	if !fileExists(filePath) {
		return "", fmt.Errorf("文件不存在：%s", filePath)
	}
	if len(MainCopies(hub, cfg, guid, false)) > 0 {
		return "", fmt.Errorf("主库已有该 mod 的副本——直接搬入会变成重复副本；要换版本请用「换用此版本」")
	}
	onlyOld := !HasLiveCopyBesides(hub, cfg, guid, filePath)
	fileName := filepath.Base(filePath)
	if onlyOld {
		fileName = StripOldFileName(fileName)
	}
	dest := filepath.Join(mainRoot.Path, fileName)
	if fileExists(dest) {
		return "", fmt.Errorf("主库已有同名文件：%s", dest)
	}
	readOnlySource := IsReadOnlyRoot(cfg, rec.RootPath)
	if err := moveOrCopy(filePath, dest, readOnlySource); err != nil {
		return "", fmt.Errorf("文件操作失败%s", FileBusyHint(err))
	}
	var found bool
	var err error
	if readOnlySource {
		found, err = AddModFileCopyHub(hub, cfg, guid, rec.RootPath, filePath, dest, TierMain, mainRoot.Path, fileName)
	} else {
		found, err = MoveModFileHub(hub, cfg, guid, filePath, rec.RootPath, dest, TierMain, mainRoot.Path, fileName)
	}
	if err != nil {
		return "", fmt.Errorf("文件已处理但数据库更新失败：%s", err.Error())
	}
	if !found {
		return "", fmt.Errorf("文件已处理但数据库未找到源副本行：%s（需重扫核对）", filePath)
	}
	if onlyOld {
		_ = hub.Core().DeleteModOld(filePath)
		return dest + "（已正名）", nil
	}
	return dest + "（保留 .old——该 guid 还有其他非旧版副本）", nil
}

// copyStream 整段复制（io.Copy）——源只读时的搬运通道。
func copyStream(dst io.Writer, src io.Reader) (int64, error) {
	return io.Copy(dst, src)
}
