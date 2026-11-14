// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片数据区布局（头段 / 脸图 / 块表 / 8 字节尾 / 载荷起点），
// 语义逐条对齐 KKManager/Core/CardEdit.cs 的 Parse 段（不移植写面）：
//   - 只覆盖人物卡（卡类型须含 "Chara"）；
//   - 块表按 MessagePack {"lstInfo":[{name,version,pos,size}…]} 直解；
//   - 载荷起点 = 尾 8 字节之后，块 pos 相对它。
package core

import (
	"os"
	"strconv"
)

// CardEditBlock 卡片数据区的一个块（lstInfo 目录条目）——名称 / 版本 / 载荷内位置 / 字节数。
type CardEditBlock struct {
	// Name 块名（Custom / Coordinate / Parameter / Status / KKEx）。
	Name string
	// Version 块版本（如 0.0.5）。
	Version string
	// VersionSet version 键是否读到（区分「未读到」与「读到空串」——C# 侧 null 与空串输出不同）。
	VersionSet bool
	// Pos 块在载荷里的位置（相对载荷起点）。
	Pos int64
	// Size 块字节数。
	Size int64
}

// CardLayout 卡片数据区布局——图片区终点 / 头段 / 块表 / 载荷起点（只读解析结果）。
type CardLayout struct {
	// FilePath 文件绝对路径。
	FilePath string
	// FileSize 文件字节数。
	FileSize int64
	// ImageEnd 图片区终点（PNG 的 IEND 结束处）——数据区起点。
	ImageEnd int64
	// CardType 卡类型标记（如 【KoiKatuChara】）。
	CardType string
	// DataVersion 数据区版本标记。
	DataVersion string
	// FaceOffset 脸图在文件里的偏移（人物卡头段记录的 int32 之后）。
	FaceOffset int64
	// FaceSize 脸图字节数。
	FaceSize int64
	// TableSizeAt 块表字节数（int32）所在偏移。
	TableSizeAt int64
	// TableAt 块表（MessagePack）起点。
	TableAt int64
	// TableSize 块表字节数。
	TableSize int64
	// TailAt 8 字节尾的起点（块表之后）。
	TailAt int64
	// TailRaw 尾部第一个 int32（实测 = 载荷字节数 + 一个常量）。
	TailRaw int64
	// TailSecond 尾部第二个 int32（原样保留）。
	TailSecond int64
	// TailConst 尾部常量（TailRaw − 载荷字节数）。
	TailConst int64
	// PayloadStart 载荷起点（块 pos 的基准）。
	PayloadStart int64
	// PayloadSize 载荷字节数（文件尾 − 载荷起点）。
	PayloadSize int64
	// Blocks 块表条目（按表内出现顺序）。
	Blocks []CardEditBlock
	// Error 解析错误（空表示可编辑布局读出）。
	Error string
}

// ParseCardLayout 解析数据区布局——人物卡：头段 + 脸图 + 块表 + 8 字节尾 + 载荷。
func ParseCardLayout(path string) *CardLayout {
	layout := &CardLayout{FilePath: path}
	st := ParseCardStructure(path)
	if st.Error != "" {
		layout.Error = st.Error
		return layout
	}
	layout.FileSize = st.Size
	layout.ImageEnd = st.ImageEnd
	layout.CardType = st.CardType
	layout.DataVersion = st.DataVersion
	if st.ImageEnd <= 0 {
		layout.Error = "未找到 PNG 的 IEND——图片区终点未知"
		return layout
	}
	if !containsChara(st.CardType) {
		cardType := st.CardType
		if cardType == "" {
			cardType = "<未识别>"
		}
		layout.Error = "不是人物卡（卡类型 " + cardType + "）——本轮编辑面只覆盖人物卡的姓 / 名 / 爱称 / 性格"
		return layout
	}

	f, err := os.Open(path)
	if err != nil {
		layout.Error = "卡片文件打不开：" + path
		return layout
	}
	defer f.Close()

	c := &fileCursor{f: f, pos: st.ImageEnd, size: st.Size}
	if _, _, err := readCardHead(c); err != nil {
		layout.Error = "数据区布局解析异常：" + err.Error()
		return layout
	}
	faceSize, err := c.readInt32()
	if err != nil {
		layout.Error = "数据区布局解析异常：" + err.Error()
		return layout
	}
	layout.FaceSize = int64(faceSize)
	layout.FaceOffset = c.pos
	if layout.FaceSize <= 0 || layout.FaceOffset+layout.FaceSize > st.Size {
		layout.Error = "脸图长度异常（" + itoa(layout.FaceSize) + " 字节）——不是预期的人物卡布局"
		return layout
	}
	c.pos = layout.FaceOffset + layout.FaceSize
	layout.TableSizeAt = c.pos
	tableSize, err := c.readInt32()
	if err != nil {
		layout.Error = "数据区布局解析异常：" + err.Error()
		return layout
	}
	if tableSize <= 0 || tableSize > 1048576 || layout.TableSizeAt+4+int64(tableSize) > st.Size {
		layout.Error = "块表长度异常（" + itoa(int64(tableSize)) + " 字节）——不是预期的人物卡布局"
		return layout
	}
	layout.TableSize = int64(tableSize)
	layout.TableAt = c.pos
	table := make([]byte, tableSize)
	if !readFullAt(f, layout.TableAt, table) {
		layout.Error = "块表读取不完整"
		return layout
	}
	if !parseLayoutTable(table, layout) {
		layout.Error = "块表不是预期的 lstInfo 结构（" + hexHead(table, 24) + "）"
		return layout
	}
	layout.TailAt = layout.TableAt + int64(tableSize)
	raw, err := readInt32At(f, layout.TailAt)
	if err != nil {
		layout.Error = "数据区布局解析异常：" + err.Error()
		return layout
	}
	second, err := readInt32At(f, layout.TailAt+4)
	if err != nil {
		layout.Error = "数据区布局解析异常：" + err.Error()
		return layout
	}
	layout.TailRaw = int64(raw)
	layout.TailSecond = int64(second)
	layout.PayloadStart = layout.TailAt + 8
	layout.PayloadSize = st.Size - layout.PayloadStart
	layout.TailConst = layout.TailRaw - layout.PayloadSize
	if layout.PayloadSize <= 0 {
		layout.Error = "载荷长度为 0——不是预期的人物卡布局"
		return layout
	}
	return layout
}

// containsChara 卡类型是否含 "Chara"（人物卡判据）。
func containsChara(cardType string) bool {
	const mark = "Chara"
	if len(cardType) < len(mark) {
		return false
	}
	for i := 0; i+len(mark) <= len(cardType); i++ {
		if cardType[i:i+len(mark)] == mark {
			return true
		}
	}
	return false
}

// parseLayoutTable 解析块表（MessagePack：{"lstInfo":[{name,version,pos,size}…]}）。
func parseLayoutTable(buf []byte, layout *CardLayout) bool {
	if len(buf) < 2 {
		return false
	}
	cur := &msgCursor{buf: buf}
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	for i := 0; i < keys; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		if key != "lstInfo" {
			if !cur.trySkipValue() {
				return false
			}
			continue
		}
		count, ok := cur.tryReadArrayHeader()
		if !ok {
			return false
		}
		for k := 0; k < count; k++ {
			if !readLayoutBlockEntry(cur, layout) {
				return false
			}
		}
	}
	return len(layout.Blocks) > 0
}

// readLayoutBlockEntry 读一条块表条目（map：name / version / pos / size）。
func readLayoutBlockEntry(cur *msgCursor, layout *CardLayout) bool {
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	var b CardEditBlock
	for i := 0; i < keys; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		switch key {
		case "name", "version":
			v, ok := cur.tryReadString()
			if !ok {
				return false
			}
			if key == "name" {
				b.Name = v
			} else {
				b.Version = v
				b.VersionSet = true
			}
		case "pos", "size":
			v, ok := cur.tryReadLong()
			if !ok {
				return false
			}
			if key == "pos" {
				b.Pos = v
			} else {
				b.Size = v
			}
		default:
			if !cur.trySkipValue() {
				return false
			}
		}
	}
	if b.Name == "" {
		return false
	}
	layout.Blocks = append(layout.Blocks, b)
	return true
}

// hexHead 缓冲区前 n 字节的十六进制（诊断用，无分隔）。
func hexHead(buf []byte, n int) string {
	take := len(buf)
	if take > n {
		take = n
	}
	const digits = "0123456789ABCDEF"
	out := make([]byte, 0, take*2)
	for i := 0; i < take; i++ {
		out = append(out, digits[buf[i]>>4], digits[buf[i]&0x0F])
	}
	return string(out)
}

// readInt32At 读指定偏移的小端 32 位整数。
func readInt32At(f *os.File, at int64) (int32, error) {
	b := make([]byte, 4)
	if !readFullAt(f, at, b) {
		return 0, errShortRead(at)
	}
	return int32(uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24), nil
}

// errShortRead 短读诊断（位置 + 缺失上下文由调用方补）。
func errShortRead(at int64) error {
	return &shortReadError{at: at}
}

// shortReadError 短读错误。
type shortReadError struct {
	at int64
}

// Error 实现 error 接口。
func (e *shortReadError) Error() string {
	return "读取区间失败（偏移 " + strconv.FormatInt(e.at, 10) + "）"
}
