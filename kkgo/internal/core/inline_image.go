// 卡片内嵌图片（数据区里的 PNG）按偏移 / 长度取图并缩放为 JPEG——对齐 C# WebApp.InlineImage。
package core

import (
	"bytes"
	"image/jpeg"
	"image/png"
	"os"
)

// InlineImageMaxBytes 单次取图的数据上限（超过一律拒绝，不无声截断）。
const InlineImageMaxBytes = 64 * 1024 * 1024

// InlineImageJPEG 按偏移 / 长度从卡片数据区取内嵌 PNG，缩放到宽 width（≤512）后编码 JPEG。
// 返回 (nil, 原因文本) 表示失败；调用方按 {ok:false,error} 回执。
func InlineImageJPEG(path string, offset, size int64, width int) ([]byte, string) {
	if width <= 0 {
		width = 96
	}
	if width > 512 {
		width = 512
	}
	if offset < 0 || size <= 0 {
		return nil, "偏移或长度非法"
	}
	if size > InlineImageMaxBytes {
		return nil, "条目过大（上限 64 MB）"
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, "打开卡片失败：" + err.Error()
	}
	defer f.Close()
	buf := make([]byte, size)
	if _, err = f.ReadAt(buf, offset); err != nil {
		return nil, "读取数据区失败：" + err.Error()
	}
	img, err := png.Decode(bytes.NewReader(buf))
	if err != nil {
		return nil, "不是可解码的 PNG：" + err.Error()
	}
	b := img.Bounds()
	if b.Dx() <= 0 || b.Dy() <= 0 {
		return nil, "图片尺寸非法"
	}
	h := b.Dy() * width / b.Dx()
	if h <= 0 {
		h = 1
	}
	var out bytes.Buffer
	if err = jpeg.Encode(&out, resizeBilinear(img, width, h), &jpeg.Options{Quality: 82}); err != nil {
		return nil, "编码 JPEG 失败：" + err.Error()
	}
	return out.Bytes(), ""
}
