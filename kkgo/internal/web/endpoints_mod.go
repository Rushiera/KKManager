// mod 家族端点（G7）——契约对齐 C# WebApp.cs 的 /api/mod/* 与 /api/mod/{guid}/*。
package web

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// handleModCards 该 mod 被哪些卡片引用（三处入口共用：mod 列表 / 缺失清单 / 引用明细行）。
func (a *App) handleModCards(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.QueryCardsByModHub(hub, cfg, guid, 1, 0)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, c := range rows {
		out = append(out, map[string]interface{}{
			"id": c.Id, "lib": c.Lib, "fileName": c.FileName, "folder": c.Folder,
			"cardType": c.CardType, "charaName": c.CharaName, "hasThumb": c.HasThumb,
		})
	}
	writeJSON(w, out)
}

// handleModFiles 该 guid 的磁盘副本清单。
func (a *App) handleModFiles(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := core.QueryModFilesHub(hub, cfg, guid)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(rows))
	for _, f := range rows {
		out = append(out, map[string]interface{}{
			"filePath": f.FilePath, "fileName": f.FileName, "tier": f.Tier,
			"rootPath": f.RootPath, "size": f.Size, "mtime": f.Mtime,
			"exists": pathExists(f.FilePath),
		})
	}
	writeJSON(w, out)
}

// handleModComposition 组成档案：GET 读已有档案（不读容器）；force=true 强制重读。
func (a *App) handleModComposition(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	force := r.URL.Query().Get("force") == "true"
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	if !force {
		rec, loadErr := hub.Core().LoadComposition(guid)
		if loadErr != nil {
			writeErr(w, 500, loadErr.Error())
			return
		}
		if rec == nil {
			writeJSON(w, map[string]interface{}{"exists": false})
			return
		}
		writeJSON(w, compositionJSON(rec))
		return
	}
	rec, err := core.AnalyzeCompositionHub(hub, cfg, guid, true, nil)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, compositionJSON(rec))
}

// compositionJSON 组成档案的线形状（条目 / 目录聚合 / 文本条目）。
func compositionJSON(rec *store.CompositionRow) map[string]interface{} {
	dirs := core.BuildDirs(rec.Entries)
	dirOut := make([]map[string]interface{}, 0, len(dirs))
	for _, d := range dirs {
		dirOut = append(dirOut, map[string]interface{}{"path": d.Path, "count": d.Count, "size": d.Size})
	}
	entries := make([]map[string]interface{}, 0, 64)
	for _, line := range strings.Split(rec.Entries, "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if len(parts) < 3 {
			continue
		}
		size, _ := strconv.ParseInt(parts[1], 10, 64)
		compressed, _ := strconv.ParseInt(parts[2], 10, 64)
		entries = append(entries, map[string]interface{}{"path": parts[0], "size": size, "compressed": compressed})
	}
	texts := make([]map[string]interface{}, 0, 8)
	for _, kv := range core.ParseTexts(rec.Texts) {
		texts = append(texts, map[string]interface{}{"name": kv.Name, "body": kv.Body})
	}
	return map[string]interface{}{
		"exists": true, "guid": rec.Guid, "filePath": rec.FilePath, "fileName": rec.FileName,
		"size": rec.Size, "mtime": rec.Mtime, "entryCount": rec.EntryCount,
		"totalSize": rec.TotalSize, "totalCompressed": rec.TotalCompressed,
		"entries": entries, "dirs": dirOut, "texts": texts, "analyzedAt": rec.AnalyzedAt,
		"cached": rec.Cached,
	}
}

// handleModEntry 组成区单条目预览（白名单 = 档案内条目）。
func (a *App) handleModEntry(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	entryPath := r.URL.Query().Get("path")
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rec, err := core.AnalyzeCompositionHub(hub, cfg, guid, false, nil)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if !core.CompositionHasEntry(rec.Entries, entryPath) {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "该条目不在组成档案内"})
		return
	}
	data, readErr := core.ReadZipEntry(rec.FilePath, entryPath, core.TextEntryMaxBytes)
	if readErr != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": readErr.Error()})
		return
	}
	if core.IsImageEntry(entryPath) {
		w.Header().Set("Content-Type", "image/*")
		w.Write(data)
		return
	}
	decoded := core.DecodeText(data)
	writeJSON(w, map[string]interface{}{
		"ok": true, "path": entryPath, "size": len(data),
		"text": core.ClampTextEntry(decoded.Text), "encoding": decoded.Encoding,
	})
}

// handleModOldOp 四个搬运端点共用：mark-old / keep-version / swap / promote。
func (a *App) handleModOldOp(op string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Guid string `json:"guid"`
			Path string `json:"path"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			writeJSON(w, map[string]interface{}{"ok": false, "error": "请求体不是合法 JSON"})
			return
		}
		hub, cfg, err := openApp(a.DBPath)
		if err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		defer hub.Close()
		var detail string
		switch op {
		case "mark-old":
			detail, err = core.MarkOldHub(hub, cfg, body.Guid, body.Path)
		case "keep-version":
			detail, err = core.KeepVersionHub(hub, cfg, body.Guid, body.Path)
		case "swap":
			detail, err = core.SwapVersionHub(hub, cfg, body.Guid, body.Path)
		case "promote":
			detail, err = core.PromoteOldHub(hub, cfg, body.Guid, body.Path)
		}
		if err != nil {
			writeJSON(w, map[string]interface{}{"ok": false, "error": err.Error()})
			return
		}
		writeJSON(w, map[string]interface{}{"ok": true, "detail": detail})
	}
}

// handleModU3d POST：强制解析某 unity3d 条目并落档。
func (a *App) handleModU3d(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Guid string `json:"guid"`
		Path string `json:"path"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "请求体不是合法 JSON"})
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rec, detail := core.AnalyzeU3d(hub, cfg, body.Guid, body.Path, true)
	if rec == nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": detail})
		return
	}
	writeJSON(w, map[string]interface{}{
		"ok": true, "guid": rec.Guid, "path": rec.EntryPath, "filePath": rec.FilePath,
		"size": rec.Size, "textureCount": rec.TextureCount, "parsedAt": rec.ParsedAt,
	})
}

// handleModU3dLoad GET：读已解析档案（无档返回 exists=false）。
func (a *App) handleModU3dLoad(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	entryPath := r.URL.Query().Get("path")
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	_ = cfg
	rec, loadErr := hub.Core().LoadU3dRow(guid, entryPath)
	if loadErr != nil {
		writeErr(w, 500, loadErr.Error())
		return
	}
	if rec == nil {
		writeJSON(w, map[string]interface{}{"exists": false})
		return
	}
	writeJSON(w, map[string]interface{}{
		"exists": true, "guid": rec.Guid, "path": rec.EntryPath,
		"textureCount": rec.TextureCount, "textures": rec.Textures, "parsedAt": rec.ParsedAt,
	})
}

// handleModU3dTexture 取某张贴图的 PNG（按条目路径 + 索引）。
func (a *App) handleModU3dTexture(w http.ResponseWriter, r *http.Request) {
	guid := r.PathValue("guid")
	entryPath := r.URL.Query().Get("path")
	index := atoiDefault(r.URL.Query().Get("index"), 0)
	maxSide := atoiDefault(r.URL.Query().Get("max"), 512)
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rec, detail := core.AnalyzeU3d(hub, cfg, guid, entryPath, false)
	if rec == nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": detail})
		return
	}
	png, pngErr := core.RenderU3dTexture(rec.FilePath, entryPath, index, maxSide)
	if pngErr != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": pngErr.Error()})
		return
	}
	w.Header().Set("Content-Type", "image/png")
	w.Write(png)
}

// handleReveal 资源管理器定位并选中文件（路径须落在受管库根内）。
func (a *App) handleReveal(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Path string `json:"path"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "请求体不是合法 JSON"})
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	if !core.PathInAnyRoot(cfg, body.Path) {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "路径不在任何受管库根内"})
		return
	}
	if _, statErr := os.Stat(body.Path); statErr != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": "文件不存在：" + body.Path})
		return
	}
	if err := core.RevealInExplorer(body.Path); err != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": err.Error()})
		return
	}
	writeJSON(w, map[string]interface{}{"ok": true})
}

// handleBrowseFolder 打开文件夹选择框（占位：返回当前缺省目录，不阻塞服务）。
func (a *App) handleBrowseFolder(w http.ResponseWriter, r *http.Request) {
	initial := r.URL.Query().Get("initial")
	if initial == "" {
		initial = filepath.Dir(a.DBPath)
	}
	writeJSON(w, map[string]interface{}{
		"ok":      false,
		"error":   "Go 版暂不提供原生文件夹选择框——请在输入框里直接填路径",
		"initial": initial,
	})
}

// pathExists 文件是否在磁盘上。
func pathExists(path string) bool {
	if strings.TrimSpace(path) == "" {
		return false
	}
	_, err := os.Stat(path)
	return err == nil
}

// itoa64 整数转字符串。
func itoa64(v int64) string { return strconv.FormatInt(v, 10) }
