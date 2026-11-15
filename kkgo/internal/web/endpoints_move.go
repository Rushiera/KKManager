// 搬移端点——契约对齐 C# WebApp.cs §/api/move 与 §/api/move-unused/*。
//
// 两件：
//   - POST /api/move：把指定 guid 在 fromTier 的副本搬到 toTier（默认缓存库 → 主库），同步返回明细；
//   - POST /api/move-unused/start|stop + GET /api/move-unused/status：
//     「未引用 mod → 缓存库」后台任务（单条约数秒，页面关掉也继续跑，重开可见进度与结果）。
package web

import (
	"encoding/json"
	"net/http"
	"os"
	"strconv"
	"sync"
	"time"

	"kkgo/internal/core"
)

// MoveJobState 「未引用 mod 移到缓存库」任务状态（含最近一次结果）。
type MoveJobState struct {
	// Running 是否正在运行。
	Running bool
	// Message 当前阶段描述。
	Message string
	// Total 待搬总数。
	Total int
	// Done 已处理数。
	Done int
	// Moved 成功搬出数。
	Moved int
	// Skipped 跳过数（有原因）。
	Skipped int
	// Failed 失败数。
	Failed int
	// Current 正在处理的 guid。
	Current string
	// StartedAt 开始时刻。
	StartedAt string
	// FinishedAt 结束时刻。
	FinishedAt string
	// StopRequested 是否收到停止请求。
	StopRequested bool
	// Errors 失败明细（上限 50 条——失败必须可见）。
	Errors []string
}

// moveJobMu 保护 moveJob 的读写。
var moveJobMu sync.Mutex

// moveJob 「未引用 mod 移到缓存库」后台任务状态（进程级，含最近一次结果）。
var moveJob = &MoveJobState{}

// moveJobJSON 任务状态 → 面板 JSON（对齐 C# MoveJobState 的 camelCase 序列化；空串按 null）。
func moveJobJSON(job *MoveJobState) map[string]interface{} {
	errs := append([]string{}, job.Errors...)
	return map[string]interface{}{
		"running":   job.Running,
		"message":   nilIfEmpty(job.Message),
		"total":     job.Total,
		"done":      job.Done,
		"moved":     job.Moved,
		"skipped":   job.Skipped,
		"failed":    job.Failed,
		"current":   nilIfEmpty(job.Current),
		"startedAt": nilIfEmpty(job.StartedAt),
		// C# 的 FinishedAt 默认 null
		"finishedAt":    nilIfEmpty(job.FinishedAt),
		"stopRequested": job.StopRequested,
		"errors":        errs,
	}
}

// handleMove POST /api/move 把指定 guid 在 fromTier 的副本搬到 toTier（默认缓存库 → 主库）。
func (a *App) handleMove(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Guids    []string `json:"guids"`
		FromTier int      `json:"fromTier"`
		ToTier   int      `json:"toTier"`
	}
	_ = json.NewDecoder(r.Body).Decode(&dto)
	moved := make([]string, 0)
	skipped := make([]string, 0)
	errs := make([]string, 0)
	if len(dto.Guids) == 0 {
		errs = append(errs, "未指定要搬运的 mod")
		writeJSON(w, map[string]interface{}{"moved": moved, "skipped": skipped, "errors": errs})
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	toTier := dto.ToTier
	if toTier <= 0 {
		toTier = core.TierMain
	}
	fromTier := dto.FromTier
	if fromTier <= 0 {
		fromTier = core.TierCache
	}
	target := core.FindModRoot(cfg, toTier)
	if target == nil {
		errs = append(errs, "未配置级别 "+strconv.Itoa(toTier)+"（"+core.TierName(toTier)+"）的 mod 库根，无法搬运")
		writeJSON(w, map[string]interface{}{"moved": moved, "skipped": skipped, "errors": errs})
		return
	}
	if target.ReadOnly {
		errs = append(errs, "目标库根为只读："+target.Path)
		writeJSON(w, map[string]interface{}{"moved": moved, "skipped": skipped, "errors": errs})
		return
	}
	if mkErr := os.MkdirAll(target.Path, 0o755); mkErr != nil {
		errs = append(errs, "目标目录不可用："+mkErr.Error())
		writeJSON(w, map[string]interface{}{"moved": moved, "skipped": skipped, "errors": errs})
		return
	}
	for _, guid := range dto.Guids {
		code, detail := core.MoveOneMod(hub, cfg, guid, fromTier, *target, toTier)
		switch code {
		case core.MoveMoved:
			moved = append(moved, guid+" → "+detail)
		case core.MoveCopied:
			moved = append(moved, guid+"（复制）→ "+detail)
		case core.MoveSkipped:
			skipped = append(skipped, guid+"（"+detail+"）")
		default:
			errs = append(errs, guid+"："+detail)
		}
	}
	writeJSON(w, map[string]interface{}{"moved": moved, "skipped": skipped, "errors": errs})
}

// handleMoveUnusedStart POST /api/move-unused/start 启动「未引用 mod → 缓存库」后台任务。
func (a *App) handleMoveUnusedStart(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Guids []string `json:"guids"`
	}
	_ = json.NewDecoder(r.Body).Decode(&dto)
	moveJobMu.Lock()
	if moveJob.Running {
		moveJobMu.Unlock()
		writeErr(w, 409, "已有搬运任务在运行")
		return
	}
	job := &MoveJobState{Running: true, Message: "准备清单", StartedAt: time.Now().Format("15:04:05")}
	moveJob = job
	moveJobMu.Unlock()
	var only map[string]bool
	if len(dto.Guids) > 0 {
		only = make(map[string]bool, len(dto.Guids))
		for _, g := range dto.Guids {
			if g != "" {
				only[g] = true
			}
		}
	}
	go a.runMoveUnused(job, only)
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleMoveUnusedStop POST /api/move-unused/stop 请求停止（当前条目跑完即停）。
func (a *App) handleMoveUnusedStop(w http.ResponseWriter, r *http.Request) {
	moveJobMu.Lock()
	moveJob.StopRequested = true
	moveJobMu.Unlock()
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleMoveUnusedStatus GET /api/move-unused/status 任务状态（含最近一次结果）。
func (a *App) handleMoveUnusedStatus(w http.ResponseWriter, r *http.Request) {
	moveJobMu.Lock()
	out := moveJobJSON(moveJob)
	moveJobMu.Unlock()
	writeJSON(w, out)
}

// runMoveUnused 后台执行「未引用 mod → 缓存库」：清单由服务端按「未被引用 ∧ 主库副本」生成
// （字节数降序，先搬大的）；only 非空时只搬其中的 guid。
func (a *App) runMoveUnused(job *MoveJobState, only map[string]bool) {
	finish := func(msg string) {
		moveJobMu.Lock()
		job.Running = false
		job.Message = msg
		job.FinishedAt = time.Now().Format("15:04:05")
		moveJobMu.Unlock()
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		finish(err.Error())
		return
	}
	defer hub.Close()
	target := core.FindModRoot(cfg, core.TierCache)
	if target == nil {
		finish("未配置缓存库（级别 2）的 mod 库根——无法搬运")
		return
	}
	if target.ReadOnly {
		finish("目标库根为只读：" + target.Path)
		return
	}
	_ = os.MkdirAll(target.Path, 0o755)
	refs, refErr := core.RefGuidsHub(hub, cfg)
	if refErr != nil {
		finish(refErr.Error())
		return
	}
	sizes, sizeErr := hub.Core().MainModSizes()
	if sizeErr != nil {
		finish(sizeErr.Error())
		return
	}
	pool := make([]string, 0, len(sizes))
	for _, m := range sizes {
		if refs[m.Guid] {
			continue
		}
		if only != nil && !only[m.Guid] {
			continue
		}
		pool = append(pool, m.Guid)
	}
	moveJobMu.Lock()
	job.Total = len(pool)
	job.Message = "搬运中"
	moveJobMu.Unlock()
	for _, guid := range pool {
		moveJobMu.Lock()
		if job.StopRequested {
			moveJobMu.Unlock()
			break
		}
		job.Current = guid
		moveJobMu.Unlock()
		code, detail := core.MoveOneMod(hub, cfg, guid, core.TierMain, *target, core.TierCache)
		moveJobMu.Lock()
		job.Done++
		switch code {
		case core.MoveMoved, core.MoveCopied:
			job.Moved++
		case core.MoveSkipped:
			job.Skipped++
		default:
			job.Failed++
			if len(job.Errors) < 50 {
				job.Errors = append(job.Errors, guid+"："+detail)
			}
		}
		moveJobMu.Unlock()
	}
	moveJobMu.Lock()
	stopped := job.StopRequested
	moveJobMu.Unlock()
	if stopped {
		finish("已停止")
		return
	}
	finish("搬运完成")
}
