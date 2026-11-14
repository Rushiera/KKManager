package store

import "database/sql"

// CardRuleRec 一条整理规则（对齐 C# Core/CardOrganizer.cs §CardRule 的持久字段）。
// Kind 取 dir / kw / chara / author 四种文本值——写入侧只产出这四种。
type CardRuleRec struct {
	Kind   string
	Match  string
	Value  string
	Origin string
	Votes  int
	Works  int
}

// cardRuleTable card_rule 表在本次连接里的引用名（表恒在主库）。
func (d *DB) cardRuleTable() string { return d.coreTable("card_rule") }

// LoadCardRules 读启用中的规则（按 id 升序）——对齐 C# Store.LoadCardRules。
func (d *DB) LoadCardRules() ([]CardRuleRec, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return nil, err
	}
	rows, err := d.execer().Query("SELECT kind, match, value, origin, votes, works FROM " + d.cardRuleTable() + " WHERE enabled=1 ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	list := make([]CardRuleRec, 0, 64)
	for rows.Next() {
		var r CardRuleRec
		var kind, match, value, origin sql.NullString
		var votes, works sql.NullInt64
		if err = rows.Scan(&kind, &match, &value, &origin, &votes, &works); err != nil {
			return nil, err
		}
		r.Kind, r.Match, r.Value, r.Origin = kind.String, match.String, value.String, origin.String
		r.Votes, r.Works = int(votes.Int64), int(works.Int64)
		list = append(list, r)
	}
	return list, rows.Err()
}

// MergeCardRules 写一批规则（导入 / bootstrap）——同 kind+match 已存在时：
// overwrite=true 覆盖值与统计；false 只补统计（保留已有人工值）。返回写入条数。
// 对齐 C# Store.MergeCardRules。
func (d *DB) MergeCardRules(rules []CardRuleRec, origin string, overwrite bool) (int, error) {
	if len(rules) == 0 {
		return 0, nil
	}
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	sqlText := `INSERT INTO ` + d.cardRuleTable() + `(kind,match,value,origin,votes,works,updated_at,enabled)
		VALUES(?,?,?,?,?,?,?,1)
		ON CONFLICT(kind,match) DO UPDATE SET
		  value=excluded.value, origin=excluded.origin, votes=excluded.votes,
		  works=excluded.works, updated_at=excluded.updated_at, enabled=1`
	if !overwrite {
		sqlText = `INSERT INTO ` + d.cardRuleTable() + `(kind,match,value,origin,votes,works,updated_at,enabled)
			VALUES(?,?,?,?,?,?,?,1)
			ON CONFLICT(kind,match) DO UPDATE SET
			  votes=CASE WHEN excluded.votes > 0 THEN excluded.votes ELSE ` + d.cardRuleTable() + `.votes END,
			  works=CASE WHEN excluded.works > 0 THEN excluded.works ELSE ` + d.cardRuleTable() + `.works END,
			  updated_at=excluded.updated_at`
	}
	n := 0
	now := nowStampUTCStr()
	if err := d.Begin(); err != nil {
		return 0, err
	}
	for _, rule := range rules {
		ruleOrigin := rule.Origin
		if ruleOrigin == "" {
			ruleOrigin = origin
		}
		if _, err := d.execer().Exec(sqlText, rule.Kind, rule.Match, rule.Value, ruleOrigin, rule.Votes, rule.Works, now); err != nil {
			d.Rollback()
			return n, err
		}
		n++
	}
	if err := d.Commit(); err != nil {
		return n, err
	}
	return n, nil
}

// LearnCardRules 写学习结果——同 kind+match 已存在时保留原值（人工改的优先），
// 只刷新票数 / 作品数；不产生重复行。对齐 C# Store.LearnCardRules。
func (d *DB) LearnCardRules(rules []CardRuleRec) (int, error) {
	if len(rules) == 0 {
		return 0, nil
	}
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	sqlText := `INSERT INTO ` + d.cardRuleTable() + `(kind,match,value,origin,votes,works,updated_at,enabled)
		VALUES(?,?,?,'learn',?,?,?,1)
		ON CONFLICT(kind,match) DO UPDATE SET
		  votes=excluded.votes, works=excluded.works, updated_at=excluded.updated_at`
	now := nowStampUTCStr()
	if err := d.Begin(); err != nil {
		return 0, err
	}
	n := 0
	for _, rule := range rules {
		if _, err := d.execer().Exec(sqlText, rule.Kind, rule.Match, rule.Value, rule.Votes, rule.Works, now); err != nil {
			d.Rollback()
			return n, err
		}
		n++
	}
	if err := d.Commit(); err != nil {
		return n, err
	}
	return n, nil
}

// CountCardRules 规则表条数（含停用；bootstrap 判空用）。
func (d *DB) CountCardRules() (int64, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM " + d.cardRuleTable()).Scan(&n)
	return n, err
}

// CountCardRulesOfOrigin 某来源的规则条数（bootstrap 判据：内置规则是否已入库）。
func (d *DB) CountCardRulesOfOrigin(origin string) (int64, error) {
	if err := d.EnsureIndexTables(); err != nil {
		return 0, err
	}
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM "+d.cardRuleTable()+" WHERE origin=?", origin).Scan(&n)
	return n, err
}
