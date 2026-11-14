// 面板侧共用的 core 辅助（G7）——白名单校验 / 图片判定 / 贴图渲染 / 库根归属。
package core

import (
	"fmt"
	"path/filepath"
	"strings"
)

// CompositionHasEntry 判断条目是否在组成档案里（条目预览的白名单校验）。
func CompositionHasEntry(entries, entryPath string) bool {
	want := strings.ToLower(strings.TrimSpace(entryPath))
	if want == "" {
		return false
	}
	for _, line := range strings.Split(entries, "\n") {
		if line == "" {
			continue
		}
		name := line
		if i := strings.IndexByte(line, '\t'); i >= 0 {
			name = line[:i]
		}
		if strings.ToLower(strings.TrimSpace(name)) == want {
			return true
		}
	}
	return false
}

// IsImageEntry 是否为图片条目（预览走原图通道，不做文本解码）。
func IsImageEntry(path string) bool {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png", ".jpg", ".jpeg", ".gif", ".bmp":
		return true
	}
	return false
}

// RenderU3dTexture 从 zipmod 里读 unity3d 条目并渲染指定贴图为 PNG。
// index 为 ListTextures 的下标；maxEdge > 0 时按最长边等比缩小。
func RenderU3dTexture(zipPath, entryPath string, index, maxEdge int) ([]byte, error) {
	data, err := ReadZipEntry(zipPath, entryPath, U3dEntryMaxBytes)
	if err != nil {
		return nil, err
	}
	list, detail := ListTextures(data)
	if detail != "" {
		return nil, fmt.Errorf("%s", detail)
	}
	if index < 0 || index >= len(list) {
		return nil, fmt.Errorf("贴图索引越界：%d（共 %d 张）", index, len(list))
	}
	png, pngDetail := RenderTexturePng(data, list[index].PathId, maxEdge)
	if pngDetail != "" {
		return nil, fmt.Errorf("%s", pngDetail)
	}
	return png, nil
}

// PathInAnyRoot 判断路径是否落在受管库根内（mod 侧 + 卡片侧）。
// 用途：/api/reveal 的路径白名单——越界路径一律拒绝，不打开资源管理器。
func PathInAnyRoot(cfg *RootsConfig, path string) bool {
	if cfg == nil || strings.TrimSpace(path) == "" {
		return false
	}
	norm := strings.ToLower(strings.ReplaceAll(path, "/", "\\"))
	for _, e := range cfg.ModRoots {
		if underRoot(norm, e.Path) {
			return true
		}
	}
	for _, e := range cfg.CardRoots {
		if underRoot(norm, e.Path) {
			return true
		}
	}
	return false
}

// underRoot 路径是否在库根之下（大小写不敏感，带分隔符比较）。
func underRoot(normPath, root string) bool {
	r := strings.ToLower(strings.ReplaceAll(strings.TrimSpace(root), "/", "\\"))
	r = strings.TrimRight(r, "\\")
	if r == "" {
		return false
	}
	return normPath == r || strings.HasPrefix(normPath, r+"\\")
}
