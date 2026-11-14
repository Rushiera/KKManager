// 卡片类端点（G7）——对齐 C# WebApp.cs 的 /api/card/* 与两个字母表端点。
package web

import (
	"encoding/json"
	"net/http"
	"strconv"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// parseID 取路径参数里的整数 id。
func parseID(s string) int64 {
	v, _ := strconv.ParseInt(s, 10, 64)
	return v
}

// handleCard GET /api/card/{id}?lib= 单张卡片行。
func (a *App) handleCard(w http.ResponseWriter, r *http.Request) {
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
		writeErr(w, 404, "找不到库 "+strconv.Itoa(lib))
		return
	}
	row, err := s.GetCardListRow(id)
	if err != nil {
		writeErr(w, 404, "卡片不存在")
		return
	}
	row.Lib = lib
	writeJSON(w, cardRowJSON(row))
}

// cardRowJSON 卡片行 → 面板 JSON（字段名对齐 C# CardRow 序列化的 camelCase）。
func cardRowJSON(r *store.CardListRow) map[string]interface{} {
	out := map[string]interface{}{
		"id":       r.Id,
		"fileName": r.FileName,
		"size":     r.Size,
		"tier":     r.Tier,
		"folder":   r.Folder,
		"modCount": r.ModCount,
		"green":    r.Green,
		"yellow":   r.Yellow,
		"red":      r.Red,
		"black":    r.Black,
		"hasThumb": r.HasThumb,
		"lib":      r.Lib,
	}
	if r.CardTypeSet {
		out["cardType"] = r.CardType
	} else {
		out["cardType"] = nil
	}
	if r.MtimeSet {
		out["mtime"] = r.Mtime
	} else {
		out["mtime"] = nil
	}
	if r.RootPathSet {
		out["rootPath"] = r.RootPath
	} else {
		out["rootPath"] = nil
	}
	if r.CharaNameSet {
		out["charaName"] = r.CharaName
	} else {
		out["charaName"] = nil
	}
	if r.TimelineSet {
		out["timelineSeconds"] = r.TimelineSeconds
	} else {
		out["timelineSeconds"] = nil
	}
	return out
}

// handlePersonalities GET /api/personalities 性格字母表。
func (a *App) handlePersonalities(w http.ResponseWriter, r *http.Request) {
	list := make([]map[string]interface{}, 0, 48)
	for id := 0; id <= 40; id++ {
		name, ok := core.PersonalityNameOf(id)
		if !ok {
			continue
		}
		list = append(list, map[string]interface{}{"id": id, "name": name})
	}
	writeJSON(w, map[string]interface{}{"ok": true, "list": list})
}

// handleWeakPoints GET /api/weak-points 敏感带字母表。
func (a *App) handleWeakPoints(w http.ResponseWriter, r *http.Request) {
	list := make([]map[string]interface{}, 0, 8)
	for id := -1; id <= 8; id++ {
		name, ok := core.WeakPointNameOf(id)
		if !ok {
			continue
		}
		list = append(list, map[string]interface{}{"id": id, "name": name})
	}
	writeJSON(w, map[string]interface{}{"ok": true, "list": list})
}

// handleCardParams GET /api/card/{id}/params 可编辑字段读面。
func (a *App) handleCardParams(w http.ResponseWriter, r *http.Request) {
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
	layout := core.ParseCardLayout(path)
	info := core.ReadCardParams(path, layout)
	if info.Error != "" {
		writeJSON(w, map[string]interface{}{"ok": false, "error": info.Error, "cardType": layout.CardType})
		return
	}
	personalityName, pOK := core.PersonalityNameOf(info.Personality)
	weakPointName, wOK := core.WeakPointNameOf(info.WeakPoint)
	var pName, wName interface{}
	if pOK {
		pName = personalityName
	}
	if wOK {
		wName = weakPointName
	}
	archives, _ := hub.Core().ListCardEdits(path)
	writeJSON(w, map[string]interface{}{
		"ok":              true,
		"cardType":        layout.CardType,
		"lastName":        info.LastName,
		"firstName":       info.FirstName,
		"nickName":        info.NickName,
		"personality":     info.Personality,
		"personalityName": pName,
		"weakPoint":       info.WeakPoint,
		"weakPointName":   wName,
		"denial":          info.Denial,
		"denialKeys":      core.DenialKeys,
		"denialNames":     core.DenialNames,
		"archived":        len(archives),
	})
}

// cardEditDto POST /api/card/{id}/params 请求体。
type cardEditDto struct {
	Lib         int     `json:"lib"`
	LastName    *string `json:"lastName"`
	FirstName   *string `json:"firstName"`
	NickName    *string `json:"nickName"`
	Personality *int    `json:"personality"`
	WeakPoint   *int    `json:"weakPoint"`
	Denial      []*bool `json:"denial"`
}

// handleCardParamsSave POST /api/card/{id}/params 写卡片字段（编辑授权闸门 + 留档 + 最小改动）。
func (a *App) handleCardParamsSave(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	var dto cardEditDto
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
	path, why := core.CardPathOf(hub, cfg, dto.Lib, id)
	if path == "" {
		writeErr(w, 404, why)
		return
	}
	layout := core.ParseCardLayout(path)
	cur := core.ReadCardParams(path, layout)
	edit := core.CardParamEdit{
		LastName:    dto.LastName,
		FirstName:   dto.FirstName,
		NickName:    dto.NickName,
		Personality: dto.Personality,
		WeakPoint:   dto.WeakPoint,
	}
	if dto.Denial != nil {
		denial := make([]*bool, len(core.DenialKeys))
		for i := range denial {
			if i < len(dto.Denial) && dto.Denial[i] != nil {
				v := *dto.Denial[i]
				denial[i] = &v
			}
		}
		edit.Denial = denial
	}
	result := core.ApplyCardEdit(path, layout, cur, edit, archiveDir(a.DBPath))
	if !result.Ok {
		writeJSON(w, map[string]interface{}{"ok": false, "error": result.Error})
		return
	}
	writeJSON(w, map[string]interface{}{
		"ok":           true,
		"changes":      result.Changes,
		"delta":        result.Delta,
		"newSize":      result.NewSize,
		"archivedFile": result.ArchivedFile,
		"archivedSize": result.ArchivedSize,
	})
}

// handleCardArchives GET /api/card/{id}/archives 编辑留档记录。
func (a *App) handleCardArchives(w http.ResponseWriter, r *http.Request) {
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
	list, err := hub.Core().ListCardEdits(path)
	if err != nil {
		writeJSON(w, map[string]interface{}{"ok": false, "error": err.Error()})
		return
	}
	out := make([]map[string]interface{}, 0, len(list))
	for _, one := range list {
		out = append(out, map[string]interface{}{
			"id": one.Id, "archivedFile": one.ArchivedFile, "archivedSize": one.ArchivedSize,
			"archivedMtime": one.ArchivedMtime, "changes": one.Changes, "editedAt": one.EditedAt,
		})
	}
	writeJSON(w, map[string]interface{}{"ok": true, "list": out})
}

// handleCardThumb GET /api/thumb/{id}?lib=&big= 卡片缩略图（JPEG）。
func (a *App) handleCardThumb(w http.ResponseWriter, r *http.Request) {
	id := parseID(r.PathValue("id"))
	lib := atoiDefault(r.URL.Query().Get("lib"), 1)
	width := 256
	if r.URL.Query().Get("big") == "1" {
		width = 512
	}
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
	jpg := core.ThumbnailFromCard(path, st.ImageEnd, width, 82)
	if jpg == nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	_, _ = w.Write(jpg)
}

// archiveDir 卡片编辑留档目录（对齐 C# CardArchiveDir：主库同级的 card_archive）。
func archiveDir(dbPath string) string {
	dir := dbPath
	if i := lastSeparator(dir); i >= 0 {
		dir = dir[:i]
	}
	return dir + string(separatorChar) + "card_archive"
}

// lastSeparator 最后一个路径分隔符位置（反斜杠或正斜杠）。
func lastSeparator(s string) int {
	for i := len(s) - 1; i >= 0; i-- {
		if s[i] == '\\' || s[i] == '/' {
			return i
		}
	}
	return -1
}

// separatorChar 本机路径分隔符。
const separatorChar = '\\'
