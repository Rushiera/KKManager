// 扫描写面——mod 侧落库、完成戳、清扫、作者聚合（对齐 C# Data/Store.cs 的扫描相关写方法）。
//
// 表归属：mod_file / scan_state / non_card 是「本连接的表」（主库与库文件各有）；
// mod 主表 / 设置表 / 作者聚合表 / 旧版登记恒在主库——库文件连接走 core 前缀。
package store

import (
	"database/sql"
	"time"
)

// StampLayout C# 的时间戳格式（yyyy-MM-ddTHH:mm:ss.fff，UTC）。
const StampLayout = "2006-01-02T15:04:05.000"

// Now 当前 UTC 时间戳（对齐 C# Store.Now）。
func Now() string { return time.Now().UTC().Format(StampLayout) }

// StampOf 文件修改时间戳文本（UTC，对齐 C# Store.StampOf）。
func StampOf(mtime time.Time) string { return mtime.UTC().Format(StampLayout) }

// ScanStepRow 一条「已完成步骤」记录——扫描跳步判据 = size + mtime 双等。
type ScanStepRow struct {
	// Step 步骤 id。
	Step string
	// Size 登记时的文件字节数。
	Size int64
	// Mtime 登记时的修改时间戳（UTC 文本）。
	Mtime string
}

// ModFileRecord 一条 mod 副本记录（mod_file 行）——跨库重算 / 重复副本用。
type ModFileRecord struct {
	// Guid mod 的 manifest guid。
	Guid string
	// Tier 级别。
	Tier int
	// RootPath 所在库根路径。
	RootPath string
	// FilePath 文件绝对路径。
	FilePath string
	// FileName 文件名。
	FileName string
	// Size 文件字节数。
	Size int64
	// Mtime 修改时间戳（UTC 文本）。
	Mtime string
}

// ModFileInput 一条待写入的 mod 副本信息（扫描侧组装——避免 store 依赖 core 的 ModInfo）。
type ModFileInput struct {
	// Guid manifest guid。
	Guid string
	// FilePath 文件绝对路径。
	FilePath string
	// FileName 文件名。
	FileName string
	// Size 文件字节数。
	Size int64
	// Tier 级别。
	Tier int
	// RootPath 所在库根路径。
	RootPath string
	// Mtime 修改时间戳（UTC 文本）。
	Mtime string
	// Name manifest name。
	Name string
	// Version manifest version。
	Version string
	// Author manifest author。
	Author string
	// Website manifest website。
	Website string
	// Description manifest description。
	Description string
	// SchemaVer manifest schema-ver。
	SchemaVer string
	// EntryCount 容器条目数。
	EntryCount int64
	// Error 解析诊断（成功为空）。
	Error string
	// Found manifest 里实际出现的元素名集合（区分「元素缺失」与「元素为空」——缺失写 NULL，空串写 ''）。
	Found map[string]bool
}

// nullText 空串写 NULL（对齐 C# 侧 null → DBNull 的落库语义）。
func nullText(value string) interface{} {
	if value == "" {
		return nil
	}
	return value
}

// manifestText 按「元素是否出现」决定落库值——元素缺失写 NULL，元素出现（含空串）写原文。
func manifestText(found map[string]bool, name, value string) interface{} {
	if found != nil && found[name] {
		return value
	}
	return nil
}

// UpsertModFile 写入一个 mod 文件副本并刷新 mod 主表的最优级别（对齐 C# Store.UpsertModFile）。
func (d *DB) UpsertModFile(in ModFileInput) error {
	var existed int64
	if err := d.execer().QueryRow("SELECT COUNT(*) FROM mod_file WHERE file_path=?", in.FilePath).Scan(&existed); err != nil {
		return err
	}
	_, err := d.execer().Exec(`INSERT INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(file_path) DO UPDATE SET
		  guid=excluded.guid, tier=excluded.tier, root_path=excluded.root_path,
		  file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime, scan_time=excluded.scan_time`,
		in.FilePath, in.Guid, in.Tier, in.RootPath, in.FileName, in.Size, in.Mtime, Now())
	if err != nil {
		return err
	}
	var delta int64
	if existed == 0 {
		delta = 1
	}
	return d.updateModRowForFile(in, delta)
}

// updateModRowForFile 增量更新 mod 主表——副本数按 delta 调整（新增副本传 1）；tier 与最佳副本取更优者。
// 对齐 C# Store.UpdateModRowForFile（不跨库读副本表，读面自会跨库合并）。
func (d *DB) updateModRowForFile(in ModFileInput, delta int64) error {
	modTable := d.modTable()
	oldTier := -1
	var dup int64
	var oldBestPath string
	err := d.execer().QueryRow("SELECT tier, dup_count, file_path FROM "+modTable+" WHERE guid=?", in.Guid).
		Scan(&oldTier, &dup, &oldBestPath)
	if err != nil && err != sql.ErrNoRows {
		return err
	}
	dupNew := dup + delta
	if dupNew < 0 {
		dupNew = 0
	}
	if oldTier >= 0 {
		takeBest := in.Tier < oldTier
		if !takeBest && in.Tier == oldTier && equalFold(oldBestPath, in.FilePath) {
			takeBest = true
		}
		if takeBest {
			_, err = d.execer().Exec("UPDATE "+modTable+` SET tier=?, root_path=?, file_path=?,
				file_name=?, size=?, mtime=?, dup_count=?, scan_time=? WHERE guid=?`,
				in.Tier, in.RootPath, in.FilePath, in.FileName, in.Size, in.Mtime, dupNew, Now(), in.Guid)
			return err
		}
		_, err = d.execer().Exec("UPDATE "+modTable+" SET dup_count=?, scan_time=? WHERE guid=?", dupNew, Now(), in.Guid)
		return err
	}
	_, err = d.execer().Exec("INSERT INTO "+modTable+`(guid,tier,root_path,file_path,file_name,size,mtime,dup_count,scan_time)
		VALUES(?,?,?,?,?,?,?,?,?)`,
		in.Guid, in.Tier, in.RootPath, in.FilePath, in.FileName, in.Size, in.Mtime, dupNew, Now())
	return err
}

// FillModMeta 填充 mod 主表的 manifest 元数据（对齐 C# Store.FillModMeta）：
// 元素缺失写 NULL、元素出现（含空串）写原文——两者在库里可辨。
func (d *DB) FillModMeta(in ModFileInput) error {
	_, err := d.execer().Exec("UPDATE "+d.modTable()+` SET name=?, version=?, author=?, website=?,
		description=?, schema_ver=?, entry_count=?, error=? WHERE guid=?`,
		manifestText(in.Found, "name", in.Name),
		manifestText(in.Found, "version", in.Version),
		manifestText(in.Found, "author", in.Author),
		manifestText(in.Found, "website", in.Website),
		manifestText(in.Found, "description", in.Description),
		nullText(in.SchemaVer), in.EntryCount, nullText(in.Error), in.Guid)
	return err
}

// WriteModRow 按跨库重算结果写回 mod 主表（副本被删 / 搬移后调用）。
func (d *DB) WriteModRow(guid string, best *ModFileRecord) error {
	if best == nil {
		return nil
	}
	_, err := d.execer().Exec("INSERT INTO "+d.modTable()+`(guid,tier,root_path,file_path,file_name,size,mtime,dup_count,scan_time)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(guid) DO UPDATE SET
		  tier=excluded.tier, root_path=excluded.root_path, file_path=excluded.file_path,
		  file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
		  dup_count=excluded.dup_count, scan_time=excluded.scan_time`,
		guid, best.Tier, best.RootPath, best.FilePath, best.FileName, best.Size, best.Mtime, 0, Now())
	return err
}

// UpdateModRowDup 只更新 mod 主表的副本数（重算后按实际条数覆盖）。
func (d *DB) UpdateModRowDup(guid string, dupCount int64) error {
	_, err := d.execer().Exec("UPDATE "+d.modTable()+" SET dup_count=? WHERE guid=?", dupCount, guid)
	return err
}

// DeleteModRow 删除 mod 主表里的某条记录（该 guid 已无任何副本时用）。
func (d *DB) DeleteModRow(guid string) error {
	_, err := d.execer().Exec("DELETE FROM "+d.modTable()+" WHERE guid=?", guid)
	return err
}

// HasModRow 某 guid 在 mod 主表是否存在。
func (d *DB) HasModRow(guid string) (bool, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM "+d.modTable()+" WHERE guid=?", guid).Scan(&n)
	return n > 0, err
}

// QueryModFileRecords 某个 guid 在本库的副本行（含 size / mtime，供跨库重算用）。
func (d *DB) QueryModFileRecords(guid string) ([]ModFileRecord, error) {
	rows, err := d.execer().Query("SELECT guid, tier, root_path, file_path, file_name, size, mtime FROM mod_file WHERE guid=? ORDER BY file_path", guid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileRecord
	for rows.Next() {
		var r ModFileRecord
		var rootPath, fileName, mtime sql.NullString
		var size sql.NullInt64
		if err = rows.Scan(&r.Guid, &r.Tier, &rootPath, &r.FilePath, &fileName, &size, &mtime); err != nil {
			return nil, err
		}
		r.RootPath = rootPath.String
		r.FileName = fileName.String
		r.Size = size.Int64
		r.Mtime = mtime.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// LoadModFileGuids 载入 mod 副本的 guid 索引——file_path → guid（扫描组装内存态用）。
func (d *DB) LoadModFileGuids() (map[string]string, error) {
	rows, err := d.execer().Query("SELECT file_path, guid FROM mod_file")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]string{}
	for rows.Next() {
		var path, guid sql.NullString
		if err = rows.Scan(&path, &guid); err != nil {
			return nil, err
		}
		if path.String != "" && guid.String != "" {
			out[path.String] = guid.String
		}
	}
	return out, rows.Err()
}

// LoadScanSteps 载入「已完成步骤」索引——file_path → （步骤 id → 记录）。
func (d *DB) LoadScanSteps() (map[string]map[string]ScanStepRow, error) {
	rows, err := d.execer().Query("SELECT file_path, step, size, mtime FROM scan_state")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]map[string]ScanStepRow{}
	for rows.Next() {
		var path, step, mtime sql.NullString
		var size sql.NullInt64
		if err = rows.Scan(&path, &step, &size, &mtime); err != nil {
			return nil, err
		}
		if path.String == "" || step.String == "" {
			continue
		}
		steps, ok := out[path.String]
		if !ok {
			steps = map[string]ScanStepRow{}
			out[path.String] = steps
		}
		steps[step.String] = ScanStepRow{Step: step.String, Size: size.Int64, Mtime: mtime.String}
	}
	return out, rows.Err()
}

// MarkScanStep 登记一步完成（同文件同步骤覆盖式更新）。
func (d *DB) MarkScanStep(filePath, step string, size int64, mtime string) error {
	if filePath == "" || step == "" {
		return nil
	}
	_, err := d.execer().Exec(`INSERT INTO scan_state(file_path,step,size,mtime,done_at)
		VALUES(?,?,?,?,?)
		ON CONFLICT(file_path,step) DO UPDATE SET
		  size=excluded.size, mtime=excluded.mtime, done_at=excluded.done_at`,
		filePath, step, size, mtime, Now())
	return err
}

// DeleteScanStepsMissingUnderRoot 清扫某库根下已从磁盘消失文件的步骤记录（present 为空时不清扫）；返回删除条数。
func (d *DB) DeleteScanStepsMissingUnderRoot(rootPath string, present map[string]bool) (int, error) {
	prefix := trimRoot(rootPath)
	if prefix == "" || len(present) == 0 {
		return 0, nil
	}
	rows, err := d.execer().Query("SELECT DISTINCT file_path FROM scan_state")
	if err != nil {
		return 0, err
	}
	var gone []string
	for rows.Next() {
		var path sql.NullString
		if err = rows.Scan(&path); err != nil {
			rows.Close()
			return 0, err
		}
		p := path.String
		if !underRoot(p, prefix) {
			continue
		}
		if !present[p] {
			gone = append(gone, p)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return 0, err
	}
	return d.deleteByPathList("scan_state", "file_path", gone)
}

// DeleteModFilesMissingUnderRoot 清扫某库根下已消失的 mod 副本记录——返回受影响的 guid（去重）。
func (d *DB) DeleteModFilesMissingUnderRoot(rootPath string, present map[string]bool) ([]string, error) {
	rows, err := d.execer().Query("SELECT file_path, guid FROM mod_file WHERE root_path=?", rootPath)
	if err != nil {
		return nil, err
	}
	var paths []string
	var guids []string
	seen := map[string]bool{}
	for rows.Next() {
		var path, guid sql.NullString
		if err = rows.Scan(&path, &guid); err != nil {
			rows.Close()
			return nil, err
		}
		if path.String == "" || (present != nil && present[path.String]) {
			continue
		}
		paths = append(paths, path.String)
		if guid.String != "" && !seen[guid.String] {
			seen[guid.String] = true
			guids = append(guids, guid.String)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if _, err = d.deleteByPathList("mod_file", "file_path", paths); err != nil {
		return nil, err
	}
	return guids, nil
}

// DeleteModOldMissingUnderRoot 清扫落在某库根下、文件已消失的旧版登记——返回清理条数（旧版登记是主库表）。
func (d *DB) DeleteModOldMissingUnderRoot(rootPath string, present map[string]bool) (int, error) {
	if !d.isCore {
		return 0, nil
	}
	prefix := trimRoot(rootPath)
	if prefix == "" {
		return 0, nil
	}
	rows, err := d.execer().Query("SELECT old_path FROM mod_old")
	if err != nil {
		return 0, err
	}
	var gone []string
	for rows.Next() {
		var path sql.NullString
		if err = rows.Scan(&path); err != nil {
			rows.Close()
			return 0, err
		}
		p := path.String
		if !underRoot(p, prefix) {
			continue
		}
		if present == nil || !present[p] {
			gone = append(gone, p)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return 0, err
	}
	return d.deleteByPathList("mod_old", "old_path", gone)
}

// UpsertNonCard 登记一个非卡 / 非 mod 文件（已在表中则更新理由与体积，保留「已搬走」标记与缩略图）。
func (d *DB) UpsertNonCard(filePath, rootPath, side, reason string, size int64, mtime string, thumb []byte) error {
	if filePath == "" {
		return nil
	}
	var thumbArg interface{}
	if len(thumb) > 0 {
		thumbArg = thumb
	}
	_, err := d.execer().Exec(`INSERT INTO non_card(file_path,root_path,side,reason,size,mtime,thumb,scan_time)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(file_path) DO UPDATE SET
		  root_path=excluded.root_path, side=excluded.side, reason=excluded.reason,
		  size=excluded.size, mtime=excluded.mtime, scan_time=excluded.scan_time,
		  thumb=COALESCE(excluded.thumb, non_card.thumb)`,
		filePath, rootPath, side, reason, size, mtime, thumbArg, Now())
	return err
}

// DeleteNonCardByPath 按路径删除非卡登记（文件变回卡片 / 变回 mod 时核销）。
func (d *DB) DeleteNonCardByPath(filePath string) error {
	if filePath == "" {
		return nil
	}
	_, err := d.execer().Exec("DELETE FROM non_card WHERE file_path=?", filePath)
	return err
}

// DeleteNonCardsMissingUnderRoot 清扫某库根下已消失的非卡登记；返回删除条数。
func (d *DB) DeleteNonCardsMissingUnderRoot(rootPath string) (int, error) {
	prefix := trimRoot(rootPath)
	if prefix == "" {
		return 0, nil
	}
	if prefix == "" {
		return 0, nil
	}
	return d.deleteNonCardsWhere(rootPath)
}

// deleteNonCardsWhere 删除某库根下文件已不存在的非卡登记（逐个 TestPath）。
func (d *DB) deleteNonCardsWhere(rootPath string) (int, error) {
	rows, err := d.execer().Query("SELECT id, file_path FROM non_card WHERE root_path=?", rootPath)
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
		if path.String == "" || !fileExists(path.String) {
			gone = append(gone, id)
		}
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return 0, err
	}
	if len(gone) == 0 {
		return 0, nil
	}
	for _, id := range gone {
		if _, err = d.execer().Exec("DELETE FROM non_card WHERE id=?", id); err != nil {
			return 0, err
		}
	}
	return len(gone), nil
}

// RefreshModAuthors 重建作者聚合表（整表重建——mod 主表是唯一真相源）。返回作者数。
func (d *DB) RefreshModAuthors() (int64, error) {
	authorTable := d.authorTable()
	modTable := d.modTable()
	if _, err := d.execer().Exec("DELETE FROM " + authorTable); err != nil {
		return 0, err
	}
	_, err := d.execer().Exec("INSERT INTO " + authorTable + `(author, mod_count)
		SELECT COALESCE(TRIM(author),''), COUNT(*) FROM ` + modTable + " GROUP BY COALESCE(TRIM(author),'')")
	if err != nil {
		return 0, err
	}
	var n int64
	err = d.execer().QueryRow("SELECT COUNT(*) FROM " + authorTable).Scan(&n)
	return n, err
}

// CountModFilesOf 某库根下的 mod 副本行数（对拍用）。
func (d *DB) CountModFilesOf(rootPath string) (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM mod_file WHERE root_path=?", rootPath).Scan(&n)
	return n, err
}

// CountModRows 本库 mod 主表行数（对拍用；主库口径）。
func (d *DB) CountModRows() (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM " + d.modTable()).Scan(&n)
	return n, err
}

// CountNonCardsOf 某库根下的非卡登记数（对拍用）。
func (d *DB) CountNonCardsOf(rootPath string) (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM non_card WHERE root_path=?", rootPath).Scan(&n)
	return n, err
}

// deleteByPathList 按路径清单逐条删除（列名与表名由调用方给定——只在本文件内使用）。
func (d *DB) deleteByPathList(table, column string, paths []string) (int, error) {
	if len(paths) == 0 {
		return 0, nil
	}
	n := 0
	for _, p := range paths {
		if _, err := d.execer().Exec("DELETE FROM "+table+" WHERE "+column+"=?", p); err != nil {
			return n, err
		}
		n++
	}
	return n, nil
}

// trimRoot 库根路径归一（去尾部分隔符）。
func trimRoot(rootPath string) string {
	out := rootPath
	for len(out) > 0 && (out[len(out)-1] == '\\' || out[len(out)-1] == '/') {
		out = out[:len(out)-1]
	}
	return out
}

// underRoot 路径是否落在库根下（前缀 + 分隔符判据，大小写不敏感）。
func underRoot(path, prefix string) bool {
	if len(path) <= len(prefix)+1 {
		return false
	}
	if !equalFold(path[:len(prefix)], prefix) {
		return false
	}
	c := path[len(prefix)]
	return c == '\\' || c == '/'
}

// equalFold ASCII 大小写不敏感比较（Windows 路径口径；非 ASCII 段逐字节比较——与 C# OrdinalIgnoreCase 在本场景等价）。
func equalFold(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := 0; i < len(a); i++ {
		ca, cb := a[i], b[i]
		if ca >= 'A' && ca <= 'Z' {
			ca += 'a' - 'A'
		}
		if cb >= 'A' && cb <= 'Z' {
			cb += 'a' - 'A'
		}
		if ca != cb {
			return false
		}
	}
	return true
}

// LocalNowText 本地时间戳文本（yyyy-MM-dd HH:mm:ss）——待办登记用（对齐 C# 侧 DateTime.Now 格式）。
func LocalNowText() string { return time.Now().Format("2006-01-02 15:04:05") }

// AddOfflineTodo 登记一条离线库待办（同类型同键只留一条）——对齐 C# Store.AddTodo(kind=offline)。
func (d *DB) AddOfflineTodo(key string) error {
	_, err := d.execer().Exec("INSERT OR IGNORE INTO todo(kind,key,created_at) VALUES('offline',?,?)", key, LocalNowText())
	return err
}
