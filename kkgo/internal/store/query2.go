// 查询补面——mod 旧版登记 / 作者聚合重建 / 某 guid 的副本记录（带大小与时间）。
//
// 契约对齐 C# Data/Store.cs：
//
//	LoadModOldMap / RefreshModAuthors / QueryModFileRecords / SetModOld / DeleteModOld
package store

import "database/sql"

// ModOldRecord 一条旧版登记（对齐 C# Store.ModOldRecord）。
type ModOldRecord struct {
	// OldPath 旧版文件的路径（主键）。
	OldPath string
	// Guid mod guid。
	Guid string
	// OldName 旧版文件名。
	OldName string
	// OldVersion 旧版版本。
	OldVersion string
	// NewName 登记时新版的文件名。
	NewName string
	// NewVersion 登记时新版的版本。
	NewVersion string
	// MarkedAt 登记时刻。
	MarkedAt string
}

// LoadModOldMap 旧版登记全表（主库表）——键 = 旧版文件路径。
func (d *DB) LoadModOldMap() map[string]ModOldRecord {
	out := make(map[string]ModOldRecord)
	if d == nil {
		return out
	}
	rows, err := d.sql.Query("SELECT old_path, guid, old_name, old_version, new_name, new_version, marked_at FROM mod_old")
	if err != nil {
		return out
	}
	defer rows.Close()
	for rows.Next() {
		var r ModOldRecord
		var guid, oldName, oldVersion, newName, newVersion, markedAt sql.NullString
		if err = rows.Scan(&r.OldPath, &guid, &oldName, &oldVersion, &newName, &newVersion, &markedAt); err != nil {
			return out
		}
		r.Guid, r.OldName, r.OldVersion = guid.String, oldName.String, oldVersion.String
		r.NewName, r.NewVersion, r.MarkedAt = newName.String, newVersion.String, markedAt.String
		out[r.OldPath] = r
	}
	return out
}

// QueryModFileRecsByGuid 某 guid 在本库的副本记录（含大小 / 时间，按级别升序）——
// 对齐 C# Store.QueryModFileRecords（跨库重算 mod 主表用）。
func (d *DB) QueryModFileRecsByGuid(guid string) ([]ModFileRec, error) {
	rows, err := d.sql.Query("SELECT file_path, guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file WHERE guid=? ORDER BY tier", guid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileRec
	for rows.Next() {
		var r ModFileRec
		var guidVal, rootPath, fileName, mtime, scanTime sql.NullString
		var tier sql.NullInt64
		if err = rows.Scan(&r.FilePath, &guidVal, &tier, &rootPath, &fileName, &r.Size, &mtime, &scanTime); err != nil {
			return nil, err
		}
		r.Guid, r.Tier = guidVal.String, int(tier.Int64)
		r.RootPath, r.FileName, r.Mtime, r.ScanTime = rootPath.String, fileName.String, mtime.String, scanTime.String
		list = append(list, r)
	}
	return list, rows.Err()
}
