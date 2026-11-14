// 旧版登记表（mod_old）的写面——判旧 / 换版本 / 正名三条链共用。
//
// 契约对齐 C# Data/Store.cs §SetModOld / §DeleteModOld：表恒在主库（库文件连接走 core 前缀）。
package store

// SetModOld 写一条旧版登记（按 old_path 覆盖）——对齐 C# Store.SetModOld。
func (d *DB) SetModOld(r ModOldRecord) error {
	if d == nil {
		return errNilDB
	}
	_, err := d.execer().Exec("INSERT OR REPLACE INTO "+d.coreTable("mod_old")+
		"(old_path,guid,old_name,old_version,new_name,new_version,marked_at) VALUES(?,?,?,?,?,?,?)",
		r.OldPath, r.Guid, r.OldName, r.OldVersion, r.NewName, r.NewVersion, r.MarkedAt)
	return err
}

// DeleteModOld 删一条旧版登记（正名 / 互换后销账）——对齐 C# Store.DeleteModOld。
func (d *DB) DeleteModOld(oldPath string) error {
	if d == nil {
		return errNilDB
	}
	_, err := d.execer().Exec("DELETE FROM "+d.coreTable("mod_old")+" WHERE old_path=?", oldPath)
	return err
}
