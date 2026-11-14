package store

// mod_hash 档案（重复副本 MD5 缓存）——对齐 C# Data/Store.cs 的 ModHashRecord 面。
// 表恒在主库：库文件连接走 core 前缀（coreTable）。

// ModHashRecord 一条 MD5 档案（键 = 文件绝对路径）。
type ModHashRecord struct {
	// FilePath 文件绝对路径（主键）。
	FilePath string
	// Size 建档时的文件字节数（命中判据之一）。
	Size int64
	// Mtime 建档时的修改时间戳（UTC 文本，命中判据之二）。
	Mtime string
	// Md5 小写十六进制内容哈希。
	Md5 string
	// HashedAt 建档时刻（UTC 文本）。
	HashedAt string
}

// LoadModHashes 读全部 MD5 档案（键 = 绝对路径，Ordinal 大小写敏感）——对齐 C# Store.LoadModHashes。
func (d *DB) LoadModHashes() (map[string]ModHashRecord, error) {
	rows, err := d.execer().Query("SELECT file_path, IFNULL(size,0), IFNULL(mtime,''), IFNULL(md5,''), IFNULL(hashed_at,'') FROM " + d.coreTable("mod_hash"))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]ModHashRecord, 64)
	for rows.Next() {
		var r ModHashRecord
		if err = rows.Scan(&r.FilePath, &r.Size, &r.Mtime, &r.Md5, &r.HashedAt); err != nil {
			return nil, err
		}
		out[r.FilePath] = r
	}
	return out, rows.Err()
}

// PutModHash 写一条 MD5 档案（按路径覆盖）——对齐 C# Store.PutModHash。
func (d *DB) PutModHash(r ModHashRecord) error {
	_, err := d.execer().Exec("INSERT OR REPLACE INTO "+d.coreTable("mod_hash")+"(file_path,size,mtime,md5,hashed_at) VALUES(?,?,?,?,?)",
		r.FilePath, r.Size, r.Mtime, r.Md5, r.HashedAt)
	return err
}
