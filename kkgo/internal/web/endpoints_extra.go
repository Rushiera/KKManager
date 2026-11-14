// 其余端点——补齐面板契约里剩下的读面与动作面。
//
// 覆盖：卡片源图 / 引用清单 / 结构 / timeline / 分析 · 非卡缩略图 · 待办增删 ·
// 设置保存 · 缺失清单 · 插件扫描触发。
package web

import (
	"encoding/json"
	"net/http"
	"os"
	"sort"
	"strconv"
	"time"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// handleCardSrc GET /api/card-src/{id}?lib= 直接把卡片图片区当卡面（缩略图未生成时的兜底）。
// 上限 12 MB（与 C# 一致）：超过则 404，由前端回落到缩略图。
func (a *App) handleCardSrc(w http.ResponseWriter, r *http.Request) {
	const capBytes = 12 * 1024 * 1024
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	path, _ := core.CardPathOf(hub, cfg, lib, id)
	if path == "" {
		http.NotFound(w, r)
		return
	}
	st := core.ParseCardStructure(path)
	if st.ImageEnd <= 0 || st.ImageEnd > capBytes {
		http.NotFound(w, r)
		return
	}
	f, openErr := os.Open(path)
	if openErr != nil {
		http.NotFound(w, r)
		return
	}
	defer f.Close()
	buf := make([]byte, st.ImageEnd)
	if _, readErr := f.ReadAt(buf, 0); readErr != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "image/png")
	_, _ = w.Write(buf)
}

// handleCardRefs GET /api/card/{id}/refs?lib= 卡片声明的 mod 清单 + 本地副本状态。
func (a *App) handleCardRefs(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	path, why := core.CardPathOf(hub, cfg, lib, id)
	if path == "" {
		writeErr(w, 404, why)
		return
	}
	info, parseErr := core.ParseCard(path)
	if parseErr != nil {
		writeErr(w, 500, parseErr.Error())
		return
	}
	guids := info.DistinctModIds()
	out := make([]map[string]interface{}, 0, len(guids))
	for _, g := range guids {
		recs, recErr := core.QueryModFilesHub(hub, cfg, g)
		row := map[string]interface{}{"guid": g, "copies": 0, "tier": 0, "fileName": ""}
		if recErr == nil && len(recs) > 0 {
			row["copies"] = len(recs)
			row["tier"] = recs[0].Tier
			row["fileName"] = recs[0].FileName
		}
		out = append(out, row)
	}
	writeJSON(w, map[string]interface{}{
		"cardType": info.CardType, "uarBlocks": info.UarBlocks,
		"refs": out, "count": len(out),
	})
}

// handleCardStructure GET /api/card/{id}/structure?lib= PNG 块表 + 数据区头段。
func (a *App) handleCardStructure(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	path, why := core.CardPathOf(hub, cfg, lib, id)
	if path == "" {
		writeErr(w, 404, why)
		return
	}
	st := core.ParseCardStructure(path)
	writeJSON(w, map[string]interface{}{
		"imageEnd":    st.ImageEnd,
		"dataSize":    st.DataSize,
		"cardType":    st.CardType,
		"dataVersion": st.DataVersion,
		"chunks":      st.Chunks,
		"dataHeadHex": st.DataHeadHex,
		"warnings":    st.Warnings,
	})
}

// handleCardTimeline GET /api/card/{id}/timeline?lib= 场景卡 timeline 摘要。
func (a *App) handleCardTimeline(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	path, why := core.CardPathOf(hub, cfg, lib, id)
	if path == "" {
		writeErr(w, 404, why)
		return
	}
	st := core.ParseCardStructure(path)
	tl := core.ReadTimeline(path, st.ImageEnd)
	if tl == nil || !tl.HasEntry {
		writeJSON(w, map[string]interface{}{"hasTimeline": false})
		return
	}
	writeJSON(w, map[string]interface{}{
		"hasTimeline": true, "duration": tl.Duration, "timeScale": tl.TimeScale,
		"keyframes": tl.Keyframes, "empty": tl.IsEmpty, "groups": tl.Groups,
		"groupNames": tl.GroupNames, "realSeconds": tl.RealSeconds,
		"maxKeyframeTime": tl.MaxKeyframeTime, "xmlLength": tl.XmlLength,
		"truncated": tl.Truncated, "error": tl.Error,
	})
}

// handleNonCardThumb GET /api/noncard/thumb?lib=&id= 非卡登记项的缩略图（无图 → 404）。
func (a *App) handleNonCardThumb(w http.ResponseWriter, r *http.Request) {
	http.NotFound(w, r)
}

// handleTodosClose POST /api/todos/close {id} 关闭一条待办。
func (a *App) handleTodosClose(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Id int64 `json:"id"`
	}
	if err := json.NewDecoder(r.Body).Decode(&dto); err != nil {
		writeErr(w, 400, "请求体无法解析")
		return
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	if err := hub.Core().CloseTodo(dto.Id); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleTodosAdd POST /api/todos/add {text} 新增一条手输待办。
func (a *App) handleTodosAdd(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Text string `json:"text"`
	}
	if err := json.NewDecoder(r.Body).Decode(&dto); err != nil {
		writeErr(w, 400, "请求体无法解析")
		return
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	why, addErr := hub.Core().AddManualTodo(dto.Text, store.Now())
	if addErr != nil {
		writeErr(w, 500, addErr.Error())
		return
	}
	if why != "" {
		writeErr(w, 400, why)
		return
	}
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleSettingsSave POST /api/settings 保存库设置（字段级合并写）。
func (a *App) handleSettingsSave(w http.ResponseWriter, r *http.Request) {
	var dto map[string]interface{}
	if err := json.NewDecoder(r.Body).Decode(&dto); err != nil {
		writeErr(w, 400, "请求体无法解析")
		return
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	for k, v := range dto {
		text := ""
		switch t := v.(type) {
		case string:
			text = t
		case float64:
			text = strconv.FormatInt(int64(t), 10)
		case bool:
			if t {
				text = "1"
			} else {
				text = "0"
			}
		default:
			continue
		}
		if setErr := hub.Core().SetSetting(k, text); setErr != nil {
			writeErr(w, 500, setErr.Error())
			return
		}
	}
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleMissing GET /api/missing?limit= 缺失清单（跨库合并，按被引用卡数倒序）。
func (a *App) handleMissing(w http.ResponseWriter, r *http.Request) {
	limit := atoiDefault(r.URL.Query().Get("limit"), 200)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	agg := map[string]int64{}
	for _, s := range core.AllStoreRefs(hub, cfg) {
		rows, rankErr := hub.MissingRankingOf(s.DB, 0)
		if rankErr != nil {
			continue
		}
		for _, one := range rows {
			agg[one.Guid] += one.Count
		}
	}
	type pair struct {
		guid  string
		cards int64
	}
	list := make([]pair, 0, len(agg))
	for k, v := range agg {
		list = append(list, pair{k, v})
	}
	sort.Slice(list, func(i, j int) bool {
		if list[i].cards != list[j].cards {
			return list[i].cards > list[j].cards
		}
		return list[i].guid < list[j].guid
	})
	out := make([]map[string]interface{}, 0, len(list))
	for i, one := range list {
		if limit > 0 && i >= limit {
			break
		}
		out = append(out, map[string]interface{}{"guid": one.guid, "cards": one.cards})
	}
	writeJSON(w, map[string]interface{}{"total": len(list), "rows": out})
}

// handlePluginsScan POST /api/plugins/scan 触发插件库扫描（后台，走同一扫描状态机）。
func (a *App) handlePluginsScan(w http.ResponseWriter, r *http.Request) {
	scanMu.Lock()
	if scanState.Running {
		scanMu.Unlock()
		writeJSON(w, map[string]interface{}{"ok": false, "error": "已有扫描在跑（同一时刻只允许一个）"})
		return
	}
	scanState.Running = true
	scanState.Target = "plugins"
	scanState.Message = "启动中"
	scanState.StartedAt = time.Now().Format("2006-01-02T15:04:05")
	scanState.FinishedAt = ""
	scanState.Errors = []string{}
	scanMu.Unlock()
	go a.runScan("plugins", false, "")
	writeJSON(w, map[string]interface{}{"ok": true})
}

// storeTodoRows 把待办行转 map（面板 JSON 形状）。
func storeTodoRows(rows []store.TodoRow) []map[string]interface{} {
	out := make([]map[string]interface{}, 0, len(rows))
	for _, t := range rows {
		out = append(out, map[string]interface{}{
			"id": t.Id, "kind": t.Kind, "text": t.Text,
			"cardPath": t.RefPath, "createdAt": t.CreatedAt,
		})
	}
	return out
}
