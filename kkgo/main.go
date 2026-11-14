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
	"unicode/utf8"

	"kkgo/internal/core"
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
	fmt.Println("  card-timeline <card.png> [tracks]     场景卡 timeline（长度 / timeScale / 关键帧 / 轨道树）")
	fmt.Println("  thumb <card.png> <out.jpg>            缩略图（JPEG；--thumb N / --quality Q）")
	fmt.Println("  filehash <path>                       文件 MD5（重复副本比对判据）")
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
