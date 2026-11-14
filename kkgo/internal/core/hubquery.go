// 跨库查询编排——把 store 的单库查询按库根组合合并、排序、分页。
//
// 契约对齐 C# Data/StoreHub.cs 的对应方法（AllStores / QueryCards / QueryFolders /
// QueryMods / QueryAuthors / ListNonCards / ListDuplicateGroups / DupCounts /
// QueryCardsByMod / QueryModFiles / QueryDupCardRefs）。
// 分层：store 不依赖 core；编排归本包（core 可 import store）。
package core

import (
	"sort"
	"strings"

	"kkgo/internal/store"
)

// StoreRef 一个参与合并的库句柄 + 它的库序号（0 = 主库）。
type StoreRef struct {
	// Lib 库序号（0 = 主库）。
	Lib int
	// DB 库句柄。
	DB *store.DB
}

// AllStoreRefs 全部参与合并的库（主库 + 各库根库文件，去重）——mod 侧单片；卡片侧按分片数展开。
// 缺片按空库跳过（读面不创建文件；跳过时由 store 层出声）。
func AllStoreRefs(hub *store.Hub, cfg *RootsConfig) []StoreRef {
	list := []StoreRef{{Lib: 0, DB: hub.Core()}}
	if cfg == nil || hub == nil {
		return list
	}
	seen := map[int]bool{}
	add := func(lib int) {
		if lib <= 0 || seen[lib] {
			return
		}
		db, err := hub.StoreByLib(lib)
		if err != nil {
			return
		}
		seen[lib] = true
		list = append(list, StoreRef{Lib: lib, DB: db})
	}
	for _, e := range cfg.ModRoots {
		lib, err := hub.LibOfEntry(e.Tier, e.Locked, e.Path, true)
		if err != nil {
			continue
		}
		add(lib)
	}
	for _, e := range cfg.CardRoots {
		lib, err := hub.LibOfEntry(e.Tier, e.Locked, e.Path, false)
		if err != nil || lib <= 0 {
			continue
		}
		shards, err := hub.ShardCountOf(lib)
		if err != nil {
			continue
		}
		if shards <= 0 {
			shards = 1
		}
		for k := 0; k < shards; k++ {
			add(store.LibOfShard(lib, k))
		}
	}
	return list
}

// CardRootLib 某个卡片库根路径对应的库序号首片（找不到返回 0）。
func CardRootLib(hub *store.Hub, cfg *RootsConfig, root string) int {
	if cfg == nil || hub == nil || strings.TrimSpace(root) == "" {
		return 0
	}
	want := strings.ToLower(strings.TrimRight(strings.TrimSpace(root), `\/`))
	for _, e := range cfg.CardRoots {
		got := strings.ToLower(strings.TrimRight(strings.TrimSpace(e.Path), `\/`))
		if got != want {
			continue
		}
		lib, err := hub.LibOfEntry(e.Tier, e.Locked, e.Path, false)
		if err != nil {
			return 0
		}
		return lib
	}
	return 0
}

// QueryCardsHub 卡片列表（跨库合并后排序分页）——对齐 C# StoreHub.QueryCards。
// folder 传 nil = 不按文件夹筛；root 非空 = 只查该库根的全部分片。
func QueryCardsHub(hub *store.Hub, cfg *RootsConfig, page, size int, filter, q string, folder *string, root, order string, desc bool) ([]store.CardListRow, error) {
	if root != "" {
		baseLib := CardRootLib(hub, cfg, root)
		var one []store.CardListRow
		shards := 1
		if baseLib > 0 {
			if n, err := hub.ShardCountOf(baseLib); err == nil && n > 0 {
				shards = n
			}
		}
		for k := 0; k < shards; k++ {
			lib := store.LibOfShard(baseLib, k)
			db, err := hub.StoreByLib(lib)
			if err != nil {
				continue
			}
			rows, err := db.QueryCardsRows(1, 0, filter, q, folder, root, order, desc)
			if err != nil {
				return nil, err
			}
			for i := range rows {
				rows[i].Lib = lib
			}
			one = append(one, rows...)
		}
		store.SortCardRows(one, order, desc)
		return store.SliceRows(one, page, size), nil
	}
	var list []store.CardListRow
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.QueryCardsRows(1, 0, filter, q, folder, "", order, desc)
		if err != nil {
			return nil, err
		}
		for i := range rows {
			rows[i].Lib = s.Lib
		}
		list = append(list, rows...)
	}
	store.SortCardRows(list, order, desc)
	return store.SliceRows(list, page, size), nil
}

// QueryFoldersHub 卡片文件夹清单（跨库拼接，同库根的多分片合并计数）——对齐 C# StoreHub.QueryFolders。
func QueryFoldersHub(hub *store.Hub, cfg *RootsConfig) ([]store.FolderRow, error) {
	type key struct{ root, folder string }
	agg := map[key]*store.FolderRow{}
	order := make([]key, 0, 16)
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.QueryFoldersRows()
		if err != nil {
			return nil, err
		}
		for _, one := range rows {
			k := key{strings.ToLower(one.RootPath), strings.ToLower(one.Folder)}
			cur, ok := agg[k]
			if !ok {
				copied := one
				agg[k] = &copied
				order = append(order, k)
				continue
			}
			cur.Count += one.Count
			cur.Ready += one.Ready
			cur.Black += one.Black
		}
	}
	list := make([]store.FolderRow, 0, len(order))
	for _, k := range order {
		list = append(list, *agg[k])
	}
	sort.SliceStable(list, func(i, j int) bool {
		if c := strings.Compare(strings.ToLower(list[i].RootPath), strings.ToLower(list[j].RootPath)); c != 0 {
			return c < 0
		}
		return strings.Compare(strings.ToLower(list[i].Folder), strings.ToLower(list[j].Folder)) < 0
	})
	return list, nil
}

// QueryModsHub mod 列表——mod 行取主库（全局表），被引用卡数跨库汇总后排序分页。
// 对齐 C# StoreHub.QueryMods（含 MatchModFilter / CompareMods）。
func QueryModsHub(hub *store.Hub, cfg *RootsConfig, page, size int, filter, q string) ([]store.ModListRow, error) {
	rows, err := hub.Core().QueryModsRows(1, 0, "all", q)
	if err != nil {
		return nil, err
	}
	used := map[string]store.RefCount{}
	for _, s := range AllStoreRefs(hub, cfg) {
		counts, err := s.DB.RefCountsByType()
		if err != nil {
			return nil, err
		}
		for guid, v := range counts {
			cur := used[guid]
			cur.Used += v.Used
			cur.Chara += v.Chara
			cur.Clothes += v.Clothes
			cur.Sd += v.Sd
			used[guid] = cur
		}
	}
	olds := hub.Core().LoadModOldMap()
	list := make([]store.ModListRow, 0, len(rows))
	for _, m := range rows {
		if v, ok := used[m.Guid]; ok {
			m.Used, m.UsedChara, m.UsedClothes, m.UsedSd = v.Used, v.Chara, v.Clothes, v.Sd
		} else {
			m.Used, m.UsedChara, m.UsedClothes, m.UsedSd = 0, 0, 0, 0
		}
		if rec, ok := olds[m.Guid]; ok {
			m.HasOld = true
			m.OldName = rec.OldName
		}
		if !matchModFilter(m, filter) {
			continue
		}
		list = append(list, m)
	}
	sort.SliceStable(list, func(i, j int) bool { return store.CompareModRows(&list[i], &list[j]) < 0 })
	return store.SliceRows(list, page, size), nil
}

// matchModFilter mod 列表筛选（用跨库汇总后的被引用卡数判定）——对齐 C# StoreHub.MatchModFilter。
func matchModFilter(m store.ModListRow, filter string) bool {
	switch filter {
	case "used":
		return m.Used > 0
	case "unused":
		return m.Used == 0 && m.Tier == TierMain
	case "tier1":
		return m.Tier == TierMain
	case "tier2":
		return m.Tier == TierCache
	case "tier3":
		return m.Tier == TierCold
	case "dup":
		return m.DupCount > 1
	default:
		return true
	}
}

// QueryAuthorsHub 作者清单（主库聚合表）——聚合表为空而 mod 主表非空时自愈重建一次。
// 对齐 C# StoreHub.QueryAuthors。
func QueryAuthorsHub(hub *store.Hub) ([]store.AuthorRow, error) {
	list, err := hub.Core().QueryAuthorsRows()
	if err != nil {
		return nil, err
	}
	if len(list) == 0 {
		snap, snapErr := hub.SnapshotOf(hub.Core())
		if snapErr == nil && snap.Mods > 0 {
			println("[作者索引] 聚合表为空而 mod 主表非空 → 重建一次")
			if _, err = hub.Core().RefreshModAuthors(); err != nil {
				return nil, err
			}
			list, err = hub.Core().QueryAuthorsRows()
			if err != nil {
				return nil, err
			}
		}
	}
	return list, nil
}

// ListNonCardsHub 非卡 / 非 mod 清单（跨库合并，按字节数降序）——对齐 C# StoreHub.ListNonCards。
func ListNonCardsHub(hub *store.Hub, cfg *RootsConfig, includeMoved bool) ([]store.NonCardRow, error) {
	var list []store.NonCardRow
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.ListNonCardRows(includeMoved)
		if err != nil {
			return nil, err
		}
		for i := range rows {
			rows[i].Lib = s.Lib
		}
		list = append(list, rows...)
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].Size > list[j].Size })
	return list, nil
}

// CountNonCardsHub 非卡 / 非 mod 条数（跨库合计）——对齐 C# StoreHub.CountNonCards。
func CountNonCardsHub(hub *store.Hub, cfg *RootsConfig, includeMoved bool) (int64, error) {
	var n int64
	for _, s := range AllStoreRefs(hub, cfg) {
		one, err := s.DB.CountNonCardRows(includeMoved)
		if err != nil {
			return n, err
		}
		n += one
	}
	return n, nil
}

// QueryCardsByModHub 某 guid 被哪些卡片引用（跨库合并）——对齐 C# StoreHub.QueryCardsByMod。
func QueryCardsByModHub(hub *store.Hub, cfg *RootsConfig, guid string, page, size int) ([]store.CardListRow, error) {
	var list []store.CardListRow
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.QueryCardsByModRows(guid, 1, 0)
		if err != nil {
			return nil, err
		}
		for i := range rows {
			rows[i].Lib = s.Lib
		}
		list = append(list, rows...)
	}
	sort.SliceStable(list, func(i, j int) bool { return store.CompareCardsByMod(&list[i], &list[j]) < 0 })
	return store.SliceRows(list, page, size), nil
}

// CountCardsByModHub 某 guid 被引用的卡片总数（跨库）——对齐 C# StoreHub.CountCardsByMod。
func CountCardsByModHub(hub *store.Hub, cfg *RootsConfig, guid string) (int64, error) {
	var n int64
	for _, s := range AllStoreRefs(hub, cfg) {
		one, err := s.DB.CountCardsByModRows(guid)
		if err != nil {
			return n, err
		}
		n += one
	}
	return n, nil
}

// QueryModFilesHub 某 guid 的全部文件副本（跨库，按级别排序）——对齐 C# StoreHub.QueryModFiles。
func QueryModFilesHub(hub *store.Hub, cfg *RootsConfig, guid string) ([]store.ModFileRec, error) {
	var list []store.ModFileRec
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.QueryModFileRecsByGuid(guid)
		if err != nil {
			return nil, err
		}
		list = append(list, rows...)
	}
	sort.SliceStable(list, func(i, j int) bool { return store.CompareRefRows(&list[i], &list[j]) < 0 })
	return list, nil
}

// ListDuplicateGroupsHub 重复副本组（跨库按 guid 分组，组内按级别排序，组间按 guid）——
// 对齐 C# StoreHub.ListDuplicateGroups。
func ListDuplicateGroupsHub(hub *store.Hub, cfg *RootsConfig) ([]store.DupGroup, error) {
	groups := map[string]*store.DupGroup{}
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.ListModFileRecs()
		if err != nil {
			return nil, err
		}
		for _, r := range rows {
			if r.Guid == "" {
				continue
			}
			g, ok := groups[r.Guid]
			if !ok {
				g = &store.DupGroup{Guid: r.Guid}
				groups[r.Guid] = g
			}
			g.Files = append(g.Files, r)
			if r.Tier == TierMain {
				g.MainCount++
			}
		}
	}
	list := make([]store.DupGroup, 0, len(groups))
	for _, g := range groups {
		if len(g.Files) <= 1 {
			continue
		}
		sort.SliceStable(g.Files, func(i, j int) bool { return store.CompareRefRows(&g.Files[i], &g.Files[j]) < 0 })
		list = append(list, *g)
	}
	sort.SliceStable(list, func(i, j int) bool { return list[i].Guid < list[j].Guid })
	return list, nil
}

// DupCountsHub 重复副本组数与「待确认」组数（跨库合并）——待确认 = 组内非旧版副本 ≥ 2。
// 对齐 C# StoreHub.DupCounts。
func DupCountsHub(hub *store.Hub, cfg *RootsConfig) (int64, int64, error) {
	type pair struct{ total, live int64 }
	agg := map[string]*pair{}
	for _, s := range AllStoreRefs(hub, cfg) {
		rows, err := s.DB.ListModFileCountRows()
		if err != nil {
			return 0, 0, err
		}
		for _, c := range rows {
			p, ok := agg[c.Guid]
			if !ok {
				p = &pair{}
				agg[c.Guid] = p
			}
			p.total += c.Total
			p.live += c.Live
		}
	}
	var groups, pending int64
	for _, p := range agg {
		if p.total <= 1 {
			continue
		}
		groups++
		if p.live >= 2 {
			pending++
		}
	}
	return groups, pending, nil
}

// QueryDupCardRefsHub 重复副本组的引用卡片（每组总数 + 前 top 张）——对齐 C# StoreHub.QueryDupCardRefs。
func QueryDupCardRefsHub(hub *store.Hub, cfg *RootsConfig, guids []string, top int) (map[string]*store.DupCardRefs, error) {
	out := make(map[string]*store.DupCardRefs)
	if len(guids) == 0 {
		return out, nil
	}
	for _, g := range guids {
		if _, ok := out[g]; !ok {
			out[g] = &store.DupCardRefs{}
		}
	}
	for _, s := range AllStoreRefs(hub, cfg) {
		counts, err := s.DB.CountCardsByModsBatch(guids)
		if err != nil {
			return nil, err
		}
		for guid, n := range counts {
			if hit, ok := out[guid]; ok {
				hit.Total += n
			}
		}
		rows, err := s.DB.RefCardsByModsBatch(guids)
		if err != nil {
			return nil, err
		}
		for _, row := range rows {
			hit, ok := out[row.Guid]
			if !ok {
				continue
			}
			var cr store.CardListRow
			cr.Id = row.Id
			cr.FileName = row.FileName
			cr.RootPath, cr.RootPathSet = row.RootPath, row.RootPathSet
			cr.Folder = row.Folder
			cr.HasThumb = row.HasThumb
			cr.CardType, cr.CardTypeSet = row.CardType, row.CardTypeSet
			cr.Lib = s.Lib
			hit.Top = append(hit.Top, cr)
		}
	}
	for _, v := range out {
		sort.SliceStable(v.Top, func(i, j int) bool { return store.CompareCardsByMod(&v.Top[i], &v.Top[j]) < 0 })
		if top > 0 && len(v.Top) > top {
			v.Top = v.Top[:top]
		}
	}
	return out, nil
}

// QueryIndexMetaHub 跨库查索引元数据——rootPath 非空时按路径前缀过滤；结果按 limit 截断。
// 对齐 C# StoreHub.QueryIndexMeta。
func QueryIndexMetaHub(hub *store.Hub, cfg *RootsConfig, rootPath, kind, work, author, keyword, series, vn, charaID string, limit int) ([]store.IdxMetaRow, error) {
	want := limit
	if want <= 0 {
		want = 100
	}
	prefix := strings.ToLower(strings.TrimRight(strings.TrimSpace(rootPath), `\/`))
	list := make([]store.IdxMetaRow, 0, want)
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryIdxRows(kind, work, author, keyword, series, vn, charaID, want)
		if err != nil {
			return nil, err
		}
		for _, row := range rows {
			if prefix == "" || strings.HasPrefix(strings.ToLower(row.FilePath), prefix) {
				list = append(list, row)
			}
		}
		if len(list) > want {
			list = list[:want]
			break
		}
	}
	if len(list) > want {
		list = list[:want]
	}
	return list, nil
}
