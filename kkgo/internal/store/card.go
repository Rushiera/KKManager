// 卡片侧写面——卡片行 / 头段 / 声明区 / 缩略图 / 角色名 / timeline 缓存 / 清扫。
//
// 表归属：card / card_mod / card_timeline / scan_state / non_card 都是「本连接的表」
// （卡片侧一律自有分片库文件，主库不持卡片行）。对齐 C# Data/Store.cs 的同名写方法。
package store

import "database/sql"

// CardStamp 卡片行的索引快照（扫描组装内存态用）——对齐 C# Store.LoadCardStamps 的七项。
type CardStamp struct {
	// Size 登记时的文件字节数。
	Size int64
	// Mtime 登记时的修改时间戳（UTC 文本）。
	Mtime string
	// NamePending 人物卡且角色名尚未读过（chara_name IS NULL AND card_type LIKE '%Chara%'）。
	NamePending bool
	// CardType 库里记的卡类型（供存量类型补正出声）。
	CardType string
	// TimelinePending 场景卡且 timeline 缓存失效（无缓存 / size 或 mtime 不符）。
	TimelinePending bool
	// CardId 卡片行 id。
	CardId int64
	// ImageEnd 图片区结束偏移（0 = 未记）。
	ImageEnd int64
}

// TimelineValues 一张卡片的 timeline 缓存值（store 侧视图——避免依赖 core 的 TimelineInfo）。
type TimelineValues struct {
	// HasEntry 是否存在 timeline 条目。
	HasEntry bool
	// IsEmpty 空时间轴。
	IsEmpty bool
	// Duration 原时长（秒）。
	Duration float64
	// TimeScale 时间缩放。
	TimeScale float64
	// Keyframes 关键帧数。
	Keyframes int
	// XmlLength sceneInfo XML 字节数。
	XmlLength int64
	// HitStage 命中扫描阶段。
	HitStage string
	// Error 读取失败原因。
	Error string
}

// LoadCardStamps 载入卡片索引快照——file_path → 七项（对齐 C# Store.LoadCardStamps）。
func (d *DB) LoadCardStamps() (map[string]CardStamp, error) {
	rows, err := d.execer().Query(`SELECT c.file_path, c.size, c.mtime,
		(c.chara_name IS NULL AND IFNULL(c.card_type,'') LIKE '%Chara%'), c.card_type,
		CASE WHEN c.card_type = 'sd' AND (t.card_id IS NULL OR t.size <> c.size OR IFNULL(t.mtime,'') <> IFNULL(c.mtime,'')) THEN 1 ELSE 0 END,
		c.id, c.image_end
		FROM card c LEFT JOIN card_timeline t ON t.card_id = c.id`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]CardStamp, 256)
	for rows.Next() {
		var path string
		var st CardStamp
		var mtime, cardType sql.NullString
		var size sql.NullInt64
		var namePending, timelinePending sql.NullInt64
		var cardId, imageEnd sql.NullInt64
		if err = rows.Scan(&path, &size, &mtime, &namePending, &cardType, &timelinePending, &cardId, &imageEnd); err != nil {
			return nil, err
		}
		st.Size = size.Int64
		st.Mtime = mtime.String
		st.NamePending = namePending.Int64 != 0
		st.CardType = cardType.String
		st.TimelinePending = timelinePending.Int64 != 0
		st.CardId = cardId.Int64
		st.ImageEnd = imageEnd.Int64
		out[path] = st
	}
	return out, rows.Err()
}

// UpsertCardRow 扫描「卡片行」步——只写文件与归属；已存在的行只刷新 size / mtime / 归属。返回卡片 id。
func (d *DB) UpsertCardRow(filePath, fileName string, size int64, mtime string, tier int, rootPath, folder string) (int64, error) {
	_, err := d.execer().Exec(`INSERT INTO card(file_path,file_name,size,mtime,tier,root_path,folder,scan_time)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(file_path) DO UPDATE SET
		  file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
		  tier=excluded.tier, root_path=excluded.root_path, folder=excluded.folder,
		  scan_time=excluded.scan_time`,
		filePath, fileName, size, mtime, tier, rootPath, folder, Now())
	if err != nil {
		return 0, err
	}
	return d.CardIdOf(filePath)
}

// CardIdOf 按路径取卡片 id（没有返回 0）。
func (d *DB) CardIdOf(filePath string) (int64, error) {
	if filePath == "" {
		return 0, nil
	}
	var id int64
	err := d.execer().QueryRow("SELECT id FROM card WHERE file_path=?", filePath).Scan(&id)
	if err == sql.ErrNoRows {
		return 0, nil
	}
	return id, err
}

// UpdateCardHead 扫描「卡头段」步——写卡类型 / 数据版本 / 图片区终点（空串写 NULL）。
func (d *DB) UpdateCardHead(filePath, cardType, dataVersion string, imageEnd int64) error {
	_, err := d.execer().Exec("UPDATE card SET card_type=?, data_version=?, image_end=? WHERE file_path=?",
		nullText(cardType), nullText(dataVersion), imageEnd, filePath)
	return err
}

// UpdateCardRefsMeta 扫描「声明区」步——刷新引用计数与 UAR 块数。
func (d *DB) UpdateCardRefsMeta(filePath string, modCount, uarBlocks int) error {
	_, err := d.execer().Exec("UPDATE card SET mod_count=?, uar_blocks=? WHERE file_path=?",
		modCount, uarBlocks, filePath)
	return err
}

// ReplaceCardRefs 替换一张卡片的全部 mod 引用——按部件粒度逐条写（mod_guid 为空跳过）；
// property / rec_* 落 NULL、slot / local_slot / category_no 落 0（与 C# 侧 ModRef 默认值一致：
// CollectRefs 只填 ModId，其余字段保持默认）。
func (d *DB) ReplaceCardRefs(cardId int64, refs []string) error {
	if _, err := d.execer().Exec("DELETE FROM card_mod WHERE card_id=?", cardId); err != nil {
		return err
	}
	if len(refs) == 0 {
		return nil
	}
	for _, guid := range refs {
		if guid == "" {
			continue
		}
		if _, err := d.execer().Exec(`INSERT OR REPLACE INTO card_mod(card_id,mod_guid,property,slot,local_slot,category_no,rec_name,rec_author,rec_website)
			VALUES(?,?,NULL,0,0,0,NULL,NULL,NULL)`, cardId, guid); err != nil {
			return err
		}
	}
	return nil
}

// UpdateCardThumb 扫描「缩略图」步——写缩略图（nil 表示本次没生成，保留原值不清空）。
func (d *DB) UpdateCardThumb(filePath string, thumb []byte) error {
	if len(thumb) == 0 {
		return nil
	}
	_, err := d.execer().Exec("UPDATE card SET thumb=? WHERE file_path=?", thumb, filePath)
	return err
}

// UpdateCardName 写一张卡片的角色名（空串表示没读到——落 NULL，下次扫描再试）。
func (d *DB) UpdateCardName(filePath, name string) error {
	_, err := d.execer().Exec("UPDATE card SET chara_name=? WHERE file_path=?", nullText(name), filePath)
	return err
}

// DeleteCardByPath 按路径删除一张卡片（含引用行与分项分析行）——头段步判定为非卡时用。
func (d *DB) DeleteCardByPath(filePath string) error {
	id, err := d.CardIdOf(filePath)
	if err != nil || id <= 0 {
		return err
	}
	for _, stmt := range []string{
		"DELETE FROM card_mod WHERE card_id=?",
		"DELETE FROM card_analysis WHERE card_id=?",
		"DELETE FROM card WHERE id=?",
	} {
		if _, err = d.execer().Exec(stmt, id); err != nil {
			return err
		}
	}
	return nil
}

// DeleteCardsMissingUnderRoot 清扫某库根下已从磁盘消失的卡片记录（含引用行）；返回删除的卡片数。
func (d *DB) DeleteCardsMissingUnderRoot(rootPath string, present map[string]bool) (int, error) {
	rows, err := d.execer().Query("SELECT id, file_path FROM card WHERE root_path=?", rootPath)
	if err != nil {
		return 0, err
	}
	var gone []int64
	for rows.Next() {
		var id int64
		var path sql.NullString
		if err = rows.Scan(&id, &path); err != nil {
			rows.Close()
			return 0, err
		}
		if path.String == "" || present == nil || !present[path.String] {
			gone = append(gone, id)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return 0, err
	}
	for _, id := range gone {
		if _, err = d.execer().Exec("DELETE FROM card_mod WHERE card_id=?", id); err != nil {
			return 0, err
		}
		if _, err = d.execer().Exec("DELETE FROM card WHERE id=?", id); err != nil {
			return 0, err
		}
	}
	return len(gone), nil
}

// SaveCardTimeline 写一张卡片的 timeline 缓存（按 card_id 覆盖；size + mtime 是失效判据）。
func (d *DB) SaveCardTimeline(cardId int64, filePath string, size int64, mtime string, t *TimelineValues) error {
	if t == nil {
		t = &TimelineValues{TimeScale: 1}
	}
	_, err := d.execer().Exec(`INSERT INTO card_timeline(card_id,file_path,size,mtime,has_entry,is_empty,
		duration,time_scale,keyframes,xml_length,hit_stage,read_at,error)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(card_id) DO UPDATE SET
		  file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
		  has_entry=excluded.has_entry, is_empty=excluded.is_empty, duration=excluded.duration,
		  time_scale=excluded.time_scale, keyframes=excluded.keyframes, xml_length=excluded.xml_length,
		  hit_stage=excluded.hit_stage, read_at=excluded.read_at, error=excluded.error`,
		cardId, nullText(filePath), size, nullText(mtime),
		boolInt(t.HasEntry), boolInt(t.IsEmpty), t.Duration, t.TimeScale,
		t.Keyframes, t.XmlLength, nullText(t.HitStage), Now(), nullText(t.Error))
	return err
}

// boolInt 布尔转 0/1（落库形态）。
func boolInt(v bool) int {
	if v {
		return 1
	}
	return 0
}

// CountCardsOf 某库根下的卡片行数（对拍用）。
func (d *DB) CountCardsOf(rootPath string) (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM card WHERE root_path=?", rootPath).Scan(&n)
	return n, err
}

// CountCardRefs 本库 card_mod 行数（对拍用）。
func (d *DB) CountCardRefs() (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM card_mod").Scan(&n)
	return n, err
}
