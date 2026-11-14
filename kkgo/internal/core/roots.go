package core

import (
	"encoding/json"
	"fmt"
	"strings"
)

// 级别常量——主库（游戏读取）/ 缓存库（游戏不读）/ 冷冻库（只读归档）。
const (
	// TierMain 主库。
	TierMain = 1
	// TierCache 缓存库。
	TierCache = 2
	// TierCold 冷冻库。
	TierCold = 3
)

// RootEntry 库根配置项——一个受管目录及其级别与扫描规则（对齐 C# Core/Roots.cs RootEntry）。
type RootEntry struct {
	// Tier 级别：1=主库 · 2=缓存库 · 3=冷冻库。
	Tier int `json:"tier"`
	// Path 目录绝对路径。
	Path string `json:"path"`
	// Recurse 是否包含子目录。
	Recurse bool `json:"recurse"`
	// ReadOnly 是否只读（冷冻库——搬运时用复制，源文件保留）。
	ReadOnly bool `json:"readOnly"`
	// Locked 是否为预置主库条目（锁定）——路径 / 含子目录 / 只读 / 存在性由规范裁决。
	Locked bool `json:"locked"`
	// Offline 是否离线——离线库不参与扫描。
	Offline bool `json:"offline"`
	// Note 离线备注（描述东西放哪了）。
	Note string `json:"note"`
	// ReplacedFrom 请求面字段（不落盘但随序列化占位）——保存配置时判定「在原行改地址」。
	ReplacedFrom string `json:"replacedFrom"`
}

// RootsConfig 库根配置——游戏根 + mod 库 / 卡片库 / 插件库三组（对齐 C# RootsConfig）。
type RootsConfig struct {
	// GameRoot 游戏根地址（空 = 未设置）。
	GameRoot string `json:"gameRoot"`
	// ModRoots mod 库根。
	ModRoots []RootEntry `json:"modRoots"`
	// CardRoots 卡片库根。
	CardRoots []RootEntry `json:"cardRoots"`
	// PluginRoots 插件库根（只有预置锁定一条）。
	PluginRoots []RootEntry `json:"pluginRoots"`
	// Seeded 是否已预置过推荐库根。
	Seeded bool `json:"seeded"`
}

// 库根规则常量——推荐条目与派生路径的唯一来源。
const (
	// DefaultGameRoot 默认游戏根（未设置时的预填值）。
	DefaultGameRoot = `D:\SteamLibrary\GameGeneral\Koikatu`
	// ModMainSub mod 主库相对子路径。
	ModMainSub = "mods"
	// ModCacheSub mod 缓存库相对子路径。
	ModCacheSub = "mods_cache"
	// CardFemaleSub 人物卡主库相对子路径。
	CardFemaleSub = `UserData\chara\female`
	// CardCoordinateSub 服装卡主库相对子路径。
	CardCoordinateSub = `UserData\coordinate`
	// CardStudioSub 场景卡主库相对子路径。
	CardStudioSub = `UserData\Studio`
	// PluginSub 插件库相对子路径。
	PluginSub = "BepInEx"
)

// TierName 级别名（对齐 C# Tier.Name）。
func TierName(tier int) string {
	switch tier {
	case TierMain:
		return "主库"
	case TierCache:
		return "缓存库"
	case TierCold:
		return "冷冻库"
	default:
		return "级别" + itoa(int64(tier))
	}
}

// trimPath 去空白与尾部斜杠（C# Trim().TrimEnd('\\','/')）。
func trimPath(p string) string {
	return strings.TrimRight(strings.TrimSpace(p), `\/`)
}

// NormalizeGameRoot 规范化游戏根地址（去空白与尾部斜杠；空则回落默认地址）。
func NormalizeGameRoot(path string) string {
	if strings.TrimSpace(path) == "" {
		return DefaultGameRoot
	}
	return trimPath(path)
}

// UnderGameRoot 拼出游戏根下的一条路径（子路径用反斜杠分段）。
func UnderGameRoot(gameRoot, sub string) string {
	return NormalizeGameRoot(gameRoot) + `\` + sub
}

// recommendRoot 构造一条推荐条目。
func recommendRoot(tier int, path string, locked bool) RootEntry {
	return RootEntry{Tier: tier, Path: path, Recurse: true, Locked: locked}
}

// RecommendModRoots 推荐 mod 库根——mods（级别 1 锁定）+ mods_cache（级别 2 槽位）。
func RecommendModRoots(gameRoot string) []RootEntry {
	return []RootEntry{
		recommendRoot(TierMain, UnderGameRoot(gameRoot, ModMainSub), true),
		recommendRoot(TierCache, UnderGameRoot(gameRoot, ModCacheSub), false),
	}
}

// RecommendCardRoots 推荐卡片库根——female / coordinate / Studio（均级别 1 · 含子目录 · 锁定）。
func RecommendCardRoots(gameRoot string) []RootEntry {
	return []RootEntry{
		recommendRoot(TierMain, UnderGameRoot(gameRoot, CardFemaleSub), true),
		recommendRoot(TierMain, UnderGameRoot(gameRoot, CardCoordinateSub), true),
		recommendRoot(TierMain, UnderGameRoot(gameRoot, CardStudioSub), true),
	}
}

// RecommendPluginRoots 推荐插件库根——BepInEx（级别 1 · 预置锁定；不预置只读）。
func RecommendPluginRoots(gameRoot string) []RootEntry {
	return []RootEntry{recommendRoot(TierMain, UnderGameRoot(gameRoot, PluginSub), true)}
}

// cleanRoots 清洗使用者列表——去空路径、级别夹在合法区间、同路径去重（保留首条）。
func cleanRoots(source []RootEntry, minTier, maxTier int) []RootEntry {
	list := make([]RootEntry, 0, len(source))
	seen := make(map[string]bool, len(source))
	for _, e := range source {
		path := strings.TrimSpace(e.Path)
		if path == "" || seen[strings.ToLower(path)] {
			continue
		}
		seen[strings.ToLower(path)] = true
		tier := e.Tier
		if tier < minTier {
			tier = minTier
		}
		if tier > maxTier {
			tier = maxTier
		}
		list = append(list, RootEntry{
			Tier: tier, Path: path, Recurse: e.Recurse, ReadOnly: e.ReadOnly,
			Locked: e.Locked, Offline: e.Offline, Note: e.Note,
		})
	}
	return list
}

// lockPresets 按预置值认定并压回锁定条目——路径与预置一致者锁定，形态一律取预置值。
func lockPresets(list []RootEntry, recommended []RootEntry) {
	for i := range list {
		matched := false
		for _, r := range recommended {
			if !r.Locked || !strings.EqualFold(list[i].Path, r.Path) {
				continue
			}
			list[i].Locked = true
			list[i].Tier = r.Tier
			list[i].Recurse = r.Recurse
			list[i].ReadOnly = r.ReadOnly
			matched = true
			break
		}
		if list[i].Locked && !matched {
			// 预置路径已变的存量锁定条目——保留锁定与级别，形态仍压回「含子目录 · 非只读」
			list[i].Recurse = true
			list[i].ReadOnly = false
		}
	}
}

// Normalize 规范化库根配置——首次填推荐值；之后以使用者列表为准；预置条目压回规范形态。
func Normalize(cfg *RootsConfig) {
	if cfg == nil {
		return
	}
	cfg.GameRoot = trimPath(cfg.GameRoot)
	if cfg.ModRoots == nil {
		cfg.ModRoots = []RootEntry{}
	}
	if cfg.CardRoots == nil {
		cfg.CardRoots = []RootEntry{}
	}
	if cfg.PluginRoots == nil {
		cfg.PluginRoots = []RootEntry{}
	}
	if !cfg.Seeded {
		if len(cfg.ModRoots) == 0 {
			cfg.ModRoots = RecommendModRoots(cfg.GameRoot)
		}
		if len(cfg.CardRoots) == 0 {
			cfg.CardRoots = RecommendCardRoots(cfg.GameRoot)
		}
		cfg.Seeded = true
	}
	if len(cfg.PluginRoots) == 0 {
		cfg.PluginRoots = RecommendPluginRoots(cfg.GameRoot)
	}
	cfg.ModRoots = cleanRoots(cfg.ModRoots, TierMain, TierCold)
	cfg.CardRoots = cleanRoots(cfg.CardRoots, TierMain, TierCache)
	cfg.PluginRoots = cleanRoots(cfg.PluginRoots, TierMain, TierMain)
	lockPresets(cfg.ModRoots, RecommendModRoots(cfg.GameRoot))
	lockPresets(cfg.CardRoots, RecommendCardRoots(cfg.GameRoot))
	lockPresets(cfg.PluginRoots, RecommendPluginRoots(cfg.GameRoot))
}

// ApplyGameRootChange 游戏根变更的预置条目处置——锁定条目按新根重派生；mod 缓存库槽位未被改过则跟随。
// 返回出声清单；cacheMoved 表示槽位是否被移动。
func ApplyGameRootChange(cfg *RootsConfig, previousGameRoot string) (warnings []string, cacheMoved bool) {
	if cfg == nil {
		return nil, false
	}
	if cfg.ModRoots == nil {
		cfg.ModRoots = []RootEntry{}
	}
	if cfg.CardRoots == nil {
		cfg.CardRoots = []RootEntry{}
	}
	if cfg.PluginRoots == nil {
		cfg.PluginRoots = []RootEntry{}
	}
	// [段1] 锁定条目按新根重派生——先清旧根的，再补新根的
	cfg.ModRoots = dropLocked(cfg.ModRoots)
	cfg.CardRoots = dropLocked(cfg.CardRoots)
	cfg.PluginRoots = dropLocked(cfg.PluginRoots)
	for _, r := range RecommendModRoots(cfg.GameRoot) {
		if r.Locked {
			cfg.ModRoots = append(cfg.ModRoots, r)
		}
	}
	cfg.CardRoots = append(cfg.CardRoots, RecommendCardRoots(cfg.GameRoot)...)
	cfg.PluginRoots = append(cfg.PluginRoots, RecommendPluginRoots(cfg.GameRoot)...)
	// [段2] 缓存库槽位跟随——路径仍等于旧根派生值即「从未被改过」
	oldCache := UnderGameRoot(previousGameRoot, ModCacheSub)
	newCache := UnderGameRoot(cfg.GameRoot, ModCacheSub)
	for i := range cfg.ModRoots {
		if cfg.ModRoots[i].Tier != TierCache || strings.TrimSpace(cfg.ModRoots[i].Path) == "" {
			continue
		}
		if !strings.EqualFold(strings.TrimSpace(cfg.ModRoots[i].Path), oldCache) {
			continue
		}
		cfg.ModRoots[i].Path = newCache
		cacheMoved = true
		warnings = append(warnings, "mod 缓存库槽位随游戏根移到「"+newCache+"」（槽位未被改过时跟随新根）")
		break
	}
	return warnings, cacheMoved
}

// dropLocked 去掉锁定条目（其余保持原样）。
func dropLocked(list []RootEntry) []RootEntry {
	out := make([]RootEntry, 0, len(list))
	for _, e := range list {
		if !e.Locked {
			out = append(out, e)
		}
	}
	return out
}

// ModRootsOrdered 按级别升序排序后的 mod 根（同级别保持配置顺序）。
func (c *RootsConfig) ModRootsOrdered() []RootEntry { return ordered(c.ModRoots) }

// CardRootsOrdered 按级别升序排序后的卡片根（同级别保持配置顺序）。
func (c *RootsConfig) CardRootsOrdered() []RootEntry { return ordered(c.CardRoots) }

// ordered 稳定按级别升序（路径为空的条目剔除）。
func ordered(list []RootEntry) []RootEntry {
	out := make([]RootEntry, 0, len(list))
	for tier := TierMain; tier <= TierCold; tier++ {
		for _, e := range list {
			if e.Tier == tier && strings.TrimSpace(e.Path) != "" {
				out = append(out, e)
			}
		}
	}
	for _, e := range list {
		if (e.Tier < TierMain || e.Tier > TierCold) && strings.TrimSpace(e.Path) != "" {
			out = append(out, e)
		}
	}
	return out
}

// ModCachePath 取出配置里的 mod 缓存库路径（无则空串）。
func ModCachePath(cfg *RootsConfig) string {
	if cfg == nil {
		return ""
	}
	for _, e := range cfg.ModRoots {
		if e.Tier == TierCache && strings.TrimSpace(e.Path) != "" {
			return e.Path
		}
	}
	return ""
}

// ParseRootsConfig 由设置文本还原库根配置（空 / 非法返回空配置；非法出声，不静默）。
func ParseRootsConfig(text string) *RootsConfig {
	cfg := &RootsConfig{}
	if strings.TrimSpace(text) == "" {
		return cfg
	}
	if err := json.Unmarshal([]byte(text), cfg); err != nil {
		fmt.Println("[配置] 库根配置解析失败（按空配置处理）：" + err.Error())
		return &RootsConfig{}
	}
	if cfg.ModRoots == nil {
		cfg.ModRoots = []RootEntry{}
	}
	if cfg.CardRoots == nil {
		cfg.CardRoots = []RootEntry{}
	}
	if cfg.PluginRoots == nil {
		cfg.PluginRoots = []RootEntry{}
	}
	return cfg
}

// RootsConfigJSON 序列化库根配置——对齐 C# System.Text.Json（缩进 2 空格 · CRLF 换行 · 属性序）。
func RootsConfigJSON(cfg *RootsConfig) (string, error) {
	raw, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return "", err
	}
	return strings.ReplaceAll(string(raw), "\n", "\r\n"), nil
}
