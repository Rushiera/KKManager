// 库根配置 / 非卡搬移 / unity3d 档案 / 组成档案写入——G7 缺口端点补齐（对齐 C# WebApp.cs）。
package web

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// saveRootsConfig 把库根配置写回主库设置（字段：roots）。
func saveRootsConfig(hub *store.Hub, cfg *core.RootsConfig) error {
	text, err := core.RootsConfigJSON(cfg)
	if err != nil {
		return err
	}
	return hub.Core().SetSetting("roots", text)
}

// handleRootsRecommend GET /api/roots/recommend?gameRoot= 推荐库根（不改配置）。
func (a *App) handleRootsRecommend(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	root := r.URL.Query().Get("gameRoot")
	if strings.TrimSpace(root) == "" {
		root = cfg.GameRoot
	}
	writeJSON(w, map[string]interface{}{
		"gameRoot":    core.NormalizeGameRoot(root),
		"pluginRoots": core.RecommendPluginRoots(root),
		"modRoots":    core.RecommendModRoots(root),
		"cardRoots":   core.RecommendCardRoots(root),
	})
}

// handleRootsOffline POST /api/roots/offline {kind, path, offline} 设置库根离线标记。
// 对齐 C# SetRootOffline：预置条目不可离线；成功后落盘。
func (a *App) handleRootsOffline(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Kind    string `json:"kind"`
		Path    string `json:"path"`
		Offline bool   `json:"offline"`
	}
	_ = json.NewDecoder(r.Body).Decode(&dto)
	if strings.TrimSpace(dto.Path) == "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "缺少路径"})
		return
	}
	isMods := dto.Kind == "mods"
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	list := cfg.CardRoots
	if isMods {
		list = cfg.ModRoots
	}
	for i := range list {
		if !strings.EqualFold(strings.TrimSpace(list[i].Path), strings.TrimSpace(dto.Path)) {
			continue
		}
		if !core.CanOffline(list[i], isMods) {
			writeJSON(w, map[string]interface{}{"ok": false, "error": "预置条目不适用离线：" + list[i].Path})
			return
		}
		list[i].Offline = dto.Offline
		if isMods {
			cfg.ModRoots = list
		} else {
			cfg.CardRoots = list
		}
		if saveErr := saveRootsConfig(hub, cfg); saveErr != nil {
			writeJSON(w, map[string]interface{}{"ok": false, "error": saveErr.Error()})
			return
		}
		if !dto.Offline {
			// 上线消解该库的「库已离线」待办（对齐 C# SetRootOffline）
			_ = hub.Core().CloseTodoByKey("offline", strings.ToLower(strings.TrimSpace(list[i].Path)))
		}
		writeJSON(w, map[string]interface{}{"ok": true})
		return
	}
	writeJSON(w, map[string]interface{}{"ok": false, "error": "库根不在配置里：" + dto.Path})
}

// handleRootsPurge POST /api/roots/purge {kind, path} 彻底删除该库数据（库文件 + 配置条目）。
// 对齐 C# PurgeRoot：预置条目拒绝；先落配置再删库文件（删除失败只出声，不改回执形状）。
func (a *App) handleRootsPurge(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Kind string `json:"kind"`
		Path string `json:"path"`
	}
	_ = json.NewDecoder(r.Body).Decode(&dto)
	if strings.TrimSpace(dto.Path) == "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "缺少路径"})
		return
	}
	isMods := dto.Kind == "mods"
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	list := cfg.CardRoots
	if isMods {
		list = cfg.ModRoots
	}
	idx := -1
	for i := range list {
		if strings.EqualFold(strings.TrimSpace(list[i].Path), strings.TrimSpace(dto.Path)) {
			idx = i
			break
		}
	}
	if idx < 0 {
		hub.Close()
		writeJSON(w, map[string]interface{}{"ok": false, "error": "库根不在配置里：" + dto.Path})
		return
	}
	if !core.CanOffline(list[idx], isMods) {
		hub.Close()
		writeJSON(w, map[string]interface{}{"ok": false, "error": "预置条目不可删除数据：" + list[idx].Path})
		return
	}
	base, libErr := core.LibOfRootPath(hub, cfg, list[idx].Path)
	// 🔴 删记录必须在移除配置之前做——移除后 AllStoreRefs 不再含该库根，分片库里的行会漏删
	purged, purgeErr := core.PurgeRootHub(hub, cfg, list[idx].Path)
	if purgeErr != nil {
		hub.Close()
		writeJSON(w, map[string]interface{}{"ok": false, "error": "清理库记录失败：" + purgeErr.Error()})
		return
	}
	trimmed := append(append([]core.RootEntry{}, list[:idx]...), list[idx+1:]...)
	if isMods {
		cfg.ModRoots = trimmed
	} else {
		cfg.CardRoots = trimmed
	}
	if saveErr := saveRootsConfig(hub, cfg); saveErr != nil {
		hub.Close()
		writeJSON(w, map[string]interface{}{"ok": false, "error": saveErr.Error()})
		return
	}
	// 库文件将被删除——先释放该库根的常驻预热连接（占用中的文件删不掉）
	a.warm.releaseLibs(hub, list[idx].Path)
	_ = hub.Core().CloseTodoByKey("offline", strings.ToLower(strings.TrimSpace(list[idx].Path)))
	hub.Close()
	removedFiles := 0
	fileErrs := []string{}
	if libErr == nil && base > 0 {
		removedFiles, fileErrs = purgeLibFiles(filepath.Dir(a.DBPath), base)
		for _, one := range fileErrs {
			println("[库数据] 删除库文件失败：" + one)
		}
	}
	writeJSON(w, map[string]interface{}{"ok": true, "purged": purged, "removedFiles": removedFiles, "fileErrors": fileErrs})
}

// purgeLibFiles 删除某库根的全部分片库文件（lib_<base>_*.db 及 -wal / -shm 边车）。
func purgeLibFiles(dir string, base int) (int, []string) {
	pattern := filepath.Join(dir, "lib_"+strconv.Itoa(base)+"_*.db*")
	files, _ := filepath.Glob(pattern)
	removed := 0
	errs := make([]string, 0, 4)
	for _, f := range files {
		if err := os.Remove(f); err != nil {
			errs = append(errs, f+"："+err.Error())
			continue
		}
		removed++
	}
	return removed, errs
}

// handleNonCardMove POST /api/noncard/move 把非卡登记项一键搬到缓存库（逐条尽力，失败留明细）。
// 对齐 C#：{ok, moved, skipped, failures:[{path,error}], pending}。
func (a *App) handleNonCardMove(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	failures := make([]map[string]interface{}, 0, 8)
	moved, skipped := 0, 0
	for _, s := range core.AllStoreRefs(hub, cfg) {
		rows, listErr := s.DB.ListNonCardRows(false)
		if listErr != nil {
			continue
		}
		for _, row := range rows {
			dest, detail, moveErr := core.MoveNonCardHub(hub, cfg, s.Lib, row.Id)
			if moveErr != nil || dest == "" {
				skipped++
				if len(failures) < 50 {
					msg := detail
					if moveErr != nil {
						msg = moveErr.Error()
					}
					failures = append(failures, map[string]interface{}{"path": row.FilePath, "error": msg})
				}
				continue
			}
			moved++
		}
	}
	pending, _ := core.CountNonCardsHub(hub, cfg, false)
	writeJSON(w, map[string]interface{}{
		"ok": true, "moved": moved, "skipped": skipped, "failures": failures, "pending": pending,
	})
}

// handleNonCardThumbByID GET /api/noncard/thumb/{id}?lib= 非卡文件缩略图（无图 404）。
func (a *App) handleNonCardThumbByID(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	s, libErr := hub.StoreByLib(lib)
	if libErr != nil || s == nil {
		notFound(w)
		return
	}
	data, loadErr := s.LoadNonCardThumb(id)
	if loadErr != nil || len(data) == 0 {
		notFound(w)
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	_, _ = w.Write(data)
}

// handleModU3dMap GET /api/mod/{guid}/u3dmap 已解析的 unity3d 条目清单（组成区「已解析」判定）。
func (a *App) handleModU3dMap(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	m, loadErr := hub.Core().LoadU3dMap(guid)
	if loadErr != nil {
		writeErr(w, 500, loadErr.Error())
		return
	}
	paths := make([]string, 0, len(m))
	for k := range m {
		paths = append(paths, k)
	}
	sort.Strings(paths)
	writeJSON(w, map[string]interface{}{"ok": true, "paths": paths})
}

// handleModU3dTex GET /api/mod/{guid}/u3dtex?path=&id=&size= 取 unity3d 内一张贴图的 PNG。
// 白名单 = 该 guid 档案里的贴图清单；size=full 给原图，其余给缩略图。
func (a *App) handleModU3dTex(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	entryPath := r.URL.Query().Get("path")
	pathId := int64(atoiDefault(r.URL.Query().Get("id"), 0))
	size := r.URL.Query().Get("size")
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rec, loadErr := hub.Core().LoadU3dRow(guid, entryPath)
	if loadErr != nil || rec == nil {
		notFound(w)
		return
	}
	if !core.PathInAnyRoot(cfg, rec.FilePath) {
		w.WriteHeader(http.StatusForbidden)
		return
	}
	textures := core.ParseU3dTextures(rec.Textures)
	index := -1
	for i, t := range textures {
		if t.PathId == pathId {
			index = i
			break
		}
	}
	if index < 0 {
		w.WriteHeader(http.StatusForbidden)
		return
	}
	maxEdge := 512
	if strings.EqualFold(size, "full") {
		maxEdge = 0
	}
	pngData, renderErr := core.RenderU3dTexture(rec.FilePath, entryPath, index, maxEdge)
	if renderErr != nil {
		notFound(w)
		return
	}
	w.Header().Set("Content-Type", "image/png")
	_, _ = w.Write(pngData)
}

// handleModCompositionSave POST /api/mod/composition {guid, force} 强制重读容器并更新组成档案。
// 对齐 C#：缺 guid → 400；建档失败 → 409；成功 → 档案 JSON + ok:true。
func (a *App) handleModCompositionSave(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Guid  string `json:"guid"`
		Force bool   `json:"force"`
	}
	_ = json.NewDecoder(r.Body).Decode(&body)
	if strings.TrimSpace(body.Guid) == "" {
		writeErr(w, 400, "缺少 guid")
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rec, detail := core.AnalyzeCompositionHub(hub, cfg, body.Guid, body.Force, nil)
	if rec == nil {
		msg := "建档失败"
		if detail != nil {
			msg = detail.Error()
		}
		writeErr(w, 409, msg)
		return
	}
	out := compositionJSON(rec)
	out["ok"] = true
	writeJSON(w, out)
}

// handleCardImage GET /api/card/{id}/image?lib=&offset=&size=&w= 卡片内嵌图片缩略图（JPEG）。
// 对齐 C#：路径解析失败 / 解码失败 → {ok:false,error}（状态 200）。
func (a *App) handleCardImage(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	q := r.URL.Query()
	lib := atoiDefault(q.Get("lib"), 1)
	offset := int64(atoiDefault(q.Get("offset"), 0))
	size := int64(atoiDefault(q.Get("size"), 0))
	width := atoiDefault(q.Get("w"), 96)
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
	jpg, detail := core.InlineImageJPEG(path, offset, size, width)
	if jpg == nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": detail})
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	_, _ = w.Write(jpg)
}

// kkcRootsOf kkc 整理库的两条接入路径——卡片侧 = <库目录>/kkc/整理，mod 侧 = 该目录下的 02-mod。
// kkc 与 mod / 卡片库根不是同一形态（它由 card-sort 链自己管），所以只在面板上用一个开关表达；
// 数据仍落同一个数据库（不分家）。
func kkcRootsOf(dbPath string) (cardRoot string, modRoot string) {
	cardRoot = filepath.Join(filepath.Dir(dbPath), "kkc", "整理")
	return cardRoot, filepath.Join(cardRoot, "02-mod")
}

// dropRootPath 去掉路径等于 path 的库根条目（大小写与尾部分隔符不敏感）。
func dropRootPath(list []core.RootEntry, path string) []core.RootEntry {
	want := strings.TrimRight(strings.TrimSpace(path), `\/`)
	out := make([]core.RootEntry, 0, len(list))
	for _, e := range list {
		if strings.EqualFold(strings.TrimRight(strings.TrimSpace(e.Path), `\/`), want) {
			continue
		}
		out = append(out, e)
	}
	return out
}

// hasRootPath 配置里是否存在路径等于 path 的库根条目。
func hasRootPath(list []core.RootEntry, path string) bool {
	want := strings.TrimRight(strings.TrimSpace(path), `\/`)
	for _, e := range list {
		if strings.EqualFold(strings.TrimRight(strings.TrimSpace(e.Path), `\/`), want) {
			return true
		}
	}
	return false
}

// handleKkcState GET /api/kkc —— kkc 接入开关状态（开 = 配置里有 kkc 的两条根）。
func (a *App) handleKkcState(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	cardRoot, modRoot := kkcRootsOf(a.DBPath)
	writeJSON(w, map[string]interface{}{
		"ok":       true,
		"enabled":  hasRootPath(cfg.CardRoots, cardRoot),
		"root":     filepath.Dir(cardRoot),
		"cardRoot": cardRoot,
		"modRoot":  modRoot,
		"modOn":    hasRootPath(cfg.ModRoots, modRoot),
	})
}

// handleKkcToggle POST /api/kkc {enabled} —— 开：把 kkc 整理库接成「卡片附加库 + mod 冷冻库」；
// 关：只摘这两条根，其余库根一字不动。开关状态同时落 setting.kkc_enabled（只为读面自证，判定以配置为准）。
func (a *App) handleKkcToggle(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		Enabled bool `json:"enabled"`
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
	cardRoot, modRoot := kkcRootsOf(a.DBPath)
	cfg.CardRoots = dropRootPath(cfg.CardRoots, cardRoot)
	cfg.ModRoots = dropRootPath(cfg.ModRoots, modRoot)
	if dto.Enabled {
		cfg.CardRoots = append(cfg.CardRoots, core.RootEntry{Tier: 2, Path: cardRoot, Recurse: true, ReadOnly: true})
		cfg.ModRoots = append(cfg.ModRoots, core.RootEntry{Tier: 3, Path: modRoot, Recurse: true, ReadOnly: true})
	}
	core.Normalize(cfg)
	if saveErr := saveRootsConfig(hub, cfg); saveErr != nil {
		writeErr(w, 500, saveErr.Error())
		return
	}
	flag := "0"
	if dto.Enabled {
		flag = "1"
	}
	_ = hub.Core().SetSetting("kkc_enabled", flag)
	fmt.Printf("[kkc] 接入开关 → %s（卡片根 %s · mod 根 %s）\n", map[bool]string{true: "开", false: "关"}[dto.Enabled], cardRoot, modRoot)
	writeJSON(w, map[string]interface{}{
		"ok": true, "enabled": dto.Enabled, "roots": cfg,
		"root": filepath.Dir(cardRoot), "cardRoot": cardRoot, "modRoot": modRoot,
	})
}
