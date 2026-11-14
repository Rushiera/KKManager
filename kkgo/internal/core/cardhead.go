// 卡片头段判定与声明区收集（扫描用）——对齐 C# Core/CardFileSession.cs §EnsureHead 与
// Core/CardReader.cs §ReadHeadState / §CollectRefs。
//
// 判定分六态：Ok = 正常卡片；NoSignature / NoIend / NoData / TypeUnknown = 非卡文件（各有理由）；
// Io = 文件读不了（按失败出声）。理由文案与 C# 逐字对齐（会落进 non_card.reason，属对拍面）。
package core

import (
	"os"
)

// CardHeadState 卡片头段判定状态。
type CardHeadState int

const (
	// CardHeadUnknown 尚未判定。
	CardHeadUnknown CardHeadState = iota
	// CardHeadOk 正常卡片（卡类型 / 数据版本读到）。
	CardHeadOk
	// CardHeadNoSignature 不是 PNG 文件（签名不符）。
	CardHeadNoSignature
	// CardHeadNoIend PNG 不完整（块链找不到 IEND）。
	CardHeadNoIend
	// CardHeadNoData 无数据区（IEND 之后没有字节——纯图片）。
	CardHeadNoData
	// CardHeadTypeUnknown 数据区头段无法识别。
	CardHeadTypeUnknown
	// CardHeadIo 文件打不开 / 读取异常。
	CardHeadIo
)

// CardHead 一次头段判定的结果。
type CardHead struct {
	// State 判定状态。
	State CardHeadState
	// CardType 卡类型标记（Ok 时有效；场景卡为 sd）。
	CardType string
	// DataVersion 数据版本。
	DataVersion string
	// ImageEnd 图片区结束偏移（未找到为 -1）。
	ImageEnd int64
	// Reason 判定理由（非卡文件的人读文案；正常时为空）。
	Reason string
}

// ReadCardHeadState 只读卡片头段判定（不扫声明区）——逐段：PNG 签名 → IEND → 数据区 → 卡类型。
func ReadCardHeadState(path string) CardHead {
	out := CardHead{ImageEnd: -1}
	f, err := os.Open(path)
	if err != nil {
		out.State = CardHeadIo
		out.Reason = "文件打不开"
		return out
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		out.State = CardHeadIo
		out.Reason = "文件打不开"
		return out
	}
	size := fi.Size()
	sig := make([]byte, 8)
	if size < 8 || !readFullAt(f, 0, sig) || sig[0] != 0x89 || sig[1] != 'P' || sig[2] != 'N' || sig[3] != 'G' {
		out.State = CardHeadNoSignature
		out.Reason = "不是 PNG 文件"
		return out
	}
	out.ImageEnd = findPngEnd(f, size)
	if out.ImageEnd <= 0 {
		out.State = CardHeadNoIend
		out.Reason = "PNG 不完整（找不到 IEND）"
		return out
	}
	if size-out.ImageEnd < 5 {
		out.State = CardHeadNoData
		out.Reason = "无数据区（纯图片）"
		return out
	}
	cursor := &fileCursor{f: f, pos: out.ImageEnd, size: size}
	cardType, dataVersion, headErr := readCardHead(cursor)
	if headErr != nil {
		out.State = CardHeadTypeUnknown
		out.Reason = "数据区头段无法识别"
		return out
	}
	out.CardType = cardType
	out.DataVersion = dataVersion
	out.State = CardHeadOk
	return out
}

// CollectCardRefs 扫数据区取 mod 引用（部件粒度·未去重）与 UAR 标记计数。
func CollectCardRefs(path string, imageEnd int64) ([]ModRef, int) {
	if imageEnd <= 0 {
		return nil, 0
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, 0
	}
	defer f.Close()
	fi, statErr := f.Stat()
	if statErr != nil {
		return nil, 0
	}
	info := &CardInfo{FilePath: path, ImageEnd: imageEnd, Size: fi.Size()}
	collectDeclaration(f, info)
	return info.ModRefs, info.UarBlocks
}
