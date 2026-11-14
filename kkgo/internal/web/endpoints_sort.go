// 「按作者整理」端点——契约对齐 C# WebApp.cs §/api/sort/*。
//
// 长驻状态：job 放进程级变量（同一时刻只允许一个整理任务），页面关掉也继续跑。
// 库句柄每次任务自建自关（SQLite 连接不跨 goroutine 共享）。
package web

import (
	"encoding/json"
	"net/http"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// sortJobMu 保护 sortJob 的读写。
var sortJobMu sync.Mutex

// sortJob 当前整理任务状态（进程级）。
var sortJob = &core.SortJobState{}

// sortNowStamp UTC 时间戳（与 store.StampLayout 同格式）。
func sortNowStamp() string {
	return time.Now().UTC().Format("2006-01-02T15:04:05.000")
}

// handleSortRoots 可整理的库根清单（只读根与离线根不进）。
func (a *App) handleSortRoots(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	_ = hub
	out := make([]map[string]interface{}, 0, 8)
	for _, e := range cfg.ModRoots {
		if e.ReadOnly || e.Offline {
			continue
		}
		out = append(out, map[string]interface{}{
			"path": e.Path, "tier": e.Tier, "recurse": e.Recurse,
		})
	}
	writeJSON(w, out)
}

// sortJobJSON 任务状态 → JSON。
func sortJobJSON(job *core.SortJobState) map[string]interface{} {
	errs := make([]string, 0, len(job.Errors))
	errs = append(errs, job.Errors...)
	return map[string]interface{}{
		"running": job.Running, "phase": job.Phase, "message": job.Message,
		"total": job.Total, "done": job.Done, "moved": job.Moved,
		"skipped": job.Skipped, "failed": job.Failed, "conflicts": job.Conflicts,
		"current": job.Current, "startedAt": job.StartedAt, "finishedAt": job.FinishedAt,
		"errors": errs, "planId": job.PlanID, "prunedDirs": job.PrunedDirs,
	}
}

// handleSortStatus 任务状态。
func (a *App) handleSortStatus(w http.ResponseWriter, r *http.Request) {
	sortJobMu.Lock()
	out := sortJobJSON(sortJob)
	sortJobMu.Unlock()
	writeJSON(w, out)
}

// handleSortStop 请求停止（当前条目跑完即停）。
func (a *App) handleSortStop(w http.ResponseWriter, r *http.Request) {
	sortJobMu.Lock()
	sortJob.StopRequested = true
	sortJobMu.Unlock()
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleSortPlan 读最新计划（做一次快照核对——已在快照位置的行标 √）。
func (a *App) handleSortPlan(w http.ResponseWriter, r *http.Request) {
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	plan, err := hub.Core().LatestSortPlan()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if plan == nil {
		writeJSON(w, map[string]interface{}{"exists": false})
		return
	}
	conflicts, reErr := core.RecheckPlan(hub, plan.ID)
	if reErr == nil {
		plan.ConflictCount = conflicts
	}
	items, err := hub.Core().QuerySortPlanItems(plan.ID)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	rows := make([]map[string]interface{}, 0, len(items))
	for _, it := range items {
		rows = append(rows, map[string]interface{}{
			"seq": it.Seq, "lib": it.Lib, "tier": it.Tier, "rootPath": it.RootPath,
			"guid": it.Guid, "author": it.Author, "folder": it.Folder,
			"srcPath": it.SrcPath, "destPath": it.DestPath, "size": it.Size,
			"mtime": it.Mtime, "state": it.State, "note": it.Note,
		})
	}
	writeJSON(w, map[string]interface{}{
		"exists": true, "id": plan.ID, "scope": plan.Scope, "createdAt": plan.CreatedAt,
		"itemCount": plan.ItemCount, "conflictCount": plan.ConflictCount,
		"state": plan.State, "note": plan.Note, "items": rows,
	})
}

// handleSortPlanStart 异步生成计划（body: {"roots":["..."]}；空 = 全部可整理库根）。
func (a *App) handleSortPlanStart(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Roots []string `json:"roots"`
	}
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&dto)
	}
	sortJobMu.Lock()
	if sortJob.Running {
		sortJobMu.Unlock()
		writeErr(w, 409, "已有整理任务在跑——先停止或等它结束")
		return
	}
	job := &core.SortJobState{Running: true, StartedAt: sortNowStamp(), Phase: "生成计划"}
	sortJob = job
	sortJobMu.Unlock()

	go func() {
		hub, cfg, err := openApp(a.DBPath)
		if err != nil {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, err.Error())
			sortJobMu.Unlock()
			return
		}
		defer hub.Close()
		roots := dto.Roots
		if len(roots) == 0 {
			for _, e := range cfg.ModRoots {
				if e.ReadOnly || e.Offline {
					continue
				}
				roots = append(roots, e.Path)
			}
		}
		err = core.BuildPlan(hub, cfg, roots, job)
		sortJobMu.Lock()
		job.Running = false
		job.FinishedAt = sortNowStamp()
		if err != nil {
			job.Errors = append(job.Errors, err.Error())
		}
		sortJobMu.Unlock()
	}()
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleSortPlanConfirm 勾「我已核对文件结构」——落 setting.sort_confirm_plan。
func (a *App) handleSortPlanConfirm(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Confirmed bool  `json:"confirmed"`
		PlanID    int64 `json:"planId"`
	}
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&dto)
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	val := "0"
	if dto.Confirmed {
		val = "1"
	}
	if err := hub.Core().SetSetting("sort_confirm_plan", val); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, map[string]interface{}{"ok": true, "confirmed": dto.Confirmed})
}

// handleSortExecStart 异步执行计划（三重闸门：已确认 / 冲突 0 / 游戏已关）。
func (a *App) handleSortExecStart(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		PlanID         int64 `json:"planId"`
		KoikatsuClosed bool  `json:"koikatsuClosed"`
	}
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&dto)
	}
	if !dto.KoikatsuClosed {
		writeErr(w, 400, "先在弹框里勾选「koikatsu 已关闭」")
		return
	}
	sortJobMu.Lock()
	if sortJob.Running {
		sortJobMu.Unlock()
		writeErr(w, 409, "已有整理任务在跑")
		return
	}
	job := &core.SortJobState{Running: true, StartedAt: sortNowStamp(), Phase: "搬运"}
	sortJob = job
	sortJobMu.Unlock()

	go func() {
		hub, cfg, err := openApp(a.DBPath)
		if err != nil {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, err.Error())
			sortJobMu.Unlock()
			return
		}
		defer hub.Close()
		planID := dto.PlanID
		if planID <= 0 {
			if plan, perr := hub.Core().LatestSortPlan(); perr == nil && plan != nil {
				planID = plan.ID
			}
		}
		confirmed, _, _ := hub.Core().GetSetting("sort_confirm_plan")
		if confirmed != "1" {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, "闸门[已确认]：先勾选「我已核对文件结构」")
			sortJobMu.Unlock()
			return
		}
		if conflicts, cerr := core.RecheckPlan(hub, planID); cerr == nil && conflicts > 0 {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, "闸门[冲突]："+strconv.FormatInt(conflicts, 10)+" 条未清零")
			sortJobMu.Unlock()
			return
		}
		err = core.ExecutePlan(hub, cfg, planID, job)
		sortJobMu.Lock()
		job.Running = false
		job.FinishedAt = sortNowStamp()
		if err != nil {
			job.Errors = append(job.Errors, err.Error())
		}
		sortJobMu.Unlock()
	}()
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleSortConflict 列出某一处整理冲突的候选副本（同目标路径的几条）。
// 查询参数：seq（任一候选的条目号）或 guid+dest。
func (a *App) handleSortConflict(w http.ResponseWriter, r *http.Request) {
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	plan, err := hub.Core().LatestSortPlan()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if plan == nil {
		writeErr(w, 404, "还没有整理计划")
		return
	}
	seq, _ := strconv.ParseInt(r.URL.Query().Get("seq"), 10, 64)
	items, err := hub.Core().QuerySortPlanItems(plan.ID)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	var target *store.SortPlanItemRow
	for i := range items {
		if items[i].Seq == seq {
			target = &items[i]
			break
		}
	}
	if target == nil {
		writeErr(w, 404, "计划里没有这个条目")
		return
	}
	group := make([]*store.SortPlanItemRow, 0, 4)
	for i := range items {
		if strings.EqualFold(items[i].DestPath, target.DestPath) {
			group = append(group, &items[i])
		}
	}
	rows := make([]map[string]interface{}, 0, len(group))
	for _, it := range group {
		rows = append(rows, map[string]interface{}{
			"seq": it.Seq, "tier": it.Tier, "rootPath": it.RootPath,
			"filePath": it.SrcPath, "fileName": filepath.Base(it.SrcPath),
			"size": it.Size, "mtime": it.Mtime, "state": it.State, "note": it.Note,
		})
	}
	sort.SliceStable(rows, func(i, j int) bool {
		return rows[i]["seq"].(int64) < rows[j]["seq"].(int64)
	})
	writeJSON(w, map[string]interface{}{
		"guid": target.Guid, "destPath": target.DestPath, "items": rows,
	})
}

// handleSortConflictKeep 保留这一份：其余同 guid 非旧版副本判旧版进缓存库。
func (a *App) handleSortConflictKeep(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Seq    int64 `json:"seq"`
		PlanID int64 `json:"planId"`
	}
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&dto)
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	planID := dto.PlanID
	if planID <= 0 {
		if plan, perr := hub.Core().LatestSortPlan(); perr == nil && plan != nil {
			planID = plan.ID
		}
	}
	detail, why := core.ResolveConflict(hub, cfg, planID, dto.Seq)
	if why != "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": why})
		return
	}
	conflicts, _ := core.RecheckPlan(hub, planID)
	writeJSON(w, map[string]interface{}{"ok": true, "detail": detail, "conflictCount": conflicts})
}
