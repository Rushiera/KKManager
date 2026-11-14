// Package store 主库最小读写——G3 存储段的第一块砖。
//
// 当前只覆盖 setting 表（库根配置 / 扫描计划都落在这一张表里），
// 余下（schema 对齐 db-template.db / 分片库 / 预热 / 增量戳）待 G3 后续补。
package store

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	_ "modernc.org/sqlite"
)

// DB 主库 / 库文件句柄（对应 C# 侧 Store 的 core 库与库文件两种形态）。
type DB struct {
	sql  *sql.DB
	path string
	// core 主库绝对路径（空 = 本连接就是主库）。
	core string
	// isCore 是否为主库连接——库文件的设置表 / mod 主表 / 作者聚合表都走 core 前缀。
	isCore bool
	// attachedCore 已挂上本连接的主库绝对路径（ATTACH 是连接级；空 = 未挂）。
	attachedCore string
	// tx 当前的写入事务（扫描按批提交；nil = 无事务）。
	tx *sql.Tx
}

// execer 语句执行面（*sql.DB 与 *sql.Tx 的公共子集）——扫描按批开事务时全部语句走事务。
type execer interface {
	Exec(query string, args ...interface{}) (sql.Result, error)
	Query(query string, args ...interface{}) (*sql.Rows, error)
	QueryRow(query string, args ...interface{}) *sql.Row
}

// execer 当前语句执行面（事务内走事务）。
func (d *DB) execer() execer {
	if d.tx != nil {
		return d.tx
	}
	return d.sql
}

// Begin 开启写入事务——deferred（sql.Tx 默认）：分片库连接都 ATTACH 了主库，IMMEDIATE 会给全部已附加库加写锁。
func (d *DB) Begin() error {
	if d.tx != nil {
		return nil
	}
	tx, err := d.sql.Begin()
	if err != nil {
		return err
	}
	d.tx = tx
	return nil
}

// Commit 提交写入事务（无事务时静默）。
func (d *DB) Commit() error {
	if d.tx == nil {
		return nil
	}
	err := d.tx.Commit()
	d.tx = nil
	return err
}

// Rollback 回滚写入事务（无事务时静默）。
func (d *DB) Rollback() error {
	if d.tx == nil {
		return nil
	}
	err := d.tx.Rollback()
	d.tx = nil
	return err
}

// modTable mod 主表在本次连接里的引用名（主库是本地表，库文件走 core 前缀）——对齐 C# Store.ModTable。
func (d *DB) modTable() string {
	if d.isCore {
		return "mod"
	}
	return "core.mod"
}

// settingTable 设置表在本次连接里的引用名。
func (d *DB) settingTable() string {
	if d.isCore {
		return "setting"
	}
	return "core.setting"
}

// authorTable 作者聚合表的引用名（表恒在主库）。
func (d *DB) authorTable() string {
	if d.isCore {
		return "mod_author"
	}
	return "core.mod_author"
}

// IsCore 是否主库连接。
func (d *DB) IsCore() bool { return d.isCore }

// errNilDB 空库句柄（调用方传了 nil——出声不静默）。
var errNilDB = fmt.Errorf("库句柄为空")

// attachCore 把主库挂到本连接上（名字 core）——已挂过同一主库则跳过（ATTACH 是连接级，重复挂会报错）。
func (d *DB) attachCore(corePath string) error {
	if d.attachedCore != "" {
		return nil
	}
	escaped := strings.ReplaceAll(corePath, "'", "''")
	if _, err := d.sql.Exec("ATTACH DATABASE '" + escaped + "' AS core"); err != nil {
		return err
	}
	d.attachedCore = corePath
	return nil
}

// openConn 打开一个 sqlite 连接并启用与 C# 一致的连接参数（WAL · synchronous=NORMAL · busy_timeout 15 s）。
func openConn(abs string) (*sql.DB, error) {
	handle, err := sql.Open("sqlite", "file:"+strings.ReplaceAll(abs, "\\", "/"))
	if err != nil {
		return nil, err
	}
	handle.SetMaxOpenConns(1)
	for _, pragma := range []string{
		"PRAGMA journal_mode=WAL",
		"PRAGMA synchronous=NORMAL",
		"PRAGMA busy_timeout=15000",
	} {
		if _, err = handle.Exec(pragma); err != nil {
			handle.Close()
			return nil, err
		}
	}
	return handle, nil
}

// Open 打开主库（core 库）——文件缺失先释放内嵌模板（失败回落最小结构并出声），再补主库独有表与增量列。
// 结构版本不符时不重建：C# 会 DropAll 重建，Go 侧破坏性动作需显式确认，这里出声并返回错误。
func Open(path string) (*DB, error) {
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	fresh := false
	if info, statErr := os.Stat(abs); statErr != nil || info.IsDir() {
		fresh = true
		if dir := filepath.Dir(abs); dir != "" {
			if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
				return nil, mkErr
			}
		}
	}
	released := false
	if fresh {
		released = ReleaseTemplate(abs)
	}
	handle, err := openConn(abs)
	if err != nil {
		return nil, err
	}
	db := &DB{sql: handle, path: abs, isCore: true}
	if !fresh {
		ver, verErr := db.schemaVersion()
		if verErr != nil {
			handle.Close()
			return nil, verErr
		}
		if ver != SchemaVersion {
			handle.Close()
			return nil, fmt.Errorf("主库结构版本 %d 与期望 %d 不符（%s）——需 C# 侧重建或迁移后再用", ver, SchemaVersion, abs)
		}
		// 模板 / 旧库可能落后于代码的增量补列（版本号未变）——幂等补齐
		if err = db.EnsureColumns(); err != nil {
			handle.Close()
			return nil, err
		}
		return db, nil
	}
	if !released {
		// 模板缺失 / 写失败——回落最小结构（只有 setting 表）；ReleaseTemplate 已出声
		fmt.Println("[库] 主库按最小 schema 新建：" + abs)
		if err = db.ensureSetting(); err != nil {
			handle.Close()
			return nil, err
		}
		return db, nil
	}
	if err = db.EnsureColumns(); err != nil {
		handle.Close()
		return nil, err
	}
	if err = db.EnsureCoreTables(); err != nil {
		handle.Close()
		return nil, err
	}
	if err = db.setSchemaVersion(); err != nil {
		handle.Close()
		return nil, err
	}
	fmt.Println("[库] 主库已从内嵌模板释放：" + abs)
	return db, nil
}

// openLib 打开一个库文件（mod 单片库 / 卡片分片库）——缺文件从内嵌模板释放（零 DDL），ATTACH 主库为 core。
// 模板释放失败回落最小结构（非核心表全集）；库文件的设置表 / mod 主表 / 作者表走 core 前缀。
func openLib(path, corePath string) (*DB, error) {
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	coreAbs, coreErr := filepath.Abs(corePath)
	if coreErr != nil {
		coreAbs = corePath
	}
	fresh := false
	if info, statErr := os.Stat(abs); statErr != nil || info.IsDir() {
		fresh = true
		if dir := filepath.Dir(abs); dir != "" {
			if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
				return nil, mkErr
			}
		}
	}
	released := false
	if fresh {
		released = ReleaseTemplate(abs)
	}
	handle, err := openConn(abs)
	if err != nil {
		return nil, err
	}
	db := &DB{sql: handle, path: abs, core: coreAbs, isCore: false}
	if fresh && !released {
		fmt.Println("[库数据] 库文件按最小结构新建：" + abs)
		if err = db.ensureShardSchema(); err != nil {
			handle.Close()
			return nil, err
		}
	}
	// 模板复制件可能落后于代码的增量补列——幂等补齐（列已存在时只是几次 pragma 查询）
	if err = db.EnsureColumns(); err != nil {
		handle.Close()
		return nil, err
	}
	if err = db.attachCore(coreAbs); err != nil {
		handle.Close()
		return nil, err
	}
	return db, nil
}

// ensureShardSchema 库文件的最小结构（模板缺失时的回落）——非核心表全集（对齐 C# EnsureSchema 的非主库分支）。
func (d *DB) ensureShardSchema() error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS mod_file(
			 file_path TEXT PRIMARY KEY, guid TEXT, tier INTEGER, root_path TEXT,
			 file_name TEXT, size INTEGER, mtime TEXT, scan_time TEXT)`,
		`CREATE TABLE IF NOT EXISTS card(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 file_path TEXT UNIQUE, file_name TEXT, size INTEGER, mtime TEXT,
			 tier INTEGER, root_path TEXT, folder TEXT,
			 card_type TEXT, data_version TEXT, image_end INTEGER, uar_blocks INTEGER,
			 mod_count INTEGER, thumb BLOB, scan_time TEXT, error TEXT, chara_name TEXT)`,
		`CREATE TABLE IF NOT EXISTS card_mod(
			 card_id INTEGER, mod_guid TEXT,
			 property TEXT, slot INTEGER, local_slot INTEGER, category_no INTEGER,
			 rec_name TEXT, rec_author TEXT, rec_website TEXT,
			 PRIMARY KEY(card_id, property, slot, local_slot, mod_guid))`,
		`CREATE TABLE IF NOT EXISTS card_analysis(
			 card_id INTEGER NOT NULL, kind TEXT NOT NULL,
			 file_path TEXT, size INTEGER, mtime TEXT, data TEXT, read_at TEXT,
			 PRIMARY KEY(card_id, kind))`,
		`CREATE TABLE IF NOT EXISTS card_timeline(
			 card_id INTEGER PRIMARY KEY, file_path TEXT, size INTEGER, mtime TEXT,
			 has_entry INTEGER, is_empty INTEGER, duration REAL, time_scale REAL,
			 keyframes INTEGER, xml_length INTEGER, hit_stage TEXT, read_at TEXT, error TEXT)`,
		`CREATE TABLE IF NOT EXISTS non_card(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 file_path TEXT UNIQUE, root_path TEXT, side TEXT, reason TEXT,
			 size INTEGER, mtime TEXT, thumb BLOB,
			 moved_to TEXT, moved_at TEXT, scan_time TEXT)`,
		`CREATE TABLE IF NOT EXISTS scan_state(
			 file_path TEXT NOT NULL, step TEXT NOT NULL,
			 size INTEGER, mtime TEXT, done_at TEXT,
			 PRIMARY KEY(file_path, step))`,
		`CREATE TABLE IF NOT EXISTS todo(
			 id INTEGER PRIMARY KEY AUTOINCREMENT,
			 kind TEXT, key TEXT, text TEXT, ref_path TEXT, ref_name TEXT, created_at TEXT)`,
	}
	for _, stmt := range stmts {
		if _, err := d.sql.Exec(stmt); err != nil {
			return err
		}
	}
	return d.setSchemaVersion()
}

// Path 主库文件绝对路径（对齐 C# 的 StoreHub.CorePath）。
func (d *DB) Path() string { return d.path }

// Close 关闭句柄。
func (d *DB) Close() error { return d.sql.Close() }

// ensureSetting 建 setting 表（key 主键 / value 文本）。
func (d *DB) ensureSetting() error {
	_, err := d.sql.Exec("CREATE TABLE IF NOT EXISTS setting(key TEXT PRIMARY KEY, value TEXT)")
	return err
}

// GetSetting 读一个设置项；不存在返回空串与 false。
func (d *DB) GetSetting(key string) (string, bool, error) {
	var value sql.NullString
	err := d.sql.QueryRow("SELECT value FROM setting WHERE key=?", key).Scan(&value)
	if err == sql.ErrNoRows {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	return value.String, true, nil
}

// SetSetting 写一个设置项（存在即覆盖）。
func (d *DB) SetSetting(key, value string) error {
	_, err := d.sql.Exec(`INSERT INTO setting(key,value) VALUES(?,?)
		ON CONFLICT(key) DO UPDATE SET value=excluded.value`, key, value)
	return err
}

// ModFileRow 一条 mod 副本行（对齐 C# Store.RefRow：guid / 级别 / 库根 / 文件 / 文件名）。
type ModFileRow struct {
	// Guid mod 的 manifest guid。
	Guid string
	// Tier 级别：1=主库 · 2=缓存库 · 3=冷冻库。
	Tier int
	// RootPath 所在库根路径。
	RootPath string
	// FilePath 文件绝对路径。
	FilePath string
	// FileName 文件名。
	FileName string
}

// LoadSettings 读设置表全部条目（键 → 值）。
func (d *DB) LoadSettings() (map[string]string, error) {
	rows, err := d.sql.Query("SELECT key, value FROM setting")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make(map[string]string)
	for rows.Next() {
		var key string
		var value sql.NullString
		if err = rows.Scan(&key, &value); err != nil {
			return nil, err
		}
		out[key] = value.String
	}
	return out, rows.Err()
}

// QueryModFileRows 某 guid 的全部 mod 副本行（按 tier 升序；表缺失时返回错误）。
func (d *DB) QueryModFileRows(guid string) ([]ModFileRow, error) {
	rows, err := d.sql.Query("SELECT guid, tier, root_path, file_path, file_name FROM mod_file WHERE guid=? ORDER BY tier", guid)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []ModFileRow
	for rows.Next() {
		var r ModFileRow
		var rootPath, fileName sql.NullString
		if err = rows.Scan(&r.Guid, &r.Tier, &rootPath, &r.FilePath, &fileName); err != nil {
			return nil, err
		}
		r.RootPath = rootPath.String
		r.FileName = fileName.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// CompositionRow 一条组成档案（对齐 C# ModComposition）。
type CompositionRow struct {
	// Guid mod 的 manifest guid。
	Guid string
	// FilePath 建档所用的副本路径。
	FilePath string
	// FileName 文件名。
	FileName string
	// Size 文件字节数（建档时）。
	Size int64
	// Mtime 文件修改时间戳（UTC，建档时）。
	Mtime string
	// EntryCount 容器条目数。
	EntryCount int64
	// TotalSize 条目原始字节合计。
	TotalSize int64
	// TotalCompressed 条目压缩字节合计。
	TotalCompressed int64
	// Entries 条目清单（每行「路径 \t 原始字节 \t 压缩字节」）。
	Entries string
	// AnalyzedAt 建档时刻（UTC）。
	AnalyzedAt string
	// Texts 文本条目内容（每条以 \u0001 开头，「路径 \t 内容」）。
	Texts string
	// Cached 是否命中已有档案（不落库）。
	Cached bool
}

// LoadComposition 读某 guid 的组成档案（没有返回 nil）——组成档案是主库表。
func (d *DB) LoadComposition(guid string) (*CompositionRow, error) {
	var rec CompositionRow
	var filePath, fileName, mtime, entries, texts sql.NullString
	var analyzedAt sql.NullString
	err := d.sql.QueryRow("SELECT guid, file_path, file_name, size, mtime, entry_count, total_size, total_compressed, entries, analyzed_at, texts FROM mod_composition WHERE guid=?", guid).
		Scan(&rec.Guid, &filePath, &fileName, &rec.Size, &mtime, &rec.EntryCount, &rec.TotalSize, &rec.TotalCompressed, &entries, &analyzedAt, &texts)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	rec.FilePath = filePath.String
	rec.FileName = fileName.String
	rec.Mtime = mtime.String
	rec.Entries = entries.String
	rec.AnalyzedAt = analyzedAt.String
	rec.Texts = texts.String
	return &rec, nil
}

// SaveComposition 写一条组成档案（同一 guid 覆盖写）——组成档案是主库表。
func (d *DB) SaveComposition(rec *CompositionRow) error {
	if rec == nil || rec.Guid == "" {
		return nil
	}
	_, err := d.sql.Exec(`INSERT INTO mod_composition(guid,file_path,file_name,size,mtime,entry_count,total_size,total_compressed,entries,analyzed_at,texts)
		VALUES(?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(guid) DO UPDATE SET
			file_path=excluded.file_path, file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
			entry_count=excluded.entry_count, total_size=excluded.total_size, total_compressed=excluded.total_compressed,
			entries=excluded.entries, analyzed_at=excluded.analyzed_at, texts=excluded.texts`,
		rec.Guid, rec.FilePath, rec.FileName, rec.Size, rec.Mtime, rec.EntryCount,
		rec.TotalSize, rec.TotalCompressed, rec.Entries, rec.AnalyzedAt, rec.Texts)
	return err
}

// PluginRow 一项插件行（plugin_file 表——一个 dll 可含多项；guid 为空表示该 dll 未解析出插件特性）。
type PluginRow struct {
	// FilePath dll 绝对路径。
	FilePath string
	// Guid 插件 GUID（空 = 非插件 dll）。
	Guid string
	// RootPath 插件库根路径。
	RootPath string
	// FileName dll 文件名。
	FileName string
	// Name 插件显示名。
	Name string
	// Version 插件版本。
	Version string
	// Processes 进程过滤（逗号分隔）。
	Processes string
	// Dependencies 依赖 GUID（逗号分隔）。
	Dependencies string
	// IsIpa 是否 IPA 插件。
	IsIpa bool
	// Size dll 字节数。
	Size int64
	// Mtime dll 修改时间戳（UTC）。
	Mtime string
	// Title 程序集标题。
	Title string
	// Description 程序集说明。
	Description string
	// Company 程序集公司 / 作者署名。
	Company string
	// Copyright 程序集版权。
	Copyright string
	// Product 程序集产品名。
	Product string
	// FileVersion 程序集版本。
	FileVersion string
	// TargetFramework 目标框架。
	TargetFramework string
	// Note 未解析出插件特性时的原因。
	Note string
}

// LoadPlugins 全部插件行（有 guid 的在前按 guid / 文件名排序；空 guid 的非插件 dll 排在最后）。
func (d *DB) LoadPlugins() ([]PluginRow, error) {
	rows, err := d.sql.Query(`SELECT file_path, guid, root_path, file_name, name, version, processes, dependencies, is_ipa, size, mtime,
		title, description, company, copyright, product, file_version, target_framework, note
		FROM plugin_file ORDER BY CASE WHEN guid='' THEN 1 ELSE 0 END, guid, file_name`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []PluginRow
	for rows.Next() {
		var r PluginRow
		var filePath, guid, rootPath, fileName, name, version, processes, dependencies sql.NullString
		var isIpa sql.NullInt64
		var size sql.NullInt64
		var mtime, title, description, company, copyright, product, fileVersion, targetFramework, note sql.NullString
		if err = rows.Scan(&filePath, &guid, &rootPath, &fileName, &name, &version, &processes, &dependencies, &isIpa, &size, &mtime,
			&title, &description, &company, &copyright, &product, &fileVersion, &targetFramework, &note); err != nil {
			return nil, err
		}
		r.FilePath = filePath.String
		r.Guid = guid.String
		r.RootPath = rootPath.String
		r.FileName = fileName.String
		r.Name = name.String
		r.Version = version.String
		r.Processes = processes.String
		r.Dependencies = dependencies.String
		r.IsIpa = isIpa.Valid && isIpa.Int64 != 0
		r.Size = size.Int64
		r.Mtime = mtime.String
		r.Title = title.String
		r.Description = description.String
		r.Company = company.String
		r.Copyright = copyright.String
		r.Product = product.String
		r.FileVersion = fileVersion.String
		r.TargetFramework = targetFramework.String
		r.Note = note.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// PluginConfigRow 一个插件配置文件的落库行（plugin_config 表——cfg 的分节 / 选项 / 作者注释以 JSON 整段存放）。
type PluginConfigRow struct {
	// FilePath cfg 绝对路径。
	FilePath string
	// FileName cfg 文件名。
	FileName string
	// PluginName 文件头读出的插件名。
	PluginName string
	// PluginVersion 文件头读出的插件版本。
	PluginVersion string
	// Guid 文件头读出的插件 GUID。
	Guid string
	// Size cfg 字节数。
	Size int64
	// Mtime cfg 修改时间戳（UTC）。
	Mtime string
	// Sections 解析结果 JSON（分节 / 选项 / 注释全在内）。
	Sections string
	// SectionCount 分节数。
	SectionCount int64
	// OptionCount 选项数。
	OptionCount int64
	// ReadAt 入库时刻（UTC）。
	ReadAt string
	// Error 解析失败原因。
	Error string
}

// LoadPluginConfigs 全部插件配置文件（按 GUID / 文件名排序——不含整段 JSON）。
func (d *DB) LoadPluginConfigs() ([]PluginConfigRow, error) {
	rows, err := d.sql.Query(`SELECT file_path, file_name, plugin_name, plugin_version, guid,
		size, mtime, section_count, option_count, read_at, error
		FROM plugin_config ORDER BY guid, file_name`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []PluginConfigRow
	for rows.Next() {
		var r PluginConfigRow
		var filePath, fileName, pluginName, pluginVersion, guid, mtime, readAt, errText sql.NullString
		var size, sectionCount, optionCount sql.NullInt64
		if err = rows.Scan(&filePath, &fileName, &pluginName, &pluginVersion, &guid,
			&size, &mtime, &sectionCount, &optionCount, &readAt, &errText); err != nil {
			return nil, err
		}
		r.FilePath = filePath.String
		r.FileName = fileName.String
		r.PluginName = pluginName.String
		r.PluginVersion = pluginVersion.String
		r.Guid = guid.String
		r.Size = size.Int64
		r.Mtime = mtime.String
		r.SectionCount = sectionCount.Int64
		r.OptionCount = optionCount.Int64
		r.ReadAt = readAt.String
		r.Error = errText.String
		list = append(list, r)
	}
	return list, rows.Err()
}

// LoadPluginConfigByGuid 按 GUID 取一个配置文件（含整段 JSON；同 GUID 多份取文件名第一个）。
func (d *DB) LoadPluginConfigByGuid(guid string) (*PluginConfigRow, error) {
	if guid == "" {
		return &PluginConfigRow{}, nil
	}
	var r PluginConfigRow
	var filePath, fileName, pluginName, pluginVersion, outGuid, mtime, sections, readAt, errText sql.NullString
	var size, sectionCount, optionCount sql.NullInt64
	err := d.sql.QueryRow(`SELECT file_path, file_name, plugin_name, plugin_version, guid,
		size, mtime, sections, section_count, option_count, read_at, error
		FROM plugin_config WHERE guid=? ORDER BY file_name LIMIT 1`, guid).
		Scan(&filePath, &fileName, &pluginName, &pluginVersion, &outGuid,
			&size, &mtime, &sections, &sectionCount, &optionCount, &readAt, &errText)
	if err == sql.ErrNoRows {
		return &PluginConfigRow{}, nil
	}
	if err != nil {
		return nil, err
	}
	r.FilePath = filePath.String
	r.FileName = fileName.String
	r.PluginName = pluginName.String
	r.PluginVersion = pluginVersion.String
	r.Guid = outGuid.String
	r.Size = size.Int64
	r.Mtime = mtime.String
	r.Sections = sections.String
	r.SectionCount = sectionCount.Int64
	r.OptionCount = optionCount.Int64
	r.ReadAt = readAt.String
	r.Error = errText.String
	return &r, nil
}
