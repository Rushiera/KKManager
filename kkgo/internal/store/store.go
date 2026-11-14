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

// DB 主库句柄（对应 C# 侧 Store 的 core 库）。
type DB struct {
	sql  *sql.DB
	path string
}

// Open 打开主库并启用与 C# 一致的连接参数（WAL · synchronous=NORMAL · busy_timeout 15 s）。
// 文件不存在时建最小 schema（只有 setting 表）并出声——C# 侧是从内嵌模板释放全量 schema，Go 侧待 G3 补。
func Open(path string) (*DB, error) {
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	fresh := false
	if _, statErr := os.Stat(abs); statErr != nil {
		fresh = true
		if dir := filepath.Dir(abs); dir != "" {
			if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
				return nil, mkErr
			}
		}
	}
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
	db := &DB{sql: handle, path: abs}
	if fresh {
		fmt.Println("[库] 主库不存在——按最小 schema 新建（只有 setting 表；全量 schema 待 G3 从模板释放）：" + abs)
		if err = db.ensureSetting(); err != nil {
			handle.Close()
			return nil, err
		}
	}
	return db, nil
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
