// 索引导入——读 kkc 风格索引（`整理/index/` 分片或 `整理/index.tsv` 全量）写库，不扫文件系统。
//
// 索引是唯一真相源：卡片 / mod 副本 / 索引元数据全部来自索引文件；文件存在性不做 stat。
// 对齐 C# Core/IndexImporter.cs（Import / ReadManifest / ReadRows / ImportRow / HashFile）。
package core

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"kkgo/internal/store"
)

// IndexImportResult 索引导入结果——行数统计 / 索引目录 / 摘要（对齐 C# IndexImportResult）。
type IndexImportResult struct {
	// IndexDir 实际使用的索引目录。
	IndexDir string
	// Rows 索引总行数（含跳过行）。
	Rows int64
	// Cards 写入的卡片行数。
	Cards int64
	// Mods 写入的 mod 副本行数（有 guid 的 mod 行）。
	Mods int64
	// Others 其它 + 无 guid 的 mod + 跳过行（只进 idx_meta）。
	Others int64
	// RefRows 写入的引用行数（索引「引用mod」列，去重 guid 粒度）。
	RefRows int64
	// Digest 索引摘要（manifest.tsv / index.tsv 的 SHA256 前 16 位）。
	Digest string
	// Error 失败原因——非空表示未导入。
	Error string
}

// indexCommitRows 提交批次行数——每 N 行提交一轮。
const indexCommitRows = 2000

// DefaultIndexDir 默认索引目录——<root>/整理/index。
func DefaultIndexDir(rootPath string) string {
	return filepath.Join(rootPath, "整理", "index")
}

// ImportIndex 导入一个库根的索引。
//
// modRoot 是 mod 库根条目；cardRoot 为空时沿用 modRoot；indexDir 空 = 默认目录。
// 事务按 indexCommitRows 分批提交（跨分片统一计数）；出错整批回滚。
func ImportIndex(hub *store.Hub, modRoot, cardRoot *RootEntry, indexDir string, log func(string)) (*IndexImportResult, error) {
	if cardRoot == nil {
		cardRoot = modRoot
	}
	result := &IndexImportResult{}
	dir := indexDir
	if strings.TrimSpace(dir) == "" {
		dir = DefaultIndexDir(modRoot.Path)
	}
	manifest := filepath.Join(dir, "manifest.tsv")
	sibling := filepath.Join(filepath.Dir(strings.TrimRight(strings.TrimRight(dir, `\`), "/")), "index.tsv")
	files := make([]string, 0, 32)
	relBase := modRoot.Path
	if fileExists(manifest) {
		result.IndexDir = dir
		result.Digest = hashFilePrefix(manifest)
		files = append(files, readManifestFiles(manifest, dir)...)
		relBase = filepath.Dir(strings.TrimRight(strings.TrimRight(dir, `\`), "/"))
	} else if fileExists(filepath.Join(dir, "index.tsv")) {
		result.IndexDir = dir
		full := filepath.Join(dir, "index.tsv")
		result.Digest = hashFilePrefix(full)
		files = append(files, full)
		relBase = dir
	} else if fileExists(sibling) {
		result.IndexDir = filepath.Dir(sibling)
		result.Digest = hashFilePrefix(sibling)
		files = append(files, sibling)
		relBase = result.IndexDir
	} else {
		result.Error = "索引不存在：" + dir + "（manifest.tsv / index.tsv 均无）"
		return result, nil
	}
	if len(files) == 0 {
		result.Error = "索引分片清单为空：" + manifest
		return result, nil
	}

	modStore, err := hub.StoreFor(modRoot.Tier, modRoot.Locked, modRoot.Path, true)
	if err != nil {
		return nil, err
	}
	var cardShards []*store.DB
	cardBase, err := hub.CardBaseOf(cardRoot.Path)
	if err != nil {
		return nil, err
	}
	if cardBase > 0 {
		shards, err := hub.ShardCountOf(cardBase)
		if err != nil {
			return nil, err
		}
		cardShards, err = hub.ShardsOf(cardBase, shards)
		if err != nil {
			return nil, err
		}
	}
	// 旧库文件按「结构已就绪」打开、不跑完整 EnsureSchema——导入前补索引表（幂等）
	if err = modStore.EnsureIndexTables(); err != nil {
		return nil, err
	}
	if err = hub.Core().EnsureIndexTables(); err != nil {
		return nil, err
	}
	for _, s := range cardShards {
		if err = s.EnsureIndexTables(); err != nil {
			return nil, err
		}
	}
	if log != nil {
		log("索引：" + result.IndexDir + "（" + strconv.Itoa(len(files)) + " 个分片）")
		desc := "库根：" + modRoot.Path + " → mod " + filepath.Base(modStore.Path())
		if len(cardShards) > 0 {
			desc += " · 卡片 " + strconv.Itoa(len(cardShards)) + " 片"
		}
		log(desc)
	}

	// 写入事务：mod 库 + 全部卡片分片一起 Begin，每 indexCommitRows 行提交一轮。
	txStores := make([]*store.DB, 0, len(cardShards)+1)
	txStores = append(txStores, modStore)
	for _, s := range cardShards {
		dup := false
		for _, exist := range txStores {
			if exist == s {
				dup = true
				break
			}
		}
		if !dup {
			txStores = append(txStores, s)
		}
	}
	pending := 0
	beginAll := func() error {
		for _, s := range txStores {
			if err := s.Begin(); err != nil {
				return err
			}
		}
		return nil
	}
	commitAll := func() error {
		for _, s := range txStores {
			if err := s.Commit(); err != nil {
				return err
			}
		}
		return nil
	}
	rollbackAll := func() {
		for _, s := range txStores {
			if err := s.Rollback(); err != nil && log != nil {
				log("回滚失败：" + s.Path() + " · " + err.Error())
			}
		}
	}
	if err = beginAll(); err != nil {
		rollbackAll()
		return nil, err
	}
	for _, f := range files {
		if !fileExists(f) {
			if log != nil {
				log("  ! 分片缺失，跳过：" + f)
			}
			continue
		}
		rows, readErr := readTSVRows(f)
		if readErr != nil {
			rollbackAll()
			return nil, readErr
		}
		for _, row := range rows {
			if err = importIndexRow(hub, modRoot, cardRoot, relBase, row, cardShards, modStore, result); err != nil {
				rollbackAll()
				return nil, err
			}
			pending++
			if pending >= indexCommitRows {
				if err = commitAll(); err != nil {
					rollbackAll()
					return nil, err
				}
				if err = beginAll(); err != nil {
					rollbackAll()
					return nil, err
				}
				pending = 0
			}
		}
	}
	if err = commitAll(); err != nil {
		rollbackAll()
		return nil, err
	}

	if err = hub.Core().UpsertIndexSource(modRoot.Path, result.IndexDir, "kkc-index-v1",
		result.Rows, result.Cards, result.Mods, result.Others, result.Digest); err != nil {
		return nil, err
	}
	// 收尾：清孤儿引用（删卡 / 换根后的 card_mod 残行）——幂等
	orphans := 0
	for _, s := range cardShards {
		n, cleanErr := s.CleanOrphanRefs()
		if cleanErr != nil {
			return nil, cleanErr
		}
		orphans += n
	}
	if n, cleanErr := modStore.CleanOrphanRefs(); cleanErr != nil {
		return nil, cleanErr
	} else {
		orphans += n
	}
	if orphans > 0 && log != nil {
		log("清理孤儿引用行：" + strconv.Itoa(orphans))
	}
	if _, err = hub.Core().RefreshModAuthors(); err != nil {
		return nil, err
	}
	if log != nil {
		log("导入完成：行 " + strconv.FormatInt(result.Rows, 10) + " · 卡 " + strconv.FormatInt(result.Cards, 10) +
			" · mod 副本 " + strconv.FormatInt(result.Mods, 10) + " · 其它 " + strconv.FormatInt(result.Others, 10) +
			" · 引用 " + strconv.FormatInt(result.RefRows, 10))
	}
	return result, nil
}

// readManifestFiles 读分片清单——manifest.tsv 的「文件」列；相对路径按索引目录解析。
func readManifestFiles(manifestPath, dir string) []string {
	list := []string{}
	f, err := os.Open(manifestPath)
	if err != nil {
		return list
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	if !sc.Scan() {
		return list
	}
	header := strings.Split(strings.TrimPrefix(sc.Text(), "\ufeff"), "\t")
	fileCol := 0
	for i, col := range header {
		if col == "文件" {
			fileCol = i
			break
		}
	}
	for sc.Scan() {
		line := sc.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if len(parts) <= fileCol || strings.TrimSpace(parts[fileCol]) == "" {
			continue
		}
		rel := strings.ReplaceAll(strings.TrimSpace(parts[fileCol]), "/", string(filepath.Separator))
		list = append(list, filepath.Join(dir, rel))
	}
	return list
}

// readTSVRows 读一个 TSV 分片——返回「列名 → 值」字典序列。
func readTSVRows(path string) ([]map[string]string, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 8*1024*1024)
	if !sc.Scan() {
		return nil, nil
	}
	cols := strings.Split(strings.TrimPrefix(sc.Text(), "\ufeff"), "\t")
	var out []map[string]string
	for sc.Scan() {
		line := sc.Text()
		if line == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		row := make(map[string]string, len(cols))
		for i, col := range cols {
			if i < len(parts) {
				row[col] = parts[i]
			} else {
				row[col] = ""
			}
		}
		out = append(out, row)
	}
	return out, sc.Err()
}

// ImportIndexRow 导入一行索引——卡写卡片分片库；mod 写 mod 库（无 guid 只进 idx_meta）；其它只进 idx_meta。
func importIndexRow(hub *store.Hub, modRoot, cardRoot *RootEntry, relBase string,
	row map[string]string, cardShards []*store.DB, modStore *store.DB, result *IndexImportResult) error {
	result.Rows++
	kind := tsvGet(row, "类型")
	rel := strings.ReplaceAll(strings.TrimSpace(tsvGet(row, "现路径")), "/", string(filepath.Separator))
	full := filepath.Join(relBase, rel)
	idxId := tsvGet(row, "ID")
	cardType := tsvGet(row, "卡类型")
	chara := tsvGet(row, "角色名")
	work := tsvGet(row, "作品")
	author := tsvGet(row, "作者")
	source := tsvGet(row, "来源")
	srcRel := tsvGet(row, "包内路径")
	note := tsvGet(row, "备注")
	guid := tsvGet(row, "guid")
	size := tsvParseLong(tsvGet(row, "大小"))

	meta := store.IdxMetaRow{
		FilePath: full, IdxId: idxId, Kind: kind, CardType: cardType, Chara: chara, Work: work,
		Author: author, Source: source, SrcRel: srcRel, Note: note, Guid: guid,
		Series: tsvGet(row, "系列"), VnId: tsvGet(row, "VN_ID"), VnName: tsvGet(row, "VN名"),
		VndbCharaId: tsvGet(row, "VNDB角色ID"), CharaIdConf: tsvGet(row, "角色ID置信度"),
		Category: tsvGet(row, "大类"), SubCategory: tsvGet(row, "子类"),
	}

	// 跳过行（现路径 = 「（跳过…）」）——只登记元数据，不写卡片 / mod
	if strings.HasPrefix(rel, "（") || rel == "" {
		meta.IdxId = "[跳过]" + idxId
		if err := modStore.UpsertIndexMeta(meta); err != nil {
			return err
		}
		result.Others++
		return nil
	}

	folder := filepath.Dir(rel)
	if folder == "." {
		folder = ""
	}
	if kind == "卡" && len(cardShards) > 0 {
		shard := store.ShardOfPath(full, len(cardShards))
		cs := cardShards[shard]
		fileName := filepath.Base(full)
		cardId, err := cs.UpsertCardRow(full, fileName, size, "", cardRoot.Tier, cardRoot.Path, folder)
		if err != nil {
			return err
		}
		if err = cs.UpdateCardHead(full, cardType, "", 0); err != nil {
			return err
		}
		if chara != "" {
			if err = cs.UpdateCardName(full, chara); err != nil {
				return err
			}
		}
		if err = cs.UpsertIndexMeta(meta); err != nil {
			return err
		}
		// 引用列（索引自带）——每 guid 一行写 card_mod，mod_count 同步
		if refsCell := tsvGet(row, "引用mod"); refsCell != "" {
			refs := make([]string, 0, 8)
			for _, g := range strings.Split(refsCell, ",") {
				id := strings.TrimSpace(g)
				if id != "" {
					refs = append(refs, id)
				}
			}
			if len(refs) > 0 {
				if err = cs.ReplaceCardRefs(cardId, refs); err != nil {
					return err
				}
				if err = cs.UpdateCardRefsMeta(full, len(refs), 0); err != nil {
					return err
				}
				result.RefRows += int64(len(refs))
			}
		}
		result.Cards++
		return nil
	}
	if kind == "mod" {
		// 索引的 guid 列可能为空（整理计划不解析 manifest）——按需读一次容器中央目录补 guid
		if guid == "" && fileExists(full) {
			parsed := ParseZipMod(full)
			if parsed.ErrorKind == ModErrNone && parsed.Guid != "" {
				guid = parsed.Guid
				if note == "" {
					note = "guid 补读"
				} else {
					note += "；guid 补读"
				}
				meta.Guid = guid
				meta.Note = note
			}
		}
		if guid != "" {
			isNew, err := modStore.HasModRow(guid)
			if err != nil {
				return err
			}
			in := store.ModFileInput{
				Guid: guid, FilePath: full, FileName: filepath.Base(full),
				Size: size, Tier: modRoot.Tier, RootPath: modRoot.Path,
				Mtime: "", Name: strings.TrimSuffix(filepath.Base(full), filepath.Ext(full)),
				Author: author, Found: map[string]bool{"name": true, "author": author != ""},
			}
			if err = modStore.UpsertModFile(in); err != nil {
				return err
			}
			if isNew {
				if err = modStore.FillModMeta(in); err != nil {
					return err
				}
			}
			result.Mods++
		} else {
			// 仍无 guid（manifest 读不到）——只进元数据，不伪造 guid
			result.Others++
		}
		if err := modStore.UpsertIndexMeta(meta); err != nil {
			return err
		}
		return nil
	}
	// 其它 / 纯图——只进元数据
	if err := modStore.UpsertIndexMeta(meta); err != nil {
		return err
	}
	result.Others++
	return nil
}

// tsvGet 取列值（缺列返回空串）。
func tsvGet(row map[string]string, key string) string {
	if v, ok := row[key]; ok {
		return v
	}
	return ""
}

// tsvParseLong 解析整数（空 / 非法返回 0）。
func tsvParseLong(text string) int64 {
	v, err := strconv.ParseInt(strings.TrimSpace(text), 10, 64)
	if err != nil {
		return 0
	}
	return v
}

// hashFilePrefix 文件摘要（SHA256 前 16 位小写）——索引变更检测用。
func hashFilePrefix(path string) string {
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	h := sha256.New()
	if _, err = io.Copy(h, f); err != nil {
		return ""
	}
	sum := h.Sum(nil)
	return hex.EncodeToString(sum[:8])
}

// SortIdxRows 按 file_path 升序整理索引行（对拍取稳定顺序用）。
func SortIdxRows(rows []store.IdxMetaRow) {
	sort.Slice(rows, func(i, j int) bool { return rows[i].FilePath < rows[j].FilePath })
}

// indexImporterReady 编译期占位：保证 fmt 在无日志分支也有引用（防未使用导入）。
var _ = fmt.Sprintf
