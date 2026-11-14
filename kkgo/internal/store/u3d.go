// mod_u3d 表读写（unity3d 贴图解析档案）——对齐 C# Data/Store.cs §SaveU3d / §LoadU3d / §LoadU3dMap。
//
// 表恒在主库：库文件连接走 core 前缀。
package store

import "database/sql"

// ModU3dRow 一条 unity3d 解析档案。
type ModU3dRow struct {
	// Guid mod 的 manifest guid。
	Guid string
	// EntryPath 容器内条目路径。
	EntryPath string
	// FilePath 建档时读的副本绝对路径。
	FilePath string
	// Size 副本字节数（失效判据之一）。
	Size int64
	// Mtime 副本修改时间（失效判据之一）。
	Mtime string
	// TextureCount 贴图张数。
	TextureCount int64
	// Textures 贴图清单文本（每行 pathID\t名称\t宽\t高\t格式\t数据字节）。
	Textures string
	// ClassSummary 类型摘要（保留字段）。
	ClassSummary string
	// ParsedAt 建档时刻。
	ParsedAt string
	// Cached 本次是否命中已有档案（不落库）。
	Cached bool
}

// u3dTable mod_u3d 表在本次连接里的引用名。
func (d *DB) u3dTable() string {
	if d.isCore {
		return "mod_u3d"
	}
	return "core.mod_u3d"
}

// SaveU3dRow 写一条解析档案（guid + entry_path 为唯一键）。
func (d *DB) SaveU3dRow(r *ModU3dRow) error {
	_, err := d.execer().Exec(`INSERT INTO `+d.u3dTable()+`(guid,entry_path,file_path,size,mtime,texture_count,textures,class_summary,parsed_at)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(guid,entry_path) DO UPDATE SET
		  file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
		  texture_count=excluded.texture_count, textures=excluded.textures,
		  class_summary=excluded.class_summary, parsed_at=excluded.parsed_at`,
		r.Guid, r.EntryPath, r.FilePath, r.Size, r.Mtime, r.TextureCount, r.Textures, r.ClassSummary, r.ParsedAt)
	return err
}

// LoadU3dRow 读一条解析档案；没有返回 nil。
func (d *DB) LoadU3dRow(guid, entryPath string) (*ModU3dRow, error) {
	row := d.execer().QueryRow("SELECT guid,entry_path,file_path,size,mtime,texture_count,textures,class_summary,parsed_at FROM "+
		d.u3dTable()+" WHERE guid=? AND entry_path=?", guid, entryPath)
	var r ModU3dRow
	var filePath, mtime, textures, classSummary, parsedAt sql.NullString
	var size, textureCount sql.NullInt64
	if err := row.Scan(&r.Guid, &r.EntryPath, &filePath, &size, &mtime, &textureCount, &textures, &classSummary, &parsedAt); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	r.FilePath, r.Mtime, r.Textures = filePath.String, mtime.String, textures.String
	r.ClassSummary, r.ParsedAt = classSummary.String, parsedAt.String
	r.Size, r.TextureCount = size.Int64, textureCount.Int64
	return &r, nil
}

// LoadU3dMap 读某 guid 的全部解析档案（条目路径 → 档案）——组成区「已解析」标蓝判定用。
func (d *DB) LoadU3dMap(guid string) (map[string]*ModU3dRow, error) {
	rows, err := d.execer().Query("SELECT guid,entry_path,file_path,size,mtime,texture_count,textures,class_summary,parsed_at FROM "+
		d.u3dTable()+" WHERE guid=?", guid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]*ModU3dRow{}
	for rows.Next() {
		var r ModU3dRow
		var filePath, mtime, textures, classSummary, parsedAt sql.NullString
		var size, textureCount sql.NullInt64
		if err = rows.Scan(&r.Guid, &r.EntryPath, &filePath, &size, &mtime, &textureCount, &textures, &classSummary, &parsedAt); err != nil {
			return out, err
		}
		r.FilePath, r.Mtime, r.Textures = filePath.String, mtime.String, textures.String
		r.ClassSummary, r.ParsedAt = classSummary.String, parsedAt.String
		r.Size, r.TextureCount = size.Int64, textureCount.Int64
		out[r.EntryPath] = &r
	}
	return out, rows.Err()
}
