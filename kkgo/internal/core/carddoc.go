// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片文件结构（PNG 块表 + 图片区 / 数据区分界 + 数据区头部），
// 语义逐条对齐 KKManager/Core/CardDocument.cs：
//   - 块表遍历（长度 / 类型 / CRC；>64 KB 的块不校验 CRC）；
//   - 数据区头：卡类型 / 数据标记 / 首 64 字节 hex；
//   - 只做「结构呈现」，不写文件、不解析数据区内部。
package core

import (
	"hash/crc32"
	"os"
	"path/filepath"
	"strings"
)

// crcCheckLimit CRC 校验的块大小上限——超过则不校验（大块读一遍代价高）。
const crcCheckLimit = 65536

// PngChunk PNG 文件里的一个块。
type PngChunk struct {
	// Type 块类型（4 个 ASCII 字符，如 IHDR / IDAT / IEND / tEXt）。
	Type string
	// Offset 块起始偏移（长度字段的第一个字节）。
	Offset int64
	// DataOffset 块数据起始偏移。
	DataOffset int64
	// Length 块数据字节数（不含长度、类型与 CRC）。
	Length int64
	// End 块结束偏移（CRC 之后，即下一块的起点）。
	End int64
	// CrcOk CRC 是否通过（未校验的块恒为真，另看 CrcChecked）。
	CrcOk bool
	// CrcChecked 是否真的校验过 CRC。
	CrcChecked bool
}

// CardStructure 卡片文件结构（只读快照，不改文件）。
type CardStructure struct {
	// FilePath 文件绝对路径。
	FilePath string
	// FileName 文件名（含扩展名）。
	FileName string
	// Size 文件字节数。
	Size int64
	// Mtime 修改时间（本地时间文本）。
	Mtime string
	// Chunks PNG 块表（按文件中出现顺序）。
	Chunks []PngChunk
	// ImageEnd 图片区结束偏移（IEND 块结束处）；-1 表示未找到。
	ImageEnd int64
	// DataSize 数据区字节数（文件尾 - 图片区结束）。
	DataSize int64
	// CardType 卡类型标记（如 【KoiKatuClothes】）。
	CardType string
	// DataVersion 数据区版本标记（如 0.0.0）。
	DataVersion string
	// DataHeadHex 数据区头部前 64 字节的十六进制。
	DataHeadHex string
	// Error 解析错误（空表示结构完整读出）。
	Error string
	// Warnings 非致命告警（失败必须可见）。
	Warnings []string
}

// ParseCardStructure 解析一张卡片的文件结构（只读，不整文件读入）。
func ParseCardStructure(path string) *CardStructure {
	st := &CardStructure{FilePath: path, ImageEnd: -1}
	fi, err := os.Stat(path)
	if err != nil {
		st.Error = "文件不存在"
		return st
	}
	abs, err := filepath.Abs(path)
	if err == nil {
		st.FilePath = abs
	}
	st.FileName = fi.Name()
	st.Size = fi.Size()
	st.Mtime = fi.ModTime().Format("2006-01-02 15:04:05")

	f, err := os.Open(path)
	if err != nil {
		st.Error = "文件不存在"
		return st
	}
	defer f.Close()

	// [段1] 读签名（对齐 C#：读 8 字节、其中前 4 字节判 PNG）
	sig := make([]byte, 8)
	if !readFullAt(f, 0, sig) || sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47 {
		st.Error = "不是 PNG 文件（签名不符）"
		return st
	}

	// [段2] 遍历块表
	hdr := make([]byte, 8)
	pos := int64(8)
	for pos+12 <= st.Size {
		if !readFullAt(f, pos, hdr) {
			st.Error = "块头读取失败（@" + itoa(pos) + "）"
			break
		}
		clen := int64(hdr[0])<<24 | int64(hdr[1])<<16 | int64(hdr[2])<<8 | int64(hdr[3])
		typ := string(hdr[4:8])
		if clen < 0 || pos+12+clen > st.Size {
			st.Error = "块长度越界（@" + itoa(pos) + " type=" + typ + " len=" + itoa(clen) + "）"
			break
		}
		ck := PngChunk{
			Type:       typ,
			Offset:     pos,
			DataOffset: pos + 8,
			Length:     clen,
			End:        pos + 12 + clen,
		}
		if clen <= crcCheckLimit {
			ck.CrcChecked = true
			ck.CrcOk = checkChunkCrc(f, ck)
		} else {
			ck.CrcChecked = false
			ck.CrcOk = true
		}
		st.Chunks = append(st.Chunks, ck)
		if typ == "IEND" {
			st.ImageEnd = ck.End
			break
		}
		pos = ck.End
	}

	// [段3] 读数据区头部（卡类型 / 数据版本 / 首 64 字节）
	if st.ImageEnd > 0 {
		st.DataSize = st.Size - st.ImageEnd
		readDataHead(f, st)
	}
	if st.ImageEnd <= 0 && st.Error == "" {
		st.Error = "未找到 IEND 块——不是可识别的卡片 PNG"
	}
	return st
}

// readDataHead 读数据区头部——卡类型 + 数据版本 + 首 64 字节 hex（页首 256 字节内）。
func readDataHead(f *os.File, st *CardStructure) {
	head := make([]byte, 256)
	n := 0
	for n < len(head) {
		m, _ := f.ReadAt(head[n:], st.ImageEnd+int64(n))
		n += m
		if m == 0 {
			break
		}
	}
	if n <= 0 {
		st.Warnings = append(st.Warnings, "数据区为空（IEND 之后没有字节）")
		return
	}
	hexLen := n
	if hexLen > 64 {
		hexLen = 64
	}
	var sb strings.Builder
	sb.Grow(hexLen * 3)
	const hexDigits = "0123456789ABCDEF"
	for i := 0; i < hexLen; i++ {
		sb.WriteByte(hexDigits[head[i]>>4])
		sb.WriteByte(hexDigits[head[i]&0x0F])
		if i+1 < hexLen {
			sb.WriteByte(' ')
		}
	}
	st.DataHeadHex = sb.String()

	if n < 6 {
		st.Warnings = append(st.Warnings, "数据区头部不足 6 字节，卡类型未读出")
		return
	}
	c := &fileCursor{f: f, pos: st.ImageEnd, size: st.ImageEnd + int64(n)}
	ct, dv, err := readCardHead(c)
	if err != nil {
		st.Warnings = append(st.Warnings, "数据区头部解析失败："+err.Error())
		st.CardType = ""
		st.DataVersion = ""
		return
	}
	st.CardType = ct
	st.DataVersion = dv
}

// checkChunkCrc 校验一个块的 CRC（类型 + 数据）。
func checkChunkCrc(f *os.File, ck PngChunk) bool {
	data := make([]byte, int(ck.Length))
	if !readFullAt(f, ck.DataOffset, data) {
		return false
	}
	crc := crc32.Update(0, crc32.IEEETable, []byte(ck.Type))
	crc = crc32.Update(crc, crc32.IEEETable, data)
	stored := make([]byte, 4)
	if !readFullAt(f, ck.DataOffset+ck.Length, stored) {
		return false
	}
	want := uint32(stored[0])<<24 | uint32(stored[1])<<16 | uint32(stored[2])<<8 | uint32(stored[3])
	return want == crc
}
