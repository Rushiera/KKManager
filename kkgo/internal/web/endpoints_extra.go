// 其余端点——补齐面板契约里剩下的读面与动作面。
//
// 覆盖：卡片源图 / 引用清单 / 结构 / timeline / 分析 · 非卡缩略图 · 待办增删 ·
// 设置保存 · 缺失清单 · 插件扫描触发。
package web

import (
	"encoding/json"
	"fmt"
	"math"
	"net/http"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
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
			tl := a.readTimelineCached(hub, lib, id, path, st.ImageEnd)
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

// handleCardTimeline GET /api/card/{id}/timeline?lib=&full= 场景卡 timeline 完整模型（只读）。
// 形状对齐 C# WebApp.cs TimelineSceneJson：组树 + 轨道 + 关键帧；full=1 附属性全集
// （后续编辑 / 导出复用同一份格式）。三条失败分支都返回 200 + {ok:false,error}（与 C# 一致）。
func (a *App) handleCardTimeline(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	full := atoiDefault(r.URL.Query().Get("full"), 0) != 0
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
	if st.Error != "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": st.Error})
		return
	}
	if st.CardType != core.SceneCardType {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "不是场景卡——只有 Studio 场景卡带 timeline 数据"})
		return
	}
	writeJSON(w, core.TimelineSceneJSON(readTimelineSceneCached(path, st.ImageEnd), full))
}

// timelineSceneCache 场景卡 timeline 模型的进程内缓存（键 = 路径 + 大小 + 修改时间；对齐 C# SceneCache）。
var (
	timelineSceneMu    sync.Mutex
	timelineSceneCache = map[string]*core.TimelineScene{}
)

// readTimelineSceneCached 取一张场景卡的完整 timeline 模型——命中缓存直接返回，未命中现场解析并回填。
// 失败或空时间轴不缓存（下次重试；与 C# 一致），容量超 4 条整表清空。
func readTimelineSceneCached(path string, imageEnd int64) *core.TimelineScene {
	key := path
	if fi, statErr := os.Stat(path); statErr == nil {
		key = path + "|" + strconv.FormatInt(fi.Size(), 10) + "|" + strconv.FormatInt(fi.ModTime().UnixNano(), 10)
	}
	timelineSceneMu.Lock()
	hit, ok := timelineSceneCache[key]
	timelineSceneMu.Unlock()
	if ok {
		return hit
	}
	scene := core.ReadTimelineScene(path, imageEnd)
	if scene.Error == "" && scene.HasEntry {
		timelineSceneMu.Lock()
		if len(timelineSceneCache) >= 4 {
			timelineSceneCache = map[string]*core.TimelineScene{}
		}
		timelineSceneCache[key] = scene
		timelineSceneMu.Unlock()
	}
	return scene
}

// readTimelineCached 取一张场景卡的 timeline 长度——库缓存优先（card_timeline 行 + size/mtime 双等），
// 未命中读盘一次并落表（读取失败不落表，下次重试）。对齐 C# WebApp.cs ReadTimelineCached：
// 命中行只带库内落下的字段——XmlAt / HitStage / Groups / GroupNames / MaxKeyframeTime 等现场字段
// 回落默认值（这是「命中缓存」与「现场解析」可观测的差别，照抄不补齐）。
func (a *App) readTimelineCached(hub *store.Hub, lib int, id int64, path string, imageEnd int64) *core.TimelineInfo {
	fi, statErr := os.Stat(path)
	if statErr != nil {
		return core.ReadTimeline(path, imageEnd)
	}
	size := fi.Size()
	mtime := store.StampOf(fi.ModTime())
	s, _ := hub.StoreByLib(lib)
	if s != nil {
		if row, err := s.GetCardTimeline(id); err == nil && row != nil && row.Size == size && row.Mtime == mtime {
			scale := row.TimeScale
			if scale <= 0 {
				scale = 1
			}
			return &core.TimelineInfo{
				Scanned: true, HasEntry: row.HasEntry, IsEmpty: row.IsEmpty,
				Duration: row.Duration, TimeScale: scale, Keyframes: row.Keyframes,
				XmlLength: row.XmlLength, RealSeconds: row.Duration / scale, Error: row.Error,
			}
		}
	}
	t := core.ReadTimeline(path, imageEnd)
	if t.Error == "" && s != nil {
		values := &store.TimelineValues{
			HasEntry: t.HasEntry, IsEmpty: t.IsEmpty, Duration: t.Duration, TimeScale: t.TimeScale,
			Keyframes: t.Keyframes, XmlLength: t.XmlLength, HitStage: t.HitStage, Error: t.Error,
		}
		_ = s.SaveCardTimeline(id, path, size, mtime, values)
	}
	return t
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

// handleSettingsSave POST /api/settings 保存库根配置（对齐 C# WebApp.cs §/api/settings）：
// 清外部锁定标记 → 消费「原行改地址」（旧库数据作废）→ 规范化 → 游戏根变更派生预置 → 还原预置条目 → 缓存库目录 → 落盘。
func (a *App) handleSettingsSave(w http.ResponseWriter, r *http.Request) {
	var cfg core.RootsConfig
	if err := json.NewDecoder(r.Body).Decode(&cfg); err != nil {
		writeErr(w, 400, "配置解析失败："+err.Error())
		return
	}
	hub, previous, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	if cfg.ModRoots == nil {
		cfg.ModRoots = []core.RootEntry{}
	}
	if cfg.CardRoots == nil {
		cfg.CardRoots = []core.RootEntry{}
	}
	warnings := make([]string, 0, 4)
	// 锁定标记只由规范按路径认定——请求带来的标记一律清掉，不信任外部输入
	core.ClearLockFlags(&cfg)
	// kkc 是独立形态：请求里的 kkc 条目（历史前端带回的残留）一律剥掉
	core.StripKkcRoots(&cfg, a.DBPath)
	// 原行改地址 = 用新地址完全替换原来的——旧库数据作废（删库文件 + 消解待办）；须在 Normalize 之前消费
	a.applyReplacedRoots(hub, &cfg)
	core.Normalize(&cfg)
	// previous 也剥掉运行态注入的 kkc 根——否则预置还原会把它们当「配置里的条目」补回
	core.StripKkcRoots(previous, a.DBPath)
	// 游戏根换了 = 预置条目按新根重派生（旧根的条目不再复活；缓存库槽位未被改过则跟随新根）
	firstModCache := core.ModCachePath(previous) == ""
	cacheMoved := false
	if !strings.EqualFold(strings.TrimSpace(previous.GameRoot), strings.TrimSpace(cfg.GameRoot)) {
		more, moved := core.ApplyGameRootChange(&cfg, previous.GameRoot)
		warnings = append(warnings, more...)
		cacheMoved = moved
		// RestorePresets 的基准里旧根的预置条目作废——否则会把它们当「不可删」补回来
		previous.ModRoots = dropLockedEntries(previous.ModRoots)
		previous.CardRoots = dropLockedEntries(previous.CardRoots)
	}
	// 预置主库不可删、不可改：请求里缺失的补回、改动的一律以磁盘现状为准（被拒的改动出声）
	warnings = append(warnings, core.RestorePresets(previous, &cfg)...)
	if firstModCache || cacheMoved {
		if cachePath := core.ModCachePath(&cfg); cachePath != "" {
			if mkErr := os.MkdirAll(cachePath, 0o755); mkErr != nil {
				warnings = append(warnings, "缓存库目录创建失败（"+cachePath+"）："+mkErr.Error())
			}
		}
	}
	if saveErr := a.saveRoots(hub, &cfg); saveErr != nil {
		writeErr(w, 500, saveErr.Error())
		return
	}
	fmt.Printf("[配置] 保存完成——游戏根 %s · mod 库根 %d 个 · 卡片库根 %d 个\n", cfg.GameRoot, len(cfg.ModRoots), len(cfg.CardRoots))
	if len(warnings) > 0 {
		fmt.Printf("[配置] 附 %d 条提示：\n", len(warnings))
		for _, one := range warnings {
			fmt.Println("  ! " + one)
		}
	}
	writeJSON(w, map[string]interface{}{"ok": true, "roots": &cfg, "warnings": warnings})
}

// dropLockedEntries 去掉锁定条目（游戏根变更时清理上次预置基准用）。
func dropLockedEntries(list []core.RootEntry) []core.RootEntry {
	out := make([]core.RootEntry, 0, len(list))
	for _, e := range list {
		if !e.Locked {
			out = append(out, e)
		}
	}
	return out
}

// applyReplacedRoots 消费「原行改地址」标记——同一行换了地址 = 用新地址完全替换原来的：旧库数据彻底删除（含待办）。
// 预置条目（路径不可改）与未变行直接清标记跳过（对齐 C# WebApp.cs ApplyReplacedRoots）。
func (a *App) applyReplacedRoots(hub *store.Hub, cfg *core.RootsConfig) {
	apply := func(list []core.RootEntry, isMods bool) {
		for i := range list {
			from := strings.TrimSpace(list[i].ReplacedFrom)
			list[i].ReplacedFrom = ""
			if from == "" {
				continue
			}
			if strings.EqualFold(from, strings.TrimSpace(list[i].Path)) {
				continue
			}
			if store.UsesCoreDB(list[i].Tier, list[i].Locked, isMods) {
				continue
			}
			a.warm.releaseLibs(hub, from)
			if hub.DropLibByPath(from) {
				_ = hub.Core().CloseTodoByKey("offline", strings.ToLower(from))
				fmt.Println("[换库] 原路径数据已作废：" + from + " → " + list[i].Path)
			}
		}
	}
	apply(cfg.ModRoots, true)
	apply(cfg.CardRoots, false)
}

// missingCache 缺失清单缓存（对齐 C# _missingCache：按 per 缓存，扫描结束后失效）。
var (
	missingMu    sync.Mutex
	missingCache map[int][]map[string]interface{}
)

// invalidateMissingCache 清空缺失清单缓存——扫描改变 mod / 卡片集合后调用（对齐 C# 扫描收尾置 null）。
func invalidateMissingCache() {
	missingMu.Lock()
	missingCache = nil
	missingMu.Unlock()
}

// handleMissing GET /api/missing?per= 缺失清单（跨库合并，按被引用卡数倒序；每条附前 per 张引用卡片）。
func (a *App) handleMissing(w http.ResponseWriter, r *http.Request) {
	per := atoiDefault(r.URL.Query().Get("per"), 12)
	if per <= 0 {
		per = 12
	}
	missingMu.Lock()
	cached := missingCache[per]
	missingMu.Unlock()
	if cached != nil {
		writeJSON(w, map[string]interface{}{"total": len(cached), "per": per, "items": cached})
		return
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
	guids := make([]string, 0, len(list))
	for _, one := range list {
		guids = append(guids, one.guid)
	}
	// 引用卡片一次批量取（对齐 C# BuildMissing 的 QueryCardsByMod 语义；逐 guid 查询是 N+1）
	samplesByGuid, _ := core.QueryMissingCardsHub(hub, cfg, guids, per)
	out := make([]map[string]interface{}, 0, len(list))
	for _, one := range list {
		sampleJSON := make([]map[string]interface{}, 0, per)
		cardsOfGuid := samplesByGuid[one.guid]
		for i := range cardsOfGuid {
			sampleJSON = append(sampleJSON, cardRowJSON(&cardsOfGuid[i]))
		}
		out = append(out, map[string]interface{}{
			"guid": one.guid, "cards": one.cards, "samples": sampleJSON,
		})
	}
	missingMu.Lock()
	if missingCache == nil {
		missingCache = map[int][]map[string]interface{}{}
	}
	missingCache[per] = out
	missingMu.Unlock()
	writeJSON(w, map[string]interface{}{"total": len(list), "per": per, "items": out})
}

// handlePluginsScan POST /api/plugins/scan 同步扫描插件库（只读解析 dll 元数据，不改动任何文件）。
// 对齐 C# WebApp.cs：回执给 dllTotal / seconds / plugins / nonPlugin / configs（前端直接显示）。
func (a *App) handlePluginsScan(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	started := time.Now()
	result := &core.ScanResult{}
	dlls, _, configs := core.ScanPlugins(hub.Core(), cfg, result, nil)
	seconds := time.Since(started).Seconds()
	rows, _ := hub.Core().LoadPlugins()
	withGuid := 0
	for _, row := range rows {
		if strings.TrimSpace(row.Guid) != "" {
			withGuid++
		}
	}
	fmt.Printf("[插件] 扫描完成——%d 个 dll · %.1f 秒 · 插件 %d 项 · 配置文件 %d 个\n", dlls, seconds, withGuid, configs)
	for _, e := range result.Errors {
		fmt.Println("  ! " + e)
	}
	writeJSON(w, map[string]interface{}{
		"ok": true, "dllTotal": dlls, "seconds": math.Round(seconds*10) / 10,
		"plugins": withGuid, "nonPlugin": len(rows) - withGuid, "configs": configs,
		"errors": result.Errors,
	})
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
