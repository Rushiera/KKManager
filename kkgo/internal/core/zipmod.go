// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：zipmod 容器解析（manifest.xml → ModInfo），语义逐条对齐
// KKManager/Core/ZipModReader.cs——错误分类 / 字段读取 / 文件名=guid 判据。
package core

import (
	"archive/zip"
	"encoding/xml"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// ModErrorKind — zipmod 解析结果分类（对齐 C# ModErrorKind）：
// 扫描侧据此区分「不是 mod 的文件」（静默跳过）与「坏 mod」（出声）。
type ModErrorKind int

const (
	// ModErrNone 解析成功。
	ModErrNone ModErrorKind = 0
	// ModErrNotContainer 打不开为 zip 容器——不是 mod 文件。
	ModErrNotContainer ModErrorKind = 1
	// ModErrNoManifest 是 zip 容器但根部没有 manifest.xml——不是 zipmod。
	ModErrNoManifest ModErrorKind = 2
	// ModErrBadManifest 有 manifest.xml 但 XML 解析失败——坏 mod。
	ModErrBadManifest ModErrorKind = 3
	// ModErrNoGuid manifest 缺 guid——坏 mod。
	ModErrNoGuid ModErrorKind = 4
	// ModErrIO 文件本身读不到——出声。
	ModErrIO ModErrorKind = 5
)

// ModInfo — zipmod 元数据（取自容器根部 manifest.xml）。
type ModInfo struct {
	// Guid manifest guid——与文件名无关的稳定标识（关联主键）。
	Guid string
	// Name manifest name。
	Name string
	// Version manifest version。
	Version string
	// Author manifest author。
	Author string
	// Website manifest website。
	Website string
	// Description manifest description。
	Description string
	// SchemaVer manifest schema-ver 属性。
	SchemaVer string
	// FilePath zipmod 文件绝对路径。
	FilePath string
	// FileName zipmod 文件名（含扩展名）——仅供人定位。
	FileName string
	// Size 文件字节数。
	Size int64
	// EntryCount 容器内条目数。
	EntryCount int
	// Error 解析诊断——成功为空。
	Error string
	// ErrorKind 解析结果分类。
	ErrorKind ModErrorKind
}

// FileNameMatchesGuid 文件名（去扩展名）与 guid 是否一致。
func (m *ModInfo) FileNameMatchesGuid() bool {
	if m.Guid == "" || m.FileName == "" {
		return false
	}
	stem := strings.TrimSuffix(m.FileName, filepath.Ext(m.FileName))
	return stem == m.Guid
}

// ZipEntryInfo — 容器内一个条目（只读中央目录：路径 / 原始大小 / 压缩大小）。
type ZipEntryInfo struct {
	Path       string
	Size       int64
	Compressed int64
}

// ParseZipMod 解析一个 zipmod（失败时返回带 Error / ErrorKind 的 ModInfo，不抛异常）。
func ParseZipMod(path string) *ModInfo {
	info := &ModInfo{FilePath: path}
	fi, err := os.Stat(path)
	if err != nil {
		info.FileName = filepath.Base(path)
		info.ErrorKind = ModErrIO
		info.Error = "文件不存在"
		return info
	}
	info.FileName = fi.Name()
	info.Size = fi.Size()

	zr, err := zip.OpenReader(path)
	if err != nil {
		// 打不开 = 不是 zip 容器（扫描侧按「非 mod 文件」跳过，不算失败）
		info.ErrorKind = ModErrNotContainer
		info.Error = "不是 zip 容器：" + err.Error()
		return info
	}
	defer zr.Close()

	info.EntryCount = len(zr.File)
	var entry *zip.File
	for _, f := range zr.File {
		if f.Name == "manifest.xml" {
			entry = f
			break
		}
	}
	if entry == nil {
		info.ErrorKind = ModErrNoManifest
		info.Error = "容器内无 manifest.xml"
		return info
	}
	rc, err := entry.Open()
	if err != nil {
		info.ErrorKind = ModErrIO
		info.Error = "读取 manifest 失败: " + err.Error()
		return info
	}
	data, err := io.ReadAll(rc)
	rc.Close()
	if err != nil {
		info.ErrorKind = ModErrIO
		info.Error = "读取 manifest 失败: " + err.Error()
		return info
	}
	info.Error = parseManifest(string(data), info)
	return info
}

// ListZipEntries 读容器内全部条目清单（只读中央目录，不解包）。
func ListZipEntries(path string) ([]ZipEntryInfo, error) {
	zr, err := zip.OpenReader(path)
	if err != nil {
		return nil, err
	}
	defer zr.Close()
	list := make([]ZipEntryInfo, 0, len(zr.File))
	for _, f := range zr.File {
		list = append(list, ZipEntryInfo{
			Path:       f.Name,
			Size:       int64(f.UncompressedSize64),
			Compressed: int64(f.CompressedSize64),
		})
	}
	return list, nil
}

// ReadZipEntry 取容器内单个条目的字节（按路径精确匹配；maxBytes>0 时超限拒绝）。
func ReadZipEntry(path, entryPath string, maxBytes int64) ([]byte, error) {
	zr, err := zip.OpenReader(path)
	if err != nil {
		return nil, err
	}
	defer zr.Close()
	for _, f := range zr.File {
		if f.Name != entryPath {
			continue
		}
		if maxBytes > 0 && int64(f.UncompressedSize64) > maxBytes {
			return nil, errEntryTooLarge(int64(f.UncompressedSize64), maxBytes)
		}
		rc, err := f.Open()
		if err != nil {
			return nil, err
		}
		defer rc.Close()
		return io.ReadAll(rc)
	}
	return nil, errNoEntry(entryPath)
}

// parseManifest 解析 manifest 文本——容忍注释 / BOM / 字段缺失。
// 成功返回空串；失败返回诊断并置 ErrorKind。
// 语义对齐 C#：XmlDocument + XPath //guid 等（取文档中首个同名元素）。
func parseManifest(text string, info *ModInfo) string {
	text = strings.TrimPrefix(text, "\ufeff")
	dec := xml.NewDecoder(strings.NewReader(text))
	fields := map[string]*string{
		"guid":        &info.Guid,
		"name":        &info.Name,
		"version":     &info.Version,
		"author":      &info.Author,
		"website":     &info.Website,
		"description": &info.Description,
	}
	seenRoot := false
	for {
		tok, err := dec.Token()
		if err == io.EOF {
			break
		}
		if err != nil {
			info.ErrorKind = ModErrBadManifest
			return "manifest XML 解析失败: " + err.Error()
		}
		se, ok := tok.(xml.StartElement)
		if !ok {
			continue
		}
		if !seenRoot {
			seenRoot = true
			for _, a := range se.Attr {
				if a.Name.Local == "schema-ver" {
					info.SchemaVer = a.Value
				}
			}
		}
		target, isField := fields[se.Name.Local]
		if !isField {
			continue
		}
		var s string
		if err := dec.DecodeElement(&s, &se); err != nil {
			info.ErrorKind = ModErrBadManifest
			return "manifest XML 解析失败: " + err.Error()
		}
		if *target == "" {
			*target = s
		}
	}
	if info.Guid == "" {
		info.ErrorKind = ModErrNoGuid
		return "manifest 缺 guid"
	}
	return ""
}

type zipModErr string

func (e zipModErr) Error() string { return string(e) }

func errNoEntry(p string) error { return zipModErr("容器内没有该条目：" + p) }

func errEntryTooLarge(size, max int64) error {
	return zipModErr("条目过大（" + itoa(size) + " 字节，上限 " + itoa(max) + "）")
}

func itoa(v int64) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	if neg {
		v = -v
	}
	var b [24]byte
	i := len(b)
	for v > 0 {
		i--
		b[i] = byte('0' + v%10)
		v /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}
