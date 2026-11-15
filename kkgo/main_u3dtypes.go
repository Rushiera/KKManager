// u3d-types 命令——裸 .unity3d（AssetBundle）对象类型分布。
// 与 u3d 命令的分工：u3d 吃 zipmod 容器并只列贴图；u3d-types 直吃裸包、给全对象类型面。
package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"kkgo/internal/core"
)

// cmdU3dTypes 统计裸 .unity3d 包的对象类型分布（单文件或目录）。
func cmdU3dTypes(rest []string) int {
	if len(rest) < 1 {
		fmt.Fprintln(os.Stderr, "用法: u3d-types <file.unity3d|目录> [--max N]")
		return 2
	}
	target := rest[0]
	maxFiles := parseIntOr(takeFlag(&rest, "--max"), 0)
	info, statErr := os.Stat(target)
	if statErr != nil {
		fmt.Fprintln(os.Stderr, "文件不存在: "+target)
		return 2
	}
	files := []string{target}
	if info.IsDir() {
		entries, err := os.ReadDir(target)
		if err != nil {
			fmt.Fprintln(os.Stderr, "读取目录失败: "+err.Error())
			return 2
		}
		files = files[:0]
		for _, e := range entries {
			if e.IsDir() {
				continue
			}
			if strings.HasSuffix(strings.ToLower(e.Name()), ".unity3d") {
				files = append(files, filepath.Join(target, e.Name()))
			}
		}
		if len(files) == 0 {
			fmt.Fprintln(os.Stderr, "目录内没有 .unity3d 文件")
			return 2
		}
	}
	failed := 0
	for i, f := range files {
		if maxFiles > 0 && i >= maxFiles {
			fmt.Println("… 余 " + strconv.Itoa(len(files)-i) + " 个文件未扫（--max " + strconv.Itoa(maxFiles) + "）")
			break
		}
		bytes, readErr := os.ReadFile(f)
		if readErr != nil {
			fmt.Println("== " + filepath.Base(f) + " · 读取失败：" + readErr.Error())
			failed++
			continue
		}
		fmt.Println("== " + filepath.Base(f) + " · " + n0(int64(len(bytes))) + " 字节")
		counts, total, diag := core.ListObjectTypes(bytes)
		if diag != "" {
			fmt.Println("  解析失败：" + diag)
			failed++
			continue
		}
		fmt.Println("  对象 " + strconv.Itoa(total) + " 个 · 类型 " + strconv.Itoa(len(counts)) + " 种")
		for _, c := range counts {
			fmt.Println("   " + c.Name + "（" + strconv.Itoa(c.ClassID) + "）× " + strconv.Itoa(c.Count))
		}
	}
	if failed > 0 {
		return 1
	}
	return 0
}
