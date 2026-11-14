package core

import (
	"crypto/rand"
	"errors"
	"io"
	"os"
	"time"
)

// timeNowLocal 本地时间（对齐 C# DateTime.Now——非 UTC）。
func timeNowLocal() time.Time { return time.Now() }

// randRead 填随机字节（失败时退化为时间派生，不 panic）。
func randRead(buf []byte) {
	if _, err := rand.Read(buf); err != nil {
		now := time.Now().UnixNano()
		for i := range buf {
			buf[i] = byte(now >> (uint(i%8) * 8))
			now = now*6364136223846793005 + 1442695040888963407
		}
	}
}

// moveFileRename 同盘改名搬运（目标已存在时报错，不覆盖）。
// 跨盘时先复制再删源（对齐 C# File.Move 的语义）。
func moveFileRename(src, dest string) error {
	if fileExists(dest) {
		return errors.New("目标已存在：" + dest)
	}
	if err := os.Rename(src, dest); err == nil {
		return nil
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	if _, err = io.Copy(out, in); err != nil {
		out.Close()
		os.Remove(dest)
		return err
	}
	if err = out.Close(); err != nil {
		os.Remove(dest)
		return err
	}
	return os.Remove(src)
}
