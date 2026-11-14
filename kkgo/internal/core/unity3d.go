// Unity3d（AssetBundle）只读解析器——UnityFS 容器解包 + SerializedFile 类型树 + Texture2D 提取与解码。
//
// 对齐 C# Core/Unity3dReader.cs：UnpackBundle / ParseSerializedFile / ReadTextureMeta / DecodeTexture
// 与 LZ4 块解压、贴图格式解码（RGBA32 / RGB24 / BGRA32 / ARGB32 / RGB565 / DXT1 / DXT5；BC7 见 unity3d_bc7.go）。
package core

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"image"
	"image/png"
)

// Unity3dTexture 包内的一张贴图（只读元数据）。
type Unity3dTexture struct {
	// PathId 对象路径 ID（包内唯一）。
	PathId int64
	// Name 贴图名称（m_Name）。
	Name string
	// Width 宽（像素）。
	Width int
	// Height 高（像素）。
	Height int
	// Format Unity 贴图格式码（4 = RGBA32 · 3 = RGB24 · 14 = BGRA32 · 5 = ARGB32 · 7 = RGB565 · 10 = DXT1 · 12 = DXT5 · 25 = BC7）。
	Format int
	// DataLength 像素数据字节数（含 mipmap）。
	DataLength int
	// DataOffset 像素数据在解包数据区内的偏移。
	DataOffset int
}

// classIDTexture2D Texture2D 的 Unity classID。
const classIDTexture2D = 28

// maxTextureBytes 单张贴图导出上限（像素数据字节数）。
const maxTextureBytes = 268435456

// maxBundleBytes 整包解包后的数据区上限（实测最大 1.42 GB，留余量到 1.9 GB）。
const maxBundleBytes = 1900000000

// sfNode SerializedFile 的 TypeTree 节点（v17 布局：24 字节）。
type sfNode struct {
	// Level 层级深度（0 = 根节点）。
	Level int
	// TypeStrOffset 类型名偏移（高位 0x80 = Unity 内置类型名表索引）。
	TypeStrOffset int64
	// NameStrOffset 字段名偏移（编码同 TypeStrOffset）。
	NameStrOffset int64
	// ByteSize 字段字节数（-1 = 变长）。
	ByteSize int
	// MetaFlag 元标记。
	MetaFlag int
}

// sfType SerializedFile 的类型表项。
type sfType struct {
	// ClassId Unity classID。
	ClassId int
	// Nodes TypeTree 节点序列。
	Nodes []sfNode
	// StringBuffer 本类型的字段名池。
	StringBuffer []byte
}

// sfObject SerializedFile 的对象表项。
type sfObject struct {
	// PathId 对象路径 ID。
	PathId int64
	// ByteStart 对象数据在数据区内的相对偏移。
	ByteStart int
	// ByteSize 对象数据字节数。
	ByteSize int
	// TypeId 类型表下标（-1 = 无）。
	TypeId int
}

// ListTextures 列出包内全部 Texture2D；失败返回诊断文本（空串 = 成功）。
func ListTextures(bundle []byte) ([]Unity3dTexture, string) {
	if len(bundle) < 64 {
		return nil, "数据过短"
	}
	data, loadErr := unpackBundle(bundle)
	if loadErr != "" {
		return nil, loadErr
	}
	dataOffset, types, objects, parseErr := parseSerializedFile(data)
	if parseErr != "" {
		return nil, parseErr
	}
	list := make([]Unity3dTexture, 0, 8)
	for _, o := range objects {
		if o.TypeId < 0 || o.TypeId >= len(types) {
			continue
		}
		if types[o.TypeId].ClassId != classIDTexture2D {
			continue
		}
		tex := readTextureMeta(data, dataOffset+o.ByteStart, types[o.TypeId], o.PathId)
		if tex != nil {
			list = append(list, *tex)
		}
	}
	return list, ""
}

// RenderTexturePng 把包内指定贴图渲染为 PNG——maxEdge > 0 时按最长边等比缩小（盒式平均）。
func RenderTexturePng(bundle []byte, pathId int64, maxEdge int) ([]byte, string) {
	if len(bundle) < 64 {
		return nil, "数据过短"
	}
	data, loadErr := unpackBundle(bundle)
	if loadErr != "" {
		return nil, loadErr
	}
	dataOffset, types, objects, parseErr := parseSerializedFile(data)
	if parseErr != "" {
		return nil, parseErr
	}
	for _, o := range objects {
		if o.PathId != pathId {
			continue
		}
		if o.TypeId < 0 || o.TypeId >= len(types) || types[o.TypeId].ClassId != classIDTexture2D {
			return nil, "该对象不是贴图"
		}
		meta := readTextureMeta(data, dataOffset+o.ByteStart, types[o.TypeId], o.PathId)
		if meta == nil {
			return nil, "贴图元数据解析失败"
		}
		rgba, decErr := decodeTexture(data, meta.DataOffset, meta.DataLength, meta.Width, meta.Height, meta.Format)
		if decErr != "" {
			return nil, decErr
		}
		if maxEdge > 0 && (meta.Width > maxEdge || meta.Height > maxEdge) {
			nw, nh := meta.Width, meta.Height
			if nw >= nh {
				nh = nh * maxEdge / nw
				nw = maxEdge
			} else {
				nw = nw * maxEdge / nh
				nh = maxEdge
			}
			if nw < 1 {
				nw = 1
			}
			if nh < 1 {
				nh = 1
			}
			rgba = downscaleRGBA(rgba, meta.Width, meta.Height, nw, nh)
			return writePngBytes(rgba, nw, nh), ""
		}
		return writePngBytes(rgba, meta.Width, meta.Height), ""
	}
	return nil, "包内没有该贴图对象"
}

// unpackBundle 解包 UnityFS 容器数据区（blocksInfo 用 LZ4 时自解压）。
func unpackBundle(bundle []byte) ([]byte, string) {
	if len(bundle) < 48 || bundle[0] != 'U' || bundle[1] != 'n' || bundle[2] != 'i' ||
		bundle[3] != 't' || bundle[4] != 'y' || bundle[5] != 'F' || bundle[6] != 'S' {
		return nil, "不是 UnityFS 容器（签名不符）"
	}
	at := 8
	version := readInt32Big(bundle, at)
	at += 4
	at = skipCString(bundle, at)
	at = skipCString(bundle, at)
	totalSize := readInt64Big(bundle, at)
	at += 8
	compInfoSize := readInt32Big(bundle, at)
	at += 4
	uncompInfoSize := readInt32Big(bundle, at)
	at += 4
	flags := readInt32Big(bundle, at)
	at += 4
	if version >= 7 {
		at += 4
	}
	headerEnd := at
	infoPos := headerEnd
	if flags&0x80 != 0 {
		infoPos = int(totalSize) - compInfoSize
	}
	if infoPos < 0 || infoPos+compInfoSize > len(bundle) || uncompInfoSize <= 0 || uncompInfoSize > 4194304 {
		return nil, "blocksInfo 越界（位置 " + itoaSmall(infoPos) + " · 压缩 " + itoaSmall(compInfoSize) + " · 解压 " + itoaSmall(uncompInfoSize) + "）"
	}
	var info []byte
	infoComp := flags & 0x3F
	if infoComp == 0 {
		info = make([]byte, compInfoSize)
		copy(info, bundle[infoPos:infoPos+compInfoSize])
	} else if infoComp == 2 || infoComp == 3 {
		dst := make([]byte, uncompInfoSize)
		got, lz4Err := lz4BlockDecompress(bundle, infoPos, compInfoSize, dst, 0, uncompInfoSize)
		if lz4Err != "" {
			return nil, "blocksInfo 解压失败：" + lz4Err
		}
		info = dst[:got]
	} else {
		return nil, "blocksInfo 压缩方式不支持（" + itoaSmall(infoComp) + "）"
	}
	p := 16
	blockCount := readInt32Big(info, p)
	p += 4
	tableEnd := 16 + 4 + blockCount*10
	if blockCount <= 0 || tableEnd > len(info) {
		return nil, "数据块数异常（" + itoaSmall(blockCount) + " · blocksInfo 仅 " + itoaSmall(len(info)) + " 字节）"
	}
	blockUncomp := make([]int, blockCount)
	blockComp := make([]int, blockCount)
	blockFlags := make([]int, blockCount)
	for i := 0; i < blockCount; i++ {
		blockUncomp[i] = readInt32Big(info, p)
		p += 4
		blockComp[i] = readInt32Big(info, p)
		p += 4
		blockFlags[i] = int(info[p])<<8 | int(info[p+1])
		p += 2
	}
	dataStart := infoPos + compInfoSize
	if flags&0x80 != 0 {
		dataStart = headerEnd
	}
	total := 0
	for i := 0; i < blockCount; i++ {
		total += blockUncomp[i]
	}
	if total <= 0 || total > maxBundleBytes {
		return nil, "数据区大小异常（" + itoaSmall(total) + " 字节）"
	}
	data := make([]byte, total)
	pos := dataStart
	writeAt := 0
	for i := 0; i < blockCount; i++ {
		if pos+blockComp[i] > len(bundle) {
			return nil, "数据块 #" + itoaSmall(i) + " 越界"
		}
		comp := blockFlags[i] & 0x3F
		if comp == 0 {
			copy(data[writeAt:writeAt+blockComp[i]], bundle[pos:pos+blockComp[i]])
		} else if comp == 2 || comp == 3 {
			got, lz4Err := lz4BlockDecompress(bundle, pos, blockComp[i], data, writeAt, blockUncomp[i])
			if lz4Err != "" {
				return nil, "数据块 #" + itoaSmall(i) + " 解压失败：" + lz4Err
			}
			if got != blockUncomp[i] {
				return nil, "数据块 #" + itoaSmall(i) + " 解压长度不符（" + itoaSmall(got) + " ≠ " + itoaSmall(blockUncomp[i]) + "）"
			}
		} else {
			return nil, "数据块压缩方式不支持（" + itoaSmall(comp) + "）"
		}
		pos += blockComp[i]
		writeAt += blockUncomp[i]
	}
	return data, ""
}

// parseSerializedFile 解析 SerializedFile——输出数据偏移、类型表与对象表。
func parseSerializedFile(data []byte) (int, []sfType, []sfObject, string) {
	types := make([]sfType, 0, 8)
	objects := make([]sfObject, 0, 64)
	if len(data) < 32 {
		return 0, types, objects, "SerializedFile 过短"
	}
	metadataSize := readInt32Big(data, 0)
	dataOffset := readInt32Big(data, 12)
	if metadataSize <= 0 || metadataSize > len(data) || dataOffset <= 0 || dataOffset > len(data) {
		return 0, types, objects, "SerializedFile 头部异常（元数据 " + itoaSmall(metadataSize) + " · 数据偏移 " + itoaSmall(dataOffset) + "）"
	}
	at := 20
	at = skipCString(data, at)
	at += 4
	if at >= len(data) {
		return 0, types, objects, "SerializedFile 头部越界"
	}
	enableTypeTree := int(data[at])
	at++
	typeCount := readInt32Little(data, at)
	at += 4
	if typeCount <= 0 || typeCount > 4096 {
		return 0, types, objects, "类型数异常（" + itoaSmall(typeCount) + "）"
	}
	for i := 0; i < typeCount; i++ {
		if at+23 > len(data) {
			return 0, types, objects, "类型表越界（#" + itoaSmall(i) + "）"
		}
		var t sfType
		t.ClassId = readInt32Little(data, at)
		at += 4
		at++
		at += 2
		if t.ClassId == 114 {
			at += 16
		}
		at += 16
		if enableTypeTree != 0 {
			t.NodeCountInit(readInt32Little(data, at))
			at += 4
			stringBufferSize := readInt32Little(data, at)
			at += 4
			nodeCount := len(t.Nodes)
			if nodeCount < 0 || nodeCount > 20000 || stringBufferSize < 0 || stringBufferSize > 1048576 {
				return 0, types, objects, "类型 #" + itoaSmall(i) + " 节点数 / 字段名池异常（" + itoaSmall(nodeCount) + " / " + itoaSmall(stringBufferSize) + "）"
			}
			if at+nodeCount*24+stringBufferSize > len(data) {
				return 0, types, objects, "类型 #" + itoaSmall(i) + " 节点区越界"
			}
			for k := 0; k < nodeCount; k++ {
				var n sfNode
				n.Level = int(data[at+2])
				n.TypeStrOffset = int64(uint32(readInt32Little(data, at+4)))
				n.NameStrOffset = int64(uint32(readInt32Little(data, at+8)))
				n.ByteSize = readInt32Little(data, at+12)
				n.MetaFlag = readInt32Little(data, at+20)
				at += 24
				t.Nodes[k] = n
			}
			t.StringBuffer = make([]byte, stringBufferSize)
			copy(t.StringBuffer, data[at:at+stringBufferSize])
			at += stringBufferSize
		}
		types = append(types, t)
	}
	if at+4 > len(data) {
		return 0, types, objects, "对象表越界"
	}
	objectCount := readInt32Little(data, at)
	at += 4
	at = (at + 3) / 4 * 4
	if objectCount < 0 || objectCount > 1000000 || at+objectCount*20 > len(data) {
		return 0, types, objects, "对象数异常（" + itoaSmall(objectCount) + "）"
	}
	for i := 0; i < objectCount; i++ {
		var o sfObject
		o.PathId = readInt64Little(data, at)
		at += 8
		o.ByteStart = readInt32Little(data, at)
		at += 4
		o.ByteSize = readInt32Little(data, at)
		at += 4
		o.TypeId = readInt32Little(data, at)
		at += 4
		objects = append(objects, o)
	}
	return dataOffset, types, objects, ""
}

// NodeCountInit 按节点数预分配 Nodes（C# 先读 nodeCount 再逐项 Add）。
func (t *sfType) NodeCountInit(n int) {
	if n > 0 && n <= 20000 {
		t.Nodes = make([]sfNode, n)
	}
}

// readTextureMeta 按 TypeTree 节点序列读取一个 Texture2D 的关键字段——失败返回 nil。
func readTextureMeta(data []byte, at int, t sfType, pathId int64) *Unity3dTexture {
	if at < 0 || at >= len(data) {
		return nil
	}
	tex := &Unity3dTexture{PathId: pathId, Name: ""}
	k := 1
	for k < len(t.Nodes) {
		n := t.Nodes[k]
		name := nodeName(t, n.NameStrOffset)
		next := k + 1
		for next < len(t.Nodes) && t.Nodes[next].Level > n.Level {
			next++
		}
		if name == "m_Name" {
			length := readInt32Little(data, at)
			at += 4
			safe := length
			if safe < 0 || safe > 4096 || at+safe > len(data) {
				safe = 0
			}
			tex.Name = string(data[at : at+safe])
			at = skipAligned(data, at, length)
			k = next
			continue
		}
		if name == "image data" {
			length := readInt32Little(data, at)
			at += 4
			tex.DataLength = length
			tex.DataOffset = at
			at = skipAligned(data, at, length)
			k = next
			continue
		}
		if name == "m_StreamData" {
			at += 8
			at = skipCString(data, at)
			k = next
			continue
		}
		if name == "m_Width" || name == "m_Height" || name == "m_TextureFormat" {
			v := readInt32Little(data, at)
			at += 4
			switch name {
			case "m_Width":
				tex.Width = v
			case "m_Height":
				tex.Height = v
			case "m_TextureFormat":
				tex.Format = v
			}
			k++
			continue
		}
		if name == "m_CompleteImageSize" || name == "m_MipCount" || name == "m_ImageCount" || name == "m_TextureDimension" ||
			name == "m_LightmapFormat" || name == "m_ColorSpace" || name == "m_ForcedFallbackFormat" {
			at += 4
			k++
			continue
		}
		if name == "m_DownscaleFallback" || name == "m_IsReadable" {
			at++
			at = (at + 3) / 4 * 4
			k++
			continue
		}
		if n.ByteSize == -1 {
			length := readInt32Little(data, at)
			at += 4
			at = skipAligned(data, at, length)
			k = next
			continue
		}
		if n.ByteSize > 0 && next > k+1 {
			k++
			continue
		}
		if n.ByteSize > 0 {
			at += n.ByteSize
			k++
			continue
		}
		k++
	}
	if tex.Width <= 0 || tex.Height <= 0 || tex.DataLength <= 0 {
		return nil
	}
	if tex.Width > 16384 || tex.Height > 16384 || tex.DataLength > maxTextureBytes {
		return nil
	}
	if tex.DataOffset+tex.DataLength > len(data) {
		return nil
	}
	return tex
}

// skipAligned 跳过 len 字节并按 4 字节对齐（带边界钳制）。
func skipAligned(data []byte, at, length int) int {
	if length > 0 && length < maxTextureBytes && at+length <= len(data) {
		at += length
	}
	at = (at + 3) / 4 * 4
	if at > len(data) {
		at = len(data)
	}
	return at
}

// nodeName 解析 TypeTree 名字——高位 0x80 为 Unity 内置类型名表索引（本实现不带该表，返回占位）。
func nodeName(t sfType, offset int64) string {
	if offset&0x80000000 != 0 {
		return "#" + itoaSmall(int(offset&0x7FFFFFFF))
	}
	if t.StringBuffer == nil || offset >= int64(len(t.StringBuffer)) {
		return "?"
	}
	start := int(offset)
	q := start
	for q < len(t.StringBuffer) && t.StringBuffer[q] != 0 {
		q++
	}
	return string(t.StringBuffer[start:q])
}

// decodeTexture 按 Unity 贴图格式码解码为 RGBA32。
func decodeTexture(data []byte, at, length, width, height, format int) ([]byte, string) {
	if at <= 0 || length <= 0 || at+length > len(data) {
		return nil, "像素数据越界（@ " + itoaSmall(at) + " · " + itoaSmall(length) + " 字节）"
	}
	src := make([]byte, length)
	copy(src, data[at:at+length])
	switch format {
	case 4:
		return decodeRgba32(src, width, height), ""
	case 3:
		return decodeRgb24(src, width, height), ""
	case 14:
		return decodeBgra32(src, width, height), ""
	case 5:
		return decodeArgb32(src, width, height), ""
	case 7:
		return decodeRgb565(src, width, height), ""
	case 10:
		return decodeDxt1(src, width, height), ""
	case 12:
		return decodeDxt5(src, width, height), ""
	case 25:
		return decodeBc7(src, width, height)
	}
	return nil, "暂不支持的贴图格式 " + itoaSmall(format)
}

// decodeRgba32 原样拷贝（不足部分留 0）。
func decodeRgba32(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	copy(dst, src)
	return dst
}

// decodeRgb24 RGB24 展开为 RGBA32。
func decodeRgb24(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	count := width * height
	for i := 0; i < count; i++ {
		if i*3+2 >= len(src) {
			break
		}
		dst[i*4] = src[i*3]
		dst[i*4+1] = src[i*3+1]
		dst[i*4+2] = src[i*3+2]
		dst[i*4+3] = 255
	}
	return dst
}

// decodeBgra32 BGRA32 换序为 RGBA32。
func decodeBgra32(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	count := width * height
	for i := 0; i < count; i++ {
		if i*4+3 >= len(src) {
			break
		}
		dst[i*4] = src[i*4+2]
		dst[i*4+1] = src[i*4+1]
		dst[i*4+2] = src[i*4]
		dst[i*4+3] = src[i*4+3]
	}
	return dst
}

// decodeArgb32 ARGB32 换序为 RGBA32。
func decodeArgb32(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	count := width * height
	for i := 0; i < count; i++ {
		if i*4+3 >= len(src) {
			break
		}
		dst[i*4] = src[i*4+1]
		dst[i*4+1] = src[i*4+2]
		dst[i*4+2] = src[i*4+3]
		dst[i*4+3] = src[i*4]
	}
	return dst
}

// decodeRgb565 RGB565 展开为 RGBA32。
func decodeRgb565(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	count := width * height
	for i := 0; i < count; i++ {
		if i*2+1 >= len(src) {
			break
		}
		c := int(src[i*2]) | int(src[i*2+1])<<8
		dst[i*4] = byte(((c >> 11) & 0x1F) * 255 / 31)
		dst[i*4+1] = byte(((c >> 5) & 0x3F) * 255 / 63)
		dst[i*4+2] = byte((c & 0x1F) * 255 / 31)
		dst[i*4+3] = 255
	}
	return dst
}

// decodeDxt1 DXT1（BC1）解码为 RGBA32——每 8 字节一个 4×4 块。
func decodeDxt1(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	bw := (width + 3) / 4
	bh := (height + 3) / 4
	at := 0
	for by := 0; by < bh; by++ {
		for bx := 0; bx < bw; bx++ {
			if at+8 > len(src) {
				return dst
			}
			c0 := int(src[at]) | int(src[at+1])<<8
			c1 := int(src[at+2]) | int(src[at+3])<<8
			bits := uint32(src[at+4]) | uint32(src[at+5])<<8 | uint32(src[at+6])<<16 | uint32(src[at+7])<<24
			at += 8
			r0 := ((c0 >> 11) & 0x1F) * 255 / 31
			g0 := ((c0 >> 5) & 0x3F) * 255 / 63
			b0 := (c0 & 0x1F) * 255 / 31
			r1 := ((c1 >> 11) & 0x1F) * 255 / 31
			g1 := ((c1 >> 5) & 0x3F) * 255 / 63
			b1 := (c1 & 0x1F) * 255 / 31
			for py := 0; py < 4; py++ {
				for px := 0; px < 4; px++ {
					x := bx*4 + px
					y := by*4 + py
					if x >= width || y >= height {
						continue
					}
					code := int((bits >> (2 * (py*4 + px))) & 3)
					var r, g, b, a int
					a = 255
					switch code {
					case 0:
						r, g, b = r0, g0, b0
					case 1:
						r, g, b = r1, g1, b1
					case 2:
						r, g, b = (2*r0+r1)/3, (2*g0+g1)/3, (2*b0+b1)/3
					default:
						if c0 > c1 {
							r, g, b = (r0+2*r1)/3, (g0+2*g1)/3, (b0+2*b1)/3
						} else {
							r, g, b, a = 0, 0, 0, 0
						}
					}
					di := (y*width + x) * 4
					dst[di], dst[di+1], dst[di+2], dst[di+3] = byte(r), byte(g), byte(b), byte(a)
				}
			}
		}
	}
	return dst
}

// decodeDxt5 DXT5（BC3）解码为 RGBA32——每 16 字节一个 4×4 块。
func decodeDxt5(src []byte, width, height int) []byte {
	dst := make([]byte, width*height*4)
	bw := (width + 3) / 4
	bh := (height + 3) / 4
	at := 0
	for by := 0; by < bh; by++ {
		for bx := 0; bx < bw; bx++ {
			if at+16 > len(src) {
				return dst
			}
			a0 := int(src[at])
			a1 := int(src[at+1])
			c0 := int(src[at+8]) | int(src[at+9])<<8
			c1 := int(src[at+10]) | int(src[at+11])<<8
			bits := uint32(src[at+12]) | uint32(src[at+13])<<8 | uint32(src[at+14])<<16 | uint32(src[at+15])<<24
			var abits uint64
			for i := 0; i < 6; i++ {
				abits |= uint64(src[at+2+i]) << (8 * i)
			}
			at += 16
			alpha := [8]int{}
			alpha[0] = a0
			alpha[1] = a1
			if a0 > a1 {
				for i := 1; i < 7; i++ {
					alpha[i+1] = ((7-i)*a0 + i*a1) / 7
				}
			} else {
				for i := 1; i < 5; i++ {
					alpha[i+1] = ((5-i)*a0 + i*a1) / 5
				}
				alpha[6] = 0
				alpha[7] = 255
			}
			r0 := ((c0 >> 11) & 0x1F) * 255 / 31
			g0 := ((c0 >> 5) & 0x3F) * 255 / 63
			b0 := (c0 & 0x1F) * 255 / 31
			r1 := ((c1 >> 11) & 0x1F) * 255 / 31
			g1 := ((c1 >> 5) & 0x3F) * 255 / 63
			b1 := (c1 & 0x1F) * 255 / 31
			for py := 0; py < 4; py++ {
				for px := 0; px < 4; px++ {
					x := bx*4 + px
					y := by*4 + py
					if x >= width || y >= height {
						continue
					}
					idx := py*4 + px
					code := int((bits >> (2 * idx)) & 3)
					var r, g, b int
					switch code {
					case 0:
						r, g, b = r0, g0, b0
					case 1:
						r, g, b = r1, g1, b1
					case 2:
						r, g, b = (2*r0+r1)/3, (2*g0+g1)/3, (2*b0+b1)/3
					default:
						r, g, b = (r0+2*r1)/3, (g0+2*g1)/3, (b0+2*b1)/3
					}
					acode := int((abits >> (3 * idx)) & 7)
					di := (y*width + x) * 4
					dst[di], dst[di+1], dst[di+2], dst[di+3] = byte(r), byte(g), byte(b), byte(alpha[acode])
				}
			}
		}
	}
	return dst
}

// downscaleRGBA 等比缩小 RGBA32 位图（盒式平均）。
func downscaleRGBA(src []byte, sw, sh, dw, dh int) []byte {
	dst := make([]byte, dw*dh*4)
	for y := 0; y < dh; y++ {
		sy0 := y * sh / dh
		sy1 := (y + 1) * sh / dh
		if sy1 <= sy0 {
			sy1 = sy0 + 1
		}
		for x := 0; x < dw; x++ {
			sx0 := x * sw / dw
			sx1 := (x + 1) * sw / dw
			if sx1 <= sx0 {
				sx1 = sx0 + 1
			}
			var r, g, b, a, cnt int
			for sy := sy0; sy < sy1 && sy < sh; sy++ {
				for sx := sx0; sx < sx1 && sx < sw; sx++ {
					si := (sy*sw + sx) * 4
					r += int(src[si])
					g += int(src[si+1])
					b += int(src[si+2])
					a += int(src[si+3])
					cnt++
				}
			}
			if cnt <= 0 {
				cnt = 1
			}
			di := (y*dw + x) * 4
			dst[di], dst[di+1], dst[di+2], dst[di+3] = byte(r/cnt), byte(g/cnt), byte(b/cnt), byte(a/cnt)
		}
	}
	return dst
}

// writePngBytes 把 RGBA32 位图编码为 PNG 字节（8 位 RGBA）。
func writePngBytes(rgba []byte, width, height int) []byte {
	img := image.NewRGBA(image.Rect(0, 0, width, height))
	copy(img.Pix, rgba)
	var buf bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.BestSpeed}
	if err := enc.Encode(&buf, img); err != nil {
		return nil
	}
	return buf.Bytes()
}

// lz4BlockDecompress LZ4 块解压（Unity 使用的 block 格式，无帧头）——成功返回写出字节数。
func lz4BlockDecompress(src []byte, srcAt, srcLen int, dst []byte, dstAt, dstLen int) (int, string) {
	ip := srcAt
	end := srcAt + srcLen
	op := dstAt
	opEnd := dstAt + dstLen
	for ip < end {
		token := int(src[ip])
		ip++
		litLen := token >> 4
		if litLen == 15 {
			b := 255
			for b == 255 {
				if ip >= end {
					return 0, "字面量长度越界"
				}
				b = int(src[ip])
				ip++
				litLen += b
			}
		}
		if ip+litLen > end || op+litLen > opEnd {
			return 0, "字面量越界（ip " + itoaSmall(ip) + " · lit " + itoaSmall(litLen) + " · op " + itoaSmall(op) + "）"
		}
		copy(dst[op:op+litLen], src[ip:ip+litLen])
		ip += litLen
		op += litLen
		if ip >= end {
			break
		}
		if ip+2 > end {
			return 0, "匹配偏移越界"
		}
		offset := int(src[ip]) | int(src[ip+1])<<8
		ip += 2
		if offset == 0 || offset > op-dstAt {
			return 0, "匹配偏移非法（" + itoaSmall(offset) + "）"
		}
		matchLen := token & 0x0F
		if matchLen == 15 {
			b := 255
			for b == 255 {
				if ip >= end {
					return 0, "匹配长度越界"
				}
				b = int(src[ip])
				ip++
				matchLen += b
			}
		}
		matchLen += 4
		if op+matchLen > opEnd {
			return 0, "匹配越界"
		}
		from := op - offset
		for i := 0; i < matchLen; i++ {
			dst[op+i] = dst[from+i]
		}
		op += matchLen
	}
	return op - dstAt, ""
}

// readInt32Big 读大端 32 位整数（带边界钳制）。
func readInt32Big(buf []byte, at int) int {
	if at < 0 || at+4 > len(buf) {
		return 0
	}
	return int(buf[at])<<24 | int(buf[at+1])<<16 | int(buf[at+2])<<8 | int(buf[at+3])
}

// readInt64Big 读大端 64 位整数（带边界钳制）。
func readInt64Big(buf []byte, at int) int64 {
	if at < 0 || at+8 > len(buf) {
		return 0
	}
	var v int64
	for i := 0; i < 8; i++ {
		v = v<<8 | int64(buf[at+i])
	}
	return v
}

// readInt32Little 读小端 32 位整数（带边界钳制）。
func readInt32Little(buf []byte, at int) int {
	if at < 0 || at+4 > len(buf) {
		return 0
	}
	return int(buf[at]) | int(buf[at+1])<<8 | int(buf[at+2])<<16 | int(buf[at+3])<<24
}

// readInt64Little 读小端 64 位整数（带边界钳制）。
func readInt64Little(buf []byte, at int) int64 {
	if at < 0 || at+8 > len(buf) {
		return 0
	}
	var v int64
	for i := 7; i >= 0; i-- {
		v = v<<8 | int64(buf[at+i])
	}
	return v
}

// skipCString 跳过 null 终止字符串（返回终止符之后的位置）。
func skipCString(buf []byte, at int) int {
	for at < len(buf) && buf[at] != 0 {
		at++
	}
	return at + 1
}

// WriteU3dInt32Big 写大端 32 位整数（PNG 块用）。
func WriteU3dInt32Big(buf []byte, at, value int) {
	binary.BigEndian.PutUint32(buf[at:at+4], uint32(value))
}

// u3dErr 统一诊断前缀（保留给上层拼接用）。
func u3dErr(format string, args ...interface{}) string {
	return fmt.Sprintf(format, args...)
}
