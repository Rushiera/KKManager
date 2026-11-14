// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：sceneInfo XML 的完整 timeline 模型（组树 + 轨道 + 关键帧 + 属性无损保留），
// 语义逐条对齐 KKManager/Core/TimelineScene.cs + TimelineReader.ReadScene：
//   - XmlReader 流式 → encoding/xml token 流；注释 / 空白 / 处理指令 / DTD 忽略；
//   - 组树扁平存放（父子靠 ParentIndex），轨道与关键帧按 XML 顺序；
//   - 属性全集原样保留（跳过 xmlns 命名空间声明，对齐 XmlReader.MoveToFirstAttribute）；
//   - 只读：不写文件；解析失败只记 Error。
package core

import (
	"encoding/xml"
	"os"
	"strconv"
	"strings"
)

// TimelineAttr XML 元素的一个属性（名 + 原值）——属性无损保留，供后续写回重建元素。
type TimelineAttr struct {
	// Name 属性名（原样）。
	Name string
	// Value 属性值（原样文本）。
	Value string
}

// TimelineGroup 插值组节点（interpolableGroup）——Timeline 的轨道分组树：对象 → 部位 → 子部位。
type TimelineGroup struct {
	// Name 组名（Timeline 界面上的组名，如 全体 / 首・頭 / 表情 / 右手）。
	Name string
	// NameSet name 属性是否读到（区分「无该属性」与「读到空串」）。
	NameSet bool
	// ParentIndex 父组下标（-1 = 顶层）。
	ParentIndex int
	// Depth 层级深度（顶层 0）。
	Depth int
	// TrackCount 直接挂在本组下的轨道数（解析后回填）。
	TrackCount int
	// Attributes 组元素属性全集（原样）。
	Attributes []TimelineAttr
}

// TimelineTrack 一条轨道（interpolable）——被驱动的对象 / 部位 / 属性，带完整关键帧序列。
type TimelineTrack struct {
	// GroupIndex 所属组下标（-1 = 不属于任何组）。
	GroupIndex int
	// Owner 写入者（owner：Timeline / KKPE / NodesConstraints 等）。
	Owner string
	// ObjectIndex 场景对象序号（objectIndex；解析不到 = -1）。
	ObjectIndex int
	// Id 轨道类型 id（guideObjectPos / bonePos / characterEyes / cameraPos …）。
	Id string
	// IdSet id 属性是否读到（区分「无该属性」与「读到空串」）。
	IdSet bool
	// Alias 轨道显示名（alias，可为空）。
	Alias string
	// GuideObjectPath 被驱动对象路径（guideObjectPath，可为空）。
	GuideObjectPath string
	// Enabled 轨道是否启用（enabled 属性；缺省按 true）。
	Enabled bool
	// Attributes 轨道元素属性全集（原样）。
	Attributes []TimelineAttr
	// Keyframes 关键帧序列（按 XML 顺序）。
	Keyframes []TimelineKeyframe
}

// TimelineCurveKey 插值曲线控制点（curveKeyframe）——关键帧内的插值曲线，写回时必须保留。
type TimelineCurveKey struct {
	// Time 曲线点时刻。
	Time float64
	// Value 曲线点值。
	Value float64
	// InTangent 入切线。
	InTangent float64
	// OutTangent 出切线。
	OutTangent float64
}

// TimelineKeyframe 一个关键帧（keyframe）——时刻 + 值（四元数 / 向量 / 标量，按轨道类型取用）。
type TimelineKeyframe struct {
	// Time 时刻（秒，XML 原值——浮点，比较需容差）。
	Time float64
	// HasValue 是否有标量值（value 属性）。
	HasValue bool
	// Value 标量值（value）。
	Value float64
	// HasXYZW 是否有分量值（valueX / valueY / valueZ / valueW）。
	HasXYZW bool
	// ValueX 分量 X。
	ValueX float64
	// ValueY 分量 Y。
	ValueY float64
	// ValueZ 分量 Z。
	ValueZ float64
	// ValueW 分量 W（四元数的实部；旋转类轨道才有）。
	ValueW float64
	// Attributes 关键帧元素属性全集（原样）。
	Attributes []TimelineAttr
	// CurveKeys 插值曲线控制点（curveKeyframe）。
	CurveKeys []TimelineCurveKey
}

// TimelineScene sceneInfo XML 的完整模型——组树 + 轨道 + 关键帧，属性无损保留。
type TimelineScene struct {
	// HasEntry 是否存在 timeline 条目（场景里没用过 Timeline 插件则无）。
	HasEntry bool
	// IsEmpty 空时间轴（有条目但零关键帧）。
	IsEmpty bool
	// Duration 时间轴长度（秒）。
	Duration float64
	// TimeScale 时间缩放（1 = 原速）。
	TimeScale float64
	// BlockLength 分块长度（root 属性 blockLength）。
	BlockLength int
	// Divisions 分块数（root 属性 divisions）。
	Divisions int
	// RootAttributes 根元素属性全集（原样）。
	RootAttributes []TimelineAttr
	// Groups 组树（扁平存放，父子靠 ParentIndex）。
	Groups []TimelineGroup
	// Tracks 轨道清单（按 XML 顺序）。
	Tracks []TimelineTrack
	// XmlLength sceneInfo XML 的字节数。
	XmlLength int64
	// Truncated XML 超出读取上限（模型只含已读部分）。
	Truncated bool
	// Error 读取 / 解析失败的原因（失败必须可见）。
	Error string
}

// ReadTimelineScene 读一张场景卡的完整 timeline 模型（组树 + 轨道 + 关键帧）。
func ReadTimelineScene(path string, imageEnd int64) *TimelineScene {
	scene := &TimelineScene{TimeScale: 1}
	if _, err := os.Stat(path); err != nil {
		scene.Error = "卡片文件不存在：" + path
		return scene
	}
	info := ReadTimeline(path, imageEnd)
	if info.Error != "" {
		scene.Error = info.Error
		return scene
	}
	if !info.HasEntry {
		scene.HasEntry = false
		return scene
	}
	scene.HasEntry = true
	scene.XmlLength = info.XmlLength
	scene.Truncated = info.XmlLength > tlMaxXMLRead
	if info.XmlLength <= 0 || info.XmlAt <= 0 {
		scene.Error = "sceneInfo 区间不可用（偏移 " + strconv.FormatInt(info.XmlAt, 10) + "）"
		return scene
	}
	want := info.XmlLength
	if want > tlMaxXMLRead {
		want = tlMaxXMLRead
	}
	buf := make([]byte, want)
	f, err := os.Open(path)
	if err != nil {
		scene.Error = "卡片文件打不开：" + path
		return scene
	}
	defer f.Close()
	n := 0
	for int64(n) < want {
		m, _ := f.ReadAt(buf[n:], info.XmlAt+int64(n))
		n += m
		if m == 0 {
			break
		}
	}
	parsed := ParseTimelineScene(buf, n)
	parsed.HasEntry = true
	parsed.XmlLength = info.XmlLength
	parsed.Truncated = scene.Truncated
	parsed.IsEmpty = CountTimelineKeyframes(parsed) == 0
	return parsed
}

// CountTimelineKeyframes 模型里的关键帧总数（判空用）。
func CountTimelineKeyframes(scene *TimelineScene) int {
	total := 0
	for i := range scene.Tracks {
		total += len(scene.Tracks[i].Keyframes)
	}
	return total
}

// ParseTimelineScene 解析 sceneInfo XML 字节（length 为有效字节数）。
func ParseTimelineScene(raw []byte, length int) *TimelineScene {
	scene := &TimelineScene{TimeScale: 1}
	if len(raw) == 0 || length <= 0 {
		scene.Error = "sceneInfo XML 为空"
		return scene
	}
	if length > len(raw) {
		length = len(raw)
	}
	dec := xml.NewDecoder(strings.NewReader(string(raw[:length])))
	dec.Strict = true
	parseTimelineLoop(dec, scene)
	return scene
}

// parseTimelineLoop 主循环——按元素名分派（组栈 / 当前轨道 / 当前关键帧）。
func parseTimelineLoop(dec *xml.Decoder, scene *TimelineScene) {
	var groupStack []int
	trackIndex := -1
	keyIndex := -1
	// 空元素标记：encoding/xml 对 <x/> 会发 Start+End 两个 token（XmlReader 只发一个 Element），
	// 组栈 push/pop 对称，两种形态的最终栈状态一致。
	for {
		tok, err := dec.Token()
		if err != nil {
			break
		}
		switch node := tok.(type) {
		case xml.StartElement:
			name := node.Name.Local
			switch name {
			case "root":
				readTimelineRoot(node, scene)
			case "interpolableGroup":
				gi := addTimelineGroup(node, scene, groupStack)
				if gi >= 0 {
					groupStack = append(groupStack, gi)
				}
			case "interpolable":
				trackIndex = addTimelineTrack(node, scene, groupStack)
			case "keyframe":
				keyIndex = addTimelineKeyframe(node, scene, trackIndex)
			case "curveKeyframe":
				addTimelineCurveKey(node, scene, trackIndex, keyIndex)
			}
		case xml.EndElement:
			switch node.Name.Local {
			case "interpolableGroup":
				if len(groupStack) > 0 {
					groupStack = groupStack[:len(groupStack)-1]
				}
			case "interpolable":
				trackIndex = -1
				keyIndex = -1
			case "keyframe":
				keyIndex = -1
			}
		}
	}
	fillTimelineGroupTrackCounts(scene)
}

// readTimelineRoot 读根元素（duration / timeScale / blockLength / divisions + 属性全集）。
func readTimelineRoot(node xml.StartElement, scene *TimelineScene) {
	for _, a := range node.Attr {
		if a.Name.Space == "xmlns" || (a.Name.Space == "" && a.Name.Local == "xmlns") {
			continue
		}
		scene.RootAttributes = append(scene.RootAttributes, TimelineAttr{Name: a.Name.Local, Value: a.Value})
		switch a.Name.Local {
		case "duration":
			if d, ok := tryTimelineNum(a.Value); ok {
				scene.Duration = d
			}
		case "timeScale":
			if d, ok := tryTimelineNum(a.Value); ok && d > 0 {
				scene.TimeScale = d
			}
		case "blockLength":
			if d, ok := tryTimelineNum(a.Value); ok {
				scene.BlockLength = int(d)
			}
		case "divisions":
			if d, ok := tryTimelineNum(a.Value); ok {
				scene.Divisions = int(d)
			}
		}
	}
}

// addTimelineGroup 新增组节点，返回其下标（父 = 当前组栈顶）。
func addTimelineGroup(node xml.StartElement, scene *TimelineScene, groupStack []int) int {
	g := TimelineGroup{ParentIndex: -1, Depth: len(groupStack)}
	if len(groupStack) > 0 {
		g.ParentIndex = groupStack[len(groupStack)-1]
	}
	for _, a := range node.Attr {
		if a.Name.Space == "xmlns" || (a.Name.Space == "" && a.Name.Local == "xmlns") {
			continue
		}
		g.Attributes = append(g.Attributes, TimelineAttr{Name: a.Name.Local, Value: a.Value})
		if a.Name.Local == "name" {
			g.Name = a.Value
			g.NameSet = true
		}
	}
	scene.Groups = append(scene.Groups, g)
	return len(scene.Groups) - 1
}

// addTimelineTrack 新增轨道，返回其下标（所属组 = 当前组栈顶）。
func addTimelineTrack(node xml.StartElement, scene *TimelineScene, groupStack []int) int {
	t := TimelineTrack{GroupIndex: -1, ObjectIndex: -1, Enabled: true}
	if len(groupStack) > 0 {
		t.GroupIndex = groupStack[len(groupStack)-1]
	}
	for _, a := range node.Attr {
		if a.Name.Space == "xmlns" || (a.Name.Space == "" && a.Name.Local == "xmlns") {
			continue
		}
		t.Attributes = append(t.Attributes, TimelineAttr{Name: a.Name.Local, Value: a.Value})
		switch a.Name.Local {
		case "owner":
			t.Owner = a.Value
		case "id":
			t.Id = a.Value
			t.IdSet = true
		case "alias":
			t.Alias = a.Value
		case "guideObjectPath":
			t.GuideObjectPath = a.Value
		case "objectIndex":
			if v, err := strconv.Atoi(strings.TrimSpace(a.Value)); err == nil {
				t.ObjectIndex = v
			}
		case "enabled":
			t.Enabled = a.Value != "false"
		}
	}
	scene.Tracks = append(scene.Tracks, t)
	return len(scene.Tracks) - 1
}

// addTimelineKeyframe 新增关键帧，返回其在轨道内的下标（无宿主轨道返回 -1）。
func addTimelineKeyframe(node xml.StartElement, scene *TimelineScene, trackIndex int) int {
	if trackIndex < 0 || trackIndex >= len(scene.Tracks) {
		return -1
	}
	var kf TimelineKeyframe
	for _, a := range node.Attr {
		if a.Name.Space == "xmlns" || (a.Name.Space == "" && a.Name.Local == "xmlns") {
			continue
		}
		kf.Attributes = append(kf.Attributes, TimelineAttr{Name: a.Name.Local, Value: a.Value})
		d, ok := tryTimelineNum(a.Value)
		if !ok {
			continue
		}
		switch a.Name.Local {
		case "time":
			kf.Time = d
		case "value":
			kf.HasValue = true
			kf.Value = d
		case "valueX":
			kf.HasXYZW = true
			kf.ValueX = d
		case "valueY":
			kf.HasXYZW = true
			kf.ValueY = d
		case "valueZ":
			kf.HasXYZW = true
			kf.ValueZ = d
		case "valueW":
			kf.HasXYZW = true
			kf.ValueW = d
		}
	}
	scene.Tracks[trackIndex].Keyframes = append(scene.Tracks[trackIndex].Keyframes, kf)
	return len(scene.Tracks[trackIndex].Keyframes) - 1
}

// addTimelineCurveKey 新增插值曲线控制点（挂在当前关键帧下）。
func addTimelineCurveKey(node xml.StartElement, scene *TimelineScene, trackIndex, keyIndex int) {
	if trackIndex < 0 || trackIndex >= len(scene.Tracks) {
		return
	}
	keys := scene.Tracks[trackIndex].Keyframes
	if keyIndex < 0 || keyIndex >= len(keys) {
		return
	}
	var ck TimelineCurveKey
	for _, a := range node.Attr {
		d, ok := tryTimelineNum(a.Value)
		if !ok {
			continue
		}
		switch a.Name.Local {
		case "time":
			ck.Time = d
		case "value":
			ck.Value = d
		case "inTangent":
			ck.InTangent = d
		case "outTangent":
			ck.OutTangent = d
		}
	}
	keys[keyIndex].CurveKeys = append(keys[keyIndex].CurveKeys, ck)
}

// fillTimelineGroupTrackCounts 回填每组直接挂载的轨道数。
func fillTimelineGroupTrackCounts(scene *TimelineScene) {
	for i := range scene.Tracks {
		gi := scene.Tracks[i].GroupIndex
		if gi >= 0 && gi < len(scene.Groups) {
			scene.Groups[gi].TrackCount++
		}
	}
}

// tryTimelineNum 按不变文化解析数值（对齐 double.TryParse NumberStyles.Float）。
func tryTimelineNum(text string) (float64, bool) {
	s := strings.TrimSpace(text)
	if s == "" {
		return 0, false
	}
	v, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return 0, false
	}
	return v, true
}
