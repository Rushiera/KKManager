// 本地面板（G7）——net/http + embed，端点契约对齐 C# Web/WebApp.cs。
//
// 设计：每请求打开库（SQLite WAL 打开便宜），长驻状态（扫描任务）放进程级变量。
// 前端 index.html 原样内嵌（design-kkm-go.md §一 第 4 条：一行不改）。
package web

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"math"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

//go:embed wwwroot/index.html
var indexHTML []byte

//go:embed wwwroot/plugin-notes.txt
var pluginNotesText string

// App 面板应用。
type App struct {
	// DBPath 主库路径。
	DBPath string
	// Version 面板版本串（右上角显示）。
	Version string
	// WebDir 前端目录（非空 → 每次请求从磁盘读 index.html / plugin-notes.txt，开发态热重载；空 → 用内嵌）。
	WebDir string
	// warm 进程级预热器（分片库 -wal 常驻；见 warmup.go）。
	warm *warmKeeper
}

// New 构造面板应用。
func New(dbPath, version string) *App {
	return &App{DBPath: dbPath, Version: version, warm: newWarmKeeper()}
}

// SetWebDir 设置前端目录——开发态热重载：改 index.html / plugin-notes.txt 后刷新即生效，不用重编。
// 空串 = 用内嵌资源（发布版行为）。
func (a *App) SetWebDir(dir string) {
	if a != nil {
		a.WebDir = strings.TrimSpace(dir)
	}
}

// Close 关闭面板长驻资源（预热器的常驻连接）——进程退出前调用。
func (a *App) Close() {
	if a != nil && a.warm != nil {
		a.warm.closeAll()
	}
}

// Handler 组装全部路由。
func (a *App) Handler() http.Handler {
	mux := http.NewServeMux()

	mux.HandleFunc("GET /", a.handleIndex)
	mux.HandleFunc("GET /api/settings", a.handleSettings)
	mux.HandleFunc("POST /api/edit-auth", a.handleEditAuth)
	mux.HandleFunc("GET /api/stats", a.handleStats)
	mux.HandleFunc("GET /api/cards", a.handleCards)
	mux.HandleFunc("GET /api/mods", a.handleMods)
	mux.HandleFunc("GET /api/folders", a.handleFolders)
	mux.HandleFunc("GET /api/authors", a.handleAuthors)
	mux.HandleFunc("GET /api/todos", a.handleTodos)
	mux.HandleFunc("GET /api/noncards", a.handleNonCards)
	mux.HandleFunc("GET /api/dup", a.handleDup)
	mux.HandleFunc("GET /api/scan/status", a.handleScanStatus)
	mux.HandleFunc("GET /api/plugins/notes", a.handlePluginNotes)
	mux.HandleFunc("GET /api/card/{id}", a.handleCard)
	mux.HandleFunc("GET /api/card/{id}/params", a.handleCardParams)
	mux.HandleFunc("POST /api/card/{id}/params", a.handleCardParamsSave)
	mux.HandleFunc("GET /api/card/{id}/archives", a.handleCardArchives)
	mux.HandleFunc("GET /api/personalities", a.handlePersonalities)
	mux.HandleFunc("GET /api/weak-points", a.handleWeakPoints)
	mux.HandleFunc("GET /api/thumb/{id}", a.handleCardThumb)

	mux.HandleFunc("POST /api/scan", a.handleScan)
	mux.HandleFunc("GET /api/scan/plan", a.handleScanPlan)
	mux.HandleFunc("POST /api/warmup", a.handleWarmup)

	mux.HandleFunc("GET /api/mod/{guid}/cards", a.handleModCards)
	mux.HandleFunc("GET /api/mod/{guid}/files", a.handleModFiles)
	mux.HandleFunc("GET /api/mod/{guid}/composition", a.handleModComposition)
	mux.HandleFunc("GET /api/mod/{guid}/entry", a.handleModEntry)
	mux.HandleFunc("POST /api/mod/mark-old", a.handleModOldOp("mark-old"))
	mux.HandleFunc("POST /api/mod/keep-version", a.handleModOldOp("keep-version"))
	mux.HandleFunc("POST /api/mod/swap", a.handleModOldOp("swap"))
	mux.HandleFunc("POST /api/mod/promote", a.handleModOldOp("promote"))
	mux.HandleFunc("POST /api/mod/u3d", a.handleModU3d)
	mux.HandleFunc("GET /api/mod/{guid}/u3d", a.handleModU3dLoad)
	mux.HandleFunc("GET /api/mod/{guid}/u3d-tex", a.handleModU3dTexture)

	mux.HandleFunc("POST /api/reveal", a.handleReveal)
	mux.HandleFunc("GET /api/browse-folder", a.handleBrowseFolder)

	mux.HandleFunc("GET /api/sort/roots", a.handleSortRoots)
	mux.HandleFunc("GET /api/sort/status", a.handleSortStatus)
	mux.HandleFunc("GET /api/sort/plan", a.handleSortPlan)
	mux.HandleFunc("POST /api/sort/plan/start", a.handleSortPlanStart)
	mux.HandleFunc("POST /api/sort/plan/confirm", a.handleSortPlanConfirm)
	mux.HandleFunc("POST /api/sort/exec/start", a.handleSortExecStart)
	mux.HandleFunc("POST /api/sort/stop", a.handleSortStop)
	mux.HandleFunc("GET /api/sort/conflict", a.handleSortConflict)
	mux.HandleFunc("POST /api/sort/conflict/keep", a.handleSortConflictKeep)

	mux.HandleFunc("GET /api/plugins", a.handlePlugins)
	mux.HandleFunc("GET /api/plugins/match", a.handlePluginsMatch)
	mux.HandleFunc("GET /api/plugins/log", a.handlePluginsLog)
	mux.HandleFunc("GET /api/plugin/detail", a.handlePluginDetail)
	mux.HandleFunc("POST /api/plugin/config", a.handlePluginConfigSave)

	mux.HandleFunc("GET /api/card-src/{id}", a.handleCardSrc)
	mux.HandleFunc("GET /api/card/{id}/refs", a.handleCardRefs)
	mux.HandleFunc("GET /api/card/{id}/structure", a.handleCardStructure)
	mux.HandleFunc("GET /api/card/{id}/timeline", a.handleCardTimeline)
	mux.HandleFunc("GET /api/noncard/thumb/{id}", a.handleNonCardThumbByID)
	mux.HandleFunc("POST /api/todos/close", a.handleTodosClose)
	mux.HandleFunc("POST /api/todos/add", a.handleTodosAdd)
	mux.HandleFunc("POST /api/settings", a.handleSettingsSave)
	mux.HandleFunc("GET /api/missing", a.handleMissing)
	mux.HandleFunc("POST /api/plugins/scan", a.handlePluginsScan)

	// G7 缺口端点（交接件 §三-2）：库根推荐 / 离线 / 彻底删除 · 非卡搬移与缩略图 ·
	// composition 强制重读 · u3dmap / u3dtex · 卡片内嵌图片
	mux.HandleFunc("GET /api/roots/recommend", a.handleRootsRecommend)
	mux.HandleFunc("POST /api/roots/offline", a.handleRootsOffline)
	mux.HandleFunc("POST /api/roots/purge", a.handleRootsPurge)
	mux.HandleFunc("POST /api/noncard/move", a.handleNonCardMove)
	mux.HandleFunc("POST /api/mod/composition", a.handleModCompositionSave)
	mux.HandleFunc("GET /api/mod/{guid}/u3dmap", a.handleModU3dMap)
	mux.HandleFunc("GET /api/mod/{guid}/u3dtex", a.handleModU3dTex)
	mux.HandleFunc("GET /api/card/{id}/image", a.handleCardImage)

	// 搬移端点（交接件 §三-2）：重复副本「正名搬入主库」/ 未引用 mod 一键移到缓存库
	mux.HandleFunc("POST /api/move", a.handleMove)
	mux.HandleFunc("POST /api/move-unused/start", a.handleMoveUnusedStart)
	mux.HandleFunc("POST /api/move-unused/stop", a.handleMoveUnusedStop)
	mux.HandleFunc("GET /api/move-unused/status", a.handleMoveUnusedStatus)
	return mux
}

// Serve 起面板（阻塞到进程退出）。
func (a *App) Serve(port int, openBrowser bool) error {
	addr := "127.0.0.1:" + strconv.Itoa(port)
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("端口 %d 不可用：%w", port, err)
	}
	url := "http://" + addr
	fmt.Println("面板: " + url)
	if openBrowser {
		openURL(url)
	}
	srv := &http.Server{Handler: a.Handler()}
	return srv.Serve(ln)
}

// openApp 打开主库并读库根配置。
func openApp(dbPath string) (*store.Hub, *core.RootsConfig, error) {
	hub, err := store.OpenHub(dbPath)
	if err != nil {
		return nil, nil, err
	}
	text, _, err := hub.Core().GetSetting("roots")
	if err != nil {
		hub.Close()
		return nil, nil, err
	}
	cfg := core.ParseRootsConfig(text)
	core.Normalize(cfg)
	return hub, cfg, nil
}

// writeJSON 输出 JSON（camelCase 形状由调用方组装）。
func writeJSON(w http.ResponseWriter, v interface{}) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(v)
}

// writeErr 输出错误（面板按 {ok:false,error} 读）。
func writeErr(w http.ResponseWriter, code int, msg string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(map[string]interface{}{"ok": false, "error": msg})
}

// notFound 404 空 body——对齐 C# Results.NotFound()（C# 面板的 404 不带文本）。
func notFound(w http.ResponseWriter) {
	w.WriteHeader(http.StatusNotFound)
}

// handleIndex 首页（单页应用）。
func (a *App) handleIndex(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" && r.URL.Path != "/index.html" {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if a.WebDir != "" {
		// 开发态热重载：每次请求读磁盘（改前端刷新即见）；读不到回落内嵌。
		if body, err := os.ReadFile(filepath.Join(a.WebDir, "index.html")); err == nil {
			_, _ = w.Write(body)
			return
		}
	}
	_, _ = w.Write(indexHTML)
}

// handlePluginNotes 插件中文说明表（非空 WebDir 时读磁盘热生效；否则用内嵌资源，离线可用）。
func (a *App) handlePluginNotes(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	if a.WebDir != "" {
		if body, err := os.ReadFile(filepath.Join(a.WebDir, "plugin-notes.txt")); err == nil {
			_, _ = w.Write(body)
			return
		}
	}
	_, _ = w.Write([]byte(pluginNotesText))
}

// handleSettings 库根配置 + 版本 + 编辑授权。
func (a *App) handleSettings(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	writeJSON(w, map[string]interface{}{
		"roots":          cfg,
		"dbPath":         hub.Core().Path(),
		"version":        a.Version,
		"editAuthorized": editAuthorized(hub),
	})
}

// editAuthorized 读编辑授权开关（默认不授权）。
func editAuthorized(hub *store.Hub) bool {
	v, _, err := hub.Core().GetSetting("edit_authorized")
	return err == nil && v == "1"
}

// handleEditAuth 编辑授权开关落盘。
func (a *App) handleEditAuth(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Granted bool `json:"granted"`
	}
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(&dto); err != nil && err.Error() != "EOF" {
		// 宽容：未知字段不挡（前端可能多传），只要求 granted 可解析
		_ = err
	}
	hub, _, hubErr := openApp(a.DBPath)
	if hubErr != nil {
		writeErr(w, 500, hubErr.Error())
		return
	}
	defer hub.Close()
	val := "0"
	if dto.Granted {
		val = "1"
	}
	if err := hub.Core().SetSetting("edit_authorized", val); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, map[string]interface{}{"ok": true, "editAuthorized": dto.Granted})
}

// handleStats 统计卡（卡片 / mod / 副本 / 四色 / 缺失排行）。
func (a *App) handleStats(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	merged := &store.Snapshot{}
	stores := core.AllStoreRefs(hub, cfg)
	coreSnap, err := hub.SnapshotOf(hub.Core())
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	merged.Mods, merged.ModsTier1, merged.ModsTier2, merged.ModsTier3 = coreSnap.Mods, coreSnap.ModsTier1, coreSnap.ModsTier2, coreSnap.ModsTier3
	merged.DupMods = coreSnap.DupMods
	var modFiles int64
	refSet := map[string]bool{}
	for _, ref := range stores {
		snap, snapErr := hub.SnapshotOf(ref.DB)
		if snapErr != nil {
			writeErr(w, 500, snapErr.Error())
			return
		}
		merged.Cards += snap.Cards
		merged.CardsReady += snap.CardsReady
		merged.CardsWithRefs += snap.CardsWithRefs
		merged.Refs += snap.Refs
		merged.Green += snap.Green
		merged.Yellow += snap.Yellow
		merged.Red += snap.Red
		merged.Black += snap.Black
		n, countErr := ref.DB.CountModFiles()
		if countErr != nil {
			writeErr(w, 500, countErr.Error())
			return
		}
		modFiles += n
		guids, guidErr := ref.DB.DistinctRefGuids()
		if guidErr != nil {
			writeErr(w, 500, guidErr.Error())
			return
		}
		for _, g := range guids {
			refSet[g] = true
		}
	}
	merged.ModFiles = modFiles
	sizes, sizeErr := hub.Core().MainModSizes()
	if sizeErr != nil {
		writeErr(w, 500, sizeErr.Error())
		return
	}
	var unused int64
	for _, m := range sizes {
		if !refSet[m.Guid] {
			unused++
		}
	}
	merged.UnusedMods = unused
	dupGroups, dupPending, dupErr := core.DupCountsHub(hub, cfg)
	if dupErr != nil {
		writeErr(w, 500, dupErr.Error())
		return
	}
	nonCards, nonErr := core.CountNonCardsHub(hub, cfg, false)
	if nonErr != nil {
		writeErr(w, 500, nonErr.Error())
		return
	}
	todos, todoErr := hub.Core().ListTodoRows()
	if todoErr != nil {
		writeErr(w, 500, todoErr.Error())
		return
	}
	missing := make([]map[string]interface{}, 0, 50)
	aggMiss := map[string]int64{}
	for _, ref := range stores {
		rank, missErr := hub.MissingRankingOf(ref.DB, 0)
		if missErr != nil {
			continue
		}
		for _, one := range rank {
			aggMiss[one.Guid] += one.Count
		}
	}
	type missPair struct {
		guid  string
		cards int64
	}
	missList := make([]missPair, 0, len(aggMiss))
	for k, v := range aggMiss {
		missList = append(missList, missPair{k, v})
	}
	sort.Slice(missList, func(i, j int) bool {
		if missList[i].cards != missList[j].cards {
			return missList[i].cards > missList[j].cards
		}
		return missList[i].guid < missList[j].guid
	})
	for i, one := range missList {
		if i >= 50 {
			break
		}
		missing = append(missing, map[string]interface{}{"guid": one.guid, "cards": one.cards})
	}
	writeJSON(w, map[string]interface{}{
		"cards":         merged.Cards,
		"mods":          merged.Mods,
		"modFiles":      merged.ModFiles,
		"refs":          merged.Refs,
		"modsTier1":     merged.ModsTier1,
		"modsTier2":     merged.ModsTier2,
		"modsTier3":     merged.ModsTier3,
		"cardsReady":    merged.CardsReady,
		"cardsWithRefs": merged.CardsWithRefs,
		"green":         merged.Green,
		"yellow":        merged.Yellow,
		"red":           merged.Red,
		"black":         merged.Black,
		"pending":       merged.Yellow + merged.Red + merged.Black,
		"unusedMods":    merged.UnusedMods,
		"dupMods":       merged.DupMods,
		"dupGroups":     dupGroups,
		"dupPending":    dupPending,
		"nonCards":      nonCards,
		"todos":         len(todos),
		"missing":       missing,
	})
}

// handleCards 卡片列表（排序 / 筛选 / 分页）。
func (a *App) handleCards(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	order := q.Get("order")
	// C# 侧不校验排序键（未知键按默认），Go 不做白名单拒绝——保持端点契约一致
	page := atoiDefault(q.Get("page"), 1)
	size := atoiDefault(q.Get("size"), 0)
	if size <= 0 {
		size = 1 << 30
	}
	desc := true
	if v := q.Get("desc"); v != "" {
		desc = v != "false" && v != "0"
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	var folderPtr *string
	if v := q.Get("folder"); v != "" {
		folderPtr = &v
	}
	rows, err := core.QueryCardsHub(hub, cfg, page, size, q.Get("filter"), q.Get("q"), folderPtr, q.Get("root"), order, desc)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, c := range rows {
		out = append(out, cardJSON(c))
	}
	writeJSON(w, out)
}

// cardJSON 卡片行的 JSON 形状（camelCase，对齐 C# CardRow 序列化）。
func cardJSON(c store.CardListRow) map[string]interface{} {
	m := map[string]interface{}{
		"id":       c.Id,
		"fileName": c.FileName,
		"size":     c.Size,
		"tier":     c.Tier,
		"folder":   c.Folder,
		"modCount": c.ModCount,
		"green":    c.Green,
		"yellow":   c.Yellow,
		"red":      c.Red,
		"black":    c.Black,
		"hasThumb": c.HasThumb,
		"lib":      c.Lib,
	}
	if c.CardTypeSet {
		m["cardType"] = c.CardType
	} else {
		m["cardType"] = nil
	}
	if c.MtimeSet {
		m["mtime"] = c.Mtime
	} else {
		m["mtime"] = nil
	}
	if c.RootPathSet {
		m["rootPath"] = c.RootPath
	} else {
		m["rootPath"] = nil
	}
	if c.CharaNameSet {
		m["charaName"] = c.CharaName
	} else {
		m["charaName"] = nil
	}
	if c.TimelineSet {
		m["timelineSeconds"] = c.TimelineSeconds
	} else {
		m["timelineSeconds"] = nil
	}
	return m
}

// handleMods mod 列表。
func (a *App) handleMods(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	page := atoiDefault(q.Get("page"), 1)
	size := atoiDefault(q.Get("size"), 0)
	if size <= 0 {
		size = 1 << 30
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.QueryModsHub(hub, cfg, page, size, q.Get("filter"), q.Get("q"))
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, m := range rows {
		out = append(out, map[string]interface{}{
			"guid": m.Guid, "name": m.Name, "author": m.Author, "version": m.Version,
			"tier": m.Tier, "rootPath": m.RootPath, "fileName": m.FileName, "filePath": m.FilePath,
			"size": m.Size, "dupCount": m.DupCount, "used": m.Used,
			"usedChara": m.UsedChara, "usedClothes": m.UsedClothes, "usedSd": m.UsedSd,
			"hasOld": m.HasOld, "oldName": oldNameOrNil(m.OldName),
		})
	}
	writeJSON(w, out)
}

// handleFolders 卡片文件夹清单。
func (a *App) handleFolders(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.QueryFoldersHub(hub, cfg)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, f := range rows {
		out = append(out, map[string]interface{}{
			"rootPath": f.RootPath, "folder": f.Folder,
			"count": f.Count, "ready": f.Ready, "black": f.Black,
		})
	}
	writeJSON(w, out)
}

// handleAuthors 作者清单（按 mod 数倒序）。
func (a *App) handleAuthors(w http.ResponseWriter, r *http.Request) {
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.QueryAuthorsHub(hub)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, ar := range rows {
		out = append(out, map[string]interface{}{"author": ar.Author, "count": ar.Count})
	}
	writeJSON(w, out)
}

// handleTodos 软件内待办清单。
func (a *App) handleTodos(w http.ResponseWriter, r *http.Request) {
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := hub.Core().ListTodoRows()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, t := range rows {
		out = append(out, map[string]interface{}{
			"id": t.Id, "kind": t.Kind, "text": t.Text,
			"cardPath": t.RefPath, "createdAt": t.CreatedAt,
		})
	}
	// C# /api/todos 是 {count, items} 两键包（不是裸数组）
	writeJSON(w, map[string]interface{}{"count": len(out), "items": out})
}

// oldNameOrNil 旧版名——空串按 null 输出（对齐 C# 可空字段的序列化）。
func oldNameOrNil(name string) interface{} {
	if name == "" {
		return nil
	}
	return name
}

// handleNonCards 非卡 / 非 mod 清单。
func (a *App) handleNonCards(w http.ResponseWriter, r *http.Request) {
	includeMoved := r.URL.Query().Get("moved") == "1"
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.ListNonCardsHub(hub, cfg, includeMoved)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, n := range rows {
		// 项键对齐 C# NonCardRow 序列化：path / root（不是 filePath / rootPath）
		out = append(out, map[string]interface{}{
			"id": n.Id, "lib": n.Lib, "path": n.FilePath, "root": n.RootPath, "side": n.Side,
			"reason": n.Reason, "size": n.Size, "mtime": n.Mtime,
			"hasThumb": n.HasThumb, "movedTo": n.MovedTo,
		})
	}
	pending, _ := core.CountNonCardsHub(hub, cfg, false)
	all, _ := core.CountNonCardsHub(hub, cfg, true)
	// C# /api/noncards 外层是 {items, total, pending, movedCount}
	writeJSON(w, map[string]interface{}{
		"items": out, "total": len(out), "pending": pending, "movedCount": all - pending,
	})
}

// handleDup 重复副本整表（组 + 副本 + 引用卡片 + 旧版登记 + MD5 档案补齐）。
func (a *App) handleDup(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	groups, err := core.ListDuplicateGroupsHub(hub, cfg)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	guids := make([]string, 0, len(groups))
	allFiles := make([]store.ModFileRec, 0, len(groups)*2)
	for _, g := range groups {
		guids = append(guids, g.Guid)
		allFiles = append(allFiles, g.Files...)
	}
	refs, _ := core.QueryDupCardRefsHub(hub, cfg, guids, 3)
	// MD5 只算冲突文件（重复副本组内），且只在打开本面板时补齐一次——算过就忽略（档案落主库 mod_hash）
	hashes, _, hashErrors, hashErr := core.FillHashesHub(hub, allFiles)
	if hashErr != nil {
		hashErrors = append(hashErrors, hashErr.Error())
	}
	if hashErrors == nil {
		hashErrors = []string{}
	}
	items := make([]map[string]interface{}, 0, len(groups))
	for _, g := range groups {
		files := make([]map[string]interface{}, 0, len(g.Files))
		for _, f := range g.Files {
			files = append(files, modCopyJSON(f, hashes))
		}
		cards := make([]map[string]interface{}, 0)
		var cardTotal int64
		if ref := refs[g.Guid]; ref != nil {
			cardTotal = ref.Total
			for _, c := range ref.Top {
				cards = append(cards, map[string]interface{}{
					"id": c.Id, "lib": c.Lib, "hasThumb": c.HasThumb, "fileName": c.FileName,
				})
			}
		}
		items = append(items, map[string]interface{}{
			"guid": g.Guid, "mainCount": g.MainCount, "files": files,
			"cards": cards, "cardTotal": cardTotal,
		})
	}
	olds := make([]map[string]interface{}, 0)
	for _, o := range core.ListModOldSorted(hub) {
		olds = append(olds, map[string]interface{}{
			"oldPath": o.OldPath, "guid": o.Guid, "oldName": o.OldName,
			"oldVersion": o.OldVersion, "newName": o.NewName, "newVersion": o.NewVersion,
			"markedAt": o.MarkedAt,
		})
	}
	writeJSON(w, map[string]interface{}{
		"total": len(items), "items": items, "olds": olds, "hashErrors": hashErrors,
	})
}

// modCopyJSON 一份 mod 副本的展示行（重复副本窗口与整理冲突小窗共用同一形状）。
func modCopyJSON(f store.ModFileRec, hashes map[string]string) map[string]interface{} {
	info := core.ReadModCopy(f.FilePath)
	out := map[string]interface{}{
		"tier": f.Tier, "tierName": core.TierName(f.Tier), "rootPath": f.RootPath,
		"filePath": f.FilePath, "fileName": f.FileName, "size": f.Size, "mtime": f.Mtime,
		"ctime":   info.Ctime,
		"version": nilIfEmpty(info.Version), "author": nilIfEmpty(info.Author),
		"name": nilIfEmpty(info.Name), "isOld": core.IsOldFileName(f.FileName),
		"error": nilIfEmpty(info.Error),
	}
	if hashes != nil {
		if md5, ok := hashes[f.FilePath]; ok {
			out["md5"] = md5
		} else {
			out["md5"] = nil
		}
	} else {
		out["md5"] = nil
	}
	return out
}

// ScanState 扫描任务状态（同一时刻只允许一个；页面关掉也继续跑）。
type ScanState struct {
	// Running 是否正在运行。
	Running bool `json:"running"`
	// Target 扫描目标（mods / cards）。
	Target string `json:"target"`
	// Message 当前进度描述。
	Message string `json:"message"`
	// CurrentRoot 当前库根。
	CurrentRoot string `json:"currentRoot"`
	// CurrentTier 当前级别。
	CurrentTier int `json:"currentTier"`
	// RootIndex 库根序号。
	RootIndex int `json:"rootIndex"`
	// RootCount 库根总数。
	RootCount int `json:"rootCount"`
	// Seen 已枚举数。
	Seen int `json:"seen"`
	// Added 已入库数。
	Added int `json:"added"`
	// Skipped 跳过数。
	Skipped int `json:"skipped"`
	// NonCard 非卡数。
	NonCard int `json:"nonCard"`
	// NonMod 非 mod 数。
	NonMod int `json:"nonMod"`
	// Failed 失败数。
	Failed int `json:"failed"`
	// Refs 引用条目数。
	Refs int64 `json:"refs"`
	// ThumbBytes 缩略图字节数。
	ThumbBytes int64 `json:"thumbBytes"`
	// NamesRead 新读到角色名的卡片数。
	NamesRead int `json:"namesRead"`
	// NamesFilled 存量补名数。
	NamesFilled int `json:"namesFilled"`
	// TypesFixed 卡类型补正数。
	TypesFixed int `json:"typesFixed"`
	// TimelineRead timeline 读取数。
	TimelineRead int `json:"timelineRead"`
	// PluginDone 插件段已完成 dll 数。
	PluginDone int `json:"pluginDone"`
	// PluginTotal 插件段 dll 总数。
	PluginTotal int `json:"pluginTotal"`
	// PluginDlls 枚举到的 dll 数。
	PluginDlls int `json:"pluginDlls"`
	// Plugins 落库插件项数。
	Plugins int `json:"plugins"`
	// PluginConfigs 读到的 cfg 数。
	PluginConfigs int `json:"pluginConfigs"`
	// Removed 清理的已消失记录数。
	Removed int `json:"removed"`
	// StepIndex 当前步骤序号。
	StepIndex int `json:"stepIndex"`
	// StepCount 步骤总数。
	StepCount int `json:"stepCount"`
	// StepName 当前步骤名。
	StepName string `json:"stepName"`
	// StepDone 步骤内已处理数。
	StepDone int `json:"stepDone"`
	// StepTotal 步骤内总数。
	StepTotal int `json:"stepTotal"`
	// StartedAt 开始时刻。
	StartedAt string `json:"startedAt"`
	// FinishedAt 结束时刻。
	FinishedAt string `json:"finishedAt"`
	// Errors 错误摘要（失败必须可见）。
	Errors []string `json:"errors"`
}

// scanState 进程级扫描状态（面板单例）。
var (
	scanMu    sync.Mutex
	scanState = &ScanState{Errors: []string{}}
)

// handleScanStatus 扫描进度。
func (a *App) handleScanStatus(w http.ResponseWriter, r *http.Request) {
	scanMu.Lock()
	s := *scanState
	errs := append([]string{}, scanState.Errors...)
	scanMu.Unlock()
	// C# ScanState 是类（string 字段默认 null）——空串按 null 输出；缩略图按 MB（一位小数）输出
	writeJSON(w, map[string]interface{}{
		"running":       s.Running,
		"target":        nilIfEmpty(s.Target),
		"message":       nilIfEmpty(s.Message),
		"currentRoot":   nilIfEmpty(s.CurrentRoot),
		"currentTier":   s.CurrentTier,
		"rootIndex":     s.RootIndex,
		"rootCount":     s.RootCount,
		"seen":          s.Seen,
		"added":         s.Added,
		"skipped":       s.Skipped,
		"nonCard":       s.NonCard,
		"nonMod":        s.NonMod,
		"failed":        s.Failed,
		"refs":          s.Refs,
		"thumbMB":       math.Round(float64(s.ThumbBytes)/1024.0/1024.0*10) / 10,
		"namesRead":     s.NamesRead,
		"namesFilled":   s.NamesFilled,
		"typesFixed":    s.TypesFixed,
		"timelineRead":  s.TimelineRead,
		"pluginDlls":    s.PluginDlls,
		"plugins":       s.Plugins,
		"pluginConfigs": s.PluginConfigs,
		"pluginDone":    s.PluginDone,
		"pluginTotal":   s.PluginTotal,
		"removed":       s.Removed,
		"stepIndex":     s.StepIndex,
		"stepCount":     s.StepCount,
		"stepName":      nilIfEmpty(s.StepName),
		"stepDone":      s.StepDone,
		"stepTotal":     s.StepTotal,
		"startedAt":     nilIfEmpty(s.StartedAt),
		"finishedAt":    nilIfEmpty(s.FinishedAt),
		"errors":        errs,
	})
}

// nilIfEmpty 空串按 null 输出（对齐 C# 可空 string 的序列化）。
func nilIfEmpty(s string) interface{} {
	if s == "" {
		return nil
	}
	return s
}

// atoiDefault 整数解析（空 / 非法用默认值）。
func atoiDefault(s string, def int) int {
	s = strings.TrimSpace(s)
	if s == "" {
		return def
	}
	v, err := strconv.Atoi(s)
	if err != nil {
		return def
	}
	return v
}

// openURL 用系统默认浏览器打开地址（Windows）。
func openURL(url string) {
	_ = execStart(url)
}

// absData paths 辅助：面板数据目录（与 exe 同级 data）。
func dataDir(exePath string) string {
	return filepath.Join(filepath.Dir(exePath), "data")
}

// ensureOSStdout 保持 os 引用（诊断输出走它）。
var _ = os.Stdout
