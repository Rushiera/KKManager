// Package store —— 「按作者整理」计划表（mod_sort_plan / mod_sort_plan_item）与
// mod_file 行的删除 / 插入（跨库搬移的源侧与目标侧）。
// 对齐 C# Data/Store.cs §AddSortPlan…§CountSortPlanItems / §RemoveModFileRow / §InsertModFileRow。
package store

import (
	"database/sql"
	"path/filepath"
	"time"
)

// SortPlanRow 整理计划头（主库表 mod_sort_plan）。
type SortPlanRow struct {
	ID            int64
	Scope         string
	CreatedAt     string
	ItemCount     int64
	ConflictCount int64
	State         string
	Note          string
}

// SortPlanItemRow 整理计划条目（主库表 mod_sort_plan_item，快照）。
type SortPlanItemRow struct {
	Seq      int64
	PlanID   int64
	Lib      int
	Tier     int
	RootPath string
	Guid     string
	Author   string
	Folder   string
	SrcPath  string
	DestPath string
	Size     int64
	Mtime    string
	State    string
	Note     string
}

// planTable 计划头表名（恒在主库）。
func (d *DB) planTable() string { return d.coreTable("mod_sort_plan") }

// planItemTable 计划条目表名（恒在主库）。
func (d *DB) planItemTable() string { return d.coreTable("mod_sort_plan_item") }

// AddSortPlan 建一条计划头（状态 building），返回计划 id；非主库连接返回 0。
func (d *DB) AddSortPlan(scope string) (int64, error) {
	if !d.isCore {
		return 0, errNilDB
	}
	stamp := time.Now().Format("2006-01-02 15:04:05")
	res, err := d.execer().Exec("INSERT INTO mod_sort_plan(scope,created_at,item_count,conflict_count,state,note) VALUES(?,?,0,0,'building','')", scope, stamp)
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

// ClearSortPlans 清空全部整理计划与条目（生成新计划前调用）。
func (d *DB) ClearSortPlans() error {
	if !d.isCore {
		return nil
	}
	if _, err := d.execer().Exec("DELETE FROM mod_sort_plan_item"); err != nil {
		return err
	}
	_, err := d.execer().Exec("DELETE FROM mod_sort_plan")
	return err
}

// AddSortPlanItem 写一条计划条目（快照）。
func (d *DB) AddSortPlanItem(row *SortPlanItemRow) error {
	if !d.isCore || row == nil {
		return nil
	}
	_, err := d.execer().Exec(`INSERT INTO mod_sort_plan_item(seq,plan_id,lib,tier,root_path,guid,author,folder,src_path,dest_path,size,mtime,state,note)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		row.Seq, row.PlanID, row.Lib, row.Tier, row.RootPath, row.Guid, row.Author, row.Folder,
		row.SrcPath, row.DestPath, row.Size, row.Mtime, row.State, row.Note)
	return err
}

// FinishSortPlan 计划收尾——写状态 / 条目数 / 冲突数 / 备注。
func (d *DB) FinishSortPlan(planID int64, state string, itemCount, conflictCount int64, note string) error {
	if !d.isCore {
		return nil
	}
	_, err := d.execer().Exec("UPDATE mod_sort_plan SET state=?,item_count=?,conflict_count=?,note=? WHERE id=?",
		state, itemCount, conflictCount, note, planID)
	return err
}

// SetSortItemState 改一条计划条目的状态与备注。
func (d *DB) SetSortItemState(planID, seq int64, state, note string) error {
	if !d.isCore {
		return nil
	}
	_, err := d.execer().Exec("UPDATE mod_sort_plan_item SET state=?,note=? WHERE plan_id=? AND seq=?",
		state, note, planID, seq)
	return err
}

// SetSortItemLocation 改一条计划条目的所在位置（判旧后条目随文件改指新位置）。
func (d *DB) SetSortItemLocation(planID, seq int64, lib, tier int, rootPath, folder, srcPath, destPath, state, note string) error {
	if !d.isCore {
		return nil
	}
	_, err := d.execer().Exec(`UPDATE mod_sort_plan_item SET lib=?,tier=?,root_path=?,folder=?,src_path=?,dest_path=?,state=?,note=?
		WHERE plan_id=? AND seq=?`,
		lib, tier, rootPath, folder, srcPath, destPath, state, note, planID, seq)
	return err
}

// LatestSortPlan 最新一条整理计划（无则 nil）。
func (d *DB) LatestSortPlan() (*SortPlanRow, error) {
	if !d.isCore {
		return nil, nil
	}
	row := d.execer().QueryRow("SELECT id,scope,created_at,item_count,conflict_count,state,note FROM mod_sort_plan ORDER BY id DESC LIMIT 1")
	return scanSortPlan(row)
}

// SortPlanByID 按 id 取一条整理计划（无则 nil）。
func (d *DB) SortPlanByID(planID int64) (*SortPlanRow, error) {
	if !d.isCore {
		return nil, nil
	}
	row := d.execer().QueryRow("SELECT id,scope,created_at,item_count,conflict_count,state,note FROM mod_sort_plan WHERE id=?", planID)
	return scanSortPlan(row)
}

// scanSortPlan 读一行计划头（sql.ErrNoRows 归一为 nil）。
func scanSortPlan(row *sql.Row) (*SortPlanRow, error) {
	var r SortPlanRow
	var scope, createdAt, state, note sql.NullString
	var itemCount, conflictCount sql.NullInt64
	if err := row.Scan(&r.ID, &scope, &createdAt, &itemCount, &conflictCount, &state, &note); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	r.Scope, r.CreatedAt = scope.String, createdAt.String
	r.ItemCount, r.ConflictCount = itemCount.Int64, conflictCount.Int64
	r.State, r.Note = state.String, note.String
	return &r, nil
}

// QuerySortPlanItems 某计划的条目（按展示顺序 seq）。
func (d *DB) QuerySortPlanItems(planID int64) ([]SortPlanItemRow, error) {
	if !d.isCore {
		return nil, nil
	}
	rows, err := d.execer().Query(`SELECT seq,plan_id,lib,tier,root_path,guid,author,folder,src_path,dest_path,size,mtime,state,note
		FROM mod_sort_plan_item WHERE plan_id=? ORDER BY seq`, planID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []SortPlanItemRow
	for rows.Next() {
		var r SortPlanItemRow
		var lib, tier, size sql.NullInt64
		var seq, pid sql.NullInt64
		var rootPath, guid, author, folder, srcPath, destPath, mtime, state, note sql.NullString
		if err = rows.Scan(&seq, &pid, &lib, &tier, &rootPath, &guid, &author, &folder,
			&srcPath, &destPath, &size, &mtime, &state, &note); err != nil {
			return nil, err
		}
		r.Seq, r.PlanID = seq.Int64, pid.Int64
		r.Lib, r.Tier = int(lib.Int64), int(tier.Int64)
		r.RootPath, r.Guid, r.Author, r.Folder = rootPath.String, guid.String, author.String, folder.String
		r.SrcPath, r.DestPath, r.Size, r.Mtime = srcPath.String, destPath.String, size.Int64, mtime.String
		r.State, r.Note = state.String, note.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// CountSortPlanItems 某计划里指定状态的条目数。
func (d *DB) CountSortPlanItems(planID int64, state string) (int64, error) {
	if !d.isCore {
		return 0, nil
	}
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM mod_sort_plan_item WHERE plan_id=? AND state=?", planID, state).Scan(&n)
	return n, err
}

// ModAuthorMap guid → 作者（主库 mod 主表；空作者为 ""）——按作者整理的来源。
func (d *DB) ModAuthorMap() (map[string]string, error) {
	rows, err := d.execer().Query("SELECT guid, COALESCE(author,'') FROM " + d.modTable())
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]string)
	for rows.Next() {
		var guid, author sql.NullString
		if err = rows.Scan(&guid, &author); err != nil {
			return nil, err
		}
		out[guid.String] = author.String
	}
	return out, rows.Err()
}

// RemoveModFileRow 删掉一条 mod 副本行，返回被删内容（找不到返回 nil）——跨库搬移的源侧用。
func (d *DB) RemoveModFileRow(filePath string) (*ModFileRec, error) {
	var r ModFileRec
	var guid sql.NullString
	var tier sql.NullInt64
	var rootPath, fileName, mtime, scanTime sql.NullString
	err := d.execer().QueryRow("SELECT guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file WHERE file_path=?",
		filePath).Scan(&guid, &tier, &rootPath, &fileName, &r.Size, &mtime, &scanTime)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	r.FilePath, r.Guid, r.Tier = filePath, guid.String, int(tier.Int64)
	r.RootPath, r.FileName, r.Mtime, r.ScanTime = rootPath.String, fileName.String, mtime.String, scanTime.String
	if _, err = d.execer().Exec("DELETE FROM mod_file WHERE file_path=?", filePath); err != nil {
		return nil, err
	}
	return &r, nil
}

// InsertModFileRow 插入一条 mod 副本行（跨库搬移的目标侧用）；existed 返回插入前是否已有行。
func (d *DB) InsertModFileRow(rec *ModFileRec, tier int, rootPath, newPath string) (bool, error) {
	if rec == nil {
		return false, nil
	}
	var n int64
	if err := d.execer().QueryRow("SELECT COUNT(*) FROM mod_file WHERE file_path=?", newPath).Scan(&n); err != nil {
		return false, err
	}
	existed := n > 0
	fileName := rec.FileName
	if fileName == "" {
		fileName = filepath.Base(newPath)
	}
	stamp := rec.ScanTime
	if stamp == "" {
		stamp = nowStampUTCStr()
	}
	_, err := d.execer().Exec(`INSERT OR REPLACE INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
		VALUES(?,?,?,?,?,?,?,?)`, newPath, rec.Guid, tier, rootPath, fileName, rec.Size, rec.Mtime, stamp)
	return existed, err
}

// nowStampUTCStr 当前 UTC 时间戳文本（对齐 C# Store.Now：yyyy-MM-ddTHH:mm:ss.fff）。
func nowStampUTCStr() string { return time.Now().UTC().Format("2006-01-02T15:04:05.000") }
