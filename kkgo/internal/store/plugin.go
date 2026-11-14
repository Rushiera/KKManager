// 插件库写面——plugin_file（dll 元数据 / 一个 dll 可含多项）与 plugin_config（cfg 解析结果）。
//
// 表恒在主库（StoreHub.Core）；扫描链只经 hub.Core() 调用。对齐 C# Data/Store.cs 的同名写方法：
// SavePlugin / SavePluginConfig / DeletePluginFile / DeletePluginConfigsUnderRoot / LoadPlugins / LoadPluginConfigs。
package store

import "strings"

// SavePlugin 写入一项插件（file_path + guid 唯一——一个 dll 可含多项；guid 为空表示该 dll 未解析出插件特性）。
func (d *DB) SavePlugin(row *PluginRow) error {
	if row == nil {
		return nil
	}
	_, err := d.execer().Exec(`INSERT OR REPLACE INTO plugin_file(
		file_path, guid, root_path, file_name, name, version, processes, dependencies, is_ipa, size, mtime, scan_time,
		title, description, company, copyright, product, file_version, target_framework, note)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		row.FilePath, row.Guid, row.RootPath, row.FileName, row.Name, row.Version,
		row.Processes, row.Dependencies, boolInt(row.IsIpa), row.Size, row.Mtime, Now(),
		row.Title, row.Description, row.Company, row.Copyright, row.Product,
		row.FileVersion, row.TargetFramework, row.Note)
	return err
}

// SavePluginConfig 写入一个插件配置文件（cfg 解析结果——分节 / 选项 / 注释整段 JSON 落库）。
func (d *DB) SavePluginConfig(row *PluginConfigRow) error {
	if row == nil {
		return nil
	}
	readAt := row.ReadAt
	if readAt == "" {
		readAt = Now()
	}
	_, err := d.execer().Exec(`INSERT OR REPLACE INTO plugin_config(
		file_path, file_name, plugin_name, plugin_version, guid,
		size, mtime, sections, section_count, option_count, read_at, error)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?)`,
		row.FilePath, row.FileName, row.PluginName, row.PluginVersion, row.Guid,
		row.Size, row.Mtime, row.Sections, row.SectionCount, row.OptionCount, readAt, row.Error)
	return err
}

// DeletePluginFile 删掉某个 dll 的全部插件行（重解析前先清——同一 dll 的插件项可能变少）。
func (d *DB) DeletePluginFile(filePath string) error {
	_, err := d.execer().Exec("DELETE FROM plugin_file WHERE file_path=?", filePath)
	return err
}

// DeletePluginConfigsUnderRoot 清掉某个插件库根下的全部配置行（重扫前先清——cfg 删掉了行也要跟着没）。
// 判据与 C# 一致：file_path LIKE <根>\%（根去尾部斜杠 + 反斜杠通配）。
func (d *DB) DeletePluginConfigsUnderRoot(pluginRoot string) error {
	if pluginRoot == "" {
		return nil
	}
	_, err := d.execer().Exec("DELETE FROM plugin_config WHERE file_path LIKE ?", strings.TrimRight(pluginRoot, `\/`)+`\%`)
	return err
}

// SaveCardAnalysis 落一份卡片分项分析（服装槽位 / 卡片分析 / 场景深度——kind 区分，data 为 JSON 文本）。
func (d *DB) SaveCardAnalysis(cardId int64, kind, filePath string, size int64, mtime, data string) error {
	if cardId <= 0 || kind == "" {
		return nil
	}
	_, err := d.execer().Exec(`INSERT INTO card_analysis(card_id,kind,file_path,size,mtime,data,read_at)
		VALUES(?,?,?,?,?,?,?)
		ON CONFLICT(card_id,kind) DO UPDATE SET
		  file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
		  data=excluded.data, read_at=excluded.read_at`,
		cardId, kind, filePath, size, mtime, data, Now())
	return err
}
