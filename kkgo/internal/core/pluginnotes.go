package core

import (
	_ "embed"
	"strings"
	"sync"
)

// 插件中文说明表——名称 / GUID → 一句中文用途（随程序内嵌，离线可用）。
// 数据源：KKManager/Web/wwwroot/plugin-notes.txt（C# 同源；更新时两处同步）。
// 键 = 插件 GUID 或 显示名（先按 GUID / 名称精确匹配，再按归一化名兜底）。

//go:embed resources/plugin-notes.txt
var pluginNotesRaw string

// noteTable 说明表（键与归一化键两张表，大小写不敏感）。
type noteTable struct {
	byKey  map[string]string
	byNorm map[string]string
}

var (
	pluginNotesOnce sync.Once
	pluginNotes     *noteTable
)

// loadPluginNotes 首次访问时载入说明表（每行「键<TAB>说明」；# 开头为注释）。
func loadPluginNotes() *noteTable {
	pluginNotesOnce.Do(func() {
		table := &noteTable{byKey: make(map[string]string), byNorm: make(map[string]string)}
		for _, line := range strings.Split(pluginNotesRaw, "\n") {
			text := strings.TrimSpace(strings.TrimSuffix(line, "\r"))
			if text == "" || text[0] == '#' {
				continue
			}
			tab := strings.Index(text, "\t")
			if tab <= 0 {
				continue
			}
			key := strings.TrimSpace(text[:tab])
			note := strings.TrimSpace(text[tab+1:])
			if key == "" || note == "" {
				continue
			}
			if _, ok := table.byKey[strings.ToLower(key)]; !ok {
				table.byKey[strings.ToLower(key)] = note
			}
			if norm := normalizeNoteKey(key); norm != "" {
				if _, ok := table.byNorm[norm]; !ok {
					table.byNorm[norm] = note
				}
			}
		}
		pluginNotes = table
	})
	return pluginNotes
}

// normalizeNoteKey 把名称归一化——小写、去掉空白 / 下划线 / 短横线 / 点，再去掉开头的 KK 前缀。
func normalizeNoteKey(name string) string {
	if strings.TrimSpace(name) == "" {
		return ""
	}
	var sb strings.Builder
	for _, c := range strings.ToLower(strings.TrimSpace(name)) {
		if c == ' ' || c == '_' || c == '-' || c == '.' {
			continue
		}
		sb.WriteRune(c)
	}
	key := sb.String()
	if strings.HasPrefix(key, "kk") && len(key) > 2 {
		key = key[2:]
	}
	return key
}

// LookupPluginNote 查一项说明——先按 GUID 精确匹配，再按插件名匹配，最后按归一化名兜底；没有返回空串。
func LookupPluginNote(guid, name string) string {
	table := loadPluginNotes()
	if g := strings.ToLower(strings.TrimSpace(guid)); g != "" {
		if note, ok := table.byKey[g]; ok {
			return note
		}
	}
	if n := strings.ToLower(strings.TrimSpace(name)); n != "" {
		if note, ok := table.byKey[n]; ok {
			return note
		}
	}
	if norm := normalizeNoteKey(name); norm != "" {
		if note, ok := table.byNorm[norm]; ok {
			return note
		}
	}
	return ""
}

// LookupPluginNoteFile 非插件 dll 的说明——按文件名匹配（精确，再归一化）。
func LookupPluginNoteFile(fileName string) string {
	table := loadPluginNotes()
	if name := strings.ToLower(strings.TrimSpace(fileName)); name != "" {
		if note, ok := table.byKey[name]; ok {
			return note
		}
	}
	if norm := normalizeNoteKey(fileName); norm != "" {
		if note, ok := table.byNorm[norm]; ok {
			return note
		}
	}
	return ""
}

// PluginNotesCount 说明表条目数（唯一键数——"表内 N 条"）。
func PluginNotesCount() int {
	return len(loadPluginNotes().byKey)
}
