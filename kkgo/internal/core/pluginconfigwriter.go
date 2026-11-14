// cfg 值写回（G6 写面）——对齐 C# Core/PluginConfigWriter.cs §Save。
//
// 最小改动：只替换目标键那一行等号右侧的值段，其余字节（BOM / 行尾 / 作者注释 / 分节顺序 / 空行 / 缩进）逐字节搬运。
// 边界：只改已存在的键，不新增键、不删键、不动注释；键不存在或同节出现多次一律拒绝并出声。
package core

import (
	"bytes"
	"os"
	"strings"
	"unicode/utf16"
)

// NoSectionName 文件开头无分节的键在读面归入的虚拟分节名（与读面同源）——写面同样接受这个名字。
const NoSectionName = "(无分节)"

// PluginConfigSaveResult cfg 值写回的结果——成败 / 原因 / 改动前后的事实。
type PluginConfigSaveResult struct {
	// Ok 是否写入成功（值本来就没变也算成功，但不写盘）。
	Ok bool
	// Error 失败原因（成功时为空串）。
	Error string
	// FilePath 写入的 cfg 绝对路径。
	FilePath string
	// FileName 写入的 cfg 文件名。
	FileName string
	// Section 分节名（文件开头无分节的键为「(无分节)」）。
	Section string
	// Key 配置键。
	Key string
	// OldValue 改动前的值。
	OldValue string
	// NewValue 改动后的值。
	NewValue string
	// SizeBefore 写入前的文件字节数。
	SizeBefore int64
	// SizeAfter 写入后的文件字节数。
	SizeAfter int64
	// MtimeBefore 写入前的修改时间戳（UTC，"o" 格式）。
	MtimeBefore string
	// MtimeAfter 写入后的修改时间戳（UTC，"o" 格式）。
	MtimeAfter string
}

// SavePluginConfigValue 把某个配置项的值写回 cfg（最小改动——只换等号右侧的值段）。
func SavePluginConfigValue(cfgPath, section, key, newValue string) *PluginConfigSaveResult {
	result := &PluginConfigSaveResult{
		FilePath: cfgPath, FileName: fileNameOf(cfgPath), Section: section, Key: key, NewValue: newValue,
	}
	// [段1] 入口校验——键非空 / 值不含换行 / 文件在位
	if trimSpace(cfgPath) == "" {
		result.Error = "配置文件路径为空"
		return result
	}
	if trimSpace(key) == "" {
		result.Error = "配置键为空"
		return result
	}
	if strings.ContainsAny(result.NewValue, "\r\n") {
		result.Error = "值里不能含换行——会破坏 cfg 的行结构"
		return result
	}
	if _, err := os.Stat(cfgPath); err != nil {
		result.Error = "配置文件不存在：" + cfgPath
		return result
	}
	// [段2] 读原始字节——探测 BOM 与编码（BOM 单独搬运，不参与正文编码）
	before, err := os.ReadFile(cfgPath)
	if err != nil {
		result.Error = "读取失败：" + err.Error()
		return result
	}
	bomLen := 0
	kind := 0 // 0 = UTF-8 · 1 = UTF-16 LE · 2 = UTF-16 BE
	if len(before) >= 3 && before[0] == 0xEF && before[1] == 0xBB && before[2] == 0xBF {
		bomLen = 3
	} else if len(before) >= 2 && before[0] == 0xFF && before[1] == 0xFE {
		bomLen, kind = 2, 1
	} else if len(before) >= 2 && before[0] == 0xFE && before[1] == 0xFF {
		bomLen, kind = 2, 2
	}
	result.SizeBefore = int64(len(before))
	result.MtimeBefore = cfgMtimeStamp(cfgPath)
	text := decodeCfgBody(before[bomLen:], kind)
	// [段3] 切行（保留每行行尾）→ 定位目标键
	lines, eols := splitCfgLines(text)
	eqPos, hitCount, hitLine := findKeyLine(lines, section, key)
	if hitCount == 0 {
		result.Error = "没找到这个配置项（分节 [" + section + "] · 键 " + key + "）——只改已存在的键，不新增"
		return result
	}
	if hitCount > 1 {
		result.Error = "同一分节里这个键出现了 " + itoaSmall(hitCount) + " 次（cfg 异常）——不猜改哪一处，请先手工确认文件"
		return result
	}
	// [段4] 只换值段——等号与其后的前导空白原样保留
	origin := lines[hitLine]
	head := origin[:eqPos+1]
	tail := origin[eqPos+1:]
	leadEnd := 0
	for leadEnd < len(tail) && (tail[leadEnd] == ' ' || tail[leadEnd] == '\t') {
		leadEnd++
	}
	lead := tail[:leadEnd]
	rest := tail[leadEnd:]
	trailStart := len(rest)
	for trailStart > 0 && (rest[trailStart-1] == ' ' || rest[trailStart-1] == '\t') {
		trailStart--
	}
	result.OldValue = rest[:trailStart]
	updated := head + lead + result.NewValue + rest[trailStart:]
	if updated == origin {
		// 值没变——不写盘（不制造无谓的 mtime 变动）
		result.Ok = true
		result.SizeAfter = int64(len(before))
		result.MtimeAfter = result.MtimeBefore
		return result
	}
	changed := make([]string, len(lines))
	copy(changed, lines)
	changed[hitLine] = updated
	// [段5] 最小改动自检——新旧行数组必须只差目标那一行
	diff := 0
	for i := range lines {
		if lines[i] != changed[i] {
			diff++
		}
	}
	if diff != 1 {
		result.Error = "内部自检失败——改动不止一行（" + itoaSmall(diff) + " 行），拒绝写盘"
		return result
	}
	// [段6] 重组 + 写盘（BOM 前缀原样搬运；行尾逐行还原）
	var sb strings.Builder
	sb.Grow(len(text) + len(result.NewValue))
	for i := range changed {
		sb.WriteString(changed[i])
		sb.WriteString(eols[i])
	}
	payload := encodeCfgBody(sb.String(), kind)
	after := make([]byte, 0, bomLen+len(payload))
	after = append(after, before[:bomLen]...)
	after = append(after, payload...)
	if err := os.WriteFile(cfgPath, after, 0o644); err != nil {
		result.Error = "写盘失败：" + err.Error()
		return result
	}
	// [段7] 读回验证——磁盘字节与预期逐字节全等（落盘实锤）
	verify, err := os.ReadFile(cfgPath)
	if err != nil {
		result.Error = "写回后重读失败：" + err.Error()
		return result
	}
	if len(verify) != len(after) {
		result.Error = "写回后字节数不符（预期 " + itoaSmall(len(after)) + " · 实际 " + itoaSmall(len(verify)) + "）"
		return result
	}
	for i := range after {
		if verify[i] != after[i] {
			result.Error = "写回后第 " + itoaSmall(i) + " 字节与预期不符"
			return result
		}
	}
	result.Ok = true
	result.SizeAfter = int64(len(verify))
	result.MtimeAfter = cfgMtimeStamp(cfgPath)
	return result
}

// splitCfgLines 把 cfg 正文切成行——每行不含行尾符，行尾符单独收在 eols（重组时逐行还原）。
func splitCfgLines(text string) ([]string, []string) {
	lines := make([]string, 0, 64)
	eols := make([]string, 0, 64)
	start := 0
	for i := 0; i < len(text); i++ {
		if text[i] != '\n' {
			continue
		}
		end := i
		eol := "\n"
		if end > start && text[end-1] == '\r' {
			end--
			eol = "\r\n"
		}
		lines = append(lines, text[start:end])
		eols = append(eols, eol)
		start = i + 1
	}
	if start < len(text) {
		lines = append(lines, text[start:])
		eols = append(eols, "")
	}
	return lines, eols
}

// findKeyLine 定位目标键所在的行（判据与读面同构：分节行 / 注释 / 空行 /「键 = 值」）。
func findKeyLine(lines []string, section, key string) (eqPos, hitCount, hitLine int) {
	eqPos, hitCount, hitLine = -1, 0, -1
	current := NoSectionName
	for i, line := range lines {
		t := strings.TrimRight(line, " \t")
		if len(t) > 2 && t[0] == '[' && t[len(t)-1] == ']' {
			current = trimSpace(t[1 : len(t)-1])
			continue
		}
		if strings.HasPrefix(t, "#") || t == "" {
			continue
		}
		eq := strings.IndexByte(t, '=')
		if eq <= 0 {
			continue
		}
		if current != section {
			continue
		}
		if trimSpace(t[:eq]) != key {
			continue
		}
		hitCount++
		if hitLine < 0 {
			hitLine = i
			eqPos = eq
		}
	}
	return eqPos, hitCount, hitLine
}

// decodeCfgBody 把正文按 BOM 探测出的编码解成字符串（kind：0 UTF-8 / 1 UTF-16LE / 2 UTF-16BE）。
func decodeCfgBody(body []byte, kind int) string {
	if kind == 0 {
		return string(body)
	}
	if len(body)%2 != 0 {
		body = body[:len(body)-1]
	}
	units := make([]uint16, 0, len(body)/2)
	for i := 0; i+1 < len(body); i += 2 {
		if kind == 1 {
			units = append(units, uint16(body[i])|uint16(body[i+1])<<8)
		} else {
			units = append(units, uint16(body[i])<<8|uint16(body[i+1]))
		}
	}
	return string(utf16.Decode(units))
}

// encodeCfgBody 把字符串按同一编码编回字节。
func encodeCfgBody(text string, kind int) []byte {
	if kind == 0 {
		return []byte(text)
	}
	units := utf16.Encode([]rune(text))
	var buf bytes.Buffer
	buf.Grow(len(units) * 2)
	for _, u := range units {
		if kind == 1 {
			buf.WriteByte(byte(u))
			buf.WriteByte(byte(u >> 8))
		} else {
			buf.WriteByte(byte(u >> 8))
			buf.WriteByte(byte(u))
		}
	}
	return buf.Bytes()
}

// fileNameOf 取路径的文件名部分（空路径返回空串）。
func fileNameOf(path string) string {
	if path == "" {
		return ""
	}
	idx := strings.LastIndexAny(path, "\\/")
	if idx < 0 {
		return path
	}
	return path[idx+1:]
}

// cfgMtimeStamp cfg 文件的修改时间文本（o 格式，对齐 C# LastWriteTimeUtc.ToString("o")）。
func cfgMtimeStamp(path string) string {
	info, err := os.Stat(path)
	if err != nil {
		return ""
	}
	return pluginFileStamp(info.ModTime())
}
