// 卡片整理（card-sort 全族）——对齐 C# Core/CardOrganizer.cs：
// 规则库（Dir / Keyword / Chara / Author）· 扫源目录判类型与角色名 · 判作品与作者 ·
// 出归位计划（重名跳过 / 改名）· 布局校验 · 计划 TSV 读写 · 执行搬运 · 引用表导出。
//
// 分层：纯内存 + 只读文件（不碰库）——写库的 card_rule / card_move / card_delete 由调用方（main）处理。
package core

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// CardRuleKind 卡片整理规则的匹配面（对齐 C# CardRuleKind）。
type CardRuleKind int

const (
	// RuleDir 源目录（相对源根第一段）→ 作品。
	RuleDir CardRuleKind = iota
	// RuleKeyword 关键词（角色名 + 相对路径）→ 作品。
	RuleKeyword
	// RuleChara 角色名精确匹配 → 作品（从既有索引学出来的规则用此型，避免子串误伤）。
	RuleChara
	// RuleAuthor 源目录 → 作者。
	RuleAuthor
)

// RuleKindName 规则类型的短名（TSV 列值）。
func RuleKindName(k CardRuleKind) string {
	switch k {
	case RuleDir:
		return "dir"
	case RuleKeyword:
		return "kw"
	case RuleChara:
		return "chara"
	case RuleAuthor:
		return "author"
	}
	return "kw"
}

// CardRule 一条卡片整理规则（对齐 C# CardRule）。
type CardRule struct {
	// Kind 匹配面。
	Kind CardRuleKind
	// Match 匹配串（Dir / Author 为目录名；Keyword 为关键词）。
	Match string
	// Value 命中后填的值（作品名或作者名）。
	Value string
	// Origin 来源：builtin / learn / manual。
	Origin string
	// Votes 学到此规则时的支撑卡片数。
	Votes int
	// Works 该 match 在学习源里出现过的作品数。
	Works int
	// Enabled 是否启用（DB 停用不删）。
	Enabled bool
}

// CardPlanItem 卡片整理计划的一行（对齐 C# CardPlanItem）。
type CardPlanItem struct {
	// SrcPath 源文件绝对路径。
	SrcPath string
	// Kind 归类（人物卡 / 服装卡 / 场景卡 / mod / 其它）。
	Kind string
	// CardType 卡类型标记（非卡为空）。
	CardType string
	// CharaName 游戏内角色名。
	CharaName string
	// Work 作品。
	Work string
	// Author 作者。
	Author string
	// SrcRoot 来源（源根目录名 = 召回键）。
	SrcRoot string
	// SrcRel 相对源根的路径。
	SrcRel string
	// DestPath 归位后的目标绝对路径。
	DestPath string
	// Size 文件字节数。
	Size int64
	// Note 备注（改名 / 跳过原因）。
	Note string
	// Move 是否搬运。
	Move bool
	// State 状态：pending / moved / skipped / conflict / failed。
	State string
}

// WorkCategory 作品的大类 / 子类。
type WorkCategory struct {
	// Cat 大类（游戏 / 动画 / 其它 / 待确认）。
	Cat string
	// Sub 子类（galgame / 手游 / 单机 / 番剧 / 剧场版 / VTuber）。
	Sub string
}

// CardPlan 卡片整理计划（对齐 C# CardPlan）。
type CardPlan struct {
	// DestRoot 目标根（整理库根）。
	DestRoot string
	// RulesPath 规则文件路径（空 = 只用内置规则）。
	RulesPath string
	// CreatedAt 计划生成时刻。
	CreatedAt string
	// Items 全部条目。
	Items []CardPlanItem
}

// Count 统计某状态的条目数。
func (p *CardPlan) Count(state string) int {
	n := 0
	for _, it := range p.Items {
		if it.State == state {
			n++
		}
	}
	return n
}

// 归类常量（对齐 C# CardOrganizer 常量）。
const (
	// KindChara 人物卡。
	KindChara = "人物卡"
	// KindClothes 服装卡。
	KindClothes = "服装卡"
	// KindScene 场景卡。
	KindScene = "场景卡"
	// KindMod 归类：mod 文件。
	KindMod = "mod"
	// KindOther 归类：其它文件。
	KindOther = "其它"
	// WorkPending 作品判不出时的占位值。
	WorkPending = "待确认"
	// EroMakeCardType EroMakeChara 卡类型标记。
	EroMakeCardType = "【EroMakeChara】"
	// EroMakeFolder EroMakeChara 目标目录。
	EroMakeFolder = "06-EroMakeChara"
	// CardStatePending 状态：待搬。
	CardStatePending = "pending"
	// CardStateMoved 状态：已就位。
	CardStateMoved = "moved"
	// CardStateSkipped 状态：跳过。
	CardStateSkipped = "skipped"
	// CardStateConflict 状态：冲突。
	CardStateConflict = "conflict"
	// CardStateFailed 状态：失败。
	CardStateFailed = "failed"
)

// BuiltinRules 内置规则——Koikatsu 卡片领域的默认判据表。
func BuiltinRules() []CardRule {
	rules := make([]CardRule, 0, 48)
	addDir := func(match, value string) {
		rules = append(rules, CardRule{Kind: RuleDir, Match: match, Value: value, Origin: "builtin", Enabled: true})
	}
	addAuthor := func(match, value string) {
		rules = append(rules, CardRule{Kind: RuleAuthor, Match: match, Value: value, Origin: "builtin", Enabled: true})
	}
	addKw := func(match, value string) {
		rules = append(rules, CardRule{Kind: RuleKeyword, Match: match, Value: value, Origin: "builtin", Enabled: true})
	}

	addDir("genshin_impact", "原神")
	addDir("honkai_impact", "崩坏：星穹铁道")
	addDir("girls_frontline", "少女前线")
	addDir("princessconnect", "公主连结")
	addDir("Umas!", "赛马娘")
	addDir("vtuber", "VTuber")
	addDir("colopl_rune_story", "白猫Project")
	addDir("itai_no_wa_iya_nano_de_bougyoryoku_ni_kyokufuri_shitai_to_omoimasu", "因为太怕痛就全点防御力了")
	addDir("爱MAS", "偶像大师")
	addDir("collection：ccccp_oc", "原创OC")

	addAuthor("method", "method")
	addAuthor("MTkano", "MTkano")
	addAuthor("JohnDoe#1519", "JohnDoe#1519")
	addAuthor("collection：ccccp_oc", "ccccp")

	addKw("Nekopara", "Nekopara")
	addKw("Sanoba Witch", "Sanoba Witch")
	addKw("恋爱决胜战", "恋爱决胜战")
	addKw("明日方舟", "明日方舟")
	addKw("Arknights", "明日方舟")
	addKw("suzuran", "明日方舟")
	addKw("碧蓝航线", "碧蓝航线")
	addKw("Azur Lane", "碧蓝航线")
	addKw("少女前线", "少女前线")
	addKw("原神", "原神")
	addKw("崩坏", "崩坏：星穹铁道")
	addKw("鸣潮", "鸣潮")
	addKw("Wuthering", "鸣潮")
	addKw("蔚蓝档案", "蔚蓝档案")
	addKw("Blue Archive", "蔚蓝档案")
	addKw("赛马娘", "赛马娘")
	addKw("ウマ娘", "赛马娘")
	addKw("偶像大师", "偶像大师")
	addKw("デレマス", "偶像大师")
	addKw("公主连结", "公主连结")
	addKw("プリコネ", "公主连结")
	addKw("Fate", "Fate")
	addKw("舰これ", "舰队Collection")
	addKw("艦これ", "舰队Collection")
	addKw("VTuber", "VTuber")
	addKw("ホロライブ", "Hololive")
	addKw("Hololive", "Hololive")
	addKw("にじさんじ", "NIJISANJI")
	return rules
}

// LoadCardRules 读外部规则文件——TSV 列（类型 / 匹配串 / 值 / 来源 / 票数 / 作品数）。
// 文件不存在返回空表并出声（不静默）；解析失败的行逐条出声。
func LoadCardRules(path string, log func(string)) []CardRule {
	rules := make([]CardRule, 0)
	if path == "" {
		return rules
	}
	if !fileExists(path) {
		if log != nil {
			log("规则文件不存在，只用内置规则：" + path)
		}
		return rules
	}
	data, err := os.ReadFile(path)
	if err != nil {
		if log != nil {
			log("规则文件读不到：" + path + " —— " + err.Error())
		}
		return rules
	}
	lines := splitLines(string(data))
	for i, raw := range lines {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		cols := strings.Split(line, "\t")
		if len(cols) < 3 {
			if log != nil {
				log("规则第 " + itoaSmall(i+1) + " 行列数不足，已跳过：" + line)
			}
			continue
		}
		kind := strings.ToLower(strings.TrimSpace(cols[0]))
		votes := 0
		works := 0
		origin := ""
		if len(cols) >= 4 {
			origin = strings.ToLower(strings.TrimSpace(cols[3]))
		}
		if origin == "" {
			origin = "manual"
		}
		if len(cols) >= 5 {
			votes = parseIntOr0(strings.TrimSpace(cols[4]))
		}
		if len(cols) >= 6 {
			works = parseIntOr0(strings.TrimSpace(cols[5]))
		}
		var k CardRuleKind
		switch kind {
		case "dir":
			k = RuleDir
		case "kw":
			k = RuleKeyword
		case "chara":
			k = RuleChara
		case "author":
			k = RuleAuthor
		default:
			if log != nil {
				log("规则第 " + itoaSmall(i+1) + " 行类型未知，已跳过：" + kind)
			}
			continue
		}
		rules = append(rules, CardRule{
			Kind: k, Match: strings.TrimSpace(cols[1]), Value: strings.TrimSpace(cols[2]),
			Origin: origin, Votes: votes, Works: works, Enabled: true,
		})
	}
	return rules
}

// WriteCardRules 把规则表写成 TSV（UTF-8 BOM——与索引 / 计划同编码）。
func WriteCardRules(path string, rules []CardRule) error {
	dir := filepath.Dir(path)
	if dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	var sb strings.Builder
	sb.WriteString("# 类型\tdir / kw / chara / author\t值\t来源\t票数\t作品数\n")
	sb.WriteString("# dir = 源目录名 → 作品；kw = 关键词 → 作品；chara = 角色名精确 → 作品；author = 源目录名 → 作者\n")
	sb.WriteString("# 来源：builtin / learn / manual；票数 / 作品数为 learn 统计（导出全量带上，导入按列补齐）\n")
	for _, r := range rules {
		sb.WriteString(RuleKindName(r.Kind) + "\t" + r.Match + "\t" + r.Value + "\t" + r.Origin + "\t" +
			itoaSmall(r.Votes) + "\t" + itoaSmall(r.Works) + "\n")
	}
	return writeFileBOM(path, sb.String())
}

// LearnCardRules 从索引 / 计划 TSV 学「角色名 → 作品」规则——只取作品明确的行；同名多作品按出现次数取多数。
func LearnCardRules(tsvPath string, log func(string)) []CardRule {
	rules := make([]CardRule, 0)
	if tsvPath == "" || !fileExists(tsvPath) {
		if log != nil {
			log("索引文件不存在：" + tsvPath)
		}
		return rules
	}
	data, err := os.ReadFile(tsvPath)
	if err != nil {
		if log != nil {
			log("索引文件读不到：" + tsvPath)
		}
		return rules
	}
	lines := splitLines(string(data))
	if len(lines) < 2 {
		return rules
	}
	head := strings.Split(lines[0], "\t")
	nameCol, workCol := -1, -1
	for i, h := range head {
		h = strings.TrimSpace(h)
		if h == "角色名" {
			nameCol = i
		} else if h == "作品" {
			workCol = i
		}
	}
	if nameCol < 0 || workCol < 0 {
		if log != nil {
			log("表头缺少「角色名」或「作品」列：" + tsvPath)
		}
		return rules
	}
	need := nameCol
	if workCol > need {
		need = workCol
	}
	votes := map[string]map[string]int{}
	for i := 1; i < len(lines); i++ {
		line := lines[i]
		if line == "" {
			continue
		}
		c := strings.Split(line, "\t")
		if len(c) <= need {
			continue
		}
		name := strings.TrimSpace(c[nameCol])
		work := strings.TrimSpace(c[workCol])
		if name == "" || work == "" || work == WorkPending || strings.Contains(work, "待归包") {
			continue
		}
		tally, ok := votes[name]
		if !ok {
			tally = map[string]int{}
			votes[name] = tally
		}
		tally[work]++
	}
	for name, tally := range votes {
		best := ""
		bestCount := 0
		for w, n := range tally {
			if n > bestCount {
				best, bestCount = w, n
			}
		}
		rules = append(rules, CardRule{Kind: RuleChara, Match: name, Value: best, Origin: "learn", Votes: bestCount, Works: len(tally), Enabled: true})
	}
	if log != nil {
		log("从索引学到 " + itoaSmall(len(rules)) + " 条角色名规则：" + tsvPath)
	}
	return rules
}

// MergeCardRules 合并规则——同 Kind 同 Match 时外部覆盖内置（外部在后）。
func MergeCardRules(builtin, external []CardRule) []CardRule {
	merged := make([]CardRule, 0, len(builtin)+len(external))
	merged = append(merged, builtin...)
	for _, e := range external {
		hit := -1
		for i := range merged {
			if merged[i].Kind == e.Kind && strings.EqualFold(merged[i].Match, e.Match) {
				hit = i
				break
			}
		}
		if hit >= 0 {
			merged[hit] = e
		} else {
			merged = append(merged, e)
		}
	}
	return merged
}

// SafeDirName 目录名清洗——Windows 非法字符换下划线、去尾部点与空格、保留设备名加前缀、超长截断；空值给「未标注」。
func SafeDirName(name string) string {
	s := strings.TrimSpace(name)
	if s == "" {
		return "未标注"
	}
	var sb strings.Builder
	for _, ch := range s {
		switch ch {
		case '\\', '/', ':', '*', '?', '"', '<', '>', '|':
			sb.WriteRune('_')
		default:
			sb.WriteRune(ch)
		}
	}
	cleaned := strings.TrimRight(sb.String(), ". ")
	if cleaned == "" {
		return "未标注"
	}
	upper := strings.ToUpper(cleaned)
	switch upper {
	case "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3":
		cleaned = "_" + cleaned
	}
	runes := []rune(cleaned)
	if len(runes) > 60 {
		cleaned = strings.TrimRight(string(runes[:60]), ". ")
	}
	return cleaned
}

// LoadWorkCategories 读大类映射（<kkc 根>/tools/work-category.tsv）——作品 → 大类 / 子类。
func LoadWorkCategories(destRoot string, log func(string)) map[string]WorkCategory {
	m := map[string]WorkCategory{}
	if destRoot == "" {
		return m
	}
	full := strings.TrimRight(filepath.Clean(destRoot), string(os.PathSeparator))
	kkcRoot := filepath.Dir(full)
	path := filepath.Join(kkcRoot, "tools", "work-category.tsv")
	if !fileExists(path) {
		if log != nil {
			log("大类映射不存在，人物卡将回落 其它/：" + path)
		}
		return m
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return m
	}
	for i, raw := range splitLines(string(data)) {
		line := strings.TrimRight(raw, "\r\n")
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		c := strings.Split(line, "\t")
		if len(c) < 3 || c[0] == "作品" {
			continue
		}
		work := strings.TrimSpace(c[0])
		cat := strings.TrimSpace(c[1])
		sub := strings.TrimSpace(c[2])
		if work == "" || cat == "" {
			if log != nil {
				log("大类映射第 " + itoaSmall(i+1) + " 行缺作品或大类，已跳过")
			}
			continue
		}
		m[work] = WorkCategory{Cat: cat, Sub: sub}
	}
	if log != nil {
		log("大类映射：" + itoaSmall(len(m)) + " 个作品（" + path + "）")
	}
	return m
}

// TargetDirOf 目标子目录（卡类型 + 作品 → 目录）——人物卡按大类 / 子类分层，mod / 其它按固定段。
func TargetDirOf(kind, work, cardType string, categories map[string]WorkCategory) string {
	if cardType == EroMakeCardType {
		return EroMakeFolder
	}
	switch kind {
	case KindChara:
		if work == WorkPending {
			return filepath.Join("01-卡片", "人物卡", WorkPending)
		}
		if wc, ok := categories[work]; ok && work != "" {
			if wc.Sub == "" {
				return filepath.Join("01-卡片", "人物卡", SafeDirName(wc.Cat), SafeDirName(work))
			}
			return filepath.Join("01-卡片", "人物卡", SafeDirName(wc.Cat), SafeDirName(wc.Sub), SafeDirName(work))
		}
		return filepath.Join("01-卡片", "人物卡", "其它", SafeDirName(work))
	case KindClothes:
		return filepath.Join("01-卡片", "服装卡", SafeDirName(work))
	case KindScene:
		return filepath.Join("01-卡片", "场景卡", SafeDirName(work))
	case KindMod:
		return filepath.Join("02-mod", "散件")
	}
	return filepath.Join("05-其它", "散件")
}

// IsCardKind 卡片类归类（人物卡 / 服装卡 / 场景卡）。
func IsCardKind(kind string) bool {
	return kind == KindChara || kind == KindClothes || kind == KindScene
}

// NormalizeMatchKey 匹配归一化——去所有空白（含全角空格）并小写。
func NormalizeMatchKey(s string) string {
	if s == "" {
		return ""
	}
	var sb strings.Builder
	for _, ch := range s {
		if isSpaceRune(ch) {
			continue
		}
		sb.WriteRune(toLowerRune(ch))
	}
	return sb.String()
}

// ClassifyWork 作品判定——先看源目录规则（目录名 = 强证据），再看角色名规则，再看关键词。
func ClassifyWork(item *CardPlanItem, rules []CardRule) string {
	firstDir := ""
	if idx := strings.Index(item.SrcRel, "/"); idx > 0 {
		firstDir = item.SrcRel[:idx]
	}
	for _, r := range rules {
		if r.Kind != RuleDir {
			continue
		}
		if strings.EqualFold(r.Match, firstDir) {
			return r.Value
		}
	}
	chara := strings.TrimSpace(item.CharaName)
	if chara != "" {
		for _, r := range rules {
			if r.Kind != RuleChara {
				continue
			}
			if strings.EqualFold(r.Match, chara) {
				return r.Value
			}
		}
		charaKey := NormalizeMatchKey(chara)
		for _, r := range rules {
			if r.Kind != RuleChara {
				continue
			}
			if NormalizeMatchKey(r.Match) == charaKey {
				return r.Value
			}
		}
	}
	text := chara + " " + item.SrcRel
	textKey := NormalizeMatchKey(text)
	for _, r := range rules {
		if r.Kind != RuleKeyword {
			continue
		}
		if strings.Contains(strings.ToLower(text), strings.ToLower(r.Match)) {
			return r.Value
		}
		if strings.Contains(textKey, NormalizeMatchKey(r.Match)) {
			return r.Value
		}
	}
	return WorkPending
}

// ResolveAuthor 作者判定——先看源目录规则，再看文件名 [作者] 前缀。
func ResolveAuthor(item *CardPlanItem, rules []CardRule) string {
	firstDir := ""
	if idx := strings.Index(item.SrcRel, "/"); idx > 0 {
		firstDir = item.SrcRel[:idx]
	}
	for _, r := range rules {
		if r.Kind != RuleAuthor {
			continue
		}
		if strings.EqualFold(r.Match, firstDir) {
			return r.Value
		}
	}
	file := filepath.Base(item.SrcPath)
	lb := strings.Index(file, "[")
	rb := strings.Index(file, "]")
	if lb == 0 && rb > 1 {
		return file[1:rb]
	}
	return ""
}

// BuildCardPlan 生成整理计划——遍历源目录，逐文件判类型 / 角色名 / 作品 / 作者，算出目标路径并检出重名。
func BuildCardPlan(srcDirs []string, destRoot string, rules []CardRule, log func(string), keepPending bool) *CardPlan {
	plan := &CardPlan{DestRoot: destRoot, CreatedAt: nowLocalText()}
	destFiles := map[string]map[string]int64{}
	usedInPlan := map[string]map[string]bool{}
	categories := LoadWorkCategories(destRoot, log)

	for _, srcDir := range srcDirs {
		src := srcDir
		info, statErr := os.Stat(src)
		if statErr != nil || !info.IsDir() {
			if log != nil {
				if statErr == nil {
					log("跳过（这是文件不是目录）：" + src)
				} else {
					log("跳过不存在的源目录：" + src)
				}
			}
			continue
		}
		rootName := filepath.Base(strings.TrimRight(src, string(os.PathSeparator)))
		if rootName == "" || rootName == "." {
			rootName = src
		}
		files := make([]string, 0, 256)
		walkErr := filepath.Walk(src, func(p string, fi os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if fi.IsDir() {
				return nil
			}
			files = append(files, p)
			return nil
		})
		if walkErr != nil {
			if log != nil {
				log("枚举失败（" + src + "）：" + walkErr.Error())
			}
			continue
		}
		sort.Slice(files, func(i, j int) bool { return strings.ToLower(files[i]) < strings.ToLower(files[j]) })
		for _, p := range files {
			item := DescribeCardFile(p, src, rootName, rules)
			if item == nil {
				continue
			}
			PlaceCardItem(item, destRoot, destFiles, usedInPlan, categories)
			if keepPending || isInPendingArea(item.SrcPath, destRoot) {
				protectPending(item)
			}
			plan.Items = append(plan.Items, *item)
		}
		if log != nil {
			log("已扫 " + rootName + "：" + itoaSmall(len(files)) + " 个文件")
		}
	}
	return plan
}

// isInPendingArea 待确认区判定——源路径已落在整理根的某个待确认目录内（01-卡片/…/待确认/…）。
func isInPendingArea(srcPath, destRoot string) bool {
	if srcPath == "" || destRoot == "" {
		return false
	}
	rel, err := filepath.Rel(destRoot, srcPath)
	if err != nil {
		return false
	}
	norm := strings.ReplaceAll(rel, "\\", "/")
	if strings.HasPrefix(norm, "../") || norm == ".." || filepath.IsAbs(norm) {
		return false
	}
	parts := strings.Split(norm, "/")
	if len(parts) < 2 || parts[0] != "01-卡片" {
		return false
	}
	for i := 1; i < len(parts)-1; i++ {
		if parts[i] == WorkPending {
			return true
		}
	}
	return false
}

// protectPending 待确认保护——作品仍判不出的条目保持原位（不写 Move）。
func protectPending(item *CardPlanItem) {
	if item == nil || item.Work != WorkPending {
		return
	}
	item.Move = false
	item.State = CardStateSkipped
	note := "保留待确认（不搬）"
	if item.Note == "" {
		item.Note = note
	} else {
		item.Note = item.Note + "；" + note
	}
}

// DescribeCardFile 判定单个文件的归类与元数据（不碰磁盘写入）。
func DescribeCardFile(path, srcRoot, rootName string, rules []CardRule) *CardPlanItem {
	rel, err := filepath.Rel(srcRoot, path)
	if err != nil {
		rel = filepath.Base(path)
	}
	rel = strings.ReplaceAll(rel, "\\", "/")
	ext := strings.ToLower(filepath.Ext(path))
	item := &CardPlanItem{
		SrcPath: path,
		SrcRoot: rootName,
		SrcRel:  rel,
		State:   CardStatePending,
	}
	if fi, statErr := os.Stat(path); statErr == nil {
		item.Size = fi.Size()
	}

	switch ext {
	case ".png":
		head := ReadCardHeadState(path)
		if head.State != CardHeadOk || head.CardType == "" {
			item.Kind = KindOther
			item.Note = "非卡 PNG"
		} else {
			item.CardType = head.CardType
			switch {
			case strings.Contains(head.CardType, "Clothes"):
				item.Kind = KindClothes
			case head.CardType == SceneCardType:
				item.Kind = KindScene
			case strings.Contains(head.CardType, "Chara"):
				item.Kind = KindChara
			default:
				item.Kind = KindOther
				item.Note = "未知卡类型"
			}
			if item.Kind == KindChara {
				item.CharaName = ReadCardName(path)
			} else if item.Kind == KindClothes {
				item.CharaName = readClothesName(path)
			}
		}
	case ".zipmod":
		item.Kind = KindMod
	default:
		item.Kind = KindOther
	}

	item.Work = ClassifyWork(item, rules)
	item.Author = ResolveAuthor(item, rules)
	item.SrcRoot = sourceOf(item, rootName)
	return item
}

// sourceOf 来源键——源根名 + 相对路径首段；根下直放文件只有源根名。
func sourceOf(item *CardPlanItem, rootName string) string {
	if idx := strings.Index(item.SrcRel, "/"); idx > 0 {
		return rootName + "/" + item.SrcRel[:idx]
	}
	return rootName
}

// readClothesName 服装卡的角色名（数据区头段第三段），读不到返回空。
func readClothesName(path string) string {
	info, err := ParseCard(path)
	if err != nil || info == nil {
		return ""
	}
	return info.CharaName
}

// PlaceCardItem 算目标路径并检出重名（目标已存在 / 计划内撞车）。
func PlaceCardItem(item *CardPlanItem, destRoot string, destFiles map[string]map[string]int64, usedInPlan map[string]map[string]bool, categories map[string]WorkCategory) {
	sub := TargetDirOf(item.Kind, item.Work, item.CardType, categories)
	dir := filepath.Join(destRoot, sub)
	fileName := filepath.Base(item.SrcPath)
	if item.Kind == KindOther {
		relDir := filepath.Dir(strings.ReplaceAll(item.SrcRel, "/", string(os.PathSeparator)))
		if relDir != "" && relDir != "." {
			dir = filepath.Join(dir, relDir)
		}
	}

	existing, ok := destFiles[dir]
	if !ok {
		existing = map[string]int64{}
		if entries, readErr := os.ReadDir(dir); readErr == nil {
			for _, e := range entries {
				if e.IsDir() {
					continue
				}
				info, infoErr := e.Info()
				if infoErr != nil {
					existing[e.Name()] = -1
					continue
				}
				existing[e.Name()] = info.Size()
			}
		}
		destFiles[dir] = existing
	}
	used, ok := usedInPlan[dir]
	if !ok {
		used = map[string]bool{}
		usedInPlan[dir] = used
	}

	if existSize, hit := lookupFold(existing, fileName); hit {
		if existSize == item.Size {
			item.DestPath = filepath.Join(dir, fileName)
			item.Note = "跳过：目标已有同名同尺寸"
			item.Move = false
			item.State = CardStateSkipped
			return
		}
		fileName = uniqueFileName(fileName, existing, used)
		item.Note = "改名：目标同名不同尺寸"
	}
	if _, ok := lookupFold(used, fileName); ok {
		fileName = uniqueFileName(fileName, existing, used)
		item.Note = "改名：计划内重名"
	}
	if len([]rune(fileName)) > 240 {
		stem := strings.TrimSuffix(fileName, filepath.Ext(fileName))
		ext := filepath.Ext(fileName)
		stemRunes := []rune(stem)
		if len(stemRunes) > 200 {
			stem = string(stemRunes[:200])
		}
		fileName = stem + ext
		item.Note = "改名：超长截断"
	}
	used[fileName] = true
	item.DestPath = filepath.Join(dir, fileName)
	item.Move = true
}

// uniqueFileName 生成不冲突的文件名——同名时依次试 [dup2] [dup3]……
func uniqueFileName(fileName string, existing map[string]int64, used map[string]bool) string {
	ext := filepath.Ext(fileName)
	stem := strings.TrimSuffix(fileName, ext)
	for n := 2; n < 1000; n++ {
		cand := stem + "[dup" + itoaSmall(n) + "]" + ext
		if _, taken := lookupFold(used, cand); !taken {
			if _, ok := lookupFold(existing, cand); !ok {
				return cand
			}
		}
	}
	return stem + "[dup" + shortRandomSuffix() + "]" + ext
}

// ValidateCardLayout 布局校验——卡片条目的目标必须落在 <destRoot>/01-卡片/<卡类型>/<作品>/ 下。
func ValidateCardLayout(destRoot string, plan *CardPlan, log func(string)) int {
	bad := 0
	rootNorm := ""
	if destRoot != "" {
		rootNorm = strings.TrimRight(strings.ReplaceAll(filepath.Clean(destRoot), "\\", "/"), "/")
	}
	for _, it := range plan.Items {
		if !IsCardKind(it.Kind) {
			continue
		}
		destNorm := strings.ReplaceAll(it.DestPath, "\\", "/")
		rel := destNorm
		if rootNorm != "" {
			if !strings.HasPrefix(strings.ToLower(destNorm), strings.ToLower(rootNorm)+"/") {
				bad++
				if log != nil {
					log("布局不合格（" + it.Kind + "）：" + it.DestPath + "——目标不在整理根 " + rootNorm + " 下")
				}
				continue
			}
			rel = destNorm[len(rootNorm)+1:]
		} else {
			if !strings.HasPrefix(strings.ToLower(destNorm), "01-卡片/") {
				if anchor := strings.Index(strings.ToLower(destNorm), "/01-卡片/"); anchor >= 0 {
					rel = destNorm[anchor+1:]
				}
			}
		}
		parts := strings.Split(rel, "/")
		var ok bool
		if it.CardType == EroMakeCardType {
			ok = len(parts) >= 2 && parts[len(parts)-2] == EroMakeFolder && parts[len(parts)-1] != ""
		} else if it.Kind == KindChara {
			ok = len(parts) >= 4 && parts[0] == "01-卡片" && parts[1] == "人物卡" &&
				strings.TrimSpace(parts[len(parts)-2]) != "" && parts[len(parts)-1] != ""
		} else {
			ok = len(parts) >= 4 && parts[len(parts)-4] == "01-卡片" && parts[len(parts)-3] == it.Kind &&
				strings.TrimSpace(parts[len(parts)-2]) != "" && parts[len(parts)-1] != ""
		}
		if !ok {
			bad++
			if log != nil {
				log("布局不合格（" + it.Kind + "）：" + it.DestPath +
					"——人物卡须为 01-卡片/人物卡/大类/子类/作品/文件名，其它卡须为 01-卡片/卡类型/作品/文件名")
			}
		}
	}
	return bad
}

// BuildLayoutPlan 生成「重排计划」——把已归位的卡片按某维度分子目录（work / author / chara），或恢复扁平（flat）。
func BuildLayoutPlan(root, indexTsv, by string, log func(string)) *CardPlan {
	plan := &CardPlan{DestRoot: root, CreatedAt: nowLocalText()}
	if indexTsv == "" || !fileExists(indexTsv) {
		if log != nil {
			log("索引文件不存在：" + indexTsv)
		}
		return plan
	}
	data, err := os.ReadFile(indexTsv)
	if err != nil {
		if log != nil {
			log("索引文件读不到：" + indexTsv)
		}
		return plan
	}
	lines := splitLines(string(data))
	if len(lines) < 2 {
		return plan
	}
	head := strings.Split(lines[0], "\t")
	colDest, colKind, colCtype, colName, colWork, colAuthor, colSrc, colInner, colSize := -1, -1, -1, -1, -1, -1, -1, -1, -1
	for i, h := range head {
		switch strings.TrimSpace(h) {
		case "现路径":
			colDest = i
		case "类型":
			colKind = i
		case "卡类型":
			colCtype = i
		case "角色名":
			colName = i
		case "作品":
			colWork = i
		case "作者":
			colAuthor = i
		case "来源":
			colSrc = i
		case "包内路径":
			colInner = i
		case "大小":
			colSize = i
		}
	}
	if colDest < 0 || colKind < 0 {
		if log != nil {
			log("索引表头缺少「现路径」或「类型」列：" + indexTsv)
		}
		return plan
	}
	maxCol := len(head) - 1
	for i := 1; i < len(lines); i++ {
		line := lines[i]
		if line == "" {
			continue
		}
		c := strings.Split(line, "\t")
		if len(c)-1 > maxCol {
			maxCol = len(c) - 1
		}
		if len(c) <= colDest {
			continue
		}
		if getTSVCol(c, colKind) != "卡" {
			continue
		}
		rel := getTSVCol(c, colDest)
		if rel == "" || strings.HasPrefix(rel, "（") {
			continue
		}
		norm := strings.ReplaceAll(rel, "\\", "/")
		parts := strings.Split(norm, "/")
		if len(parts) < 3 {
			continue
		}
		typeDir := parts[0] + "/" + parts[1]
		fileName := parts[len(parts)-1]
		dim := ""
		switch by {
		case "work":
			dim = SafeDirName(getTSVCol(c, colWork))
		case "author":
			dim = SafeDirName(getTSVCol(c, colAuthor))
		case "chara":
			dim = SafeDirName(getTSVCol(c, colName))
		}
		newRel := typeDir + "/" + fileName
		if dim != "" {
			newRel = typeDir + "/" + dim + "/" + fileName
		}
		srcAbs := filepath.Join(root, strings.ReplaceAll(norm, "/", string(os.PathSeparator)))
		dstAbs := filepath.Join(root, strings.ReplaceAll(newRel, "/", string(os.PathSeparator)))
		size := int64(0)
		if sizeText := getTSVCol(c, colSize); sizeText != "" {
			size = parseInt64Or0(sizeText)
		}
		item := CardPlanItem{
			SrcPath:   srcAbs,
			Kind:      "卡",
			CardType:  getTSVCol(c, colCtype),
			CharaName: getTSVCol(c, colName),
			Work:      getTSVCol(c, colWork),
			Author:    getTSVCol(c, colAuthor),
			SrcRoot:   getTSVCol(c, colSrc),
			DestPath:  dstAbs,
			Size:      size,
			State:     CardStatePending,
		}
		if inner := getTSVCol(c, colInner); inner != "" {
			item.SrcRel = inner
		} else {
			item.SrcRel = norm
		}
		if strings.EqualFold(srcAbs, dstAbs) {
			item.Note = "已在目标位置"
			item.Move = false
			item.State = CardStateSkipped
		} else if !fileExists(srcAbs) {
			item.Note = "源已不在"
			item.Move = false
			item.State = CardStateSkipped
		} else if fileExists(dstAbs) {
			item.Note = "目标已存在同名文件"
			item.Move = false
			item.State = CardStateConflict
		} else {
			item.Move = true
		}
		plan.Items = append(plan.Items, item)
	}
	return plan
}

// WriteCardPlan 写计划 TSV（UTF-8 BOM——与 index.tsv 同编码）。
func WriteCardPlan(path string, plan *CardPlan) error {
	dir := filepath.Dir(path)
	if dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	var sb strings.Builder
	sb.WriteString("源路径\t类型\t卡类型\t角色名\t作品\t作者\t来源\t相对路径\t目标路径\t大小\t备注\t移动\t状态\n")
	for _, it := range plan.Items {
		move := "N"
		if it.Move {
			move = "Y"
		}
		sb.WriteString(strings.Join([]string{
			cleanTSV(it.SrcPath), cleanTSV(it.Kind), cleanTSV(it.CardType), cleanTSV(it.CharaName),
			cleanTSV(it.Work), cleanTSV(it.Author), cleanTSV(it.SrcRoot), cleanTSV(it.SrcRel),
			cleanTSV(it.DestPath), itoaSmall64(it.Size), cleanTSV(it.Note), move, cleanTSV(it.State),
		}, "\t") + "\n")
	}
	return writeFileBOM(path, sb.String())
}

// ReadCardPlan 回读计划 TSV（执行 / 核对用）。
func ReadCardPlan(path string) *CardPlan {
	plan := &CardPlan{}
	if !fileExists(path) {
		return plan
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return plan
	}
	lines := splitLines(string(data))
	for i, line := range lines {
		if i == 0 {
			continue
		}
		if line == "" {
			continue
		}
		c := strings.Split(line, "\t")
		if len(c) < 13 {
			continue
		}
		plan.Items = append(plan.Items, CardPlanItem{
			SrcPath:   c[0],
			Kind:      c[1],
			CardType:  c[2],
			CharaName: c[3],
			Work:      c[4],
			Author:    c[5],
			SrcRoot:   c[6],
			SrcRel:    c[7],
			DestPath:  c[8],
			Size:      parseInt64Or0(c[9]),
			Note:      c[10],
			Move:      c[11] == "Y",
			State:     c[12],
		})
	}
	return plan
}

// ExecuteCardPlan 执行计划——只搬「待搬」条目；逐条落状态，中断安全。返回移动数。
func ExecuteCardPlan(plan *CardPlan, log func(string)) int {
	done := 0
	for i := range plan.Items {
		it := &plan.Items[i]
		if !it.Move || it.State != CardStatePending {
			continue
		}
		if !fileExists(it.SrcPath) {
			it.State = CardStateSkipped
			it.Note = appendNote(it.Note, "源已不在")
			continue
		}
		if fileExists(it.DestPath) {
			it.State = CardStateConflict
			it.Note = appendNote(it.Note, "目标已存在同名文件")
			continue
		}
		dir := filepath.Dir(it.DestPath)
		if dir != "" && dir != "." {
			if err := os.MkdirAll(dir, 0o755); err != nil {
				it.State = CardStateFailed
				it.Note = appendNote(it.Note, err.Error())
				if log != nil {
					log("失败：" + it.SrcPath + " —— " + err.Error())
				}
				continue
			}
		}
		if err := moveFileRename(it.SrcPath, it.DestPath); err != nil {
			it.State = CardStateFailed
			it.Note = appendNote(it.Note, err.Error())
			if log != nil {
				log("失败：" + it.SrcPath + " —— " + err.Error())
			}
			continue
		}
		it.State = CardStateMoved
		done++
	}
	if log != nil {
		log("已搬 " + itoaSmall(done) + " 个文件")
	}
	return done
}

// PruneEmptyDirsCard 删掉某根下的空目录（自下而上多轮；根本身不动）；返回清掉的目录数。
func PruneEmptyDirsCard(root string, log func(string)) int {
	removed := 0
	blocked := 0
	for pass := 0; pass < 8; pass++ {
		any := false
		dirs := make([]string, 0, 128)
		walkErr := filepath.Walk(root, func(p string, fi os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if fi.IsDir() && p != root {
				dirs = append(dirs, p)
			}
			return nil
		})
		if walkErr != nil {
			if log != nil {
				log("枚举目录失败（" + root + "）：" + walkErr.Error())
			}
			break
		}
		sort.Slice(dirs, func(i, j int) bool { return len(dirs[i]) > len(dirs[j]) })
		for _, dir := range dirs {
			entries, readErr := os.ReadDir(dir)
			if readErr != nil {
				continue
			}
			if len(entries) == 0 {
				if err := os.Remove(dir); err != nil {
					blocked++
					if log != nil {
						log("空目录删不掉（可能被占用）：" + dir + " —— " + err.Error())
					}
					continue
				}
				removed++
				any = true
			}
		}
		if !any {
			break
		}
	}
	if blocked > 0 && log != nil {
		log("空目录清理：删掉 " + itoaSmall(removed) + " 个 · 删不掉 " + itoaSmall(blocked) + " 次（已逐条出声）")
	}
	return removed
}

// ExportCardRefs 导出整理根的卡片引用表（现路径 → 引用 guid 列表）——索引生成的前置数据，不写库。
func ExportCardRefs(root, outPath string, log func(string)) (int, error) {
	rootFull, err := filepath.Abs(root)
	if err != nil {
		return 0, err
	}
	n := 0
	withRefs := 0
	var rows int64
	dir := filepath.Dir(outPath)
	if dir != "" && dir != "." {
		if err = os.MkdirAll(dir, 0o755); err != nil {
			return 0, err
		}
	}
	var sb strings.Builder
	sb.WriteString("现路径\t引用mod\t引用行数\n")
	walkErr := filepath.Walk(rootFull, func(p string, fi os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if fi.IsDir() || !strings.EqualFold(filepath.Ext(p), ".png") {
			return nil
		}
		rel, relErr := filepath.Rel(rootFull, p)
		if relErr != nil {
			rel = filepath.Base(p)
		}
		rel = strings.ReplaceAll(rel, "\\", "/")
		n++
		info, parseErr := ParseCard(p)
		if parseErr != nil || info == nil {
			sb.WriteString(rel + "\t\t0\n")
			return nil
		}
		ids := info.DistinctModIds()
		if len(ids) > 0 {
			withRefs++
		}
		rows += int64(len(info.ModRefs))
		sb.WriteString(rel + "\t" + strings.Join(ids, ",") + "\t" + itoaSmall(len(info.ModRefs)) + "\n")
		if log != nil && n%500 == 0 {
			log("  已读 " + itoaSmall(n))
		}
		return nil
	})
	if walkErr != nil {
		return n, walkErr
	}
	if err = writeFileBOM(outPath, sb.String()); err != nil {
		return n, err
	}
	if log != nil {
		log("引用表：" + itoaSmall(n) + " 张 · 有引用 " + itoaSmall(withRefs) + " · 引用行 " + itoaSmall64(rows))
	}
	return n, nil
}

// ---------- 小工具 ----------

// getTSVCol 取 TSV 第 index 列（越界返回空）。
func getTSVCol(cols []string, index int) string {
	if index < 0 || index >= len(cols) {
		return ""
	}
	return strings.TrimSpace(cols[index])
}

// cleanTSV 清洗一个 TSV 字段（去掉制表符与换行，避免破坏列）。
func cleanTSV(v string) string {
	if v == "" {
		return ""
	}
	v = strings.ReplaceAll(v, "\t", " ")
	v = strings.ReplaceAll(v, "\r", " ")
	v = strings.ReplaceAll(v, "\n", " ")
	return v
}

// appendNote 追加备注（分号分隔）。
func appendNote(note, add string) string {
	if note == "" {
		return add
	}
	return note + "；" + add
}

// splitLines 按行切分（去尾部空行）。
func splitLines(text string) []string {
	text = strings.ReplaceAll(text, "\r\n", "\n")
	text = strings.ReplaceAll(text, "\r", "\n")
	lines := strings.Split(text, "\n")
	for len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return lines
}

// writeFileBOM 写 UTF-8 BOM 文本（对齐 C# new UTF8Encoding(true)）。
func writeFileBOM(path, text string) error {
	return os.WriteFile(path, append([]byte{0xEF, 0xBB, 0xBF}, []byte(text)...), 0o644)
}

// parseIntOr0 整数解析（失败给 0）。
func parseIntOr0(s string) int {
	v, err := parseIntStrict(s)
	if err != nil {
		return 0
	}
	return v
}

// parseInt64Or0 整数解析（失败给 0）。
func parseInt64Or0(s string) int64 {
	var v int64
	s = strings.TrimSpace(s)
	if s == "" {
		return 0
	}
	neg := false
	if strings.HasPrefix(s, "-") {
		neg = true
		s = s[1:]
	}
	for _, ch := range s {
		if ch < '0' || ch > '9' {
			return 0
		}
		v = v*10 + int64(ch-'0')
	}
	if neg {
		return -v
	}
	return v
}

// parseIntStrict 整数解析（返回错误）。
func parseIntStrict(s string) (int, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0, fmt.Errorf("空值")
	}
	neg := false
	if strings.HasPrefix(s, "-") {
		neg = true
		s = s[1:]
	}
	v := 0
	for _, ch := range s {
		if ch < '0' || ch > '9' {
			return 0, fmt.Errorf("非整数: %s", s)
		}
		v = v*10 + int(ch-'0')
	}
	if neg {
		return -v, nil
	}
	return v, nil
}

// nowLocalText 本地时间文本（对齐 C# DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")）。
func nowLocalText() string {
	return timeNowLocal().Format("2006-01-02 15:04:05")
}

// lookupFold 大小写不敏感查表（返回是否命中与值）。
func lookupFold[T any](m map[string]T, key string) (T, bool) {
	if v, ok := m[key]; ok {
		return v, true
	}
	lower := strings.ToLower(key)
	for k, v := range m {
		if strings.ToLower(k) == lower {
			return v, true
		}
	}
	var zero T
	return zero, false
}

// shortRandomSuffix 6 位随机后缀（对齐 C# Guid.NewGuid().ToString("N").Substring(0,6)）。
func shortRandomSuffix() string {
	const alphabet = "0123456789abcdef"
	buf := make([]byte, 6)
	randRead(buf)
	for i := range buf {
		buf[i] = alphabet[int(buf[i])%len(alphabet)]
	}
	return string(buf)
}

// isSpaceRune Unicode 空白判定（含全角空格 U+3000）。
func isSpaceRune(ch rune) bool {
	switch ch {
	case ' ', '\t', '\r', '\n', '\v', '\f', 0x00A0, 0x3000, 0x2000, 0x2001, 0x2002, 0x2003,
		0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0xFEFF:
		return true
	}
	return false
}

// toLowerRune 单字符小写（仅 ASCII 与常见拉丁；其余原样）。
func toLowerRune(ch rune) rune {
	if ch >= 'A' && ch <= 'Z' {
		return ch + ('a' - 'A')
	}
	return ch
}
