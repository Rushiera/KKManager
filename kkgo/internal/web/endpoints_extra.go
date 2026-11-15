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
		notFound(w)
		return
	}
	st := core.ParseCardStructure(path)
	if st.ImageEnd <= 0 || st.ImageEnd > capBytes {
		notFound(w)
		return
	}
	f, openErr := os.Open(path)
	if openErr != nil {
		notFound(w)
		return
	}
	defer f.Close()
	buf := make([]byte, st.ImageEnd)
	if _, readErr := f.ReadAt(buf, 0); readErr != nil {
		notFound(w)
		return
	}
	w.Header().Set("Content-Type", "image/png")
	_, _ = w.Write(buf)
}

// handleCardRefs GET /api/card/{id}/refs?tier=&lib= 卡片声明的 mod 引用明细。
// 对齐 C# QueryCardRefs：裸数组（按 tier、guid 排序；同 guid 多部件聚合一行；无行 = 空数组）。
func (a *App) handleCardRefs(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	tier := atoiDefault(r.URL.Query().Get("tier"), 0)
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	s, libErr := hub.StoreByLib(lib)
	if libErr != nil || s == nil {
		writeErr(w, 404, "找不到库 "+strconv.Itoa(lib))
		return
	}
	rows, qErr := s.QueryCardRefs(id, tier)
	if qErr != nil {
		writeErr(w, 500, qErr.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, one := range rows {
		out = append(out, map[string]interface{}{
			"guid": one.Guid, "property": one.Property, "tier": one.Tier,
			"rootPath": one.RootPath, "filePath": one.FilePath, "fileName": one.FileName,
			"recName": one.RecName, "recAuthor": one.RecAuthor, "recWebsite": one.RecWebsite,
		})
	}
	writeJSON(w, out)
}

// handleCardStructure GET /api/card/{id}/structure?lib= 卡片文件结构（PNG 块表 + 数据区头部 + 分项分析）。
// 形状对齐 C# WebApp.cs：{ok, error, structure, detail, timeline, timelineText, coords, scene}。
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
		writeJSON(w, map[string]interface{}{"ok": false, "error": why})
		return
	}
	st := core.ParseCardStructure(path)
	var detailOut, coordsOut, sceneOut, timelineOut, timelineText interface{}
	if st.Error == "" && st.ImageEnd > 0 {
		detail := core.ParseCardDetail(path, st.ImageEnd)
		detailOut = core.CardDetailJSON(detail)
		if st.CardType == core.SceneCardType {
			tl := core.ReadTimeline(path, st.ImageEnd)
			timelineOut = core.TimelineInfoJSON(tl)
			if tl != nil {
				if text := core.DescribeTimeline(tl); text != "" {
					timelineText = text
				}
			}
			// 场景卡（sd）深度分析——逐份内嵌角色卡面图（与数据区图片清单同源）
			scene := core.ReadSceneInfo(path, st.ImageEnd)
			if scene != nil && detail != nil {
				scene.AttachCharaFaces(detail.Images)
			}
			sceneOut = core.CardSceneJSON(scene)
		} else {
			// 服装 / 饰品（Coordinate 块七套槽位）——人物卡才有；非人物卡由解析器给 Error
			coordsOut = core.CardCoordinateJSON(core.ReadCardCoordinate(path))
		}
	}
	writeJSON(w, map[string]interface{}{
		"ok": st.Error == "", "error": nilIfEmpty(st.Error),
		"structure":    cardStructureJSON(st),
		"detail":       detailOut,
		"timeline":     timelineOut,
		"timelineText": timelineText,
		"coords":       coordsOut,
		"scene":        sceneOut,
	})
}

// cardStructureJSON C# CardStructure 的序列化形状（PNG 块表 + 数据区头部字段）。
func cardStructureJSON(st *core.CardStructure) map[string]interface{} {
	chunks := make([]map[string]interface{}, 0, len(st.Chunks))
	for _, c := range st.Chunks {
		chunks = append(chunks, map[string]interface{}{
			"type": c.Type, "offset": c.Offset, "dataOffset": c.DataOffset,
			"length": c.Length, "end": c.End, "crcOk": c.CrcOk, "crcChecked": c.CrcChecked,
		})
	}
	warnings := append([]string{}, st.Warnings...)
	return map[string]interface{}{
		"filePath": st.FilePath, "fileName": st.FileName, "size": st.Size, "mtime": st.Mtime,
		"chunks": chunks, "imageEnd": st.ImageEnd, "dataSize": st.DataSize,
		"cardType": nilIfEmpty(st.CardType), "dataVersion": nilIfEmpty(st.DataVersion),
		"dataHeadHex": nilIfEmpty(st.DataHeadHex), "error": nilIfEmpty(st.Error),
		"warnings": warnings,
	}
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
	if st.Error != "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": st.Error})
		return
	}
	if st.CardType != core.SceneCardType {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "不是场景卡——只有 Studio 场景卡带 timeline 数据"})
		return
	}
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
	notFound(w)
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

// handleMissing GET /api/missing?per= 缺失清单（跨库合并，按被引用卡数倒序；每条附前 per 张引用卡片）。
func (a *App) handleMissing(w http.ResponseWriter, r *http.Request) {
	per := atoiDefault(r.URL.Query().Get("per"), 12)
	if per <= 0 {
		per = 12
	}
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
	for _, one := range list {
		samples, sErr := core.QueryCardsByModHub(hub, cfg, one.guid, 1, per)
		if sErr != nil {
			writeErr(w, 500, sErr.Error())
			return
		}
		sampleJSON := make([]map[string]interface{}, 0, len(samples))
		for i := range samples {
			sampleJSON = append(sampleJSON, cardRowJSON(&samples[i]))
		}
		out = append(out, map[string]interface{}{
			"guid": one.guid, "cards": one.cards, "samples": sampleJSON,
		})
	}
	writeJSON(w, map[string]interface{}{"total": len(list), "per": per, "items": out})
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
