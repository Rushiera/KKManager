package core

import (
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"

	"golang.org/x/text/encoding/simplifiedchinese"
)

// 组成档案（mod_composition）的纯文本判据与目录聚合——对齐 C# Data/Store.cs §IsTextEntry / §ClampTextEntry
// 与 Data/StoreHub.cs §BuildDirs / §ParseTexts / §DecodeText。

// textEntryExts 可读文本类条目的扩展名（小写比较）。
var textEntryExts = []string{".csv", ".xml", ".txt", ".json", ".ini", ".yml", ".yaml", ".md", ".list"}

// TextEntryLimit 单条目文本入库上限（字符）——超长截断并留标记，避免档案表膨胀。
const TextEntryLimit = 20000

// IsTextEntry 一条条目是否可读文本类（按扩展名判，忽略大小写）。
func IsTextEntry(path string) bool {
	lower := strings.ToLower(path)
	for _, ext := range textEntryExts {
		if strings.HasSuffix(lower, ext) {
			return true
		}
	}
	return false
}

// ClampTextEntry 截断条目文本（超上限时留标记——不静默丢内容；长度按 UTF-16 码元计，对齐 C# string.Length）。
func ClampTextEntry(text string) string {
	units := utf16.Encode([]rune(text))
	if len(units) <= TextEntryLimit {
		return text
	}
	prefix := string(utf16.Decode(units[:TextEntryLimit]))
	return prefix + "\n…（已截断，共 " + strconv.Itoa(len(units)) + " 字符）"
}

// TextEntryCount 文本条目份数（按记录分隔符 \u0001 计——条目内容可能含换行，不能用行数当份数）。
func TextEntryCount(texts string) int {
	return strings.Count(texts, "\u0001")
}

// HasTextEntry 条目清单里是否含可读文本类条目（判档案是否缺文本内容——旧档案补捞一次用）。
func HasTextEntry(entries string) bool {
	for _, line := range strings.Split(entries, "\n") {
		if line == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if IsTextEntry(parts[0]) {
			return true
		}
	}
	return false
}

// DecodedText 一条文本条目的解码结果。
type DecodedText struct {
	// Text 解码后的内容。
	Text string
	// Encoding 实际使用的编码形态：utf-8 / gbk / gbk-unavailable（后者按宽松 UTF-8 解，内容可能失真）。
	Encoding string
}

// DecodeText 解码条目文本——先按严格 UTF-8，失败回落 GBK（Koikatsu 的 csv 有本地编码形态）；
// GBK 解码失败时按宽松 UTF-8 解并标 gbk-unavailable（调用方出声，绝不抛出）。
func DecodeText(data []byte) DecodedText {
	if len(data) == 0 {
		return DecodedText{Text: "", Encoding: "utf-8"}
	}
	if utf8.Valid(data) {
		return DecodedText{Text: string(data), Encoding: "utf-8"}
	}
	if text, err := simplifiedchinese.GBK.NewDecoder().String(string(data)); err == nil {
		return DecodedText{Text: text, Encoding: "gbk"}
	}
	return DecodedText{Text: strings.ToValidUTF8(string(data), "\uFFFD"), Encoding: "gbk-unavailable"}
}

// TextEntry 一条已入档文本条目的解析结果。
type TextEntry struct {
	// Name 条目路径。
	Name string
	// Body 条目内容。
	Body string
}

// ParseTexts 解析档案里的文本条目串（每条以 \u0001 开头，「路径 \t 内容」）。
func ParseTexts(texts string) []TextEntry {
	list := make([]TextEntry, 0)
	for _, rec := range strings.Split(texts, "\u0001") {
		if rec == "" {
			continue
		}
		tab := strings.Index(rec, "\t")
		if tab <= 0 {
			continue
		}
		list = append(list, TextEntry{Name: rec[:tab], Body: rec[tab+1:]})
	}
	return list
}

// CompositionDir 目录聚合的一行（条目数 / 原始字节合计 / 目录路径）。
type CompositionDir struct {
	// Path 目录路径（无目录段的条目归「(容器根)」）。
	Path string
	// Count 条目数。
	Count int
	// Size 原始字节合计。
	Size int64
}

// BuildDirs 把条目清单按目录聚合（每行「路径 \t 原始字节 \t 压缩字节」）——
// 按条目数降序，同数按目录名 ordinal 升序（对齐 C# CompareCompositionDirs）。
func BuildDirs(entries string) []CompositionDir {
	index := map[string]int{}
	list := make([]CompositionDir, 0)
	if entries != "" {
		for _, line := range strings.Split(entries, "\n") {
			if line == "" {
				continue
			}
			parts := strings.Split(line, "\t")
			full := parts[0]
			var size int64
			if len(parts) > 1 {
				size, _ = strconv.ParseInt(parts[1], 10, 64)
			}
			slash := strings.LastIndex(full, "/")
			dir := "(容器根)"
			if slash > 0 {
				dir = full[:slash]
			}
			pos, ok := index[dir]
			if !ok {
				pos = len(list)
				index[dir] = pos
				list = append(list, CompositionDir{Path: dir})
			}
			list[pos].Count++
			list[pos].Size += size
		}
	}
	// 插入排序（条目目录数通常 < 100）——条目数降序，同数目录名 ordinal 升序。
	for i := 1; i < len(list); i++ {
		cur := list[i]
		j := i - 1
		for j >= 0 && (list[j].Count < cur.Count || (list[j].Count == cur.Count && list[j].Path > cur.Path)) {
			list[j+1] = list[j]
			j--
		}
		list[j+1] = cur
	}
	return list
}
