// kkgo — KK Manager 的 Go 重写（对照 C# 0.21.9）。
//
// 本轮已移植：modinfo / cardinfo / card-structure（解析核最小闭环）。
// 命令面与输出格式对齐 KMR.exe 同名命令（Program.cs §ModInfoCommand / §CardInfoCommand / §CardStructureCommand）。
package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// version 构建版本（发布时由 -ldflags "-X main.version=..." 注入）。
var version = "0.1.0-dev"

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	if len(args) < 1 {
		usage()
		return 2
	}
	cmd := args[0]
	rest := args[1:]
	switch cmd {
	case "modinfo":
		if len(rest) < 1 {
			return requireArgs("modinfo <zipmod>")
		}
		return cmdModInfo(rest[0])
	case "cardinfo":
		if len(rest) < 1 {
			return requireArgs("cardinfo <card.png>")
		}
		return cmdCardInfo(rest[0])
	case "card-structure":
		if len(rest) < 1 {
			return requireArgs("card-structure <card.png>")
		}
		return cmdCardStructure(rest[0])
	case "card-detail":
		if len(rest) < 1 {
			return requireArgs("card-detail <card.png>")
		}
		return cmdCardDetail(rest[0])
	case "card-coord":
		if len(rest) < 1 {
			return requireArgs("card-coord <card.png> [--all]")
		}
		return cmdCardCoord(rest)
	case "card-params":
		if len(rest) < 1 {
			return requireArgs("card-params <card.png|dir> [--recurse]")
		}
		return cmdCardParams(rest)
	case "card-scene":
		if len(rest) < 1 {
			return requireArgs("card-scene <card.png>")
		}
		return cmdCardScene(rest[0])
	case "card-analysis":
		if len(rest) < 1 {
			return requireArgs("card-analysis <card.png> [coord|detail|scene]...")
		}
		return cmdCardAnalysis(rest)
	case "roots-list":
		return cmdRootsList(rest)
	case "roots-add":
		return cmdRootsAdd(rest)
	case "roots-clear":
		return cmdRootsClear(rest)
	case "set-game-root":
		return cmdSetGameRoot(rest)
	case "scan-plan":
		return cmdScanPlan(rest)
	case "card-timeline":
		if len(rest) < 1 {
			return requireArgs("card-timeline <card.png> [tracks]")
		}
		return cmdCardTimeline(rest)
	case "thumb":
		if len(rest) < 2 {
			return requireArgs("thumb <card.png> <out.jpg> [--thumb N] [--quality Q]")
		}
		return cmdThumb(rest)
	case "filehash":
		if len(rest) < 1 {
			return requireArgs("filehash <path>")
		}
		return cmdFileHash(rest[0])
	case "modfiles":
		if len(rest) < 1 {
			return requireArgs("modfiles <guid> [--db <db>]")
		}
		return cmdModFiles(rest)
	case "composition":
		return cmdComposition(rest)
	case "plugin-info":
		if len(rest) < 1 {
			return requireArgs("plugin-info <dll>")
		}
		return cmdPluginInfo(rest[0])
	case "plugin-config":
		return cmdPluginConfig(rest)
	case "plugins":
		return cmdPlugins(rest)
	case "version":
		fmt.Println("kkgo " + version)
		return 0
	case "help":
		usage()
		return 0
	default:
		fmt.Fprintln(os.Stderr, "未知命令: "+cmd)
		usage()
		return 2
	}
}

func cmdModInfo(path string) int {
	m := core.ParseZipMod(path)
	fmt.Println("文件       : " + m.FileName)
	fmt.Println("大小       : " + n0(m.Size) + " 字节")
	fmt.Println("guid       : " + fieldOrNone(m.Guid, m.Found["guid"]))
	fmt.Println("name       : " + fieldOrNone(m.Name, m.Found["name"]))
	fmt.Println("version    : " + fieldOrNone(m.Version, m.Found["version"]))
	fmt.Println("author     : " + fieldOrNone(m.Author, m.Found["author"]))
	fmt.Println("website    : " + fieldOrNone(m.Website, m.Found["website"]))
	fmt.Println("schema-ver : " + m.SchemaVer)
	fmt.Println("条目数     : " + itoa(m.EntryCount))
	if m.FileNameMatchesGuid() {
		fmt.Println("文件名=guid: 是")
	} else {
		fmt.Println("文件名=guid: 否")
	}
	if m.Error != "" {
		fmt.Println("诊断       : " + m.Error)
		return 1
	}
	return 0
}

func cmdCardInfo(path string) int {
	c, err := core.ParseCard(path)
	if err != nil {
		fmt.Println("诊断       : " + err.Error())
		return 1
	}
	fmt.Println("文件       : " + c.FileName)
	fmt.Println("大小       : " + n0(c.Size) + " 字节")
	fmt.Println("图片区结束 : " + n0(c.ImageEnd) + "（数据区 " + n0(c.Size-c.ImageEnd) + " 字节）")
	fmt.Println("卡类型     : " + orNone(c.CardType))
	fmt.Println("数据标记   : " + orNone(c.DataVersion))
	fmt.Println("声明区数   : " + itoa(c.UarBlocks))
	fmt.Println("引用条目   : " + itoa(len(c.ModRefs)))
	ids := c.DistinctModIds()
	fmt.Println("去重 mod   : " + itoa(len(ids)))
	for _, id := range ids {
		fmt.Println("  - " + id)
	}
	return 0
}

func cmdCardStructure(path string) int {
	st := core.ParseCardStructure(path)
	fmt.Println("文件       : " + st.FilePath)
	mtime := "?"
	if st.Mtime != "" {
		mtime = st.Mtime
	}
	fmt.Println("大小       : " + n0(st.Size) + " 字节 · 改于 " + mtime)
	fmt.Println()
	fmt.Println("PNG 块表   : " + itoa(len(st.Chunks)) + " 个")
	for _, ck := range st.Chunks {
		crcText := "未校验"
		if ck.CrcChecked {
			if ck.CrcOk {
				crcText = "OK"
			} else {
				crcText = "不符"
			}
		}
		fmt.Println("  @" + padLeft(n0(ck.Offset), 14) + "  " + padRight(ck.Type, 5) +
			" len=" + padLeft(n0(ck.Length), 12) + "  CRC=" + crcText)
	}
	fmt.Println()
	if st.ImageEnd < 0 {
		fmt.Println("图片区结束 : <未找到>")
	} else {
		fmt.Println("图片区结束 : " + n0(st.ImageEnd))
	}
	start := "?"
	if st.ImageEnd >= 0 {
		start = n0(st.ImageEnd)
	}
	fmt.Println("数据区     : " + n0(st.DataSize) + " 字节（起点 " + start + "）")
	fmt.Println("卡类型     : " + orNone(st.CardType))
	fmt.Println("数据标记   : " + orNone(st.DataVersion))
	fmt.Println("数据区首64 : " + orNone(st.DataHeadHex))
	for _, w := range st.Warnings {
		fmt.Println("提示       : " + w)
	}
	if st.Error != "" {
		fmt.Println("诊断       : " + st.Error)
		return 1
	}
	return 0
}

func cmdCardDetail(path string) int {
	st := core.ParseCardStructure(path)
	if st.ImageEnd <= 0 {
		errText := "<无>"
		if st.Error != "" {
			errText = st.Error
		}
		fmt.Println("图片区终点未知——无法解析数据区：" + errText)
		return 3
	}
	filePath := path
	if st.FilePath != "" {
		filePath = st.FilePath
	}
	d := core.ParseCardDetail(filePath, st.ImageEnd)
	fmt.Println("文件       : " + filePath)
	fmt.Println("卡类型     : " + orNone(st.CardType) + " · 数据标记 " + orNone(st.DataVersion))
	fmt.Println("角色名     : " + orNone(d.CharName))
	if d.FaceImageSize > 0 {
		fmt.Println("脸图       : " + n0(d.FaceImageSize) + " 字节 @" + n0(d.FaceImageOffset))
	}
	fmt.Println("数据区     : " + n0(st.DataSize) + " 字节（起点 " + n0(st.ImageEnd) + "）")
	fmt.Println()
	tail := ""
	if st.DataSize > 0 {
		tail = "（占数据区 " + strconv.FormatFloat(float64(d.ImageBytesTotal)*100.0/float64(st.DataSize), 'f', 1, 64) + "%）"
	}
	fmt.Println("内嵌图片   : " + itoa(len(d.Images)) + " 张 · 合计 " + n0(d.ImageBytesTotal) + " 字节" + tail)
	for _, img := range d.Images {
		line := "  #" + padLeft(itoa(img.Index), 3) + " @" + padLeft(n0(img.Offset), 14)
		line += "  " + padLeft(n0(img.Size), 12) + " 字节  " + itoa(img.Width) + "×" + itoa(img.Height)
		line += "  位深 " + itoa(img.BitDepth) + " 色型 " + itoa(img.ColorType) + "  块 " + itoa(img.Chunks)
		if !img.Ended {
			line += "  [未闭合]"
		}
		if img.IsFace {
			line += "  [脸图]"
		}
		fmt.Println(line)
	}
	fmt.Println()
	fmt.Println("服装部件   : " + itoa(len(d.Parts)) + " 件")
	for _, p := range d.Parts {
		idText := "?"
		if p.Id >= 0 {
			idText = strconv.FormatInt(p.Id, 10)
		}
		fmt.Println("  #" + itoa(p.Index) + "  id=" + idText + "  键: " + strings.Join(p.Keys, ", "))
	}
	fmt.Println()
	fmt.Println("数据块目录 : " + itoa(len(d.Blocks)) + " 条")
	for _, b := range d.Blocks {
		ver := b.Version
		if !b.VersionSet {
			ver = "?"
		}
		fmt.Println("  " + b.Name + "  v" + ver + "  pos=" + n0(b.Pos) + "  size=" + n0(b.Size))
	}
	fmt.Println()
	fmt.Println("插件块     : " + itoa(len(d.KkxOffsets)) + " 处 · 插件名 " + itoa(len(d.PluginNames)) + " 个")
	for _, name := range d.PluginNames {
		fmt.Println("  - " + name)
	}
	fmt.Println()
	seen := make(map[string]bool, len(d.ModRefs))
	distinct := 0
	for _, m := range d.ModRefs {
		if m.ModId != "" && !seen[m.ModId] {
			seen[m.ModId] = true
			distinct++
		}
	}
	fmt.Println("声明区条目 : " + itoa(len(d.ModRefs)) + " 条（去重 " + itoa(distinct) + " 个 mod）")
	for _, m := range d.ModRefs {
		prop := m.Property
		if !m.PropertySet {
			prop = "?"
		}
		line := "  " + prop + "  slot=" + itoa(m.Slot) + "  " + m.ModId
		if m.AuthorSet {
			line += "  作者 " + m.Author
		}
		fmt.Println(line)
	}
	for _, w := range d.Warnings {
		fmt.Println("提示       : " + w)
	}
	return 0
}

func cmdCardScene(path string) int {
	st := core.ParseCardStructure(path)
	if st.Error != "" {
		fmt.Println("诊断       : " + st.Error)
		return 3
	}
	filePath := path
	if st.FilePath != "" {
		filePath = st.FilePath
	}
	fmt.Println("文件       : " + filePath)
	fmt.Println("卡类型     : " + orNone(st.CardType) + " · 数据标记 " + orNone(st.DataVersion))
	if st.CardType != core.SceneCardType {
		fmt.Println("场景分析   : 不是场景卡——只有 Studio 场景卡（sd）带插件数据条目与内嵌角色数据")
		return 0
	}
	s := core.ReadSceneInfo(filePath, st.ImageEnd)
	if s.Error != "" {
		fmt.Println("诊断       : " + s.Error)
		return 3
	}
	fmt.Println("数据区     : " + n0(st.DataSize) + " 字节 · 已扫描 " + n0(s.ScannedBytes) + " 字节")
	fmt.Println("内嵌角色   : " + itoa(s.CharaDataCount) + " 份（数据块目录命中）")
	cd := core.ParseCardDetail(filePath, st.ImageEnd)
	s.AttachCharaFaces(cd.Images)
	fmt.Println("内嵌角色首图: " + itoa(len(s.CharaFaces)) + " 张（每份一张——该份角色卡的卡面图）")
	for _, face := range s.CharaFaces {
		line := "  #" + padLeft(itoa(face.Index), 3) + " @" + padLeft(n0(face.Offset), 14)
		line += "  " + padLeft(n0(face.Size), 12) + " 字节  " + itoa(face.Width) + "×" + itoa(face.Height)
		if face.IsFace {
			line += "  [脸图]"
		}
		fmt.Println(line)
	}
	if len(s.CharaData) > 0 {
		fmt.Println("内嵌角色明细: " + itoa(len(s.CharaData)) + " 份（份头段 × 数据块目录）")
		for _, chd := range s.CharaData {
			line := "  第 " + itoa(chd.Index) + " 份 · 数据版本 " + verOrQ(chd.DataVersion, chd.DataVersionSet) +
				" · 卡面图 @" + n0(chd.FaceOffset) + "（" + n0(chd.FaceSize) + " 字节）" +
				" · 块 " + itoa(len(chd.Blocks)) + " 个："
			for bi, b := range chd.Blocks {
				if bi > 0 {
					line += " · "
				}
				line += b.Name + " v" + b.Version + "（" + n0(b.Size) + " 字节）"
			}
			fmt.Println(line)
		}
	}
	fmt.Println("插件条目   : " + itoa(len(s.Plugins)) + " 条")
	for _, it := range s.Plugins {
		line := "  @" + padLeft(n0(it.Offset), 14) + "  " + padRightW(it.Key, 22)
		if it.Version == "" {
			line += "        "
		} else {
			line += padRightW("v"+it.Version, 8)
		}
		line += "  " + padRightW(it.Shape, 18)
		if it.Bytes > 0 {
			line += "  " + padLeft(n0(it.Bytes), 12) + " 字节"
		} else {
			line += "              "
		}
		if it.Summary != "" {
			line += "  " + it.Summary
		}
		fmt.Println(line)
	}
	if s.KkpeItemCount >= 0 {
		fmt.Println("场景道具   : itemInfo " + n0(int64(s.KkpeItemCount)) + " 条 · 名字 " + itoa(len(s.PropNames)) + " 种（kkpe 记录）")
		shown := 0
		for _, nc := range s.PropNames {
			if shown >= 30 {
				break
			}
			fmt.Println("  ×" + padLeft(itoa(nc.Count), 4) + "  " + nc.Name)
			shown++
		}
	}
	if s.SssbObjects >= 0 {
		fmt.Println("场景快照   : 对象 " + itoa(s.SssbObjects) + " 个（相机 " + itoa(s.SssbCameras) + " 个——vnge_sssb）")
	}
	t := core.ReadTimeline(filePath, st.ImageEnd)
	fmt.Println("timeline   : " + core.DescribeTimeline(t))
	if t.Error == "" && t.HasEntry && !t.IsEmpty {
		fmt.Println("  timeline 深度: 关键帧 " + n0(int64(t.Keyframes)) + " · 插值组 " + itoa(t.Groups) +
			" · 最长关键帧 " + core.FormatSeconds(t.MaxKeyframeTime))
		if len(t.GroupNames) > 0 {
			fmt.Println("  轨道组名 : " + strings.Join(t.GroupNames, " · "))
		}
	}
	for _, w := range s.Warnings {
		fmt.Println("提示       : " + w)
	}
	return 0
}

// cmdCardAnalysis 分项分析序列化（与 C# CardAnalysis.Run 落库的 JSON 同形状）。
// 输出：每行「kind<TAB>紧凑 JSON」；被跳过的分项与失败原因走 stderr（不污染 JSON 行）。
func cmdCardAnalysis(rest []string) int {
	results, skipped, err := core.AnalyzeCard(rest[0], rest[1:])
	for _, s := range skipped {
		fmt.Fprintln(os.Stderr, "跳过 "+s)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 3
	}
	for _, r := range results {
		fmt.Println(r.Kind + "\t" + r.JSON)
	}
	return 0
}

// verOrQ 版本文本（未读到 → ?；读到空串原样输出）。
func verOrQ(s string, set bool) string {
	if !set {
		return "?"
	}
	return s
}

func cmdCardCoord(args []string) int {
	all := false
	rest := make([]string, 0, len(args))
	for _, a := range args {
		if a == "--all" {
			all = true
			continue
		}
		rest = append(rest, a)
	}
	if len(rest) < 1 {
		return requireArgs("card-coord <card.png> [--all]")
	}
	r := core.ReadCardCoordinate(rest[0])
	filePath := r.FilePath
	if filePath == "" {
		filePath = rest[0]
	}
	fmt.Println("文件       : " + filePath)
	if r.Error != "" {
		fmt.Println("诊断       : " + r.Error)
		return 3
	}
	ver := r.BlockVersion
	if !r.BlockVersionSet {
		ver = "?"
	}
	scope := "（只列非空槽位 · --all 列全部）"
	if all {
		scope = "（全量列出）"
	}
	fmt.Println("Coordinate : v" + ver + " · " + n0(r.BlockSize) + " 字节 · " + itoa(len(r.Outfits)) + " 套" + scope)
	for _, o := range r.Outfits {
		fmt.Println()
		makeup := "开"
		if !o.EnableMakeup {
			makeup = "关"
		}
		makeupTail := ""
		if o.MakeupText != "" {
			makeupTail = "（" + o.MakeupText + "）"
		}
		fmt.Println("[" + itoa(o.Index) + "] " + o.TypeName +
			" — 服装 " + itoa(o.ClothesCount()) + " 件 · 饰品 " + itoa(o.AccessoryCount()) + " 件" +
			" · 化妆 " + makeup + makeupTail)
		for _, p := range o.Clothes {
			if !all && p.Id == 0 {
				continue
			}
			fmt.Println("  服装 " + p.Slot + "： id=" + itoa(p.Id) +
				" · 袖型 " + itoa(p.SleevesType) +
				" · 徽章 " + itoa(p.EmblemeId) + "/" + itoa(p.EmblemeId2) +
				" · 隐藏 " + yesNo(p.HideOptA) + "/" + yesNo(p.HideOptB))
			for _, c := range p.Colors {
				fmt.Println("        " + c)
			}
		}
		for _, p := range o.Accessories {
			if !all && p.Id == 0 {
				continue
			}
			parentTail := ""
			if p.ParentKeySet {
				parentTail = "（" + p.ParentKey + "）"
			}
			fmt.Println("  饰品 #" + itoa(p.Index) + " " + p.TypeName + "： id=" + itoa(p.Id) +
				" · 挂点 " + p.ParentName + parentTail +
				" · 不晃 " + yesNo(p.NoShake) +
				" · 隐藏类别 " + itoa(p.HideCategory))
			for _, c := range p.Colors {
				fmt.Println("        " + c)
			}
		}
		for _, w := range o.Warnings {
			fmt.Println("  提示： " + w)
		}
	}
	for _, w := range r.Warnings {
		fmt.Println("提示       : " + w)
	}
	return 0
}

// yesNo 布尔显示（是 / 否）。
func yesNo(v bool) string {
	if v {
		return "是"
	}
	return "否"
}

func cmdCardParams(rest []string) int {
	target := rest[0]
	recurse := false
	for _, a := range rest[1:] {
		if a == "--recurse" {
			recurse = true
		}
	}
	if fi, err := os.Stat(target); err == nil && fi.IsDir() {
		return cardParamsDir(target, recurse)
	}
	layout := core.ParseCardLayout(target)
	if layout.Error != "" {
		fmt.Println("布局解析失败：" + layout.Error)
		return 3
	}
	cur := core.ReadCardParams(target, layout)
	fmt.Println("文件       : " + target)
	fmt.Println("卡类型     : " + orNone(layout.CardType) + " · 数据标记 " + orNone(layout.DataVersion))
	fmt.Println("图片区     : " + n0(layout.ImageEnd) + " 字节 · 脸图 " + n0(layout.FaceSize) +
		" 字节 @" + n0(layout.FaceOffset))
	fmt.Println("块表       : " + n0(layout.TableSize) + " 字节 @" + n0(layout.TableAt) +
		" · 载荷起点 " + n0(layout.PayloadStart) + " · 载荷 " + n0(layout.PayloadSize) + " 字节")
	for _, b := range layout.Blocks {
		fmt.Println("  " + padRightW(b.Name, 12) + " v" + b.Version +
			"  pos=" + n0(b.Pos) + "  size=" + n0(b.Size))
	}
	if cur.Error != "" {
		fmt.Println("字段读取失败：" + cur.Error)
		return 3
	}
	pnText := "(未收录——表外 ID)"
	if pn, ok := core.PersonalityNameOf(cur.Personality); ok {
		pnText = pn
	}
	fmt.Println("字段       : 姓「" + cur.LastName + "」 名「" + cur.FirstName + "」 爱称「" + cur.NickName + "」")
	fmt.Println("性格       : " + itoa(cur.Personality) + " " + pnText)
	fmt.Println("值区间     : 姓 @" + n0(cur.LastNameAt) + " +" + itoa(cur.LastNameLen) +
		" · 名 @" + n0(cur.FirstNameAt) + " +" + itoa(cur.FirstNameLen) +
		" · 爱称 @" + n0(cur.NickNameAt) + " +" + itoa(cur.NickNameLen) +
		" · 性格 @" + n0(cur.PersonalityAt) + " +" + itoa(cur.PersonalityLen))
	wpText := "(未收录——表外 ID)"
	if wp, ok := core.WeakPointNameOf(cur.WeakPoint); ok {
		wpText = wp
	}
	fmt.Println("敏感带     : " + itoa(cur.WeakPoint) + " " + wpText +
		" · 值区间 @" + n0(cur.WeakPointAt) + " +" + itoa(cur.WeakPointLen))
	var den strings.Builder
	for i := range core.DenialKeys {
		if i > 0 {
			den.WriteString(" · ")
		}
		den.WriteString("接受「" + core.DenialNames[i] + "」")
		den.WriteString(yesNo(cur.Denial[i]))
		den.WriteString(" @" + n0(cur.DenialAt[i]) + " +" + itoa(cur.DenialLen[i]))
	}
	fmt.Println("是否接受   : " + den.String())
	return 0
}

// cardParamsDir 目录分支——全量清单 + 性格 / 敏感带取值分布。
func cardParamsDir(dir string, recurse bool) int {
	files := collectCards(dir, recurse)
	fmt.Println("目录       : " + dir + " · 卡片 " + itoa(len(files)) + " 张")
	dist := make(map[int]int)
	wpDist := make(map[int]int)
	denialYes := make([]int, len(core.DenialKeys))
	noParam := 0
	for _, f := range files {
		layout := core.ParseCardLayout(f)
		info := core.ReadCardParams(f, layout)
		if !info.HasParameter {
			noParam++
			tail := "无 Parameter 块"
			if info.Error != "" {
				tail = info.Error
			}
			fmt.Println("  " + filepath.Base(f) + "  —  " + tail)
			continue
		}
		pnText := "(未收录)"
		if pn, ok := core.PersonalityNameOf(info.Personality); ok {
			pnText = pn
		}
		wpText := "(未收录)"
		if wp, ok := core.WeakPointNameOf(info.WeakPoint); ok {
			wpText = wp
		}
		fmt.Println("  " + filepath.Base(f) + "  姓「" + info.LastName + "」 名「" + info.FirstName +
			"」 爱称「" + info.NickName + "」  性格 " + itoa(info.Personality) + " " + pnText +
			"  敏感带 " + itoa(info.WeakPoint) + " " + wpText)
		wpDist[info.WeakPoint]++
		for i := range core.DenialKeys {
			if info.Denial[i] {
				denialYes[i]++
			}
		}
		dist[info.Personality]++
	}
	fmt.Println()
	fmt.Println("性格分布   : " + itoa(len(dist)) + " 种 · 无 Parameter " + itoa(noParam) + " 张")
	for _, id := range sortedIntKeys(dist) {
		nameText := "未收录"
		if raw, ok := core.PersonalityNameOf(id); ok {
			nameText = raw
		}
		fmt.Println("  ID " + padLeftW(itoa(id), 3) + "  " + padRightW(nameText, 18) + "  " +
			itoa(dist[id]) + " 张")
	}
	fmt.Println()
	fmt.Println("敏感带分布 : " + itoa(len(wpDist)) + " 种")
	for _, id := range sortedIntKeys(wpDist) {
		nameText := "未收录"
		if raw, ok := core.WeakPointNameOf(id); ok {
			nameText = raw
		}
		fmt.Println("  ID " + padLeftW(itoa(id), 3) + "  " + padRightW(nameText, 18) + "  " +
			itoa(wpDist[id]) + " 张")
	}
	fmt.Println("是否接受分布（是 / 卡片数）: ")
	for i := range core.DenialKeys {
		fmt.Println("  " + padRightW(core.DenialNames[i], 8) + " " +
			itoa(denialYes[i]) + " / " + itoa(len(files)))
	}
	return 0
}

// collectCards 收集目录下的卡片文件（.png）——recurse 为真时进子目录；按完整路径忽略大小写升序。
func collectCards(dir string, recurse bool) []string {
	var files []string
	if recurse {
		_ = filepath.Walk(dir, func(p string, info os.FileInfo, err error) error {
			if err != nil || info == nil || info.IsDir() {
				return nil
			}
			if strings.EqualFold(filepath.Ext(p), ".png") {
				files = append(files, p)
			}
			return nil
		})
	} else {
		entries, err := os.ReadDir(dir)
		if err == nil {
			for _, e := range entries {
				if e.IsDir() {
					continue
				}
				if strings.EqualFold(filepath.Ext(e.Name()), ".png") {
					files = append(files, filepath.Join(dir, e.Name()))
				}
			}
		}
	}
	sort.Slice(files, func(i, j int) bool {
		return strings.ToLower(files[i]) < strings.ToLower(files[j])
	})
	return files
}

// sortedIntKeys 取 map 的键并按升序排列。
func sortedIntKeys(m map[int]int) []int {
	keys := make([]int, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Ints(keys)
	return keys
}

// padLeftW 按字符数右对齐补空格（对齐 .NET PadLeft 的 UTF-16 码元口径）。
func padLeftW(s string, width int) string {
	n := utf8.RuneCountInString(s)
	if n >= width {
		return s
	}
	return strings.Repeat(" ", width-n) + s
}

// padRightW 按字符数左对齐补空格（对齐 .NET PadRight 的 UTF-16 码元口径）。
func padRightW(s string, width int) string {
	n := utf8.RuneCountInString(s)
	if n >= width {
		return s
	}
	return s + strings.Repeat(" ", width-n)
}

// cmdCardTimeline 场景卡 timeline 数据（原时长 / 时间缩放 / 关键帧 / 插值组；tracks 时输出轨道树）。
func cmdCardTimeline(args []string) int {
	tracks := len(args) > 1 && args[1] == "tracks"
	st := core.ParseCardStructure(args[0])
	if st.Error != "" {
		fmt.Println("诊断       : " + st.Error)
		return 3
	}
	filePath := args[0]
	if st.FilePath != "" {
		filePath = st.FilePath
	}
	fmt.Println("文件       : " + filePath)
	fmt.Println("卡类型     : " + orNone(st.CardType) + " · 数据标记 " + orNone(st.DataVersion))
	if st.CardType != core.SceneCardType {
		fmt.Println("timeline   : 不是场景卡——只有 Studio 场景卡带 timeline 数据")
		return 0
	}
	t := core.ReadTimeline(filePath, st.ImageEnd)
	fmt.Println("timeline   : " + core.DescribeTimeline(t))
	if t.Error == "" && t.HasEntry {
		fmt.Println("  原时长   : " + core.SecondsText(t.Duration) + " 秒（" + core.FormatSeconds(t.Duration) + "）")
		fmt.Println("  timeScale: " + core.SecondsText(t.TimeScale) + "（实际播放 " + core.FormatSeconds(t.RealSeconds) + "）")
		fmt.Println("  关键帧   : " + n0(int64(t.Keyframes)) + " 个 · 插值组 " + itoa(t.Groups) +
			" · 最长关键帧 " + core.FormatSeconds(t.MaxKeyframeTime) +
			" · sceneInfo XML " + n0(t.XmlLength) + " 字节 · 命中阶段 " + orNone(t.HitStage))
	}
	if tracks {
		dumpTimelineTracks(filePath, st.ImageEnd)
	}
	return 0
}

// dumpTimelineTracks 输出 timeline 轨道树（组 → 轨道 → 帧数）——完整模型旁证（只读）。
func dumpTimelineTracks(path string, imageEnd int64) {
	scene := core.ReadTimelineScene(path, imageEnd)
	if scene.Error != "" {
		fmt.Println("轨道模型   : 读取失败——" + scene.Error)
		return
	}
	if !scene.HasEntry {
		fmt.Println("轨道模型   : 无 timeline 条目")
		return
	}
	cut := ""
	if scene.Truncated {
		cut = "（超上限截断）"
	}
	fmt.Println("轨道模型   : 组 " + itoa(len(scene.Groups)) + " · 轨道 " + itoa(len(scene.Tracks)) +
		" · XML " + n0(scene.XmlLength) + " 字节" + cut)
	for _, g := range scene.Groups {
		pad := strings.Repeat(" ", g.Depth*2)
		fmt.Println("  " + pad + "- " + g.Name + "（轨道 " + itoa(g.TrackCount) + "）")
	}
	for i, tr := range scene.Tracks {
		alias := tr.Alias
		if alias == "" {
			alias = "<无别名>"
		}
		id := tr.Id
		if !tr.IdSet {
			id = "<无类型>"
		}
		off := ""
		if !tr.Enabled {
			off = " · 未启用"
		}
		frames, first, last := timelineTrackSpan(tr)
		fmt.Println("  #" + itoa(i+1) + " " + alias + " · " + id + " · " + tr.Owner +
			" · obj " + itoa(tr.ObjectIndex) + " · 帧 " + itoa(frames) +
			" [" + core.SecondsText(first) + "," + core.SecondsText(last) + "]" + off)
	}
}

// timelineTrackSpan 一条轨道的关键帧帧数与首末时刻。
func timelineTrackSpan(track core.TimelineTrack) (int, float64, float64) {
	first := 0.0
	last := 0.0
	frames := 0
	for _, kf := range track.Keyframes {
		if frames == 0 {
			first = kf.Time
		}
		last = kf.Time
		frames++
	}
	return frames, first, last
}

// cmdThumb 缩略图——截取卡片图片区并缩为 JPEG（尺寸 / 质量参数对齐，编码器不同故不逐字节比对）。
func cmdThumb(args []string) int {
	if len(args) < 2 {
		return requireArgs("thumb <card.png> <out.jpg> [--thumb N] [--quality Q]")
	}
	cardPath, outPath := args[0], args[1]
	thumbWidth, quality := 256, 82
	for i := 2; i+1 < len(args); i++ {
		switch args[i] {
		case "--thumb":
			if v, err := strconv.Atoi(args[i+1]); err == nil {
				thumbWidth = v
			}
			i++
		case "--quality":
			if v, err := strconv.Atoi(args[i+1]); err == nil {
				quality = v
			}
			i++
		}
	}
	c, err := core.ParseCard(cardPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "未找到图片区")
		return 1
	}
	if c.ImageEnd <= 0 {
		fmt.Fprintln(os.Stderr, "未找到图片区")
		return 1
	}
	jpg := core.ThumbnailFromCard(cardPath, c.ImageEnd, thumbWidth, quality)
	if jpg == nil {
		fmt.Fprintln(os.Stderr, "缩略图生成失败: "+core.ThumbnailLastError())
		return 1
	}
	if err := os.WriteFile(outPath, jpg, 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "写出失败: "+err.Error())
		return 1
	}
	fmt.Println("卡类型     : " + orNone(c.CardType))
	fmt.Println("图片区     : " + n0(c.ImageEnd) + " 字节")
	fmt.Println("缩略图     : " + n0(int64(len(jpg))) + " 字节  " + core.DescribeThumbnailSize(jpg))
	fmt.Println("输出       : " + outPath)
	return 0
}

// cmdFileHash 文件内容哈希（MD5，小写十六进制）——重复副本比对的判据面。
func cmdFileHash(path string) int {
	hash, err := core.FileMd5(path)
	if err != nil {
		fmt.Fprintln(os.Stderr, "哈希失败: "+err.Error())
		return 1
	}
	fmt.Println("算法       : " + core.FileHashAlgorithm())
	fmt.Println("文件       : " + path)
	fmt.Println("哈希       : " + hash)
	return 0
}

// ---------- 多库读面（分片库）——契约对齐 Data/StoreHub.cs §AllStores / §QueryModFiles ----------

// openHub 打开多库协调器并读库根配置（已规范化）。
func openHub(dbPath string) (*store.Hub, *core.RootsConfig, error) {
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

// usesCoreDB 该库根的数据是否落主库文件（对齐 C# RootsRules.UsesCoreDb）：
// mod 侧预置条目（锁定主库 / 缓存库槽位）走主库；卡片侧一律自有库文件。
func usesCoreDB(e core.RootEntry, isMods bool) bool {
	if !isMods {
		return false
	}
	return e.Locked || e.Tier == core.TierCache
}

// libOfEntry 取某条库根的库序号（走主库的条目返回 0）。
func libOfEntry(hub *store.Hub, e core.RootEntry, isMods bool) (int, error) {
	if usesCoreDB(e, isMods) {
		return 0, nil
	}
	return hub.LibOf(e.Path)
}

// allStores 全部参与合并的库（主库 + 各库根库文件，去重）——对齐 C# StoreHub.AllStores：
// mod 侧单片（库序号直接成文件名）；卡片侧按分片数展开（每片一个库文件）。
// 缺片按空库跳过并在 stderr 出声（读面不创建文件）。
func allStores(hub *store.Hub, cfg *core.RootsConfig) ([]*store.DB, error) {
	list := []*store.DB{hub.Core()}
	seen := map[int]bool{}
	add := func(lib int) error {
		if lib <= 0 || seen[lib] {
			return nil
		}
		db, err := hub.StoreByLib(lib)
		if err != nil {
			fmt.Fprintln(os.Stderr, "  [库] 跳过缺失库文件："+err.Error())
			return nil
		}
		seen[lib] = true
		list = append(list, db)
		return nil
	}
	for _, e := range cfg.ModRoots {
		lib, err := libOfEntry(hub, e, true)
		if err != nil {
			return nil, err
		}
		if err = add(lib); err != nil {
			return nil, err
		}
	}
	for _, e := range cfg.CardRoots {
		lib, err := libOfEntry(hub, e, false)
		if err != nil {
			return nil, err
		}
		if lib <= 0 {
			continue
		}
		libs, err := hub.LibsOfBase(lib)
		if err != nil {
			return nil, err
		}
		for _, one := range libs {
			if err = add(one); err != nil {
				return nil, err
			}
		}
	}
	return list, nil
}

// queryModFiles 某 guid 的全部文件副本（跨库，按级别升序）——对齐 C# StoreHub.QueryModFiles。
func queryModFiles(hub *store.Hub, cfg *core.RootsConfig, guid string) ([]store.ModFileRow, error) {
	stores, err := allStores(hub, cfg)
	if err != nil {
		return nil, err
	}
	var list []store.ModFileRow
	for _, db := range stores {
		rows, err := db.QueryModFileRows(guid)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", db.Path(), err)
		}
		list = append(list, rows...)
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].Tier < list[j].Tier })
	return list, nil
}

// stampFormat 时间戳格式（对齐 C# Store.StampFormat = "yyyy-MM-ddTHH:mm:ss.fff"，UTC）。
const stampFormat = "2006-01-02T15:04:05.000"

// textEntryMaxBytes 文本类条目入档的字节上限（超过则不入档并出声——不静默丢内容）。
const textEntryMaxBytes = 4 * 1024 * 1024

// takeSwitch 取出一个布尔开关并从 rest 里移除。
func takeSwitch(rest *[]string, name string) bool {
	for i := 0; i < len(*rest); i++ {
		if (*rest)[i] == name {
			*rest = append((*rest)[:i], (*rest)[i+1:]...)
			return true
		}
	}
	return false
}

// stampOf 取文件的修改时间戳文本（UTC）——对齐 C# Store.StampOf。
func stampOf(path string) (string, int64, error) {
	info, err := os.Stat(path)
	if err != nil {
		return "", 0, err
	}
	return info.ModTime().UTC().Format(stampFormat), info.Size(), nil
}

// nowStamp 当前 UTC 时间戳文本（对齐 C# Store.Now）。
func nowStamp() string {
	return time.Now().UTC().Format(stampFormat)
}

// analyzeComposition 读（必要时建立）某 mod 的组成档案——对齐 C# StoreHub.AnalyzeComposition：
// 取该 guid 最优级别且磁盘上真实存在的副本；force=false 时按路径 / 大小 / 修改时间判失效（未变直接复用）；
// force=true 强制重读容器；无副本时具名出声。
func analyzeComposition(hub *store.Hub, cfg *core.RootsConfig, guid string, force bool) (*store.CompositionRow, error) {
	if guid == "" {
		return nil, fmt.Errorf("缺少 guid")
	}
	files, err := queryModFiles(hub, cfg, guid)
	if err != nil {
		return nil, err
	}
	var pick *store.ModFileRow
	for i := range files {
		if _, err = os.Stat(files[i].FilePath); err == nil {
			pick = &files[i]
			break
		}
	}
	if pick == nil {
		if len(files) == 0 {
			return nil, fmt.Errorf("本地没有该 mod 的文件（库内无副本）")
		}
		return nil, fmt.Errorf("登记的副本都不在磁盘上（需重扫）")
	}
	mtime, size, err := stampOf(pick.FilePath)
	if err != nil {
		return nil, err
	}
	// [段1] 非强制 + 已有档案且文件未变 → 直接复用（不重读容器）；档案缺文本内容而容器有文本条目时视为陈旧，重建一次
	cached, err := hub.Core().LoadComposition(guid)
	if err != nil {
		return nil, err
	}
	staleTexts := cached != nil && cached.Texts == "" && core.HasTextEntry(cached.Entries)
	if !force && cached != nil && !staleTexts &&
		strings.EqualFold(cached.FilePath, pick.FilePath) &&
		cached.Size == size && cached.Mtime == mtime {
		cached.Cached = true
		return cached, nil
	}
	// [段2] 读容器中央目录 → 建档落库
	entries, err := core.ListZipEntries(pick.FilePath)
	if err != nil {
		return nil, fmt.Errorf("读容器失败：%s", err.Error())
	}
	var totalSize, totalCompressed int64
	lines := make([]string, 0, len(entries))
	texts := make([]string, 0)
	for _, e := range entries {
		totalSize += e.Size
		totalCompressed += e.Compressed
		lines = append(lines, e.Path+"\t"+strconv.FormatInt(e.Size, 10)+"\t"+strconv.FormatInt(e.Compressed, 10))
	}
	// [段3] 顺带捞文本类条目内容（csv / xml / txt…）——悬停预览用；读不到出声，不静默丢
	for _, e := range entries {
		if !core.IsTextEntry(e.Path) {
			continue
		}
		data, readErr := core.ReadZipEntry(pick.FilePath, e.Path, textEntryMaxBytes)
		if readErr != nil {
			fmt.Println("[组成] 文本条目未入档：" + e.Path + " —— " + readErr.Error())
			continue
		}
		decoded := core.DecodeText(data)
		if decoded.Encoding == "gbk-unavailable" {
			fmt.Println("[组成] GBK 编码不可用——" + e.Path + " 按 UTF-8 宽松解码")
		}
		texts = append(texts, "\u0001"+e.Path+"\t"+core.ClampTextEntry(decoded.Text))
	}
	rec := &store.CompositionRow{
		Guid:            guid,
		FilePath:        pick.FilePath,
		FileName:        pick.FileName,
		Size:            size,
		Mtime:           mtime,
		EntryCount:      int64(len(entries)),
		TotalSize:       totalSize,
		TotalCompressed: totalCompressed,
		Entries:         strings.Join(lines, "\n"),
		Texts:           strings.Join(texts, "\n"),
		AnalyzedAt:      nowStamp(),
	}
	if err = hub.Core().SaveComposition(rec); err != nil {
		return nil, err
	}
	fmt.Println("[组成] 建档：" + guid + " · " + itoa(len(entries)) + " 条目 · 文本 " + itoa(len(texts)) + " 份 · " + pick.FileName)
	return rec, nil
}

// cmdComposition 查看某 mod 的组成（按需建档：容器条目清单 + 目录聚合 + 文本条目内容）。
// 契约对齐 Program.cs §CompositionCommand / Data/StoreHub.cs §AnalyzeComposition。
func cmdComposition(rest []string) int {
	dbPath := dbFlag(&rest)
	force := takeSwitch(&rest, "--force")
	if len(rest) < 1 {
		return requireArgs("composition <guid> [--force] [--text]")
	}
	guid := rest[0]
	showText := takeSwitch(&rest, "--text")
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rec, err := analyzeComposition(hub, cfg, guid, force)
	if err != nil {
		fmt.Fprintln(os.Stderr, "失败: "+err.Error())
		return 3
	}
	fmt.Println("guid       : " + rec.Guid)
	fmt.Println("文件       : " + rec.FilePath)
	fmt.Println("大小       : " + n0(rec.Size) + " 字节")
	fmt.Println("条目数     : " + n0(rec.EntryCount))
	fmt.Println("原始合计   : " + n0(rec.TotalSize) + " 字节")
	fmt.Println("压缩合计   : " + n0(rec.TotalCompressed) + " 字节")
	fmt.Println("文本条目   : " + itoa(core.TextEntryCount(rec.Texts)) + " 份")
	origin := "（本次新建）"
	if rec.Cached {
		origin = "（命中已有档案）"
	}
	fmt.Println("建档于     : " + rec.AnalyzedAt + origin)
	fmt.Println()
	fmt.Println("目录聚合（按条目数降序，前 15）：")
	dirs := core.BuildDirs(rec.Entries)
	for i, d := range dirs {
		if i >= 15 {
			break
		}
		fmt.Println("  " + padLeft(itoa(d.Count), 6) + " 项  " + padLeft(n0(d.Size), 14) + " 字节  " + d.Path)
	}
	if showText {
		fmt.Println()
		fmt.Println("文本条目内容（前 200 字符）：")
		for _, kv := range core.ParseTexts(rec.Texts) {
			body := strings.ReplaceAll(kv.Body, "\r", "")
			body = strings.ReplaceAll(body, "\n", " | ")
			bodyLen := utf16Len(body)
			if bodyLen > 200 {
				body = string([]rune(body)[:utf16SafeCut(body, 200)]) + "…"
			}
			fmt.Println("  ● " + kv.Name + "（" + n0(int64(utf16Len(kv.Body))) + " 字符）")
			fmt.Println("    " + body)
		}
	}
	return 0
}

// utf16Len 字符串的 UTF-16 码元数（对齐 C# string.Length）。
func utf16Len(s string) int {
	n := 0
	for _, r := range s {
		if r > 0xFFFF {
			n += 2
		} else {
			n++
		}
	}
	return n
}

// utf16SafeCut 取 UTF-16 码元长度不超过 limit 时的 rune 个数（C# Substring(0,200) 可能切在代理对中间，
// 这里按整 rune 截断——输出差异仅在样本含 emoji 且恰好切到时出现）。
func utf16SafeCut(s string, limit int) int {
	n := 0
	used := 0
	for _, r := range s {
		step := 1
		if r > 0xFFFF {
			step = 2
		}
		if used+step > limit {
			break
		}
		used += step
		n++
	}
	return n
}

// cmdModFiles 某 guid 的跨库副本清单（分片库读面调试命令——C# 无同名命令，对拍由 compare.py libmap 直读库作真相源）。
func cmdModFiles(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 1 {
		return requireArgs("modfiles <guid> [--db <db>]")
	}
	guid := rest[0]
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rows, err := queryModFiles(hub, cfg, guid)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("guid       : " + guid)
	fmt.Println("副本       : " + itoa(len(rows)) + " 份")
	for _, r := range rows {
		fmt.Println(itoa(r.Tier) + "\t" + r.RootPath + "\t" + r.FilePath + "\t" + r.FileName)
	}
	return 0
}

// cmdPluginInfo 解析一个插件 dll（PE / .NET 元数据只读通道）——对齐 C# Program.cs §PluginInfoCommand。
func cmdPluginInfo(path string) int {
	list, meta, errText := core.ReadPluginDll(path)
	fmt.Println("文件: " + path)
	if meta.Title != "" {
		line := "程序集: " + meta.Title
		if meta.FileVersion != "" {
			line += " " + meta.FileVersion
		}
		fmt.Println(line)
	}
	if meta.Description != "" {
		fmt.Println("说明: " + meta.Description)
	}
	if meta.Company != "" {
		fmt.Println("公司: " + meta.Company)
	}
	if meta.Copyright != "" {
		fmt.Println("版权: " + meta.Copyright)
	}
	if meta.TargetFramework != "" {
		fmt.Println("框架: " + meta.TargetFramework)
	}
	if len(list) == 0 {
		fmt.Fprintln(os.Stderr, "解析失败: "+errText)
		return 1
	}
	fmt.Println("插件 " + itoa(len(list)) + " 项：")
	for _, info := range list {
		kind := "BepInEx"
		if info.IsIpa {
			kind = "IPA"
		}
		fmt.Println("  [" + kind + "] " + info.Name + " " + info.Version)
		fmt.Println("      GUID: " + info.Guid)
		if len(info.Processes) > 0 {
			fmt.Println("      进程过滤: " + strings.Join(info.Processes, ", "))
		}
		if len(info.Dependencies) > 0 {
			fmt.Println("      依赖: " + strings.Join(info.Dependencies, ", "))
		}
		note := core.LookupPluginNote(info.Guid, info.Name)
		if note == "" {
			note = "未收录（可在 plugin-notes.txt 补充）"
		}
		fmt.Println("      说明: " + note)
	}
	return 0
}

// cmdPlugins 列出插件库里的插件（与面板「插件清单」同一数据源；--unnoted 只列未收录说明的项）。
// 契约对齐 Program.cs §PluginsCommand。
func cmdPlugins(rest []string) int {
	dbPath := dbFlag(&rest)
	onlyUnnoted := takeSwitch(&rest, "--unnoted")
	db, err := store.Open(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	rows, err := db.LoadPlugins()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	cfgs, err := db.LoadPluginConfigs()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	cfgByGuid := map[string]*store.PluginConfigRow{}
	cfgByName := map[string]*store.PluginConfigRow{}
	for i := range cfgs {
		cfg := &cfgs[i]
		if cfg.Guid != "" {
			if _, ok := cfgByGuid[strings.ToLower(cfg.Guid)]; !ok {
				cfgByGuid[strings.ToLower(cfg.Guid)] = cfg
			}
		}
		if cfg.PluginName != "" {
			if _, ok := cfgByName[strings.ToLower(cfg.PluginName)]; !ok {
				cfgByName[strings.ToLower(cfg.PluginName)] = cfg
			}
		}
	}
	withGuid := 0
	noted := 0
	unnoted := make([]string, 0)
	for _, row := range rows {
		if row.Guid != "" {
			withGuid++
		}
		if core.LookupPluginNote(row.Guid, row.Name) != "" || core.LookupPluginNoteFile(row.FileName) != "" {
			noted++
		} else {
			line := row.Guid + "  |  " + row.Name
			if row.Guid == "" {
				line += "  |  " + row.FileName + "（非插件 dll）"
			}
			unnoted = append(unnoted, line)
		}
	}
	fmt.Println("—— 插件 " + itoa(withGuid) + " 项 · 非插件 dll " + itoa(len(rows)-withGuid) +
		" 个 · 说明收录 " + itoa(noted) + " / " + itoa(len(rows)) + " 项（表内 " + itoa(core.PluginNotesCount()) +
		" 条）· 配置文件 " + itoa(len(cfgs)) + " 个")
	if !onlyUnnoted {
		for _, row := range rows {
			if row.Guid == "" {
				continue
			}
			var cfg *store.PluginConfigRow
			if hit, ok := cfgByGuid[strings.ToLower(row.Guid)]; ok {
				cfg = hit
			}
			if cfg == nil && row.Name != "" {
				if hit, ok := cfgByName[strings.ToLower(row.Name)]; ok {
					cfg = hit
				}
			}
			note := core.LookupPluginNote(row.Guid, row.Name)
			line := row.Guid + "  |  " + row.Name + " " + row.Version + "  |  " + row.FileName
			if row.Processes != "" {
				line += "  |  进程 " + row.Processes
			}
			if row.IsIpa {
				line += "  |  [IPA]"
			}
			if cfg != nil {
				line += "  |  配置 " + itoa(int(cfg.SectionCount)) + " 节 " + itoa(int(cfg.OptionCount)) + " 项"
			}
			if note == "" {
				note = "未收录"
			}
			line += "  |  " + note
			fmt.Println(line)
		}
	}
	fmt.Println()
	fmt.Println("## 未收录说明（" + itoa(len(unnoted)) + "）——可在 Web\\wwwroot\\plugin-notes.txt 补一行（键 = GUID 或插件名）")
	for _, u := range unnoted {
		fmt.Println("  " + u)
	}
	return 0
}

// cmdPluginConfig 列插件配置文件——无参数列全部 cfg；给 GUID 或插件名则列出该插件的分节与配置项。
// 契约对齐 Program.cs §PluginConfigCommand。
func cmdPluginConfig(rest []string) int {
	dbPath := dbFlag(&rest)
	db, err := store.Open(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	if len(rest) == 0 {
		rows, err := db.LoadPluginConfigs()
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("—— 配置文件 " + itoa(len(rows)) + " 个")
		for _, row := range rows {
			guid := row.Guid
			if guid == "" {
				guid = "(无)"
			}
			line := row.FileName + "  |  " + row.PluginName + " " + row.PluginVersion +
				"  |  GUID " + guid + "  |  " + itoa(int(row.SectionCount)) + " 节 " + itoa(int(row.OptionCount)) + " 项"
			if row.Error != "" {
				line += "  |  读取失败：" + row.Error
			}
			fmt.Println(line)
		}
		return 0
	}
	key := rest[0]
	cfg, err := db.LoadPluginConfigByGuid(key)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if cfg.FilePath == "" {
		rows, err := db.LoadPluginConfigs()
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		for _, row := range rows {
			if strings.EqualFold(row.PluginName, key) {
				cfg, err = db.LoadPluginConfigByGuid(row.Guid)
				if err != nil {
					fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
					return 1
				}
				if cfg.FilePath == "" {
					cfg = &row
				}
				break
			}
		}
	}
	if cfg.FilePath == "" {
		fmt.Fprintln(os.Stderr, "未找到该插件的配置文件："+key)
		return 1
	}
	parsed := core.ParsePluginConfigJSON(cfg.Sections)
	guid := cfg.Guid
	if guid == "" {
		guid = "(无)"
	}
	fmt.Println("配置: " + cfg.FileName + "  |  " + cfg.PluginName + " " + cfg.PluginVersion +
		"  |  GUID " + guid + "  |  " + itoa(len(parsed.Sections)) + " 节")
	for _, section := range parsed.Sections {
		fmt.Println()
		fmt.Println("[" + section.Name + "]")
		for _, option := range section.Options {
			line := "  " + option.Key + " = " + option.Value
			if option.Type != "" {
				line += "   (" + option.Type
				if option.Def != "" {
					line += " · 默认 " + option.Def
				}
				line += ")"
			}
			fmt.Println(line)
			if option.Comment != "" {
				fmt.Println("      " + option.Comment)
			}
		}
	}
	return 0
}

// ---------- 库根（roots）命令——契约对齐 Program.cs §RootsList / RootsAdd / RootsClear / SetGameRoot ----------

// dbFlag 取出 --db（缺省 = exe 同级 data/kkmanager.db，对齐 C# 默认值）。
func dbFlag(rest *[]string) string {
	if v := takeFlag(rest, "--db"); v != "" {
		return v
	}
	exe, err := os.Executable()
	if err != nil {
		return filepath.Join("data", "kkmanager.db")
	}
	return filepath.Join(filepath.Dir(exe), "data", "kkmanager.db")
}

// takeFlag 取出「<name> <值>」并从 rest 里移除；没有返回空串。
func takeFlag(rest *[]string, name string) string {
	for i := 0; i < len(*rest); i++ {
		if (*rest)[i] == name && i+1 < len(*rest) {
			value := (*rest)[i+1]
			*rest = append((*rest)[:i], (*rest)[i+2:]...)
			return value
		}
	}
	return ""
}

// openRoots 打开主库并读库根配置（已规范化）。
func openRoots(dbPath string) (*store.DB, *core.RootsConfig, error) {
	db, err := store.Open(dbPath)
	if err != nil {
		return nil, nil, err
	}
	text, _, err := db.GetSetting("roots")
	if err != nil {
		db.Close()
		return nil, nil, err
	}
	cfg := core.ParseRootsConfig(text)
	core.Normalize(cfg)
	return db, cfg, nil
}

// rootTags 库根行的标记串（顺序对齐 C#：含子目录 / 只读 / 预置锁定 / 缓存库 / 离线）。
func rootTags(e core.RootEntry, withTierName, withCache, withOffline bool) string {
	tags := ""
	if withTierName {
		tags = "  级别 " + itoa(e.Tier) + "（" + core.TierName(e.Tier) + "）  "
	} else {
		tags = "  级别 " + itoa(e.Tier) + "  "
	}
	tags += e.Path
	if e.Recurse {
		tags += "  [含子目录]"
	} else {
		tags += "  [仅本目录]"
	}
	if e.ReadOnly {
		tags += "  [只读]"
	}
	if e.Locked {
		tags += "  [预置·锁定]"
	}
	if withCache && e.Tier == core.TierCache {
		tags += "  [缓存库·不可删]"
	}
	if withOffline && e.Offline {
		if strings.TrimSpace(e.Note) == "" {
			tags += "  [已离线]"
		} else {
			tags += "  [已离线]（备注：" + e.Note + "）"
		}
	}
	return tags
}

// cmdRootsList 列库根——插件库根 / mod 库根 / 卡片库根三段（对齐 C# RootsListCommand）。
func cmdRootsList(rest []string) int {
	db, cfg, err := openRoots(dbFlag(&rest))
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	fmt.Println("库: " + db.Path())
	fmt.Println("游戏根: " + cfg.GameRoot)
	fmt.Println()
	fmt.Println("## 插件库根")
	for _, e := range cfg.PluginRoots {
		fmt.Println(rootTags(e, true, false, false))
	}
	fmt.Println("## mod 库根")
	for _, e := range cfg.ModRootsOrdered() {
		fmt.Println(rootTags(e, true, true, true))
	}
	fmt.Println("## 卡片库根")
	for _, e := range cfg.CardRootsOrdered() {
		fmt.Println(rootTags(e, false, false, true))
	}
	return 0
}

// cmdRootsAdd 登记一条库根（同路径先删后加，再整体规范化）。
func cmdRootsAdd(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 3 {
		fmt.Fprintln(os.Stderr, "用法: roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]")
		return 2
	}
	kind := rest[0]
	tier, err := strconv.Atoi(rest[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, "用法: roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]")
		return 2
	}
	entry := core.RootEntry{Tier: tier, Path: rest[2], Recurse: true}
	for _, arg := range rest[3:] {
		switch arg {
		case "含子目录", "recurse":
			entry.Recurse = true
		case "仅本目录", "norecurse":
			entry.Recurse = false
		case "只读", "readonly":
			entry.ReadOnly = true
		}
	}
	db, cfg, err := openRoots(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	list := append([]core.RootEntry{}, cfg.CardRoots...)
	if kind == "mods" {
		list = append([]core.RootEntry{}, cfg.ModRoots...)
	}
	kept := make([]core.RootEntry, 0, len(list)+1)
	for _, e := range list {
		if !strings.EqualFold(e.Path, entry.Path) {
			kept = append(kept, e)
		}
	}
	kept = append(kept, entry)
	if kind == "mods" {
		cfg.ModRoots = kept
	} else {
		cfg.CardRoots = kept
	}
	core.Normalize(cfg)
	if err = saveRoots(db, cfg); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	label := "卡片"
	if kind == "mods" {
		label = "mod"
	}
	fmt.Println("已登记 " + label + " 库根：级别 " + itoa(tier) + " · " + entry.Path)
	fmt.Println("（库根由你添加——扫描只覆盖你添加的文件夹，不做全库盲扫）")
	fmt.Println("当前 mod 库根 " + itoa(len(cfg.ModRoots)) + " 个 / 卡片库根 " + itoa(len(cfg.CardRoots)) + " 个")
	return 0
}

// cmdRootsClear 清空使用者条目（保留预置主库 / 缓存库槽位）。
func cmdRootsClear(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 1 {
		return requireArgs("roots-clear <mods|cards|all>")
	}
	db, cfg, err := openRoots(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	kind := rest[0]
	kept := 0
	if kind == "mods" || kind == "all" {
		cfg.ModRoots, kept = keepPresets(cfg.ModRoots, true, kept)
	}
	if kind == "cards" || kind == "all" {
		cfg.CardRoots, kept = keepPresets(cfg.CardRoots, false, kept)
	}
	if err = saveRoots(db, cfg); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("已清空：" + kind + "（保留预置条目 " + itoa(kept) + " 条——预置主库 / 缓存库槽位不可删除）")
	return 0
}

// keepPresets 保留锁定条目（keepCacheSlot 时连缓存库槽位一并保留）；返回保留计数。
func keepPresets(list []core.RootEntry, keepCacheSlot bool, kept int) ([]core.RootEntry, int) {
	out := make([]core.RootEntry, 0, len(list))
	for _, e := range list {
		keep := e.Locked || (keepCacheSlot && e.Tier == core.TierCache)
		if keep {
			out = append(out, e)
			kept++
		}
	}
	return out, kept
}

// cmdSetGameRoot 设置游戏根——锁定条目重派生、缓存库槽位未被改过则跟随。
func cmdSetGameRoot(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 1 {
		return requireArgs("set-game-root <路径>")
	}
	db, cfg, err := openRoots(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	previousRoot := cfg.GameRoot
	cfg.GameRoot = core.NormalizeGameRoot(rest[0])
	warnings, _ := core.ApplyGameRootChange(cfg, previousRoot)
	core.Normalize(cfg)
	if err = saveRoots(db, cfg); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("游戏根: " + cfg.GameRoot)
	fmt.Println("插件库根 " + itoa(len(cfg.PluginRoots)) + " 个 / mod 库根 " + itoa(len(cfg.ModRoots)) +
		" 个 / 卡片库根 " + itoa(len(cfg.CardRoots)) + " 个")
	for _, w := range warnings {
		fmt.Println("  ! " + w)
	}
	for _, e := range cfg.ModRootsOrdered() {
		fmt.Println("  级别 " + itoa(e.Tier) + "（" + core.TierName(e.Tier) + "）  " + e.Path)
	}
	return 0
}

// cmdScanPlan 读 / 写扫描计划（对齐 C# ScanPlanCommand；键 scan_plan_order / scan_plan_on）。
func cmdScanPlan(rest []string) int {
	dbPath := dbFlag(&rest)
	db, err := store.Open(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	fmt.Println("库: " + db.Path())
	if len(rest) > 0 && rest[0] == "set" {
		order := ""
		if len(rest) > 1 {
			order = rest[1]
		}
		on := order
		if len(rest) > 2 {
			on = rest[2]
		}
		given := core.LoadScanPlan(order, on)
		if err = db.SetSetting(core.ScanPlanOrderKey, core.ScanPlanOrderText(given)); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if err = db.SetSetting(core.ScanPlanOnKey, core.ScanPlanOnText(given)); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("扫描计划已保存：")
		printScanPlan(given)
		return 0
	}
	orderText, _, err := db.GetSetting(core.ScanPlanOrderKey)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	onText, _, err := db.GetSetting(core.ScanPlanOnKey)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	printScanPlan(core.LoadScanPlan(orderText, onText))
	return 0
}

// printScanPlan 打印扫描计划——步骤（勾选 / 必选 / 灰置）+ 切出的段（对齐 C# PrintPlan）。
func printScanPlan(p *core.ScanPlan) {
	fmt.Println("步骤（顺序即执行顺序）：")
	for _, id := range p.Order {
		d := core.FindScanStep(id)
		if d == nil {
			continue
		}
		mark := "[ ]"
		if d.Grayed {
			mark = "（灰置）"
		} else if p.IsOn(id) {
			mark = "[x]"
		}
		required := ""
		if d.Required {
			required = "（必选）"
		}
		group := ""
		if d.Group != "" {
			group = "（组 " + d.Group + "）"
		}
		fmt.Println("  " + mark + " " + id + " —— " + d.Name + required + group + " · " + d.Note)
	}
	fmt.Println("段（同段共用一次文件打开）：")
	for _, seg := range p.Segments() {
		side := "卡片"
		if seg.Mods {
			side = "mod"
		}
		fmt.Println("  " + side + " / 档 " + itoa(seg.Tier) + "：" + seg.Label())
	}
}

// saveRoots 写回库根配置（键 roots）。
func saveRoots(db *store.DB, cfg *core.RootsConfig) error {
	text, err := core.RootsConfigJSON(cfg)
	if err != nil {
		return err
	}
	return db.SetSetting("roots", text)
}

func requireArgs(usage string) int {
	fmt.Fprintln(os.Stderr, "用法: "+usage)
	return 2
}

func usage() {
	fmt.Println("kkgo —— KK Manager 的 Go 重写（对照 C# 0.21.9）")
	fmt.Println()
	fmt.Println("  version                               显示版本")
	fmt.Println("  modinfo   <zipmod>                    解析单个 zipmod 的 manifest")
	fmt.Println("  cardinfo  <card.png>                  解析单张卡片的声明区")
	fmt.Println("  card-structure <card.png>             卡片文件结构（PNG 块表 + 图片区 / 数据区划分）")
	fmt.Println("  card-detail <card.png>                卡片数据区内容（部件 / 声明区 / 数据块目录 / 内嵌贴图 / 插件块）")
	fmt.Println("  card-coord <card.png> [--all]         人物卡服装 / 饰品（Coordinate 七套槽位）")
	fmt.Println("  card-params <card.png|dir> [--recurse] 可编辑字段（姓 / 名 / 爱称 / 性格 / 敏感带 / 五项是否接受）")
	fmt.Println("  card-scene <card.png>                 场景卡（sd）插件条目 / 内嵌角色 / timeline 摘要")
	fmt.Println("  card-analysis <card.png> [分项...]    分项分析序列化（coord / detail / scene——与 C# 落库 JSON 同形状）")
	fmt.Println("  card-timeline <card.png> [tracks]     场景卡 timeline（长度 / timeScale / 关键帧 / 轨道树）")
	fmt.Println("  thumb <card.png> <out.jpg>            缩略图（JPEG；--thumb N / --quality Q）")
	fmt.Println("  filehash <path>                       文件 MD5（重复副本比对判据）")
	fmt.Println("  modfiles <guid>                       某 mod 的跨库副本清单（分片库读面）")
	fmt.Println("  composition <guid> [--force] [--text] 某 mod 的组成（按需建档：条目清单 + 目录聚合 + 文本条目）")
	fmt.Println("  plugin-info <dll>                     插件 dll 元数据（BepInPlugin / IPA / 程序集信息）")
	fmt.Println("  plugin-config [GUID|插件名]           插件配置文件（无参 = 全表；给键 = 分节与配置项）")
	fmt.Println("  plugins [--unnoted]                   插件清单（与面板同一数据源；--unnoted 只列未收录说明）")
	fmt.Println("  roots-list                            库根配置（插件 / mod / 卡片三段）")
	fmt.Println("  roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]  登记一条库根")
	fmt.Println("  roots-clear <mods|cards|all>          清空使用者库根（预置条目保留）")
	fmt.Println("  set-game-root <路径>                  设游戏根（锁定条目重派生 / 缓存库槽位跟随）")
	fmt.Println("  scan-plan [set <顺序> [启用]]         扫描计划（读 / 写；两键用于扫描链）")
}

// orNone 空值显示 <无>（对齐 C# ?? "<无>"）。
func orNone(s string) string {
	if s == "" {
		return "<无>"
	}
	return s
}

// fieldOrNone 元素缺失显示 <无>、元素存在但为空则原样输出（对齐 C#：null → <无>，空串 → 空）。
func fieldOrNone(s string, found bool) string {
	if !found {
		return "<无>"
	}
	return s
}

// n0 千分位整数（对齐 .NET ToString("N0")，InvariantGlobalization）。
func n0(v int64) string {
	s := strconv.FormatInt(v, 10)
	neg := strings.HasPrefix(s, "-")
	if neg {
		s = s[1:]
	}
	var parts []string
	for len(s) > 3 {
		parts = append([]string{s[len(s)-3:]}, parts...)
		s = s[:len(s)-3]
	}
	parts = append([]string{s}, parts...)
	out := strings.Join(parts, ",")
	if neg {
		out = "-" + out
	}
	return out
}

func padLeft(s string, width int) string {
	if len(s) >= width {
		return s
	}
	return strings.Repeat(" ", width-len(s)) + s
}

func padRight(s string, width int) string {
	if len(s) >= width {
		return s
	}
	return s + strings.Repeat(" ", width-len(s))
}

// itoa 十进制整数（无千分位）。
func itoa(v int) string { return strconv.Itoa(v) }
