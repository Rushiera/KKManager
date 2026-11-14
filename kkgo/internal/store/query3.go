// 面板单条读面（G7）：按 id 取一张卡片行 / 一张卡片的编辑留档记录。
//
// 契约对齐 C# Store.GetCard / Store.ListCardEdits。
package store

import (
	"database/sql"
)

// GetCardListRow 按 id 取单张卡片行（面板卡片窗用）。
// 四色计数（Green/Yellow/Red/Black）由调用方按 card_mod 现算或留零——本方法只出卡片自身字段。
func (d *DB) GetCardListRow(id int64) (*CardListRow, error) {
	row := d.execer().QueryRow(`SELECT id, file_path, file_name, card_type, size, mtime, root_path, tier, folder, has_thumb, chara_name
		FROM card WHERE id=?`, id)
	var r CardListRow
	var filePath, cardType, mtime, rootPath, folder, charaName sql.NullString
	var size, hasThumb sql.NullInt64
	if err := row.Scan(&r.Id, &filePath, &cardType, &size, &mtime, &rootPath, &r.Tier, &folder, &hasThumb, &charaName); err != nil {
		return nil, err
	}
	r.FilePath = filePath.String
	r.FileName = ""
	r.CardType, r.CardTypeSet = cardType.String, cardType.Valid
	r.Size = size.Int64
	r.Mtime, r.MtimeSet = mtime.String, mtime.Valid
	r.RootPath, r.RootPathSet = rootPath.String, rootPath.Valid
	r.Folder = folder.String
	r.HasThumb = hasThumb.Int64 != 0
	r.CharaName, r.CharaNameSet = charaName.String, charaName.Valid
	// file_name 单独取（上面 Scan 顺序里已含）
	var fileName sql.NullString
	if err := d.execer().QueryRow("SELECT file_name FROM card WHERE id=?", id).Scan(&fileName); err == nil {
		r.FileName = fileName.String
	}
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
