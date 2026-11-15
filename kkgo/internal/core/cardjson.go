// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：卡片分项分析 / timeline 的「面板内嵌」序列化形状——把包内的线格式
// 结构导出给 web 层直接嵌入响应（/api/card/{id}/structure 的 detail / coords / scene / timeline 子对象）。
// 形状与 C# 同名类型的 camelCase 序列化逐字一致（与 CLI card-analysis 同一份线格式）。
package core

// CardDetailJSON C# CardDetailResult 的序列化形状（面板 structure 端点直接嵌入）。
func CardDetailJSON(d *CardDetailResult) interface{} {
	if d == nil {
		return nil
	}
	return detailJSONOf(d)
}

// CardCoordinateJSON C# CardCoordinateResult 的序列化形状。
func CardCoordinateJSON(r *CardCoordinateResult) interface{} {
	if r == nil {
		return nil
	}
	return coordJSONOf(r)
}

// CardSceneJSON C# SceneInfoResult 的序列化形状。
func CardSceneJSON(s *SceneInfoResult) interface{} {
	if s == nil {
		return nil
	}
	return sceneJSONOf(s)
}

// TimelineSceneJSON C# WebApp.cs TimelineSceneJson 的线格式——组树 + 轨道 + 关键帧（面板轨道视图）。
// full=false 精简视图；full=true 附轨道属性全集与根属性全集（后续编辑 / 导出复用同一份格式）。
func TimelineSceneJSON(scene *TimelineScene, full bool) map[string]interface{} {
	if scene == nil {
		return map[string]interface{}{"ok": false, "error": "timeline 模型为空"}
	}
	groups := make([]map[string]interface{}, 0, len(scene.Groups))
	for _, g := range scene.Groups {
		groups = append(groups, map[string]interface{}{
			"n": setStr(g.Name, g.NameSet), "p": g.ParentIndex, "d": g.Depth, "tc": g.TrackCount,
		})
	}
	tracks := make([]map[string]interface{}, 0, len(scene.Tracks))
	keyframeTotal := 0
	for _, t := range scene.Tracks {
		kfs := make([][]float64, 0, len(t.Keyframes))
		for _, kf := range t.Keyframes {
			if kf.HasXYZW {
				kfs = append(kfs, []float64{kf.Time, kf.ValueX, kf.ValueY, kf.ValueZ, kf.ValueW})
				continue
			}
			kfs = append(kfs, []float64{kf.Time, kf.Value})
		}
		keyframeTotal += len(t.Keyframes)
		item := map[string]interface{}{
			"g":     t.GroupIndex,
			"id":    setStr(t.Id, t.IdSet),
			"alias": setStr(t.Alias, timelineAttrHas(t.Attributes, "alias")),
			"owner": setStr(t.Owner, timelineAttrHas(t.Attributes, "owner")),
			"oi":    t.ObjectIndex,
			"path":  setStr(t.GuideObjectPath, timelineAttrHas(t.Attributes, "guideObjectPath")),
			"en":    t.Enabled,
			"kf":    kfs,
		}
		if full {
			item["attrs"] = timelineAttrJSON(t.Attributes)
		}
		tracks = append(tracks, item)
	}
	var rootAttrs interface{}
	if full {
		rootAttrs = timelineAttrJSON(scene.RootAttributes)
	}
	return map[string]interface{}{
		"ok":          scene.Error == "",
		"error":       optStr(scene.Error),
		"hasEntry":    scene.HasEntry,
		"isEmpty":     scene.IsEmpty,
		"duration":    scene.Duration,
		"timeScale":   scene.TimeScale,
		"blockLength": scene.BlockLength,
		"divisions":   scene.Divisions,
		"xmlLength":   scene.XmlLength,
		"truncated":   scene.Truncated,
		"keyframes":   keyframeTotal,
		"groups":      groups,
		"tracks":      tracks,
		"rootAttrs":   rootAttrs,
	}
}

// timelineAttrJSON 属性全集的线格式——C# TimelineAttr 由 RelaxJson（未设命名策略）序列化，
// 键名保持 PascalCase（Name / Value）；与同响应里匿名对象的小写键不同源，照抄不统一。
func timelineAttrJSON(attrs []TimelineAttr) []map[string]interface{} {
	out := make([]map[string]interface{}, 0, len(attrs))
	for _, a := range attrs {
		out = append(out, map[string]interface{}{"Name": a.Name, "Value": a.Value})
	}
	return out
}

// timelineAttrHas 属性全集里是否有该名字——C# 的轨道 / 组字段只在属性出现时赋值，
// 缺属性即 null（不是空串）；序列化按这一口径决定输出值还是 null。
func timelineAttrHas(attrs []TimelineAttr, name string) bool {
	for i := range attrs {
		if attrs[i].Name == name {
			return true
		}
	}
	return false
}

// TimelineInfoJSON C# TimelineInfo 的序列化形状（字段名 = C# 属性 camelCase）。
func TimelineInfoJSON(t *TimelineInfo) map[string]interface{} {
	if t == nil {
		return nil
	}
	return map[string]interface{}{
		"scanned":         t.Scanned,
		"hasEntry":        t.HasEntry,
		"isEmpty":         t.IsEmpty,
		"duration":        t.Duration,
		"timeScale":       t.TimeScale,
		"xmlLength":       t.XmlLength,
		"xmlAt":           t.XmlAt,
		"keyframes":       t.Keyframes,
		"truncated":       t.Truncated,
		"groups":          t.Groups,
		"maxKeyframeTime": t.MaxKeyframeTime,
		"groupNames":      strs(t.GroupNames),
		"realSeconds":     t.RealSeconds,
		"hitStage":        optStr(t.HitStage),
		"error":           optStr(t.Error),
	}
}
