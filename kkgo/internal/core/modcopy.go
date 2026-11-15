// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：一份 mod 副本的展示参考信息（manifest 实时读 + 创建时间实时读）与
// 旧版登记的读取排序——语义对齐 KKManager/Core/ModCopyReader.cs：
//   - 副本窗口 / 冲突小窗 / CLI 共用同一装配，字段加一处三处一起变；
//   - MD5 档案补齐走 refsreader.go §FillHashesHub（同一档档案，算过就忽略）。
package core

import (
	"sort"

	"kkgo/internal/store"
)

// ModCopyInfo 一份 mod 副本的参考信息——manifest 实时读 + 创建时间实时读。
type ModCopyInfo struct {
	// Version manifest 版本（实时读；缺元素为空串）。
	Version string
	// Author manifest 作者（实时读；缺元素为空串）。
	Author string
	// Name manifest 名称（实时读；缺元素为空串）。
	Name string
	// Error manifest 读不到时的原因（空 = 读到了）。
	Error string
	// Ctime 创建时间（文件系统；读不到为空串）。
	Ctime string
}

// ReadModCopy 读一份副本的 manifest（版本 / 作者 / 名称 / 读取失败原因）与创建时间。
func ReadModCopy(filePath string) ModCopyInfo {
	info := ModCopyInfo{}
	if m := ParseZipMod(filePath); m != nil {
		info.Version = m.Version
		info.Author = m.Author
		info.Name = m.Name
		info.Error = m.Error
	}
	info.Ctime = CreatedStampOf(filePath)
	return info
}

// ListModOldSorted 全部旧版登记（按登记时刻升序——对齐 C# Store.ListModOld 的 ORDER BY marked_at）。
func ListModOldSorted(hub *store.Hub) []store.ModOldRecord {
	oldMap := hub.Core().LoadModOldMap()
	list := make([]store.ModOldRecord, 0, len(oldMap))
	for _, o := range oldMap {
		list = append(list, o)
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].MarkedAt < list[j].MarkedAt })
	return list
}
