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
	"kkgo/internal/web"
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
	case "card-edit":
		return cmdCardEdit(rest)
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
	case "scan-mods":
		return cmdScanMods(rest)
	case "scan-cards":
		return cmdScanCards(rest)
	case "scan-main":
		return cmdScanScope(rest, core.ScanScopePreset)
	case "scan-extra":
		return cmdScanScope(rest, core.ScanScopeExtra)
	case "scan-plugins":
		return cmdScanPlugins(rest)
	case "keep-version":
		return cmdOldOp(rest, "keep-version")
	case "mark-old":
		return cmdOldOp(rest, "mark-old")
	case "swap":
		return cmdOldOp(rest, "swap")
	case "promote":
		return cmdOldOp(rest, "promote")
	case "sort":
		return cmdSort(rest)
	case "card-sort":
		return cmdCardSort(dbFlag(&rest), rest)
	case "dup":
		return cmdDup(rest)
	case "refs-read":
		return cmdRefsRead(rest)
	case "index-import":
		return cmdIndexImport(rest)
	case "idx":
		return cmdIdx(rest)
	case "stats":
		return cmdStats(rest)
	case "authors":
		return cmdAuthors(rest)
	case "cards":
		return cmdCards(rest)
	case "todos":
		return cmdTodos(rest)
	case "noncards":
		return cmdNonCards(rest)
	case "noncard-move":
		return cmdNonCardMove(rest)
	case "todo-add":
		return cmdTodoAdd(rest)
	case "todo-close":
		return cmdTodoClose(rest)
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
	case "plugin-log":
		return cmdPluginLog(rest)
	case "plugin-match":
		return cmdPluginMatch(rest)
	case "plugins":
		return cmdPlugins(rest)
	case "u3d":
		return cmdU3d(rest)
	case "u3d-db":
		return cmdU3dDb(dbFlag(&rest), rest)
	case "serve":
		return cmdServe(rest)
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

// cmdStats 库统计——卡片 / mod / 副本 / 四色 / 未被引用 / 缺失排行（跨库合并）。
// 契约对齐 Program.cs §StatsCommand + Data/StoreHub.cs §Snapshot / §DupCounts / §MissingRanking。
// 四色聚合走逐库 SQL：每库用与 C# Store.ColorCte 同构的 CTE（库文件 ATTACH 主库取 mod 表），计数最后相加。
func cmdStats(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	fmt.Println("库: " + hub.Core().Path())
	coreSnap, err := hub.SnapshotOf(hub.Core())
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	merged := &store.Snapshot{
		Mods:      coreSnap.Mods,
		ModsTier1: coreSnap.ModsTier1,
		ModsTier2: coreSnap.ModsTier2,
		ModsTier3: coreSnap.ModsTier3,
		DupMods:   coreSnap.DupMods,
	}
	stores, err := allStores(hub, cfg)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	var modFiles int64
	refSet := map[string]bool{}
	dupAgg := map[string]*[2]int64{}
	for _, one := range stores {
		snap, snapErr := hub.SnapshotOf(one)
		if snapErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+snapErr.Error())
			return 1
		}
		merged.Cards += snap.Cards
		merged.CardsReady += snap.CardsReady
		merged.CardsWithRefs += snap.CardsWithRefs
		merged.Refs += snap.Refs
		merged.Green += snap.Green
		merged.Yellow += snap.Yellow
		merged.Red += snap.Red
		merged.Black += snap.Black
		count, countErr := one.CountModFiles()
		if countErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+countErr.Error())
			return 1
		}
		modFiles += count
		guids, guidErr := one.DistinctRefGuids()
		if guidErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+guidErr.Error())
			return 1
		}
		for _, g := range guids {
			refSet[g] = true
		}
		counts, listErr := one.ListModFileCounts()
		if listErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+listErr.Error())
			return 1
		}
		for _, c := range counts {
			v, ok := dupAgg[c.Guid]
			if !ok {
				v = &[2]int64{}
				dupAgg[c.Guid] = v
			}
			v[0] += c.Total
			v[1] += c.Live
		}
	}
	merged.ModFiles = modFiles
	// 未被引用的 mod：主库级（tier=1）里 guid 不在全库引用集合的
	sizes, err := hub.Core().MainModSizes()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	unused := int64(0)
	for _, m := range sizes {
		if !refSet[m.Guid] {
			unused++
		}
	}
	merged.UnusedMods = unused
	// 重复副本组数 / 待确认组数（跨库合并：total > 1 成组，live ≥ 2 待确认）
	var dupGroups, dupPending int64
	for _, v := range dupAgg {
		if v[0] <= 1 {
			continue
		}
		dupGroups++
		if v[1] >= 2 {
			dupPending++
		}
	}
	// 缺失排行（跨库合并：每库本地 mod 表里没有的 guid，被引用卡片数累加）
	missCounts := map[string]int64{}
	for _, one := range stores {
		list, missErr := hub.MissingRankingOf(one, 0)
		if missErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+missErr.Error())
			return 1
		}
		for _, kv := range list {
			missCounts[kv.Guid] += kv.Count
		}
	}
	ranking := make([]store.MissingRow, 0, len(missCounts))
	for guid, count := range missCounts {
		ranking = append(ranking, store.MissingRow{Guid: guid, Count: count})
	}
	sort.Slice(ranking, func(i, j int) bool {
		if ranking[i].Count != ranking[j].Count {
			return ranking[i].Count > ranking[j].Count
		}
		return ranking[i].Guid < ranking[j].Guid
	})
	if len(ranking) > 30 {
		ranking = ranking[:30]
	}
	fmt.Println()
	fmt.Println("卡片总数     : " + n0(merged.Cards) + "（有引用 " + n0(merged.CardsWithRefs) + "，主库就绪 " + n0(merged.CardsReady) + "）")
	fmt.Println("mod 总数     : " + n0(merged.Mods) + "（主库 " + strconv.FormatInt(merged.ModsTier1, 10) + " / 缓存 " + strconv.FormatInt(merged.ModsTier2, 10) + " / 冷冻 " + strconv.FormatInt(merged.ModsTier3, 10) + "）")
	fmt.Println("mod 文件副本 : " + n0(merged.ModFiles) + "（重复副本 " + n0(dupGroups) + " 组 · " + n0(dupPending) + " 待确认）")
	fmt.Println("引用（去重） : " + n0(merged.Refs))
	fmt.Println("  绿（主库） : " + n0(merged.Green))
	fmt.Println("  黄（缓存） : " + n0(merged.Yellow))
	fmt.Println("  红（冷冻） : " + n0(merged.Red))
	fmt.Println("  黑（全无） : " + n0(merged.Black))
	fmt.Println("未被引用的 mod: " + n0(merged.UnusedMods))
	fmt.Println()
	fmt.Println("缺失排行（全库皆无，按被引用卡片数）：")
	for _, kv := range ranking {
		fmt.Println("  " + padLeft(strconv.FormatInt(kv.Count, 10), 5) + " 张  " + kv.Guid)
	}
	return 0
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

// cmdPluginLog 解析 BepInEx 日志（LogOutput.log）——插件加载 / 进程过滤跳过 / 错误三类实况。
// 契约对齐 Program.cs §PluginLogCommand（含「装了但本次日志没提」的插件清单）。
func cmdPluginLog(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	path := core.LogPathOf(cfg)
	s := core.ReadPluginLog(path)
	fmt.Println("日志: " + s.FilePath)
	if s.Error != "" {
		fmt.Fprintln(os.Stderr, "解析失败: "+s.Error)
		return 1
	}
	fmt.Println("时刻: " + s.FileTime + " · " + strconv.FormatInt(s.FileSize/1024/1024, 10) + " MB")
	fmt.Println("待加载 " + itoa(s.ToLoad) + " · 已加载 " + itoa(len(s.Loaded)) +
		" · 进程过滤跳过 " + itoa(len(s.Skipped)) + " · 错误行 " + itoa(len(s.Errors)))
	if len(s.Errors) > 0 {
		fmt.Println()
		fmt.Println("## 错误（" + itoa(len(s.Errors)) + "）")
		for _, e := range s.Errors {
			fmt.Println("  " + e)
		}
	}
	if len(s.Skipped) > 0 {
		fmt.Println()
		fmt.Println("## 进程过滤跳过（" + itoa(len(s.Skipped)) + "）")
		for _, k := range s.Skipped {
			fmt.Println("  " + k)
		}
	}
	// 装了但日志里完全没提（仅 plugins 目录下的插件——core / patchers 不在插件加载面）
	rows, err := hub.Core().LoadPlugins()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	unmentioned := make([]string, 0)
	for _, row := range rows {
		if row.Guid == "" || !strings.Contains(strings.ToLower(row.FilePath), `\plugins\`) {
			continue
		}
		if core.PluginLogStateOf(s, row.Name) != "none" {
			continue
		}
		unmentioned = append(unmentioned, row.Guid+"  |  "+row.Name+" "+row.Version+"  |  "+row.FileName)
	}
	fmt.Println()
	fmt.Println("## 装了但本次日志没提（" + itoa(len(unmentioned)) + "）——可疑：可能没被 BepInEx 扫到")
	for _, u := range unmentioned {
		fmt.Println("  " + u)
	}
	return 0
}

// cmdPluginMatch 场景卡插件键 ↔ 已装插件对照（键是卡片数据区里的简名——vnge_sss / kkpe / timeline）。
// 契约对齐 Program.cs §PluginMatchCommand / Core/PluginMatcher.cs。
func cmdPluginMatch(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 1 {
		return requireArgs("plugin-match <键...> [--db <db>]")
	}
	db, err := store.Open(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer db.Close()
	plugins, err := db.LoadPlugins()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	for _, key := range rest {
		var hits []store.PluginRow
		for _, p := range plugins {
			if core.PluginKeyMatches(p.Guid, p.Name, key) {
				hits = append(hits, p)
			}
		}
		if len(hits) == 0 {
			fmt.Println(key + "  →  未装（插件库里没有匹配的插件）")
			continue
		}
		fmt.Println(key + "  →  " + itoa(len(hits)) + " 项")
		for _, p := range hits {
			fmt.Println("      " + p.Guid + "  |  " + p.Name + " " + p.Version + "  |  " + p.FileName)
		}
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
	fmt.Println("  plugin-log                            解析 BepInEx 日志（加载 / 进程过滤跳过 / 错误 / 日志未提）")
	fmt.Println("  plugin-match <键...>                  场景卡插件键 ↔ 已装插件对照（vnge_sss / kkpe / timeline）")
	fmt.Println("  plugins [--unnoted]                   插件清单（与面板同一数据源；--unnoted 只列未收录说明）")
	fmt.Println("  roots-list                            库根配置（插件 / mod / 卡片三段）")
	fmt.Println("  roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]  登记一条库根")
	fmt.Println("  roots-clear <mods|cards|all>          清空使用者库根（预置条目保留）")
	fmt.Println("  set-game-root <路径>                  设游戏根（锁定条目重派生 / 缓存库槽位跟随）")
	fmt.Println("  scan-plan [set <顺序> [启用]]         扫描计划（读 / 写；两键用于扫描链）")
	fmt.Println("  stats                                 库统计（卡片 / mod / 四色 / 未被引用 / 缺失排行）")
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

// ---------- 扫描链（G4）——mod 侧 ----------

// loadPlan 读已保存的扫描计划；steps 非空时以它为顺序与勾选（并落盘）——对齐 C# Program.cs §BuildPlan。
func loadPlan(hub *store.Hub, steps string) (*core.ScanPlan, error) {
	if steps != "" {
		plan := core.LoadScanPlan(steps, steps)
		if err := hub.Core().SetSetting(core.ScanPlanOrderKey, core.ScanPlanOrderText(plan)); err != nil {
			return nil, err
		}
		if err := hub.Core().SetSetting(core.ScanPlanOnKey, core.ScanPlanOnText(plan)); err != nil {
			return nil, err
		}
		return plan, nil
	}
	orderText, _, err := hub.Core().GetSetting(core.ScanPlanOrderKey)
	if err != nil {
		return nil, err
	}
	onText, _, err := hub.Core().GetSetting(core.ScanPlanOnKey)
	if err != nil {
		return nil, err
	}
	return core.LoadScanPlan(orderText, onText), nil
}

// cmdScanMods 扫描 mod 库——契约对齐 Program.cs §ScanModsCommand（输出行与计数口径逐字对齐）。
func cmdScanMods(rest []string) int {
	dbPath := dbFlag(&rest)
	force := takeSwitch(&rest, "--force")
	steps := takeFlag(&rest, "--steps")
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	if err = hub.EnsureMigrated(core.ModLibRoots(cfg), core.CardLibRoots(cfg)); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	// 命令行给了目录则临时按级别 1 构造配置（对齐 C# BuildConfig）
	if len(rest) > 0 {
		list := make([]core.RootEntry, 0, len(rest))
		for _, dir := range rest {
			list = append(list, core.RootEntry{Tier: core.TierMain, Path: dir, Recurse: true})
		}
		cfg.ModRoots = list
	}
	plan, err := loadPlan(hub, steps)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("库: " + hub.Core().Path())
	result, err := core.ScanMods(hub, cfg, nil, core.ScanScopeAll, force, plan, func(msg string) {
		fmt.Println(msg)
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println()
	fmt.Printf("mod 扫描完成：%.1f 秒\n", result.Elapsed.Seconds())
	fmt.Printf("  枚举 %d  新增/更新 %d  跳过 %d  非 mod %d  失败 %d  清理 %d\n",
		result.Seen, result.Added, result.Skipped, result.NonMod, result.Failed, result.Removed)
	for _, e := range result.Errors {
		fmt.Println("  ! " + e)
	}
	return 0
}

// cmdScanCards 扫描卡片库——契约对齐 Program.cs §ScanCardsCommand（输出行与计数口径逐字对齐）。
func cmdScanCards(rest []string) int {
	dbPath := dbFlag(&rest)
	force := takeSwitch(&rest, "--force")
	steps := takeFlag(&rest, "--steps")
	thumb := 256
	if v := takeFlag(&rest, "--thumb"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			thumb = n
		}
	}
	quality := 82
	if v := takeFlag(&rest, "--quality"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			quality = n
		}
	}
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	if err = hub.EnsureMigrated(core.ModLibRoots(cfg), core.CardLibRoots(cfg)); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if len(rest) > 0 {
		list := make([]core.RootEntry, 0, len(rest))
		for _, dir := range rest {
			list = append(list, core.RootEntry{Tier: core.TierMain, Path: dir, Recurse: true})
		}
		cfg.CardRoots = list
	}
	plan, err := loadPlan(hub, steps)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("库: " + hub.Core().Path())
	result, err := core.ScanCards(hub, cfg, nil, core.ScanScopeAll, force, thumb, quality, plan, func(msg string) {
		fmt.Println(msg)
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println()
	fmt.Printf("卡片扫描完成：%.1f 秒\n", result.Elapsed.Seconds())
	fmt.Printf("  枚举 %d  新增/更新 %d  跳过 %d  非卡 %d  非 mod %d  失败 %d  清理 %d\n",
		result.Seen, result.Added, result.Skipped, result.NonCard, result.NonMod, result.Failed, result.Removed)
	fmt.Printf("  引用条目 %d  缩略图 %.1f MB\n", result.RefEntries, float64(result.ThumbBytes)/1024.0/1024.0)
	fmt.Printf("  角色名：新读 %d  存量补读 %d\n", result.NamesRead, result.NamesFilled)
	fmt.Printf("  卡类型补正 %d\n", result.TypesFixed)
	fmt.Printf("  timeline 读取 %d\n", result.TimelineRead)
	for _, e := range result.Errors {
		fmt.Println("  ! " + e)
	}
	return 0
}

// cmdScanPlugins 扫描插件库并落库——契约对齐 Program.cs §ScanPluginsCommand（输出行与计数口径逐字对齐）。
func cmdScanPlugins(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	start := time.Now()
	result := &core.ScanResult{}
	dlls, _, configs := core.ScanPlugins(hub.Core(), cfg, result, func(msg string) {
		fmt.Println(msg)
	})
	span := time.Since(start)
	fmt.Printf("插件库扫描完成：%d 个 dll · 耗时 %.1f 秒 · 配置文件 %d 个\n", dlls, span.Seconds(), configs)
	for _, e := range result.Errors {
		fmt.Fprintln(os.Stderr, "  ! "+e)
	}
	rows, err := hub.Core().LoadPlugins()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	withGuid := 0
	for _, row := range rows {
		if row.Guid != "" {
			withGuid++
		}
	}
	fmt.Printf("插件项 %d 条 · 非插件 dll %d 个\n", withGuid, len(rows)-withGuid)
	return 0
}

// cmdScanScope 按范围扫描（面板「扫描主要 / 追加库扫描」的 CLI 通道）——契约对齐 Program.cs §ScanScopeCommand。
func cmdScanScope(rest []string, scope core.ScanScope) int {
	dbPath := dbFlag(&rest)
	force := takeSwitch(&rest, "--force")
	steps := takeFlag(&rest, "--steps")
	thumb := 256
	if v := takeFlag(&rest, "--thumb"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			thumb = n
		}
	}
	quality := 82
	if v := takeFlag(&rest, "--quality"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			quality = n
		}
	}
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	if err = hub.EnsureMigrated(core.ModLibRoots(cfg), core.CardLibRoots(cfg)); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	plan, err := loadPlan(hub, steps)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("库: " + hub.Core().Path())
	if scope == core.ScanScopePreset {
		fmt.Println("范围: 主要库——预置条目（mod 主库 / 缓存库槽位 + 卡片 female / coordinate / Studio）")
	} else {
		fmt.Println("范围: 追加库——使用者添加的库根（mod 冷冻库 + 卡片附加库）")
	}
	result, err := core.ScanAll(hub, cfg, nil, scope, force, thumb, quality, plan, func(msg string) {
		fmt.Println(msg)
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println()
	fmt.Printf("扫描完成：%.1f 秒\n", result.Elapsed.Seconds())
	fmt.Printf("  枚举 %d  新增/更新 %d  跳过 %d  非卡 %d  非 mod %d  失败 %d  清理 %d\n",
		result.Seen, result.Added, result.Skipped, result.NonCard, result.NonMod, result.Failed, result.Removed)
	fmt.Printf("  角色名：新读 %d  存量补读 %d\n", result.NamesRead, result.NamesFilled)
	fmt.Printf("  卡类型补正 %d\n", result.TypesFixed)
	fmt.Printf("  timeline 读取 %d\n", result.TimelineRead)
	if result.PluginDlls > 0 {
		fmt.Printf("  插件库：%d 个 dll · 插件 %d 项 · 配置文件 %d 个\n", result.PluginDlls, result.Plugins, result.PluginConfigs)
	}
	for _, e := range result.Errors {
		fmt.Println("  ! " + e)
	}
	return 0
}

// ---- G5 查询族（authors / cards / todos / todo-add / todo-close / noncards / noncard-move） ----

// cmdAuthors 列出作者清单（按发布的 mod 数量倒序）——与面板「按作者筛选」同一数据源（聚合表 mod_author）。
// 契约对齐 Program.cs §AuthorsCommand。
func cmdAuthors(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, _, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	fmt.Println("库: " + hub.Core().Path())
	list, err := core.QueryAuthorsHub(hub)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("作者数: " + n0(int64(len(list))))
	var total int64
	for _, a := range list {
		total += a.Count
	}
	fmt.Println("mod 行合计: " + n0(total))
	fmt.Println()
	shown := 0
	for _, a := range list {
		if shown >= 30 {
			break
		}
		name := a.Author
		if strings.TrimSpace(name) == "" {
			name = "（无作者）"
		}
		fmt.Println("  " + padLeft(strconv.FormatInt(a.Count, 10), 5) + " 个  " + name)
		shown++
	}
	return 0
}

// cmdCards 列出卡片（按指定排序键）——与面板「卡片排序」同一实现（QueryCardsHub）。
// 契约对齐 Program.cs §CardsCommand。
func cmdCards(rest []string) int {
	dbPath := dbFlag(&rest)
	order := takeFlag(&rest, "--order")
	if order == "" {
		order = "mtime"
	}
	desc := takeSwitch(&rest, "--desc")
	q := takeFlag(&rest, "--q")
	limit := 40
	if v := takeFlag(&rest, "--limit"); v != "" {
		if n, convErr := strconv.Atoi(v); convErr == nil {
			limit = n
		}
	}
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	orderNote := " 升序"
	switch order {
	case "mtime":
		orderNote = "（恒倒序）"
	case "miss":
		orderNote = "（固定序：黑 → 灰 → 黄 → 绿）"
	default:
		if desc {
			orderNote = " 降序"
		}
	}
	qNote := q
	if q == "" {
		qNote = "（无）"
	}
	fmt.Println("库: " + hub.Core().Path())
	fmt.Println("排序: " + order + orderNote + " · 关键词: " + qNote)
	fmt.Println()
	rows, err := core.QueryCardsHub(hub, cfg, 1, limit, "all", q, nil, "", order, desc)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("共列 " + strconv.Itoa(len(rows)) + " 张")
	for _, r := range rows {
		name := r.CharaName
		if name == "" {
			name = "（无名）"
		}
		tail := ""
		if r.TimelineSet {
			tail = "  |  timeline " + fmt3(r.TimelineSeconds) + " 秒"
		}
		fmt.Println("  " + name + "  |  " + r.Folder + "  |  " + r.FileName + tail)
	}
	return 0
}

// cmdTodos 列出待办——按分支输出（库已离线 / 卡片 / 手输三类，id 供 todo-close 用）。
// 契约对齐 Program.cs §TodosCommand。
func cmdTodos(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rows, err := hub.Core().ListTodoRows()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	all := append([]core.RootEntry{}, cfg.ModRootsOrdered()...)
	all = append(all, cfg.CardRootsOrdered()...)
	fmt.Println("库: " + hub.Core().Path())
	fmt.Println("待办: " + strconv.Itoa(len(rows)) + " 条")
	printTodoGroup("库已离线", "offline", rows, all)
	printTodoGroup("卡片", "card", rows, all)
	printTodoGroup("手输", "manual", rows, all)
	return 0
}

// printTodoGroup 按分支打印一组待办——离线库条目的路径与备注从库根配置还原（配置是唯一真相源，不双写）。
func printTodoGroup(title, kind string, rows []store.TodoRow, all []core.RootEntry) {
	var group []store.TodoRow
	for _, t := range rows {
		if t.Kind == kind {
			group = append(group, t)
		}
	}
	if len(group) == 0 {
		return
	}
	fmt.Println("  [" + title + "] " + strconv.Itoa(len(group)) + " 条")
	for _, t := range group {
		tail := "  （登记于 " + t.CreatedAt + "）"
		switch kind {
		case "offline":
			path, note := t.Key, ""
			for _, e := range all {
				if strings.EqualFold(strings.TrimSpace(e.Path), t.Key) {
					path, note = e.Path, e.Note
					break
				}
			}
			if strings.TrimSpace(note) == "" {
				fmt.Println("    #" + strconv.FormatInt(t.Id, 10) + "  " + path + tail)
			} else {
				fmt.Println("    #" + strconv.FormatInt(t.Id, 10) + "  " + path + "  备注：" + note + tail)
			}
		case "card":
			fmt.Println("    #" + strconv.FormatInt(t.Id, 10) + "  " + t.Text + "  —— " + t.RefName + "  <" + t.RefPath + ">" + tail)
		default:
			fmt.Println("    #" + strconv.FormatInt(t.Id, 10) + "  " + t.Text + tail)
		}
	}
}

// cmdTodoAdd 新建待办——`todo-add <文本>` 手输；`todo-add --card <卡片路径> <文本>` 挂到卡片上。
// 契约对齐 Program.cs §TodoAddCommand。
func cmdTodoAdd(rest []string) int {
	dbPath := dbFlag(&rest)
	card := takeFlag(&rest, "--card")
	text := strings.TrimSpace(strings.Join(rest, " "))
	hub, _, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	var problem string
	if strings.TrimSpace(card) != "" {
		problem, err = hub.Core().AddCardTodo(card, filepath.Base(card), text, nowStamp())
	} else {
		problem, err = hub.Core().AddManualTodo(text, nowStamp())
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if problem != "" {
		fmt.Println("! " + problem)
		return 1
	}
	if strings.TrimSpace(card) == "" {
		fmt.Println("已登记手输待办：" + text)
	} else {
		fmt.Println("已登记卡片待办：" + text + "  —— " + card)
	}
	return 0
}

// cmdTodoClose 关闭待办——`todo-close <id>`（id 由 `todos` 列出）。
func cmdTodoClose(rest []string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 1 {
		fmt.Println("用法: todo-close <待办 id>")
		return 2
	}
	id, convErr := strconv.ParseInt(rest[0], 10, 64)
	if convErr != nil {
		fmt.Println("用法: todo-close <待办 id>")
		return 2
	}
	hub, _, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rows, err := hub.Core().ListTodoRows()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	found := false
	for _, t := range rows {
		if t.Id == id {
			found = true
			if err = hub.Core().CloseTodo(id); err != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
				return 1
			}
			break
		}
	}
	if !found {
		fmt.Println("! 没有这条待办: #" + strconv.FormatInt(id, 10))
		return 1
	}
	fmt.Println("已关闭待办 #" + strconv.FormatInt(id, 10))
	return 0
}

// cmdNonCards 列出非卡 / 非 mod 清单——跨库合并，按字节数降序；--moved 含已搬走。
// 契约对齐 Program.cs §NonCardsCommand。
func cmdNonCards(rest []string) int {
	dbPath := dbFlag(&rest)
	includeMoved := takeSwitch(&rest, "--moved")
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	pending, err := core.CountNonCardsHub(hub, cfg, false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	all, err := core.CountNonCardsHub(hub, cfg, true)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	rows, err := core.ListNonCardsHub(hub, cfg, includeMoved)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("库: " + hub.Core().Path())
	head := "非卡 / 非 mod: " + n0(pending) + " 个待处置"
	if all > pending {
		head += " · 已搬走 " + n0(all-pending) + " 个"
	}
	if includeMoved {
		head += "（列出含已搬走）"
	}
	fmt.Println(head)
	for _, r := range rows {
		side := "卡片库"
		if r.Side == "mod" {
			side = "mod 库"
		}
		line := "  #" + strconv.FormatInt(r.Id, 10) + " [" + strconv.Itoa(r.Lib) + "] " + side + " · " + r.Reason +
			" · " + n0(r.Size) + " 字节"
		if r.HasThumb {
			line += " [有图]"
		} else {
			line += " [无图]"
		}
		line += " · " + r.FilePath
		if r.MovedTo != "" {
			line += "  已搬走 → " + r.MovedTo
		}
		fmt.Println(line)
	}
	return 0
}

// cmdNonCardMove 一键把非卡 / 非 mod 文件搬到 mod 缓存库（游戏不读的那条库根）——逐条出声，失败项文件不动。
func cmdNonCardMove(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rows, err := core.ListNonCardsHub(hub, cfg, false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	movedCount, skipped := 0, 0
	for _, r := range rows {
		dest, detail, moveErr := core.MoveNonCardHub(hub, cfg, r.Lib, r.Id)
		if moveErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+moveErr.Error())
			return 1
		}
		if dest == "" {
			skipped++
			fmt.Println("  ! " + r.FilePath + " → " + detail)
			continue
		}
		movedCount++
		fmt.Println("  " + r.FilePath + " → " + dest)
	}
	left, err := core.CountNonCardsHub(hub, cfg, false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("搬运完成：成功 " + strconv.Itoa(movedCount) + " · 跳过/失败 " + strconv.Itoa(skipped) +
		" · 缓存库 " + core.ModCachePath(cfg))
	fmt.Println("剩余待处置: " + n0(left))
	return 0
}

// fmt3 小数格式化（对齐 C# 格式串 "0.###"——最多三位小数，去尾随 0）。
func fmt3(v float64) string {
	s := strconv.FormatFloat(v, 'f', 3, 64)
	s = strings.TrimRight(s, "0")
	s = strings.TrimRight(s, ".")
	return s
}

// cmdDup 列出重复副本组——同 guid 多份文件（版本 / 作者实时读 manifest，人工筛旧版的判据）。
// 契约对齐 Program.cs §DupCommand：MD5 只算冲突文件、算过就忽略（档案落主库 mod_hash）；
// 引用卡片与面板组标题行最右侧缩略图区同一数据源（每组总数 + 前三张）。
func cmdDup(rest []string) int {
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	groups, err := core.ListDuplicateGroupsHub(hub, cfg)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	var allFiles []store.ModFileRec
	guids := make([]string, 0, len(groups))
	for _, g := range groups {
		allFiles = append(allFiles, g.Files...)
		guids = append(guids, g.Guid)
	}
	hashes, computed, hashErrs, err := core.FillHashesHub(hub, allFiles)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	_ = hashes
	oldCount, err := hub.Core().CountModOld()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	cardRefs, err := core.QueryDupCardRefsHub(hub, cfg, guids, 3)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("库: " + hub.Core().Path())
	fmt.Println("重复副本组: " + strconv.Itoa(len(groups)) + "（已登记旧版 " + n0(oldCount) + " 条）· MD5 本次新增 " + strconv.Itoa(computed) + " 条档案")
	for _, e := range hashErrs {
		fmt.Println("[哈希] 算不出：" + e)
	}
	for _, g := range groups {
		fmt.Println()
		fmt.Println("● " + g.Guid + "（" + strconv.Itoa(len(g.Files)) + " 份 · 主库 " + strconv.FormatInt(g.MainCount, 10) + "）")
		refs := cardRefs[g.Guid]
		refTotal := int64(0)
		refNames := ""
		if refs != nil {
			refTotal = refs.Total
			for _, c := range refs.Top {
				if refNames != "" {
					refNames += " · "
				}
				refNames += c.FileName
			}
		}
		line := "    引用卡片 " + strconv.FormatInt(refTotal, 10) + " 张"
		if refNames != "" {
			line += "（前三张 " + refNames + "）"
		}
		fmt.Println(line)
		for _, f := range g.Files {
			info := core.ParseZipMod(f.FilePath)
			tail := ""
			if core.IsOldFileName(f.FileName) {
				tail += "  [旧版]"
			}
			if info.Error != "" {
				tail += "  ! " + info.Error
			}
			fmt.Println("    [" + core.TierName(f.Tier) + "] " + f.FileName +
				"  版本 " + fieldOrNone(info.Version, info.Found["version"]) +
				"  作者 " + fieldOrNone(info.Author, info.Found["author"]) +
				"  " + strconv.FormatInt(f.Size/1024/1024, 10) + " MB" + tail)
		}
	}
	return 0
}

// cmdRefsRead 引用补读——读某库根下已入库卡片的声明区写 mod 引用（一次性，不扫目录）。
// 契约对齐 Program.cs §RefsReadCommand + Core/RefsReader.cs。
func cmdRefsRead(rest []string) int {
	dbPath := dbFlag(&rest)
	root := strings.TrimRight(takeFlag(&rest, "--root"), "\\/")
	onlyPending := takeSwitch(&rest, "--only-pending")
	limit := 0
	if v := takeFlag(&rest, "--limit"); v != "" {
		limit, _ = strconv.Atoi(v)
	}
	if strings.TrimSpace(root) == "" {
		fmt.Fprintln(os.Stderr, "用法：refs-read --root <库根路径> [--only-pending] [--limit N]")
		return 2
	}
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	r, err := core.RefsReadRootHub(hub, cfg, root, onlyPending, limit, func(m string) {
		fmt.Println("  " + m)
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("引用补读：卡 " + strconv.FormatInt(r.Cards, 10) +
		" · 有引用 " + strconv.FormatInt(r.WithRefs, 10) +
		" · 引用行 " + strconv.FormatInt(r.RefRows, 10) +
		" · 失败 " + strconv.FormatInt(r.Failed, 10))
	return 0
}

// cmdSort 「按作者整理」命令行通道——plan 生成计划 / show 读计划表并核对 /
// exec 执行 / conflict 看冲突候选 / keep 保留一份（与面板端点同一实现）。
func cmdSort(rest []string) int {
	sub := "show"
	if len(rest) > 0 {
		sub = rest[0]
		rest = rest[1:]
	}
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()

	if sub == "plan" {
		roots := make([]string, 0, len(rest))
		for _, a := range rest {
			if !strings.HasPrefix(a, "--") {
				roots = append(roots, a)
			}
		}
		if len(roots) == 0 {
			for _, e := range cfg.ModRoots {
				if e.ReadOnly || e.Offline {
					continue
				}
				roots = append(roots, e.Path)
			}
		}
		if len(roots) == 0 {
			fmt.Fprintln(os.Stderr, "没有可整理的库根（只读 / 离线库不参与）")
			return 2
		}
		if err = hub.Core().ClearSortPlans(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		job := &core.SortJobState{}
		if err = core.BuildPlan(hub, cfg, roots, job); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("计划 #" + strconv.FormatInt(job.PlanID, 10) + "：" + job.Message)
		fmt.Println("  条目 " + strconv.FormatInt(job.Total, 10) + " · 冲突 " + strconv.FormatInt(job.Conflicts, 10))
		if job.Conflicts > 0 {
			return 1
		}
		return 0
	}

	plan, err := hub.Core().LatestSortPlan()
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if plan == nil {
		fmt.Println("还没有整理计划——先跑 sort plan")
		return 0
	}

	switch sub {
	case "conflict":
		if len(rest) < 1 {
			fmt.Fprintln(os.Stderr, "用法: sort conflict <seq>")
			return 2
		}
		seq, convErr := strconv.ParseInt(rest[0], 10, 64)
		if convErr != nil {
			fmt.Fprintln(os.Stderr, "用法: sort conflict <seq>")
			return 2
		}
		group, groupErr := core.ConflictGroup(hub, plan.ID, seq)
		if groupErr != "" {
			fmt.Fprintln(os.Stderr, groupErr)
			return 2
		}
		fmt.Println("冲突组 guid " + group.Guid + " ⇒ " + group.DestPath)
		recs := make([]store.ModFileRec, 0, len(group.Items))
		for _, c := range group.Items {
			recs = append(recs, store.ModFileRec{
				FilePath: c.FilePath, Guid: group.Guid, Tier: c.Tier, RootPath: c.RootPath,
				FileName: c.FileName, Size: c.Size, Mtime: c.Mtime,
			})
		}
		hashes, computed, hashErrors, hashErr := core.FillHashesHub(hub, recs)
		if hashErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+hashErr.Error())
			return 1
		}
		_ = computed
		for _, c := range group.Items {
			m := core.ParseZipMod(c.FilePath)
			version := m.Version
			if version == "" {
				version = "<无>"
			}
			author := m.Author
			if author == "" {
				author = "<无>"
			}
			fmt.Println("  [seq " + strconv.FormatInt(c.Seq, 10) + " · " + strconv.Itoa(c.Tier) + " 级] " + c.FilePath)
			fmt.Println("      版本 " + version + " · 作者 " + author + " · " + strconv.FormatInt(c.Size, 10) +
				" 字节 · 建 " + "<读不到>" + " · 改 " + c.Mtime)
			if md5, ok := hashes[c.FilePath]; ok && md5 != "" {
				fmt.Println("      MD5 " + md5)
			} else {
				fmt.Println("      MD5 未算出（档案缺失或读不到）")
			}
			if m.Guid == "" && m.Name == "" {
				fmt.Println("      manifest 读不到：容器内没有 manifest.xml 或缺 guid")
			}
		}
		for _, e := range hashErrors {
			fmt.Println("  ! MD5 算不出：" + e)
		}
		fmt.Println("挑一份保留：sort keep <seq>")
		return 0

	case "keep":
		if len(rest) < 1 {
			fmt.Fprintln(os.Stderr, "用法: sort keep <seq>")
			return 2
		}
		seq, convErr := strconv.ParseInt(rest[0], 10, 64)
		if convErr != nil {
			fmt.Fprintln(os.Stderr, "用法: sort keep <seq>")
			return 2
		}
		detail, keepErr := core.ResolveConflict(hub, cfg, plan.ID, seq)
		if keepErr != "" {
			fmt.Fprintln(os.Stderr, keepErr)
			return 2
		}
		fmt.Println("已处理：" + detail)
		return 0

	case "exec":
		yes := false
		for _, a := range rest {
			if a == "--yes" {
				yes = true
			}
		}
		if !yes {
			fmt.Fprintln(os.Stderr, "执行会移动磁盘文件——请加 --yes 确认（并确保 koikatsu 已关闭）")
			return 2
		}
		conflicts, err := hub.Core().CountSortPlanItems(plan.ID, core.SortStateConflict)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if conflicts > 0 {
			fmt.Fprintln(os.Stderr, "还有 "+strconv.FormatInt(conflicts, 10)+" 条冲突未解决——先处理再执行")
			return 2
		}
		job := &core.SortJobState{}
		if err = core.ExecutePlan(hub, cfg, plan.ID, job); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("执行：就位 " + strconv.FormatInt(job.Moved, 10) + " · 跳过 " + strconv.FormatInt(job.Skipped, 10) +
			" · 失败 " + strconv.FormatInt(job.Failed, 10) + " · 冲突 " + strconv.FormatInt(job.Conflicts, 10) +
			" · 清空目录 " + strconv.FormatInt(job.PrunedDirs, 10))
		for _, e := range job.Errors {
			fmt.Fprintln(os.Stderr, "  失败："+e)
		}
		if job.Failed > 0 {
			return 1
		}
		return 0
	}

	if sub != "show" {
		fmt.Fprintln(os.Stderr, "未知子命令: "+sub+"（可用：plan / show / exec / conflict / keep）")
		return 2
	}
	conflicts, err := core.RecheckPlan(hub, plan.ID)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if err = hub.Core().FinishSortPlan(plan.ID, plan.State, plan.ItemCount, conflicts, plan.Note); err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	fmt.Println("计划 #" + strconv.FormatInt(plan.ID, 10) + " · " + plan.CreatedAt + " · 状态 " + plan.State +
		" · 条目 " + strconv.FormatInt(plan.ItemCount, 10))
	for _, line := range strings.Split(plan.Scope, "\n") {
		if strings.TrimSpace(line) != "" {
			fmt.Println("  范围: " + line)
		}
	}
	var pending, conflict, moved, failed, skipped int64
	all, err := hub.Core().QuerySortPlanItems(plan.ID)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	for _, r := range all {
		switch r.State {
		case core.SortStateMoved:
			moved++
		case core.SortStateConflict:
			conflict++
		case core.SortStateFailed:
			failed++
		case core.SortStateSkipped:
			skipped++
		default:
			pending++
		}
	}
	shownConflict := 0
	for _, r := range all {
		if r.State != core.SortStateConflict || shownConflict >= 40 {
			continue
		}
		fmt.Println("  [冲突] seq " + strconv.FormatInt(r.Seq, 10) + "  " + r.SrcPath + " ⇒ " + r.DestPath + "（" + r.Note + "）")
		shownConflict++
	}
	shown := 0
	for _, r := range all {
		if r.State != core.SortStatePending || shown >= 20 {
			continue
		}
		fmt.Println("  [待搬] " + r.Folder + " ← " + r.SrcPath)
		shown++
	}
	fmt.Println("统计：待搬 " + strconv.FormatInt(pending, 10) + " · 就位 " + strconv.FormatInt(moved, 10) +
		" · 冲突 " + strconv.FormatInt(conflict, 10) + " · 失败 " + strconv.FormatInt(failed, 10) +
		" · 跳过 " + strconv.FormatInt(skipped, 10))
	if conflict > 0 {
		fmt.Fprintln(os.Stderr, "冲突未清零——处理完（或改名 / 删掉目标同名文件）再跑一次 sort show 核对")
		return 1
	}
	return 0
}

// ---------- 旧版管理（keep-version / mark-old / swap / promote） ----------
// 契约对齐 Program.cs §OldOpCommand + Data/StoreHub.cs §KeepVersion / §MarkOld / §SwapVersion / §PromoteOld。

// cmdOldOp 旧版管理四条命令的统一入口——失败 exit 3（与 C# 同档），成功打印「完成: <说明>」。
func cmdOldOp(rest []string, op string) int {
	dbPath := dbFlag(&rest)
	if len(rest) < 2 {
		fmt.Fprintln(os.Stderr, "用法: "+op+" <guid> <文件路径>")
		return 2
	}
	guid, path := rest[0], rest[1]
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	var detail string
	switch op {
	case "keep-version":
		detail, err = core.KeepVersionHub(hub, cfg, guid, path)
	case "mark-old":
		detail, err = core.MarkOldHub(hub, cfg, guid, path)
	case "swap":
		detail, err = core.SwapVersionHub(hub, cfg, guid, path)
	default:
		detail, err = core.PromoteOldHub(hub, cfg, guid, path)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "失败: "+err.Error())
		return 3
	}
	fmt.Println("完成: " + detail)
	return 0
}

// cmdIndexImport 索引导入——读 kkc 风格索引（整理/index 分片或 index.tsv 全量）写库，不扫文件系统。
// 契约对齐 Program.cs §IndexImportCommand + Core/IndexImporter.cs。
func cmdIndexImport(rest []string) int {
	root := strings.TrimRight(strings.TrimRight(takeFlag(&rest, "--root"), `\`), "/")
	index := takeFlag(&rest, "--index")
	oldRoot := takeFlag(&rest, "--old-root")
	purgeFirst := takeSwitch(&rest, "--purge-first")
	if root == "" {
		fmt.Fprintln(os.Stderr, "用法：index-import --root <库根路径> [--index <索引目录>] [--old-root <旧库根路径>]")
		return 2
	}
	dbPath := dbFlag(&rest)
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	if oldRoot != "" {
		fmt.Fprintln(os.Stderr, "index-import：--old-root 未移植（库根路径迁移与旧记录清理归 card-sort 段一并补）")
		return 3
	}
	if purgeFirst {
		fmt.Fprintln(os.Stderr, "index-import：--purge-first 未移植（同根重建清理归 card-sort 段一并补）")
		return 3
	}
	modRoot := findRootEntry(cfg.ModRoots, root)
	cardRoot := findRootEntry(cfg.CardRoots, root)
	if modRoot == nil && cardRoot == nil {
		fmt.Fprintln(os.Stderr, "该路径不是已配置的库根："+root+"——先 roots-add，或加 --old-root 迁移")
		return 2
	}
	useMod := modRoot
	if useMod == nil {
		useMod = cardRoot
	}
	res, err := core.ImportIndex(hub, useMod, cardRoot, index, func(msg string) { fmt.Println("  " + msg) })
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	if res.Error != "" {
		fmt.Fprintln(os.Stderr, "导入失败："+res.Error)
		return 3
	}
	fmt.Println("索引源登记：root=" + root + " · 摘要 " + res.Digest + " · 目录 " + res.IndexDir)
	return 0
}

// findRootEntry 在库根列表里按路径（大小写不敏感、忽略尾分隔符）找条目。
func findRootEntry(list []core.RootEntry, path string) *core.RootEntry {
	for i := range list {
		one := strings.TrimRight(strings.TrimRight(list[i].Path, `\`), "/")
		if strings.EqualFold(one, path) {
			return &list[i]
		}
	}
	return nil
}

// cmdIdx 查索引元数据（作品 / 作者 / 角色 / 来源）——索引导入独有字段。
// 契约对齐 Program.cs §IdxCommand + Data/StoreHub.cs §QueryIndexMeta。
func cmdIdx(rest []string) int {
	dbPath := dbFlag(&rest)
	kind := takeFlag(&rest, "--kind")
	work := takeFlag(&rest, "--work")
	author := takeFlag(&rest, "--author")
	series := takeFlag(&rest, "--series")
	vn := takeFlag(&rest, "--vn")
	charaID := takeFlag(&rest, "--chara-id")
	q := takeFlag(&rest, "--q")
	root := takeFlag(&rest, "--root")
	limit := 50
	if v := takeFlag(&rest, "--limit"); v != "" {
		if n, err := parseIntLoose(v); err == nil {
			limit = n
		}
	}
	wide := takeSwitch(&rest, "--wide")
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rows, err := core.QueryIndexMetaHub(hub, cfg, root, kind, work, author, q, series, vn, charaID, limit)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	line := "索引元数据：命中 " + strconv.Itoa(len(rows)) + " 条（上限 " + strconv.Itoa(limit) + "）"
	if wide {
		line += " · 宽表 13 列"
	}
	fmt.Println(line)
	for _, r := range rows {
		s := "  " + r.IdxId + " | " + r.Kind + " | " + r.Work + " | " + r.Chara + " | " + r.Author + " | " + r.FilePath
		if wide {
			s += " | " + r.Series + " | " + r.VnId + " | " + r.VnName + " | " + r.VndbCharaId + " | " +
				r.CharaIdConf + " | " + r.Category + " | " + r.SubCategory
		}
		fmt.Println(s)
	}
	return 0
}

// parseIntLoose 整数解析（失败返回错误）。
func parseIntLoose(s string) (int, error) {
	s = strings.TrimSpace(s)
	v := 0
	neg := false
	if strings.HasPrefix(s, "-") {
		neg = true
		s = s[1:]
	}
	if s == "" {
		return 0, fmt.Errorf("空值")
	}
	for _, ch := range s {
		if ch < '0' || ch > '9' {
			return 0, fmt.Errorf("非整数: %s", s)
		}
		v = v*10 + int(ch-'0')
	}
	if neg {
		return -v, nil
	}
	return v, nil
}

// cmdCardEdit 改卡片字段（姓 / 名 / 爱称 / 性格 / 敏感带 / 五项是否接受）——原版留档后最小改动写回。
// 契约对齐 Program.cs §CardEditCommand + Core/CardEdit.cs §Apply。
func cmdCardEdit(rest []string) int {
	if len(rest) < 1 {
		return requireArgs("card-edit <card.png> [--last X] [--first Y] [--nick Z] [--personality N] [--weak-point N] [--denial <键>=<yes|no>] [--archive <dir>]")
	}
	path := rest[0]
	rest = rest[1:]
	archive, _ := takeFlagPresent(&rest, "--archive")
	var edit core.CardParamEdit
	if v, ok := takeFlagPresent(&rest, "--last"); ok {
		edit.LastName = &v
	}
	if v, ok := takeFlagPresent(&rest, "--first"); ok {
		edit.FirstName = &v
	}
	if v, ok := takeFlagPresent(&rest, "--nick"); ok {
		edit.NickName = &v
	}
	if v, ok := takeFlagPresent(&rest, "--personality"); ok {
		n, err := parseIntLoose(v)
		if err != nil {
			fmt.Println("--personality 需要整数：" + v)
			return 2
		}
		edit.Personality = &n
	}
	if v, ok := takeFlagPresent(&rest, "--weak-point"); ok {
		n, err := parseIntLoose(v)
		if err != nil {
			fmt.Println("--weak-point 需要整数：" + v)
			return 2
		}
		edit.WeakPoint = &n
	}
	denials := takeAllFlag(&rest, "--denial")
	if len(denials) > 0 {
		deny := make([]*bool, len(core.DenialKeys))
		for _, d := range denials {
			eq := strings.IndexByte(d, '=')
			if eq <= 0 {
				fmt.Println("--denial 参数格式应为 <键>=<yes|no>：" + d)
				return 2
			}
			key := d[:eq]
			val := d[eq+1:]
			slot := -1
			for i, k := range core.DenialKeys {
				if k == key {
					slot = i
					break
				}
			}
			if slot < 0 {
				fmt.Println("未知的「是否接受」键：" + key + "（可用：" + strings.Join(core.DenialKeys, " ") + "）")
				return 2
			}
			flag := false
			switch val {
			case "yes", "true":
				flag = true
			case "no", "false":
				flag = false
			default:
				fmt.Println("--denial 的值应为 yes 或 no：" + d)
				return 2
			}
			v := flag
			deny[slot] = &v
		}
		edit.Denial = deny
	}
	layout := core.ParseCardLayout(path)
	if layout.Error != "" {
		fmt.Println("布局解析失败：" + layout.Error)
		return 3
	}
	cur := core.ReadCardParams(path, layout)
	if cur.Error != "" {
		fmt.Println("字段读取失败：" + cur.Error)
		return 3
	}
	fmt.Println("编辑前     : " + cardFieldSummary(cur))
	r := core.ApplyCardEdit(path, layout, cur, edit, archive)
	if !r.Ok {
		fmt.Println("编辑失败   : " + r.Error)
		return 3
	}
	fmt.Println("改动       : " + strings.Join(r.Changes, " · "))
	fmt.Println("字节增量   : " + strconv.Itoa(r.Delta) + "（新大小 " + n0(r.NewSize) + " 字节）")
	if r.ArchivedFile != "" {
		fmt.Println("留档       : " + r.ArchivedFile + "（" + n0(r.ArchivedSize) + " 字节）")
	}
	after := core.ParseCardLayout(path)
	now := core.ReadCardParams(path, after)
	fmt.Println("编辑后     : " + cardFieldSummary(now))
	fmt.Println("结构复验   : 块表 " + strconv.Itoa(len(after.Blocks)) + " 条 · 载荷 " + n0(after.PayloadSize) + " 字节 · 载荷起点 " + n0(after.PayloadStart))
	for _, b := range after.Blocks {
		fmt.Println("  " + padRight(b.Name, 12) + " pos=" + n0(b.Pos) + "  size=" + n0(b.Size))
	}
	return 0
}

// cardFieldSummary 可编辑字段的一行摘要（编辑前后打印用）——对齐 C# CardFieldSummary。
func cardFieldSummary(info *core.CardParamInfo) string {
	sb := strings.Builder{}
	sb.WriteString("姓「" + info.LastName + "」 名「" + info.FirstName + "」 爱称「" + info.NickName + "」")
	sb.WriteString(" 性格 " + strconv.Itoa(info.Personality))
	wpName, wpKnown := core.WeakPointNameOf(info.WeakPoint)
	if !wpKnown {
		wpName = "(未收录)"
	}
	sb.WriteString(" 敏感带 " + strconv.Itoa(info.WeakPoint) + " " + wpName)
	sb.WriteString(" 接受[")
	for i, k := range core.DenialKeys {
		if i > 0 {
			sb.WriteString(" ")
		}
		sb.WriteString(core.DenialNames[i] + "=")
		if i < len(info.Denial) && info.Denial[i] {
			sb.WriteString("是")
		} else {
			sb.WriteString("否")
		}
		_ = k
	}
	sb.WriteString("]")
	return sb.String()
}

// takeFlagPresent 取出「<name> <值>」并从 rest 里移除；返回是否给了这个参数（区分「未给」与「给了空串」）。
func takeFlagPresent(rest *[]string, name string) (string, bool) {
	for i := 0; i < len(*rest); i++ {
		if (*rest)[i] == name && i+1 < len(*rest) {
			value := (*rest)[i+1]
			*rest = append((*rest)[:i], (*rest)[i+2:]...)
			return value, true
		}
	}
	return "", false
}

// takeAllFlag 取出全部同名参数值（可重复出现的选项）并移出列表。
func takeAllFlag(rest *[]string, name string) []string {
	out := make([]string, 0, 4)
	for {
		v, ok := takeFlagPresent(rest, name)
		if !ok {
			break
		}
		out = append(out, v)
	}
	return out
}

// cmdU3d 列出 zipmod 容器内 unity3d 包中的贴图（可选导出缩小版 PNG）——对齐 C# §U3dCommand。
func cmdU3d(rest []string) int {
	if len(rest) < 1 {
		fmt.Fprintln(os.Stderr, "用法: u3d <zipmod> [--tex <目录>] [--max N]")
		return 2
	}
	zipPath := rest[0]
	outDir := takeFlag(&rest, "--tex")
	maxCount := parseIntOr(takeFlag(&rest, "--max"), 0)
	if _, statErr := os.Stat(zipPath); statErr != nil {
		fmt.Fprintln(os.Stderr, "文件不存在: "+zipPath)
		return 2
	}
	entries, err := core.ListZipEntries(zipPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "读取容器失败: "+err.Error())
		return 2
	}
	found := 0
	for _, e := range entries {
		if !strings.HasSuffix(strings.ToLower(e.Path), ".unity3d") {
			continue
		}
		found++
		fmt.Println("== " + e.Path + " · " + n0(int64(e.Size)) + " 字节")
		bytes, readErr := core.ReadZipEntry(zipPath, e.Path, core.U3dEntryMaxBytes)
		if readErr != nil {
			fmt.Println("  读取失败：" + readErr.Error())
			continue
		}
		texs, parseErr := core.ListTextures(bytes)
		if parseErr != "" {
			fmt.Println("  解析失败：" + parseErr)
			continue
		}
		fmt.Println("  贴图 " + strconv.Itoa(len(texs)) + " 张")
		for k, t := range texs {
			fmt.Println("   #" + strconv.FormatInt(t.PathId, 10) + " " + t.Name + " · " +
				strconv.Itoa(t.Width) + "×" + strconv.Itoa(t.Height) + " · 格式 " + strconv.Itoa(t.Format) +
				" · " + n0(int64(t.DataLength)) + " 字节")
			if outDir != "" && (maxCount <= 0 || k < maxCount) {
				png, pngErr := core.RenderTexturePng(bytes, t.PathId, 512)
				if pngErr != "" {
					fmt.Println("     导出失败：" + pngErr)
					continue
				}
				if err := os.MkdirAll(outDir, 0o755); err != nil {
					fmt.Println("     导出失败：" + err.Error())
					continue
				}
				file := filepath.Join(outDir, "u3d_"+strconv.FormatInt(t.PathId, 10)+".png")
				if err := os.WriteFile(file, png, 0o644); err != nil {
					fmt.Println("     导出失败：" + err.Error())
					continue
				}
				fmt.Println("     → " + file)
			}
		}
	}
	if found == 0 {
		fmt.Println("容器内没有 .unity3d 条目")
	}
	return 0
}

// cmdU3dDb 按库副本解析某 mod 的 unity3d 条目并落档（与面板端点共用同一实现）——对齐 C# §U3dDbCommand。
func cmdU3dDb(dbPath string, rest []string) int {
	force := takeSwitch(&rest, "--force")
	if len(rest) < 2 {
		fmt.Fprintln(os.Stderr, "用法: u3d-db <guid> <条目路径> [--force]")
		return 2
	}
	guid, entryPath := rest[0], rest[1]
	hub, cfg, err := openHub(dbPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
		return 1
	}
	defer hub.Close()
	rec, detail := core.AnalyzeU3d(hub, cfg, guid, entryPath, force)
	if rec == nil {
		fmt.Fprintln(os.Stderr, "失败: "+detail)
		return 3
	}
	fmt.Println("guid       : " + rec.Guid)
	fmt.Println("条目       : " + rec.EntryPath)
	fmt.Println("容器文件   : " + rec.FilePath)
	fmt.Println("大小       : " + n0(int64(rec.Size)) + " 字节")
	fmt.Println("贴图张数   : " + n0(int64(rec.TextureCount)))
	if rec.Cached {
		fmt.Println("建档于     : " + rec.ParsedAt + "（命中已有档案）")
	} else {
		fmt.Println("建档于     : " + rec.ParsedAt + "（本次新建）")
	}
	fmt.Println()
	fmt.Println("贴图清单：")
	for _, t := range core.ParseU3dTextures(rec.Textures) {
		fmt.Println("  #" + strconv.FormatInt(t.PathId, 10) + "  " + strconv.Itoa(t.Width) + "×" +
			strconv.Itoa(t.Height) + "  格式 " + strconv.Itoa(t.Format) + "  " + n0(int64(t.DataLength)) + " 字节")
	}
	return 0
}

// cmdServe 起本地面板（阻塞到进程退出）——契约对齐 Program.cs §ServeCommand。
func cmdServe(rest []string) int {
	dbPath := dbFlag(&rest)
	port := parseIntOr(takeFlag(&rest, "--port"), 8539)
	openBrowser := takeSwitch(&rest, "--open")
	takeSwitch(&rest, "--browse-debug")
	app := web.New(dbPath, version)
	if err := app.Serve(port, openBrowser); err != nil {
		fmt.Fprintln(os.Stderr, "面板启动失败: "+err.Error())
		return 1
	}
	return 0
}
