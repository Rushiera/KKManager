// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：缩略图——从卡片图片区（PNG）截取并缩为 JPEG 字节，
// 语义对齐 KKManager/Core/Thumbnail.cs：
//   - 图片区长度非法（≤0 / 超 int32）具名失败；
//   - 目标宽 = min(width, 原宽)，高 = round(原高 × 目标宽 / 原宽)（至少 1 像素）；
//   - JPEG 质量参数原样透传。
//
// 与 C# 的已知差异（设计判据已声明）：C# 用 ImageSharp 的重采样与 JPEG 编码器，
// Go 用 stdlib（自写双线性 + image/jpeg）——**不要求逐字节一致**，判据是「可解码 + 尺寸一致 + 质量参数一致」。
package core

import (
	"bytes"
	"fmt"
	"image"
	"image/jpeg"
	"math"
	"os"

	// 图片解码器注册（cards 的图片区是 PNG；兼容 GIF，便于诊断）。
	_ "image/gif"
	_ "image/png"
)

// thumbnailLastError 最近一次缩略图失败的诊断（成功为空）。
var thumbnailLastError string

// ThumbnailLastError 最近一次缩略图失败的诊断（成功为空串）。
func ThumbnailLastError() string { return thumbnailLastError }

// ThumbnailFromCard 读卡片图片区并生成 JPEG 缩略图（失败返回 nil，诊断见 ThumbnailLastError）。
func ThumbnailFromCard(path string, imageEnd int64, width, quality int) []byte {
	thumbnailLastError = ""
	if imageEnd <= 0 {
		thumbnailLastError = "图片区长度非法: " + itoa(imageEnd)
		return nil
	}
	if imageEnd > math.MaxInt32 {
		thumbnailLastError = "图片区过大: " + itoa(imageEnd)
		return nil
	}
	raw := make([]byte, int(imageEnd))
	f, err := os.Open(path)
	if err != nil {
		thumbnailLastError = "打开失败: " + err.Error()
		return nil
	}
	defer f.Close()
	total := 0
	for total < len(raw) {
		n, _ := f.ReadAt(raw[total:], int64(total))
		total += n
		if n <= 0 {
			break
		}
	}
	if total != len(raw) {
		thumbnailLastError = "图片区读取不足: " + itoa(int64(total)) + "/" + itoa(int64(len(raw)))
		return nil
	}
	return ThumbnailFromBytes(raw, width, quality)
}

// ThumbnailFromBytes 从内存里的图片字节生成 JPEG 缩略图（失败返回 nil）。
func ThumbnailFromBytes(raw []byte, width, quality int) []byte {
	thumbnailLastError = ""
	if len(raw) == 0 {
		thumbnailLastError = "字节为空"
		return nil
	}
	src, _, err := image.Decode(bytes.NewReader(raw))
	if err != nil {
		thumbnailLastError = "解码失败: " + err.Error()
		return nil
	}
	sb := src.Bounds()
	sw, sh := sb.Dx(), sb.Dy()
	if sw <= 0 || sh <= 0 {
		thumbnailLastError = "图片尺寸非法: " + itoa(int64(sw)) + "x" + itoa(int64(sh))
		return nil
	}
	w := width
	if w > sw {
		w = sw
	}
	if w < 1 {
		w = 1
	}
	h := int(math.Round(float64(sh) * float64(w) / float64(sw)))
	if h < 1 {
		h = 1
	}
	scaled := resizeBilinear(src, w, h)
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, scaled, &jpeg.Options{Quality: quality}); err != nil {
		thumbnailLastError = "JPEG 编码失败: " + err.Error()
		return nil
	}
	return buf.Bytes()
}

// resizeBilinear 双线性缩放（预乘 alpha 上插值，避免透明边缘发黑）。
func resizeBilinear(src image.Image, w, h int) *image.NRGBA {
	sb := src.Bounds()
	sw, sh := sb.Dx(), sb.Dy()
	dst := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		fy := (float64(y)+0.5)*float64(sh)/float64(h) - 0.5
		y0 := int(math.Floor(fy))
		wy := fy - float64(y0)
		if y0 < 0 {
			y0 = 0
			wy = 0
		}
		y1 := y0 + 1
		if y1 > sh-1 {
			y1 = sh - 1
		}
		for x := 0; x < w; x++ {
			fx := (float64(x)+0.5)*float64(sw)/float64(w) - 0.5
			x0 := int(math.Floor(fx))
			wx := fx - float64(x0)
			if x0 < 0 {
				x0 = 0
				wx = 0
			}
			x1 := x0 + 1
			if x1 > sw-1 {
				x1 = sw - 1
			}
			r00, g00, b00, a00 := pixelAt(src, sb.Min.X+x0, sb.Min.Y+y0)
			r10, g10, b10, a10 := pixelAt(src, sb.Min.X+x1, sb.Min.Y+y0)
			r01, g01, b01, a01 := pixelAt(src, sb.Min.X+x0, sb.Min.Y+y1)
			r11, g11, b11, a11 := pixelAt(src, sb.Min.X+x1, sb.Min.Y+y1)
			r := blend4(r00, r10, r01, r11, wx, wy)
			g := blend4(g00, g10, g01, g11, wx, wy)
			b := blend4(b00, b10, b01, b11, wx, wy)
			a := blend4(a00, a10, a01, a11, wx, wy)
			off := dst.PixOffset(x, y)
			dst.Pix[off] = clamp8(r)
			dst.Pix[off+1] = clamp8(g)
			dst.Pix[off+2] = clamp8(b)
			dst.Pix[off+3] = clamp8(a)
		}
	}
	return dst
}

// pixelAt 取一个像素的 RGBA（0–255 非预乘；越界按边缘取）。
func pixelAt(src image.Image, x, y int) (float64, float64, float64, float64) {
	b := src.Bounds()
	if x < b.Min.X {
		x = b.Min.X
	}
	if y < b.Min.Y {
		y = b.Min.Y
	}
	if x >= b.Max.X {
		x = b.Max.X - 1
	}
	if y >= b.Max.Y {
		y = b.Max.Y - 1
	}
	r, g, bl, a := src.At(x, y).RGBA()
	if a == 0 {
		return 0, 0, 0, 0
	}
	af := float64(a) / 257.0
	return float64(r) / float64(a) * af, float64(g) / float64(a) * af, float64(bl) / float64(a) * af, af
}

// blend4 四个邻点按权重插值。
func blend4(v00, v10, v01, v11, wx, wy float64) float64 {
	top := v00 + (v10-v00)*wx
	bottom := v01 + (v11-v01)*wx
	return top + (bottom-top)*wy
}

// clamp8 浮点分量裁到 0–255 并取整。
func clamp8(v float64) uint8 {
	if v <= 0 {
		return 0
	}
	if v >= 255 {
		return 255
	}
	return uint8(math.Round(v))
}

// DescribeThumbnailSize 解码 JPEG 字节并给出「宽x高」文本（回验用；失败给诊断文本）。
func DescribeThumbnailSize(jpg []byte) string {
	cfg, err := jpeg.DecodeConfig(bytes.NewReader(jpg))
	if err != nil {
		return "回验失败: " + err.Error()
	}
	return fmt.Sprintf("%dx%d", cfg.Width, cfg.Height)
}
