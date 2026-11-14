// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：场景卡（sd）数据区深度分析——插件数据条目 / 内嵌角色数据份数 / kkpe 道具名 / vnge_sssb 对象数。
// 语义逐条对齐 KKManager/Core/SceneReader.cs：
//   - 一遍 1 MB 流式扫描（192 字节跨块携带）找 lstInfo 锚点、份头段锚点（int32 100 + 卡类型串）与 0x92 00 条目起点；
//   - 条目形态实测：fixstr 键名 92 00 81 A9 "sceneInfo" <str>（XML 型）或 fixstr 键名 92 00 82 A4 "main" <值>（VNGE 型）；
//   - 只读：不写文件、不整文件读入；解析失败只记告警不抛异常（告警文本与 C# 逐句同形）。
package core

import (
	"encoding/binary"
	"os"
	"sort"
	"strings"
)

var (
	// sceneLstInfoMark 数据块目录锚点——fixstr(7) "lstInfo"。
	sceneLstInfoMark = []byte{0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F}
	// sceneInfoMark sceneInfo 字段锚点——fixstr(9) "sceneInfo"。
	sceneInfoMark = []byte{0xA9, 0x73, 0x63, 0x65, 0x6E, 0x65, 0x49, 0x6E, 0x66, 0x6F}
	// sceneMainMark main 字段锚点——fixstr(4) "main"。
	sceneMainMark = []byte{0xA4, 0x6D, 0x61, 0x69, 0x6E}
	// sceneVersionKey version 键锚点——fixstr(7) "version"。
	sceneVersionKey = []byte{0xA7, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E}
	// sceneItemInfoMark kkpe 的 itemInfo 元素锚点。
	sceneItemInfoMark = []byte("<itemInfo ")
	// sceneNameMark name 属性锚点。
	sceneNameMark = []byte("name=\"")
	// sceneAliasMark vnge_sssb 快照里的对象别名键。
	sceneAliasMark = []byte("\"alias\":")
	// sceneCameraMark vnge_sssb 快照里的相机对象键。
	sceneCameraMark = []byte("\"cameraNode\"")
)

// 扫描参数（对齐 C# 同名常量）。
const (
	// sceneBufSize 扫描缓冲区（1 MB）。
	sceneBufSize = 1 << 20
	// sceneCarrySize 跨缓冲保留字节（覆盖键名回溯 31 字节 + 份头段锚点 33 字节 + 前瞻余量）。
	sceneCarrySize = 192
	// sceneValueReadCap 条目载荷读取上限（kkpe / vnge_sssb 的 XML 与 JSON）。
	sceneValueReadCap = 4 * 1024 * 1024
	// sceneMaxPropNames kkpe 名字清单上限。
	sceneMaxPropNames = 200
)

// ScenePluginItem 场景卡数据区里的一条插件数据条目（键名 + 版本 + 值形态 + 字节数 + 摘要）。
type ScenePluginItem struct {
	// Key 插件键名（MessagePack map 键，如 timeline / kkpe / vnge_actor）。
	Key string
	// Version 条目值之后的 version 串（timeline / kkpe 无此键 → 空串）。
	Version string
	// Shape 值形态（sceneInfo(XML) / main(串) / main(map N 键) 等）。
	Shape string
	// Bytes 载荷字节数（0 = 未解析出长度）。
	Bytes int64
	// Offset 条目在文件内的偏移。
	Offset int64
	// Summary 内容摘要（按条目语义生成——kkpe 的 itemInfo 条数 / vnge_sssb 的对象数；无则空串）。
	Summary string
	// valueAt 字符串值在文件内的起点（非字符串型 0）——内部用。
	valueAt int64
	// valueLen 字符串值字节数（非字符串型 0）——内部用。
	valueLen int64
}

// SceneNameCount 场景里的一个名字及其出现次数（kkpe 记录的 itemInfo 名——道具 / 节点）。
type SceneNameCount struct {
	// Name 名字（道具 / 节点名，UTF-8）。
	Name string
	// Count 出现次数。
	Count int
}

// SceneCharaData 场景里的一份内嵌角色卡数据（份头段 + 数据块目录）。
type SceneCharaData struct {
	// Index 份序号（1 起，按份头段在文件内出现顺序）。
	Index int
	// DataVersion 该份头段声明的数据版本串（如 0.0.0）。
	DataVersion string
	// DataVersionSet 版本是否读到（区分「未读到」与「读到空串」）。
	DataVersionSet bool
	// FaceOffset 该份卡面图在文件内的偏移。
	FaceOffset int64
	// FaceSize 该份卡面图字节数（头段声明值）。
	FaceSize int64
	// Blocks 该份的数据块目录（块名 / 版本 / 位置 / 大小）。
	Blocks []CardBlockInfo
}

// SceneInfoResult 场景卡（sd）数据区深度分析结果——只读快照。
type SceneInfoResult struct {
	// Scanned 是否完成扫描。
	Scanned bool
	// Error 失败原因（失败必须可见）。
	Error string
	// Plugins 插件数据条目（按文件内出现顺序）。
	Plugins []ScenePluginItem
	// CharaDataCount 场景内嵌角色卡数据份数（数据块目录 lstInfo 命中数）。
	CharaDataCount int
	// ScannedBytes 数据区已扫描字节数。
	ScannedBytes int64
	// KkpeItemCount kkpe 记录的 itemInfo 条数（-1 = 卡里没有 kkpe 条目）。
	KkpeItemCount int
	// PropNames kkpe 记录的 itemInfo 名字（去重，按出现次数降序；上限 200）。
	PropNames []SceneNameCount
	// SssbObjects vnge_sssb 场景快照里的对象数（-1 = 无该条目）。
	SssbObjects int
	// SssbCameras vnge_sssb 场景快照里的相机对象数。
	SssbCameras int
	// CharaFaces 内嵌角色数据的首张图（每份一张——该份角色卡的卡面图；由 AttachCharaFaces 装配）。
	CharaFaces []CardImageInfo
	// CharaData 内嵌角色数据明细（每份：数据版本 / 卡面图 / 数据块目录）。
	CharaData []SceneCharaData
	// Warnings 解析告警（失败必须可见）。
	Warnings []string

	// charaFaceOffsets 内嵌角色卡数据的卡面图偏移（份头段锚点扫出——内部用）。
	charaFaceOffsets []int64
	// charaFaceSizes 各份头段声明的脸图字节数（与偏移一一对应——内部用）。
	charaFaceSizes []int64
	// charaVersions 各份头段声明的数据版本串（与偏移一一对应——内部用）。
	charaVersions []string
	// charaLstOffsets 各份数据块目录在文件内的偏移（升序——内部用）。
	charaLstOffsets []int64
}

// AttachCharaFaces 按份头段扫出的卡面图偏移，从数据区图片清单里取对应的那张（每份一张 = 该份角色卡的卡面图）。
// 图片清单来自 ParseCardDetail（同一遍数据区扫描）；偏移对不上出声（不静默丢弃）。
func (r *SceneInfoResult) AttachCharaFaces(images []CardImageInfo) {
	r.CharaFaces = nil
	if len(images) == 0 || len(r.charaFaceOffsets) == 0 {
		return
	}
	for k := 0; k < len(r.charaFaceOffsets); k++ {
		off := r.charaFaceOffsets[k]
		hit := -1
		for i := range images {
			if images[i].Offset == off {
				hit = i
				break
			}
		}
		if hit < 0 {
			r.Warnings = append(r.Warnings, "第 "+itoa(int64(k+1))+" 份卡面图（@"+n0Text(off)+" · 声明 "+
				n0Text(r.charaFaceSizes[k])+" 字节）未出现在数据区图片清单里")
			continue
		}
		r.CharaFaces = append(r.CharaFaces, images[hit])
	}
}

// ReadSceneInfo 读一张场景卡的插件条目、内嵌角色数据份数、kkpe 道具名与 vnge_sssb 对象数。
func ReadSceneInfo(path string, imageEnd int64) *SceneInfoResult {
	r := &SceneInfoResult{Scanned: true, KkpeItemCount: -1, SssbObjects: -1}
	if _, err := os.Stat(path); err != nil {
		r.Error = "卡片文件不存在：" + path
		return r
	}
	f, err := os.Open(path)
	if err != nil {
		r.Error = "卡片文件打不开：" + path
		return r
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		r.Error = "卡片文件打不开：" + path
		return r
	}
	size := fi.Size()
	from := int64(0)
	if imageEnd > 0 {
		from = imageEnd
	}
	if from >= size {
		r.Error = "数据区为空（图片区到文件尾没有字节）"
		return r
	}

	seen := make(map[int64]bool)
	lstSeen := make(map[int64]bool)
	faceSeen := make(map[int64]bool)

	buf := make([]byte, sceneBufSize+256)
	pos := from
	consumed := from
	carry := 0
	for pos < size {
		want := size - pos
		if want > sceneBufSize {
			want = sceneBufSize
		}
		read, _ := f.ReadAt(buf[carry:carry+int(want)], pos)
		if read <= 0 {
			break
		}
		pos += int64(read)
		baseOff := consumed - int64(carry)
		consumed += int64(read)
		total := carry + read
		r.ScannedBytes += int64(read)

		for i := 0; i <= total-len(sceneLstInfoMark); i++ {
			if buf[i] != sceneLstInfoMark[0] || !matchAt(buf, i, sceneLstInfoMark) {
				continue
			}
			lstAt := baseOff + int64(i)
			if !lstSeen[lstAt] {
				lstSeen[lstAt] = true
				r.CharaDataCount++
				r.charaLstOffsets = append(r.charaLstOffsets, lstAt)
			}
		}

		// 份头段锚点（内嵌角色卡数据头：int32 100 + 卡类型串 + 数据版本串 + int32 脸图长度 + PNG 签名）——记卡面图偏移
		for i := 0; i <= total-45; i++ {
			if buf[i] != 0x64 || buf[i+1] != 0x00 || buf[i+2] != 0x00 || buf[i+3] != 0x00 {
				continue
			}
			p := i + 4
			cardType, okType := read7BitInBuf(buf, &p, total)
			dataVer, okVer := read7BitInBuf(buf, &p, total)
			if !okType || !okVer || !strings.Contains(cardType, "KoiKatu") {
				continue
			}
			if p+12 > total {
				continue
			}
			faceLen := int64(buf[p]) | int64(buf[p+1])<<8 | int64(buf[p+2])<<16 | int64(buf[p+3])<<24
			faceAt := p + 4
			if faceLen <= 0 || buf[faceAt] != 0x89 || buf[faceAt+1] != 0x50 || buf[faceAt+2] != 0x4E || buf[faceAt+3] != 0x47 {
				continue
			}
			faceOff := baseOff + int64(faceAt)
			if !faceSeen[faceOff] {
				faceSeen[faceOff] = true
				r.charaFaceOffsets = append(r.charaFaceOffsets, faceOff)
				r.charaFaceSizes = append(r.charaFaceSizes, faceLen)
				r.charaVersions = append(r.charaVersions, dataVer)
			}
		}

		for i := 0; i <= total-96; i++ {
			if buf[i] != 0x92 || buf[i+1] != 0x00 {
				continue
			}
			at := baseOff + int64(i)
			if seen[at] {
				continue
			}
			seen[at] = true
			key, ok := readSceneKeyAt(buf, i)
			if !ok {
				continue
			}
			item := parseSceneItem(buf, i, at, key, r)
			if item != nil {
				if item.valueAt > 0 {
					item.Version = readSceneVersion(f, item.valueAt+item.valueLen, size)
				}
				r.Plugins = append(r.Plugins, *item)
			}
		}

		newCarry := total
		if newCarry > sceneCarrySize {
			newCarry = sceneCarrySize
		}
		copy(buf[:newCarry], buf[total-newCarry:total])
		carry = newCarry
	}

	// 按条目语义补摘要与明细（kkpe 的道具名 / vnge_sssb 的对象数）
	for i := range r.Plugins {
		it := &r.Plugins[i]
		if it.valueAt <= 0 {
			continue
		}
		if it.Key == "kkpe" && r.KkpeItemCount < 0 {
			readKkpe(f, it, r)
		} else if it.Key == "vnge_sssb" && r.SssbObjects < 0 {
			readSssb(f, it, r)
		}
	}

	// 内嵌角色数据明细——份头段（数据版本 / 卡面图）× 数据块目录 按出现顺序配对
	pairs := len(r.charaFaceOffsets)
	if len(r.charaLstOffsets) < pairs {
		pairs = len(r.charaLstOffsets)
	}
	for k := 0; k < pairs; k++ {
		cd := SceneCharaData{
			Index:      k + 1,
			FaceOffset: r.charaFaceOffsets[k],
			FaceSize:   r.charaFaceSizes[k],
		}
		if k < len(r.charaVersions) {
			cd.DataVersion = r.charaVersions[k]
			cd.DataVersionSet = true
		}
		blocks := readSceneBlockTable(f, r.charaLstOffsets[k])
		if len(blocks) == 0 {
			r.Warnings = append(r.Warnings, "第 "+itoa(int64(k+1))+" 份角色数据的块表未解析出条目（@"+
				n0Text(r.charaLstOffsets[k])+"）")
		}
		cd.Blocks = append(cd.Blocks, blocks...)
		r.CharaData = append(r.CharaData, cd)
	}
	if len(r.charaFaceOffsets) != len(r.charaLstOffsets) {
		r.Warnings = append(r.Warnings, "份头段 "+itoa(int64(len(r.charaFaceOffsets)))+" 个 · 数据块目录 "+
			itoa(int64(len(r.charaLstOffsets)))+" 处——数量不等，明细按 "+itoa(int64(pairs))+" 份配对")
	}

	if len(r.Plugins) == 0 {
		r.Warnings = append(r.Warnings, "未识别到插件数据条目（数据区里没有 timeline / kkpe / vnge_* 形态的条目）")
	}
	return r
}

// readSceneBlockTable 读一份角色数据的数据块目录（lstInfo 数组——每项 map：name / version / pos / size）。
func readSceneBlockTable(f *os.File, lstAt int64) []CardBlockInfo {
	var list []CardBlockInfo
	buf := readSceneValue(f, lstAt+int64(len(sceneLstInfoMark)), 64*1024, 64*1024)
	if buf == nil {
		return list
	}
	cur := &msgCursor{buf: buf}
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		return list
	}
	for i := 0; i < count; i++ {
		keys, ok := cur.tryReadMapHeader()
		if !ok {
			break
		}
		var b CardBlockInfo
		entryOk := true
		for k := 0; k < keys; k++ {
			key, ok := cur.tryReadString()
			if !ok {
				entryOk = false
				break
			}
			switch key {
			case "name", "version":
				v, ok := cur.tryReadString()
				if !ok {
					entryOk = false
					break
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
					entryOk = false
					break
				}
				if key == "pos" {
					b.Pos = v
				} else {
					b.Size = v
				}
			default:
				if !cur.trySkipValue() {
					entryOk = false
					break
				}
			}
			if !entryOk {
				break
			}
		}
		if !entryOk {
			break
		}
		if b.Name != "" {
			list = append(list, b)
		}
	}
	return list
}

// readKkpe 读 kkpe 的 sceneInfo XML——itemInfo 条数与名字清单（按出现次数降序）。
func readKkpe(f *os.File, item *ScenePluginItem, r *SceneInfoResult) {
	xml := readSceneValue(f, item.valueAt, item.valueLen, sceneValueReadCap)
	if xml == nil {
		r.KkpeItemCount = 0
		r.Warnings = append(r.Warnings, "kkpe 条目读取失败（偏移 "+itoa(item.Offset)+"）")
		return
	}
	r.KkpeItemCount = countAt(xml, sceneItemInfoMark)
	counts := make(map[string]int)
	order := make([]string, 0, sceneMaxPropNames)
	for i := 0; i <= len(xml)-len(sceneNameMark); i++ {
		if xml[i] != sceneNameMark[0] || !matchAt(xml, i, sceneNameMark) {
			continue
		}
		start := i + len(sceneNameMark)
		p := start
		for p < len(xml) && xml[p] != 0x22 {
			p++
		}
		if p <= start {
			continue
		}
		name := string(xml[start:p])
		if cur, ok := counts[name]; ok {
			counts[name] = cur + 1
		} else if len(counts) < sceneMaxPropNames {
			counts[name] = 1
			order = append(order, name)
		}
	}
	for _, name := range order {
		r.PropNames = append(r.PropNames, SceneNameCount{Name: name, Count: counts[name]})
	}
	sort.SliceStable(r.PropNames, func(a, b int) bool {
		if r.PropNames[a].Count != r.PropNames[b].Count {
			return r.PropNames[a].Count > r.PropNames[b].Count
		}
		return r.PropNames[a].Name < r.PropNames[b].Name
	})
	item.Summary = "itemInfo " + n0Text(int64(r.KkpeItemCount)) + " 条 · 名字 " + itoa(int64(len(r.PropNames))) + " 种"
}

// readSssb 读 vnge_sssb 的场景快照 JSON——对象数与相机数。
func readSssb(f *os.File, item *ScenePluginItem, r *SceneInfoResult) {
	json := readSceneValue(f, item.valueAt, item.valueLen, sceneValueReadCap)
	if json == nil {
		r.SssbObjects = 0
		r.Warnings = append(r.Warnings, "vnge_sssb 条目读取失败（偏移 "+itoa(item.Offset)+"）")
		return
	}
	r.SssbObjects = countAt(json, sceneAliasMark)
	r.SssbCameras = countAt(json, sceneCameraMark)
	item.Summary = "快照对象 " + itoa(int64(r.SssbObjects)) + " 个（相机 " + itoa(int64(r.SssbCameras)) + "）"
}

// parseSceneItem 解析 0x92 00 之后的条目值形态；不是插件条目形态返回 nil。
func parseSceneItem(buf []byte, at92 int, at int64, key string, r *SceneInfoResult) *ScenePluginItem {
	shapeAt := at92 + 2
	if buf[shapeAt] == 0x81 && matchAt(buf, shapeAt+1, sceneInfoMark) {
		l, headLen := readStrHeader(buf, shapeAt+1+len(sceneInfoMark))
		if headLen < 0 {
			r.Warnings = append(r.Warnings, key+" 的 sceneInfo 字符串头未识别（偏移 "+itoa(at)+"）")
			return &ScenePluginItem{Key: key, Shape: "sceneInfo(XML) · 头未识别", Bytes: 0, Offset: at}
		}
		valueAt := shapeAt + 1 + len(sceneInfoMark) + headLen
		return &ScenePluginItem{
			Key:      key,
			Shape:    "sceneInfo(XML)",
			Bytes:    l,
			Offset:   at,
			valueAt:  at + int64(valueAt-at92),
			valueLen: l,
		}
	}
	if buf[shapeAt] == 0x82 && matchAt(buf, shapeAt+1, sceneMainMark) {
		p := shapeAt + 1 + len(sceneMainMark)
		b := buf[p]
		if b >= 0xA0 && b <= 0xBF {
			return &ScenePluginItem{Key: key, Shape: "main(短串)", Bytes: int64(b - 0xA0), Offset: at,
				valueAt: at + int64(p+1-at92), valueLen: int64(b - 0xA0)}
		}
		if b >= 0x80 && b <= 0x8F {
			return &ScenePluginItem{Key: key, Shape: "main(map " + itoa(int64(b-0x80)) + " 键)", Bytes: 0, Offset: at}
		}
		if b >= 0x90 && b <= 0x9F {
			return &ScenePluginItem{Key: key, Shape: "main(数组 " + itoa(int64(b-0x90)) + " 项)", Bytes: 0, Offset: at}
		}
		l, headLen := readStrHeader(buf, p)
		if headLen >= 0 {
			return &ScenePluginItem{
				Key:      key,
				Shape:    "main(串)",
				Bytes:    l,
				Offset:   at,
				valueAt:  at + int64(p+headLen-at92),
				valueLen: l,
			}
		}
		return &ScenePluginItem{Key: key, Shape: "main(0x" + hex2(b) + ")", Bytes: 0, Offset: at}
	}
	return nil
}

// readSceneVersion 读条目值之后紧跟的 version 串（`A7 "version" 串`）；无则空串。
func readSceneVersion(f *os.File, at, fileSize int64) string {
	if at+2 > fileSize {
		return ""
	}
	b := make([]byte, 64)
	if !readFullAt(f, at, b) {
		return ""
	}
	for skip := 0; skip <= 1; skip++ {
		if skip+len(sceneVersionKey) > len(b) || !matchAt(b, skip, sceneVersionKey) {
			continue
		}
		p := skip + len(sceneVersionKey)
		if s, ok := readStrAt(b, len(b), &p); ok {
			return s
		}
	}
	return ""
}

// readSceneValue 读文件的一段字节（上限 cap；读不到返回 nil）。
func readSceneValue(f *os.File, at, l, cap int64) []byte {
	if at <= 0 || l <= 0 {
		return nil
	}
	take := l
	if take > cap {
		take = cap
	}
	fi, err := f.Stat()
	if err != nil || at >= fi.Size() {
		return nil
	}
	buf := make([]byte, take)
	done := 0
	for int64(done) < take {
		m, _ := f.ReadAt(buf[done:], at+int64(done))
		done += m
		if m == 0 {
			break
		}
	}
	return buf[:done]
}

// readStrHeader 读 MessagePack 字符串头（fixstr / str8 / str16 / str32），返回长度与头字节数；未识别返回 -1。
func readStrHeader(buf []byte, p int) (int64, int) {
	if p >= len(buf) {
		return 0, -1
	}
	b := buf[p]
	if b >= 0xA0 && b <= 0xBF {
		return int64(b - 0xA0), 1
	}
	if b == 0xD9 && p+2 <= len(buf) {
		return int64(buf[p+1]), 2
	}
	if b == 0xDA && p+3 <= len(buf) {
		return int64(buf[p+1])<<8 | int64(buf[p+2]), 3
	}
	if b == 0xDB && p+5 <= len(buf) {
		return int64(buf[p+1])<<24 | int64(buf[p+2])<<16 | int64(buf[p+3])<<8 | int64(buf[p+4]), 5
	}
	return 0, -1
}

// readStrAt 读缓冲里 p 处的 MessagePack 字符串（含头）；失败返回 false。
func readStrAt(buf []byte, end int, p *int) (string, bool) {
	l, headLen := readStrHeader(buf, *p)
	if headLen < 0 || *p+headLen+int(l) > end {
		return "", false
	}
	s := string(buf[*p+headLen : *p+headLen+int(l)])
	*p = *p + headLen + int(l)
	return s, true
}

// read7BitInBuf 读缓冲里 p 处的 7bit 长度前缀 UTF-8 串（BinaryWriter.Write(string) 形态）；失败返回 false。
func read7BitInBuf(buf []byte, p *int, end int) (string, bool) {
	var l int64
	shift := 0
	for {
		if *p >= end {
			return "", false
		}
		b := buf[*p]
		*p = *p + 1
		l |= int64(b&0x7F) << shift
		if b&0x80 == 0 {
			break
		}
		shift += 7
		if shift > 28 {
			return "", false
		}
	}
	if l <= 0 || int64(*p)+l > int64(end) {
		return "", false
	}
	s := string(buf[*p : *p+int(l)])
	*p = *p + int(l)
	return s, true
}

// readSceneKeyAt 从 0x92 位置往前回溯相邻的 fixstr 键名；不是 ASCII 名字返回 false。
func readSceneKeyAt(buf []byte, at92 int) (string, bool) {
	for n := 1; n <= 31; n++ {
		hp := at92 - 1 - n
		if hp < 0 {
			return "", false
		}
		if buf[hp] != byte(0xA0|n) {
			continue
		}
		var sb strings.Builder
		for k := 0; k < n; k++ {
			c := buf[hp+1+k]
			ok := (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
				c == '_' || c == '.' || c == '-'
			if !ok {
				return "", false
			}
			sb.WriteByte(c)
		}
		return sb.String(), true
	}
	return "", false
}

// countAt 统计 needle 在缓冲里出现的次数（命中后前进一个 needle 长度）。
func countAt(buf, needle []byte) int {
	count := 0
	for i := 0; i <= len(buf)-len(needle); i++ {
		if buf[i] == needle[0] && matchAt(buf, i, needle) {
			count++
			i += len(needle) - 1
		}
	}
	return count
}

// matchAt 在缓冲区指定位置匹配字节序列（越界返回 false）。
func matchAt(buf []byte, at int, pattern []byte) bool {
	if at < 0 || at+len(pattern) > len(buf) {
		return false
	}
	for k := 0; k < len(pattern); k++ {
		if buf[at+k] != pattern[k] {
			return false
		}
	}
	return true
}

// hex2 一字节的两位十六进制（诊断用）。
func hex2(b byte) string {
	const digits = "0123456789ABCDEF"
	return string([]byte{digits[b>>4], digits[b&0x0F]})
}

// n0Text 千分位整数（对齐 .NET ToString("N0")）。
func n0Text(v int64) string {
	var buf [24]byte
	s := formatInt(buf[:], v)
	neg := strings.HasPrefix(s, "-")
	if neg {
		s = s[1:]
	}
	var parts []string
	for len(s) > 3 {
		parts = append([]string{s[len(s)-3:]}, parts...)
		s = s[:len(s)-3]
	}
	parts = append([]string{s}, parts...)
	out := strings.Join(parts, ",")
	if neg {
		out = "-" + out
	}
	return out
}

// formatInt 十进制整数文本（内部用，避免依赖 main 包）。
func formatInt(scratch []byte, v int64) string {
	return string(appendInt(scratch[:0], v))
}

// appendInt 把整数写成十进制追加到 buf。
func appendInt(buf []byte, v int64) []byte {
	if v == 0 {
		return append(buf, '0')
	}
	neg := v < 0
	var tmp [24]byte
	i := len(tmp)
	u := uint64(v)
	if neg {
		u = uint64(-v)
	}
	for u > 0 {
		i--
		tmp[i] = byte('0' + u%10)
		u /= 10
	}
	if neg {
		i--
		tmp[i] = '-'
	}
	return append(buf, tmp[i:]...)
}

// readCoordInt32LE 小端 32 位整数（场景卡份头段用）。
func readCoordInt32LE(b []byte) int64 {
	return int64(binary.LittleEndian.Uint32(b))
}
