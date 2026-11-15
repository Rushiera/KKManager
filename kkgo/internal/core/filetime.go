package core

import (
	"os"
	"syscall"
	"time"
)

// createdStampFormat 创建时间戳文本格式（UTC，毫秒三位）——对齐 C# Store.StampFormat。
const createdStampFormat = "2006-01-02T15:04:05.000"

// CreatedStampOf 取文件的创建时间戳文本（UTC）——对齐 C# Store.CreatedStampOf。
// 文件不存在或拿不到创建时间时返回空串，调用方按「读不到」处理（不静默当成 0 值）。
// Windows 专用：os.Stat 的 Sys() 在 Windows 上返回 *syscall.Win32FileAttributeData。
func CreatedStampOf(path string) string {
	info, err := os.Stat(path)
	if err != nil {
		return ""
	}
	data, ok := info.Sys().(*syscall.Win32FileAttributeData)
	if !ok {
		return ""
	}
	return time.Unix(0, data.CreationTime.Nanoseconds()).UTC().Format(createdStampFormat)
}
