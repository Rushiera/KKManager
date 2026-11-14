// kkgo — KK Manager 的 Go 重写（对照 C# 0.21.9）。
//
// 本轮已移植：modinfo / cardinfo / card-structure（解析核最小闭环）。
// 命令面与输出格式对齐 KMR.exe 同名命令（Program.cs §ModInfoCommand / §CardInfoCommand / §CardStructureCommand）。
package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"

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
	fmt.Println("guid       : " + orNone(m.Guid))
	fmt.Println("name       : " + orNone(m.Name))
	fmt.Println("version    : " + orNone(m.Version))
	fmt.Println("author     : " + orNone(m.Author))
	fmt.Println("website    : " + orNone(m.Website))
	fmt.Println("schema-ver : " + orNone(m.SchemaVer))
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
}

// orNone 空值显示 <无>（对齐 C# ?? "<无>"）。
func orNone(s string) string {
	if s == "" {
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
