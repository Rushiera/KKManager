// 面板单条读面（G7）：按 id 取一张卡片行 / 一张卡片的编辑留档记录。
//
// 契约对齐 C# Store.GetCard / Store.ListCardEdits。
package store

import (
	"database/sql"
)

// GetCardListRow 按 id 取单张卡片行（面板卡片窗用）——对齐 C# Store.GetCard：
// 四色聚合走 colorCte（库文件经 core.mod）；hasThumb 由 thumb 非空判定（card 表无 has_thumb 列）。
// file_path 取出来给 CardPathOf 用（C# CardRow 不回传该字段，面板 JSON 不输出）。
func (d *DB) GetCardListRow(id int64) (*CardListRow, error) {
	modTable := "mod"
	if !d.isCore {
		if err := d.attachCore(d.core); err != nil {
			return nil, err
		}
		modTable = "core.mod"
	}
	row := d.execer().QueryRow(colorCte(modTable)+` SELECT c.id, c.file_path, c.file_name, c.card_type,
		c.size, c.mtime, c.root_path, c.tier, c.folder, c.mod_count,
		COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
		(c.thumb IS NOT NULL)
		FROM card c LEFT JOIN agg a ON a.card_id = c.id
		WHERE c.id = ?`, id)
	var r CardListRow
	var filePath, fileName, cardType, mtime, rootPath, folder sql.NullString
	var size, modCount, green, yellow, red, black, hasThumb sql.NullInt64
	if err := row.Scan(&r.Id, &filePath, &fileName, &cardType, &size, &mtime, &rootPath, &r.Tier, &folder,
		&modCount, &green, &yellow, &red, &black, &hasThumb); err != nil {
		return nil, err
	}
	r.FilePath, r.FileName = filePath.String, fileName.String
	r.CardType, r.CardTypeSet = cardType.String, cardType.Valid
	r.Size = size.Int64
	r.Mtime, r.MtimeSet = mtime.String, mtime.Valid
	r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
	r.Folder = folder.String
	r.ModCount = modCount.Int64
	r.Green, r.Yellow, r.Red, r.Black = green.Int64, yellow.Int64, red.Int64, black.Int64
	r.HasThumb = hasThumb.Int64 != 0
	return &r, nil
}

// CardEditRow 卡片编辑留档记录（对齐 C# Store.CardEditRow）。
type CardEditRow struct {
	// Id 记录号。
	Id int64
	// CardPath 卡片路径。
	CardPath string
	// CardName 卡片名。
	CardName string
	// Lib 库序号。
	Lib int
	// ArchivedFile 留档文件路径。
	ArchivedFile string
	// ArchivedSize 留档字节数。
	ArchivedSize int64
	// ArchivedMtime 留档文件修改时间。
	ArchivedMtime string
	// Changes 改动摘要。
	Changes string
	// EditedAt 编辑时刻。
	EditedAt string
}

// ListCardEdits 某张卡片的全部留档记录（时间倒序）——「寻找旧版」数据面。
func (d *DB) ListCardEdits(cardPath string) ([]CardEditRow, error) {
	rows, err := d.execer().Query(`SELECT id, card_path, card_name, lib, archived_file, archived_size, archived_mtime, changes, edited_at
		FROM `+d.coreTable("card_edit")+` WHERE card_path=? ORDER BY id DESC`, cardPath)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	list := make([]CardEditRow, 0, 4)
	for rows.Next() {
		var r CardEditRow
		var cardName, archivedFile, archivedMtime, changes, editedAt sql.NullString
		var lib, archivedSize sql.NullInt64
		if err = rows.Scan(&r.Id, &r.CardPath, &cardName, &lib, &archivedFile, &archivedSize, &archivedMtime, &changes, &editedAt); err != nil {
			return nil, err
		}
		r.CardName, r.Lib = cardName.String, int(lib.Int64)
		r.ArchivedFile, r.ArchivedSize = archivedFile.String, archivedSize.Int64
		r.ArchivedMtime, r.Changes, r.EditedAt = archivedMtime.String, changes.String, editedAt.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// AddCardEdit 追加一条卡片编辑留档记录。
func (d *DB) AddCardEdit(cardPath, cardName string, lib int, archivedFile string, archivedSize int64, archivedMtime, changes, editedAt string) error {
	_, err := d.execer().Exec("INSERT INTO "+d.coreTable("card_edit")+
		"(card_path,card_name,lib,archived_file,archived_size,archived_mtime,changes,edited_at) VALUES(?,?,?,?,?,?,?,?)",
		cardPath, cardName, lib, archivedFile, archivedSize, archivedMtime, changes, editedAt)
	return err
}

// UpdateCardSize 更新一张卡片记录的字节数——编辑后文件长度变了；修改时间按最小改动原则还原，故只更新字节数。
func (d *DB) UpdateCardSize(id, size int64) error {
	_, err := d.execer().Exec("UPDATE card SET size=? WHERE id=?", size, id)
	return err
}

// QueryCardRefs 某张卡的引用明细（对齐 C# Store.QueryCardRefs）——
// 同 guid 多部件聚合为一行（MIN(property) 等），tier 按跨库最优；tierFilter 0=不过滤，1/2/3/9 过滤。
func (d *DB) QueryCardRefs(cardId int64, tierFilter int) ([]RefRow, error) {
	modTable := "mod"
	if !d.isCore {
		if err := d.attachCore(d.core); err != nil {
			return nil, err
		}
		modTable = "core.mod"
	}
	having := ""
	switch tierFilter {
	case 1:
		having = " HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 1"
	case 2:
		having = " HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 2"
	case 3:
		having = " HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 3"
	case 9:
		having = " HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 9"
	}
	sqlText := `SELECT cm.mod_guid, MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) AS tier,
		MIN(COALESCE(m.root_path,'')), MIN(COALESCE(m.file_path,'')), MIN(COALESCE(m.file_name,'')),
		MIN(COALESCE(cm.property,'')), MIN(COALESCE(cm.rec_name,'')), MIN(COALESCE(cm.rec_author,'')),
		MIN(COALESCE(cm.rec_website,''))
		FROM card_mod cm LEFT JOIN ` + modTable + ` m ON m.guid = cm.mod_guid
		WHERE cm.card_id = ? GROUP BY cm.mod_guid` + having + ` ORDER BY tier, cm.mod_guid`
	rows, err := d.execer().Query(sqlText, cardId)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	list := make([]RefRow, 0, 16)
	for rows.Next() {
		var r RefRow
		if err = rows.Scan(&r.Guid, &r.Tier, &r.RootPath, &r.FilePath, &r.FileName,
			&r.Property, &r.RecName, &r.RecAuthor, &r.RecWebsite); err != nil {
			return nil, err
		}
		list = append(list, r)
	}
	return list, rows.Err()
}
