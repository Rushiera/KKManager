// Hub 多库协调器——主库 + 各库根对应的库文件（对齐 C# Data/StoreHub.cs 的读面部分）。
//
// 库序号是复合号：库根序号 × LibShardStride + 分片序号（C# RootsRules）。
// 主库设置表键：libdb:<库根路径> = 复合首片号；libshards:<复合首号> = 该库根分片数。
// 🔴 真相源是这两个设置键——分片库文件按它们推导，不靠目录枚举（C# 亦如此）。
package store

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// LibShardStride 库序号的进位基数——库序号 = 库根序号 × 本值 + 分片序号。
const LibShardStride = 1000

// DefaultShardTarget 分片数目标的默认值（每片一个库文件）。
const DefaultShardTarget = 32

// BaseOfLib 取库序号里的库根序号。
func BaseOfLib(lib int) int { return lib / LibShardStride }

// ShardOfLib 取库序号里的分片序号。
func ShardOfLib(lib int) int { return lib % LibShardStride }

// LibOfShard 把库根序号与分片序号合成库序号（分片 0 = 首片）。
func LibOfShard(baseLib, shard int) int { return baseLib*LibShardStride + shard }

// LibDbFileName 库文件序号对应的文件名——主库目录下 lib_<库根序号>_<分片序号>.db。
func LibDbFileName(lib int) string {
	return fmt.Sprintf("lib_%d_%d.db", BaseOfLib(lib), ShardOfLib(lib))
}

// Hub 主库 + 已打开的库文件（懒开并缓存）。所有库文件与主库同目录。
type Hub struct {
	core    *DB
	dir     string
	libs    map[int]*DB
	libOf   map[string]int
	nextLib int
}

// OpenHub 打开主库并载入库序号映射（libdb: 键；含旧编号迁移，行为对齐 C# LoadLibMap）。
func OpenHub(corePath string) (*Hub, error) {
	core, err := Open(corePath)
	if err != nil {
		return nil, err
	}
	dir := filepath.Dir(core.Path())
	h := &Hub{core: core, dir: dir, libs: make(map[int]*DB), libOf: make(map[string]int), nextLib: 1}
	if err = h.loadLibMap(); err != nil {
		core.Close()
		return nil, err
	}
	return h, nil
}

// Core 主库句柄（设置表 / mod 主表 / 预置库根数据）。
func (h *Hub) Core() *DB { return h.core }

// Dir 数据目录（主库所在目录）。
func (h *Hub) Dir() string { return h.dir }

// Close 关闭全部已打开的库文件与主库。
func (h *Hub) Close() error {
	var first error
	for _, db := range h.libs {
		if err := db.Close(); err != nil && first == nil {
			first = err
		}
	}
	if err := h.core.Close(); err != nil && first == nil {
		first = err
	}
	return first
}

// loadLibMap 载入库序号映射（键前缀 libdb:）——含旧编号迁移：
// 旧编号 N → 复合号 N×1000，库文件 lib_N.db → lib_N_0.db（边车文件一并搬）。
func (h *Hub) loadLibMap() error {
	all, err := h.core.LoadSettings()
	if err != nil {
		return err
	}
	for key, value := range all {
		if len(key) < 6 || !strings.EqualFold(key[:6], "libdb:") {
			continue
		}
		n, convErr := strconv.Atoi(strings.TrimSpace(value))
		if convErr != nil || n <= 0 {
			continue
		}
		rootPath := key[6:]
		if n < LibShardStride {
			oldFile := filepath.Join(h.dir, "lib_"+strconv.Itoa(n)+".db")
			moved := LibOfShard(n, 0)
			newFile := filepath.Join(h.dir, LibDbFileName(moved))
			if fileExists(oldFile) && !fileExists(newFile) {
				if moveErr := os.Rename(oldFile, newFile); moveErr != nil {
					fmt.Fprintln(os.Stderr, "[库数据] 编号迁移失败（"+filepath.Base(oldFile)+"）："+moveErr.Error()+"——该库需重扫")
				} else {
					moveSidecar(oldFile+"-wal", newFile+"-wal")
					moveSidecar(oldFile+"-shm", newFile+"-shm")
					fmt.Println("[库数据] 编号迁移：" + filepath.Base(oldFile) + " → " + filepath.Base(newFile))
				}
			}
			n = moved
			if err = h.core.SetSetting(key, strconv.Itoa(n)); err != nil {
				return err
			}
		}
		h.libOf[rootPath] = n
		baseNo := BaseOfLib(n) + 1
		if baseNo >= h.nextLib {
			h.nextLib = baseNo + 1
		}
	}
	return nil
}

// LibOf 取某个库根的库序号（没有则分配并落盘）——返回复合号的**首片**（库根序号 × 进位基数）。
func (h *Hub) LibOf(rootPath string) (int, error) {
	key := strings.ToLower(strings.TrimSpace(rootPath))
	if key == "" {
		return 0, nil
	}
	if n, ok := h.libOf[key]; ok {
		return n, nil
	}
	n := LibOfShard(h.nextLib, 0)
	h.nextLib++
	h.libOf[key] = n
	if err := h.core.SetSetting("libdb:"+key, strconv.Itoa(n)); err != nil {
		return 0, err
	}
	return n, nil
}

// StoreByLib 按库序号取库文件（0 = 主库；懒开并缓存）。
// 文件不存在 = 该库尚未扫描（读面按空库处理，不创建文件——与写路径的模板释放区分）。
func (h *Hub) StoreByLib(lib int) (*DB, error) {
	if lib <= 0 {
		return h.core, nil
	}
	if s, ok := h.libs[lib]; ok {
		return s, nil
	}
	path := filepath.Join(h.dir, LibDbFileName(lib))
	if !fileExists(path) {
		return nil, fmt.Errorf("库文件不存在：%s", LibDbFileName(lib))
	}
	s, err := Open(path)
	if err != nil {
		return nil, err
	}
	h.libs[lib] = s
	return s, nil
}

// ShardCountOf 某个库根的分片数（读 libshards:<复合首号>；无记录返回 0 = 尚未定）。
func (h *Hub) ShardCountOf(baseLib int) (int, error) {
	if baseLib <= 0 {
		return 0, nil
	}
	text, _, err := h.core.GetSetting("libshards:" + strconv.Itoa(baseLib))
	if err != nil {
		return 0, err
	}
	return parseCount(text), nil
}

// SetShardCount 写入某个库根的分片数（分片数变更需整库重扫）。
func (h *Hub) SetShardCount(baseLib, shards int) error {
	if baseLib <= 0 {
		return nil
	}
	if shards <= 0 {
		shards = 1
	}
	return h.core.SetSetting("libshards:"+strconv.Itoa(baseLib), strconv.Itoa(shards))
}

// LibsOfBase 某个库根的分片号集合（按分片序号升序；尚未定片数时按 1 片）。
func (h *Hub) LibsOfBase(baseLib int) ([]int, error) {
	if baseLib <= 0 {
		return nil, nil
	}
	n, err := h.ShardCountOf(baseLib)
	if err != nil {
		return nil, err
	}
	if n <= 0 {
		n = 1
	}
	list := make([]int, 0, n)
	for k := 0; k < n; k++ {
		list = append(list, LibOfShard(baseLib, k))
	}
	return list, nil
}

// parseCount 解析一个计数字符串（空 / 非法 / 非正一律 0）。
func parseCount(text string) int {
	n, err := strconv.Atoi(strings.TrimSpace(text))
	if err != nil {
		return 0
	}
	return n
}

// fileExists 文件是否存在。
func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// moveSidecar 随主库文件一起搬动边车文件（存在才动；失败出声不静默）。
func moveSidecar(from, to string) {
	if !fileExists(from) || fileExists(to) {
		return
	}
	if err := os.Rename(from, to); err != nil {
		fmt.Fprintln(os.Stderr, "[库数据] 边车文件迁移失败（"+from+"）："+err.Error())
	}
}
