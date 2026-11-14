// 库快照与统计读面——四色聚合 / 缺失排行 / 副本计数。
// 契约对齐 C# Data/Store.cs §Snapshot / §MissingRanking / §CountModFiles / §ListModFileCounts
// 与 Data/StoreHub.cs §Snapshot / §DupCounts（含 ATTACH core 的 mod 表前缀规则）。
package store

import (
	"strconv"
)

// Snapshot 库快照——各表计数与四色统计（对齐 C# Snapshot）。
type Snapshot struct {
	// Cards 卡片总数。
	Cards int64
	// Mods mod 总数（按 guid）。
	Mods int64
	// ModFiles mod 文件副本总数。
	ModFiles int64
	// Refs 引用条目总数（去重 guid 后的条数）。
	Refs int64
	// ModsTier1 主库 mod 数。
	ModsTier1 int64
	// ModsTier2 缓存库 mod 数。
	ModsTier2 int64
	// ModsTier3 冷冻库 mod 数。
	ModsTier3 int64
	// CardsReady 主库齐备的卡数（就绪）。
	CardsReady int64
	// CardsWithRefs 有引用的卡数。
	CardsWithRefs int64
	// Green 主库齐备的引用数。
	Green int64
	// Yellow 仅缓存库的引用数。
	Yellow int64
	// Red 仅冷冻库的引用数。
	Red int64
	// Black 全库皆无的引用数。
	Black int64
	// UnusedMods 未被任何卡片引用的 mod 数。
	UnusedMods int64
	// DupMods 同 guid 多文件（重复安装）数。
	DupMods int64
}

// MissingRow 缺失 guid 排行行。
type MissingRow struct {
	// Guid 缺失的 mod guid。
	Guid string
	// Count 引用它的卡片数（跨库合计口径由调用方决定）。
	Count int64
}

// ModSizeRow 主库级 mod 的 guid 与字节数。
type ModSizeRow struct {
	// Guid mod guid。
	Guid string
	// Size 最优副本字节数。
	Size int64
}

// ModFileCount 本库按 guid 的副本计数（总数 / 非旧版数）——重复副本组数与「待确认」组数用。
type ModFileCount struct {
	// Guid mod guid。
	Guid string
	// Total 副本总数。
	Total int64
	// Live 非旧版副本数。
	Live int64
}

// colorCte 四色聚合 CTE——mod 引用按所在连接前缀（主库本地表 / 库文件走 core）。
// 与 C# Store.ColorCte 逐字同构：per 按 (card_id, mod_guid) 取最小 tier（无行 = 9），agg 按 card 汇总四色。
func colorCte(modTable string) string {
	return `
            WITH per AS (
              SELECT cm.card_id, cm.mod_guid, MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) AS tier
              FROM card_mod cm LEFT JOIN ` + modTable + ` m ON m.guid = cm.mod_guid
              GROUP BY cm.card_id, cm.mod_guid
            ),
            agg AS (
              SELECT card_id,
                SUM(CASE WHEN tier=1 THEN 1 ELSE 0 END) AS green,
                SUM(CASE WHEN tier=2 THEN 1 ELSE 0 END) AS yellow,
                SUM(CASE WHEN tier=3 THEN 1 ELSE 0 END) AS red,
                SUM(CASE WHEN tier=9 THEN 1 ELSE 0 END) AS black
              FROM per GROUP BY card_id
            )`
}

// snapshotSQL 单库快照 SQL（15 个标量）——与 C# Store.Snapshot 的查询逐项同序。
func snapshotSQL(modTable string) string {
	return colorCte(modTable) + ` SELECT
                     (SELECT COUNT(*) FROM card),
                     (SELECT COUNT(*) FROM ` + modTable + `),
                     (SELECT COUNT(*) FROM mod_file),
                     (SELECT COUNT(*) FROM per),
                     (SELECT COUNT(*) FROM ` + modTable + ` WHERE tier=1),
                     (SELECT COUNT(*) FROM ` + modTable + ` WHERE tier=2),
                     (SELECT COUNT(*) FROM ` + modTable + ` WHERE tier=3),
                     (SELECT COUNT(*) FROM card c LEFT JOIN agg a ON a.card_id=c.id WHERE c.mod_count>0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0))=0),
                     (SELECT COUNT(*) FROM card WHERE mod_count>0),
                     (SELECT COALESCE(SUM(green),0) FROM agg),
                     (SELECT COALESCE(SUM(yellow),0) FROM agg),
                     (SELECT COALESCE(SUM(red),0) FROM agg),
                     (SELECT COALESCE(SUM(black),0) FROM agg),
                     (SELECT COUNT(*) FROM ` + modTable + ` m WHERE NOT EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)),
                     (SELECT COUNT(*) FROM ` + modTable + ` WHERE dup_count>1)`
}

// SnapshotOf 某个库的快照——主库直查；库文件先 ATTACH 主库为 core（对齐 C# ModTable 规则）。
func (h *Hub) SnapshotOf(db *DB) (*Snapshot, error) {
	if db == nil {
		return nil, errNilDB
	}
	modTable := "mod"
	if db != h.core {
		if err := db.attachCore(h.core.Path()); err != nil {
			return nil, err
		}
		modTable = "core.mod"
	}
	var s Snapshot
	err := db.sql.QueryRow(snapshotSQL(modTable)).Scan(
		&s.Cards, &s.Mods, &s.ModFiles, &s.Refs,
		&s.ModsTier1, &s.ModsTier2, &s.ModsTier3,
		&s.CardsReady, &s.CardsWithRefs,
		&s.Green, &s.Yellow, &s.Red, &s.Black,
		&s.UnusedMods, &s.DupMods)
	if err != nil {
		return nil, err
	}
	return &s, nil
}

// MissingRankingOf 某个库的缺失 guid 排行（该库 mod 表里没有的 guid，按被引用卡片数倒序）；
// top ≤ 0 = 不限条数。库文件先 ATTACH 主库为 core（对齐 C# ModTable 规则）。
func (h *Hub) MissingRankingOf(db *DB, top int) ([]MissingRow, error) {
	if db == nil {
		return nil, errNilDB
	}
	modTable := "mod"
	if db != h.core {
		if err := db.attachCore(h.core.Path()); err != nil {
			return nil, err
		}
		modTable = "core.mod"
	}
	limit := top
	if limit <= 0 {
		limit = -1
	}
	rows, err := db.sql.Query(`SELECT cm.mod_guid, COUNT(DISTINCT cm.card_id) AS c
		FROM card_mod cm WHERE NOT EXISTS(SELECT 1 FROM ` + modTable + ` m WHERE m.guid=cm.mod_guid)
		GROUP BY cm.mod_guid ORDER BY c DESC, cm.mod_guid LIMIT ` + strconv.Itoa(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []MissingRow
	for rows.Next() {
		var r MissingRow
		if err = rows.Scan(&r.Guid, &r.Count); err != nil {
			return nil, err
		}
		list = append(list, r)
	}
	return list, rows.Err()
}

// CountModFiles 本库 mod 副本行数。
func (d *DB) CountModFiles() (int64, error) {
	var n int64
	err := d.sql.QueryRow("SELECT COUNT(*) FROM mod_file").Scan(&n)
	return n, err
}

// DistinctRefGuids 本库 card_mod 出现过的 mod guid 集合（跨库算「未被引用的 mod」用；大小写敏感）。
func (d *DB) DistinctRefGuids() ([]string, error) {
	rows, err := d.sql.Query("SELECT DISTINCT mod_guid FROM card_mod")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []string
	for rows.Next() {
		var guid string
		if err = rows.Scan(&guid); err != nil {
			return nil, err
		}
		list = append(list, guid)
	}
	return list, rows.Err()
}

// MainModSizes 主库级 mod 的 guid 与字节数（最优副本在主库 · 级别 1），按字节数降序——「未引用 mod」判定的来源。
func (d *DB) MainModSizes() ([]ModSizeRow, error) {
	rows, err := d.sql.Query("SELECT guid, size FROM mod WHERE tier=1 ORDER BY size DESC")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModSizeRow
	for rows.Next() {
		var r ModSizeRow
		if err = rows.Scan(&r.Guid, &r.Size); err != nil {
			return nil, err
		}
		list = append(list, r)
	}
	return list, rows.Err()
}

// ListModFileCounts 本库按 guid 的副本计数（总数 / 非旧版数）——旧版判据与 RootsRules.IsOldFileName 对齐
// （扩展名前的 .old 段；SQL 侧按容器扩展名枚举 .zipmod / .zip）。
func (d *DB) ListModFileCounts() ([]ModFileCount, error) {
	rows, err := d.sql.Query(`SELECT guid, COUNT(*), SUM(CASE WHEN file_name LIKE '%.old.zipmod' OR file_name LIKE '%.old.zip' THEN 0 ELSE 1 END)
		FROM mod_file WHERE guid IS NOT NULL AND guid <> '' GROUP BY guid`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileCount
	for rows.Next() {
		var r ModFileCount
		var live *int64
		if err = rows.Scan(&r.Guid, &r.Total, &live); err != nil {
			return nil, err
		}
		if live != nil {
			r.Live = *live
		}
		list = append(list, r)
	}
	return list, rows.Err()
}
