// 插件库端点（G7）——契约对齐 C# Web/WebApp.cs §/api/plugins 家族。
//
// 六件：列表（含中文说明 / 配置节数）· 扫描 · 场景键对照 · 日志 · 单插件详情 · cfg 值写回。
package web

import (
	"encoding/json"
	"net/http"
	"strings"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// handlePlugins 插件清单（与面板「插件清单」同源）。
func (a *App) handlePlugins(w http.ResponseWriter, r *http.Request) {
	filter := r.URL.Query().Get("filter")
	keyword := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("q")))
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := hub.Core().LoadPlugins()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	cfgs, err := hub.Core().LoadPluginConfigs()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	cfgByGuid := map[string][]store.PluginConfigRow{}
	for _, c := range cfgs {
		if c.Guid == "" {
			continue
		}
		cfgByGuid[c.Guid] = append(cfgByGuid[c.Guid], c)
	}
	_ = cfg
	out := make([]map[string]interface{}, 0, len(rows))
	for _, p := range rows {
		note := core.LookupPluginNote(p.Guid, p.Name)
		if note == "" {
			note = core.LookupPluginNoteFile(p.FileName)
		}
		list := cfgByGuid[p.Guid]
		sectionCount, optionCount := int64(0), int64(0)
		for _, c := range list {
			sectionCount += c.SectionCount
			optionCount += c.OptionCount
		}
		item := pluginRowJSON(p, note, sectionCount, optionCount, len(list) > 0)
		if !pluginPassFilter(item, filter) {
			continue
		}
		if keyword != "" && !strings.Contains(strings.ToLower(toString(item["name"])+toString(item["fileName"])+toString(item["guid"])+note), keyword) {
			continue
		}
		out = append(out, item)
	}
	writeJSON(w, out)
}

// pluginRowJSON 插件行 → 面板 JSON。
func pluginRowJSON(p store.PluginRow, note string, sectionCount, optionCount int64, hasConfig bool) map[string]interface{} {
	return map[string]interface{}{
		"filePath": p.FilePath, "fileName": p.FileName, "guid": p.Guid,
		"name": p.Name, "version": p.Version, "processes": p.Processes,
		"dependencies": p.Dependencies, "isIpa": p.IsIpa,
		"size": p.Size, "mtime": p.Mtime,
		"title": p.Title, "description": p.Description,
		"company": p.Company, "copyright": p.Copyright, "product": p.Product,
		"fileVersion": p.FileVersion, "targetFramework": p.TargetFramework,
		"note": note, "pluginNote": p.Note,
		"configSections": sectionCount, "configOptions": optionCount, "hasConfig": hasConfig,
	}
}

// pluginPassFilter 六筛选（全部 / 已收录说明 / 未收录说明 / 有配置文件 / 无配置文件 / 非插件 dll）。
func pluginPassFilter(item map[string]interface{}, filter string) bool {
	note := toString(item["note"])
	isPlugin := toString(item["guid"]) != ""
	switch filter {
	case "", "all":
		return true
	case "noted":
		return note != ""
	case "unnoted":
		return note == ""
	case "config":
		return item["hasConfig"] == true
	case "noconfig":
		return isPlugin && item["hasConfig"] != true
	case "nonplugin":
		return !isPlugin
	}
	return true
}

// handlePluginsMatch 场景卡插件键 ↔ 已装插件对照（keys 逗号分隔）。
func (a *App) handlePluginsMatch(w http.ResponseWriter, r *http.Request) {
	raw := r.URL.Query().Get("keys")
	keys := make([]string, 0, 8)
	for _, one := range strings.Split(raw, ",") {
		if v := strings.TrimSpace(one); v != "" {
			keys = append(keys, v)
		}
	}
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := hub.Core().LoadPlugins()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]map[string]interface{}, 0, len(keys))
	for _, key := range keys {
		hits := make([]map[string]interface{}, 0, 2)
		for _, p := range rows {
			if core.PluginKeyMatches(p.Guid, p.Name, key) {
				hits = append(hits, map[string]interface{}{
					"guid": p.Guid, "name": p.Name, "version": p.Version, "fileName": p.FileName,
				})
			}
		}
		out = append(out, map[string]interface{}{"key": key, "matches": hits, "found": len(hits) > 0})
	}
	writeJSON(w, out)
}

// handlePluginsLog 解析 BepInEx 日志（加载 / 进程过滤跳过 / 错误）。
func (a *App) handlePluginsLog(w http.ResponseWriter, r *http.Request) {
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	path := core.LogPathOf(cfg)
	summary := core.ReadPluginLog(path)
	writeJSON(w, map[string]interface{}{
		"path":    path,
		"exists":  summary.FileSize > 0,
		"loaded":  len(summary.LoadedNames),
		"skipped": len(summary.SkippedNames),
		"errors":  summary.Errors,
		"error":   summary.Error,
	})
}

// handlePluginDetail 单插件详情（元数据 + cfg 全量）。
func (a *App) handlePluginDetail(w http.ResponseWriter, r *http.Request) {
	guid := strings.TrimSpace(r.URL.Query().Get("guid"))
	name := strings.TrimSpace(r.URL.Query().Get("name"))
	hub, _, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	rows, err := hub.Core().LoadPlugins()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	var hit *store.PluginRow
	for i := range rows {
		if guid != "" && strings.EqualFold(rows[i].Guid, guid) {
			hit = &rows[i]
			break
		}
		if guid == "" && name != "" && strings.EqualFold(rows[i].Name, name) {
			hit = &rows[i]
			break
		}
	}
	if hit == nil {
		writeErr(w, 404, "插件不存在（先扫一次插件库）")
		return
	}
	note := core.LookupPluginNote(hit.Guid, hit.Name)
	if note == "" {
		note = core.LookupPluginNoteFile(hit.FileName)
	}
	cfgList := pluginConfigsOf(hub, hit.Guid)
	confJSON := ""
	if len(cfgList) > 0 {
		file := core.ReadPluginConfig(cfgList[0].FilePath)
		confJSON = core.ToPluginConfigJSON(file)
	}
	writeJSON(w, map[string]interface{}{
		"plugin": pluginRowJSON(*hit, note, 0, 0, len(cfgList) > 0),
		"cfgs":   cfgList,
		"config": confJSON,
	})
}

// handlePluginConfigSave cfg 值写回（编辑授权 + 插件库根白名单 + 最小改动）。
func (a *App) handlePluginConfigSave(w http.ResponseWriter, r *http.Request) {
	var dto struct {
		FilePath string `json:"filePath"`
		Section  string `json:"section"`
		Key      string `json:"key"`
		Value    string `json:"value"`
	}
	if err := json.NewDecoder(r.Body).Decode(&dto); err != nil {
		writeErr(w, 400, "请求体无法解析")
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	if !editAuthorized(hub) {
		writeErr(w, 403, "未取得编辑授权——先在顶部栏勾选「编辑授权」")
		return
	}
	if !underPluginRoot(cfg, dto.FilePath) {
		writeErr(w, 403, "路径不在插件库根内")
		return
	}
	result := core.SavePluginConfigValue(dto.FilePath, dto.Section, dto.Key, dto.Value)
	if !result.Ok {
		writeJSON(w, map[string]interface{}{"ok": false, "error": result.Error})
		return
	}
	writeJSON(w, map[string]interface{}{
		"ok": true, "oldValue": result.OldValue, "newValue": result.NewValue,
		"sizeBefore": result.SizeBefore, "sizeAfter": result.SizeAfter,
	})
}

// toString 任意值 → 字符串（JSON map 取值用）。
func toString(v interface{}) string {
	if s, ok := v.(string); ok {
		return s
	}
	return ""
}

// pluginConfigsOf 某 GUID 的配置文件清单（主库 plugin_config 表）。
func pluginConfigsOf(hub *store.Hub, guid string) []store.PluginConfigRow {
	if guid == "" {
		return nil
	}
	rows, err := hub.Core().LoadPluginConfigs()
	if err != nil {
		return nil
	}
	out := make([]store.PluginConfigRow, 0, 2)
	for _, r := range rows {
		if strings.EqualFold(r.Guid, guid) {
			out = append(out, r)
		}
	}
	return out
}

// underPluginRoot 路径是否落在插件库根下（cfg 写回的白名单判据）。
func underPluginRoot(cfg *core.RootsConfig, path string) bool {
	if cfg == nil || path == "" {
		return false
	}
	lower := strings.ToLower(strings.ReplaceAll(path, "/", `\`))
	for _, e := range cfg.PluginRoots {
		root := strings.ToLower(strings.TrimRight(strings.ReplaceAll(e.Path, "/", `\`), `\`))
		if root != "" && strings.HasPrefix(lower, root+`\`) {
			return true
		}
	}
	return false
}
