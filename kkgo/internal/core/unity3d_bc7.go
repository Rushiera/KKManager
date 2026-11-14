// BC7 贴图解码（Unity 格式码 25）——对齐 C# Core/Unity3dReader.cs §DecodeBc7。
//
// 16 字节一块（4×4），覆盖全部 8 种模式：模式前缀 → 分区 / 旋转 / 索引选择 → 端点 → P 位 → 索引 → 插值。
package core

// bc7Partition2 2 子集分区表——每项 16 位，位 i 置位表示像素 i 属于子集 1。
var bc7Partition2 = [64]uint16{
	0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80,
	0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
	0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce,
	0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
	0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
	0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
	0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c,
	0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22,
}

// bc7Partition3 3 子集分区表——每项 32 位，每像素 2 位给出所属子集（0 / 1 / 2）。
var bc7Partition3 = [64]uint32{
	0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
	0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
	0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
	0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
	0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
	0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
	0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
	0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254,
}

// bc7Anchor2 2 子集锚点表——子集 1 的锚点像素序号（该像素的索引少读 1 位）。
var bc7Anchor2 = [64]byte{
	15, 15, 15, 15, 15, 15, 15, 15,
	15, 15, 15, 15, 15, 15, 15, 15,
	15, 2, 8, 2, 2, 8, 8, 15,
	2, 8, 2, 2, 8, 8, 2, 2,
	15, 15, 6, 8, 2, 8, 15, 15,
	2, 8, 2, 2, 2, 15, 15, 6,
	6, 2, 6, 8, 15, 15, 2, 2,
	15, 15, 15, 15, 15, 2, 2, 15,
}

// bc7Anchor3 3 子集锚点表——[子集 - 1][分区号] 给出该子集的锚点像素序号。
var bc7Anchor3 = [2][64]byte{
	{
		3, 3, 15, 15, 8, 3, 15, 15,
		8, 8, 6, 6, 6, 5, 3, 3,
		3, 3, 8, 15, 3, 3, 6, 10,
		5, 8, 8, 6, 8, 5, 15, 15,
		8, 15, 3, 5, 6, 10, 8, 15,
		15, 3, 15, 5, 15, 15, 15, 15,
		3, 15, 5, 5, 5, 8, 5, 10,
		5, 10, 8, 13, 15, 12, 3, 3,
	},
	{
		15, 8, 8, 3, 15, 15, 3, 8,
		15, 15, 15, 15, 15, 15, 15, 8,
		15, 8, 15, 3, 15, 8, 15, 8,
		3, 15, 6, 10, 15, 15, 10, 8,
		15, 3, 15, 10, 10, 8, 9, 10,
		6, 15, 8, 15, 3, 6, 6, 8,
		15, 3, 15, 15, 15, 15, 15, 15,
		15, 15, 15, 15, 3, 15, 15, 8,
	},
}

// bc7Factors 插值权重表——[索引位宽 - 2][索引值] 给出 0..64 的权重（按 64 归一）。
var bc7Factors = [3][16]byte{
	{0, 21, 43, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
	{0, 9, 18, 27, 37, 46, 55, 64, 0, 0, 0, 0, 0, 0, 0, 0},
	{0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64},
}

// bc7ModeInfo BC7 单个模式的字段布局。
type bc7ModeInfo struct {
	// Subsets 子集数（1 / 2 / 3）。
	Subsets int
	// PartitionBits 分区号位数。
	PartitionBits int
	// RotationBits 旋转位数。
	RotationBits int
	// IndexSelectionBits 索引选择位数。
	IndexSelectionBits int
	// ColorBits 颜色端点的每通道位数。
	ColorBits int
	// AlphaBits Alpha 端点位（0 = 全部不透明）。
	AlphaBits int
	// EndpointPBits 每端点的 P 位数（0 = 不用逐端点 P 位）。
	EndpointPBits int
	// SharedPBits 每子集共享的 P 位数（0 = 不用共享 P 位）。
	SharedPBits int
	// IndexBits0 主索引位数（颜色流）。
	IndexBits0 int
	// IndexBits1 次索引位数（Alpha 流；0 = 与颜色流共用）。
	IndexBits1 int
}

// bc7ModeInfos BC7 的 8 种模式布局表（索引 = 模式号）。
var bc7ModeInfos = [8]bc7ModeInfo{
	{3, 4, 0, 0, 4, 0, 1, 0, 3, 0},
	{2, 6, 0, 0, 6, 0, 0, 1, 3, 0},
	{3, 6, 0, 0, 5, 0, 0, 0, 2, 0},
	{2, 6, 0, 0, 7, 0, 1, 0, 2, 0},
	{1, 0, 2, 1, 5, 6, 0, 0, 2, 3},
	{1, 0, 2, 0, 7, 8, 0, 0, 2, 2},
	{1, 0, 0, 0, 7, 7, 1, 0, 4, 0},
	{2, 6, 0, 0, 5, 5, 1, 0, 2, 0},
}

// decodeBc7 BC7 解码为 RGBA32——16 字节一块，覆盖全部 8 种模式。
func decodeBc7(src []byte, width, height int) ([]byte, string) {
	dst := make([]byte, width*height*4)
	var block [16]byte
	var pixels [64]byte
	var epR, epG, epB, epA [6]int
	var index, offset [2]int
	bw := (width + 3) / 4
	bh := (height + 3) / 4
	at := 0
	for by := 0; by < bh; by++ {
		for bx := 0; bx < bw; bx++ {
			if at+16 > len(src) {
				return dst, ""
			}
			copy(block[:], src[at:at+16])
			at += 16
			decodeBc7Block(&block, &pixels, &epR, &epG, &epB, &epA, &index, &offset)
			copyBc7Block(&pixels, dst, width, height, bx*4, by*4)
		}
	}
	return dst, ""
}

// decodeBc7Block 解一个 BC7 块（16 字节）为 4×4 的 RGBA 像素（64 字节）。
func decodeBc7Block(block *[16]byte, pixels *[64]byte, epR, epG, epB, epA *[6]int, index, offset *[2]int) {
	bitPos := 0
	mode := 0
	for mode < 8 && bc7ReadBits(block, bitPos, 1) == 0 {
		bitPos++
		mode++
	}
	if mode == 8 {
		for i := 0; i < 64; i++ {
			pixels[i] = 0
		}
		return
	}
	bitPos++
	mi := bc7ModeInfos[mode]
	pBits := mi.EndpointPBits
	if pBits == 0 {
		pBits = mi.SharedPBits
	}
	partitionSetIdx := bc7ReadBits(block, bitPos, mi.PartitionBits)
	bitPos += mi.PartitionBits
	rotationMode := bc7ReadBits(block, bitPos, mi.RotationBits)
	bitPos += mi.RotationBits
	indexSelectionMode := bc7ReadBits(block, bitPos, mi.IndexSelectionBits)
	bitPos += mi.IndexSelectionBits
	for ii := 0; ii < mi.Subsets; ii++ {
		epR[ii*2] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
		epR[ii*2+1] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
	}
	for ii := 0; ii < mi.Subsets; ii++ {
		epG[ii*2] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
		epG[ii*2+1] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
	}
	for ii := 0; ii < mi.Subsets; ii++ {
		epB[ii*2] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
		epB[ii*2+1] = bc7ReadBits(block, bitPos, mi.ColorBits) << pBits
		bitPos += mi.ColorBits
	}
	if mi.AlphaBits != 0 {
		for ii := 0; ii < mi.Subsets; ii++ {
			epA[ii*2] = bc7ReadBits(block, bitPos, mi.AlphaBits) << pBits
			bitPos += mi.AlphaBits
			epA[ii*2+1] = bc7ReadBits(block, bitPos, mi.AlphaBits) << pBits
			bitPos += mi.AlphaBits
		}
	} else {
		for ii := 0; ii < 6; ii++ {
			epA[ii] = 255
		}
	}
	if pBits != 0 {
		for ii := 0; ii < mi.Subsets; ii++ {
			pda := bc7ReadBits(block, bitPos, pBits)
			bitPos += pBits
			pdb := pda
			if mi.SharedPBits == 0 {
				pdb = bc7ReadBits(block, bitPos, pBits)
				bitPos += pBits
			}
			epR[ii*2] |= pda
			epR[ii*2+1] |= pdb
			epG[ii*2] |= pda
			epG[ii*2+1] |= pdb
			epB[ii*2] |= pda
			epB[ii*2+1] |= pdb
			epA[ii*2] |= pda
			epA[ii*2+1] |= pdb
		}
	}
	colorBits := mi.ColorBits + pBits
	for ii := 0; ii < mi.Subsets; ii++ {
		epR[ii*2] = bc7Expand(epR[ii*2], colorBits)
		epR[ii*2+1] = bc7Expand(epR[ii*2+1], colorBits)
		epG[ii*2] = bc7Expand(epG[ii*2], colorBits)
		epG[ii*2+1] = bc7Expand(epG[ii*2+1], colorBits)
		epB[ii*2] = bc7Expand(epB[ii*2], colorBits)
		epB[ii*2+1] = bc7Expand(epB[ii*2+1], colorBits)
	}
	if mi.AlphaBits != 0 {
		alphaBits := mi.AlphaBits + pBits
		for ii := 0; ii < mi.Subsets; ii++ {
			epA[ii*2] = bc7Expand(epA[ii*2], alphaBits)
			epA[ii*2+1] = bc7Expand(epA[ii*2+1], alphaBits)
		}
	}
	hasIndexBits1 := mi.IndexBits1 != 0
	factors0 := bc7Factors[mi.IndexBits0-2]
	factors1 := factors0
	if hasIndexBits1 {
		factors1 = bc7Factors[mi.IndexBits1-2]
	}
	offset[0] = 0
	offset[1] = mi.Subsets * (16*mi.IndexBits0 - 1)
	for py := 0; py < 4; py++ {
		for px := 0; px < 4; px++ {
			idx := py*4 + px
			subsetIndex := 0
			indexAnchor := 0
			if mi.Subsets == 2 {
				subsetIndex = int((bc7Partition2[partitionSetIdx] >> idx) & 1)
				if subsetIndex != 0 {
					indexAnchor = int(bc7Anchor2[partitionSetIdx])
				}
			} else if mi.Subsets == 3 {
				subsetIndex = int((bc7Partition3[partitionSetIdx] >> (2 * idx)) & 3)
				if subsetIndex != 0 {
					indexAnchor = int(bc7Anchor3[subsetIndex-1][partitionSetIdx])
				}
			}
			anchor := 0
			if idx == indexAnchor {
				anchor = 1
			}
			num0 := mi.IndexBits0 - anchor
			num1 := 0
			if hasIndexBits1 {
				num1 = mi.IndexBits1 - anchor
			}
			index[0] = bc7ReadBits(block, bitPos+offset[0], num0)
			index[1] = index[0]
			if hasIndexBits1 {
				index[1] = bc7ReadBits(block, bitPos+offset[1], num1)
			}
			offset[0] += num0
			offset[1] += num1
			fc := int(factors0[index[0]])
			fa := int(factors1[index[1]])
			if indexSelectionMode != 0 {
				fc = int(factors1[index[1]])
				fa = int(factors0[index[0]])
			}
			fca := 64 - fc
			faa := 64 - fa
			s := subsetIndex * 2
			rr := (epR[s]*fca + epR[s+1]*fc + 32) >> 6
			gg := (epG[s]*fca + epG[s+1]*fc + 32) >> 6
			bb := (epB[s]*fca + epB[s+1]*fc + 32) >> 6
			aa := (epA[s]*faa + epA[s+1]*fa + 32) >> 6
			switch rotationMode {
			case 1:
				aa, rr = rr, aa
			case 2:
				aa, gg = gg, aa
			case 3:
				aa, bb = bb, aa
			}
			pixels[idx*4] = byte(rr)
			pixels[idx*4+1] = byte(gg)
			pixels[idx*4+2] = byte(bb)
			pixels[idx*4+3] = byte(aa)
		}
	}
}

// bc7ReadBits 从 16 字节块里按位序取 numBits 位（低位在前，跨字节按小端拼接）。
func bc7ReadBits(block *[16]byte, bitPos, numBits int) int {
	if numBits <= 0 {
		return 0
	}
	pos := bitPos / 8
	shift := bitPos & 7
	avail := 16 - pos
	if avail > 4 {
		avail = 4
	}
	var data uint32
	for i := 0; i < avail; i++ {
		data |= uint32(block[pos+i]) << (8 * i)
	}
	return int((data >> shift) & ((1 << uint(numBits)) - 1))
}

// bc7Expand BC7 端点量化扩展——把 bits 位的值扩到 8 位。
func bc7Expand(value, bits int) int {
	v := value << (8 - bits)
	return v | (v >> uint(bits))
}

// copyBc7Block 把解出的 4×4 像素拷到目标位图。
func copyBc7Block(pixels *[64]byte, dst []byte, width, height, x0, y0 int) {
	for py := 0; py < 4; py++ {
		y := y0 + py
		if y < 0 || y >= height {
			continue
		}
		for px := 0; px < 4; px++ {
			x := x0 + px
			if x < 0 || x >= width {
				continue
			}
			s := (py*4 + px) * 4
			d := (y*width + x) * 4
			dst[d] = pixels[s]
			dst[d+1] = pixels[s+1]
			dst[d+2] = pixels[s+2]
			dst[d+3] = pixels[s+3]
		}
	}
}
