// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片文件解析（PNG 图片区 / 数据区分界 + 卡类型 + Sideloader 声明区），
// 语义逐条对齐 KKManager/Core/CardReader.cs：
//   - 数据区头段先按「版本号形态」试场景卡（sd），不成立再按「int32 标记 + 卡类型 + 数据版本」读；
//   - 声明区按 MessagePack 锚点 fixstr(5)"ModID"（0xA5 'M' 'o' 'd' 'I' 'D'）扫描，不依赖 ChaFile 全解；
//   - 扫描窗口：1 MB → 16 MB → 全扫回落（实测声明区恒定落在数据区起点后 200–400 KB）。
package core

import (
	"bytes"
	"fmt"
	"io"
	"os"
)

// SceneCardType 场景卡（Studio 场景）的类型记录值——数据区头段是 7bit 版本串。
const SceneCardType = "sd"

// ModRef 卡片的一条 mod 引用（本轮到 ModId 粒度；槽位字段按声明区解析后续补）。
type ModRef struct {
	// ModId 即该 zipmod 的 manifest guid（不是文件名）。
	ModId string
}

// CardInfo 卡片解析结果。
type CardInfo struct {
	// FilePath 卡片绝对路径。
	FilePath string
	// FileName 卡片文件名（含扩展名）。
	FileName string
	// Size 文件字节数。
	Size int64
	// ImageEnd PNG 图片区结束偏移（IEND chunk 之后）；-1 表示未找到。
	ImageEnd int64
	// CardType 卡类型标记（如 【KoiKatuClothes】/【KoiKatuChara】/ sd）。
	CardType string
	// DataVersion 数据区标记（如 0.0.0）。
	DataVersion string
	// UarBlocks Sideloader UAR 声明区出现次数。
	UarBlocks int
	// CharaName 服装卡数据区头段的角色名段（人物卡为空——名字在 Parameter 块）。
	CharaName string
	// ModRefs 卡片声明的 mod 引用（部件粒度，未去重）。
	ModRefs []ModRef
}

// DistinctModIds 去重后的 mod 标识（保持首次出现顺序）。
func (c *CardInfo) DistinctModIds() []string {
	seen := make(map[string]bool, len(c.ModRefs))
	list := make([]string, 0, len(c.ModRefs))
	for _, r := range c.ModRefs {
		if r.ModId != "" && !seen[r.ModId] {
			seen[r.ModId] = true
			list = append(list, r.ModId)
		}
	}
	return list
}

// ParseCard 解析一张卡片（流式，不整文件读入）。
func ParseCard(path string) (*CardInfo, error) {
	fi, err := os.Stat(path)
	if err != nil {
		return nil, fmt.Errorf("卡片不存在: %s", path)
	}
	info := &CardInfo{FilePath: path, FileName: fi.Name(), Size: fi.Size()}
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	info.ImageEnd = findPngEnd(f, fi.Size())
	if info.ImageEnd > 0 && fi.Size()-info.ImageEnd >= 5 {
		c := &fileCursor{f: f, pos: info.ImageEnd, size: fi.Size()}
		ct, dv, err := readCardHead(c)
		if err != nil {
			return nil, fmt.Errorf("数据区头部解析失败: %v", err)
		}
		info.CardType = ct
		info.DataVersion = dv
		// 服装卡：数据区头段第三段是角色名（人物卡此处是脸图长度，不能按字符串读）
		if bytes.Contains([]byte(info.CardType), []byte("Clothes")) && fi.Size()-c.pos >= 1 {
			name, err := read7BitString(c)
			if err != nil {
				return nil, fmt.Errorf("服装卡角色名读取失败: %v", err)
			}
			info.CharaName = name
		}
	}
	collectDeclaration(f, info)
	return info, nil
}

// ReadCardHead 读卡片数据区头段——先按「版本号形态」试场景卡，不成立再按卡片布局读。
// 语义对齐 C# CardReader.ReadCardHead（被 CardDocument 复用）。
func readCardHead(c *fileCursor) (cardType, dataVersion string, err error) {
	if v := tryReadSceneVersion(c); v != "" {
		return SceneCardType, v, nil
	}
	if _, err = c.readInt32(); err != nil {
		return "", "", err
	}
	cardType, err = read7BitString(c)
	if err != nil {
		return "", "", err
	}
	if c.size-c.pos >= 1 {
		dataVersion, err = read7BitString(c)
		if err != nil {
			return "", "", err
		}
	}
	return cardType, dataVersion, nil
}

// tryReadSceneVersion 试读场景卡数据区首段——是版本号形态则取走（位置停在版本串之后），否则位置不动返回空。
func tryReadSceneVersion(c *fileCursor) string {
	start := c.pos
	if c.size-start < 2 {
		return ""
	}
	lenB, err := c.readByte()
	if err != nil {
		return ""
	}
	l := int(lenB)
	if l <= 0 || l > 16 || c.size-c.pos < int64(l) {
		c.pos = start
		return ""
	}
	body, err := c.readFull(l)
	if err != nil {
		c.pos = start
		return ""
	}
	c.pos = start
	s := string(body)
	if !IsVersionLike(s) {
		return ""
	}
	c.pos = start + 1 + int64(l)
	return s
}

// IsVersionLike 版本号形态判据——只含数字与点、至少一个点、长度 1..16。
func IsVersionLike(s string) bool {
	if len(s) == 0 || len(s) > 16 {
		return false
	}
	dot := false
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c == '.' {
			dot = true
			continue
		}
		if c < '0' || c > '9' {
			return false
		}
	}
	return dot
}

// findPngEnd 定位 PNG 的 IEND 结束偏移（图片区终点）；未找到返回 -1。
func findPngEnd(f *os.File, size int64) int64 {
	hdr := make([]byte, 8)
	pos := int64(8)
	for pos+8 <= size {
		if !readFullAt(f, pos, hdr) {
			return -1
		}
		clen := int64(hdr[0])<<24 | int64(hdr[1])<<16 | int64(hdr[2])<<8 | int64(hdr[3])
		typ := string(hdr[4:8])
		if typ == "IEND" {
			return pos + 12
		}
		if clen < 0 || pos+12+clen > size {
			return -1
		}
		pos += 12 + clen
	}
	return -1
}

var (
	modIDAnchor = []byte{0xA5, 'M', 'o', 'd', 'I', 'D'}
	uarMark     = []byte("com.bepis.sideloader.universalautoresolver")
)

// collectDeclaration 扫描数据区，收集 ModID 引用与 UAR 标记计数。
// 窗口逐级扩大（1 MB → 16 MB → 全扫回落），扫满首窗已命中即停（声明区每卡只有一处）。
func collectDeclaration(f *os.File, info *CardInfo) {
	start := info.ImageEnd
	if start <= 0 {
		start = 0
	}
	limits := []int64{1024 * 1024, 16 * 1024 * 1024}
	scanned := start
	for _, lim := range limits {
		to := start + lim
		if to > info.Size {
			to = info.Size
		}
		if to <= scanned {
			continue
		}
		scanRegion(f, info, scanned, to)
		scanned = to
		if len(info.ModRefs) > 0 {
			return
		}
	}
	if scanned < info.Size {
		scanRegion(f, info, scanned, info.Size)
	}
}

// scanRegion 在一个字节区间内扫 ModID 锚点与 UAR 标记（1 MB 缓冲 + 63 字节跨块携带）。
func scanRegion(f *os.File, info *CardInfo, from, to int64) {
	const bufSize = 1 << 20
	buf := make([]byte, bufSize+64)
	pos := from
	remaining := to - from
	carry := 0
	for remaining > 0 {
		want := bufSize
		if int64(want) > remaining {
			want = int(remaining)
		}
		n, _ := f.ReadAt(buf[carry:carry+want], pos)
		pos += int64(n)
		remaining -= int64(n)
		if n <= 0 {
			break
		}
		total := carry + n

		// 锚点定位 + MessagePack 字符串读取
		off := 0
		for off <= total-len(modIDAnchor) {
			hit := bytes.Index(buf[off:total], modIDAnchor)
			if hit < 0 {
				break
			}
			abs := off + hit
			if s, ok := readMsgPackString(buf[:total], abs+len(modIDAnchor)); ok && s != "" {
				info.ModRefs = append(info.ModRefs, ModRef{ModId: s})
			}
			off = abs + 1
		}
		// UAR 标记计数
		uo := 0
		for uo <= total-len(uarMark) {
			hit := bytes.Index(buf[uo:total], uarMark)
			if hit < 0 {
				break
			}
			info.UarBlocks++
			uo += hit + 1
		}

		carry = 63
		if total < 63 {
			carry = total
		}
		copy(buf[:carry], buf[total-carry:total])
	}
}

// readMsgPackString 按 MessagePack 字符串规则读一个字符串（返回是否成功）。
// 对齐 C# CardReader.ReadMsgPackString：fixstr / str8 / str16 / str32。
func readMsgPackString(buf []byte, p int) (string, bool) {
	total := len(buf)
	if p >= total {
		return "", false
	}
	b := buf[p]
	var l int
	switch {
	case b >= 0xA0 && b <= 0xBF: // fixstr
		l = int(b & 0x1F)
		p++
	case b == 0xD9: // str8
		if p+2 > total {
			return "", false
		}
		l = int(buf[p+1])
		p += 2
	case b == 0xDA: // str16
		if p+3 > total {
			return "", false
		}
		l = int(buf[p+1])<<8 | int(buf[p+2])
		p += 3
	case b == 0xDB: // str32
		if p+5 > total {
			return "", false
		}
		l = int(buf[p+1])<<24 | int(buf[p+2])<<16 | int(buf[p+3])<<8 | int(buf[p+4])
		p += 5
	default:
		return "", false
	}
	if l < 0 || p+l > total {
		return "", false
	}
	return string(buf[p : p+l]), true
}

// fileCursor 带显式偏移的文件游标（对齐 C# BinaryReader 语义：越界即错）。
type fileCursor struct {
	f    *os.File
	pos  int64
	size int64
}

func (c *fileCursor) readByte() (byte, error) {
	var b [1]byte
	n, _ := c.f.ReadAt(b[:], c.pos)
	if n != 1 {
		return 0, io.ErrUnexpectedEOF
	}
	c.pos++
	return b[0], nil
}

func (c *fileCursor) readFull(n int) ([]byte, error) {
	if n < 0 {
		return nil, io.ErrUnexpectedEOF
	}
	buf := make([]byte, n)
	done := 0
	for done < n {
		m, err := c.f.ReadAt(buf[done:], c.pos+int64(done))
		done += m
		if done == n {
			break
		}
		if err != nil {
			return nil, io.ErrUnexpectedEOF
		}
	}
	c.pos += int64(n)
	return buf, nil
}

func (c *fileCursor) readInt32() (int32, error) {
	b, err := c.readFull(4)
	if err != nil {
		return 0, err
	}
	return int32(uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24), nil
}

// read7BitString 读 7-bit 长度前缀的 UTF-8 字符串（BinaryWriter.Write(string) 形态）。
func read7BitString(c *fileCursor) (string, error) {
	length := 0
	shift := 0
	for {
		b, err := c.readByte()
		if err != nil {
			return "", err
		}
		length |= int(b&0x7F) << shift
		if b&0x80 == 0 {
			break
		}
		shift += 7
		if shift > 28 {
			return "", fmt.Errorf("7-bit 长度前缀异常")
		}
	}
	raw, err := c.readFull(length)
	if err != nil {
		return "", err
	}
	return string(raw), nil
}

// readFullAt 从偏移处读满 buf（不足返回 false）。
func readFullAt(f *os.File, off int64, buf []byte) bool {
	done := 0
	for done < len(buf) {
		n, err := f.ReadAt(buf[done:], off+int64(done))
		done += n
		if done == len(buf) {
			return true
		}
		if err != nil {
			return false
		}
	}
	return true
}
