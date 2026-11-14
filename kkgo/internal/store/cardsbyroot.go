package store

import "database/sql"

// CardIdPath 一张卡的编号 + 路径（逐卡操作用）——对齐 C# QueryCardIdsByRoot 的 KeyValuePair 形态。
type CardIdPath struct {
	// Id 卡片行 id（分片内自增）。
	Id int64
	// FilePath 卡片绝对路径。
	FilePath string
}

// QueryCardIdsByRoot 某库根下的卡片行（id + 路径，按路径升序）——对齐 C# Store.QueryCardIdsByRoot。
// pendingStep 非空时只取该步骤无完成戳的卡（分批续跑）。
func (d *DB) QueryCardIdsByRoot(rootPath, pendingStep string) ([]CardIdPath, error) {
	sqlText := "SELECT c.id, c.file_path FROM card c WHERE c.root_path=?"
	args := []interface{}{trimTrailSlash(rootPath)}
	if pendingStep != "" {
		sqlText += " AND NOT EXISTS (SELECT 1 FROM scan_state s WHERE s.file_path=c.file_path AND s.step=?)"
		args = append(args, pendingStep)
	}
	sqlText += " ORDER BY c.file_path"
	rows, err := d.execer().Query(sqlText, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []CardIdPath
	for rows.Next() {
		var one CardIdPath
		var path sql.NullString
		if err = rows.Scan(&one.Id, &path); err != nil {
			return nil, err
		}
		one.FilePath = path.String
		list = append(list, one)
	}
	return list, rows.Err()
}

// trimTrailSlash 去掉路径尾部的分隔符（库根比较口径，对齐 C# TrimEnd('\\','/')）。
func trimTrailSlash(p string) string {
	for len(p) > 0 {
		last := p[len(p)-1]
		if last != '\\' && last != '/' {
			break
		}
		p = p[:len(p)-1]
	}
	return p
}
