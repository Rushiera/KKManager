// 场景卡插件键 ↔ 已装插件的对照——键是卡片数据区里的简名（vnge_sss / kkpe / timeline），插件是 GUID / 名称。
// 契约对齐 C# Core/PluginMatcher.cs（单插件判定；批量遍历归调用方，保持 core 不依赖 store）。
package core

import (
	"strings"
	"unicode/utf8"
)

// PluginKeyMatches 场景插件键是否命中一个已装插件（大小写不敏感）。
// 命中判据：键整体出现在 GUID / 名称里；或键的首段（第一个 '_' 之前，长度 ≥ 4 字符）出现在 GUID / 名称里。
// guid 为空的非插件 dll 一律不命中（对齐 C# PluginMatcher.Match）。
func PluginKeyMatches(guid, name, key string) bool {
	if guid == "" || strings.TrimSpace(key) == "" {
		return false
	}
	text := strings.ToLower(strings.TrimSpace(key))
	// 键的首段（vnge_sss → vnge）——同一插件的多个数据区键都归到该插件
	head := text
	if under := strings.IndexByte(text, '_'); under > 0 {
		head = text[:under]
	}
	g := strings.ToLower(guid)
	n := strings.ToLower(name)
	hit := strings.Contains(g, text) || strings.Contains(n, text)
	if !hit && utf8.RuneCountInString(head) >= 4 {
		hit = strings.Contains(g, head) || strings.Contains(n, head)
	}
	return hit
}
