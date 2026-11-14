package store

import "os"

// UpdateCardPath 把一张卡片的路径从 oldPath 改到 newPath（回滚用）——
// 同库内直接改行；目标分片不同则返回 false（调用方出声「DB 行未迁移」）。
// 对齐 C# StoreHub.MoveCardRow 的「同库改名」分支；跨分片搬行由 index-import 负责。
func (d *DB) UpdateCardPath(oldPath, newPath string) (bool, error) {
	if _, err := os.Stat(newPath); err != nil {
		return false, nil
	}
	res, err := d.execer().Exec("UPDATE card SET file_path=?, file_name=? WHERE file_path=?", newPath, baseName(newPath), oldPath)
	if err != nil {
		return false, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return false, err
	}
	return n > 0, nil
}

// baseName 取路径末段（与 filepath.Base 同义，避免在本文件再引一次 filepath）。
func baseName(p string) string {
	for i := len(p) - 1; i >= 0; i-- {
		if p[i] == '\\' || p[i] == '/' {
			return p[i+1:]
		}
	}
	return p
}
