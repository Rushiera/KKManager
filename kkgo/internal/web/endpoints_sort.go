// 「按作者整理」端点——契约对齐 C# WebApp.cs §/api/sort/*。
//
// 长驻状态：job 放进程级变量（同一时刻只允许一个整理任务），页面关掉也继续跑。
// 库句柄每次任务自建自关（SQLite 连接不跨 goroutine 共享）。
package web

import (
	"encoding/json"
	"fmt"
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

// handleSortRoots 可整理的库根清单（含只读 / 离线根——是否可整理由 canSort 表达）。
func (a *App) handleSortRoots(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	list := make([]core.RootEntry, 0, len(cfg.ModRoots))
	for _, e := range cfg.ModRoots {
		if strings.TrimSpace(e.Path) == "" {
			continue
		}
		list = append(list, e)
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].Tier < list[j].Tier })
	out := make([]map[string]interface{}, 0, len(list))
	for _, e := range list {
		out = append(out, map[string]interface{}{
			"path": e.Path, "tier": e.Tier, "tierName": core.TierName(e.Tier),
			"readOnly": e.ReadOnly, "offline": e.Offline, "locked": e.Locked,
			"canSort": !e.ReadOnly && !e.Offline,
		})
	}
	writeJSON(w, map[string]interface{}{"ok": true, "roots": out})
}

// sortJobJSON 任务状态 → JSON。
func sortJobJSON(job *core.SortJobState) map[string]interface{} {
	errs := make([]string, 0, len(job.Errors))
	errs = append(errs, job.Errors...)
	return map[string]interface{}{
		"running": job.Running, "stopRequested": job.StopRequested,
		"phase": job.Phase, "message": job.Message,
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
	view := map[string]interface{}{
		"ok": false, "error": nil, "rechecked": false, "planId": int64(0),
		"scope": nil, "createdAt": nil, "state": nil, "note": nil,
		"itemCount": int64(0), "conflictCount": int64(0), "movedCount": int64(0),
		"pendingCount": int64(0), "failedCount": int64(0), "skippedCount": int64(0),
		"confirmed": false, "items": []map[string]interface{}{},
	}
	plan, err := hub.Core().LatestSortPlan()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if plan == nil {
		view["error"] = "还没有生成计划"
		writeJSON(w, view)
		return
	}
	rechecked := false
	if plan.State == "ready" {
		conflicts, reErr := core.RecheckPlan(hub, plan.ID)
		if reErr == nil {
			_ = hub.Core().FinishSortPlan(plan.ID, plan.State, plan.ItemCount, conflicts, plan.Note)
			if reread, rErr := hub.Core().SortPlanByID(plan.ID); rErr == nil && reread != nil {
				plan = reread
			}
			rechecked = true
		}
	}
	items, err := hub.Core().QuerySortPlanItems(plan.ID)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	confirmed, _, _ := hub.Core().GetSetting("sort_confirm_plan")
	rows := make([]map[string]interface{}, 0, len(items))
	var moved, conflict, failed, skipped, pending int64
	for _, it := range items {
		rows = append(rows, map[string]interface{}{
			"seq": it.Seq, "lib": it.Lib, "tier": it.Tier, "rootPath": it.RootPath,
			"guid": it.Guid, "author": it.Author, "folder": it.Folder,
			"fileName": filepath.Base(it.SrcPath),
			"srcPath":  it.SrcPath, "destPath": it.DestPath, "size": it.Size,
			"state": it.State, "note": nilIfEmpty(it.Note),
		})
		switch it.State {
		case core.SortStateMoved:
			moved++
		case core.SortStateConflict:
			conflict++
		case core.SortStateFailed:
			failed++
		case core.SortStateSkipped:
			skipped++
		default:
			pending++
		}
	}
	view["ok"] = true
	view["rechecked"] = rechecked
	view["planId"] = plan.ID
	view["scope"] = nilIfEmpty(plan.Scope)
	view["createdAt"] = nilIfEmpty(plan.CreatedAt)
	view["state"] = nilIfEmpty(plan.State)
	view["note"] = nilIfEmpty(plan.Note)
	view["itemCount"] = plan.ItemCount
	view["confirmed"] = confirmed == strconv.FormatInt(plan.ID, 10)
	view["items"] = rows
	view["movedCount"] = moved
	view["conflictCount"] = conflict
	view["failedCount"] = failed
	view["skippedCount"] = skipped
	view["pendingCount"] = pending
	writeJSON(w, view)
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

// handleSortPlanConfirm 勾「我已核对文件结构」——落 setting.sort_confirm_plan（值为计划 ID，对齐 C# ConfirmSortPlan）。
func (a *App) handleSortPlanConfirm(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Confirmed bool  `json:"confirmed"`
		PlanID    int64 `json:"planId"`
	}
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&dto)
	}
	if dto.PlanID <= 0 {
		writeErr(w, 409, "未指定计划")
		return
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	plan, perr := hub.Core().SortPlanByID(dto.PlanID)
	if perr != nil || plan == nil {
		writeErr(w, 409, "计划不存在（可能已被重新生成）")
		return
	}
	if dto.Confirmed {
		if setErr := hub.Core().SetSetting("sort_confirm_plan", strconv.FormatInt(plan.ID, 10)); setErr != nil {
			writeErr(w, 500, setErr.Error())
			return
		}
		fmt.Printf("[整理] 计划 #%d 已核对确认\n", plan.ID)
	} else {
		_ = hub.Core().DeleteSetting("sort_confirm_plan")
		fmt.Printf("[整理] 计划 #%d 确认已撤销\n", plan.ID)
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
	if dto.PlanID <= 0 {
		writeErr(w, 400, "未指定计划")
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
		plan, perr := hub.Core().SortPlanByID(planID)
		if perr != nil || plan == nil {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, "计划不存在（可能已被重新生成）")
			sortJobMu.Unlock()
			return
		}
		if plan.State != "ready" {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, "计划状态不是 ready（当前 "+plan.State+"）——重新生成计划")
			sortJobMu.Unlock()
			return
		}
		confirmed, _, _ := hub.Core().GetSetting("sort_confirm_plan")
		if confirmed != strconv.FormatInt(planID, 10) {
			sortJobMu.Lock()
			job.Running = false
			job.FinishedAt = sortNowStamp()
			job.Errors = append(job.Errors, "闸门[已确认]：先在计划窗勾选「我已核对文件结构」")
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
