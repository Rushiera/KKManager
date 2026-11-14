// Package store 主库最小读写——G3 存储段的第一块砖。
//
// 当前只覆盖 setting 表（库根配置 / 扫描计划都落在这一张表里），
// 余下（schema 对齐 db-template.db / 分片库 / 预热 / 增量戳）待 G3 后续补。
package store

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	_ "modernc.org/sqlite"
)

// DB 主库句柄（对应 C# 侧 Store 的 core 库）。
type DB struct {
	sql  *sql.DB
	path string
}

// Open 打开主库并启用与 C# 一致的连接参数（WAL · synchronous=NORMAL · busy_timeout 15 s）。
// 文件不存在时建最小 schema（只有 setting 表）并出声——C# 侧是从内嵌模板释放全量 schema，Go 侧待 G3 补。
func Open(path string) (*DB, error) {
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	fresh := false
	if _, statErr := os.Stat(abs); statErr != nil {
		fresh = true
		if dir := filepath.Dir(abs); dir != "" {
			if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
				return nil, mkErr
			}
		}
	}
	handle, err := sql.Open("sqlite", "file:"+strings.ReplaceAll(abs, "\\", "/"))
	if err != nil {
		return nil, err
	}
	handle.SetMaxOpenConns(1)
	for _, pragma := range []string{
		"PRAGMA journal_mode=WAL",
		"PRAGMA synchronous=NORMAL",
		"PRAGMA busy_timeout=15000",
	} {
		if _, err = handle.Exec(pragma); err != nil {
			handle.Close()
			return nil, err
		}
	}
	db := &DB{sql: handle, path: abs}
	if fresh {
		fmt.Println("[库] 主库不存在——按最小 schema 新建（只有 setting 表；全量 schema 待 G3 从模板释放）：" + abs)
		if err = db.ensureSetting(); err != nil {
			handle.Close()
			return nil, err
		}
	}
	return db, nil
}

// Path 主库文件绝对路径（对齐 C# 的 StoreHub.CorePath）。
func (d *DB) Path() string { return d.path }

// Close 关闭句柄。
func (d *DB) Close() error { return d.sql.Close() }

// ensureSetting 建 setting 表（key 主键 / value 文本）。
func (d *DB) ensureSetting() error {
	_, err := d.sql.Exec("CREATE TABLE IF NOT EXISTS setting(key TEXT PRIMARY KEY, value TEXT)")
	return err
}

// GetSetting 读一个设置项；不存在返回空串与 false。
func (d *DB) GetSetting(key string) (string, bool, error) {
	var value sql.NullString
	err := d.sql.QueryRow("SELECT value FROM setting WHERE key=?", key).Scan(&value)
	if err == sql.ErrNoRows {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	return value.String, true, nil
}

// SetSetting 写一个设置项（存在即覆盖）。
func (d *DB) SetSetting(key, value string) error {
	_, err := d.sql.Exec(`INSERT INTO setting(key,value) VALUES(?,?)
		ON CONFLICT(key) DO UPDATE SET value=excluded.value`, key, value)
	return err
}
