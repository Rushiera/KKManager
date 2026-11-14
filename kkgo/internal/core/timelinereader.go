// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：场景卡 timeline 读取（Timeline 插件写在场景插件块 "timeline" 条目里）——长度 / 时间缩放 / 关键帧数 / 插值组。
// 语义逐条对齐 KKManager/Core/TimelineReader.cs 的读面（Read / ReadXml / Describe / FormatSeconds / SecondsText）：
//   - 条目恒在数据区尾段，扫描自尾部 16 MB 起步，未命中放大到 64 MB 再全量；
//   - sceneInfo XML 读上限 16 MB（超出只统计已读部分并标 Truncated）；
//   - 只读：不写文件；解析失败只记 Error / 空值，不抛异常。
package core

import (
	"math"
	"os"
	"strconv"
	"strings"
)

var (
	// timelineKeyAnchor timeline 条目的 MessagePack 键锚点——fixstr8 "timeline"。
	timelineKeyAnchor = []byte{0xA8, 0x74, 0x69, 0x6D, 0x65, 0x6C, 0x69, 0x6E, 0x65}
	// tlDurNeedle sceneInfo XML 里原时长属性的锚点——duration="。
	tlDurNeedle = []byte("duration=\"")
	// tlScaleNeedle sceneInfo XML 里时间缩放属性的锚点——timeScale="。
	tlScaleNeedle = []byte("timeScale=\"")
	// tlKeyframeNeedle 关键帧元素锚点——<keyframe 。
	tlKeyframeNeedle = []byte("<keyframe ")
	// tlGroupNeedle 插值组元素锚点——<interpolableGroup 。
	tlGroupNeedle = []byte("<interpolableGroup ")
	// tlTimeNeedle 时刻属性锚点——time="（关键帧时刻）。
	tlTimeNeedle = []byte("time=\"")
	// tlNameNeedle 名字属性锚点——name="（插值组名）。
	tlNameNeedle = []byte("name=\"")
)

// 窗口与上限（对齐 C# 同名常量）。
const (
	// tlTailNear 首轮尾部窗口——16 MB（实测条目距文件尾最大约 12.5 MB）。
	tlTailNear = 16 * 1024 * 1024
	// tlTailFar 次轮尾部窗口——64 MB（首轮未命中再放大一档）。
	tlTailFar = 64 * 1024 * 1024
	// tlMaxXMLRead sceneInfo XML 的读取上限——16 MB（超出只统计已读部分）。
	tlMaxXMLRead = 16 * 1024 * 1024
)

// TimelineInfo 场景卡的 timeline 数据（Timeline 插件写在场景插件块 "timeline" 条目里）——只读快照。
type TimelineInfo struct {
	// Scanned 是否扫过（只有场景卡才扫）。
	Scanned bool
	// HasEntry 是否存在 timeline 条目（场景里没用过 Timeline 插件就没有）。
	HasEntry bool
	// IsEmpty 空时间轴——有条目但没有任何关键帧（Timeline 的默认状态）。
	IsEmpty bool
	// Duration timeline 长度（秒，原值——Timeline 界面上的长度）。
	Duration float64
	// TimeScale 时间缩放（Timeline 的 timeScale 属性 = Unity 的 Time.timeScale；1 = 原速）。
	TimeScale float64
	// XmlLength sceneInfo XML 的字节数。
	XmlLength int64
	// XmlAt sceneInfo XML 在卡片文件里的起点偏移（0 = 未定位）。
	XmlAt int64
	// Keyframes XML 内关键帧数（读前 16 MB 计数）。
	Keyframes int
	// Truncated XML 被读取上限截断（关键帧数只统计了已读部分）。
	Truncated bool
	// Groups sceneInfo 里 interpolableGroup 数量（被 timeline 驱动的插值组 / 轨道）。
	Groups int
	// MaxKeyframeTime sceneInfo 里最长关键帧时刻（秒）——不等于 timeline 长度。
	MaxKeyframeTime float64
	// GroupNames 插值组名（Timeline 轨道——被驱动的部位 / 对象；去重，最多 200 个）。
	GroupNames []string
	// RealSeconds 实际播放时长（秒）= 原时长 / timeScale。
	RealSeconds float64
	// HitStage 命中 timeline 条目的扫描阶段（尾部 16MB / 尾部 64MB / 全量）。
	HitStage string
	// Error 读取失败的原因（失败必须可见）。
	Error string
}

// ReadTimeline 读一张场景卡的 timeline 数据（path = 卡片文件；imageEnd = 图片区结束偏移）。
func ReadTimeline(path string, imageEnd int64) *TimelineInfo {
	info := &TimelineInfo{Scanned: true, TimeScale: 1}
	if _, err := os.Stat(path); err != nil {
		info.Error = "卡片文件不存在：" + path
		return info
	}
	f, err := os.Open(path)
	if err != nil {
		info.Error = "卡片文件打不开：" + path
		return info
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		info.Error = "卡片文件打不开：" + path
		return info
	}
	size := fi.Size()
	from := int64(0)
	if imageEnd > 0 {
		from = imageEnd
	}
	if from >= size {
		info.Error = "数据区为空（图片区到文件尾没有字节）"
		return info
	}

	near := from
	if size-tlTailNear > from {
		near = size - tlTailNear
	}
	far := from
	if size-tlTailFar > from {
		far = size - tlTailFar
	}

	hit := int64(-1)
	stage := ""
	// [段1] 近窗优先——条目实测恒落在文件尾 16 MB 内（最大 ~12.5 MB），先扫近窗即命中
	hit = scanTimelineFor(f, size, near)
	if hit >= 0 {
		stage = "尾部 16MB"
	}
	if hit < 0 && far < near {
		hit = scanTimelineFor(f, near, far)
		if hit >= 0 {
			stage = "尾部 64MB"
		}
	}
	if hit < 0 && from < far {
		hit = scanTimelineFor(f, far, from)
		if hit >= 0 {
			stage = "全量"
		}
	}
	info.HitStage = stage
	if hit < 0 {
		info.HasEntry = false
		return info
	}

	info.HasEntry = true
	head := make([]byte, 512)
	n := 0
	for n < len(head) {
		m, _ := f.ReadAt(head[n:], hit+int64(n))
		n += m
		if m == 0 {
			break
		}
	}
	if n < 28 || head[9] != 0x92 || head[11] != 0x81 || head[12] != 0xA9 || string(head[13:22]) != "sceneInfo" {
		info.Error = "timeline 条目结构未识别（偏移 " + itoa(hit) + "）"
		return info
	}
	strHead := head[22]
	var xmlLen int64
	var xmlAt int64
	// 条目形态：A8 "timeline" + 92 00 + 81 A9 "sceneInfo" = 22 字节，串头落在 hit+22
	switch strHead {
	case 0xD9:
		xmlLen = int64(head[23])
		xmlAt = hit + 24
	case 0xDA:
		xmlLen = int64(head[23])<<8 | int64(head[24])
		xmlAt = hit + 25
	case 0xDB:
		xmlLen = int64(head[23])<<24 | int64(head[24])<<16 | int64(head[25])<<8 | int64(head[26])
		xmlAt = hit + 27
	default:
		info.Error = "sceneInfo 字符串头未识别（0x" + hex2(strHead) + "）"
		return info
	}
	if xmlLen <= 0 || xmlAt+xmlLen > size {
		info.Error = "sceneInfo 长度越界（" + itoa(xmlLen) + " 字节 @ " + itoa(xmlAt) + "）"
		return info
	}
	info.XmlLength = xmlLen
	info.XmlAt = xmlAt
	readTimelineXML(f, xmlAt, xmlLen, info)
	return info
}

// readTimelineXML 读 sceneInfo XML（上限 16 MB）——取 duration / timeScale / 关键帧数 / 插值组数 / 最长关键帧时刻 / 插值组名 / 空轴标记。
func readTimelineXML(f *os.File, xmlAt, xmlLen int64, info *TimelineInfo) {
	take := xmlLen
	if take > tlMaxXMLRead {
		take = tlMaxXMLRead
	}
	info.Truncated = xmlLen > tlMaxXMLRead
	xml := make([]byte, take)
	n := 0
	for int64(n) < take {
		m, _ := f.ReadAt(xml[n:], xmlAt+int64(n))
		n += m
		if m == 0 {
			break
		}
	}

	durationText, ok := readQuotedNumber(xml, n, tlDurNeedle)
	if !ok {
		info.Error = "sceneInfo 里没有 duration 属性"
		return
	}
	dur, err := strconv.ParseFloat(durationText, 64)
	if err != nil {
		info.Error = "duration 不是数字：" + durationText
		return
	}
	info.Duration = dur

	scaleText, okScale := readQuotedNumber(xml, n, tlScaleNeedle)
	scale := 0.0
	parsed := false
	if okScale {
		if v, e := strconv.ParseFloat(scaleText, 64); e == nil {
			scale = v
			parsed = true
		}
	}
	if parsed && scale > 0 {
		info.TimeScale = scale
	} else {
		info.TimeScale = 1
	}
	info.RealSeconds = info.Duration / info.TimeScale

	info.Keyframes = countNeedle(xml, n, tlKeyframeNeedle)
	info.Groups = countNeedle(xml, n, tlGroupNeedle)
	info.MaxKeyframeTime = maxQuotedNumber(xml, n, tlTimeNeedle)
	info.IsEmpty = info.Keyframes == 0 && !info.Truncated

	// 插值组名（Timeline 轨道）——从第一个 interpolableGroup 起取 name 属性，去重（上限 200）
	firstGroup := indexOfNeedle(xml, n, tlGroupNeedle)
	if firstGroup < 0 {
		firstGroup = 0
	}
	for i := firstGroup; i <= n-len(tlNameNeedle) && len(info.GroupNames) < 200; i++ {
		if xml[i] != tlNameNeedle[0] || !matchAt(xml, i, tlNameNeedle) {
			continue
		}
		name, ok := readQuotedText(xml, n, tlNameNeedle, i)
		if ok && name != "" && !containsString(info.GroupNames, name) {
			info.GroupNames = append(info.GroupNames, name)
		}
	}
}

// scanTimelineFor 在 [from, to) 区间流式找 timeline 条目锚点，返回文件偏移（未命中 -1）。
func scanTimelineFor(f *os.File, to, from int64) int64 {
	const bufSize = 1 << 20
	buf := make([]byte, bufSize+16)
	pos := from
	consumed := from
	carry := 0
	for pos < to {
		want := to - pos
		if want > bufSize {
			want = bufSize
		}
		read, _ := f.ReadAt(buf[carry:carry+int(want)], pos)
		if read <= 0 {
			break
		}
		pos += int64(read)
		baseOff := consumed - int64(carry)
		consumed += int64(read)
		total := carry + read
		at := indexOf(buf[:total], timelineKeyAnchor, 0)
		if at >= 0 {
			return baseOff + int64(at)
		}
		newCarry := total
		if newCarry > 15 {
			newCarry = 15
		}
		copy(buf[:newCarry], buf[total-newCarry:total])
		carry = newCarry
	}
	return -1
}

// readQuotedNumber 在字节缓冲里找 needle，随后读带引号的数值文本；未命中返回 false。
func readQuotedNumber(buf []byte, n int, needle []byte) (string, bool) {
	for i := 0; i <= n-len(needle); i++ {
		if buf[i] != needle[0] || !matchAt(buf, i, needle) {
			continue
		}
		var sb strings.Builder
		p := i + len(needle)
		for p < n {
			b := buf[p]
			ok := (b >= 0x30 && b <= 0x39) || b == 0x2E || b == 0x2D
			if !ok {
				break
			}
			sb.WriteByte(b)
			p++
		}
		return sb.String(), true
	}
	return "", false
}

// indexOfNeedle 找 needle 在缓冲里首次出现的位置；无则 -1。
func indexOfNeedle(buf []byte, n int, needle []byte) int {
	for i := 0; i <= n-len(needle); i++ {
		if buf[i] == needle[0] && matchAt(buf, i, needle) {
			return i
		}
	}
	return -1
}

// readQuotedText 读 needle 之后带引号的文本（UTF-8 解码；从 fromAt 起找 needle）；无则 false。
func readQuotedText(buf []byte, n int, needle []byte, fromAt int) (string, bool) {
	for i := fromAt; i <= n-len(needle); i++ {
		if buf[i] != needle[0] || !matchAt(buf, i, needle) {
			continue
		}
		start := i + len(needle)
		p := start
		for p < n && buf[p] != 0x22 {
			p++
		}
		return string(buf[start:p]), true
	}
	return "", false
}

// countNeedle 统计缓冲里 needle 出现的次数（命中后前进 needle 长度 + 1）。
func countNeedle(buf []byte, n int, needle []byte) int {
	count := 0
	for i := 0; i <= n-len(needle); i++ {
		if buf[i] == needle[0] && matchAt(buf, i, needle) {
			count++
			i += len(needle)
		}
	}
	return count
}

// maxQuotedNumber 扫全缓冲里 needle 之后带引号的数值文本，取最大值；没有返回 0。
func maxQuotedNumber(buf []byte, n int, needle []byte) float64 {
	best := 0.0
	for i := 0; i <= n-len(needle); i++ {
		if buf[i] != needle[0] || !matchAt(buf, i, needle) {
			continue
		}
		var sb strings.Builder
		p := i + len(needle)
		for p < n {
			b := buf[p]
			ok := (b >= 0x30 && b <= 0x39) || b == 0x2E || b == 0x2D
			if !ok {
				break
			}
			sb.WriteByte(b)
			p++
		}
		if v, err := strconv.ParseFloat(sb.String(), 64); err == nil && v > best {
			best = v
		}
	}
	return best
}

// FormatSeconds 秒 → mm:ss.ff（ff = 百分秒；各段补零，对齐 .NET 的 "00" 格式）。
func FormatSeconds(seconds float64) string {
	centis := int64(math.Round(seconds * 100.0))
	if centis < 0 {
		centis = 0
	}
	minutes := centis / 6000
	rest := (centis % 6000) / 100
	frac := centis % 100
	return zeroPad2(minutes) + ":" + zeroPad2(rest) + "." + zeroPad2(frac)
}

// zeroPad2 两位数补零（不足两位左侧补 0）。
func zeroPad2(v int64) string {
	s := strconv.FormatInt(v, 10)
	if len(s) < 2 {
		return strings.Repeat("0", 2-len(s)) + s
	}
	return s
}

// SecondsText 秒数文本（去掉多余的零）——281.5 / 39.5 / 67。
func SecondsText(seconds float64) string {
	return secondsText(seconds)
}

// secondsText 最多三位小数、去尾零（对齐 .NET ToString("0.###")）。
func secondsText(v float64) string {
	s := strconv.FormatFloat(v, 'f', 3, 64)
	if strings.Contains(s, ".") {
		s = strings.TrimRight(s, "0")
		s = strings.TrimRight(s, ".")
	}
	if s == "-0" {
		s = "0"
	}
	return s
}

// padLeft 左侧补空格到指定宽度（core 包内文本对齐用）。
func padLeft(s string, width int) string {
	if len(s) >= width {
		return s
	}
	return strings.Repeat(" ", width-len(s)) + s
}

// DescribeTimeline 面板与 CLI 共用的 timeline 文案。
func DescribeTimeline(t *TimelineInfo) string {
	if t == nil {
		return "—"
	}
	if t.Error != "" {
		return "读取失败：" + t.Error
	}
	if !t.HasEntry {
		return "无实际 timeline"
	}
	if t.IsEmpty {
		return "无实际 timeline（空轴）"
	}
	text := secondsText(t.Duration) + "(" + FormatSeconds(t.Duration) + ")"
	if math.Abs(t.TimeScale-1.0) > 0.0005 {
		text += " · 实际播放 " + FormatSeconds(t.RealSeconds) + "（timeScale " + secondsText(t.TimeScale) + "）"
	}
	return text
}
