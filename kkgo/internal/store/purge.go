// 库根级清理（换根 / 重建索引用）——对齐 C# Data/Store.cs §DeleteRecordsUnderRoot / §PurgeSkipRows。
//
// 清理面固定五表：card / mod_file / non_card / idx_meta / scan_state（card_mod 先于 card 删，
// card_id 是关联键，次序反了子查询取不到旧卡 id）。跳过行不带库根前缀，由 PurgeSkipRows 单独清。
package store

import "strings"

// DeleteRecordsUnderRoot 清理某库根前缀下的索引相关记录（换根 / 重建索引用）——
// card / mod_file / non_card / idx_meta / scan_state；返回受影响的 mod guid 清单。
// 对齐 C# Store.DeleteRecordsUnderRoot：路径前缀匹配 idx_meta / scan_state 时用「前缀 + \%」。
func (d *DB) DeleteRecordsUnderRoot(rootPath string) ([]string, error) {
	guids := []string{}
	prefix := RootKey(rootPath)
	if prefix == "" {
		return guids, nil
	}
	rows, err := d.execer().Query("SELECT DISTINCT guid FROM mod_file WHERE root_path=?", prefix)
	if err != nil {
		return guids, err
	}
	for rows.Next() {
		var guid *string
		if err = rows.Scan(&guid); err != nil {
			rows.Close()
			return guids, err
		}
		if guid != nil {
			guids = append(guids, *guid)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return guids, err
	}
	stmts := []string{
		// 🔴 card_mod 先于 card 删（card_id 是关联键；次序反了子查询取不到旧卡 id）
		"DELETE FROM card_mod WHERE card_id IN (SELECT id FROM card WHERE root_path=?)",
		"DELETE FROM card WHERE root_path=?",
		"DELETE FROM mod_file WHERE root_path=?",
		"DELETE FROM non_card WHERE root_path=?",
		"DELETE FROM idx_meta WHERE file_path LIKE ?",
		"DELETE FROM scan_state WHERE file_path LIKE ?",
	}
	// 与 C# 同形：前缀 + 反斜杠 + 通配符（默认 LIKE 语序；反斜杠是字面量，不是转义符）
	like := prefix + `\%`
	for _, stmt := range stmts {
		arg := prefix
		if strings.Contains(stmt, "file_path LIKE") {
			arg = like
		}
		if _, err = d.execer().Exec(stmt, arg); err != nil {
			return guids, err
		}
	}
	return guids, nil
}

// PurgeSkipRows 清理索引跳过行（idx_meta.file_path = "[跳过]" + 索引 ID）——导入前清理专用：
// 跳过行不带库根前缀，按根前缀清理（DeleteRecordsUnderRoot）清不到；重导索引会重写。
// 返回删除行数。对齐 C# Store.PurgeSkipRows。
func (d *DB) PurgeSkipRows() (int, error) {
	res, err := d.execer().Exec("DELETE FROM idx_meta WHERE file_path LIKE '[跳过]%'")
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return int(n), nil
}
