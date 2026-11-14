// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：文件内容哈希——重复副本内容比对的判据（只读，不改文件、不落盘内容）。
// 语义对齐 KKManager/Core/FileHash.cs：
//   - 算法固定 MD5，输出小写十六进制；
//   - 流式读（1 MB 缓冲），峰值内存 = 缓冲大小；
//   - 读不到时返回错误（不静默返回空串）。
package core

import (
	"crypto/md5"
	"encoding/hex"
	"fmt"
	"io"
	"os"
)

// fileHashAlgo 哈希算法名（档案与面板展示用）。
const fileHashAlgo = "MD5"

// hashBufferSize 读缓冲字节数（大 zipmod 不整读进内存）。
const hashBufferSize = 1024 * 1024

// FileHashAlgorithm 哈希算法名（对齐 C# FileHash.AlgoName）。
func FileHashAlgorithm() string { return fileHashAlgo }

// FileMd5 计算文件的 MD5（小写十六进制）；读不到时报错给出原因（不静默）。
func FileMd5(path string) (string, error) {
	if path == "" {
		return "", fmt.Errorf("路径为空")
	}
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := md5.New()
	buf := make([]byte, hashBufferSize)
	for {
		n, rerr := f.Read(buf)
		if n > 0 {
			h.Write(buf[:n])
		}
		if rerr == io.EOF {
			break
		}
		if rerr != nil {
			return "", rerr
		}
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
