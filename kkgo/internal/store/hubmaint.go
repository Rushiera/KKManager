// 多库维护（库根迁移 / 卡片行跨分片搬移）——对齐 C# Data/StoreHub.cs
// §RekeyRoot / §MoveCardRow。
//
// 分层：store 只做单库读写与跨库句柄操作；跨库编排（哪些记录该搬）归 core。
package store

import (
	"database/sql"
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
)

// RekeyRoot 库根换路径时迁移库序号映射——旧路径的库号搬到新路径
// （否则 LibOf 给新路径分配新号，数据成孤岛）。对齐 C# StoreHub.RekeyRoot。
func (h *Hub) RekeyRoot(oldPath, newPath string) error {
	ok := strings.ToLower(strings.TrimRight(strings.TrimSpace(oldPath), `\/`))
	nk := strings.ToLower(strings.TrimRight(strings.TrimSpace(newPath), `\/`))
	if ok == "" || nk == "" || ok == nk {
		return nil
	}
	lib, exists := h.libOf[ok]
	if !exists {
		// 旧路径没有映射（或已在上一轮搬过）——新路径若有映射则无需处理
		return nil
	}
	delete(h.libOf, ok)
	h.libOf[nk] = lib
	if err := h.core.DeleteSetting("libdb:" + ok); err != nil {
		return err
	}
	if err := h.core.SetSetting("libdb:"+nk, strconv.Itoa(lib)); err != nil {
		return err
	}
	fmt.Println("[库数据] 库号迁移：" + ok + " → " + nk + "（lib " + strconv.Itoa(lib) + "）")
	return nil
}

// MoveCardRow 把一张卡片行从源分片搬到目标分片（按源路径定位，保 id / 保 card_mod 引用 /
// 搬 idx_meta）——文件移动后的导入消费用。返回 true = 已搬。
// 对齐 C# StoreHub.MoveCardRow：先删源行（同分片移动时释放 id），目标同路径残留先删；
// 插入 id 冲突才换分片内新 id（不动他人行）。
func (h *Hub) MoveCardRow(from, to *DB, fromPath, newPath, newFolder string) (bool, error) {
	if from == nil || to == nil || fromPath == "" || newPath == "" {
		return false, nil
	}
	cardID, err := from.CardIdOf(fromPath)
	if err != nil {
		return false, err
	}
	if cardID <= 0 {
		return false, nil
	}
	var oldPath, mtime, rootPath, folder, scanTime sql.NullString
	var cardType, dataVersion, errText, chara sql.NullString
	var size, tier, imageEnd, uarBlocks, modCount sql.NullInt64
	var thumb []byte
	err = from.execer().QueryRow(`SELECT file_path, size, mtime, tier, root_path, folder,
		card_type, data_version, image_end, uar_blocks, mod_count, thumb, scan_time, error, chara_name
		FROM card WHERE id=?`, cardID).
		Scan(&oldPath, &size, &mtime, &tier, &rootPath, &folder, &cardType, &dataVersion,
			&imageEnd, &uarBlocks, &modCount, &thumb, &scanTime, &errText, &chara)
	if err == sql.ErrNoRows {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	// card_mod 引用行（删源行之前读进内存）
	type refRow [8]interface{}
	refs := make([]refRow, 0, 64)
	rows, err := from.execer().Query(`SELECT mod_guid, property, slot, local_slot, category_no,
		rec_name, rec_author, rec_website FROM card_mod WHERE card_id=?`, cardID)
	if err != nil {
		return false, err
	}
	for rows.Next() {
		var one refRow
		if err = rows.Scan(&one[0], &one[1], &one[2], &one[3], &one[4], &one[5], &one[6], &one[7]); err != nil {
			rows.Close()
			return false, err
		}
		refs = append(refs, one)
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return false, err
	}
	// idx_meta（删源行之前读）
	var meta IdxMetaRow
	var iIdxID, iKind, iCardType, iChara, iWork, iAuthor, iSource, iSrcRel, iNote, iGuid sql.NullString
	var iSeries, iVnID, iVnName, iVnChara, iCharaConf, iCategory, iSubCategory sql.NullString
	hasMeta := false
	err = from.execer().QueryRow(`SELECT idx_id, kind, card_type, chara, work, author, source, src_rel,
		note, guid, series, vn_id, vn_name, vndb_chara_id, chara_id_conf, category, sub_category
		FROM idx_meta WHERE file_path=?`, oldPath.String).
		Scan(&iIdxID, &iKind, &iCardType, &iChara, &iWork, &iAuthor, &iSource, &iSrcRel,
			&iNote, &iGuid, &iSeries, &iVnID, &iVnName, &iVnChara, &iCharaConf, &iCategory, &iSubCategory)
	if err == nil {
		hasMeta = true
		meta = IdxMetaRow{
			FilePath: newPath, IdxId: iIdxID.String, Kind: iKind.String, CardType: iCardType.String,
			Chara: iChara.String, Work: iWork.String, Author: iAuthor.String, Source: iSource.String,
			SrcRel: iSrcRel.String, Note: iNote.String, Guid: iGuid.String, Series: iSeries.String,
			VnId: iVnID.String, VnName: iVnName.String, VndbCharaId: iVnChara.String,
			CharaIdConf: iCharaConf.String, Category: iCategory.String, SubCategory: iSubCategory.String,
		}
	} else if err != sql.ErrNoRows {
		return false, err
	}
	// 先删源行（同分片移动时释放 id；card_mod / card_analysis 一并删）
	if err = from.DeleteCardByPath(oldPath.String); err != nil {
		return false, err
	}
	if hasMeta {
		if _, err = from.execer().Exec("DELETE FROM idx_meta WHERE file_path=?", oldPath.String); err != nil {
			return false, err
		}
	}
	// 目标已有同路径行（重复导入残留）——先删
	if err = to.DeleteCardByPath(newPath); err != nil {
		return false, err
	}
	// 插入 id：无冲突用原 id；冲突则分配分片内新 id（card.id 只在分片内唯一）
	insertID := cardID
	var sameID int64
	if err = to.execer().QueryRow("SELECT COUNT(*) FROM card WHERE id=?", cardID).Scan(&sameID); err != nil {
		return false, err
	}
	if sameID > 0 {
		if err = to.execer().QueryRow("SELECT COALESCE(MAX(id),0)+1 FROM card").Scan(&insertID); err != nil {
			return false, err
		}
	}
	_, err = to.execer().Exec(`INSERT INTO card(id, file_path, file_name, size, mtime, tier, root_path,
		folder, card_type, data_version, image_end, uar_blocks, mod_count, thumb, scan_time, error, chara_name)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		insertID, newPath, filepath.Base(newPath), size.Int64, mtime.String, tier.Int64, rootPath.String,
		newFolder, nullIfNull(cardType), nullIfNull(dataVersion), imageEnd.Int64, uarBlocks.Int64, modCount.Int64,
		thumb, scanTime.String, nullIfNull(errText), nullIfNull(chara))
	if err != nil {
		return false, err
	}
	// card_mod 引用行随卡搬（引用新 id）
	for _, one := range refs {
		if _, err = to.execer().Exec(`INSERT INTO card_mod(card_id, mod_guid, property, slot, local_slot,
			category_no, rec_name, rec_author, rec_website) VALUES(?,?,?,?,?,?,?,?,?)`,
			insertID, one[0], one[1], one[2], one[3], one[4], one[5], one[6], one[7]); err != nil {
			return false, err
		}
	}
	if hasMeta {
		if err = to.UpsertIndexMeta(meta); err != nil {
			return false, err
		}
	}
	return true, nil
}

// nullIfNull 可空文本落库：NULL 保持 NULL，空串保持空串（对齐 C# DBNull 语义）。
func nullIfNull(v sql.NullString) interface{} {
	if !v.Valid {
		return nil
	}
	return v.String
}
