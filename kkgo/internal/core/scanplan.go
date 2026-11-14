package core

import "strings"

// 扫描步骤的读取档——同侧同档的相邻步骤合成一段，段内共用一次文件打开。
const (
	// ReadNone 零读（只用枚举结果与文件属性）。
	ReadNone = 0
	// ReadLocal 局部读（头段 / 块表 / 声明区 / 坐标块）。
	ReadLocal = 1
	// ReadImage 图片区读（缩略图解码）。
	ReadImage = 2
	// ReadFull 全扫（整个数据区流式扫描）。
	ReadFull = 3
)

// 设置键——扫描计划落两个键（对齐 C# ScanPlanCatalog.OrderKey / OnKey）。
const (
	// ScanPlanOrderKey 全集顺序。
	ScanPlanOrderKey = "scan_plan_order"
	// ScanPlanOnKey 启用集合。
	ScanPlanOnKey = "scan_plan_on"
	// GroupCore 组 id——统一扫描（必选）。
	GroupCore = "core"
	// GroupAnalyze 组 id——卡片分析（可选）。
	GroupAnalyze = "analyze"
)

// ScanStepDef 扫描步骤定义。
type ScanStepDef struct {
	// Id 步骤 id（落 scan_state 与 setting 的稳定标识）。
	Id string
	// Name 界面显示名。
	Name string
	// Mods true = mod 侧；false = 卡片侧。
	Mods bool
	// Tier 读取档。
	Tier int
	// Required 必选（勾选框锁定）。
	Required bool
	// DefaultOn 默认勾选。
	DefaultOn bool
	// Grayed 灰置（可见不可选）。
	Grayed bool
	// Note 界面提示。
	Note string
	// Group 所属扫描组 id（空 = 不属于任何组）。
	Group string
}

// ScanGroupDef 扫描组——配置窗的勾选单元（组内步骤同进同出）。
type ScanGroupDef struct {
	// Id 组 id。
	Id string
	// Name 界面显示名。
	Name string
	// Note 界面提示。
	Note string
	// Required 必选组。
	Required bool
	// DefaultOn 默认勾选。
	DefaultOn bool
}

// ScanSegment 扫描段——同侧同档的连续步骤（段内共用一次文件打开）。
type ScanSegment struct {
	// Mods true = mod 侧。
	Mods bool
	// Tier 读取档。
	Tier int
	// Steps 段内步骤（按执行顺序）。
	Steps []ScanStepDef
}

// Label 段的显示名（段内步骤名以 + 连接）。
func (s *ScanSegment) Label() string {
	parts := make([]string, 0, len(s.Steps))
	for _, step := range s.Steps {
		parts = append(parts, step.Name)
	}
	return strings.Join(parts, " + ")
}

// ScanPlan 扫描计划——全集顺序 + 启用集合（全局一套）。
type ScanPlan struct {
	// Order 全集顺序（全部步骤 id）。
	Order []string
	// On 启用集合（只记用户显式勾选的非必选项）。
	On map[string]bool
}

// scanSteps 内置步骤定义（数组顺序即默认执行顺序）。
var scanSteps = []ScanStepDef{
	{Id: "row", Name: "卡片行", Tier: ReadNone, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "只记文件与文件夹——卡片视图先能看见卡（不读文件内容）"},
	{Id: "modrow", Name: "mod 行", Mods: true, Tier: ReadNone, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "manifest 的 guid / 名称 / 版本 / 作者——mod 总数与关联主键（库内能读到 manifest.xml 的文件都算）"},
	{Id: "head", Name: "卡头段", Tier: ReadLocal, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "卡类型与图片区终点——其余数据区步骤的前置"},
	{Id: "refs", Name: "声明区", Tier: ReadLocal, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "卡片引用的 mod——四色与缺失清单的依据"},
	{Id: "name", Name: "角色名", Tier: ReadLocal, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "Parameter 块的姓 / 名——按角色名排序用"},
	{Id: "timeline", Name: "时间轴", Tier: ReadLocal, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "场景卡的 Timeline 时长——按时间轴排序用"},
	{Id: "coord", Name: "服装槽位", Tier: ReadLocal, Group: GroupAnalyze,
		Note: "人物卡七套 coordinate（服装 / 饰品 / 化妆）——只读分析"},
	{Id: "thumb", Name: "缩略图", Tier: ReadImage, Required: true, DefaultOn: true, Group: GroupCore,
		Note: "卡片视图的图（图片区解码）"},
	{Id: "detail", Name: "卡片分析", Tier: ReadFull, Group: GroupAnalyze,
		Note: "整个数据区的结构全字段（耗时与卡片体积成正比）"},
	{Id: "scene", Name: "场景深度", Tier: ReadFull, Group: GroupAnalyze,
		Note: "场景插件条目 / 道具 / 轨道组 / 内嵌角色数据（sd 专用）"},
	{Id: "composition", Name: "组成档案", Mods: true, Tier: ReadLocal,
		Note: "zipmod 条目清单与文本条目内容（打开 mod 时的组成视图）"},
	{Id: "u3d", Name: "unity3d 贴图", Mods: true, Tier: ReadFull, Grayed: true,
		Note: "容器内 unity3d 贴图解析——本轮占位，不参与扫描"},
}

// scanGroups 内置组定义（数组顺序即配置窗展示顺序）。
var scanGroups = []ScanGroupDef{
	{Id: GroupCore, Name: "统一扫描", Required: true, DefaultOn: true,
		Note: "卡片行 + mod 行 + 卡头段 + 声明区 + 角色名 + 时间轴 + 缩略图 + 插件库（插件库随主要库扫描）——必选，一次跑完"},
	{Id: GroupAnalyze, Name: "卡片分析",
		Note: "服装槽位 + 卡片分析 + 场景深度——整段数据区读取，耗时与卡片体积成正比"},
}

// FindScanStep 按 id 找步骤定义（未知返回 nil）。
func FindScanStep(id string) *ScanStepDef {
	for i := range scanSteps {
		if scanSteps[i].Id == id {
			return &scanSteps[i]
		}
	}
	return nil
}

// FindScanGroup 按 id 找组（未知返回 nil）。
func FindScanGroup(id string) *ScanGroupDef {
	for i := range scanGroups {
		if scanGroups[i].Id == id {
			return &scanGroups[i]
		}
	}
	return nil
}

// ScanStepsOf 某组的步骤（按内置顺序）。
func ScanStepsOf(groupID string) []ScanStepDef {
	list := make([]ScanStepDef, 0, 4)
	for _, d := range scanSteps {
		if d.Group != "" && d.Group == groupID {
			list = append(list, d)
		}
	}
	return list
}

// DefaultScanPlan 默认计划——内置顺序 + 默认勾选。
func DefaultScanPlan() *ScanPlan {
	p := &ScanPlan{Order: make([]string, 0, len(scanSteps)), On: map[string]bool{}}
	for _, d := range scanSteps {
		p.Order = append(p.Order, d.Id)
		if d.DefaultOn && !d.Required {
			p.On[d.Id] = true
		}
	}
	return p
}

// LoadScanPlan 从设置文本载入计划——order 给全集顺序（未知 id 丢弃、缺失项按内置顺序补末尾），on 给启用集合。
func LoadScanPlan(orderText, onText string) *ScanPlan {
	p := &ScanPlan{Order: make([]string, 0, len(scanSteps)), On: map[string]bool{}}
	seen := map[string]bool{}
	if orderText != "" {
		for _, raw := range strings.Split(orderText, ",") {
			id := strings.TrimSpace(raw)
			if id == "" || seen[id] || FindScanStep(id) == nil {
				continue
			}
			p.Order = append(p.Order, id)
			seen[id] = true
		}
	}
	for _, d := range scanSteps {
		if !seen[d.Id] {
			p.Order = append(p.Order, d.Id)
			seen[d.Id] = true
		}
	}
	on := map[string]bool{}
	if onText != "" {
		for _, raw := range strings.Split(onText, ",") {
			id := strings.TrimSpace(raw)
			if id != "" {
				on[id] = true
			}
		}
	}
	explicitOn := len(on) > 0
	for _, d := range scanSteps {
		if d.Grayed {
			continue
		}
		if d.Required {
			p.On[d.Id] = true
			continue
		}
		if explicitOn {
			if on[d.Id] {
				p.On[d.Id] = true
			}
			continue
		}
		if d.DefaultOn {
			p.On[d.Id] = true
		}
	}
	// 组归一——可选组只有「全开 / 全关」两态：组内步骤并非全部启用时按全关处理
	for _, g := range scanGroups {
		if g.Required {
			continue
		}
		steps := ScanStepsOf(g.Id)
		all := len(steps) > 0
		for _, d := range steps {
			if !p.On[d.Id] {
				all = false
				break
			}
		}
		if !all {
			for _, d := range steps {
				delete(p.On, d.Id)
			}
		}
	}
	return p
}

// IsOn 某步骤当前是否启用（必选项恒真，灰置项恒假）。
func (p *ScanPlan) IsOn(id string) bool {
	def := FindScanStep(id)
	if def == nil || def.Grayed {
		return false
	}
	if def.Required {
		return true
	}
	return p.On[id]
}

// Segments 按当前顺序 + 启用集合切出执行段——跨侧交错按顺序走，同侧同档相邻步骤合并。
func (p *ScanPlan) Segments() []ScanSegment {
	list := make([]ScanSegment, 0, 4)
	cur := -1
	for _, id := range p.Order {
		def := FindScanStep(id)
		if def == nil || def.Grayed || !p.IsOn(id) {
			continue
		}
		if cur >= 0 && list[cur].Mods == def.Mods && list[cur].Tier == def.Tier {
			list[cur].Steps = append(list[cur].Steps, *def)
			continue
		}
		list = append(list, ScanSegment{Mods: def.Mods, Tier: def.Tier, Steps: []ScanStepDef{*def}})
		cur = len(list) - 1
	}
	return list
}

// ScanPlanOrderText 顺序文本（逗号分隔）。
func ScanPlanOrderText(p *ScanPlan) string { return strings.Join(p.Order, ",") }

// ScanPlanOnText 启用文本（逗号分隔；只记非必选项、不含灰置）。
func ScanPlanOnText(p *ScanPlan) string {
	list := make([]string, 0, len(p.Order))
	for _, id := range p.Order {
		def := FindScanStep(id)
		if def == nil || def.Required || def.Grayed {
			continue
		}
		if p.On[id] {
			list = append(list, id)
		}
	}
	return strings.Join(list, ",")
}
