package core

import (
	"os"
	"path/filepath"
	"strings"
	"time"

	"kkgo/internal/store"
)

// trimRootSlash 去掉路径尾部的斜杠 / 反斜杠（库根比对用）。
func trimRootSlash(p string) string {
	return strings.TrimRight(strings.TrimSpace(p), `\/`)
}

// IsReadOnlyRoot 判断某个 mod 库根是否只读（无记录时保守视为只读，避免误删源文件）。
// 对齐 C# RootsRules.IsReadOnlyRoot——搬运 / 互换 / 正名的源侧判定唯一实现。
func IsReadOnlyRoot(cfg *RootsConfig, path string) bool {
	if path == "" {
		return true
	}
	if cfg == nil || cfg.ModRoots == nil {
		return true
	}
	norm := trimRootSlash(path)
	for _, r := range cfg.ModRoots {
		if strings.EqualFold(trimRootSlash(r.Path), norm) {
			return r.ReadOnly
		}
	}
	return true
}

// IsReadOnlyCardRoot 判断某个卡片库根是否只读（无记录时保守视为只读）——非卡文件搬运的源侧判定。
// 对齐 C# RootsRules.IsReadOnlyCardRoot。
func IsReadOnlyCardRoot(cfg *RootsConfig, path string) bool {
	if path == "" {
		return true
	}
	if cfg == nil || cfg.CardRoots == nil {
		return true
	}
	norm := trimRootSlash(path)
	for _, r := range cfg.CardRoots {
		if strings.EqualFold(trimRootSlash(r.Path), norm) {
			return r.ReadOnly
		}
	}
	return true
}

// nowStampLocal 当前 UTC 时间戳文本（对齐 C# Store.Now）。
func nowStampLocal() string {
	return time.Now().UTC().Format("2006-01-02T15:04:05.000")
}

// MoveNonCardHub 把一条非卡 / 非 mod 文件搬到 mod 缓存库（游戏不读的那条库根）。
// 返回新路径（空 = 失败）与原因文本——失败时文件不动。对齐 C# StoreHub.MoveNonCard。
func MoveNonCardHub(hub *store.Hub, cfg *RootsConfig, lib int, id int64) (string, string, error) {
	db, err := hub.StoreByLib(lib)
	if err != nil {
		return "", "找不到库 " + itoaSmall(lib), err
	}
	row, err := db.GetNonCardRow(id)
	if err != nil {
		return "", "", err
	}
	if row == nil {
		return "", "登记不存在（可能已被搬走或已核销）", nil
	}
	cache := ModCachePath(cfg)
	if strings.TrimSpace(cache) == "" {
		return "", "未配置 mod 缓存库——先在「库根设置」里设好缓存库", nil
	}
	var readOnly bool
	if row.Side == "mod" {
		readOnly = IsReadOnlyRoot(cfg, row.RootPath)
	} else {
		readOnly = IsReadOnlyCardRoot(cfg, row.RootPath)
	}
	if readOnly {
		return "", "所在库根为只读，不能移动", nil
	}
	if _, statErr := os.Stat(row.FilePath); statErr != nil {
		return "", "文件不存在（已从磁盘消失）", nil
	}
	if mkErr := os.MkdirAll(cache, 0o755); mkErr != nil {
		return "", "缓存库目录不可用：" + mkErr.Error(), nil
	}
	dest := filepath.Join(cache, filepath.Base(row.FilePath))
	if _, statErr := os.Stat(dest); statErr == nil {
		return "", "缓存库已有同名文件：" + filepath.Base(row.FilePath), nil
	}
	if mvErr := os.Rename(row.FilePath, dest); mvErr != nil {
		return "", "文件操作失败：" + mvErr.Error(), nil
	}
	if err = db.MarkNonCardMoved(id, dest, nowStampLocal()); err != nil {
		return "", "", err
	}
	return dest, dest, nil
}

// itoaSmall 小整数转文本（避免为一个库序号引入多余依赖）。
func itoaSmall(v int) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	if neg {
		v = -v
	}
	var buf [20]byte
	i := len(buf)
	for v > 0 {
		i--
		buf[i] = byte('0' + v%10)
		v /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}
