package store

import (
	"database/sql"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// CardMoveRow 卡片移动记录的一行（计划 → 认领 → 执行 → 导入消费）——
// 对齐 C# Data/Store.cs §CardMoveRow。
type CardMoveRow struct {
	Id         int64
	FromPath   string
	ToPath     string
	Work       string
	CardType   string
	State      string
	ClaimedBy  string
	ClaimedAt  string
	ExecutedAt string
	Consumed   bool
	Note       string
}

// CardDeleteRow 确认删除区记录的一行——删除动作只移入该区，物理删除由人手动完成。
type CardDeleteRow struct {
	Id        int64
	FromPath  string
	ToPath    string
	Size      int64
	State     string
	DeletedAt string
	Note      string
}

// cardMoveTable / cardDeleteTable 表在本次连接里的引用名（表恒在主库）。
func (d *DB) cardMoveTable() string   { return d.coreTable("card_move") }
func (d *DB) cardDeleteTable() string { return d.coreTable("card_delete") }

// UpsertCardMoves 批量写移动记录（plan 落盘）——同 from_path 已 done 且目标相同则保持，
// 其余重置 pending；返回写入条数。对齐 C# Store.UpsertCardMoves。
func (d *DB) UpsertCardMoves(rows []CardMoveRow) (int, error) {
	if len(rows) == 0 {
		return 0, nil
	}
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	tbl := d.cardMoveTable()
	sqlText := `INSERT INTO ` + tbl + `(from_path,to_path,work,card_type,state,note,claimed_by,claimed_at,executed_at,consumed)
		VALUES(?,?,?,?,'pending',?,NULL,NULL,NULL,0)
		ON CONFLICT(from_path) DO UPDATE SET
		  state=CASE WHEN ` + tbl + `.state IN ('done','skipped') AND ` + tbl + `.to_path=excluded.to_path THEN ` + tbl + `.state ELSE 'pending' END,
		  to_path=excluded.to_path, work=excluded.work, card_type=excluded.card_type,
		  note=CASE WHEN ` + tbl + `.state IN ('done','skipped') AND ` + tbl + `.to_path=excluded.to_path THEN ` + tbl + `.note ELSE excluded.note END`
	if err := d.Begin(); err != nil {
		return 0, err
	}
	n := 0
	for _, row := range rows {
		if _, err := d.execer().Exec(sqlText, row.FromPath, row.ToPath, nullStr(row.Work), nullStr(row.CardType), nullStr(row.Note)); err != nil {
			d.Rollback()
			return n, err
		}
		n++
	}
	if err := d.Commit(); err != nil {
		return n, err
	}
	return n, nil
}

// LoadCardMoves 按状态读移动记录（state 空 = 全部，按 id 升序）。
func (d *DB) LoadCardMoves(state string) ([]CardMoveRow, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return nil, err
	}
	sqlText := "SELECT id, from_path, to_path, work, card_type, state, claimed_by, claimed_at, executed_at, consumed, note FROM " + d.cardMoveTable()
	args := []interface{}{}
	if state != "" {
		sqlText += " WHERE state=?"
		args = append(args, state)
	}
	sqlText += " ORDER BY id"
	rows, err := d.execer().Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardMoveRow
	for rows.Next() {
		r, err := scanCardMove(rows)
		if err != nil {
			return nil, err
		}
		list = append(list, r)
	}
	return list, rows.Err()
}

// LoadCardMovesByFrom 按源路径找移动记录（回滚 / 状态查询）。
func (d *DB) LoadCardMovesByFrom(fromPath string) ([]CardMoveRow, error) {
	return d.loadCardMovesWhere("from_path", fromPath)
}

// LoadCardMovesByTo 按目标路径找移动记录（导入消费）。
func (d *DB) LoadCardMovesByTo(toPath string) ([]CardMoveRow, error) {
	return d.loadCardMovesWhere("to_path", toPath)
}

// loadCardMovesWhere 单列等值查询（列名白名单内）。
func (d *DB) loadCardMovesWhere(column, value string) ([]CardMoveRow, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return nil, err
	}
	rows, err := d.execer().Query("SELECT id, from_path, to_path, work, card_type, state, claimed_by, claimed_at, executed_at, consumed, note FROM "+d.cardMoveTable()+" WHERE "+column+"=? ORDER BY id", value)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardMoveRow
	for rows.Next() {
		r, err := scanCardMove(rows)
		if err != nil {
			return nil, err
		}
		list = append(list, r)
	}
	return list, rows.Err()
}

// scanCardMove 读一行移动记录（列序固定）。
func scanCardMove(rows *sql.Rows) (CardMoveRow, error) {
	var r CardMoveRow
	var fromPath, toPath, work, cardType, state, claimedBy, claimedAt, executedAt, note sql.NullString
	var consumed sql.NullInt64
	if err := rows.Scan(&r.Id, &fromPath, &toPath, &work, &cardType, &state, &claimedBy, &claimedAt, &executedAt, &consumed, &note); err != nil {
		return r, err
	}
	r.FromPath, r.ToPath, r.Work, r.CardType = fromPath.String, toPath.String, work.String, cardType.String
	r.State, r.ClaimedBy, r.ClaimedAt, r.ExecutedAt = state.String, claimedBy.String, claimedAt.String, executedAt.String
	r.Consumed, r.Note = consumed.Int64 != 0, note.String
	return r, nil
}

// ClaimCardMove 认领一条移动（pending → claimed，原子）——返回 true = 认领成功。
func (d *DB) ClaimCardMove(id int64, by string) (bool, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return false, err
	}
	res, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET state='claimed', claimed_by=?, claimed_at=? WHERE id=? AND state='pending'", by, nowStampUTCStr(), id)
	if err != nil {
		return false, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return false, err
	}
	return n == 1, nil
}

// FinishCardMove 结束一条移动（→ done / failed / skipped）。
func (d *DB) FinishCardMove(id int64, state, note string) error {
	if err := d.EnsureIndexTables(); err != nil {
		return err
	}
	_, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET state=?, executed_at=?, note=COALESCE(?, note) WHERE id=?", state, nowStampUTCStr(), nullStr(note), id)
	return err
}

// ReopenCardMove 回滚一条已完成移动——状态回 pending、清除认领与消费标记。
func (d *DB) ReopenCardMove(id int64) error {
	if err := d.EnsureIndexTables(); err != nil {
		return err
	}
	_, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET state='pending', consumed=0, claimed_by=NULL, claimed_at=NULL, executed_at=NULL WHERE id=?", id)
	return err
}

// ResetDeadLocalClaims 回收本机已死进程的认领（claimed_by 以本机名开头且 pid 不存在 → pending）；
// 返回回收条数。并发执行时存活进程的认领不动。对齐 C# Store.ResetDeadLocalClaims。
func (d *DB) ResetDeadLocalClaims(machineName string) (int, error) {
	claimed, err := d.LoadCardMoves("claimed")
	if err != nil {
		return 0, err
	}
	prefix := machineName + "/"
	n := 0
	for _, mv := range claimed {
		if !strings.HasPrefix(strings.ToLower(mv.ClaimedBy), strings.ToLower(prefix)) {
			continue
		}
		pid, convErr := strconv.Atoi(mv.ClaimedBy[len(prefix):])
		if convErr != nil {
			pid = 0
		}
		if pid > 0 && processAlive(pid) {
			continue
		}
		if _, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET state='pending', claimed_by=NULL, claimed_at=NULL WHERE id=?", mv.Id); err != nil {
			return n, err
		}
		n++
	}
	return n, nil
}

// ResetStaleCardClaims 回收超时的 claimed（认领超过 minutes 分钟 → pending）；返回回收条数。
func (d *DB) ResetStaleCardClaims(minutes int) (int, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	if minutes < 1 {
		minutes = 1
	}
	cutoff := stampAfterMinutesUTC(-minutes)
	res, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET state='pending', claimed_by=NULL, claimed_at=NULL WHERE state='claimed' AND (claimed_at IS NULL OR claimed_at < ?)", cutoff)
	if err != nil {
		return 0, err
	}
	n, err := res.RowsAffected()
	return int(n), err
}

// MarkCardMovesConsumed 标记 done 记录已被导入消费；返回标记条数。
func (d *DB) MarkCardMovesConsumed(ids []int64) (int, error) {
	if len(ids) == 0 {
		return 0, nil
	}
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	if err := d.Begin(); err != nil {
		return 0, err
	}
	n := 0
	for _, id := range ids {
		res, err := d.execer().Exec("UPDATE "+d.cardMoveTable()+" SET consumed=1 WHERE id=?", id)
		if err != nil {
			d.Rollback()
			return n, err
		}
		affected, err := res.RowsAffected()
		if err != nil {
			d.Rollback()
			return n, err
		}
		n += int(affected)
	}
	if err := d.Commit(); err != nil {
		return n, err
	}
	return n, nil
}

// InsertCardDelete 写一条确认删除区记录——删除动作的唯一落账方式（不物理删除）。
func (d *DB) InsertCardDelete(fromPath, toPath string, size int64, note string) error {
	if err := d.EnsureIndexTables(); err != nil {
		return err
	}
	_, err := d.execer().Exec("INSERT INTO "+d.cardDeleteTable()+"(from_path,to_path,size,state,deleted_at,note) VALUES(?,?,?,'done',?,?)",
		fromPath, toPath, size, localStampText(), note)
	return err
}

// LoadCardDeletes 按时间倒序读确认删除区记录（limit 0 = 全部）。
func (d *DB) LoadCardDeletes(limit int) ([]CardDeleteRow, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return nil, err
	}
	sqlText := "SELECT id, from_path, to_path, size, state, deleted_at, note FROM " + d.cardDeleteTable() + " ORDER BY id DESC"
	if limit > 0 {
		sqlText += " LIMIT " + strconv.Itoa(limit)
	}
	rows, err := d.execer().Query(sqlText)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardDeleteRow
	for rows.Next() {
		var r CardDeleteRow
		var fromPath, toPath, state, deletedAt, note sql.NullString
		var size sql.NullInt64
		if err = rows.Scan(&r.Id, &fromPath, &toPath, &size, &state, &deletedAt, &note); err != nil {
			return nil, err
		}
		r.FromPath, r.ToPath, r.State = fromPath.String, toPath.String, state.String
		r.Size, r.DeletedAt, r.Note = size.Int64, deletedAt.String, note.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// processAlive 进程是否存在（已退出 / 无权限 / 不存在都按不存在处理）。
func processAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	const processQueryLimitedInformation = 0x1000
	handle, err := syscall.OpenProcess(processQueryLimitedInformation, false, uint32(pid))
	if err != nil {
		return false
	}
	defer syscall.CloseHandle(handle)
	var code uint32
	if err := syscall.GetExitCodeProcess(handle, &code); err != nil {
		return false
	}
	const stillActive = 259
	return code == stillActive
}

// nullStr 空串落 NULL（与 C# AddWithValue 的 DBNull 形态对齐）。
func nullStr(s string) interface{} {
	if s == "" {
		return nil
	}
	return s
}

// stampAfterMinutesUTC 当前 UTC 时间加 minutes 分钟后的时间戳文本（负值 = 过去时刻；认领超时判定用）。
func stampAfterMinutesUTC(minutes int) string {
	return time.Now().Add(time.Duration(minutes) * time.Minute).UTC().Format(StampLayout)
}

// localStampText 本地时间文本（对齐 C# DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")）。
func localStampText() string {
	return time.Now().Format("2006-01-02 15:04:05")
}
