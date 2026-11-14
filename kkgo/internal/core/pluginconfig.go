package core

import (
	"encoding/json"
	"strings"
)

// 插件配置文件（BepInEx/config/*.cfg）的 JSON 结构——与 C# Core/PluginConfigReader.cs 的
// PluginConfigFile / PluginConfigSection / PluginConfigOption 同形（plugin_config.sections 列的原样 JSON）。

// PluginConfigOption cfg 里的一项配置（键 / 当前值 / 作者写的说明 / 类型与默认值）。
type PluginConfigOption struct {
	// Key 配置键（cfg 等号左边的名字）。
	Key string `json:"key"`
	// Value 当前值（cfg 等号右边的文本）。
	Value string `json:"value"`
	// Comment 作者写的说明（cfg 里键上方的 ## 注释，可多行合成一段）。
	Comment string `json:"comment"`
	// Type 设置类型（「# Setting type:」——如 Boolean / Int32 / Single / String / KeyboardShortcut）。
	Type string `json:"type"`
	// Def 默认值（「# Default value:」）。
	Def string `json:"def"`
	// Acceptable 可选值（「# Acceptable values:」——枚举型才有）。
	Acceptable string `json:"acceptable"`
}

// PluginConfigSection cfg 里的一个分节（配置管理器里的一栏）。
type PluginConfigSection struct {
	// Name 节名（cfg 的 [方括号] 里的名字）。
	Name string `json:"name"`
	// Options 本节的全部配置项（保持文件顺序）。
	Options []PluginConfigOption `json:"options"`
}

// PluginConfigFile 一个插件配置文件（分节清单 + 文件头信息）。
type PluginConfigFile struct {
	// FilePath cfg 绝对路径。
	FilePath string `json:"filePath"`
	// FileName cfg 文件名。
	FileName string `json:"fileName"`
	// PluginName 插件显示名（文件头读出）。
	PluginName string `json:"pluginName"`
	// PluginVersion 插件版本（文件头读出）。
	PluginVersion string `json:"pluginVersion"`
	// Guid 插件 GUID（文件头「## Plugin GUID:」——老配置文件可能没有）。
	Guid string `json:"guid"`
	// Size cfg 字节数。
	Size int64 `json:"size"`
	// Mtime cfg 修改时间戳文本（UTC）。
	Mtime string `json:"mtime"`
	// Sections 分节清单（保持文件顺序）。
	Sections []PluginConfigSection `json:"sections"`
	// Error 解析失败原因（空 = 成功）。
	Error string `json:"error"`
}

// ParsePluginConfigJSON 从 JSON 反序列化（空 / 非法返回空对象——解析失败在 Error 里出声）。
func ParsePluginConfigJSON(text string) *PluginConfigFile {
	file := &PluginConfigFile{Sections: []PluginConfigSection{}}
	if strings.TrimSpace(text) == "" {
		return file
	}
	var raw PluginConfigFile
	if err := json.Unmarshal([]byte(text), &raw); err != nil {
		file.Error = "配置 JSON 解析失败：" + err.Error()
		return file
	}
	if raw.Sections == nil {
		raw.Sections = []PluginConfigSection{}
	}
	return &raw
}
