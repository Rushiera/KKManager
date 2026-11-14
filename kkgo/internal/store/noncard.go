package store

import "database/sql"

// GetNonCardRow 按 id 读一条非卡登记（搬运前定位用）；不存在返回 nil——对齐 C# Store.GetNonCard。
func (d *DB) GetNonCardRow(id int64) (*NonCardRow, error) {
	var r NonCardRow
	var movedTo sql.NullString
	var hasThumb sql.NullInt64
	err := d.execer().QueryRow("SELECT id,file_path,root_path,side,reason,size,mtime,moved_to,(thumb IS NOT NULL) FROM non_card WHERE id=?", id).
		Scan(&r.Id, &r.FilePath, &r.RootPath, &r.Side, &r.Reason, &r.Size, &r.Mtime, &movedTo, &hasThumb)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	r.MovedTo = movedTo.String
	r.HasThumb = hasThumb.Int64 != 0
	return &r, nil
}

// MarkNonCardMoved 把一条非卡登记改指搬走后的新路径（保留「已搬走」标记与缩略图；目标路径若已有登记先删掉）。
// 对齐 C# Store.MarkNonCardMoved。
func (d *DB) MarkNonCardMoved(id int64, dest, now string) error {
	if id <= 0 || dest == "" {
		return nil
	}
	if _, err := d.execer().Exec("DELETE FROM non_card WHERE file_path=? AND id<>?", dest, id); err != nil {
		return err
	}
	_, err := d.execer().Exec("UPDATE non_card SET file_path=?, moved_to=?, moved_at=? WHERE id=?", dest, dest, now, id)
	return err
}
