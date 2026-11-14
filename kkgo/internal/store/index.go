// 索引面写面——idx_meta（索引元数据）/ index_source（索引源登记）以及主库四张整理表。
//
// 表归属：idx_meta 每个库文件都有（卡片分片也存自己那份）；index_source / card_rule /
// card_move / card_delete 恒在主库。对齐 C# Data/Store.cs §EnsureIndexTables / §UpsertIndexMeta /
// §UpsertIndexSource / §CleanOrphanRefs。
package store

import (
	"database/sql"
	"fmt"
	"path/filepath"
	"strings"
)

// EnsureIndexTables 确保索引相关表存在——旧库文件按「结构已就绪」打开时不跑完整 EnsureSchema，
// 导入前补表（幂等轻量）。主库额外补 index_source / card_rule / card_move / card_delete 四表。
func (d *DB) EnsureIndexTables() error {
	if _, err := d.sql.Exec(`CREATE TABLE IF NOT EXISTS idx_meta(
		 file_path TEXT PRIMARY KEY, idx_id TEXT, kind TEXT, card_type TEXT, chara TEXT,
		 work TEXT, author TEXT, source TEXT, src_rel TEXT, note TEXT, guid TEXT,
		 series TEXT, vn_id TEXT, vn_name TEXT, vndb_chara_id TEXT, chara_id_conf TEXT,
		 category TEXT, sub_category TEXT)`); err != nil {
		return err
	}
	for _, col := range []string{"series", "vn_id", "vn_name", "vndb_chara_id", "chara_id_conf", "category", "sub_category"} {
		if err := d.ensureColumn("idx_meta", col, "TEXT"); err != nil {
			return err
		}
	}
	if !d.isCore {
		return nil
	}
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS index_source(
			 root_path TEXT PRIMARY KEY, index_dir TEXT, format TEXT,
			 rows INTEGER, cards INTEGER, mods INTEGER, others INTEGER,
			 digest TEXT, imported_at TEXT)`,
		`CREATE TABLE IF NOT EXISTS card_rule(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 kind TEXT NOT NULL, match TEXT NOT NULL, value TEXT NOT NULL,
			 origin TEXT, votes INTEGER, works INTEGER, updated_at TEXT,
			 enabled INTEGER NOT NULL DEFAULT 1,
			 UNIQUE(kind, match))`,
		`CREATE TABLE IF NOT EXISTS card_move(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 from_path TEXT NOT NULL UNIQUE, to_path TEXT NOT NULL,
			 work TEXT, card_type TEXT,
			 state TEXT NOT NULL DEFAULT 'pending',
			 claimed_by TEXT, claimed_at TEXT, executed_at TEXT,
			 consumed INTEGER NOT NULL DEFAULT 0, note TEXT)`,
		`CREATE TABLE IF NOT EXISTS card_delete(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 from_path TEXT NOT NULL, to_path TEXT NOT NULL,
			 size INTEGER, state TEXT NOT NULL DEFAULT 'done',
			 deleted_at TEXT, note TEXT)`,
	}
	for _, stmt := range stmts {
		if _, err := d.sql.Exec(stmt); err != nil {
			return err
		}
	}
	return nil
}

// IdxMetaRow 索引元数据一行（索引导入独有字段 + 参考层七列）——对齐 C# UpsertIndexMeta 的十八个入参。
type IdxMetaRow struct {
	FilePath    string
	IdxId       string
	Kind        string
	CardType    string
	Chara       string
	Work        string
	Author      string
	Source      string
	SrcRel      string
	Note        string
	Guid        string
	Series      string
	VnId        string
	VnName      string
	VndbCharaId string
	CharaIdConf string
	Category    string
	SubCategory string
}

// UpsertIndexMeta 索引元数据 upsert（file_path 为唯一键）。
func (d *DB) UpsertIndexMeta(r IdxMetaRow) error {
	_, err := d.execer().Exec(`INSERT INTO idx_meta(file_path,idx_id,kind,card_type,chara,work,author,source,src_rel,note,guid,series,vn_id,vn_name,vndb_chara_id,chara_id_conf,category,sub_category)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(file_path) DO UPDATE SET
		  idx_id=excluded.idx_id, kind=excluded.kind, card_type=excluded.card_type, chara=excluded.chara,
		  work=excluded.work, author=excluded.author, source=excluded.source, src_rel=excluded.src_rel,
		  note=excluded.note, guid=excluded.guid, series=excluded.series, vn_id=excluded.vn_id,
		  vn_name=excluded.vn_name, vndb_chara_id=excluded.vndb_chara_id, chara_id_conf=excluded.chara_id_conf,
		  category=excluded.category, sub_category=excluded.sub_category`,
		r.FilePath, r.IdxId, r.Kind, r.CardType, r.Chara, r.Work, r.Author, r.Source, r.SrcRel,
		r.Note, r.Guid, r.Series, r.VnId, r.VnName, r.VndbCharaId, r.CharaIdConf, r.Category, r.SubCategory)
	return err
}

// UpsertIndexSource 索引源登记 upsert（主库）——某库根的索引目录 / 格式 / 行数 / 摘要 / 导入时间。
func (d *DB) UpsertIndexSource(rootPath, indexDir, format string, rows, cards, mods, others int64, digest string) error {
	if !d.isCore {
		return fmt.Errorf("index_source 恒在主库，当前连接不是主库")
	}
	_, err := d.sql.Exec(`INSERT INTO index_source(root_path,index_dir,format,rows,cards,mods,others,digest,imported_at)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(root_path) DO UPDATE SET
		  index_dir=excluded.index_dir, format=excluded.format, rows=excluded.rows,
		  cards=excluded.cards, mods=excluded.mods, others=excluded.others,
		  digest=excluded.digest, imported_at=excluded.imported_at`,
		rootPath, indexDir, format, rows, cards, mods, others, digest, Now())
	return err
}

// CleanOrphanRefs 清孤儿引用行（card_id 不在 card 表的 card_mod 残行）——幂等，返回清理条数。
func (d *DB) CleanOrphanRefs() (int, error) {
	res, err := d.execer().Exec(`DELETE FROM card_mod WHERE card_id NOT IN (SELECT id FROM card)`)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return int(n), nil
}

// DeleteIdxMetaUnderRoot 清掉某库根下（路径前缀命中）的索引元数据行——同根重建 / 换根清理用。
func (d *DB) DeleteIdxMetaUnderRoot(rootPath string) (int, error) {
	if rootPath == "" {
		return 0, nil
	}
	prefix := strings.TrimRight(rootPath, `\/`) + string(filepath.Separator)
	res, err := d.execer().Exec(`DELETE FROM idx_meta WHERE file_path LIKE ? ESCAPE '\'`,
		escapeLike(prefix)+"%")
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return int(n), nil
}

// LoadIndexSources 读索引源登记（root_path → 摘要信息）。
func (d *DB) LoadIndexSources() (map[string]IndexSourceRow, error) {
	rows, err := d.sql.Query(`SELECT root_path, IFNULL(index_dir,''), IFNULL(format,''), IFNULL(rows,0),
		IFNULL(cards,0), IFNULL(mods,0), IFNULL(others,0), IFNULL(digest,''), IFNULL(imported_at,'')
		FROM index_source`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]IndexSourceRow)
	for rows.Next() {
		var r IndexSourceRow
		if err = rows.Scan(&r.RootPath, &r.IndexDir, &r.Format, &r.Rows, &r.Cards, &r.Mods, &r.Others, &r.Digest, &r.ImportedAt); err != nil {
			return nil, err
		}
		out[r.RootPath] = r
	}
	return out, rows.Err()
}

// IndexSourceRow 索引源登记一行。
type IndexSourceRow struct {
	RootPath   string
	IndexDir   string
	Format     string
	Rows       int64
	Cards      int64
	Mods       int64
	Others     int64
	Digest     string
	ImportedAt string
}

// escapeLike 转义 LIKE 通配符（配合 ESCAPE '\'）——路径前缀匹配用。
func escapeLike(s string) string {
	s = strings.ReplaceAll(s, `\`, `\\`)
	s = strings.ReplaceAll(s, "%", `\%`)
	return strings.ReplaceAll(s, "_", `\_`)
}

// QueryIdxRows 查索引元数据——按类型 / 作品 / 作者 / 系列 / VN / VNDB 角色 ID 精确筛选 +
// 关键词模糊（角色名 / 路径 / 作品 / 作者 / guid）；空条件不筛。对齐 C# Store.QueryIndexMeta。
func (d *DB) QueryIdxRows(kind, work, author, keyword, series, vn, charaID string, limit int) ([]IdxMetaRow, error) {
	if limit <= 0 {
		limit = 100
	}
	like := "%" + keyword + "%"
	rows, err := d.sql.Query(`SELECT file_path, idx_id, kind, card_type, chara, work, author, source, src_rel, note, guid,
		series, vn_id, vn_name, vndb_chara_id, chara_id_conf, category, sub_category
		FROM idx_meta WHERE (?='' OR kind=?) AND (?='' OR work=?) AND (?='' OR author=?)
		  AND (?='' OR series=?) AND (?='' OR vn_id=? OR vn_name=?) AND (?='' OR vndb_chara_id=?)
		  AND (?='' OR chara LIKE ? OR file_path LIKE ? OR work LIKE ? OR author LIKE ? OR guid LIKE ?)
		ORDER BY work, chara, file_path LIMIT ?`,
		kind, kind, work, work, author, author, series, series, vn, vn, charaID, charaID,
		keyword, like, like, like, like, like, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	list := make([]IdxMetaRow, 0, limit)
	for rows.Next() {
		var r IdxMetaRow
		var cardType, chara, workVal, authorVal, source, srcRel, note, guid, seriesVal, vnID, vnName, vnChara, charaConf, category, subCategory sql.NullString
		if err = rows.Scan(&r.FilePath, &r.IdxId, &r.Kind, &cardType, &chara, &workVal, &authorVal, &source, &srcRel,
			&note, &guid, &seriesVal, &vnID, &vnName, &vnChara, &charaConf, &category, &subCategory); err != nil {
			return nil, err
		}
		r.CardType, r.Chara, r.Work, r.Author = cardType.String, chara.String, workVal.String, authorVal.String
		r.Source, r.SrcRel, r.Note, r.Guid = source.String, srcRel.String, note.String, guid.String
		r.Series, r.VnId, r.VnName, r.VndbCharaId = seriesVal.String, vnID.String, vnName.String, vnChara.String
		r.CharaIdConf, r.Category, r.SubCategory = charaConf.String, category.String, subCategory.String
		list = append(list, r)
	}
	return list, rows.Err()
}
