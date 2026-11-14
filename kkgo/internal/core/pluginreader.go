package core

import (
	"bytes"
	"debug/pe"
	"encoding/binary"
	"fmt"
	"os"
	"strings"
)

// 插件 dll 元数据解析——走 PE / ECMA-335 元数据表只读通道，不加载程序集、不执行任何代码。
// 语义对齐 C# Core/PluginReader.cs（System.Reflection.Metadata 通道）：
// 程序集级元数据（标题 / 说明 / 公司 / 版权 / 产品 / 版本 / 目标框架）+ 类型上的 BepInPlugin / BepInProcess /
// BepInDependency / IPA Plugin 特性。CustomAttribute 只按 SerString 顺序取固定参数字符串（目标特性的字符串参数都在前）。

// AssemblyMeta 程序集级元数据（一个 dll 一份——插件类共用）。
type AssemblyMeta struct {
	// Title 标题（AssemblyTitleAttribute；缺省 = 程序集名）。
	Title string
	// Description 说明（AssemblyDescriptionAttribute）。
	Description string
	// Company 公司 / 作者署名（AssemblyCompanyAttribute）。
	Company string
	// Copyright 版权（AssemblyCopyrightAttribute）。
	Copyright string
	// Product 产品名（AssemblyProductAttribute）。
	Product string
	// FileVersion 程序集版本（优先 InformationalVersion，回落 Assembly 表版本）。
	FileVersion string
	// TargetFramework 目标框架（TargetFrameworkAttribute）。
	TargetFramework string
}

// PluginInfo 一个插件 dll 里的一项插件元数据（只读解析所得）。
type PluginInfo struct {
	// FilePath dll 绝对路径。
	FilePath string
	// FileName 文件名（含扩展名）。
	FileName string
	// Guid 插件 GUID（BepInPlugin 第一参数；IPA 插件为「IPA.<名>」）。
	Guid string
	// Name 插件显示名。
	Name string
	// Version 插件版本。
	Version string
	// Processes 进程过滤（BepInProcess 参数；空 = 所有进程都加载）。
	Processes []string
	// Dependencies 依赖的插件 GUID（BepInDependency 第一参数）。
	Dependencies []string
	// IsIpa 是否为 IPA 插件（旧框架，无 BepInPlugin 特性）。
	IsIpa bool
	// Title 程序集标题。
	Title string
	// Description 程序集说明。
	Description string
	// Company 程序集公司 / 作者署名。
	Company string
	// Copyright 程序集版权。
	Copyright string
	// Product 程序集产品名。
	Product string
	// FileVersion 程序集版本。
	FileVersion string
	// TargetFramework 目标框架。
	TargetFramework string
	// HasPluginAttribute 程序集是否被标记为插件（含 BepInPlugin / IPA Plugin 特性）。
	HasPluginAttribute bool
}

// 列类型（#~ 表流行宽计算用）。
const (
	colU16 = iota
	colU32
	colString
	colGUID
	colBlob
	colTable
	colCoded
)

// 编码索引种类。
const (
	ciTypeDefOrRef = iota
	ciHasConstant
	ciHasCustomAttribute
	ciHasFieldMarshal
	ciHasDeclSecurity
	ciMemberRefParent
	ciHasSemantics
	ciMethodDefOrRef
	ciMemberForwarded
	ciImplementation
	ciCustomAttributeType
	ciResolutionScope
	ciTypeOrMethodDef
)

// codedIndexTables 编码索引的目标表（tag 顺序即下标）。
var codedIndexTables = map[int][]int{
	ciTypeDefOrRef:        {0x02, 0x01, 0x1B},
	ciHasConstant:         {0x04, 0x08, 0x17},
	ciHasCustomAttribute:  {0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B},
	ciHasFieldMarshal:     {0x04, 0x08},
	ciHasDeclSecurity:     {0x02, 0x06, 0x20},
	ciMemberRefParent:     {0x02, 0x01, 0x1A, 0x06, 0x1B},
	ciHasSemantics:        {0x14, 0x17},
	ciMethodDefOrRef:      {0x06, 0x0A},
	ciMemberForwarded:     {0x04, 0x06},
	ciImplementation:      {0x26, 0x23, 0x27},
	ciCustomAttributeType: {0x06, 0x06, 0x06, 0x0A, 0x06},
	ciResolutionScope:     {0x00, 0x1A, 0x23, 0x01},
	ciTypeOrMethodDef:     {0x02, 0x06},
}

// codedIndexBits 编码索引的 tag 位数。
var codedIndexBits = map[int]int{
	ciTypeDefOrRef:        2,
	ciHasConstant:         2,
	ciHasCustomAttribute:  5,
	ciHasFieldMarshal:     1,
	ciHasDeclSecurity:     2,
	ciMemberRefParent:     3,
	ciHasSemantics:        1,
	ciMethodDefOrRef:      1,
	ciMemberForwarded:     1,
	ciImplementation:      2,
	ciCustomAttributeType: 3,
	ciResolutionScope:     2,
	ciTypeOrMethodDef:     1,
}

// colSpec 一列的形态（表 schema 用）。
type colSpec struct {
	kind int
	ref  int
}

// tableSchemas ECMA-335 II.22 全表列定义（行宽计算必需——出现未列的表即报错，不猜宽度）。
var tableSchemas = map[int][]colSpec{
	0x00: {{colU16, 0}, {colString, 0}, {colGUID, 0}, {colGUID, 0}, {colGUID, 0}},
	0x01: {{colCoded, ciResolutionScope}, {colString, 0}, {colString, 0}},
	0x02: {{colU32, 0}, {colString, 0}, {colString, 0}, {colCoded, ciTypeDefOrRef}, {colTable, 0x04}, {colTable, 0x06}},
	0x03: {{colTable, 0x04}},
	0x04: {{colU16, 0}, {colString, 0}, {colBlob, 0}},
	0x05: {{colTable, 0x06}},
	0x06: {{colU32, 0}, {colU16, 0}, {colU16, 0}, {colString, 0}, {colBlob, 0}, {colTable, 0x08}},
	0x07: {{colTable, 0x08}},
	0x08: {{colU16, 0}, {colU16, 0}, {colString, 0}},
	0x09: {{colTable, 0x02}, {colCoded, ciTypeDefOrRef}},
	0x0A: {{colCoded, ciMemberRefParent}, {colString, 0}, {colBlob, 0}},
	0x0B: {{colU16, 0}, {colCoded, ciHasConstant}, {colBlob, 0}},
	0x0C: {{colCoded, ciHasCustomAttribute}, {colCoded, ciCustomAttributeType}, {colBlob, 0}},
	0x0D: {{colCoded, ciHasFieldMarshal}, {colBlob, 0}},
	0x0E: {{colU16, 0}, {colCoded, ciHasDeclSecurity}, {colBlob, 0}},
	0x0F: {{colU16, 0}, {colU32, 0}, {colTable, 0x02}},
	0x10: {{colU32, 0}, {colTable, 0x04}},
	0x11: {{colBlob, 0}},
	0x12: {{colTable, 0x02}, {colTable, 0x14}},
	0x13: {{colTable, 0x14}},
	0x14: {{colU16, 0}, {colString, 0}, {colCoded, ciTypeDefOrRef}},
	0x15: {{colTable, 0x02}, {colTable, 0x17}},
	0x16: {{colTable, 0x17}},
	0x17: {{colU16, 0}, {colString, 0}, {colBlob, 0}},
	0x18: {{colU16, 0}, {colTable, 0x06}, {colCoded, ciHasSemantics}},
	0x19: {{colTable, 0x02}, {colCoded, ciMethodDefOrRef}, {colCoded, ciMethodDefOrRef}},
	0x1A: {{colString, 0}},
	0x1B: {{colBlob, 0}},
	0x1C: {{colU16, 0}, {colCoded, ciMemberForwarded}, {colString, 0}, {colTable, 0x1A}},
	0x1D: {{colU32, 0}, {colTable, 0x04}},
	0x1E: {{colU32, 0}, {colU32, 0}},
	0x1F: {{colU32, 0}},
	0x20: {{colU32, 0}, {colU16, 0}, {colU16, 0}, {colU16, 0}, {colU16, 0}, {colU32, 0}, {colBlob, 0}, {colString, 0}, {colString, 0}},
	0x21: {{colU32, 0}},
	0x22: {{colU32, 0}, {colU32, 0}, {colU32, 0}},
	0x23: {{colU16, 0}, {colU16, 0}, {colU16, 0}, {colU16, 0}, {colU32, 0}, {colBlob, 0}, {colString, 0}, {colString, 0}, {colBlob, 0}},
	0x24: {{colU32, 0}, {colTable, 0x23}},
	0x25: {{colU32, 0}, {colU32, 0}, {colU32, 0}, {colTable, 0x23}},
	0x26: {{colU32, 0}, {colString, 0}, {colBlob, 0}},
	0x27: {{colU32, 0}, {colU32, 0}, {colString, 0}, {colString, 0}, {colCoded, ciImplementation}},
	0x28: {{colU32, 0}, {colU32, 0}, {colString, 0}, {colCoded, ciImplementation}},
	0x29: {{colTable, 0x02}, {colTable, 0x02}},
	0x2A: {{colU16, 0}, {colU16, 0}, {colCoded, ciTypeOrMethodDef}, {colString, 0}},
	0x2B: {{colCoded, ciMethodDefOrRef}, {colBlob, 0}},
	0x2C: {{colTable, 0x2A}, {colCoded, ciTypeDefOrRef}},
}

// tableStream #~ 表流（行数 / 行宽 / 行偏移）。
type tableStream struct {
	data       []byte
	heapSizes  byte
	rowCounts  [64]int
	rowSizes   [64]int
	rowOffsets [64]int
}

// columnWidth 一列的字节宽度（堆索引宽度由 HeapSizes 位决定：bit0 字符串 / bit1 GUID / bit2 Blob）。
func (ts *tableStream) columnWidth(c colSpec) int {
	switch c.kind {
	case colU16:
		return 2
	case colU32:
		return 4
	case colString:
		if ts.heapSizes&0x01 != 0 {
			return 4
		}
		return 2
	case colGUID:
		if ts.heapSizes&0x02 != 0 {
			return 4
		}
		return 2
	case colBlob:
		if ts.heapSizes&0x04 != 0 {
			return 4
		}
		return 2
	case colTable:
		if ts.rowCounts[c.ref] >= 0x10000 {
			return 4
		}
		return 2
	case colCoded:
		bits := codedIndexBits[c.ref]
		max := 0
		for _, t := range codedIndexTables[c.ref] {
			if ts.rowCounts[t] > max {
				max = ts.rowCounts[t]
			}
		}
		if max >= 1<<(16-bits) {
			return 4
		}
		return 2
	}
	return 0
}

// codedWidth 编码索引列的字节宽度。
func (ts *tableStream) codedWidth(kind int) int {
	return ts.columnWidth(colSpec{kind: colCoded, ref: kind})
}

// rowReader 一行数据的顺序读取器。
type rowReader struct {
	data []byte
	off  int
	ts   *tableStream
}

func (r *rowReader) u8() int {
	v := int(r.data[r.off])
	r.off++
	return v
}

func (r *rowReader) u16() int {
	v := int(binary.LittleEndian.Uint16(r.data[r.off:]))
	r.off += 2
	return v
}

func (r *rowReader) u32() int {
	v := int(binary.LittleEndian.Uint32(r.data[r.off:]))
	r.off += 4
	return v
}

func (r *rowReader) index(kind int) int {
	if kind == colU16 {
		return r.u16()
	}
	if kind == colU32 {
		return r.u32()
	}
	return 0
}

func (r *rowReader) coded(kind int) (tag, index int) {
	var raw int
	if r.ts.codedWidth(kind) == 4 {
		raw = r.u32()
	} else {
		raw = r.u16()
	}
	bits := codedIndexBits[kind]
	return raw & ((1 << bits) - 1), raw >> bits
}

// dotnetMeta 一个 dll 的只读元数据视图。
type dotnetMeta struct {
	stringsHeap []byte
	blobHeap    []byte
	tables      *tableStream
	methodOwner []int
}

// typeDefRow TypeDef 表行（只取用到的列）。
type typeDefRow struct {
	Name       int
	Namespace  int
	MethodList int
}

// typeRefRow TypeRef 表行（只取用到的列）。
type typeRefRow struct {
	Name      int
	Namespace int
}

// memberRefRow MemberRef 表行（只取 Class 与名字）。
type memberRefRow struct {
	ClassTag   int
	ClassIndex int
}

// customAttrRow CustomAttribute 表行。
type customAttrRow struct {
	ParentTag   int
	ParentIndex int
	TypeTag     int
	TypeIndex   int
	Value       int
}

// loadDotNetMeta 打开 PE → CLI 头 → 元数据根 → 表流 / 字符串堆 / Blob 堆。
func loadDotNetMeta(path string) (*dotnetMeta, error) {
	if _, err := os.Stat(path); err != nil {
		return nil, fmt.Errorf("文件不存在")
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	f, err := pe.NewFile(bytes.NewReader(raw))
	if err != nil {
		return nil, fmt.Errorf("非 PE 文件：%s", err)
	}
	var dirs []pe.DataDirectory
	switch oh := f.OptionalHeader.(type) {
	case *pe.OptionalHeader32:
		dirs = oh.DataDirectory[:]
	case *pe.OptionalHeader64:
		dirs = oh.DataDirectory[:]
	default:
		return nil, fmt.Errorf("非 .NET 程序集（无元数据）")
	}
	if len(dirs) <= 14 || dirs[14].VirtualAddress == 0 || dirs[14].Size == 0 {
		return nil, fmt.Errorf("非 .NET 程序集（无元数据）")
	}
	cliOff, ok := rvaToOffset(f, dirs[14].VirtualAddress)
	if !ok || int(cliOff)+16 > len(raw) {
		return nil, fmt.Errorf("非 .NET 程序集（无元数据）")
	}
	metaRVA := binary.LittleEndian.Uint32(raw[cliOff+8:])
	metaOff, ok := rvaToOffset(f, metaRVA)
	if !ok || int(metaOff)+16 > len(raw) {
		return nil, fmt.Errorf("元数据根越界")
	}
	if binary.LittleEndian.Uint32(raw[metaOff:]) != 0x424A5342 {
		return nil, fmt.Errorf("元数据签名不符")
	}
	verLen := int(binary.LittleEndian.Uint32(raw[metaOff+12:]))
	p := int(metaOff) + 16 + verLen
	if p+4 > len(raw) {
		return nil, fmt.Errorf("元数据根越界")
	}
	streamCount := int(binary.LittleEndian.Uint16(raw[p+2:]))
	p += 4
	var tsOff, tsSize, strOff, strSize, blobOff, blobSize int
	for i := 0; i < streamCount; i++ {
		if p+8 > len(raw) {
			return nil, fmt.Errorf("元数据流头越界")
		}
		sOff := int(binary.LittleEndian.Uint32(raw[p:]))
		sSize := int(binary.LittleEndian.Uint32(raw[p+4:]))
		nameStart := p + 8
		nameEnd := nameStart
		for nameEnd < len(raw) && raw[nameEnd] != 0 {
			nameEnd++
		}
		name := string(raw[nameStart:nameEnd])
		p = (nameEnd + 1 + 3) &^ 3
		switch name {
		case "#~", "#-":
			tsOff, tsSize = int(metaOff)+sOff, sSize
		case "#Strings":
			strOff, strSize = int(metaOff)+sOff, sSize
		case "#Blob":
			blobOff, blobSize = int(metaOff)+sOff, sSize
		}
	}
	if tsOff == 0 || tsOff+tsSize > len(raw) || strOff+strSize > len(raw) || blobOff+blobSize > len(raw) {
		return nil, fmt.Errorf("元数据流缺失")
	}
	ts := &tableStream{data: raw[tsOff : tsOff+tsSize]}
	if len(ts.data) < 24 {
		return nil, fmt.Errorf("表流过短")
	}
	ts.heapSizes = ts.data[6]
	valid := binary.LittleEndian.Uint64(ts.data[8:])
	pos := 24
	for t := 0; t < 64; t++ {
		if valid&(1<<uint(t)) == 0 {
			continue
		}
		if pos+4 > len(ts.data) {
			return nil, fmt.Errorf("表流行数越界")
		}
		ts.rowCounts[t] = int(binary.LittleEndian.Uint32(ts.data[pos:]))
		pos += 4
	}
	for t := 0; t < 64; t++ {
		if ts.rowCounts[t] == 0 {
			continue
		}
		schema, ok := tableSchemas[t]
		if !ok {
			return nil, fmt.Errorf("未知元数据表 0x%02X", t)
		}
		size := 0
		for _, c := range schema {
			size += ts.columnWidth(c)
		}
		ts.rowSizes[t] = size
		ts.rowOffsets[t] = pos
		pos += size * ts.rowCounts[t]
		if pos > len(ts.data) {
			return nil, fmt.Errorf("表数据越界")
		}
	}
	m := &dotnetMeta{
		stringsHeap: raw[strOff : strOff+strSize],
		blobHeap:    raw[blobOff : blobOff+blobSize],
		tables:      ts,
	}
	m.buildMethodOwner()
	return m, nil
}

// rvaToOffset 节表映射（RVA → 文件偏移）。
func rvaToOffset(f *pe.File, rva uint32) (uint32, bool) {
	for _, s := range f.Sections {
		size := s.VirtualSize
		if size == 0 {
			size = s.Size
		}
		if rva >= s.VirtualAddress && rva < s.VirtualAddress+size {
			return rva - s.VirtualAddress + s.Offset, true
		}
	}
	return 0, false
}

// buildMethodOwner 由 TypeDef.MethodList 建「方法 → 声明类型」反查表（1-based；0 = 未知）。
func (m *dotnetMeta) buildMethodOwner() {
	count := m.tables.rowCounts[0x06]
	m.methodOwner = make([]int, count+1)
	typeCount := m.tables.rowCounts[0x02]
	starts := make([]int, typeCount+2)
	for t := 1; t <= typeCount; t++ {
		row := m.typeDef(t)
		starts[t] = row.MethodList
	}
	starts[typeCount+1] = count + 1
	for t := 1; t <= typeCount; t++ {
		start := starts[t]
		end := starts[t+1]
		if start < 1 {
			start = 1
		}
		for i := start; i < end && i <= count; i++ {
			m.methodOwner[i] = t
		}
	}
}

// row 取某表第 idx 行（1-based）的读取器。
func (m *dotnetMeta) row(table, idx int) *rowReader {
	ts := m.tables
	off := ts.rowOffsets[table] + (idx-1)*ts.rowSizes[table]
	return &rowReader{data: ts.data, off: off, ts: ts}
}

// str 取 #Strings 堆的字符串（0 = 空串）。
func (m *dotnetMeta) str(index int) string {
	if index <= 0 || index >= len(m.stringsHeap) {
		return ""
	}
	end := index
	for end < len(m.stringsHeap) && m.stringsHeap[end] != 0 {
		end++
	}
	return string(m.stringsHeap[index:end])
}

// blobAt 取 #Blob 堆的一个 blob（compressed uint 长度前缀）。
func (m *dotnetMeta) blobAt(index int) []byte {
	if index <= 0 || index >= len(m.blobHeap) {
		return nil
	}
	n, used := readCompressedUInt(m.blobHeap[index:])
	if used <= 0 || int(n) > len(m.blobHeap)-index-used {
		return nil
	}
	return m.blobHeap[index+used : index+used+int(n)]
}

// strIndex 读一个 #Strings 堆索引（宽度由 HeapSizes bit0 决定）。
func (m *dotnetMeta) strIndex(r *rowReader) int {
	if m.tables.heapSizes&0x01 != 0 {
		return r.u32()
	}
	return r.u16()
}

// typeDef 取 TypeDef 表一行。
func (m *dotnetMeta) typeDef(idx int) typeDefRow {
	r := m.row(0x02, idx)
	_ = r.u32()
	row := typeDefRow{}
	row.Name = m.strIndex(r)
	row.Namespace = m.strIndex(r)
	// Extends（TypeDefOrRef coded）
	_, _ = r.coded(ciTypeDefOrRef)
	// FieldList（简单索引——不进结果）
	if m.tables.rowCounts[0x04] >= 0x10000 {
		r.off += 4
	} else {
		r.off += 2
	}
	if m.tables.rowCounts[0x06] >= 0x10000 {
		row.MethodList = r.u32()
	} else {
		row.MethodList = r.u16()
	}
	return row
}

// typeRef 取 TypeRef 表一行（只取名字与命名空间）。
func (m *dotnetMeta) typeRef(idx int) typeRefRow {
	r := m.row(0x01, idx)
	_, _ = r.coded(ciResolutionScope)
	return typeRefRow{Name: m.strIndex(r), Namespace: m.strIndex(r)}
}

// memberRef 取 MemberRef 表一行（Class 编码索引 + 名字）。
func (m *dotnetMeta) memberRef(idx int) memberRefRow {
	r := m.row(0x0A, idx)
	tag, index := r.coded(ciMemberRefParent)
	return memberRefRow{ClassTag: tag, ClassIndex: index}
}

// customAttr 取 CustomAttribute 表一行。
func (m *dotnetMeta) customAttr(idx int) customAttrRow {
	r := m.row(0x0C, idx)
	row := customAttrRow{}
	row.ParentTag, row.ParentIndex = r.coded(ciHasCustomAttribute)
	row.TypeTag, row.TypeIndex = r.coded(ciCustomAttributeType)
	if m.tables.heapSizes&0x04 != 0 {
		row.Value = r.u32()
	} else {
		row.Value = r.u16()
	}
	return row
}

// readCompressedUInt ECMA-335 II.23.2 压缩无符号整数。
func readCompressedUInt(data []byte) (uint32, int) {
	if len(data) == 0 {
		return 0, 0
	}
	b := data[0]
	switch {
	case b&0x80 == 0:
		return uint32(b), 1
	case b&0xC0 == 0x80:
		if len(data) < 2 {
			return 0, 0
		}
		return uint32(b&0x3F)<<8 | uint32(data[1]), 2
	case b&0xE0 == 0xC0:
		if len(data) < 4 {
			return 0, 0
		}
		return uint32(b&0x1F)<<24 | uint32(data[1])<<16 | uint32(data[2])<<8 | uint32(data[3]), 4
	}
	return 0, 0
}

// readSerString 读一个 SerString（0xFF = null；否则压缩长度 + UTF-8）。
func readSerString(data []byte) (string, int, bool) {
	if len(data) == 0 {
		return "", 0, false
	}
	if data[0] == 0xFF {
		return "", 1, true
	}
	if data[0] == 0x00 {
		return "", 1, true
	}
	n, used := readCompressedUInt(data)
	if used <= 0 || int(n) > len(data)-used {
		return "", 0, false
	}
	return string(data[used : used+int(n)]), used + int(n), true
}

// stringArgs 取特性的固定参数字符串序列（按 SerString 读；目标特性的字符串参数都在前）。
func (m *dotnetMeta) stringArgs(blobIndex int) []string {
	blob := m.blobAt(blobIndex)
	if len(blob) < 2 || binary.LittleEndian.Uint16(blob) != 0x0001 {
		return nil
	}
	p := 2
	var out []string
	for p < len(blob) {
		s, used, ok := readSerString(blob[p:])
		if !ok {
			break
		}
		p += used
		if s != "" {
			out = append(out, s)
		}
	}
	return out
}

// attrOwnerName 取特性构造函数所属类型的名（wantNamespace=false）或命名空间（true）。
func (m *dotnetMeta) attrOwnerName(ca customAttrRow, wantNamespace bool) string {
	switch ca.TypeTag {
	case 2: // MethodDef
		if ca.TypeIndex < len(m.methodOwner) {
			owner := m.methodOwner[ca.TypeIndex]
			if owner > 0 {
				td := m.typeDef(owner)
				if wantNamespace {
					return m.str(td.Namespace)
				}
				return m.str(td.Name)
			}
		}
	case 3: // MemberRef
		mr := m.memberRef(ca.TypeIndex)
		switch mr.ClassTag {
		case 0: // TypeDef
			td := m.typeDef(mr.ClassIndex)
			if wantNamespace {
				return m.str(td.Namespace)
			}
			return m.str(td.Name)
		case 1: // TypeRef
			tr := m.typeRef(mr.ClassIndex)
			if wantNamespace {
				return m.str(tr.Namespace)
			}
			return m.str(tr.Name)
		}
	}
	return ""
}

// assemblyMetaFrom 读程序集级元数据（标题 / 说明 / 公司 / 版权 / 产品 / 版本 / 目标框架）。
func (m *dotnetMeta) assemblyMetaFrom() AssemblyMeta {
	meta := AssemblyMeta{}
	if m.tables.rowCounts[0x20] == 0 {
		return meta
	}
	r := m.row(0x20, 1)
	_ = r.u32() // HashAlgId
	major := r.u16()
	minor := r.u16()
	build := r.u16()
	revision := r.u16()
	_ = r.u32() // Flags
	// PublicKey blob
	if m.tables.heapSizes&0x04 != 0 {
		r.off += 4
	} else {
		r.off += 2
	}
	name := m.strIndex(r)
	_ = m.strIndex(r) // Culture（不进结果）
	meta.Title = m.str(name)
	meta.FileVersion = fmt.Sprintf("%d.%d.%d.%d", major, minor, build, revision)
	for i := 1; i <= m.tables.rowCounts[0x0C]; i++ {
		ca := m.customAttr(i)
		if ca.ParentTag != 14 || ca.ParentIndex != 1 {
			continue
		}
		typeName := m.attrOwnerName(ca, false)
		args := m.stringArgs(ca.Value)
		first := ""
		if len(args) > 0 {
			first = args[0]
		}
		switch typeName {
		case "AssemblyTitleAttribute":
			meta.Title = first
		case "AssemblyDescriptionAttribute":
			meta.Description = first
		case "AssemblyCompanyAttribute":
			meta.Company = first
		case "AssemblyCopyrightAttribute":
			meta.Copyright = first
		case "AssemblyProductAttribute":
			meta.Product = first
		case "AssemblyInformationalVersionAttribute":
			if first != "" {
				meta.FileVersion = first
			}
		case "TargetFrameworkAttribute":
			meta.TargetFramework = first
		}
	}
	return meta
}

// referencesPluginFramework 该程序集是否引用了插件框架（BepInEx / IllusionPlugin / IPA）。
func (m *dotnetMeta) referencesPluginFramework() bool {
	for i := 1; i <= m.tables.rowCounts[0x23]; i++ {
		row := m.row(0x23, i)
		row.off += 8  // Major / Minor / Build / Revision
		_ = row.u32() // Flags
		if m.tables.heapSizes&0x04 != 0 {
			row.off += 4
		} else {
			row.off += 2
		}
		var name int
		if m.tables.heapSizes&0x01 != 0 {
			name = row.u32()
		} else {
			name = row.u16()
		}
		switch m.str(name) {
		case "BepInEx", "IllusionPlugin", "IPA":
			return true
		}
	}
	return false
}

// readPluginAttributes 遍历全部类型定义读插件特性（BepInPlugin 加在插件类上，不在程序集上）。
func (m *dotnetMeta) readPluginAttributes(path string, meta AssemblyMeta) []PluginInfo {
	list := make([]PluginInfo, 0)
	buckets := make(map[int][]customAttrRow)
	for i := 1; i <= m.tables.rowCounts[0x0C]; i++ {
		ca := m.customAttr(i)
		if ca.ParentTag == 3 {
			buckets[ca.ParentIndex] = append(buckets[ca.ParentIndex], ca)
		}
	}
	fileName := path
	if i := strings.LastIndexAny(path, `\/`); i >= 0 {
		fileName = path[i+1:]
	}
	for t := 1; t <= m.tables.rowCounts[0x02]; t++ {
		attrs := buckets[t]
		if len(attrs) == 0 {
			continue
		}
		var info *PluginInfo
		processes := make([]string, 0)
		dependencies := make([]string, 0)
		for _, ca := range attrs {
			typeName := m.attrOwnerName(ca, false)
			switch typeName {
			case "BepInPlugin":
				args := m.stringArgs(ca.Value)
				if len(args) >= 3 && info == nil {
					info = &PluginInfo{Guid: args[0], Name: args[1], Version: args[2]}
				}
			case "BepInProcess":
				args := m.stringArgs(ca.Value)
				if len(args) >= 1 {
					processes = append(processes, args[0])
				}
			case "BepInDependency":
				args := m.stringArgs(ca.Value)
				if len(args) >= 1 {
					dependencies = append(dependencies, args[0])
				}
			case "Plugin", "PluginAttribute":
				if m.attrOwnerName(ca, true) == "IllusionPlugin" {
					args := m.stringArgs(ca.Value)
					if len(args) >= 2 && info == nil {
						info = &PluginInfo{IsIpa: true, Name: args[0], Version: args[1], Guid: "IPA." + args[0]}
					}
				}
			}
		}
		if info == nil {
			continue
		}
		info.FilePath = path
		info.FileName = fileName
		info.Processes = processes
		info.Dependencies = dependencies
		info.HasPluginAttribute = true
		info.Title = meta.Title
		info.Description = meta.Description
		info.Company = meta.Company
		info.Copyright = meta.Copyright
		info.Product = meta.Product
		info.FileVersion = meta.FileVersion
		info.TargetFramework = meta.TargetFramework
		list = append(list, *info)
	}
	return list
}

// ReadPluginDll 解析一个 dll——插件清单 + 程序集级元数据（非插件 dll 也能拿到标题 / 说明 / 公司）。
// 返回 error 文本（空 = 成功）——语义对齐 C# PluginReader.ReadDll 的 error 输出。
func ReadPluginDll(path string) ([]PluginInfo, AssemblyMeta, string) {
	meta := AssemblyMeta{}
	m, err := loadDotNetMeta(path)
	if err != nil {
		return nil, meta, err.Error()
	}
	list := make([]PluginInfo, 0)
	meta = m.assemblyMetaFrom()
	if !m.referencesPluginFramework() {
		return list, meta, "未引用插件框架（BepInEx / IllusionPlugin）"
	}
	list = m.readPluginAttributes(path, meta)
	if len(list) == 0 {
		return list, meta, "未找到插件特性（BepInPlugin / IPA Plugin）"
	}
	return list, meta, ""
}
