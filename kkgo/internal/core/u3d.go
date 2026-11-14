// unity3d 解析档案的跨库编排——对齐 C# Data/StoreHub.cs §AnalyzeU3d / §ParseU3dTextures。
//
// 分层：本包负责「取最优副本 / 判失效 / 调解析器 / 落库」，store 只做单库读写。
package core

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"kkgo/internal/store"
)

// U3dEntryMaxBytes 读单个 unity3d 条目的上限（对齐 C# U3dEntryMaxBytes = 512 MB）。
const U3dEntryMaxBytes = 536870912

// AnalyzeU3d 读（必要时建立）某 mod 的 unity3d 解析档案——取最优级别且磁盘上存在的副本；
// force=false 时按路径 / 大小 / 修改时间判失效（未变直接复用）。失败返回诊断文本（空串 = 成功）。
func AnalyzeU3d(hub *store.Hub, cfg *RootsConfig, guid, entryPath string, force bool) (*store.ModU3dRow, string) {
	if guid == "" || entryPath == "" {
		return nil, "缺少 guid 或条目路径"
	}
	if !strings.HasSuffix(strings.ToLower(entryPath), ".unity3d") {
		return nil, "该条目不是 unity3d：" + entryPath
	}
	files, err := QueryModFilesHub(hub, cfg, guid)
	if err != nil {
		return nil, err.Error()
	}
	var pick *store.ModFileRec
	for i := range files {
		if fileExists(files[i].FilePath) {
			pick = &files[i]
			break
		}
	}
	if pick == nil {
		if len(files) == 0 {
			return nil, "本地没有该 mod 的文件（库内无副本）"
		}
		return nil, "登记的副本都不在磁盘上（需重扫）"
	}
	info, statErr := os.Stat(pick.FilePath)
	if statErr != nil {
		return nil, "读副本信息失败：" + statErr.Error()
	}
	mtime := info.ModTime().UTC().Format(stampUTC)
	cached, err := hub.Core().LoadU3dRow(guid, entryPath)
	if err != nil {
		return nil, err.Error()
	}
	if !force && cached != nil && strings.EqualFold(cached.FilePath, pick.FilePath) &&
		cached.Size == info.Size() && cached.Mtime == mtime {
		cached.Cached = true
		return cached, ""
	}
	bytes, readErr := ReadZipEntry(pick.FilePath, entryPath, U3dEntryMaxBytes)
	if readErr != nil {
		return nil, "读条目失败：" + readErr.Error()
	}
	texs, parseErr := ListTextures(bytes)
	if parseErr != "" {
		return nil, "解析失败：" + parseErr
	}
	lines := make([]string, 0, len(texs))
	for _, t := range texs {
		lines = append(lines, strconv.FormatInt(t.PathId, 10)+"\t"+t.Name+"\t"+itoaSmall(t.Width)+"\t"+
			itoaSmall(t.Height)+"\t"+itoaSmall(t.Format)+"\t"+itoaSmall(t.DataLength))
	}
	rec := &store.ModU3dRow{
		Guid: guid, EntryPath: entryPath, FilePath: pick.FilePath,
		Size: info.Size(), Mtime: mtime, TextureCount: int64(len(texs)),
		Textures: strings.Join(lines, "\n"), ClassSummary: "", ParsedAt: nowStampUTC(),
	}
	if err := hub.Core().SaveU3dRow(rec); err != nil {
		return nil, "落库失败：" + err.Error()
	}
	return rec, ""
}

// ParseU3dTextures 解析贴图清单文本（每行「pathID \t 名称 \t 宽 \t 高 \t 格式 \t 数据字节」）→ 结构列表。
func ParseU3dTextures(textures string) []Unity3dTexture {
	list := make([]Unity3dTexture, 0, 8)
	if textures == "" {
		return list
	}
	for _, line := range strings.Split(textures, "\n") {
		if line == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if len(parts) < 6 {
			continue
		}
		pathId, _ := strconv.ParseInt(parts[0], 10, 64)
		w, _ := strconv.Atoi(parts[2])
		h, _ := strconv.Atoi(parts[3])
		fmtVal, _ := strconv.Atoi(parts[4])
		size, _ := strconv.Atoi(parts[5])
		list = append(list, Unity3dTexture{
			PathId: pathId, Name: parts[1], Width: w, Height: h, Format: fmtVal, DataLength: size,
		})
	}
	return list
}

// U3dParent 条目所在容器的显示用父路径（诊断用）。
func U3dParent(p string) string { return filepath.Dir(p) }
