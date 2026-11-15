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
func (a *App) handleScan(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Target string `json:"target"`
		Force  bool   `json:"force"`
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
	go a.runScan(body.Target, body.Force, body.Steps)
	writeJSON(w, map[string]interface{}{"ok": true, "target": body.Target})
}

// runScan 后台执行扫描（mods / cards / all）。
func (a *App) runScan(target string, force bool, steps string) {
	defer func() {
		scanMu.Lock()
		scanState.Running = false
		scanState.Message = "完成"
		scanState.FinishedAt = time.Now().Format("2006-01-02T15:04:05")
		scanMu.Unlock()
	}()
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		scanMu.Lock()
		scanState.Errors = append(scanState.Errors, err.Error())
		scanMu.Unlock()
		return
	}
	defer hub.Close()
	plan := loadScanPlan(hub, steps)
	log := func(msg string) {
		scanMu.Lock()
		scanState.Message = msg
		scanMu.Unlock()
	}
	switch target {
	case "mods":
		result, scanErr := core.ScanMods(hub, cfg, nil, core.ScanScopePreset, force, plan, log)
		a.finishScan(result, scanErr)
	case "cards":
		result, scanErr := core.ScanCards(hub, cfg, nil, core.ScanScopePreset, force, 256, 82, plan, log)
		a.finishScan(result, scanErr)
	default:
		result, scanErr := core.ScanAll(hub, cfg, nil, core.ScanScopePreset, force, 256, 82, plan, log)
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

// handleWarmup 静默预热分片库（逐片打开并写一次，把 -wal 建出来）。
func (a *App) handleWarmup(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	stores := core.AllStoreRefs(hub, cfg)
	opened := 0
	for _, s := range stores {
		if s.DB == nil {
			continue
		}
		if _, execErr := hub.SnapshotOf(s.DB); execErr != nil {
			continue
		}
		opened++
	}
	writeJSON(w, map[string]interface{}{"ok": true, "warmed": opened})
}
