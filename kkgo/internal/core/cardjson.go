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
