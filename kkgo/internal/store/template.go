// 模板释放与结构对齐——对齐 C# Data/Store.cs §ReleaseTemplate / §EnsureColumns / §EnsureIndexTables。
//
// 库文件（mod 单片库 / 卡片分片库）由内嵌模板零 DDL 释放（模板 user_version=2，结构已就绪）；
// 主库在模板之上补五张主库表（idx_meta / index_source / card_rule / card_move / card_delete）。
// 全部动作幂等：列已存在跳到下一列，表已存在 CREATE IF NOT EXISTS 空转。
package store

import (
	_ "embed"
	"fmt"
	"os"
	"path/filepath"
)

// templateDB 内嵌空结构模板库——来源 KKManager/Resources/db-template.db（C# Store.TemplateResource 同一份）。
//
//go:embed resources/db-template.db
var templateDB []byte

// SchemaVersion 结构版本（与 C# Store.SchemaVersion 同值）——模板 / 新库的 user_version。
const SchemaVersion = 2

// TemplateSize 内嵌模板的字节数（自检 / 出声用）。
func TemplateSize() int { return len(templateDB) }

// ReleaseTemplate 把内嵌模板释放到目标路径（已存在返回 false 不覆盖；资源缺失 / 写失败出声并返回 false）。
// 写临时文件再改名——写一半中断不留下半截库文件。
func ReleaseTemplate(path string) bool {
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	if info, statErr := os.Stat(abs); statErr == nil && !info.IsDir() {
		return false
	}
	if dir := filepath.Dir(abs); dir != "" {
		if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
			fmt.Println("[库数据] 模板释放失败（" + abs + "）：" + mkErr.Error() + "——该库按最小结构新建")
			return false
		}
	}
	if len(templateDB) == 0 {
		fmt.Println("[库数据] 内嵌模板缺失——该库按最小结构新建")
		return false
	}
	tmp := abs + ".tmp"
	if wErr := os.WriteFile(tmp, templateDB, 0o644); wErr != nil {
		fmt.Println("[库数据] 模板释放失败（" + abs + "）：" + wErr.Error() + "——该库按最小结构新建")
		return false
	}
	if rErr := os.Rename(tmp, abs); rErr != nil {
		fmt.Println("[库数据] 模板释放失败（" + abs + "）：" + rErr.Error() + "——该库按最小结构新建")
		os.Remove(tmp)
		return false
	}
	return true
}

// schemaVersion 读 user_version。
func (d *DB) schemaVersion() (int64, error) {
	var v int64
	err := d.sql.QueryRow("PRAGMA user_version").Scan(&v)
	return v, err
}

// setSchemaVersion 写 user_version。
func (d *DB) setSchemaVersion() error {
	_, err := d.sql.Exec(fmt.Sprintf("PRAGMA user_version=%d", SchemaVersion))
	return err
}

// tableExists 表是否存在（sqlite_master 查询；索引 / 视图不算）。
func (d *DB) tableExists(table string) (bool, error) {
	var n int64
	err := d.sql.QueryRow("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", table).Scan(&n)
	if err != nil {
		return false, err
	}
	return n > 0, nil
}

// hasColumn 列是否存在。
func (d *DB) hasColumn(table, column string) (bool, error) {
	var n int64
	err := d.sql.QueryRow("SELECT COUNT(*) FROM pragma_table_info('"+table+"') WHERE name=?", column).Scan(&n)
	if err != nil {
		return false, err
	}
	return n > 0, nil
}

// ensureColumn 缺列则补（表不存在时跳过——该表由建表路径负责；列已在则不动）。
func (d *DB) ensureColumn(table, column, typ string) error {
	exists, err := d.tableExists(table)
	if err != nil || !exists {
		return err
	}
	has, err := d.hasColumn(table, column)
	if err != nil || has {
		return err
	}
	_, err = d.sql.Exec("ALTER TABLE " + table + " ADD COLUMN " + column + " " + typ)
	return err
}

// EnsureColumns 增量补列与表迁移（幂等）——对齐 C# Store.EnsureColumns / §MigrateTodoTable。
// 主库与库文件共用：card.chara_name 两侧都有 card 表；其余列只在主库。
func (d *DB) EnsureColumns() error {
	if err := d.ensureColumn("card", "chara_name", "TEXT"); err != nil {
		return err
	}
	if !d.isCore {
		return nil
	}
	if err := d.ensureColumn("mod_composition", "texts", "TEXT"); err != nil {
		return err
	}
	// plugin_file 程序集元数据八列（v0.20.10）
	for _, col := range []string{"title", "description", "company", "copyright", "product", "file_version", "target_framework", "note"} {
		if err := d.ensureColumn("plugin_file", col, "TEXT"); err != nil {
			return err
		}
	}
	return d.migrateTodoIfNeeded()
}

// migrateTodoIfNeeded todo 表迁移（v0.20.2）：旧表没有 text 列时建新表搬运行，
// 并把「同类型同键只留一条」的唯一性缩到只对 offline 生效。行数不一致即回滚并报错（不静默丢待办）。
func (d *DB) migrateTodoIfNeeded() error {
	exists, err := d.tableExists("todo")
	if err != nil || !exists {
		return err
	}
	has, err := d.hasColumn("todo", "text")
	if err != nil || has {
		return err
	}
	var before int64
	if err = d.sql.QueryRow("SELECT COUNT(*) FROM todo").Scan(&before); err != nil {
		return err
	}
	tx, err := d.sql.Begin()
	if err != nil {
		return err
	}
	rollback := func(cause error) error {
		tx.Rollback()
		return cause
	}
	steps := []string{
		`CREATE TABLE todo_new(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 kind TEXT, key TEXT, text TEXT, ref_path TEXT, ref_name TEXT, created_at TEXT)`,
		"INSERT INTO todo_new(id, kind, key, created_at) SELECT id, kind, key, created_at FROM todo",
	}
	for _, stmt := range steps {
		if _, err = tx.Exec(stmt); err != nil {
			return rollback(err)
		}
	}
	var after int64
	if err = tx.QueryRow("SELECT COUNT(*) FROM todo_new").Scan(&after); err != nil {
		return rollback(err)
	}
	if after != before {
		return rollback(fmt.Errorf("todo 表迁移行数不一致：%d → %d", before, after))
	}
	for _, stmt := range []string{
		"DROP TABLE todo",
		"ALTER TABLE todo_new RENAME TO todo",
		"CREATE UNIQUE INDEX IF NOT EXISTS ux_todo_offline ON todo(kind, key) WHERE kind = 'offline'",
	} {
		if _, err = tx.Exec(stmt); err != nil {
			return rollback(err)
		}
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	fmt.Println("[库数据] todo 表已迁移到 v0.20.2 结构（补 text / ref_path / ref_name 三列）")
	return nil
}

// EnsureCoreTables 补主库独有的五张表与索引（幂等）——对齐 C# Store.EnsureIndexTables。
// 模板库文件只需要基础表；主库要额外的整理链表与索引参考层（G5 / G6 写面用）。
func (d *DB) EnsureCoreTables() error {
	if !d.isCore {
		return nil
	}
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS idx_meta(
			 file_path TEXT PRIMARY KEY, idx_id TEXT, kind TEXT, card_type TEXT, chara TEXT,
			 work TEXT, author TEXT, source TEXT, src_rel TEXT, note TEXT, guid TEXT,
			 series TEXT, vn_id TEXT, vn_name TEXT, vndb_chara_id TEXT, chara_id_conf TEXT,
			 category TEXT, sub_category TEXT)`,
		`CREATE TABLE IF NOT EXISTS index_source(
			 root_path TEXT PRIMARY KEY, index_dir TEXT, format TEXT,
			 rows INTEGER, cards INTEGER, mods INTEGER, others INTEGER,
			 digest TEXT, imported_at TEXT)`,
		`CREATE TABLE IF NOT EXISTS card_rule(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 kind TEXT NOT NULL, match TEXT NOT NULL, value TEXT NOT NULL,
			 origin TEXT, votes INTEGER, works INTEGER, updated_at TEXT,
			 enabled INTEGER NOT NULL DEFAULT 1,
			 UNIQUE(kind, match))`,
		`CREATE TABLE IF NOT EXISTS card_move(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 from_path TEXT NOT NULL UNIQUE, to_path TEXT NOT NULL,
			 work TEXT, card_type TEXT,
			 state TEXT NOT NULL DEFAULT 'pending',
			 claimed_by TEXT, claimed_at TEXT, executed_at TEXT,
			 consumed INTEGER NOT NULL DEFAULT 0, note TEXT)`,
		`CREATE TABLE IF NOT EXISTS card_delete(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 from_path TEXT NOT NULL, to_path TEXT NOT NULL,
			 size INTEGER, state TEXT NOT NULL DEFAULT 'done',
			 deleted_at TEXT, note TEXT)`,
	}
	for _, stmt := range stmts {
		if _, err := d.sql.Exec(stmt); err != nil {
			return err
		}
	}
	// idx_meta 参考层七列（旧库不重建表）
	for _, col := range []string{"series", "vn_id", "vn_name", "vndb_chara_id", "chara_id_conf", "category", "sub_category"} {
		if err := d.ensureColumn("idx_meta", col, "TEXT"); err != nil {
			return err
		}
	}
	return nil
}

// tables 当前库的表名集合（自检用——对齐判据「表名集合与模板相同」）。
func (d *DB) tables() ([]string, error) {
	rows, err := d.sql.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []string
	for rows.Next() {
		var name string
		if err = rows.Scan(&name); err != nil {
			return nil, err
		}
		list = append(list, name)
	}
	return list, rows.Err()
}

// TableNames 对外暴露表名集合（自检命令用）。
func (d *DB) TableNames() ([]string, error) { return d.tables() }
