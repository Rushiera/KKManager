// 扫描与预热端点（G7）——契约对齐 C# WebApp.cs 的 /api/scan · /api/scan/status · /api/scan/plan · /api/warmup。
package web

import (
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// handleScan 启动一次扫描（后台任务，页面关掉也继续跑）。
// target 语义对齐 C# WebApp.cs §/api/scan：main = 预置条目 · extra = 使用者添加的库根 ·
// mods / cards = 单库更新（root 指定，只扫这一条）· plugins = 插件库 · all = 全量。
func (a *App) handleScan(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Target string `json:"target"`
		Force  bool   `json:"force"`
		Root   string `json:"root"`
		Order  string `json:"order"`
		On     string `json:"on"`
		Steps  string `json:"steps"`
	}
	_ = json.NewDecoder(r.Body).Decode(&body)
	if body.Target == "" {
		body.Target = "all"
	}
	scanMu.Lock()
	if scanState.Running {
		scanMu.Unlock()
		writeJSON(w, map[string]interface{}{"ok": false, "error": "已有扫描在跑（同一时刻只允许一个）"})
		return
	}
	scanState.Running = true
	scanState.Target = body.Target
	scanState.Message = "启动中"
	scanState.StartedAt = time.Now().Format("2006-01-02T15:04:05")
	scanState.FinishedAt = ""
	scanState.Errors = []string{}
	scanMu.Unlock()
	go a.runScan(body.Target, body.Force, body.Root, body.Order, body.On, body.Steps)
	writeJSON(w, map[string]interface{}{"ok": true, "target": body.Target})
}

// scanFail 扫描前置校验失败——落进状态面（失败必须可见），不静默降级成全量扫描。
func (a *App) scanFail(msg string) {
	scanMu.Lock()
	scanState.Errors = append(scanState.Errors, msg)
	scanMu.Unlock()
}

// runScan 后台执行扫描（main / extra / mods / cards / plugins / all）。
func (a *App) runScan(target string, force bool, rootPath, orderText, onText, steps string) {
	defer func() {
		scanMu.Lock()
		scanState.Running = false
		scanState.Message = "完成"
		scanState.FinishedAt = time.Now().Format("2006-01-02T15:04:05")
		scanMu.Unlock()
		// 扫描改变缺失集合（mod 库 / 卡片库都算）——缓存失效，下次打开缺失清单重建
		invalidateMissingCache()
	}()
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		a.scanFail(err.Error())
		return
	}
	defer hub.Close()
	// 扫描计划——请求带了顺序 / 勾选就落盘（全局一套，追加库扫描同样适用），否则读已保存的
	if strings.TrimSpace(orderText) != "" || strings.TrimSpace(onText) != "" {
		_ = hub.Core().SetSetting("scan_plan_order", orderText)
		_ = hub.Core().SetSetting("scan_plan_on", onText)
	}
	plan := loadScanPlan(hub, steps)
	log := func(msg string) {
		scanMu.Lock()
		scanState.Message = msg
		scanMu.Unlock()
	}
	// [段1] 单库更新（面板「更新本库」）——只扫这一条；离线库不参与扫描
	var only *core.RootEntry
	if strings.TrimSpace(rootPath) != "" {
		only = core.FindRootEntry(cfg, rootPath)
		if only == nil {
			a.scanFail("该库根不在已保存的配置里——先在「库根设置」点「保存配置」，再更新本库：" + rootPath)
			return
		}
		if only.Offline {
			a.scanFail("该库已离线——先上线再更新：" + only.Path)
			return
		}
	}
	// [段2] 扫描范围——main = 预置条目 · extra = 使用者添加的库根（离线库跳过）
	switch target {
	case "main", "extra":
		scope := core.ScanScopePreset
		if target == "extra" {
			scope = core.ScanScopeExtra
		}
		result, scanErr := core.ScanAll(hub, cfg, nil, scope, force, 256, 82, plan, log)
		a.finishScan(result, scanErr)
	case "mods":
		result, scanErr := core.ScanMods(hub, cfg, only, core.ScanScopeAll, force, plan, log)
		a.finishScan(result, scanErr)
	case "cards":
		result, scanErr := core.ScanCards(hub, cfg, only, core.ScanScopeAll, force, 256, 82, plan, log)
		a.finishScan(result, scanErr)
	case "plugins":
		result := &core.ScanResult{}
		dlls, plugins, configs := core.ScanPlugins(hub.Core(), cfg, result, log)
		result.PluginDlls, result.Plugins, result.PluginConfigs = dlls, plugins, configs
		a.finishScan(result, nil)
	default:
		result, scanErr := core.ScanAll(hub, cfg, nil, core.ScanScopeAll, force, 256, 82, plan, log)
		a.finishScan(result, scanErr)
	}
}

// finishScan 把扫描结果计数写进状态面。
func (a *App) finishScan(result *core.ScanResult, err error) {
	scanMu.Lock()
	defer scanMu.Unlock()
	if err != nil {
		scanState.Errors = append(scanState.Errors, err.Error())
	}
	if result == nil {
		return
	}
	scanState.Seen = result.Seen
	scanState.Added = result.Added
	scanState.Skipped = result.Skipped
	scanState.NonCard = result.NonCard
	scanState.NonMod = result.NonMod
	scanState.Failed = result.Failed
	scanState.Refs = result.RefEntries
	scanState.ThumbBytes = result.ThumbBytes
	scanState.NamesRead = result.NamesRead
	scanState.NamesFilled = result.NamesFilled
	scanState.TypesFixed = result.TypesFixed
	scanState.TimelineRead = result.TimelineRead
	scanState.PluginDlls = result.PluginDlls
	scanState.Plugins = result.Plugins
	scanState.PluginConfigs = result.PluginConfigs
	scanState.Removed = result.Removed
	scanState.StepCount = result.StepCount
	for _, e := range result.Errors {
		scanState.Errors = append(scanState.Errors, e)
	}
}

// loadScanPlan 读扫描步骤配置（order / on 两个设置键）；steps 显式给出时只留这些步骤。
func loadScanPlan(hub *store.Hub, steps string) *core.ScanPlan {
	order, _, _ := hub.Core().GetSetting("scan_plan_order")
	on, _, _ := hub.Core().GetSetting("scan_plan_on")
	plan := core.LoadScanPlan(order, on)
	if strings.TrimSpace(steps) == "" {
		return plan
	}
	keep := map[string]bool{}
	for _, raw := range strings.Split(steps, ",") {
		id := strings.TrimSpace(raw)
		if id != "" {
			keep[id] = true
		}
	}
	if plan == nil {
		return nil
	}
	for id := range plan.On {
		if !keep[id] {
			plan.On[id] = false
		}
	}
	return plan
}

// handleScanPlan 读扫描步骤配置（面板扫描配置窗：全集顺序 / 启用集合 / 步骤定义 / 组定义）。
func (a *App) handleScanPlan(w http.ResponseWriter, r *http.Request) {
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	plan := loadScanPlan(hub, "")
	steps := make([]map[string]interface{}, 0, 12)
	for _, d := range core.AllScanSteps() {
		steps = append(steps, map[string]interface{}{
			"id": d.Id, "name": d.Name, "mods": d.Mods, "required": d.Required,
			"defaultOn": d.DefaultOn, "grayed": d.Grayed, "note": d.Note,
			"group": nilIfEmpty(d.Group), "on": plan.IsOn(d.Id),
		})
	}
	groups := make([]map[string]interface{}, 0, 4)
	for _, g := range core.AllScanGroups() {
		ids := make([]string, 0, 4)
		for _, d := range core.ScanStepsOf(g.Id) {
			ids = append(ids, d.Id)
		}
		groups = append(groups, map[string]interface{}{
			"id": g.Id, "name": g.Name, "note": g.Note,
			"required": g.Required, "defaultOn": g.DefaultOn, "steps": ids,
		})
	}
	order := make([]string, 0, len(plan.Order))
	order = append(order, plan.Order...)
	// on = 启用集合全集（必选项恒在 + 用户显式勾选的可选项；灰置项不在）——对齐 C# plan.On
	enable := make([]string, 0, len(plan.Order))
	for _, id := range plan.Order {
		def := core.FindScanStep(id)
		if def == nil || def.Grayed {
			continue
		}
		if plan.IsOn(id) {
			enable = append(enable, id)
		}
	}
	writeJSON(w, map[string]interface{}{"order": order, "on": enable, "steps": steps, "groups": groups})
}

// handleWarmup 见 warmup.go（G10：进程级 warm keeper，后台建 -wal 并常驻连接）。
