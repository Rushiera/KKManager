// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片数据区内容解析（部件 / 声明区明细 / 数据块目录 / 内嵌图片 / 插件块），
// 语义逐条对齐 KKManager/Core/CardDetail.cs：
//   - 一次流式扫描（1 MB 缓冲 + 41 字节跨块携带）定位 PNG 签名 / UAR 声明区 / lstInfo / KKEx / parts；
//   - 命中后各段局部读入（≤512 KB 窗口）逐类解析，不整文件读入；
//   - 解析失败只记告警不抛异常；告警文本与 C# 逐句同形（对拍判据）。
package core

import (
	"fmt"
	"math"
	"os"
	"strings"
)

// localWindow 局部读入窗口（锚点之后最多读这么多字节）。
const localWindow = 512 * 1024

var (
	// pngSigBytes PNG 文件签名。
	pngSigBytes = []byte{0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A}
	// lstInfoMark 数据块目录标记（fixstr(7) "lstInfo"）。
	lstInfoMark = []byte{0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F}
	// kkxMark 插件块标记 "KKEx"。
	kkxMark = []byte{0x4B, 0x4B, 0x45, 0x78}
	// partsMark 服装部件表标记（fixstr(5) "parts"）。
	partsMark = []byte{0xA5, 0x70, 0x61, 0x72, 0x74, 0x73}
)

// CardPart 数据区里的一件服装部件（parts 数组元素）。
type CardPart struct {
	// Index 部件序号（1 起）。
	Index int
	// Id 部件槽位 id（数组元素 id 字段；缺失为 -1）。
	Id int64
	// Keys 该部件记录的键名清单（如 id / colorInfo / ExtendedSaveData / emblemeId）。
	Keys []string
}

// CardModDetail 声明区（Sideloader UAR）里的一条 mod 引用——部件粒度。
type CardModDetail struct {
	// ModId mod 标识（manifest guid）。
	ModId string
	// Property 部件属性名（如 ChaFileClothes.ClothesTop）。
	Property string
	// PropertySet property 键是否读到（区分「未读到」与「读到空串」）。
	PropertySet bool
	// Slot 槽位号。
	Slot int
	// LocalSlot 本地槽位号。
	LocalSlot int
	// CategoryNo 部件分类号。
	CategoryNo int
	// Author 卡内记录的作者。
	Author string
	// AuthorSet author 键是否读到（区分「未读到」与「读到空串」——C# 侧 null 与空串输出不同）。
	AuthorSet bool
	// Website 卡内记录的来源网址。
	Website string
	// WebsiteSet website 键是否读到（区分「未读到」与「读到空串」——C# 侧 null 与空串输出不同）。
	WebsiteSet bool
	// Name 卡内记录的名称。
	Name string
	// NameSet name 键是否读到（同上——C# 侧 null 与空串输出不同）。
	NameSet bool
}

// CardImageInfo 数据区里的一张内嵌图片（PNG）。
type CardImageInfo struct {
	// Index 序号（1 起，按文件内出现顺序）。
	Index int
	// Offset 文件内绝对偏移。
	Offset int64
	// Size 图片字节数。
	Size int64
	// Width 宽（像素）。
	Width int
	// Height 高（像素）。
	Height int
	// BitDepth 位深。
	BitDepth int
	// ColorType 色型（0 灰度 / 2 RGB / 6 RGBA）。
	ColorType int
	// Chunks PNG 块数。
	Chunks int
	// Ended 是否读到 IEND（未闭合 = 数据被截断，或不是完整 PNG）。
	Ended bool
	// IsFace 是否卡面脸图（数据区头段直接引用的那一张）。
	IsFace bool
}

// CardBlockInfo 数据块目录（lstInfo）里的一条——块名 / 版本 / 位置 / 大小。
type CardBlockInfo struct {
	// Name 块名（如 KKEx / Custom / Coordinate）。
	Name string
	// Version 块版本。
	Version string
	// VersionSet version 键是否读到（区分「未读到」与「读到空串」）。
	VersionSet bool
	// Pos 块在数据区里的位置（相对数据区起点）。
	Pos int64
	// Size 块字节数。
	Size int64
}

// CardDetailResult 数据区深度解析结果（只读快照）。
type CardDetailResult struct {
	// CharName 角色名（服装卡头段记录；人物卡为空）。
	CharName string
	// CharNameSet 角色名段是否读到（区分「未读到」与「读到空串」——C# 侧 null 与空串输出不同）。
	CharNameSet bool
	// FaceImageSize 卡面脸图字节数（人物卡头段记录的 int32；无则 0）。
	FaceImageSize int64
	// FaceImageOffset 卡面脸图在文件内的偏移（无则 0）。
	FaceImageOffset int64
	// Parts 服装部件（parts 数组）。
	Parts []CardPart
	// ModRefs 声明区的 mod 引用（部件粒度）。
	ModRefs []CardModDetail
	// Images 内嵌图片清单。
	Images []CardImageInfo
	// Blocks 数据块目录（lstInfo）。
	Blocks []CardBlockInfo
	// PluginNames 插件块里记录的插件名（KKEx map 的键）。
	PluginNames []string
	// KkxOffsets 插件块（KKEx）在文件内的偏移。
	KkxOffsets []int64
	// ImageBytesTotal 内嵌图片字节合计。
	ImageBytesTotal int64
	// Warnings 解析告警（失败必须可见）。
	Warnings []string
}

// ParseCardDetail 解析数据区内容（path = 卡片文件；imageEnd = 图片区结束偏移）。
func ParseCardDetail(path string, imageEnd int64) *CardDetailResult {
	r := &CardDetailResult{}
	if _, err := os.Stat(path); err != nil {
		r.Warnings = append(r.Warnings, "卡片文件不存在——跳过数据区解析")
		return r
	}
	if imageEnd <= 0 {
		r.Warnings = append(r.Warnings, "图片区终点未知——跳过数据区解析")
		return r
	}
	f, err := os.Open(path)
	if err != nil {
		r.Warnings = append(r.Warnings, "卡片文件打不开——跳过数据区解析")
		return r
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		r.Warnings = append(r.Warnings, "卡片文件打不开——跳过数据区解析")
		return r
	}
	size := fi.Size()

	var pngHits, kkxHits []int64
	uarHit, lstHit, partsHit := int64(-1), int64(-1), int64(-1)

	if err := readDetailHead(f, size, imageEnd, r); err != nil {
		r.Warnings = append(r.Warnings, "数据区解析异常："+err.Error())
		return r
	}
	scanMarks(f, imageEnd, size, &pngHits, &kkxHits, &uarHit, &lstHit, &partsHit)

	buildImages(f, size, pngHits, r)
	if uarHit > 0 {
		parseDeclaration(f, size, uarHit, r)
	}
	if lstHit > 0 {
		parseLstInfo(f, size, lstHit, r)
	}
	if partsHit > 0 {
		parseParts(f, size, partsHit, r)
	}
	r.KkxOffsets = append(r.KkxOffsets, kkxHits...)
	if uarHit > 0 {
		parsePlugins(f, size, uarHit, r)
	}
	return r
}

// readDetailHead 读数据区头段——卡类型（场景卡 / 人物卡 / 服装卡）与角色名（服装卡）或脸图长度（人物卡）。
func readDetailHead(f *os.File, size, imageEnd int64, r *CardDetailResult) error {
	c := &fileCursor{f: f, pos: imageEnd, size: size}
	cardType, _, err := readCardHead(c)
	if err != nil {
		return err
	}
	if cardType == SceneCardType {
		// 场景卡：头段只有场景数据版本，其后就是场景数据——没有角色名段、没有脸图
		return nil
	}
	afterHead := c.pos

	if strings.Contains(cardType, "Clothes") {
		name, err := read7BitString(c)
		if err != nil {
			return err
		}
		r.CharName = name
		r.CharNameSet = true
		return nil
	}

	// 人物卡：int32 脸图长度 + 脸图 PNG
	if size-afterHead < 16 {
		return nil
	}
	head := make([]byte, 16)
	if !readFullAt(f, afterHead, head) {
		return nil
	}
	l := int64(head[0]) | int64(head[1])<<8 | int64(head[2])<<16 | int64(head[3])<<24
	isPng := true
	for i := 0; i < len(pngSigBytes); i++ {
		if head[4+i] != pngSigBytes[i] {
			isPng = false
			break
		}
	}
	if isPng && l > 0 && afterHead+4+l <= size {
		r.FaceImageOffset = afterHead + 4
		r.FaceImageSize = l
	} else if !isPng {
		r.Warnings = append(r.Warnings, "人物卡头段之后不是 PNG 签名——脸图长度未确认")
	}
	return nil
}

// scanMarks 流式扫描数据区，收集 PNG 签名 / 声明区 / 数据块目录 / 插件块 / 服装部件表的位置。
func scanMarks(f *os.File, from, size int64, pngHits, kkxHits *[]int64, uarHit, lstHit, partsHit *int64) {
	if from < 0 || from >= size {
		return
	}
	const bufSize = 1 << 20
	const maxPng = 2000
	maxLen := len(pngSigBytes)
	if len(uarMark) > maxLen {
		maxLen = len(uarMark)
	}
	if len(lstInfoMark) > maxLen {
		maxLen = len(lstInfoMark)
	}

	buf := make([]byte, bufSize+64)
	pos := from
	baseOffset := from
	carry := 0
	for {
		n, _ := f.ReadAt(buf[carry:carry+bufSize], pos)
		pos += int64(n)
		if n <= 0 {
			break
		}
		total := carry + n
		limit := total - maxLen
		if limit < 0 {
			carry = maxLen - 1
			if carry > total {
				carry = total
			}
			copy(buf[:carry], buf[total-carry:total])
			continue
		}
		span := buf[:limit+1]

		searchFrom := 0
		for searchFrom <= limit {
			hit := indexOf(span, pngSigBytes, searchFrom)
			if hit < 0 {
				break
			}
			if len(*pngHits) < maxPng {
				*pngHits = append(*pngHits, baseOffset+int64(hit))
			}
			searchFrom = hit + 1
		}
		if *uarHit < 0 {
			if hit := indexOf(span, uarMark, 0); hit >= 0 {
				*uarHit = baseOffset + int64(hit)
			}
		}
		if *lstHit < 0 {
			if hit := indexOf(span, lstInfoMark, 0); hit >= 0 {
				*lstHit = baseOffset + int64(hit)
			}
		}
		searchFrom = 0
		for searchFrom <= limit {
			hit := indexOf(span, kkxMark, searchFrom)
			if hit < 0 {
				break
			}
			*kkxHits = append(*kkxHits, baseOffset+int64(hit))
			searchFrom = hit + 1
		}
		if *partsHit < 0 {
			if hit := indexOf(span, partsMark, 0); hit >= 0 {
				*partsHit = baseOffset + int64(hit)
			}
		}

		carry = maxLen - 1
		if carry > total {
			carry = total
		}
		copy(buf[:carry], buf[total-carry:total])
		baseOffset += int64(total - carry)
	}
}

// indexOf 在 buf 的 from 之后查找 pattern（对齐 Span.IndexOf 的子串语义）。
func indexOf(buf, pattern []byte, from int) int {
	if from < 0 || from > len(buf) {
		return -1
	}
	if len(pattern) == 0 {
		return from
	}
	for i := from; i+len(pattern) <= len(buf); i++ {
		match := true
		for k := 0; k < len(pattern); k++ {
			if buf[i+k] != pattern[k] {
				match = false
				break
			}
		}
		if match {
			return i
		}
	}
	return -1
}

// buildImages 逐张解析内嵌 PNG（块表 → 宽高 / 位深 / 色型 / 字节数）。
func buildImages(f *os.File, size int64, hits []int64, r *CardDetailResult) {
	hdr := make([]byte, 8)
	for k, off := range hits {
		img := CardImageInfo{Index: k + 1, Offset: off}
		pos := off + int64(len(pngSigBytes))
		bytesN := int64(len(pngSigBytes))
		chunks := 0
		for chunks < 20000 {
			if !readFullAt(f, pos, hdr) {
				break
			}
			clen := int64(hdr[0])<<24 | int64(hdr[1])<<16 | int64(hdr[2])<<8 | int64(hdr[3])
			typ := string(hdr[4:8])
			if clen < 0 || pos+12+clen > size {
				break
			}
			if typ == "IHDR" && clen == 13 {
				ih := make([]byte, 13)
				if readFullAt(f, pos+8, ih) {
					img.Width = int(int32(uint32(ih[0])<<24 | uint32(ih[1])<<16 | uint32(ih[2])<<8 | uint32(ih[3])))
					img.Height = int(int32(uint32(ih[4])<<24 | uint32(ih[5])<<16 | uint32(ih[6])<<8 | uint32(ih[7])))
					img.BitDepth = int(ih[8])
					img.ColorType = int(ih[9])
				}
			}
			chunks++
			bytesN = pos + 12 + clen - off
			if typ == "IEND" {
				img.Ended = true
				break
			}
			pos += 12 + clen
		}
		img.Chunks = chunks
		img.Size = bytesN
		r.ImageBytesTotal += bytesN
		r.Images = append(r.Images, img)
	}

	if r.FaceImageOffset > 0 {
		for i := range r.Images {
			if r.Images[i].Offset == r.FaceImageOffset {
				r.Images[i].IsFace = true
				break
			}
		}
	}
}

// parseDeclaration 解析声明区——Sideloader UAR 的 info 数组（部件级 mod 引用）。
func parseDeclaration(f *os.File, size, uarAt int64, r *CardDetailResult) {
	buf := readWindow(f, uarAt+int64(len(uarMark)), size, localWindow)
	if buf == nil {
		r.Warnings = append(r.Warnings, "声明区读取失败——mod 明细未解析")
		return
	}
	cur := &msgCursor{buf: buf}
	n, ok := cur.tryReadArrayHeader()
	if !ok {
		r.Warnings = append(r.Warnings, "声明区结构不符（不是数组）——mod 明细未解析")
		return
	}
	if n >= 1 && !cur.trySkipValue() {
		r.Warnings = append(r.Warnings, "声明区首元素跳过失败——mod 明细未解析")
		return
	}
	n, ok = cur.tryReadMapHeader()
	if !ok {
		r.Warnings = append(r.Warnings, "声明区结构不符（不是 map）——mod 明细未解析")
		return
	}
	foundInfo := false
	for i := 0; i < n; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			break
		}
		if key == "info" {
			foundInfo = true
			count, ok := cur.tryReadArrayHeader()
			if !ok {
				r.Warnings = append(r.Warnings, "声明区 info 不是数组——mod 明细未解析")
				return
			}
			for j := 0; j < count; j++ {
				if !readModEntry(cur, r) {
					r.Warnings = append(r.Warnings,
						fmt.Sprintf("声明区第 %d 条解析中断——已解析 %d 条", j+1, len(r.ModRefs)))
					break
				}
			}
			break
		}
		if !cur.trySkipValue() {
			break
		}
	}
	if !foundInfo {
		r.Warnings = append(r.Warnings, "声明区未找到 info 段——mod 明细未解析")
	}
}

// readModEntry 读一条声明区条目（map：ModID / Property / Slot / LocalSlot / CategoryNo / Author / Website / Name）。
func readModEntry(cur *msgCursor, r *CardDetailResult) bool {
	// 条目常被 bin8 / bin16 包裹（实测 C4 AB …）——剥掉 bin 头后即 map
	head := cur.peekByte()
	if head == 0xC4 || head == 0xC5 || head == 0xC6 {
		if !cur.trySkipBinHeader() {
			return false
		}
	}
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	d := CardModDetail{Slot: -1, LocalSlot: -1, CategoryNo: -1}
	for i := 0; i < keys; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		switch key {
		case "ModID", "Property", "Author", "Website", "Name":
			v, ok := cur.tryReadString()
			if !ok {
				return false
			}
			switch key {
			case "ModID":
				d.ModId = v
			case "Property":
				d.Property = v
				d.PropertySet = true
			case "Author":
				d.Author = v
				d.AuthorSet = true
			case "Website":
				d.Website = v
				d.WebsiteSet = true
			default:
				d.Name = v
				d.NameSet = true
			}
		case "Slot", "LocalSlot", "CategoryNo":
			v, ok := cur.tryReadLong()
			if !ok {
				return false
			}
			switch key {
			case "Slot":
				d.Slot = int(v)
			case "LocalSlot":
				d.LocalSlot = int(v)
			default:
				d.CategoryNo = int(v)
			}
		default:
			if !cur.trySkipValue() {
				return false
			}
		}
	}
	if d.ModId != "" {
		r.ModRefs = append(r.ModRefs, d)
	}
	return true
}

// parseLstInfo 解析数据块目录（lstInfo）——块名 / 版本 / 位置 / 大小。
func parseLstInfo(f *os.File, size, lstAt int64, r *CardDetailResult) {
	buf := readWindow(f, lstAt+int64(len(lstInfoMark)), size, localWindow)
	if buf == nil {
		r.Warnings = append(r.Warnings, "数据块目录读取失败——目录未解析")
		return
	}
	cur := &msgCursor{buf: buf}
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		r.Warnings = append(r.Warnings, "数据块目录不是数组——目录未解析")
		return
	}
	for i := 0; i < count; i++ {
		if !readBlockEntry(cur, r) {
			r.Warnings = append(r.Warnings,
				fmt.Sprintf("数据块目录第 %d 条解析中断——已解析 %d 条", i+1, len(r.Blocks)))
			break
		}
	}
}

// readBlockEntry 读一条数据块目录条目（map：name / version / pos / size）。
func readBlockEntry(cur *msgCursor, r *CardDetailResult) bool {
	keys, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	var b CardBlockInfo
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
	if b.Name != "" {
		r.Blocks = append(r.Blocks, b)
	}
	return true
}

// parseParts 解析服装部件表（parts 数组）——件数 + 每件的 id 与键名。
func parseParts(f *os.File, size, partsAt int64, r *CardDetailResult) {
	buf := readWindow(f, partsAt+int64(len(partsMark)), size, localWindow)
	if buf == nil {
		r.Warnings = append(r.Warnings, "服装部件表读取失败——部件未解析")
		return
	}
	cur := &msgCursor{buf: buf}
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		r.Warnings = append(r.Warnings, "服装部件表不是数组——部件未解析")
		return
	}
	for i := 0; i < count; i++ {
		p := CardPart{Index: i + 1, Id: -1}
		keys, ok := cur.tryReadMapHeader()
		if !ok {
			r.Warnings = append(r.Warnings, fmt.Sprintf("服装部件表第 %d 件解析中断", i+1))
			break
		}
		entryOk := true
		for k := 0; k < keys; k++ {
			key, ok := cur.tryReadString()
			if !ok {
				entryOk = false
				break
			}
			p.Keys = append(p.Keys, key)
			if key == "id" {
				v, ok := cur.tryReadLong()
				if ok {
					p.Id = v
					continue
				}
				entryOk = false
				break
			}
			if !cur.trySkipValue() {
				entryOk = false
				break
			}
		}
		r.Parts = append(r.Parts, p)
		if !entryOk {
			r.Warnings = append(r.Warnings, fmt.Sprintf("服装部件表第 %d 件解析不完整", i+1))
			break
		}
	}
}

// parsePlugins 解析插件块（KKEx）——取 map 键名（插件标识）。
func parsePlugins(f *os.File, size, uarAt int64, r *CardDetailResult) {
	if uarAt <= 0 {
		return
	}
	from := uarAt - 64
	if from < 0 {
		from = 0
	}
	buf := readWindow(f, from, size, localWindow)
	if buf == nil {
		return
	}

	// 声明区所在的插件 map——键即插件标识；map 头落在 UAR 名（str8）之前最多 8 字节内
	at := int(uarAt - from)
	for back := 1; back <= 8; back++ {
		idx := at - back
		if idx < 0 {
			break
		}
		b := buf[idx]
		isMap := (b >= 0x80 && b <= 0x8F) || b == 0xDE || b == 0xDF
		if !isMap {
			continue
		}
		cur := &msgCursor{buf: buf, pos: idx}
		keys, ok := cur.tryReadMapHeader()
		if !ok {
			continue
		}
		firstName, ok := cur.tryReadString()
		if !ok {
			continue
		}
		if firstName != "com.bepis.sideloader.universalautoresolver" {
			continue
		}
		r.PluginNames = append(r.PluginNames, firstName)

		// 键 1 的值（声明区数据）先跳过——否则后续键名会被当成值解析
		if !cur.trySkipValue() {
			r.Warnings = append(r.Warnings, "插件名列表不完整（声明区数据越出读取窗口）")
			return
		}
		for k := 1; k < keys; k++ {
			name, ok := cur.tryReadString()
			if !ok {
				r.Warnings = append(r.Warnings,
					fmt.Sprintf("插件名列表可能不完整（第 %d 个键越出读取窗口）", k+1))
				break
			}
			if name != "" && !containsString(r.PluginNames, name) {
				r.PluginNames = append(r.PluginNames, name)
			}
			if !cur.trySkipValue() {
				r.Warnings = append(r.Warnings,
					fmt.Sprintf("插件名列表可能不完整（第 %d 个键的值越出读取窗口）", k+1))
				break
			}
		}
		break
	}
}

// containsString 字符串集合包含判断（顺序保留）。
func containsString(list []string, v string) bool {
	for _, s := range list {
		if s == v {
			return true
		}
	}
	return false
}

// readWindow 从指定偏移读入一个局部窗口（不足则按实际长度返回；读失败返回 nil）。
func readWindow(f *os.File, from, size int64, want int) []byte {
	if from < 0 || from >= size {
		return nil
	}
	remain := size - from
	length := int64(want)
	if remain < length {
		length = remain
	}
	buf := make([]byte, length)
	done := 0
	for int64(done) < length {
		m, _ := f.ReadAt(buf[done:], from+int64(done))
		done += m
		if m == 0 {
			break
		}
	}
	if done <= 0 {
		return nil
	}
	return buf[:done]
}

// msgCursor 内存里的 MessagePack 顺序读取器——只读与跳过（对齐 C# MsgPackCursor）。
type msgCursor struct {
	buf []byte
	pos int
}

// peekByte 看当前字节（不前进；到末尾返回 0）。
func (c *msgCursor) peekByte() byte {
	if c.pos >= len(c.buf) {
		return 0
	}
	return c.buf[c.pos]
}

// tryReadMapHeader 读 map 头（键数）。
func (c *msgCursor) tryReadMapHeader() (int, bool) {
	if c.pos >= len(c.buf) {
		return 0, false
	}
	b := c.buf[c.pos]
	if b >= 0x80 && b <= 0x8F {
		c.pos++
		return int(b & 0x0F), true
	}
	if b == 0xDE && c.pos+3 <= len(c.buf) {
		n := int(c.buf[c.pos+1])<<8 | int(c.buf[c.pos+2])
		c.pos += 3
		return n, true
	}
	if b == 0xDF && c.pos+5 <= len(c.buf) {
		n := int(c.buf[c.pos+1])<<24 | int(c.buf[c.pos+2])<<16 | int(c.buf[c.pos+3])<<8 | int(c.buf[c.pos+4])
		c.pos += 5
		return n, true
	}
	return 0, false
}

// tryReadArrayHeader 读数组头（元素数）。
func (c *msgCursor) tryReadArrayHeader() (int, bool) {
	if c.pos >= len(c.buf) {
		return 0, false
	}
	b := c.buf[c.pos]
	if b >= 0x90 && b <= 0x9F {
		c.pos++
		return int(b & 0x0F), true
	}
	if b == 0xDC && c.pos+3 <= len(c.buf) {
		n := int(c.buf[c.pos+1])<<8 | int(c.buf[c.pos+2])
		c.pos += 3
		return n, true
	}
	if b == 0xDD && c.pos+5 <= len(c.buf) {
		n := int(c.buf[c.pos+1])<<24 | int(c.buf[c.pos+2])<<16 | int(c.buf[c.pos+3])<<8 | int(c.buf[c.pos+4])
		c.pos += 5
		return n, true
	}
	return 0, false
}

// tryReadString 读字符串（fixstr / str8 / str16 / str32）。
func (c *msgCursor) tryReadString() (string, bool) {
	if c.pos >= len(c.buf) {
		return "", false
	}
	b := c.buf[c.pos]
	var l int
	switch {
	case b >= 0xA0 && b <= 0xBF:
		l = int(b & 0x1F)
		c.pos++
	case b == 0xD9 && c.pos+2 <= len(c.buf):
		l = int(c.buf[c.pos+1])
		c.pos += 2
	case b == 0xDA && c.pos+3 <= len(c.buf):
		l = int(c.buf[c.pos+1])<<8 | int(c.buf[c.pos+2])
		c.pos += 3
	case b == 0xDB && c.pos+5 <= len(c.buf):
		l = int(c.buf[c.pos+1])<<24 | int(c.buf[c.pos+2])<<16 | int(c.buf[c.pos+3])<<8 | int(c.buf[c.pos+4])
		c.pos += 5
	default:
		return "", false
	}
	if l < 0 || c.pos+l > len(c.buf) {
		return "", false
	}
	s := string(c.buf[c.pos : c.pos+l])
	c.pos += l
	return s, true
}

// tryReadLong 读整数（各宽度整型）。
func (c *msgCursor) tryReadLong() (int64, bool) {
	if c.pos >= len(c.buf) {
		return 0, false
	}
	b := c.buf[c.pos]
	if b <= 0x7F {
		c.pos++
		return int64(b), true
	}
	if b >= 0xE0 {
		c.pos++
		return int64(int8(b)), true
	}
	switch b {
	case 0xCC:
		if c.pos+2 > len(c.buf) {
			return 0, false
		}
		v := int64(c.buf[c.pos+1])
		c.pos += 2
		return v, true
	case 0xCD:
		if c.pos+3 > len(c.buf) {
			return 0, false
		}
		v := int64(c.buf[c.pos+1])<<8 | int64(c.buf[c.pos+2])
		c.pos += 3
		return v, true
	case 0xCE:
		if c.pos+5 > len(c.buf) {
			return 0, false
		}
		v := int64(c.buf[c.pos+1])<<24 | int64(c.buf[c.pos+2])<<16 | int64(c.buf[c.pos+3])<<8 | int64(c.buf[c.pos+4])
		c.pos += 5
		return v, true
	case 0xD0:
		if c.pos+2 > len(c.buf) {
			return 0, false
		}
		v := int64(int8(c.buf[c.pos+1]))
		c.pos += 2
		return v, true
	case 0xD1:
		if c.pos+3 > len(c.buf) {
			return 0, false
		}
		v := int64(int16(uint16(c.buf[c.pos+1])<<8 | uint16(c.buf[c.pos+2])))
		c.pos += 3
		return v, true
	case 0xD2:
		if c.pos+5 > len(c.buf) {
			return 0, false
		}
		u := uint32(c.buf[c.pos+1])<<24 | uint32(c.buf[c.pos+2])<<16 | uint32(c.buf[c.pos+3])<<8 | uint32(c.buf[c.pos+4])
		c.pos += 5
		return int64(int32(u)), true
	}
	return 0, false
}

// trySkipValue 按类型跳过一个值（含嵌套容器）。
func (c *msgCursor) trySkipValue() bool {
	return c.skipValue(0)
}

// skipValue 按类型跳过一个值（带嵌套深度保护）。
func (c *msgCursor) skipValue(depth int) bool {
	if depth > 64 || c.pos >= len(c.buf) {
		return false
	}
	b := c.buf[c.pos]
	switch {
	case b <= 0x7F || b >= 0xE0:
		c.pos++
		return true
	case b >= 0xA0 && b <= 0xBF:
		l := int(b & 0x1F)
		c.pos += 1 + l
		return c.pos <= len(c.buf)
	case b >= 0x90 && b <= 0x9F:
		n := int(b & 0x0F)
		c.pos++
		return c.skipItems(n, depth)
	case b >= 0x80 && b <= 0x8F:
		n := int(b&0x0F) * 2
		c.pos++
		return c.skipItems(n, depth)
	case b == 0xC0 || b == 0xC2 || b == 0xC3:
		c.pos++
		return true
	case b == 0xC4 || b == 0xC5 || b == 0xC6:
		width := 1
		if b == 0xC5 {
			width = 2
		} else if b == 0xC6 {
			width = 4
		}
		return c.skipPayload(width)
	case b == 0xCA:
		c.pos += 5
		return c.pos <= len(c.buf)
	case b == 0xCB:
		c.pos += 9
		return c.pos <= len(c.buf)
	case b == 0xCC || b == 0xD0:
		c.pos += 2
		return c.pos <= len(c.buf)
	case b == 0xCD || b == 0xD1:
		c.pos += 3
		return c.pos <= len(c.buf)
	case b == 0xCE || b == 0xD2:
		c.pos += 5
		return c.pos <= len(c.buf)
	case b == 0xCF || b == 0xD3:
		c.pos += 9
		return c.pos <= len(c.buf)
	case b == 0xD9 || b == 0xDA || b == 0xDB:
		width := 1
		if b == 0xDA {
			width = 2
		} else if b == 0xDB {
			width = 4
		}
		return c.skipPayload(width)
	case b == 0xDC || b == 0xDD:
		n, ok := c.tryReadArrayHeader()
		if !ok {
			return false
		}
		return c.skipItems(n, depth+1)
	case b == 0xDE || b == 0xDF:
		n, ok := c.tryReadMapHeader()
		if !ok {
			return false
		}
		return c.skipItems(n*2, depth+1)
	}
	return false
}

// skipPayload 按长度前缀（1 / 2 / 4 字节）跳过负载。
func (c *msgCursor) skipPayload(width int) bool {
	if c.pos+1+width > len(c.buf) {
		return false
	}
	var l int64
	switch width {
	case 1:
		l = int64(c.buf[c.pos+1])
	case 2:
		l = int64(c.buf[c.pos+1])<<8 | int64(c.buf[c.pos+2])
	default:
		l = int64(c.buf[c.pos+1])<<24 | int64(c.buf[c.pos+2])<<16 | int64(c.buf[c.pos+3])<<8 | int64(c.buf[c.pos+4])
	}
	c.pos += 1 + width
	if l < 0 || int64(c.pos)+l > int64(len(c.buf)) {
		return false
	}
	c.pos += int(l)
	return true
}

// skipItems 连续跳过 n 个值。
func (c *msgCursor) skipItems(n, depth int) bool {
	for i := 0; i < n; i++ {
		if !c.skipValue(depth + 1) {
			return false
		}
	}
	return true
}

// tryReadDouble 读浮点（float32 / float64）。
func (c *msgCursor) tryReadDouble() (float64, bool) {
	if c.pos >= len(c.buf) {
		return 0, false
	}
	b := c.buf[c.pos]
	if b == 0xCA && c.pos+5 <= len(c.buf) {
		bits := uint32(c.buf[c.pos+1])<<24 | uint32(c.buf[c.pos+2])<<16 | uint32(c.buf[c.pos+3])<<8 | uint32(c.buf[c.pos+4])
		v := float64(math.Float32frombits(bits))
		c.pos += 5
		return v, true
	}
	if b == 0xCB && c.pos+9 <= len(c.buf) {
		bits := uint64(c.buf[c.pos+1])<<56 | uint64(c.buf[c.pos+2])<<48 | uint64(c.buf[c.pos+3])<<40 |
			uint64(c.buf[c.pos+4])<<32 | uint64(c.buf[c.pos+5])<<24 | uint64(c.buf[c.pos+6])<<16 |
			uint64(c.buf[c.pos+7])<<8 | uint64(c.buf[c.pos+8])
		v := math.Float64frombits(bits)
		c.pos += 9
		return v, true
	}
	return 0, false
}

// tryReadBool 读布尔（MessagePack true / false）。
func (c *msgCursor) tryReadBool() (bool, bool) {
	if c.pos >= len(c.buf) {
		return false, false
	}
	b := c.buf[c.pos]
	if b == 0xC2 {
		c.pos++
		return false, true
	}
	if b == 0xC3 {
		c.pos++
		return true, true
	}
	return false, false
}

// trySkipBinHeader 读 bin 头（bin8 / bin16 / bin32）——只前进头部，负载由调用方按需继续解析。
func (c *msgCursor) trySkipBinHeader() bool {
	if c.pos >= len(c.buf) {
		return false
	}
	b := c.buf[c.pos]
	switch {
	case b == 0xC4 && c.pos+2 <= len(c.buf):
		c.pos += 2
		return true
	case b == 0xC5 && c.pos+3 <= len(c.buf):
		c.pos += 3
		return true
	case b == 0xC6 && c.pos+5 <= len(c.buf):
		c.pos += 5
		return true
	}
	return false
}
