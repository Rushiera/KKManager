package core

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// 插件配置文件（BepInEx/config/*.cfg）的 JSON 结构——与 C# Core/PluginConfigReader.cs 的
// PluginConfigFile / PluginConfigSection / PluginConfigOption 同形（plugin_config.sections 列的原样 JSON）。

// PluginConfigOption cfg 里的一项配置（键 / 当前值 / 作者写的说明 / 类型与默认值）。
type PluginConfigOption struct {
	// Key 配置键（cfg 等号左边的名字）。
	Key string `json:"key"`
	// Value 当前值（cfg 等号右边的文本）。
	Value string `json:"value"`
	// Comment 作者写的说明（cfg 里键上方的 ## 注释，可多行合成一段）。
	Comment string `json:"comment"`
	// Type 设置类型（「# Setting type:」——如 Boolean / Int32 / Single / String / KeyboardShortcut）。
	Type string `json:"type"`
	// Def 默认值（「# Default value:」）。
	Def string `json:"def"`
	// Acceptable 可选值（「# Acceptable values:」——枚举型才有）。
	Acceptable string `json:"acceptable"`
}

// PluginConfigSection cfg 里的一个分节（配置管理器里的一栏）。
type PluginConfigSection struct {
	// Name 节名（cfg 的 [方括号] 里的名字）。
	Name string `json:"name"`
	// Options 本节的全部配置项（保持文件顺序）。
	Options []PluginConfigOption `json:"options"`
}

// PluginConfigFile 一个插件配置文件（分节清单 + 文件头信息）。
type PluginConfigFile struct {
	// FilePath cfg 绝对路径。
	FilePath string `json:"filePath"`
	// FileName cfg 文件名。
	FileName string `json:"fileName"`
	// PluginName 插件显示名（文件头读出）。
	PluginName string `json:"pluginName"`
	// PluginVersion 插件版本（文件头读出）。
	PluginVersion string `json:"pluginVersion"`
	// Guid 插件 GUID（文件头「## Plugin GUID:」——老配置文件可能没有）。
	Guid string `json:"guid"`
	// Size cfg 字节数。
	Size int64 `json:"size"`
	// Mtime cfg 修改时间戳文本（UTC）。
	Mtime string `json:"mtime"`
	// Sections 分节清单（保持文件顺序）。
	Sections []PluginConfigSection `json:"sections"`
	// Error 解析失败原因（空 = 成功）。
	Error string `json:"error"`
}

// ParsePluginConfigJSON 从 JSON 反序列化（空 / 非法返回空对象——解析失败在 Error 里出声）。
func ParsePluginConfigJSON(text string) *PluginConfigFile {
	file := &PluginConfigFile{Sections: []PluginConfigSection{}}
	if strings.TrimSpace(text) == "" {
		return file
	}
	var raw PluginConfigFile
	if err := json.Unmarshal([]byte(text), &raw); err != nil {
		file.Error = "配置 JSON 解析失败：" + err.Error()
		return file
	}
	if raw.Sections == nil {
		raw.Sections = []PluginConfigSection{}
	}
	return &raw
}

// ---------- cfg 读取（BepInEx/config/*.cfg）——对齐 C# Core/PluginConfigReader.cs ----------

// pluginConfigNoSection 没有分节的键归入的节名（对齐 C# 的字面量）。
const pluginConfigNoSection = "(无分节)"

// pluginFileStampLayout 插件文件的时间戳格式——对齐 C# 插件段用的 LastWriteTimeUtc.ToString("o")（7 位小数 + Z）。
// 注意与 Store.StampFormat（3 位小数无 Z）不同：插件两表走 .NET 的往返格式，其余表走 StampFormat。
const pluginFileStampLayout = "2006-01-02T15:04:05.0000000Z"

// pluginFileStamp 文件修改时间戳文本（UTC——插件两表口径）。
func pluginFileStamp(t time.Time) string { return t.UTC().Format(pluginFileStampLayout) }

// ReadPluginConfig 读一个 cfg 文件（失败时 Error 写明，不抛）——对齐 C# PluginConfigReader.Read：
// UTF-8（剥 BOM）逐行；`## ` 注释累积；`# Setting type:/Default value:/Acceptable values:` 归下一个键；
// `[节]` 起新分节；`键 = 值` 落项；空行与其余 `#` 行忽略（注释与属性跨空行保留）。
func ReadPluginConfig(path string) *PluginConfigFile {
	file := &PluginConfigFile{FilePath: path, FileName: filepath.Base(path), Sections: []PluginConfigSection{}}
	info, statErr := os.Stat(path)
	if statErr != nil || info.IsDir() {
		file.Error = "配置文件不存在"
		return file
	}
	file.Size = info.Size()
	file.Mtime = pluginFileStamp(info.ModTime())
	raw, readErr := os.ReadFile(path)
	if readErr != nil {
		file.Error = readErr.Error()
		return file
	}
	text := strings.TrimPrefix(string(raw), "\uFEFF")
	comments := make([]string, 0, 4)
	typ, def, acceptable := "", "", ""
	var section *PluginConfigSection
	for _, line := range splitTextLines(text) {
		line = strings.TrimRightFunc(line, unicode.IsSpace)
		if strings.HasPrefix(line, "## Settings file was created by plugin ") {
			parsePluginConfigHeader(strings.TrimPrefix(line, "## Settings file was created by plugin "), file)
			continue
		}
		if strings.HasPrefix(line, "## Plugin GUID:") {
			file.Guid = strings.TrimSpace(strings.TrimPrefix(line, "## Plugin GUID:"))
			continue
		}
		if len(line) > 2 && line[0] == '[' && line[len(line)-1] == ']' {
			sec := PluginConfigSection{Name: strings.TrimSpace(line[1 : len(line)-1]), Options: []PluginConfigOption{}}
			file.Sections = append(file.Sections, sec)
			section = &file.Sections[len(file.Sections)-1]
			comments = comments[:0]
			typ, def, acceptable = "", "", ""
			continue
		}
		if strings.HasPrefix(line, "## ") {
			comments = append(comments, strings.TrimSpace(line[3:]))
			continue
		}
		if strings.HasPrefix(line, "# Setting type:") {
			typ = strings.TrimSpace(strings.TrimPrefix(line, "# Setting type:"))
			continue
		}
		if strings.HasPrefix(line, "# Default value:") {
			def = strings.TrimSpace(strings.TrimPrefix(line, "# Default value:"))
			continue
		}
		if strings.HasPrefix(line, "# Acceptable values:") {
			acceptable = strings.TrimSpace(strings.TrimPrefix(line, "# Acceptable values:"))
			continue
		}
		if strings.HasPrefix(line, "#") {
			continue
		}
		if line == "" {
			continue
		}
		eq := strings.IndexByte(line, '=')
		if eq <= 0 {
			continue
		}
		if section == nil {
			file.Sections = append(file.Sections, PluginConfigSection{Name: pluginConfigNoSection, Options: []PluginConfigOption{}})
			section = &file.Sections[len(file.Sections)-1]
		}
		section.Options = append(section.Options, PluginConfigOption{
			Key:        strings.TrimSpace(line[:eq]),
			Value:      strings.TrimSpace(line[eq+1:]),
			Comment:    strings.Join(comments, " "),
			Type:       typ,
			Def:        def,
			Acceptable: acceptable,
		})
		comments = comments[:0]
		typ, def, acceptable = "", "", ""
	}
	return file
}

// parsePluginConfigHeader 解析文件头里的插件名与版本（形态「<名> v<版本>」——版本段可能不存在）。
func parsePluginConfigHeader(text string, file *PluginConfigFile) {
	head := strings.TrimSpace(text)
	at := strings.LastIndex(head, " v")
	if at > 0 {
		file.PluginName = strings.TrimSpace(head[:at])
		file.PluginVersion = strings.TrimSpace(head[at+1:])
		return
	}
	file.PluginName = head
}

// splitTextLines 按 ReadLine 语义拆行（\r\n / \n / 单独 \r 都算；末尾换行不产生空行）。
func splitTextLines(text string) []string {
	lines := make([]string, 0, 64)
	start := 0
	for i := 0; i < len(text); i++ {
		switch text[i] {
		case '\n':
			lines = append(lines, text[start:i])
			start = i + 1
		case '\r':
			lines = append(lines, text[start:i])
			if i+1 < len(text) && text[i+1] == '\n' {
				i++
			}
			start = i + 1
		}
	}
	if start < len(text) {
		lines = append(lines, text[start:])
	}
	return lines
}

// ReadPluginConfigDir 读一个插件库下的全部 cfg（只读顶层 *.cfg——子目录是插件自己的数据）。
// 对齐 C# PluginConfigReader.ReadAll：目录缺失 / 枚举失败入 errors；结果按文件名（不区分大小写）排序。
func ReadPluginConfigDir(pluginRoot string, errors *[]string) []PluginConfigFile {
	list := make([]PluginConfigFile, 0, 8)
	if strings.TrimSpace(pluginRoot) == "" {
		return list
	}
	dir := filepath.Join(pluginRoot, "config")
	info, statErr := os.Stat(dir)
	if statErr != nil || !info.IsDir() {
		addPluginConfigError(errors, "配置目录不存在："+dir)
		return list
	}
	entries, readErr := os.ReadDir(dir)
	if readErr != nil {
		addPluginConfigError(errors, "配置目录枚举失败（"+dir+"）："+readErr.Error())
		return list
	}
	files := make([]string, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		if strings.EqualFold(filepath.Ext(e.Name()), ".cfg") {
			files = append(files, filepath.Join(dir, e.Name()))
		}
	}
	sort.Slice(files, func(i, j int) bool {
		return strings.ToLower(filepath.Base(files[i])) < strings.ToLower(filepath.Base(files[j]))
	})
	for _, filePath := range files {
		cfg := ReadPluginConfig(filePath)
		if cfg.Error != "" {
			addPluginConfigError(errors, "配置读取失败（"+filePath+"）："+cfg.Error)
		}
		list = append(list, *cfg)
	}
	return list
}

// addPluginConfigError 记一条 cfg 读取错误（errors 为 nil 时静默——调用方不需要出声的场合）。
func addPluginConfigError(errors *[]string, message string) {
	if errors != nil {
		*errors = append(*errors, message)
	}
}

// ToPluginConfigJSON 把一个 cfg 解析结果序列化为 JSON（落库用）——逐字节对齐 C# System.Text.Json：
// 紧凑（无空白）、键序 = 属性声明序、转义按默认 encoder（非 ASCII 与 < > & ' " + 一律 \uXXXX 大写）。
func ToPluginConfigJSON(file *PluginConfigFile) string {
	if file == nil {
		return ""
	}
	var b strings.Builder
	b.Grow(256)
	b.WriteString(`{"filePath":`)
	appendCSharpJSONString(&b, file.FilePath)
	b.WriteString(`,"fileName":`)
	appendCSharpJSONString(&b, file.FileName)
	b.WriteString(`,"pluginName":`)
	appendCSharpJSONString(&b, file.PluginName)
	b.WriteString(`,"pluginVersion":`)
	appendCSharpJSONString(&b, file.PluginVersion)
	b.WriteString(`,"guid":`)
	appendCSharpJSONString(&b, file.Guid)
	b.WriteString(`,"size":`)
	b.WriteString(strconv.FormatInt(file.Size, 10))
	b.WriteString(`,"mtime":`)
	appendCSharpJSONString(&b, file.Mtime)
	b.WriteString(`,"sections":[`)
	for i := range file.Sections {
		if i > 0 {
			b.WriteByte(',')
		}
		sec := &file.Sections[i]
		b.WriteString(`{"name":`)
		appendCSharpJSONString(&b, sec.Name)
		b.WriteString(`,"options":[`)
		for k := range sec.Options {
			if k > 0 {
				b.WriteByte(',')
			}
			opt := &sec.Options[k]
			b.WriteString(`{"key":`)
			appendCSharpJSONString(&b, opt.Key)
			b.WriteString(`,"value":`)
			appendCSharpJSONString(&b, opt.Value)
			b.WriteString(`,"comment":`)
			appendCSharpJSONString(&b, opt.Comment)
			b.WriteString(`,"type":`)
			appendCSharpJSONString(&b, opt.Type)
			b.WriteString(`,"def":`)
			appendCSharpJSONString(&b, opt.Def)
			b.WriteString(`,"acceptable":`)
			appendCSharpJSONString(&b, opt.Acceptable)
			b.WriteByte('}')
		}
		b.WriteString(`]}`)
	}
	b.WriteString(`],"error":`)
	appendCSharpJSONString(&b, file.Error)
	b.WriteByte('}')
	return b.String()
}

// appendCSharpJSONString 按 System.Text.Json 默认 encoder（JavaScriptEncoder.Default）口径写一个 JSON 字符串：
// 短转义 \b \f \n \r \t；`"` → \u0022、`\` → \\；HTML 敏感 < > & ' + → \uXXXX；
// 其余控制字符与非 ASCII → \uXXXX（大写；BMP 外走 UTF-16 代理对）。
func appendCSharpJSONString(b *strings.Builder, s string) {
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			b.WriteString(`\u0022`)
		case '\\':
			b.WriteString(`\\`)
		case '\b':
			b.WriteString(`\b`)
		case '\f':
			b.WriteString(`\f`)
		case '\n':
			b.WriteString(`\n`)
		case '\r':
			b.WriteString(`\r`)
		case '\t':
			b.WriteString(`\t`)
		case '<':
			b.WriteString(`\u003C`)
		case '>':
			b.WriteString(`\u003E`)
		case '&':
			b.WriteString(`\u0026`)
		case '\'':
			b.WriteString(`\u0027`)
		case '+':
			b.WriteString(`\u002B`)
		default:
			switch {
			case r < 0x20 || r == 0x7F:
				fmt.Fprintf(b, `\u%04X`, r)
			case r < 0x80:
				b.WriteByte(byte(r))
			case r <= 0xFFFF:
				fmt.Fprintf(b, `\u%04X`, r)
			default:
				v := r - 0x10000
				fmt.Fprintf(b, `\u%04X\u%04X`, 0xD800+(v>>10), 0xDC00+(v&0x3FF))
			}
		}
	}
	b.WriteByte('"')
}
