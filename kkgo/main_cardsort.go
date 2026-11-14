// card-sort 命令族（卡片整理链）——契约对齐 Program.cs §CardSortCommand。
// 十一个子命令：plan / show / exec / rules / moves / rollback / delete / delete-list / learn / layout / refs。
package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// cmdCardSort 卡片整理命令族入口。
func cmdCardSort(dbPath string, rest []string) int {
	sub := "show"
	if len(rest) > 0 {
		sub = rest[0]
		rest = rest[1:]
	}
	outPath := takeFlag(&rest, "--out")
	if outPath == "" {
		outPath = defaultDataPath("card-plan.tsv")
	}
	rulesPath := takeFlag(&rest, "--rules")
	dest := takeFlag(&rest, "--dest")
	yes := takeSwitch(&rest, "--yes")
	keepPending := takeSwitch(&rest, "--keep-pending")
	defaultPlanPath := defaultDataPath("card-plan.tsv")

	if sub == "refs" {
		refsRoot := takeFlag(&rest, "--root")
		if refsRoot == "" {
			fmt.Fprintln(os.Stderr, "用法: card-sort refs --root <整理根> [--out <引用表.tsv>]")
			return 2
		}
		refsOut := outPath
		if strings.EqualFold(refsOut, defaultPlanPath) {
			refsOut = defaultDataPath("card-refs.tsv")
		}
		count, err := core.ExportCardRefs(refsRoot, refsOut, func(m string) { fmt.Println(m) })
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("引用表已写出：" + refsOut + "（" + strconv.Itoa(count) + " 张卡）")
		return 0
	}

	if sub == "rules" {
		writePath := takeFlag(&rest, "--write")
		importPath := takeFlag(&rest, "--import")
		origin := takeFlag(&rest, "--origin")
		if origin == "" {
			origin = "manual"
		}
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if importPath != "" {
			imported := core.LoadCardRules(importPath, func(m string) { fmt.Println(m) })
			n, err := hub.Core().MergeCardRules(toStoreRules(imported), origin, true)
			if err != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
				return 1
			}
			fmt.Println("已导入规则库：" + importPath + " → " + strconv.Itoa(n) + " 条（origin=" + origin + "）")
		}
		dbRules, err := hub.Core().LoadCardRules()
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if len(dbRules) == 0 {
			fmt.Println("规则库为空：" + dbPath + "——先导入 card-sort rules --import <规则.tsv> [--origin manual]")
			return 0
		}
		if writePath != "" {
			if err := core.WriteCardRules(writePath, fromStoreRules(dbRules)); err != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
				return 1
			}
			fmt.Println("已导出规则：" + writePath + "（" + strconv.Itoa(len(dbRules)) + " 条）")
			return 0
		}
		fmt.Println("规则库：" + dbPath + "（启用中 " + strconv.Itoa(len(dbRules)) + " 条）：")
		for _, r := range dbRules {
			fmt.Println("  " + r.Kind + "\t" + r.Match + "\t" + r.Value + "\t" + r.Origin + "\t" +
				strconv.Itoa(r.Votes) + "\t" + strconv.Itoa(r.Works))
		}
		return 0
	}

	if sub == "layout" {
		layoutRoot := takeFlag(&rest, "--root")
		layoutIndex := takeFlag(&rest, "--index")
		by := takeFlag(&rest, "--by")
		if by == "" {
			by = "flat"
		}
		layoutOut := outPath
		if strings.EqualFold(layoutOut, defaultPlanPath) {
			layoutOut = defaultDataPath("card-layout.tsv")
		}
		if layoutRoot == "" || layoutIndex == "" {
			fmt.Fprintln(os.Stderr, "用法: card-sort layout --root <整理根> --index <索引.tsv> --by work|author|chara|flat [--out <计划.tsv>] [--yes]")
			return 2
		}
		if by != "work" && by != "author" && by != "chara" && by != "flat" {
			fmt.Fprintln(os.Stderr, "--by 只认 work / author / chara / flat（当前："+by+"）")
			return 2
		}
		plan := core.BuildLayoutPlan(layoutRoot, layoutIndex, by, func(m string) { fmt.Println(m) })
		if err := core.WriteCardPlan(layoutOut, plan); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		printCardPlan(plan)
		fmt.Println()
		if !yes {
			fmt.Println("计划已写出：" + layoutOut + "（按 " + by + " 重排）")
			fmt.Println("确认后执行：card-sort layout --root \"" + layoutRoot + "\" --index \"" + layoutIndex + "\" --by " + by + " --yes")
			return 0
		}
		core.ExecuteCardPlan(plan, func(m string) { fmt.Println(m) })
		pruned := core.PruneEmptyDirsCard(layoutRoot, func(m string) { fmt.Println(m) })
		if err := core.WriteCardPlan(layoutOut, plan); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("空目录清理：" + strconv.Itoa(pruned) + " 个")
		fmt.Println("状态已回写：" + layoutOut)
		if plan.Count(core.CardStateFailed) > 0 {
			return 1
		}
		return 0
	}

	if sub == "moves" {
		limit := parseIntOr(takeFlag(&rest, "--limit"), 20)
		allStates := takeSwitch(&rest, "--all")
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		state := "done"
		if allStates {
			state = ""
		}
		moves, err := hub.Core().LoadCardMoves(state)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		sort.SliceStable(moves, func(i, j int) bool {
			return moveStamp(moves[i]) > moveStamp(moves[j])
		})
		scope := "done"
		if allStates {
			scope = "全部状态"
		}
		fmt.Println("搬运记录：" + strconv.Itoa(len(moves)) + " 条（" + scope + "，按时间倒序）")
		shown := 0
		for _, mv := range moves {
			if shown >= limit {
				break
			}
			shown++
			fmt.Println("  [" + moveStamp(mv) + "] " + mv.State + " · " + mv.Work + " | " + mv.FromPath + " → " + mv.ToPath)
		}
		if len(moves) > shown {
			fmt.Println("  … 另有 " + strconv.Itoa(len(moves)-shown) + " 条（--limit 调整）")
		}
		return 0
	}

	if sub == "delete" {
		deleteRoot := takeFlag(&rest, "--root")
		deleteNote := takeFlag(&rest, "--note")
		if len(rest) == 0 {
			fmt.Fprintln(os.Stderr, "用法: card-sort delete <路径...> --root <整理根> [--note <说明>]——移入确认删除区，由人工复核后手动删除")
			return 2
		}
		if deleteRoot == "" {
			fmt.Fprintln(os.Stderr, "缺少 --root <整理根>（确认删除区落 <整理根>/99-确认删除/<时间戳>/）")
			return 2
		}
		stamp := nowCompactStamp()
		zone := filepath.Join(deleteRoot, "99-确认删除", stamp)
		movedDel, missingDel, failedDel := 0, 0, 0
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		for _, p := range rest {
			fileInfo, statErr := os.Stat(p)
			if statErr != nil {
				missingDel++
				fmt.Fprintln(os.Stderr, "  源已不在："+p)
				continue
			}
			rel, relErr := filepath.Rel(deleteRoot, p)
			if relErr != nil || strings.HasPrefix(rel, "..") {
				rel = filepath.Base(p)
			}
			destPath := filepath.Join(zone, rel)
			size := int64(0)
			if !fileInfo.IsDir() {
				size = fileInfo.Size()
			}
			if mkErr := os.MkdirAll(filepath.Dir(destPath), 0o755); mkErr != nil {
				failedDel++
				fmt.Fprintln(os.Stderr, "  失败："+p+" —— "+mkErr.Error())
				continue
			}
			if moveErr := os.Rename(p, destPath); moveErr != nil {
				failedDel++
				fmt.Fprintln(os.Stderr, "  失败："+p+" —— "+moveErr.Error())
				continue
			}
			if insErr := hub.Core().InsertCardDelete(p, destPath, size, deleteNote); insErr != nil {
				failedDel++
				fmt.Fprintln(os.Stderr, "  失败："+p+" —— "+insErr.Error())
				continue
			}
			movedDel++
		}
		fmt.Println("确认删除区：" + zone)
		fmt.Println("已移入 " + strconv.Itoa(movedDel) + " · 源已不在 " + strconv.Itoa(missingDel) + " · 失败 " + strconv.Itoa(failedDel))
		fmt.Println("该区为人工复核删除的最终落点——由人手动清理（工具不提供物理清除）。")
		if failedDel > 0 {
			return 1
		}
		return 0
	}

	if sub == "delete-list" {
		limit := parseIntOr(takeFlag(&rest, "--limit"), 50)
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		rows, err := hub.Core().LoadCardDeletes(limit)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("确认删除区记录：" + strconv.Itoa(len(rows)) + " 条（按时间倒序）")
		for _, row := range rows {
			sizeText := "目录"
			if row.Size > 0 {
				sizeText = fmt.Sprintf("%.1f MB", float64(row.Size)/1024.0/1024.0)
			}
			note := ""
			if row.Note != "" {
				note = " · " + row.Note
			}
			fmt.Println("  [" + row.DeletedAt + "] " + sizeText + " | " + row.FromPath + " → " + row.ToPath + note)
		}
		return 0
	}

	if sub == "learn" {
		from := takeFlag(&rest, "--from")
		writePath := takeFlag(&rest, "--write")
		if from == "" {
			fmt.Fprintln(os.Stderr, "用法: card-sort learn --from <索引.tsv> [--write <导出规则.tsv>]")
			return 2
		}
		learned := core.LearnCardRules(from, func(m string) { fmt.Println(m) })
		if len(learned) == 0 {
			fmt.Println("没有学到规则（索引里没有可用的「角色名 + 作品」行）")
			return 0
		}
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		n, err := hub.Core().LearnCardRules(toStoreRules(learned))
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("已写入规则库：" + dbPath + "（学 " + strconv.Itoa(n) + " 条，origin=learn；已有同名规则保留原值）")
		if writePath != "" {
			all, err := hub.Core().LoadCardRules()
			if err != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
				return 1
			}
			if err := core.WriteCardRules(writePath, fromStoreRules(all)); err != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
				return 1
			}
			fmt.Println("已导出规则：" + writePath + "（" + strconv.Itoa(len(all)) + " 条）")
		}
		return 0
	}

	if sub == "plan" {
		srcs := append([]string{}, rest...)
		if len(srcs) == 0 {
			fmt.Fprintln(os.Stderr, "用法: card-sort plan <源目录...> --dest <目标根> [--rules <规则.tsv>] [--out <计划.tsv>] [--keep-pending]")
			return 2
		}
		if dest == "" {
			fmt.Fprintln(os.Stderr, "缺少 --dest <目标根>（归位到哪个整理库根）")
			return 2
		}
		hub, _, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		if err := hub.Core().EnsureIndexTables(); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if rulesPath != "" {
			imported := core.LoadCardRules(rulesPath, func(m string) { fmt.Println(m) })
			n, mergeErr := hub.Core().MergeCardRules(toStoreRules(imported), "manual", true)
			if mergeErr != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+mergeErr.Error())
				return 1
			}
			fmt.Println("规则导入：" + rulesPath + "（" + strconv.Itoa(n) + " 条，origin=manual）")
		}
		builtinCount, err := hub.Core().CountCardRulesOfOrigin("builtin")
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if builtinCount == 0 {
			n, mergeErr := hub.Core().MergeCardRules(toStoreRules(core.BuiltinRules()), "builtin", false)
			if mergeErr != nil {
				fmt.Fprintln(os.Stderr, "诊断 "+mergeErr.Error())
				return 1
			}
			fmt.Println("规则库缺内置规则——已补 " + strconv.Itoa(n) + " 条（origin=builtin）")
		}
		storedRules, err := hub.Core().LoadCardRules()
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("规则来源：DB（" + strconv.Itoa(len(storedRules)) + " 条启用）")
		plan := core.BuildCardPlan(srcs, dest, fromStoreRules(storedRules), func(m string) { fmt.Println(m) }, keepPending)
		layoutBad := core.ValidateCardLayout(dest, plan, func(m string) { fmt.Fprintln(os.Stderr, "  "+m) })
		if layoutBad > 0 {
			fmt.Fprintln(os.Stderr, "布局校验失败："+strconv.Itoa(layoutBad)+" 条卡片目标不在作品子目录——计划未写出")
			return 3
		}
		if err := core.WriteCardPlan(outPath, plan); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		rows := make([]store.CardMoveRow, 0, len(plan.Items))
		for _, it := range plan.Items {
			if !it.Move || it.State != core.CardStatePending {
				continue
			}
			rows = append(rows, store.CardMoveRow{
				FromPath: it.SrcPath, ToPath: it.DestPath, Work: it.Work, CardType: it.CardType, Note: it.Note,
			})
		}
		n, err := hub.Core().UpsertCardMoves(rows)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("移动映射已落盘：" + strconv.Itoa(n) + " 条（card_move pending · DB " + dbPath + "）")
		printCardPlan(plan)
		fmt.Println()
		fmt.Println("计划已写出：" + outPath)
		fmt.Println("核对后执行：card-sort exec --out \"" + outPath + "\" --yes")
		return 0
	}

	loaded := core.ReadCardPlan(outPath)
	if len(loaded.Items) == 0 && sub != "rollback" {
		fmt.Println("还没有整理计划——先跑 card-sort plan")
		return 0
	}

	if sub == "show" {
		srcGone, destThere := 0, 0
		for _, it := range loaded.Items {
			if !it.Move || it.State != core.CardStatePending {
				continue
			}
			if !pathExists(it.SrcPath) {
				srcGone++
			}
			if pathExists(it.DestPath) {
				destThere++
			}
		}
		printCardPlan(loaded)
		fmt.Println()
		fmt.Println("核对：源已不在 " + strconv.Itoa(srcGone) + " 条 · 目标已存在 " + strconv.Itoa(destThere) + " 条")
		return 0
	}

	if sub == "rollback" {
		dryRun := takeSwitch(&rest, "--dry-run")
		lastN := parseIntOr(takeFlag(&rest, "--last"), 0)
		if !yes && !dryRun {
			fmt.Fprintln(os.Stderr, "回滚会移动磁盘文件——请加 --yes 确认（--dry-run 只列不搬）")
			return 2
		}
		rollbackRoot := takeFlag(&rest, "--root")
		hub, cfg, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		moves, err := hub.Core().LoadCardMoves("done")
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		if lastN > 0 && len(moves) > lastN {
			sort.SliceStable(moves, func(i, j int) bool { return moves[i].ExecutedAt > moves[j].ExecutedAt })
			moves = moves[:lastN]
		}
		if len(moves) == 0 {
			fmt.Println("没有可回滚的移动记录（done 0 条）")
			return 0
		}
		if dryRun {
			fmt.Println("dry-run：将回滚 " + strconv.Itoa(len(moves)) + " 条（未动文件）")
			for _, mv := range moves {
				fmt.Println("  " + mv.ExecutedAt + " | " + mv.ToPath + " → " + mv.FromPath)
			}
			return 0
		}
		rootPath := rollbackRoot
		if rootPath == "" {
			for _, e := range cfg.CardRoots {
				p := strings.TrimRight(e.Path, "\\/")
				if p != "" && strings.HasPrefix(strings.ToLower(moves[0].ToPath), strings.ToLower(p+"\\")) {
					rootPath = p
					break
				}
			}
		}
		if rootPath == "" {
			fmt.Fprintln(os.Stderr, "回滚定位不到库根——用 --root <整理根> 指定")
			return 3
		}
		prefix := strings.TrimRight(rootPath, "\\/")
		stores, storeErr := allStores(hub, cfg)
		if storeErr != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+storeErr.Error())
			return 1
		}
		movedFiles, skipped, failed := 0, 0, 0
		for _, mv := range moves {
			if !strings.HasPrefix(strings.ToLower(mv.ToPath), strings.ToLower(prefix+"\\")) {
				continue
			}
			if !pathExists(mv.ToPath) {
				skipped++
				continue
			}
			if pathExists(mv.FromPath) {
				failed++
				fmt.Fprintln(os.Stderr, "  原位置已有文件，跳过："+mv.FromPath)
				continue
			}
			if mkErr := os.MkdirAll(filepath.Dir(mv.FromPath), 0o755); mkErr != nil {
				failed++
				fmt.Fprintln(os.Stderr, "  回滚失败："+mv.ToPath+" —— "+mkErr.Error())
				continue
			}
			if mvErr := os.Rename(mv.ToPath, mv.FromPath); mvErr != nil {
				failed++
				fmt.Fprintln(os.Stderr, "  回滚失败："+mv.ToPath+" —— "+mvErr.Error())
				continue
			}
			if migrated, _ := migrateCardRowPath(stores, mv.ToPath, mv.FromPath); !migrated {
				fmt.Fprintln(os.Stderr, "  DB 行未迁移（跨分片/未索引）："+mv.ToPath+" → "+mv.FromPath+"——回滚后跑一次 index-import 重建")
			}
			if err := hub.Core().ReopenCardMove(mv.Id); err != nil {
				fmt.Fprintln(os.Stderr, "  状态回写失败："+err.Error())
			}
			movedFiles++
		}
		rbPlan := core.ReadCardPlan(outPath)
		touched := false
		for i := range rbPlan.Items {
			if rbPlan.Items[i].State != core.CardStateMoved {
				continue
			}
			for _, mv := range moves {
				if strings.EqualFold(rbPlan.Items[i].DestPath, mv.ToPath) {
					rbPlan.Items[i].State = core.CardStatePending
					rbPlan.Items[i].Note = appendNoteText(rbPlan.Items[i].Note, "已回滚")
					touched = true
					break
				}
			}
		}
		if touched {
			if err := core.WriteCardPlan(outPath, rbPlan); err == nil {
				fmt.Println("状态已回写：" + outPath)
			}
		}
		fmt.Println("回滚：文件回位 " + strconv.Itoa(movedFiles) + " · 跳过 " + strconv.Itoa(skipped) + " · 失败 " + strconv.Itoa(failed))
		if failed > 0 {
			return 1
		}
		return 0
	}

	if sub == "exec" {
		if !yes {
			fmt.Fprintln(os.Stderr, "执行会移动磁盘文件——请加 --yes 确认（并确保游戏 / 面板已关闭）")
			return 2
		}
		layoutBad := core.ValidateCardLayout("", loaded, func(m string) { fmt.Fprintln(os.Stderr, "  "+m) })
		if layoutBad > 0 {
			fmt.Fprintln(os.Stderr, "闸门[布局]："+strconv.Itoa(layoutBad)+" 条卡片目标不在作品子目录——拒绝执行")
			return 3
		}
		pendingMove, conflicts := 0, 0
		firstDest := ""
		for _, it := range loaded.Items {
			if it.State == core.CardStateConflict {
				conflicts++
			}
			if it.Move && it.State == core.CardStatePending {
				pendingMove++
				if firstDest == "" {
					firstDest = it.DestPath
				}
			}
		}
		if pendingMove == 0 {
			fmt.Fprintln(os.Stderr, "闸门[可搬条目数]：待搬 0 条——红灯（先核对计划，不是「没事可做」）")
			return 3
		}
		if conflicts > 0 {
			fmt.Fprintln(os.Stderr, "闸门[冲突]："+strconv.Itoa(conflicts)+" 条冲突未清零——先处理再执行")
			return 3
		}
		execRoot := takeFlag(&rest, "--root")
		hub, cfg, err := openHub(dbPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		defer hub.Close()
		rootPath := execRoot
		if rootPath == "" {
			for _, e := range cfg.CardRoots {
				p := strings.TrimRight(e.Path, "\\/")
				if p != "" && firstDest != "" && strings.HasPrefix(strings.ToLower(firstDest), strings.ToLower(p+"\\")) {
					rootPath = p
					break
				}
			}
		}
		if rootPath == "" {
			fmt.Fprintln(os.Stderr, "闸门[索引新鲜度]：定位不到计划所属库根——用 --root <整理根> 指定")
			return 3
		}
		cards, err := core.CardsOfRootHub(hub, cfg, rootPath, "")
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		sample := len(cards)
		if sample > 200 {
			sample = 200
		}
		if sample == 0 {
			fmt.Fprintln(os.Stderr, "闸门[索引新鲜度]：库根下无卡片行（索引未导入）——先 index-import 再执行")
			return 3
		}
		stride := len(cards) / sample
		if stride < 1 {
			stride = 1
		}
		taken, missing := 0, 0
		for i := 0; i < len(cards) && taken < sample; i += stride {
			taken++
			if !pathExists(cards[i].FilePath) {
				missing++
			}
		}
		missRate := 100.0
		if taken > 0 {
			missRate = float64(missing) * 100.0 / float64(taken)
		}
		if missRate > 1.0 {
			fmt.Fprintln(os.Stderr, "闸门[索引新鲜度]：抽样 "+strconv.Itoa(taken)+" 条 · 失配 "+strconv.Itoa(missing)+
				" 条（"+fmt.Sprintf("%.2f", missRate)+"% > 1%）——索引过期，先重建索引（kkc_index.py --split --refs）")
			return 3
		}
		fmt.Println("闸门[索引新鲜度]：抽样 " + strconv.Itoa(taken) + " · 失配 " + strconv.Itoa(missing) + "（" + fmt.Sprintf("%.2f", missRate) + "%）通过")
		allMoves, err := hub.Core().LoadCardMoves("")
		if err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		moveByFrom := map[string]store.CardMoveRow{}
		for _, mv := range allMoves {
			if _, ok := moveByFrom[strings.ToLower(mv.FromPath)]; !ok {
				moveByFrom[strings.ToLower(mv.FromPath)] = mv
			}
		}
		missingMove := 0
		for _, it := range loaded.Items {
			if !it.Move || it.State != core.CardStatePending {
				continue
			}
			mv, ok := moveByFrom[strings.ToLower(it.SrcPath)]
			if !ok {
				missingMove++
				fmt.Fprintln(os.Stderr, "  无映射："+it.SrcPath)
			} else if mv.State != "pending" && mv.State != "claimed" {
				missingMove++
				fmt.Fprintln(os.Stderr, "  映射状态不可搬（"+mv.State+"）："+it.SrcPath)
			}
		}
		if missingMove > 0 {
			fmt.Fprintln(os.Stderr, "闸门[映射]："+strconv.Itoa(missingMove)+" 条待搬缺 card_move 映射（或状态已终结）——重跑 plan 落映射")
			return 3
		}
		machine, _ := os.Hostname()
		stale, _ := hub.Core().ResetStaleCardClaims(10)
		dead, _ := hub.Core().ResetDeadLocalClaims(machine)
		if stale+dead > 0 {
			fmt.Println("回收失效认领：" + strconv.Itoa(stale+dead) + " 条")
		}
		moved, skipped, failed, missed := 0, 0, 0, 0
		executor := machine + "/" + strconv.Itoa(os.Getpid())
		for i := range loaded.Items {
			it := &loaded.Items[i]
			if !it.Move || it.State != core.CardStatePending {
				continue
			}
			mv, ok := moveByFrom[strings.ToLower(it.SrcPath)]
			if !ok {
				continue
			}
			if mv.State == "done" || mv.State == "skipped" {
				it.State = core.CardStateSkipped
				it.Note = appendNoteText(it.Note, "映射已终结")
				skipped++
				continue
			}
			claimed, claimErr := hub.Core().ClaimCardMove(mv.Id, executor)
			if claimErr != nil || !claimed {
				missed++
				it.Note = appendNoteText(it.Note, "已被他人认领")
				continue
			}
			if !pathExists(it.SrcPath) {
				it.State = core.CardStateSkipped
				it.Note = appendNoteText(it.Note, "源已不在")
				hub.Core().FinishCardMove(mv.Id, "skipped", "源已不在")
				skipped++
				continue
			}
			if pathExists(it.DestPath) {
				it.State = core.CardStateConflict
				it.Note = appendNoteText(it.Note, "目标已存在同名文件")
				hub.Core().FinishCardMove(mv.Id, "skipped", "目标已存在")
				failed++
				continue
			}
			if mkErr := os.MkdirAll(filepath.Dir(it.DestPath), 0o755); mkErr != nil {
				it.State = core.CardStateFailed
				it.Note = appendNoteText(it.Note, mkErr.Error())
				hub.Core().FinishCardMove(mv.Id, "failed", mkErr.Error())
				failed++
				fmt.Fprintln(os.Stderr, "  失败："+it.SrcPath+" —— "+mkErr.Error())
				continue
			}
			if mvErr := os.Rename(it.SrcPath, it.DestPath); mvErr != nil {
				it.State = core.CardStateFailed
				it.Note = appendNoteText(it.Note, mvErr.Error())
				hub.Core().FinishCardMove(mv.Id, "failed", mvErr.Error())
				failed++
				fmt.Fprintln(os.Stderr, "  失败："+it.SrcPath+" —— "+mvErr.Error())
				continue
			}
			it.State = core.CardStateMoved
			hub.Core().FinishCardMove(mv.Id, "done", it.Note)
			moved++
		}
		if err := core.WriteCardPlan(outPath, loaded); err != nil {
			fmt.Fprintln(os.Stderr, "诊断 "+err.Error())
			return 1
		}
		fmt.Println("执行：就位 " + strconv.Itoa(moved) + " · 跳过 " + strconv.Itoa(skipped) +
			" · 失败 " + strconv.Itoa(failed) + " · 未认领 " + strconv.Itoa(missed))
		fmt.Println("状态已回写：" + outPath)
		if failed > 0 || missed > 0 {
			return 1
		}
		return 0
	}

	fmt.Fprintln(os.Stderr, "未知子命令: "+sub+"（可用：plan / show / exec / rules / moves / rollback / delete / delete-list / learn / layout / refs）")
	return 2
}

// printCardPlan 打印整理计划摘要（类型 / 状态 / 作品分布 + 待搬样例）——对齐 C# PrintCardPlan。
func printCardPlan(plan *core.CardPlan) {
	kinds := map[string]int{}
	works := map[string]int{}
	states := map[string]int{}
	var bytes int64
	for _, it := range plan.Items {
		kinds[it.Kind]++
		w := it.Work
		if w == "" {
			w = "（无）"
		}
		works[w]++
		states[it.State]++
		if it.Move {
			bytes += it.Size
		}
	}
	fmt.Println("条目 " + strconv.Itoa(len(plan.Items)) + " · 待搬字节 " + fmt.Sprintf("%.1f", float64(bytes)/1024.0/1024.0) + " MB")
	fmt.Println("类型：" + joinCounts(kinds))
	fmt.Println("状态：" + joinCounts(states))
	fmt.Println("作品数：" + strconv.Itoa(len(works)))
	shown := 0
	for _, it := range plan.Items {
		if !it.Move || it.State != core.CardStatePending {
			continue
		}
		if shown >= 5 {
			break
		}
		fmt.Println("  例：" + it.SrcPath + " → " + it.DestPath)
		shown++
	}
	if len(plan.Items) > 5 {
		fmt.Println("  …")
	}
}

// joinCounts 把「键 → 计数」按键序拼成一行。
func joinCounts(m map[string]int) string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	parts := make([]string, 0, len(keys))
	for _, k := range keys {
		parts = append(parts, k+" "+strconv.Itoa(m[k]))
	}
	return strings.Join(parts, " · ")
}

// toStoreRules core 规则 → store 规则（跨层转换）。
func toStoreRules(rules []core.CardRule) []store.CardRuleRec {
	out := make([]store.CardRuleRec, 0, len(rules))
	for _, r := range rules {
		out = append(out, store.CardRuleRec{
			Kind: core.RuleKindName(r.Kind), Match: r.Match, Value: r.Value,
			Origin: r.Origin, Votes: r.Votes, Works: r.Works,
		})
	}
	return out
}

// fromStoreRules store 规则 → core 规则（跨层转换）。
func fromStoreRules(rules []store.CardRuleRec) []core.CardRule {
	out := make([]core.CardRule, 0, len(rules))
	for _, r := range rules {
		kind := core.RuleKeyword
		switch strings.ToLower(r.Kind) {
		case "dir":
			kind = core.RuleDir
		case "author":
			kind = core.RuleAuthor
		case "chara":
			kind = core.RuleChara
		}
		out = append(out, core.CardRule{
			Kind: kind, Match: r.Match, Value: r.Value,
			Origin: r.Origin, Votes: r.Votes, Works: r.Works, Enabled: true,
		})
	}
	return out
}

// moveStamp 移动记录的排序时间戳（完成时刻优先，回落认领时刻）。
func moveStamp(mv store.CardMoveRow) string {
	if mv.ExecutedAt != "" {
		return mv.ExecutedAt
	}
	return mv.ClaimedAt
}

// appendNoteText 追加备注（分号分隔）——对齐 C# 的 note 累加口径。
func appendNoteText(note, add string) string {
	if note == "" {
		return add
	}
	return note + "；" + add
}

// defaultDataPath exe 同级 data 目录下的固定文件名。
func defaultDataPath(name string) string {
	exe, err := os.Executable()
	if err != nil {
		return filepath.Join("data", name)
	}
	return filepath.Join(filepath.Dir(exe), "data", name)
}

// pathExists 路径是否存在（文件或目录）。
func pathExists(p string) bool {
	_, err := os.Stat(p)
	return err == nil
}

// migrateCardRowPath 回滚时把卡片行从旧路径改到新路径（遍历全部库找归属）。
func migrateCardRowPath(stores []*store.DB, oldPath, newPath string) (bool, error) {
	for _, db := range stores {
		id, err := db.CardIdOf(oldPath)
		if err != nil || id <= 0 {
			continue
		}
		return db.UpdateCardPath(oldPath, newPath)
	}
	return false, nil
}

// parseIntOr 整数解析（空值或非法时用默认值）。
func parseIntOr(s string, def int) int {
	if strings.TrimSpace(s) == "" {
		return def
	}
	v, err := parseIntLoose(s)
	if err != nil {
		return def
	}
	return v
}

// nowCompactStamp 紧凑时间戳（确认删除区目录名，对齐 C# yyyyMMdd-HHmmss）。
func nowCompactStamp() string {
	return time.Now().Format("20060102-150405")
}
