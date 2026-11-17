package core

import (
	"encoding/json"
	"fmt"
	"kkgo/internal/store"
	"path/filepath"
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

// LibraryRootOf 把路径归并到库根口径：`<x>\整理` → `<x>`，其余原样（去尾部空白与路径分隔符）。
// 判据：库里 card.root_path / index_source.root_path 只用库根；导入 / 扫描收到子目录时先过这里。
// 规则只实现一处（store.RootKey）——库序号与写入面同一口径，避免两套规则漂移。
func LibraryRootOf(path string) string {
	return store.RootKey(path)
}

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

// ClearLockFlags 清掉外部输入带来的锁定标记——主库身份只由规范按路径认定，不信任请求。
func ClearLockFlags(cfg *RootsConfig) {
	if cfg == nil {
		return
	}
	clearLockList(cfg.ModRoots)
	clearLockList(cfg.CardRoots)
	clearLockList(cfg.PluginRoots)
}

// clearLockList 清掉一组列表里的锁定标记。
func clearLockList(list []RootEntry) {
	for i := range list {
		list[i].Locked = false
	}
}

// restoreMainList 还原一组列表里的锁定条目——以磁盘现状为准（不可删、不可改），被拒的改动出声。
// 返回处理后的列表（append 可能换底层数组，必须回写调用方）。
func restoreMainList(previous, current []RootEntry, label string, warnings *[]string) []RootEntry {
	for _, p := range previous {
		if !p.Locked {
			continue
		}
		index := -1
		for i := range current {
			if strings.EqualFold(strings.TrimSpace(current[i].Path), strings.TrimSpace(p.Path)) {
				index = i
				break
			}
		}
		if index < 0 {
			current = append(current, RootEntry{Tier: p.Tier, Path: p.Path, Recurse: p.Recurse, ReadOnly: p.ReadOnly, Locked: true})
			*warnings = append(*warnings, label+"「"+p.Path+"」不可删除——已按原样保留")
			continue
		}
		c := current[index]
		if c.Tier != p.Tier || c.Recurse != p.Recurse || c.ReadOnly != p.ReadOnly {
			*warnings = append(*warnings, label+"「"+p.Path+"」由规范锁定——本次改动已忽略")
		}
		current[index] = RootEntry{Tier: p.Tier, Path: p.Path, Recurse: p.Recurse, ReadOnly: p.ReadOnly, Locked: true, Note: c.Note}
	}
	return current
}

// ensureCacheSlot 缓存库槽位不可删——请求里丢了缓存库时按磁盘现状补回（路径与勾选归使用者，故只保证存在性），并出声。
func ensureCacheSlot(previous, current []RootEntry, label string, warnings *[]string) []RootEntry {
	for _, cached := range previous {
		if cached.Tier != TierCache || strings.TrimSpace(cached.Path) == "" {
			continue
		}
		found := false
		for _, e := range current {
			if e.Tier == TierCache && strings.TrimSpace(e.Path) != "" {
				found = true
				break
			}
		}
		if !found {
			current = append(current, RootEntry{Tier: cached.Tier, Path: cached.Path, Recurse: cached.Recurse, ReadOnly: cached.ReadOnly, Note: cached.Note})
			*warnings = append(*warnings, label+"「"+cached.Path+"」是缓存库槽位、不可删除——已按原样保留")
		}
		return current
	}
	return current
}

// RestorePresets 还原请求里的预置条目——主库（含卡片主库）：改动忽略、缺失补回；缓存库：只保证槽位存在。
// 返回出声清单（对齐 C# RootsRules.RestorePresets）。
func RestorePresets(previous, current *RootsConfig) []string {
	warnings := make([]string, 0, 4)
	if previous == nil || current == nil {
		return warnings
	}
	current.ModRoots = restoreMainList(previous.ModRoots, current.ModRoots, "mod 预置库根", &warnings)
	current.CardRoots = restoreMainList(previous.CardRoots, current.CardRoots, "卡片预置库根", &warnings)
	current.PluginRoots = restoreMainList(previous.PluginRoots, current.PluginRoots, "插件预置库根", &warnings)
	current.ModRoots = ensureCacheSlot(previous.ModRoots, current.ModRoots, "mod 缓存库", &warnings)
	return warnings
}

// FindRootEntry 按路径在 mod / 卡片库根里找一条（「更新本库」按已保存配置校验用）；找不到返回 nil。
func FindRootEntry(cfg *RootsConfig, path string) *RootEntry {
	if cfg == nil {
		return nil
	}
	key := strings.ToLower(strings.TrimSpace(path))
	if key == "" {
		return nil
	}
	for i := range cfg.ModRoots {
		if strings.ToLower(strings.TrimSpace(cfg.ModRoots[i].Path)) == key {
			return &cfg.ModRoots[i]
		}
	}
	for i := range cfg.CardRoots {
		if strings.ToLower(strings.TrimSpace(cfg.CardRoots[i].Path)) == key {
			return &cfg.CardRoots[i]
		}
	}
	return nil
}

// ---------- kkc 接入（独立形态） ----------
//
// kkc 整理库由 card-sort 链自己管，**不进库根配置**（不在「库根设置」的卡片库 / mod 库里出现）。
// 接入 = 一个独立开关（setting kkc_enabled）；开着时由本组函数在**运行态**注入两条库根，
// 关掉时整个 kkc 不出现在扫描面 / 读面 / 文件夹视图里。
// 判据：任何写回 roots 的路径都必须先 StripKkcRoots——配置里永远看不到 kkc 条目。

// KkcSettingKey kkc 接入开关的 setting 键（"1" = 开 · 缺省 / 其它值 = 关）。
const KkcSettingKey = "kkc_enabled"

// KkcDataDir 返回 kkc 整理库的数据根（主库目录下的 kkc 子目录）；dbPath 为空时返回空串。
func KkcDataDir(dbPath string) string {
	if strings.TrimSpace(dbPath) == "" {
		return ""
	}
	return filepath.Join(filepath.Dir(dbPath), "kkc")
}

// KkcRootEntries 生成 kkc 接入的两条运行态库根：
// 卡片侧 = <数据根>\整理（级别 2 · 含子目录 · 只读）· mod 侧 = <数据根>\整理\02-mod（级别 3 · 含子目录 · 只读）。
// 两者路径都落在 kkc 数据根内——所以任何写回配置的路径都必须先 StripKkcRoots。
func KkcRootEntries(dbPath string) (card RootEntry, mod RootEntry, ok bool) {
	data := KkcDataDir(dbPath)
	if data == "" {
		return RootEntry{}, RootEntry{}, false
	}
	cardRoot := filepath.Join(data, store.KkcOrganizeSubdir)
	return RootEntry{Tier: TierCache, Path: cardRoot, Recurse: true, ReadOnly: true},
		RootEntry{Tier: TierCold, Path: filepath.Join(cardRoot, "02-mod"), Recurse: true, ReadOnly: true},
		true
}

// UnderKkc 判断路径是否落在 kkc 数据根内（含数据根本身与全部子目录）。
func UnderKkc(dbPath, path string) bool {
	data := strings.ToLower(trimPath(KkcDataDir(dbPath)))
	p := strings.ToLower(trimPath(path))
	if data == "" || p == "" {
		return false
	}
	return p == data || strings.HasPrefix(p, data+`\`) || strings.HasPrefix(p, data+"/")
}

// StripKkcRoots 剥掉配置里落在 kkc 数据根内的库根条目（历史残留 / 旧写法 / 运行态注入）——返回剥掉的条数。
func StripKkcRoots(cfg *RootsConfig, dbPath string) int {
	if cfg == nil || KkcDataDir(dbPath) == "" {
		return 0
	}
	before := len(cfg.ModRoots) + len(cfg.CardRoots)
	cfg.ModRoots = dropUnderKkc(cfg.ModRoots, dbPath)
	cfg.CardRoots = dropUnderKkc(cfg.CardRoots, dbPath)
	return before - len(cfg.ModRoots) - len(cfg.CardRoots)
}

// dropUnderKkc 去掉落在 kkc 数据根内的条目（其余保持原样）。
func dropUnderKkc(list []RootEntry, dbPath string) []RootEntry {
	out := make([]RootEntry, 0, len(list))
	for _, e := range list {
		if UnderKkc(dbPath, e.Path) {
			continue
		}
		out = append(out, e)
	}
	return out
}

// InjectKkcRoots 把 kkc 的两条运行态库根追加进配置（只存在内存；写回配置前必须 StripKkcRoots）。
// 返回是否注入（dbPath 为空时不注入）。
func InjectKkcRoots(cfg *RootsConfig, dbPath string) bool {
	card, mod, ok := KkcRootEntries(dbPath)
	if cfg == nil || !ok {
		return false
	}
	if !hasRootPathIn(cfg.CardRoots, card.Path) {
		cfg.CardRoots = append(cfg.CardRoots, card)
	}
	if !hasRootPathIn(cfg.ModRoots, mod.Path) {
		cfg.ModRoots = append(cfg.ModRoots, mod)
	}
	return true
}

// hasRootPathIn 列表里是否存在路径等于 path 的条目（大小写与尾部分隔符不敏感）。
func hasRootPathIn(list []RootEntry, path string) bool {
	want := strings.ToLower(trimPath(path))
	for _, e := range list {
		if strings.ToLower(trimPath(e.Path)) == want {
			return true
		}
	}
	return false
}

// KkcEnabled 读 kkc 接入开关（缺省关）。
func KkcEnabled(hub *store.Hub) bool {
	if hub == nil {
		return false
	}
	v, _, err := hub.Core().GetSetting(KkcSettingKey)
	return err == nil && strings.TrimSpace(v) == "1"
}

// KkcRootsOfDB kkc 数据根与两条运行态库根路径（展示面用——不碰配置）。
func KkcRootsOfDB(dbPath string) (dataRoot, cardRoot, modRoot string) {
	card, mod, ok := KkcRootEntries(dbPath)
	if !ok {
		return "", "", ""
	}
	return KkcDataDir(dbPath), card.Path, mod.Path
}
