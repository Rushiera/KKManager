// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片可编辑字段的读面（Parameter 块 → 姓 / 名 / 爱称 / 性格 / 敏感带 / 五项「是否接受」）+ 角色名拼装，
// 语义逐条对齐 KKManager/Core/CardEdit.cs 的 ReadParams 段与 Core/CardName.cs（写面留 G5，不在本文件）：
//   - 布局复用 ParseCardLayout（对齐 CardEdit.Parse）；
//   - Parameter 块是 MessagePack map，值区间按块起点 + 读前位置记账（写面按它原位替换）；
//   - 读不到一律返回空串 / 零值，不编造（对齐 C# 返回 null 的口径）。
package core

import (
	"os"
)

// ParamBlockName 参数块的名字（可编辑字段所在块）。
const ParamBlockName = "Parameter"

// DenialKeys 五项「是否接受」的键名（数组顺序即界面顺序）。
var DenialKeys = []string{"kiss", "aibu", "anal", "massage", "notCondom"}

// DenialNames 五项「是否接受」的显示名（与 DenialKeys 同序）。
var DenialNames = []string{"接吻", "爱抚", "肛门", "按摩", "不戴套"}

// personalityNames 汉化补丁收录的性格名称——下标即性格 ID（0..38）。
var personalityNames = []string{
	"性感", "大小姐", "高傲", "后辈", "神秘", "电波", "大和抚子", "男孩子气", "纯真无垢", "单纯",
	"中二病", "母性", "大姐头", "辣妹", "不良少女", "野生", "高意识冷酷", "乖僻", "不幸少女", "文学少女",
	"扭扭捏捏", "正统女主角", "追星族（时尚）", "宅女", "病娇", "怕麻烦", "无口", "顽固", "古风", "直率冷漠",
	"随和（坦率）", "好胜", "诚实", "娇艳", "归国子女", "方言娘", "抖S", "无感情", "几帐面",
}

// personalityExtraIds 额外 mod 加的性格 ID（汉化补丁 39 项之外）——名称未知。
var personalityExtraIds = []int{83}

// personalityUnnamed 额外性格的显示名（没有权威名称——不假装知道）。
const personalityUnnamed = "未命名（额外 mod）"

// weakPointNoneId 无敏感带（值 -1——本机卡片库实测存在）。
const weakPointNoneId = -1

// weakPointNoneName 空值的显示名。
const weakPointNoneName = "无"

// weakPointNames 敏感带名称——下标即 ID；未收录不猜名。
var weakPointNames = []string{"嘴唇", "胸部"}

// PersonalityNameOf 性格名称（第二个返回值为 false 表示表外 ID——C# 侧返回 null）。
func PersonalityNameOf(id int) (string, bool) {
	if id >= 0 && id < len(personalityNames) {
		return personalityNames[id], true
	}
	for _, extra := range personalityExtraIds {
		if extra == id {
			return personalityUnnamed, true
		}
	}
	return "", false
}

// WeakPointNameOf 敏感带名称（第二个返回值为 false 表示表外 ID——C# 侧返回 null）。
func WeakPointNameOf(id int) (string, bool) {
	if id == weakPointNoneId {
		return weakPointNoneName, true
	}
	if id >= 0 && id < len(weakPointNames) {
		return weakPointNames[id], true
	}
	return "", false
}

// CardParamInfo 人物卡 Parameter 块里的可编辑字段（含值在文件里的字节区间——写入按它原位替换）。
type CardParamInfo struct {
	// HasParameter 是否读到了 Parameter 块。
	HasParameter bool
	// LastName 姓（lastname）。
	LastName string
	// FirstName 名（firstname）。
	FirstName string
	// NickName 爱称（nickname）。
	NickName string
	// Personality 性格（personality）——数值，名称见 PersonalityNameOf。
	Personality int
	// BlockStart Parameter 块起点（文件内绝对偏移）。
	BlockStart int64
	// BlockSize Parameter 块字节数。
	BlockSize int64
	// BlockPos Parameter 块在载荷里的位置（相对载荷起点）。
	BlockPos int64
	// LastNameAt 姓的值区间起点（含 MessagePack 头）。
	LastNameAt int64
	// LastNameLen 姓的值区间字节数。
	LastNameLen int
	// FirstNameAt 名的值区间起点。
	FirstNameAt int64
	// FirstNameLen 名的值区间字节数。
	FirstNameLen int
	// NickNameAt 爱称的值区间起点。
	NickNameAt int64
	// NickNameLen 爱称的值区间字节数。
	NickNameLen int
	// PersonalityAt 性格的值区间起点。
	PersonalityAt int64
	// PersonalityLen 性格的值区间字节数。
	PersonalityLen int
	// WeakPoint 敏感带 / 弱点部位（weakPoint）——数值，名称见 WeakPointNameOf。
	WeakPoint int
	// WeakPointAt 敏感带的值区间起点。
	WeakPointAt int64
	// WeakPointLen 敏感带的值区间字节数。
	WeakPointLen int
	// Denial 五项「是否接受」的当前值——顺序见 DenialKeys。
	Denial []bool
	// DenialAt 五项「是否接受」各自的值区间起点。
	DenialAt []int64
	// DenialLen 五项「是否接受」各自的值区间字节数。
	DenialLen []int
	// Error 读取错误（空表示读到了字段）。
	Error string
}

// JoinCardName 人物卡的姓 / 名拼成游戏内显示名（两者都空返回空串；只有一个则用它；两个都有则以空格分隔）。
func JoinCardName(lastName, firstName string) string {
	last := trimSpace(lastName)
	first := trimSpace(firstName)
	if last == "" && first == "" {
		return ""
	}
	if last == "" {
		return first
	}
	if first == "" {
		return last
	}
	return last + " " + first
}

// trimSpace 两端去空白（对齐 C# string.Trim 的常用口径：空白字符集）。
func trimSpace(s string) string {
	start := 0
	end := len(s)
	for start < end && isSpaceByte(s[start]) {
		start++
	}
	for end > start && isSpaceByte(s[end-1]) {
		end--
	}
	return s[start:end]
}

// isSpaceByte 是否空白字符（ASCII 空白 + 常见 Unicode 空白按 UTF-8 首字节放过）。
func isSpaceByte(b byte) bool {
	return b == ' ' || b == '\t' || b == '\n' || b == '\r' || b == '\v' || b == '\f'
}

// ReadCardParams 读 Parameter 块里的字段（含值在文件里的字节区间）。只读；失败具名返回。
func ReadCardParams(path string, layout *CardLayout) *CardParamInfo {
	info := &CardParamInfo{
		Denial:    make([]bool, len(DenialKeys)),
		DenialAt:  make([]int64, len(DenialKeys)),
		DenialLen: make([]int, len(DenialKeys)),
	}
	if layout == nil {
		info.Error = "布局未解析"
		return info
	}
	if layout.Error != "" {
		info.Error = layout.Error
		return info
	}
	block := findLayoutBlock(layout, ParamBlockName)
	if block == nil {
		info.Error = "这张卡的数据块里没有 " + ParamBlockName + "（现有块：" + layoutBlockNames(layout) + "）"
		return info
	}
	info.BlockPos = block.Pos
	info.BlockSize = block.Size
	info.BlockStart = layout.PayloadStart + block.Pos
	buf := readCardRange(path, info.BlockStart, int(block.Size))
	if buf == nil {
		info.Error = "Parameter 块读取失败"
		return info
	}
	cur := &msgCursor{buf: buf}
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		info.Error = "Parameter 块不是 MessagePack 映射"
		return info
	}
	info.HasParameter = true
	for i := 0; i < keys; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			break
		}
		at := cur.pos
		switch key {
		case "lastname", "firstname", "nickname":
			v, ok := cur.tryReadString()
			if !ok {
				return info
			}
			switch key {
			case "lastname":
				info.LastName = v
				info.LastNameAt = info.BlockStart + int64(at)
				info.LastNameLen = cur.pos - at
			case "firstname":
				info.FirstName = v
				info.FirstNameAt = info.BlockStart + int64(at)
				info.FirstNameLen = cur.pos - at
			default:
				info.NickName = v
				info.NickNameAt = info.BlockStart + int64(at)
				info.NickNameLen = cur.pos - at
			}
		case "personality":
			v, ok := cur.tryReadLong()
			if !ok {
				return info
			}
			info.Personality = int(v)
			info.PersonalityAt = info.BlockStart + int64(at)
			info.PersonalityLen = cur.pos - at
		case "weakPoint":
			v, ok := cur.tryReadLong()
			if !ok {
				return info
			}
			info.WeakPoint = int(v)
			info.WeakPointAt = info.BlockStart + int64(at)
			info.WeakPointLen = cur.pos - at
		case "denial":
			if !readDenial(cur, info) {
				return info
			}
		default:
			if !cur.trySkipValue() {
				return info
			}
		}
	}
	return info
}

// readDenial 读「是否接受」五项（denial 嵌套映射：kiss / aibu / anal / massage / notCondom）。
func readDenial(cur *msgCursor, info *CardParamInfo) bool {
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	for i := 0; i < keys; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		at := cur.pos
		v, ok := cur.tryReadBool()
		if !ok {
			return false
		}
		slot := denialSlot(key)
		if slot >= 0 {
			info.Denial[slot] = v
			info.DenialAt[slot] = info.BlockStart + int64(at)
			info.DenialLen[slot] = cur.pos - at
		}
	}
	return true
}

// denialSlot 「是否接受」某项在数组里的位置（未知键返回 -1）。
func denialSlot(key string) int {
	for i := range DenialKeys {
		if key == DenialKeys[i] {
			return i
		}
	}
	return -1
}

// readCardRange 读文件的一段（越界或短读返回 nil，由调用方出声）。
func readCardRange(path string, at int64, count int) []byte {
	if count < 0 {
		return nil
	}
	if count == 0 {
		return []byte{}
	}
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	buf := make([]byte, count)
	if !readFullAt(f, at, buf) {
		return nil
	}
	return buf
}

// ReadCardName 读一张人物卡的角色名（数据区 Parameter 块）；布局或字段读不到一律返回空串。
func ReadCardName(path string) string {
	layout := ParseCardLayout(path)
	if layout.Error != "" {
		return ""
	}
	info := ReadCardParams(path, layout)
	if info.Error != "" {
		return ""
	}
	return JoinCardName(info.LastName, info.FirstName)
}

// itoaParamInt 已由包级 itoa(int64) 覆盖——本文件不需要额外转换。
