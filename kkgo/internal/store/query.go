// 查询读面（单库）——卡片 / 文件夹 / mod / 作者 / 非卡 / 重复副本 / 引用行。
//
// 契约对齐 C# Data/Store.cs 同名查询：
//
//	QueryCards / QueryFolders / QueryMods / QueryAuthors / ListNonCards / ListModFiles /
//	ListModFileCounts / RefCountsByTypeByGuid / QueryCardsByMod / CountCardsByMod /
//	CountCardsByMods / RefCardsByMods / QueryModFiles / QueryModFilesByRoot
//
// 跨库合并（AllStores + 排序 + 分页）在 core 层编排——store 不依赖 core 的 RootsConfig。
package store

import (
	"database/sql"
	"sort"
	"strings"
)

// CardListRow 卡片列表行（对齐 C# Store.CardRow）。
type CardListRow struct {
	// Id 卡片编号（分片内唯一）。
	Id int64
	// FilePath 卡片文件绝对路径（单条读面用；列表查询不回填）。
	FilePath string
	// FileName 文件名。
	FileName string
	// CardType 卡类型（可空）。
	CardType string
	// CardTypeSet 卡类型是否有值（NULL 与空串可辨）。
	CardTypeSet bool
	// Size 文件字节数。
	Size int64
	// Mtime 修改时间戳（可空）。
	Mtime string
	// MtimeSet 修改时间是否有值。
	MtimeSet bool
	// RootPath 所在库根（可空）。
	RootPath string
	// RootPathSet 库根是否有值。
	RootPathSet bool
	// Tier 级别。
	Tier int
	// Folder 文件夹。
	Folder string
	// ModCount 声明的 mod 数。
	ModCount int64
	// Green 主库齐备引用数。
	Green int64
	// Yellow 仅缓存库引用数。
	Yellow int64
	// Red 仅冷冻库引用数。
	Red int64
	// Black 全库皆无引用数。
	Black int64
	// HasThumb 是否有缩略图。
	HasThumb bool
	// CharaName 角色名（可空）。
	CharaName string
	// CharaNameSet 角色名是否有值。
	CharaNameSet bool
	// TimelineSeconds timeline 长度（可空；序列化时按有值 / null 两态）。
	TimelineSeconds float64
	// TimelineSet timeline 是否有值。
	TimelineSet bool
	// Lib 库序号（跨库合并时由调用方填；单库查询为 0）。
	Lib int
}

// FolderRow 卡片文件夹行（对齐 C# Store.FolderRow）。
type FolderRow struct {
	// RootPath 库根路径。
	RootPath string
	// Folder 文件夹。
	Folder string
	// Count 卡片数。
	Count int64
	// Ready 就绪数（有 mod 引用且黄红黑为 0）。
	Ready int64
	// Black 有全库皆无引用的卡数。
	Black int64
}

// ModListRow mod 列表行（对齐 C# Store.ModRow）。
type ModListRow struct {
	// Guid mod guid。
	Guid string
	// Name 名称（可空）。
	Name string
	// NameSet 名称是否有值。
	NameSet bool
	// Author 作者（可空）。
	Author string
	// AuthorSet 作者是否有值。
	AuthorSet bool
	// Version 版本（可空）。
	Version string
	// VersionSet 版本是否有值。
	VersionSet bool
	// Tier 级别。
	Tier int
	// RootPath 库根（可空）。
	RootPath string
	// RootPathSet 库根是否有值。
	RootPathSet bool
	// FileName 文件名（可空）。
	FileName string
	// FileNameSet 文件名是否有值。
	FileNameSet bool
	// FilePath 文件路径（可空）。
	FilePath string
	// FilePathSet 文件路径是否有值。
	FilePathSet bool
	// Size 字节数。
	Size int64
	// DupCount 副本数。
	DupCount int64
	// Used 被引用卡数（单库口径；跨库汇总由调用方覆盖）。
	Used int64
	// UsedChara 人物卡引用数。
	UsedChara int64
	// UsedClothes 服装卡引用数。
	UsedClothes int64
	// UsedSd 场景卡引用数。
	UsedSd int64
	// HasOld 是否有旧版登记。
	HasOld bool
	// OldName 旧版登记里的旧名。
	OldName string
}

// AuthorRow 作者聚合行（对齐 C# Store.AuthorRow）。
type AuthorRow struct {
	// Author 作者名（空作者为 ""）。
	Author string
	// Count mod 数量。
	Count int64
}

// NonCardRow 非卡 / 非 mod 登记行（对齐 C# Store.NonCardRow）。
type NonCardRow struct {
	// Id 登记编号。
	Id int64
	// FilePath 文件路径。
	FilePath string
	// RootPath 库根路径。
	RootPath string
	// Side 侧别（card / mod）。
	Side string
	// Reason 判定理由。
	Reason string
	// Size 字节数。
	Size int64
	// Mtime 修改时间戳。
	Mtime string
	// MovedTo 搬走后的新路径（空 = 未搬）。
	MovedTo string
	// HasThumb 是否有缩略图。
	HasThumb bool
	// Lib 库序号（跨库合并时填）。
	Lib int
}

// ModFileRec mod 副本记录（含大小 / 时间，对齐 C# Store.ModFileRecord）。
type ModFileRec struct {
	// FilePath 文件路径。
	FilePath string
	// Guid mod guid。
	Guid string
	// Tier 级别。
	Tier int
	// RootPath 库根路径。
	RootPath string
	// FileName 文件名。
	FileName string
	// Size 字节数。
	Size int64
	// Mtime 修改时间戳。
	Mtime string
	// ScanTime 扫描时刻。
	ScanTime string
}

// RefRow mod 文件副本行（对齐 C# Store.RefRow）。
type RefRow struct {
	// Guid mod guid。
	Guid string
	// Tier 级别。
	Tier int
	// RootPath 库根。
	RootPath string
	// FilePath 文件路径。
	FilePath string
	// FileName 文件名。
	FileName string
}

// CardRefRow 引用卡片轻量行（对齐 C# Store.CardRefRow——不含四色聚合）。
type CardRefRow struct {
	// Guid mod guid。
	Guid string
	// Id 卡片编号。
	Id int64
	// FileName 文件名。
	FileName string
	// RootPath 库根（可空）。
	RootPath string
	// RootPathSet 库根是否有值。
	RootPathSet bool
	// Folder 文件夹。
	Folder string
	// HasThumb 是否有缩略图。
	HasThumb bool
	// CardType 卡类型（可空）。
	CardType string
	// CardTypeSet 卡类型是否有值。
	CardTypeSet bool
}

// DupGroup 重复副本组（对齐 C# StoreHub.DupGroup）。
type DupGroup struct {
	// Guid mod guid。
	Guid string
	// Files 各副本。
	Files []ModFileRec
	// MainCount 主库副本数。
	MainCount int64
}

// DupCardRefs 重复副本组的引用卡片（总数 + 前若干张）。
type DupCardRefs struct {
	// Total 引用卡片总数。
	Total int64
	// Top 前若干张卡片。
	Top []CardListRow
}

// RefCount 某 guid 的引用计数四元组（总数 + 三类型分列）。
type RefCount struct {
	// Used 被引用卡片总数。
	Used int64
	// Chara 人物卡数。
	Chara int64
	// Clothes 服装卡数。
	Clothes int64
	// Sd 场景卡数。
	Sd int64
}

// QueryCardsRows 单库卡片列表查询（对齐 C# Store.QueryCards）。
// folder 传 nil = 不按文件夹筛；root 空 = 不按库根筛；size ≤ 0 = 不限条数。
func (d *DB) QueryCardsRows(page, size int, filter, q string, folder *string, root, order string, desc bool) ([]CardListRow, error) {
	where := "WHERE 1=1"
	args := make([]interface{}, 0, 6)
	if q != "" {
		where += " AND c.file_name LIKE ?"
		args = append(args, "%"+q+"%")
	}
	if folder != nil {
		where += " AND c.folder = ?"
		args = append(args, *folder)
	}
	if root != "" {
		where += " AND c.root_path = ?"
		args = append(args, root)
	}
	switch filter {
	case "pending":
		where += " AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0)) > 0"
	case "ready":
		where += " AND c.mod_count > 0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0)) = 0"
	case "black":
		where += " AND COALESCE(a.black,0) > 0"
	case "nomod":
		where += " AND c.mod_count = 0"
	case "nothumb":
		where += " AND c.thumb IS NULL"
	}
	orderBy := "ORDER BY c.folder, c.mtime DESC, c.id"
	switch order {
	case "size":
		if desc {
			orderBy = "ORDER BY c.folder, c.size DESC, c.id"
		} else {
			orderBy = "ORDER BY c.folder, c.size ASC, c.id"
		}
	case "file":
		if desc {
			orderBy = "ORDER BY c.folder, c.file_name DESC, c.id"
		} else {
			orderBy = "ORDER BY c.folder, c.file_name ASC, c.id"
		}
	case "chara":
		if desc {
			orderBy = "ORDER BY c.folder, CASE WHEN c.chara_name IS NULL OR c.chara_name = '' THEN 1 ELSE 0 END, c.chara_name DESC, c.id"
		} else {
			orderBy = "ORDER BY c.folder, CASE WHEN c.chara_name IS NULL OR c.chara_name = '' THEN 1 ELSE 0 END, c.chara_name ASC, c.id"
		}
	case "timeline":
		if desc {
			orderBy = "ORDER BY c.folder, CASE WHEN tl.duration IS NULL THEN 1 ELSE 0 END, tl.duration DESC, c.id"
		} else {
			orderBy = "ORDER BY c.folder, CASE WHEN tl.duration IS NULL THEN 1 ELSE 0 END, tl.duration ASC, c.id"
		}
	case "miss":
		orderBy = "ORDER BY c.folder, CASE WHEN COALESCE(a.black,0) > 0 THEN 0 WHEN COALESCE(a.red,0) > 0 THEN 1 WHEN COALESCE(a.yellow,0) > 0 THEN 2 ELSE 3 END, c.mtime DESC, c.id"
	}
	limit := size
	if limit <= 0 {
		limit = -1
	}
	off := 0
	if limit > 0 && page > 1 {
		off = (page - 1) * limit
	}
	args = append(args, limit, off)
	sqlText := colorCte(d.modTable()) + ` SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL), c.chara_name,
                     CASE WHEN tl.has_entry = 1 AND tl.is_empty = 0 THEN tl.duration END
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     LEFT JOIN card_timeline tl ON tl.card_id = c.id ` + where + " " + orderBy + `
                     LIMIT ? OFFSET ?`
	rows, err := d.sql.Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardListRow
	for rows.Next() {
		var r CardListRow
		var cardType, mtime, rootPath, charaName sql.NullString
		var duration sql.NullFloat64
		var thumb int64
		if err = rows.Scan(&r.Id, &r.FileName, &cardType, &r.Size, &mtime,
			&rootPath, &r.Tier, &r.Folder, &r.ModCount,
			&r.Green, &r.Yellow, &r.Red, &r.Black, &thumb, &charaName, &duration); err != nil {
			return nil, err
		}
		r.CardType, r.CardTypeSet = cardType.String, cardType.Valid
		r.Mtime, r.MtimeSet = mtime.String, mtime.Valid
		r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
		r.CharaName, r.CharaNameSet = charaName.String, charaName.Valid
		r.HasThumb = thumb != 0
		r.TimelineSeconds, r.TimelineSet = duration.Float64, duration.Valid
		list = append(list, r)
	}
	return list, rows.Err()
}

// QueryFoldersRows 单库卡片文件夹清单（对齐 C# Store.QueryFolders）。
func (d *DB) QueryFoldersRows() ([]FolderRow, error) {
	sqlText := colorCte(d.modTable()) + ` SELECT c.root_path, c.folder, COUNT(*) AS n,
                     SUM(CASE WHEN c.mod_count > 0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0))=0 THEN 1 ELSE 0 END) AS ready,
                     SUM(CASE WHEN COALESCE(a.black,0) > 0 THEN 1 ELSE 0 END) AS black
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     GROUP BY c.root_path, c.folder ORDER BY c.root_path, c.folder`
	rows, err := d.sql.Query(sqlText)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []FolderRow
	for rows.Next() {
		var r FolderRow
		var rootPath, folder sql.NullString
		if err = rows.Scan(&rootPath, &folder, &r.Count, &r.Ready, &r.Black); err != nil {
			return nil, err
		}
		r.RootPath, r.Folder = rootPath.String, folder.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// QueryModsRows 单库 mod 列表查询（对齐 C# Store.QueryMods）。size ≤ 0 = 不限条数。
func (d *DB) QueryModsRows(page, size int, filter, q string) ([]ModListRow, error) {
	where := "WHERE 1=1"
	args := make([]interface{}, 0, 3)
	if q != "" {
		where += " AND (m.guid LIKE ? OR m.file_name LIKE ? OR m.name LIKE ?)"
		like := "%" + q + "%"
		args = append(args, like, like, like)
	}
	switch filter {
	case "unused":
		where += " AND m.tier=1 AND NOT EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)"
	case "used":
		where += " AND EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)"
	case "tier1":
		where += " AND m.tier=1"
	case "tier2":
		where += " AND m.tier=2"
	case "tier3":
		where += " AND m.tier=3"
	case "dup":
		where += " AND m.dup_count > 1"
	}
	limit := size
	if limit <= 0 {
		limit = -1
	}
	off := 0
	if limit > 0 && page > 1 {
		off = (page - 1) * limit
	}
	args = append(args, limit, off)
	sqlText := "SELECT m.guid, m.name, m.author, m.version, m.tier, m.root_path, m.file_name, m.file_path, m.size, m.dup_count, " +
		"(SELECT COUNT(DISTINCT cm.card_id) FROM card_mod cm WHERE cm.mod_guid=m.guid) AS used" +
		" FROM " + d.modTable() + " m " + where + " ORDER BY used DESC, m.guid LIMIT ? OFFSET ?"
	rows, err := d.sql.Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModListRow
	for rows.Next() {
		var r ModListRow
		var name, author, version, rootPath, fileName, filePath sql.NullString
		var tier, dupCount sql.NullInt64
		if err = rows.Scan(&r.Guid, &name, &author, &version, &tier, &rootPath, &fileName, &filePath, &r.Size, &dupCount, &r.Used); err != nil {
			return nil, err
		}
		r.Name, r.NameSet = name.String, name.Valid
		r.Author, r.AuthorSet = author.String, author.Valid
		r.Version, r.VersionSet = version.String, version.Valid
		r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
		r.FileName, r.FileNameSet = fileName.String, fileName.Valid
		r.FilePath, r.FilePathSet = filePath.String, filePath.Valid
		r.Tier, r.DupCount = int(tier.Int64), dupCount.Int64
		list = append(list, r)
	}
	return list, rows.Err()
}

// QueryAuthorsRows 作者聚合表（对齐 C# Store.QueryAuthors）：数量倒序，同数按作者名。
func (d *DB) QueryAuthorsRows() ([]AuthorRow, error) {
	rows, err := d.sql.Query("SELECT author, mod_count FROM " + d.authorTable() + " ORDER BY mod_count DESC, author")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []AuthorRow
	for rows.Next() {
		var r AuthorRow
		var author sql.NullString
		var count sql.NullInt64
		if err = rows.Scan(&author, &count); err != nil {
			return nil, err
		}
		r.Author, r.Count = author.String, count.Int64
		list = append(list, r)
	}
	return list, rows.Err()
}

// ListNonCardRows 非卡 / 非 mod 登记（对齐 C# Store.ListNonCards）：按字节数降序。
func (d *DB) ListNonCardRows(includeMoved bool) ([]NonCardRow, error) {
	sqlText := "SELECT id,file_path,root_path,side,reason,size,mtime,moved_to,(thumb IS NOT NULL) FROM non_card"
	if !includeMoved {
		sqlText += " WHERE moved_to IS NULL"
	}
	sqlText += " ORDER BY size DESC"
	rows, err := d.sql.Query(sqlText)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []NonCardRow
	for rows.Next() {
		var r NonCardRow
		var filePath, rootPath, side, reason, mtime, movedTo sql.NullString
		var thumb int64
		if err = rows.Scan(&r.Id, &filePath, &rootPath, &side, &reason, &r.Size, &mtime, &movedTo, &thumb); err != nil {
			return nil, err
		}
		r.FilePath, r.RootPath, r.Side, r.Reason = filePath.String, rootPath.String, side.String, reason.String
		r.Mtime, r.MovedTo, r.HasThumb = mtime.String, movedTo.String, thumb != 0
		list = append(list, r)
	}
	return list, rows.Err()
}

// CountNonCardRows 非卡登记条数（对齐 C# Store.CountNonCards）。
func (d *DB) CountNonCardRows(includeMoved bool) (int64, error) {
	sqlText := "SELECT COUNT(*) FROM non_card"
	if !includeMoved {
		sqlText += " WHERE moved_to IS NULL"
	}
	var n int64
	err := d.sql.QueryRow(sqlText).Scan(&n)
	return n, err
}

// ListModFileRecs 本库全部 mod 副本行（对齐 C# Store.ListModFiles）：按 guid、tier 排序。
func (d *DB) ListModFileRecs() ([]ModFileRec, error) {
	rows, err := d.sql.Query("SELECT file_path, guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file ORDER BY guid, tier")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileRec
	for rows.Next() {
		var r ModFileRec
		var guid, rootPath, fileName, mtime, scanTime sql.NullString
		var tier sql.NullInt64
		if err = rows.Scan(&r.FilePath, &guid, &tier, &rootPath, &fileName, &r.Size, &mtime, &scanTime); err != nil {
			return nil, err
		}
		r.Guid, r.Tier = guid.String, int(tier.Int64)
		r.RootPath, r.FileName, r.Mtime, r.ScanTime = rootPath.String, fileName.String, mtime.String, scanTime.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// ListModFileCountRows 本库按 guid 的副本计数（对齐 C# Store.ListModFileCounts）：
// 旧版判据与 RootsRules.IsOldFileName 对齐（扩展名前的 .old 段）。
func (d *DB) ListModFileCountRows() ([]ModFileCount, error) {
	rows, err := d.sql.Query(`SELECT guid, COUNT(*), SUM(CASE WHEN file_name LIKE '%.old.zipmod' OR file_name LIKE '%.old.zip' THEN 0 ELSE 1 END)
                     FROM mod_file WHERE guid IS NOT NULL AND guid <> '' GROUP BY guid`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileCount
	for rows.Next() {
		var r ModFileCount
		var live sql.NullInt64
		if err = rows.Scan(&r.Guid, &r.Total, &live); err != nil {
			return nil, err
		}
		r.Live = live.Int64
		list = append(list, r)
	}
	return list, rows.Err()
}

// RefCountsByType 本库按 guid 的引用计数（对齐 C# Store.RefCountsByTypeByGuid）：
// 一次查询同时出总数与三类型分列（人物卡 / 服装卡 / 场景卡 sd）。
func (d *DB) RefCountsByType() (map[string]RefCount, error) {
	sqlText := `SELECT cm.mod_guid,
                     COUNT(DISTINCT cm.card_id),
                     COUNT(DISTINCT CASE WHEN c.card_type LIKE '%Chara%' THEN cm.card_id END),
                     COUNT(DISTINCT CASE WHEN c.card_type LIKE '%Clothes%' THEN cm.card_id END),
                     COUNT(DISTINCT CASE WHEN c.card_type = 'sd' THEN cm.card_id END)
                     FROM card_mod cm LEFT JOIN card c ON c.id = cm.card_id
                     GROUP BY cm.mod_guid`
	rows, err := d.sql.Query(sqlText)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]RefCount)
	for rows.Next() {
		var guid string
		var v RefCount
		if err = rows.Scan(&guid, &v.Used, &v.Chara, &v.Clothes, &v.Sd); err != nil {
			return nil, err
		}
		out[guid] = v
	}
	return out, rows.Err()
}

// QueryCardsByModRows 某 guid 被哪些卡片引用（对齐 C# Store.QueryCardsByMod）。
func (d *DB) QueryCardsByModRows(guid string, page, size int) ([]CardListRow, error) {
	limit := size
	if limit <= 0 {
		limit = -1
	}
	off := 0
	if limit > 0 && page > 1 {
		off = (page - 1) * limit
	}
	sqlText := colorCte(d.modTable()) + ` SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL)
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     WHERE c.id IN (SELECT card_id FROM card_mod WHERE mod_guid = ?)
                     ORDER BY c.root_path, c.folder, c.file_name LIMIT ? OFFSET ?`
	rows, err := d.sql.Query(sqlText, guid, limit, off)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardListRow
	for rows.Next() {
		var r CardListRow
		var cardType, mtime, rootPath sql.NullString
		var thumb int64
		if err = rows.Scan(&r.Id, &r.FileName, &cardType, &r.Size, &mtime,
			&rootPath, &r.Tier, &r.Folder, &r.ModCount,
			&r.Green, &r.Yellow, &r.Red, &r.Black, &thumb); err != nil {
			return nil, err
		}
		r.CardType, r.CardTypeSet = cardType.String, cardType.Valid
		r.Mtime, r.MtimeSet = mtime.String, mtime.Valid
		r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
		r.HasThumb = thumb != 0
		list = append(list, r)
	}
	return list, rows.Err()
}

// CountCardsByModRows 某 guid 被引用的卡片总数（对齐 C# Store.CountCardsByMod）。
func (d *DB) CountCardsByModRows(guid string) (int64, error) {
	var n int64
	err := d.sql.QueryRow("SELECT COUNT(DISTINCT card_id) FROM card_mod WHERE mod_guid = ?", guid).Scan(&n)
	return n, err
}

// CountCardsByModsBatch 一批 guid 各自的引用卡片总数（对齐 C# Store.CountCardsByMods）。
func (d *DB) CountCardsByModsBatch(guids []string) (map[string]int64, error) {
	out := make(map[string]int64)
	if len(guids) == 0 {
		return out, nil
	}
	names := make([]string, len(guids))
	args := make([]interface{}, len(guids))
	for i, g := range guids {
		names[i] = "?"
		args[i] = g
	}
	sqlText := "SELECT mod_guid, COUNT(DISTINCT card_id) FROM card_mod WHERE mod_guid IN (" +
		strings.Join(names, ",") + ") GROUP BY mod_guid"
	rows, err := d.sql.Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var guid string
		var n int64
		if err = rows.Scan(&guid, &n); err != nil {
			return nil, err
		}
		out[guid] = n
	}
	return out, rows.Err()
}

// RefCardsByModsBatch 一批 guid 的引用卡片轻量行（对齐 C# Store.RefCardsByMods）。
func (d *DB) RefCardsByModsBatch(guids []string) ([]CardRefRow, error) {
	if len(guids) == 0 {
		return nil, nil
	}
	names := make([]string, len(guids))
	args := make([]interface{}, len(guids))
	for i, g := range guids {
		names[i] = "?"
		args[i] = g
	}
	sqlText := `SELECT cm.mod_guid, c.id, c.file_name, c.root_path, c.folder, (c.thumb IS NOT NULL), c.card_type
                     FROM (SELECT DISTINCT mod_guid, card_id FROM card_mod WHERE mod_guid IN (` +
		strings.Join(names, ",") + `)) cm
                     JOIN card c ON c.id = cm.card_id`
	rows, err := d.sql.Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardRefRow
	for rows.Next() {
		var r CardRefRow
		var rootPath, cardType sql.NullString
		var thumb int64
		if err = rows.Scan(&r.Guid, &r.Id, &r.FileName, &rootPath, &r.Folder, &thumb, &cardType); err != nil {
			return nil, err
		}
		r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
		r.CardType, r.CardTypeSet = cardType.String, cardType.Valid
		r.HasThumb = thumb != 0
		list = append(list, r)
	}
	return list, rows.Err()
}

// QueryRefRowsByGuid 某 guid 的全部文件副本（对齐 C# Store.QueryModFiles）：按 tier 升序。
func (d *DB) QueryRefRowsByGuid(guid string) ([]RefRow, error) {
	rows, err := d.sql.Query("SELECT guid, tier, root_path, file_path, file_name FROM mod_file WHERE guid=? ORDER BY tier", guid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []RefRow
	for rows.Next() {
		var r RefRow
		var tier sql.NullInt64
		var rootPath, fileName sql.NullString
		if err = rows.Scan(&r.Guid, &tier, &rootPath, &r.FilePath, &fileName); err != nil {
			return nil, err
		}
		r.Tier = int(tier.Int64)
		r.RootPath, r.FileName = rootPath.String, fileName.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// QueryModFileRecsByRoot 某库根下全部 mod 副本行（对齐 C# Store.QueryModFilesByRoot）：按路径升序。
func (d *DB) QueryModFileRecsByRoot(rootPath string) ([]ModFileRec, error) {
	rows, err := d.sql.Query("SELECT file_path,guid,tier,root_path,file_name,size,mtime,scan_time FROM mod_file WHERE root_path=? ORDER BY file_path", rootPath)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileRec
	for rows.Next() {
		var r ModFileRec
		var guid, root, fileName, mtime, scanTime sql.NullString
		var tier sql.NullInt64
		if err = rows.Scan(&r.FilePath, &guid, &tier, &root, &fileName, &r.Size, &mtime, &scanTime); err != nil {
			return nil, err
		}
		r.Guid, r.Tier, r.RootPath = guid.String, int(tier.Int64), root.String
		r.FileName, r.Mtime, r.ScanTime = fileName.String, mtime.String, scanTime.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// CompareCardRows 卡片排序（对齐 C# StoreHub.CardComparer）：
// 文件夹（忽略大小写）· 主键（按 order）· 库位 · 编号。
func CompareCardRows(a, b *CardListRow, order string, desc bool) int {
	c := strings.Compare(strings.ToLower(a.Folder), strings.ToLower(b.Folder))
	if c != 0 {
		return c
	}
	switch order {
	case "size":
		if desc {
			c = cmpInt64(b.Size, a.Size)
		} else {
			c = cmpInt64(a.Size, b.Size)
		}
	case "file":
		c = strings.Compare(strings.ToLower(a.FileName), strings.ToLower(b.FileName))
		if desc {
			c = -c
		}
	case "chara":
		ea, eb := a.CharaName == "", b.CharaName == ""
		if ea != eb {
			if ea {
				c = 1
			} else {
				c = -1
			}
		} else if !ea {
			c = strings.Compare(strings.ToLower(a.CharaName), strings.ToLower(b.CharaName))
			if desc {
				c = -c
			}
		}
	case "timeline":
		ea, eb := !a.TimelineSet, !b.TimelineSet
		if ea != eb {
			if ea {
				c = 1
			} else {
				c = -1
			}
		} else if !ea {
			if a.TimelineSeconds < b.TimelineSeconds {
				c = -1
			} else if a.TimelineSeconds > b.TimelineSeconds {
				c = 1
			} else {
				c = 0
			}
			if desc {
				c = -c
			}
		}
	case "miss":
		c = cmpInt64(int64(missRank(a)), int64(missRank(b)))
		if c == 0 {
			c = strings.Compare(b.Mtime, a.Mtime)
		}
	default:
		c = strings.Compare(b.Mtime, a.Mtime)
	}
	if c != 0 {
		return c
	}
	c = cmpInt64(int64(a.Lib), int64(b.Lib))
	if c != 0 {
		return c
	}
	return cmpInt64(a.Id, b.Id)
}

// missRank 缺失严重度排名（0 黑 · 1 灰 · 2 黄 · 3 绿）——与四色优先级同源。
func missRank(r *CardListRow) int {
	if r.Black > 0 {
		return 0
	}
	if r.Red > 0 {
		return 1
	}
	if r.Yellow > 0 {
		return 2
	}
	return 3
}

// SortCardRows 按卡片排序器就地排序（稳定）。
func SortCardRows(list []CardListRow, order string, desc bool) {
	sort.SliceStable(list, func(i, j int) bool {
		return CompareCardRows(&list[i], &list[j], order, desc) < 0
	})
}

// SliceRows 通用分页切片（对齐 C# StoreHub.Slice）：page ≤ 0 按 1；size ≤ 0 或超界 = 全量。
func SliceRows[T any](list []T, page, size int) []T {
	if page <= 0 {
		page = 1
	}
	if size <= 0 || size >= len(list) {
		return list
	}
	off := (page - 1) * size
	if off >= len(list) {
		return nil
	}
	end := off + size
	if end > len(list) {
		end = len(list)
	}
	return list[off:end]
}

// cmpInt64 三态比较（-1 / 0 / 1）。
func cmpInt64(a, b int64) int {
	if a < b {
		return -1
	}
	if a > b {
		return 1
	}
	return 0
}

// CompareModRows mod 排序（对齐 C# StoreHub.CompareMods）：被引用卡数倒序 · guid。
func CompareModRows(a, b *ModListRow) int {
	if c := cmpInt64(b.Used, a.Used); c != 0 {
		return c
	}
	return strings.Compare(a.Guid, b.Guid)
}

// CompareRefRows 副本排序（对齐 C# StoreHub.CompareFileByTier）：级别 · 文件名（忽略大小写）。
func CompareRefRows(a, b *ModFileRec) int {
	if c := cmpInt64(int64(a.Tier), int64(b.Tier)); c != 0 {
		return c
	}
	return strings.Compare(strings.ToLower(a.FileName), strings.ToLower(b.FileName))
}

// CompareCardsByMod 反查卡片排序（对齐 C# StoreHub.CompareCardsByMod）：库根 · 文件夹 · 文件名。
func CompareCardsByMod(a, b *CardListRow) int {
	if c := strings.Compare(strings.ToLower(a.RootPath), strings.ToLower(b.RootPath)); c != 0 {
		return c
	}
	if c := strings.Compare(strings.ToLower(a.Folder), strings.ToLower(b.Folder)); c != 0 {
		return c
	}
	return strings.Compare(strings.ToLower(a.FileName), strings.ToLower(b.FileName))
}
