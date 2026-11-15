// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：跨库搬移的单条实现（缓存库 ⇄ 主库）——语义对齐 KKManager/Web/WebApp.cs
// §MoveMods / §MoveOneMod 与 Data/StoreHub.cs §MoveModFile / §AddModFileCopy：
//   - 源库只读 → 复制（源行保留）；否则移动；
//   - 文件动了而数据库没找到副本行时按失败上报（不静默算成功）；
//   - detail 面向使用者：成功给目标路径，跳过 / 失败给原因。
package core

import (
	"os"
	"path/filepath"

	"kkgo/internal/store"
)

// 单条搬移结果分类（对齐 C# MoveMoved / MoveCopied / MoveSkipped / MoveFailed）。
const (
	// MoveMoved 成功（移动）。
	MoveMoved = 0
	// MoveCopied 成功（源只读，复制）。
	MoveCopied = 1
	// MoveSkipped 跳过（有原因）。
	MoveSkipped = 2
	// MoveFailed 失败（有原因）。
	MoveFailed = 3
)

// RefGuidsHub 跨库引用 guid 集合（对齐 C# StoreHub.RefGuids）。
func RefGuidsHub(hub *store.Hub, cfg *RootsConfig) (map[string]bool, error) {
	set := map[string]bool{}
	for _, ref := range AllStoreRefs(hub, cfg) {
		guids, err := ref.DB.DistinctRefGuids()
		if err != nil {
			return nil, err
		}
		for _, g := range guids {
			set[g] = true
		}
	}
	return set, nil
}

// MoveOneMod 把某个 guid 在 fromTier 的一份副本搬到目标库根——成功时 detail 为目标路径，
// 跳过 / 失败时 detail 为原因。
func MoveOneMod(hub *store.Hub, cfg *RootsConfig, guid string, fromTier int, target RootEntry, toTier int) (int, string) {
	files, err := QueryModFilesHub(hub, cfg, guid)
	if err != nil {
		return MoveFailed, err.Error()
	}
	var pick *store.ModFileRec
	for i := range files {
		if files[i].Tier == fromTier {
			pick = &files[i]
			break
		}
	}
	if pick == nil {
		return MoveSkipped, "级别 " + itoa(int64(fromTier)) + "（" + TierName(fromTier) + "）无文件"
	}
	dest := filepath.Join(target.Path, filepath.Base(pick.FilePath))
	if _, statErr := os.Stat(dest); statErr == nil {
		return MoveSkipped, "目标已存在同名文件"
	}
	readOnlySource := IsReadOnlyRoot(cfg, pick.RootPath)
	if moveErr := moveOrCopy(pick.FilePath, dest, readOnlySource); moveErr != nil {
		return MoveFailed, "文件操作失败" + FileBusyHint(moveErr)
	}
	if readOnlySource {
		ok, dbErr := AddModFileCopyHub(hub, cfg, guid, pick.RootPath, pick.FilePath, dest, toTier, target.Path, "")
		if dbErr != nil {
			return MoveFailed, "文件已复制但数据库更新失败：" + dbErr.Error()
		}
		if !ok {
			return MoveFailed, "已复制但数据库未找到源副本行：" + pick.FilePath + "（需重扫核对）"
		}
		return MoveCopied, dest
	}
	ok, dbErr := MoveModFileHub(hub, cfg, guid, pick.FilePath, pick.RootPath, dest, toTier, target.Path, "")
	if dbErr != nil {
		return MoveFailed, "文件已移动但数据库更新失败：" + dbErr.Error()
	}
	if !ok {
		return MoveFailed, "已移动但数据库未找到源副本行：" + pick.FilePath + "（需重扫核对）"
	}
	return MoveMoved, dest
}
