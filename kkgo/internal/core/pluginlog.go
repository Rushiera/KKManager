// BepInEx 日志（LogOutput.log）只读解析——插件加载 / 进程过滤跳过 / 错误三类事实。
// 契约对齐 C# Core/PluginLogReader.cs（同名结构 / 同名判定 / 同优先级）。
package core

import (
	"bufio"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// PluginLogSummary 一次 BepInEx 启动的插件加载实况（只读解析 LogOutput.log 所得——日志是运行期产物，不入库）。
type PluginLogSummary struct {
	// FilePath 日志文件绝对路径。
	FilePath string
	// FileTime 日志修改时间（本次启动时刻，本地时间 yyyy-MM-dd HH:mm:ss）。
	FileTime string
	// FileSize 日志字节数。
	FileSize int64
	// ToLoad 「N plugins to load」里的 N（0 = 未读到）。
	ToLoad int
	// Loaded 实际加载的插件（「Loading [名 版本]」逐行）。
	Loaded []string
	// Skipped 被进程过滤跳过的插件（「名 版本 → 原因」）。
	Skipped []string
	// LoadedNames 已加载插件的名称集合（去版本——供行级标识查表；大小写不敏感）。
	LoadedNames map[string]bool
	// SkippedNames 被进程过滤跳过插件的名称集合（去版本——供行级标识查表）。
	SkippedNames map[string]bool
	// ErrorNames 错误行里点名的插件名集合（「[Error :名] ...」——供行级标识查表）。
	ErrorNames map[string]bool
	// Errors 错误行（[Error ...] 原文收下，不做二次解释）。
	Errors []string
	// Error 解析失败原因（空 = 成功）。
	Error string
}

// LogPathOf 插件库根下的日志路径（无插件库根返回空串）。
func LogPathOf(cfg *RootsConfig) string {
	if cfg == nil || cfg.PluginRoots == nil {
		return ""
	}
	for _, e := range cfg.PluginRoots {
		if strings.TrimSpace(e.Path) != "" {
			return filepath.Join(e.Path, "LogOutput.log")
		}
	}
	return ""
}

// PluginLogStateOf 某个插件在某次日志里的加载实况——取键值（供行级标识着色；呈现归前端）。
// 返回 loaded（本次已加载）· skipped（进程过滤跳过）· error（日志点名有错误）· none（日志未提及）；
// 判定优先级 error → loaded → skipped（对齐 C# PluginLogReader.StateOf）。
func PluginLogStateOf(summary *PluginLogSummary, name string) string {
	if summary == nil || name == "" {
		return "none"
	}
	key := strings.ToLower(name)
	if summary.ErrorNames[key] {
		return "error"
	}
	if summary.LoadedNames[key] {
		return "loaded"
	}
	if summary.SkippedNames[key] {
		return "skipped"
	}
	return "none"
}

// ReadPluginLog 解析日志——逐行扫关键行（待加载总数 / 加载 / 进程过滤跳过 / 错误）。
// 失败时 error 出声，其余字段为空（对齐 C# PluginLogReader.Read）。
func ReadPluginLog(path string) *PluginLogSummary {
	s := &PluginLogSummary{
		FilePath:     path,
		LoadedNames:  map[string]bool{},
		SkippedNames: map[string]bool{},
		ErrorNames:   map[string]bool{},
	}
	if strings.TrimSpace(path) == "" {
		s.Error = "日志不存在（先启动一次游戏，BepInEx 才会写日志）：" + path
		return s
	}
	info, err := os.Stat(path)
	if err != nil || info.IsDir() {
		s.Error = "日志不存在（先启动一次游戏，BepInEx 才会写日志）：" + path
		return s
	}
	s.FileSize = info.Size()
	s.FileTime = info.ModTime().Format("2006-01-02 15:04:05")
	file, err := os.Open(path)
	if err != nil {
		s.Error = err.Error()
		return s
	}
	defer file.Close()
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		line := scanner.Text()
		// [段1] 待加载总数——「262 plugins to load」
		if at := strings.Index(line, "plugins to load"); at > 0 {
			end := at
			for end > 0 && line[end-1] == ' ' {
				end--
			}
			start := end
			for start > 0 && line[start-1] >= '0' && line[start-1] <= '9' {
				start--
			}
			if start < end {
				if value, convErr := strconv.Atoi(line[start:end]); convErr == nil {
					s.ToLoad = value
				}
			}
			continue
		}
		// [段2] 进程过滤跳过——「Skipping [名 版本] because of process filters (...)」
		if skipAt := strings.Index(line, "Skipping ["); skipAt >= 0 {
			open := skipAt + len("Skipping [")
			rel := strings.IndexByte(line[open:], ']')
			name := ""
			if rel > 0 {
				name = line[open : open+rel]
			}
			reason := "原因未记"
			if paren := strings.Index(line, "process filters ("); paren >= 0 {
				reason = "进程过滤：" + strings.TrimRight(line[paren+len("process filters ("):], ") ")
			}
			s.Skipped = append(s.Skipped, name+"  →  "+reason)
			if skipName := pluginNameOf(name); skipName != "" {
				s.SkippedNames[strings.ToLower(skipName)] = true
			}
			continue
		}
		// [段3] 实际加载——「Loading [名 版本]」
		if loadAt := strings.Index(line, "Loading ["); loadAt >= 0 {
			open := loadAt + len("Loading [")
			if rel := strings.IndexByte(line[open:], ']'); rel > 0 {
				item := line[open : open+rel]
				s.Loaded = append(s.Loaded, item)
				if loadName := pluginNameOf(item); loadName != "" {
					s.LoadedNames[strings.ToLower(loadName)] = true
				}
			}
			continue
		}
		// [段4] 错误行——原文收下（不二次解释）+ 记下被点名的来源（「[Error  :名] ...」）
		if errAt := strings.Index(line, "[Error"); errAt >= 0 {
			s.Errors = append(s.Errors, line)
			if src := errorSourceName(line, errAt); src != "" {
				s.ErrorNames[strings.ToLower(src)] = true
			}
		}
	}
	if err = scanner.Err(); err != nil {
		s.Error = err.Error()
	}
	return s
}

// pluginNameOf 日志条目（「名 版本」）取名称部分——去掉最后一个空格段（BepInEx 会规范化版本号，故不按版本比）。
func pluginNameOf(item string) string {
	if item == "" {
		return ""
	}
	if at := strings.LastIndexByte(item, ' '); at > 0 {
		return item[:at]
	}
	return item
}

// errorSourceName 错误行取被点名的来源名——「[Error  :名] 正文」里冒号与「]」之间那段。
func errorSourceName(line string, errAt int) string {
	i := errAt + len("[Error")
	for i < len(line) && (line[i] == ' ' || line[i] == ':') {
		i++
	}
	rel := strings.IndexByte(line[i:], ']')
	if rel <= 0 {
		return ""
	}
	return strings.TrimSpace(line[i : i+rel])
}
