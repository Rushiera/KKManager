// 组成档案（mod 容器条目清单 + 文本条目内容）——从 main 包下沉到 core。
//
// 对齐 C# StoreHub.AnalyzeComposition：取该 guid 最优级别且磁盘上真实存在的副本；
// force=false 时按路径 / 大小 / 修改时间判失效（未变直接复用）；force=true 强制重读容器。
// 分层：本函数不打印（库包不写 stdout），诊断交给调用方。
package core

import (
	"fmt"
	"os"
	"strconv"
	"strings"

	"kkgo/internal/store"
)

// TextEntryMaxBytes 文本类条目入档的字节上限（超过则不入档并出声——不静默丢内容）。
const TextEntryMaxBytes = 4 * 1024 * 1024

// AnalyzeCompositionHub 读（必要时建立）某 mod 的组成档案。
// 返回的记录 Cached=true 表示命中已有档案、未重读容器。
func AnalyzeCompositionHub(hub *store.Hub, cfg *RootsConfig, guid string, force bool, log func(string)) (*store.CompositionRow, error) {
	if guid == "" {
		return nil, fmt.Errorf("缺少 guid")
	}
	if log == nil {
		log = func(string) {}
	}
	files, err := QueryModFilesHub(hub, cfg, guid)
	if err != nil {
		return nil, err
	}
	var pick *store.ModFileRec
	for i := range files {
		if _, statErr := os.Stat(files[i].FilePath); statErr == nil {
			pick = &files[i]
			break
		}
	}
	if pick == nil {
		if len(files) == 0 {
			return nil, fmt.Errorf("本地没有该 mod 的文件（库内无副本）")
		}
		return nil, fmt.Errorf("登记的副本都不在磁盘上（需重扫）")
	}
	info, err := os.Stat(pick.FilePath)
	if err != nil {
		return nil, err
	}
	mtime := info.ModTime().UTC().Format(stampUTC)
	size := info.Size()
	// [段1] 非强制 + 已有档案且文件未变 → 直接复用（不重读容器）；档案缺文本内容而容器有文本条目时视为陈旧，重建一次
	cached, err := hub.Core().LoadComposition(guid)
	if err != nil {
		return nil, err
	}
	staleTexts := cached != nil && cached.Texts == "" && HasTextEntry(cached.Entries)
	if !force && cached != nil && !staleTexts &&
		strings.EqualFold(cached.FilePath, pick.FilePath) &&
		cached.Size == size && cached.Mtime == mtime {
		cached.Cached = true
		return cached, nil
	}
	// [段2] 读容器中央目录 → 建档
	entries, err := ListZipEntries(pick.FilePath)
	if err != nil {
		return nil, fmt.Errorf("读容器失败：%s", err.Error())
	}
	var totalSize, totalCompressed int64
	lines := make([]string, 0, len(entries))
	for _, e := range entries {
		totalSize += e.Size
		totalCompressed += e.Compressed
		lines = append(lines, e.Path+"\t"+strconv.FormatInt(e.Size, 10)+"\t"+strconv.FormatInt(e.Compressed, 10))
	}
	// [段3] 顺带捞文本类条目内容（csv / xml / txt…）——悬停预览用；读不到出声，不静默丢
	texts := make([]string, 0)
	for _, e := range entries {
		if !IsTextEntry(e.Path) {
			continue
		}
		data, readErr := ReadZipEntry(pick.FilePath, e.Path, TextEntryMaxBytes)
		if readErr != nil {
			log("[组成] 文本条目未入档：" + e.Path + " —— " + readErr.Error())
			continue
		}
		decoded := DecodeText(data)
		if decoded.Encoding == "gbk-unavailable" {
			log("[组成] GBK 编码不可用——" + e.Path + " 按 UTF-8 宽松解码")
		}
		texts = append(texts, "\u0001"+e.Path+"\t"+ClampTextEntry(decoded.Text))
	}
	rec := &store.CompositionRow{
		Guid:            guid,
		FilePath:        pick.FilePath,
		FileName:        pick.FileName,
		Size:            size,
		Mtime:           mtime,
		EntryCount:      int64(len(entries)),
		TotalSize:       totalSize,
		TotalCompressed: totalCompressed,
		Entries:         strings.Join(lines, "\n"),
		Texts:           strings.Join(texts, "\n"),
		AnalyzedAt:      nowStampUTC(),
	}
	if err = hub.Core().SaveComposition(rec); err != nil {
		return nil, err
	}
	log("[组成] 建档：" + guid + " · " + strconv.Itoa(len(entries)) + " 条目 · 文本 " + strconv.Itoa(len(texts)) + " 份 · " + pick.FileName)
	return rec, nil
}
