// 预热语义（G10）——进程级 warm keeper：后台逐片把 -wal 建出来并保持连接，
// 扫描进行中让路。对齐 C# Data/WarmUp.cs。
package web

import (
	"fmt"
	"net/http"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"kkgo/internal/core"
	"kkgo/internal/store"
)

// warmKeeper 预热状态机 + 常驻连接表。
// state：0 未启动 / 已让路（可再触发）· 1 进行中 · 2 已完成。
type warmKeeper struct {
	mu     sync.Mutex
	state  int
	kept   map[int]*store.DB
	lastMs int64
}

// newWarmKeeper 构造预热器。
func newWarmKeeper() *warmKeeper {
	return &warmKeeper{kept: map[int]*store.DB{}}
}

// started 预热是否已在跑或已跑过（面板据此避免重复触发）。
func (k *warmKeeper) started() bool {
	k.mu.Lock()
	defer k.mu.Unlock()
	return k.state != 0
}

// start 启动后台预热（同一进程只跑一次；清单为空 / 已在跑 / 已跑过 → false 不启动）。
func (k *warmKeeper) start(corePath string, libs []int, busy func() bool) bool {
	if len(libs) == 0 {
		return false
	}
	k.mu.Lock()
	if k.state != 0 {
		k.mu.Unlock()
		return false
	}
	k.state = 1
	k.mu.Unlock()
	go k.run(corePath, libs, busy)
	return true
}

// run 后台预热主循环——逐片把 -wal 建出来并保持连接；任一片失败只出声不中断（失败必须可见）。
func (k *warmKeeper) run(corePath string, libs []int, busy func() bool) {
	dir := filepath.Dir(corePath)
	begin := time.Now()
	warmed, skipped := 0, 0
	yielded := false
	for _, lib := range libs {
		if busy != nil && busy() {
			// 扫描已开始——按现有方式跑，预热整段让路（未预热的片留给下次页面加载）
			yielded = true
			break
		}
		path := filepath.Join(dir, store.LibDbFileName(lib))
		one, err := store.OpenWarmLib(path, corePath)
		if err == nil {
			if err = one.WarmShard(); err != nil {
				one.Close()
			}
		}
		if err != nil {
			skipped++
			fmt.Println("[预热] 跳过 " + filepath.Base(path) + "：" + err.Error())
			continue
		}
		k.mu.Lock()
		if _, exists := k.kept[lib]; exists {
			one.Close()
		} else {
			k.kept[lib] = one
		}
		k.mu.Unlock()
		warmed++
	}
	elapsed := time.Since(begin).Milliseconds()
	k.mu.Lock()
	k.lastMs = elapsed
	if yielded {
		k.state = 0
	} else {
		k.state = 2
	}
	k.mu.Unlock()
	stateText := "完成"
	if yielded {
		stateText = "让路（扫描已开始）"
	}
	fmt.Println("[预热] " + stateText + "：暖 " + strconv.Itoa(warmed) + " 片 · 跳过 " + strconv.Itoa(skipped) +
		" 片 · 用时 " + strconv.FormatInt(elapsed, 10) + " ms")
}

// releaseLibs 释放某个库根的常驻预热连接（该库文件将被删除前调用——占用中的文件删不掉；只放该库，不动别的库）。
func (k *warmKeeper) releaseLibs(hub *store.Hub, rootPath string) {
	if hub == nil || strings.TrimSpace(rootPath) == "" {
		return
	}
	base, err := hub.LibOf(rootPath)
	if err != nil || base <= 0 {
		return
	}
	libs, err := hub.LibsOfBase(base)
	if err != nil {
		return
	}
	gone := make([]*store.DB, 0, len(libs))
	k.mu.Lock()
	for _, lib := range libs {
		if one, ok := k.kept[lib]; ok {
			delete(k.kept, lib)
			gone = append(gone, one)
		}
	}
	k.mu.Unlock()
	for _, one := range gone {
		one.Close()
	}
	if len(gone) > 0 {
		fmt.Println("[预热] 释放 " + rootPath + " 的常驻连接 " + strconv.Itoa(len(gone)) + " 个（库文件将被删除）")
	}
}

// closeAll 进程退出时统一关闭常驻连接。
func (k *warmKeeper) closeAll() {
	k.mu.Lock()
	all := make([]*store.DB, 0, len(k.kept))
	for _, one := range k.kept {
		all = append(all, one)
	}
	k.kept = map[int]*store.DB{}
	k.mu.Unlock()
	for _, one := range all {
		one.Close()
	}
}

// scanBusy 扫描是否在跑（预热让路判据）。
func scanBusy() bool {
	scanMu.Lock()
	defer scanMu.Unlock()
	return scanState.Running
}

// planWarmLibs 要预热的库序号清单——只列卡片侧分片库（卡片行写的就是它们；mod 数据在主库，无分片文件）。
// 对齐 C# WarmUp.PlanLibs：跳过离线 / 空路径；尚未定片数的按分片目标展开。
func planWarmLibs(hub *store.Hub, cfg *core.RootsConfig) []int {
	list := make([]int, 0, 32)
	if hub == nil || cfg == nil {
		return list
	}
	target := hub.ShardTarget()
	for _, e := range cfg.CardRoots {
		if strings.TrimSpace(e.Path) == "" || e.Offline {
			continue
		}
		base, err := hub.CardBaseOf(e.Path)
		if err != nil || base <= 0 {
			continue
		}
		shards, err := hub.ShardCountOf(base)
		if err != nil {
			continue
		}
		if shards <= 0 {
			shards = target
		}
		if shards < 1 {
			shards = 1
		}
		for k := 0; k < shards; k++ {
			list = append(list, store.LibOfShard(base, k))
		}
	}
	return list
}

// handleWarmup 静默预热分片库——只算清单并启动后台任务，立即返回（前端 fire-and-forget）。
func (a *App) handleWarmup(w http.ResponseWriter, r *http.Request) {
	if scanBusy() {
		writeJSON(w, map[string]interface{}{"ok": true, "started": false, "reason": "scan-running"})
		return
	}
	hub, cfg, err := openApp(a.DBPath)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer hub.Close()
	libs := planWarmLibs(hub, cfg)
	started := a.warm.start(hub.Core().Path(), libs, scanBusy)
	writeJSON(w, map[string]interface{}{"ok": true, "started": started, "files": len(libs)})
}
