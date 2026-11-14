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
	"unicode/utf16"
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
// 文件不存在 = 该库尚未扫描（读面按空库处理，不创建文件——与写路径的 StoreFor 区分）。
// 库文件连接一律 ATTACH 主库（四色聚合 / mod 主表都读主库）。
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
	s, err := openLib(path, h.core.Path())
	if err != nil {
		return nil, err
	}
	h.libs[lib] = s
	return s, nil
}

// openLibFile 打开（或复用）一个库文件——缺文件从内嵌模板释放（写面语义；读面用 StoreByLib 的缺文件报错）。
func (h *Hub) openLibFile(lib int) (*DB, error) {
	if lib <= 0 {
		return h.core, nil
	}
	if s, ok := h.libs[lib]; ok {
		return s, nil
	}
	path := filepath.Join(h.dir, LibDbFileName(lib))
	s, err := openLib(path, h.core.Path())
	if err != nil {
		return nil, err
	}
	h.libs[lib] = s
	return s, nil
}

// UsesCoreDB 该库根的数据是否落主库文件（对齐 C# RootsRules.UsesCoreDb）：
// mod 侧预置条目（锁定主库 / 缓存库槽位）走主库；卡片侧一律自有库文件。
func UsesCoreDB(tier int, locked bool, isMods bool) bool {
	if !isMods {
		return false
	}
	return locked || tier == TierCacheValue
}

// TierCacheValue 缓存库级别值（store 包不依赖 core，避免循环引用）。
const TierCacheValue = 2

// StoreFor 取某条库根对应的 Store——走主库的条目返回主库，其余各自一个库文件（缺文件从模板释放）。
func (h *Hub) StoreFor(tier int, locked bool, rootPath string, isMods bool) (*DB, error) {
	if UsesCoreDB(tier, locked, isMods) {
		return h.core, nil
	}
	lib, err := h.LibOf(rootPath)
	if err != nil {
		return nil, err
	}
	return h.openLibFile(lib)
}

// LibOfEntry 取某条库根的库序号（走主库的条目返回 0）。
func (h *Hub) LibOfEntry(tier int, locked bool, rootPath string, isMods bool) (int, error) {
	if UsesCoreDB(tier, locked, isMods) {
		return 0, nil
	}
	return h.LibOf(rootPath)
}

// LibRoot 库根在库文件布局里的一行（扫描器 / 读面共用——避免 store 依赖 core 的 RootEntry）。
type LibRoot struct {
	// Path 目录绝对路径。
	Path string
	// Tier 级别：1=主库 · 2=缓存库 · 3=冷冻库。
	Tier int
	// Locked 是否预置锁定条目。
	Locked bool
}

// ShardTarget 分片数目标（库设置 scan_shards → 环境变量 KKM_SCAN_SHARDS；无记录按默认 32）——与并发度解耦。
func (h *Hub) ShardTarget() int {
	if n := parseCount(os.Getenv("KKM_SCAN_SHARDS")); n > 0 {
		return n
	}
	if text, _, err := h.core.GetSetting("scan_shards"); err == nil {
		if n := parseCount(text); n > 0 {
			return n
		}
	}
	return DefaultShardTarget
}

// missingLibFiles 列出缺失的库文件路径——卡片侧按分片数展开（未定则按分片目标定下并落盘），mod 侧单片。
func (h *Hub) missingLibFiles(roots []LibRoot, isMods bool) ([]string, error) {
	list := make([]string, 0, len(roots))
	for _, root := range roots {
		if UsesCoreDB(root.Tier, root.Locked, isMods) {
			continue
		}
		base, err := h.LibOf(root.Path)
		if err != nil {
			return nil, err
		}
		if isMods {
			path := filepath.Join(h.dir, LibDbFileName(base))
			if !fileExists(path) {
				list = append(list, path)
			}
			continue
		}
		shards, err := h.ShardCountOf(base)
		if err != nil {
			return nil, err
		}
		if shards <= 0 {
			shards = h.ShardTarget()
			if shards <= 0 {
				shards = 1
			}
			if err = h.SetShardCount(base, shards); err != nil {
				return nil, err
			}
		}
		for k := 0; k < shards; k++ {
			path := filepath.Join(h.dir, LibDbFileName(LibOfShard(base, k)))
			if !fileExists(path) {
				list = append(list, path)
			}
		}
	}
	return list, nil
}

// EnsureDatabases 预建全部库文件（首次打开一次性备齐）——第一个从内嵌模板释放一次，其余直接文件复制
// （全程不开数据库连接、不跑 DDL）。返回新建文件数。对齐 C# StoreHub.EnsureDatabases。
func (h *Hub) EnsureDatabases(mods, cards []LibRoot) (int, error) {
	missing, err := h.missingLibFiles(mods, true)
	if err != nil {
		return 0, err
	}
	more, err := h.missingLibFiles(cards, false)
	if err != nil {
		return 0, err
	}
	missing = append(missing, more...)
	if len(missing) == 0 {
		return 0, nil
	}
	if !ReleaseTemplate(missing[0]) {
		return 0, nil
	}
	built := 1
	for i := 1; i < len(missing); i++ {
		raw, readErr := os.ReadFile(missing[0])
		if readErr != nil {
			return built, readErr
		}
		if writeErr := os.WriteFile(missing[i], raw, 0o644); writeErr != nil {
			return built, writeErr
		}
		built++
	}
	fmt.Println("[库数据] 预建库文件 " + itoaInt(built) + " 个")
	return built, nil
}

// EnsureMigrated 全库扫描前按库根配置准备库文件——含首次迁移（把主库里附加库 / 冷藏库的 mod 数据搬进各自库文件）。
// 对齐 C# StoreHub.EnsureMigrated：dbSplit 标记已置则只预建；未置则先迁移再置标记。
func (h *Hub) EnsureMigrated(mods, cards []LibRoot) error {
	flag, _, err := h.core.GetSetting("dbSplit")
	if err != nil {
		return err
	}
	if flag != "1" {
		if err = h.migrateRoots(mods, true); err != nil {
			return err
		}
		if err = h.migrateRoots(cards, false); err != nil {
			return err
		}
		if err = h.core.SetSetting("dbSplit", "1"); err != nil {
			return err
		}
		fmt.Println("[迁移] 按库拆分完成——各库根已独立为库文件")
	}
	_, err = h.EnsureDatabases(mods, cards)
	return err
}

// migrateRoots 把一组库根的数据从主库搬进各自的库文件（mod_file / card / card_mod 三表）。
func (h *Hub) migrateRoots(roots []LibRoot, isMods bool) error {
	for _, root := range roots {
		if UsesCoreDB(root.Tier, root.Locked, isMods) {
			continue
		}
		base, err := h.LibOf(root.Path)
		if err != nil {
			return err
		}
		target := base
		if !isMods {
			target = LibOfShard(base, 0)
		}
		store, err := h.openLibFile(target)
		if err != nil {
			return err
		}
		if err = store.adoptRootFromCore(root.Path); err != nil {
			return err
		}
		fmt.Println("[迁移] " + root.Path + " → " + filepath.Base(store.Path()))
	}
	return nil
}

// adoptRootFromCore 把主库里某个库根的数据整段搬进本库文件，再从主库删掉这些行（存量迁移用）——
// 本库连接必须 ATTACH 了主库。对齐 C# Store.AdoptRootFromCore（mod 侧 + 卡片侧三表）。
func (d *DB) adoptRootFromCore(rootPath string) error {
	if d.isCore {
		return nil
	}
	stmts := []string{
		`INSERT OR IGNORE INTO card(id,file_path,file_name,size,mtime,tier,root_path,folder,
			 card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error)
		 SELECT id,file_path,file_name,size,mtime,tier,root_path,folder,
			 card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error
		 FROM core.card WHERE root_path=?`,
		`INSERT OR IGNORE INTO card_mod(card_id,mod_guid,property,slot,local_slot,category_no,rec_name,rec_author,rec_website)
		 SELECT cm.card_id,cm.mod_guid,cm.property,cm.slot,cm.local_slot,cm.category_no,cm.rec_name,cm.rec_author,cm.rec_website
		 FROM core.card_mod cm JOIN core.card c ON c.id = cm.card_id WHERE c.root_path=?`,
		`INSERT OR IGNORE INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
		 SELECT file_path,guid,tier,root_path,file_name,size,mtime,scan_time
		 FROM core.mod_file WHERE root_path=?`,
		"DELETE FROM core.card_mod WHERE card_id IN (SELECT id FROM core.card WHERE root_path=?)",
		"DELETE FROM core.card WHERE root_path=?",
		"DELETE FROM core.mod_file WHERE root_path=?",
	}
	for _, stmt := range stmts {
		if err := d.execWithPath(stmt, rootPath); err != nil {
			return err
		}
	}
	return nil
}

// execWithPath 带单个路径参数的写语句。
func (d *DB) execWithPath(sqlText, path string) error {
	_, err := d.sql.Exec(sqlText, path)
	return err
}

// AllStores 全部参与合并的库（主库 + 各库根库文件，去重）——mod 侧单片；卡片侧按分片数展开。
// 缺片按空库跳过并出声（读面不创建文件）。对齐 C# StoreHub.AllStores。
func (h *Hub) AllStores(mods, cards []LibRoot) []*DB {
	list := []*DB{h.core}
	seen := map[int]bool{}
	add := func(lib int) {
		if lib <= 0 || seen[lib] {
			return
		}
		db, err := h.StoreByLib(lib)
		if err != nil {
			fmt.Fprintln(os.Stderr, "  [库] 跳过缺失库文件："+err.Error())
			return
		}
		seen[lib] = true
		list = append(list, db)
	}
	for _, e := range mods {
		lib, err := h.LibOfEntry(e.Tier, e.Locked, e.Path, true)
		if err != nil {
			continue
		}
		add(lib)
	}
	for _, e := range cards {
		lib, err := h.LibOfEntry(e.Tier, e.Locked, e.Path, false)
		if err != nil || lib <= 0 {
			continue
		}
		shards, err := h.ShardCountOf(lib)
		if err != nil {
			continue
		}
		if shards <= 0 {
			shards = 1
		}
		for k := 0; k < shards; k++ {
			add(LibOfShard(lib, k))
		}
	}
	return list
}

// RecomputeMod 跨库重算某个 guid 的 mod 主表（副本被删 / 搬移后调用）——对齐 C# StoreHub.RecomputeMod。
func (h *Hub) RecomputeMod(stores []*DB, guid string) error {
	if guid == "" {
		return nil
	}
	var best *ModFileRecord
	var count int64
	for _, s := range stores {
		rows, err := s.QueryModFileRecords(guid)
		if err != nil {
			return err
		}
		for i := range rows {
			count++
			if best == nil || rows[i].Tier < best.Tier {
				best = &rows[i]
			}
		}
	}
	if best == nil {
		return h.core.DeleteModRow(guid)
	}
	return h.core.WriteModRow(guid, best)
}

// itoaInt 十进制整数转文本（避免为一行输出引入 strconv 依赖以外的分支）。
func itoaInt(v int) string {
	return fmt.Sprintf("%d", v)
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

// CardBaseOf 某个卡片库根对应的库根序号（卡片侧一律自有库文件，懒分配并落盘）。
func (h *Hub) CardBaseOf(rootPath string) (int, error) { return h.LibOf(rootPath) }

// ShardsOf 某个库根的全部分片库（缺片从模板释放）——写面用（读面走 StoreByLib，缺片不创建）。
func (h *Hub) ShardsOf(baseLib, shards int) ([]*DB, error) {
	if shards <= 0 {
		shards = 1
	}
	list := make([]*DB, 0, shards)
	for k := 0; k < shards; k++ {
		db, err := h.openLibFile(LibOfShard(baseLib, k))
		if err != nil {
			return nil, err
		}
		list = append(list, db)
	}
	return list, nil
}

// ShardOfPath 把一个文件路径归到某个库根的分片号——FNV-1a 哈希取模；同一文件恒落同一分片。
// 对齐 C# StoreHub.ShardOfPath：大小写不敏感归一后按 UTF-16 码元逐位计算（与 C# 的 char 循环同构）。
func ShardOfPath(filePath string, shards int) int {
	if shards <= 1 || filePath == "" {
		return 0
	}
	const offset32 uint32 = 2166136261
	const prime32 uint32 = 16777619
	var hash uint32 = offset32
	for _, unit := range utf16.Encode([]rune(strings.ToLower(filePath))) {
		hash = (hash ^ uint32(unit)) * prime32
	}
	return int(hash % uint32(shards))
}
