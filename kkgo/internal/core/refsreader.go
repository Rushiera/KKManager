// Package core — 引用补读（RefsReader）+ 重复副本 MD5 档案补齐（FillHashes）。
//
// 契约对齐 KKManager/Core/RefsReader.cs 与 Data/StoreHub.cs §FillHashes：
//   - 引用补读只读 card 表列出的文件，不枚举目录、不重建卡片行；按库分组事务；
//   - MD5 只算传入的重复副本文件，按 size + mtime 命中档案复用，未命中现算并落档。
package core

import (
	"os"
	"path/filepath"
	"strconv"
	"time"

	"kkgo/internal/store"
)

// RefsReadResult 引用补读结果——卡数 / 有引用数 / 引用行数 / 失败数。
type RefsReadResult struct {
	// Cards 处理的卡片数。
	Cards int64
	// WithRefs 有引用（ModRefs 非空）的卡片数。
	WithRefs int64
	// RefRows 写入的引用行数（未去重，含部件粒度）。
	RefRows int64
	// Failed 读卡失败数。
	Failed int64
}

// CardTargetRef 一张待补引用的卡片（编号 + 路径 + 所在库句柄）。
type CardTargetRef struct {
	// CardId 卡片行 id。
	CardId int64
	// FilePath 卡片绝对路径。
	FilePath string
	// DB 该卡所在的库连接。
	DB *store.DB
}

// CardsOfRootHub 跨库取某库根的卡片目标（id + 路径 + 所在库）——对齐 C# StoreHub.CardsOfRoot。
func CardsOfRootHub(hub *store.Hub, cfg *RootsConfig, rootPath, pendingStep string) ([]CardTargetRef, error) {
	var list []CardTargetRef
	for _, ref := range AllStoreRefs(hub, cfg) {
		rows, err := ref.DB.QueryCardIdsByRoot(rootPath, pendingStep)
		if err != nil {
			return nil, err
		}
		for _, one := range rows {
			list = append(list, CardTargetRef{CardId: one.Id, FilePath: one.FilePath, DB: ref.DB})
		}
	}
	return list, nil
}

// RefsReadRootHub 补读某库根下卡片的引用——对齐 C# RefsReader.ReadRoot。
// onlyPending=true 时只处理无 refs 戳的卡（分批续跑），limit<=0 = 不限。
func RefsReadRootHub(hub *store.Hub, cfg *RootsConfig, rootPath string, onlyPending bool, limit int, logf func(string)) (*RefsReadResult, error) {
	res := &RefsReadResult{}
	pending := ""
	if onlyPending {
		pending = "refs"
	}
	cards, err := CardsOfRootHub(hub, cfg, rootPath, pending)
	if err != nil {
		return nil, err
	}
	if limit > 0 && len(cards) > limit {
		cards = cards[:limit]
	}
	if logf != nil {
		suffix := ""
		if onlyPending {
			suffix = "（只含未补的）"
		}
		logf("待补引用卡片：" + strconv.Itoa(len(cards)) + suffix)
	}
	var current *store.DB
	fail := func(err error) (*RefsReadResult, error) {
		if current != nil {
			_ = current.Rollback()
		}
		return res, err
	}
	for _, t := range cards {
		if t.DB != current {
			if current != nil {
				if err = current.Commit(); err != nil {
					return fail(err)
				}
			}
			current = t.DB
			if err = current.Begin(); err != nil {
				return fail(err)
			}
		}
		res.Cards++
		info, cardErr := ParseCard(t.FilePath)
		if cardErr != nil {
			res.Failed++
			if logf != nil {
				logf("  ! 读卡失败：" + filepath.Base(t.FilePath) + "（" + cardErr.Error() + "）")
			}
			continue
		}
		ids := info.DistinctModIds()
		if err = current.ReplaceCardRefs(t.CardId, ids); err != nil {
			return fail(err)
		}
		if err = current.UpdateCardRefsMeta(t.FilePath, len(ids), info.UarBlocks); err != nil {
			return fail(err)
		}
		if err = current.MarkScanStep(t.FilePath, "refs", info.Size, ""); err != nil {
			return fail(err)
		}
		res.RefRows += int64(len(info.ModRefs))
		if len(info.ModRefs) > 0 {
			res.WithRefs++
		}
	}
	if current != nil {
		if err = current.Commit(); err != nil {
			return res, err
		}
	}
	return res, nil
}

// FillHashesHub 补齐一组 mod 文件的 MD5 档案——对齐 C# StoreHub.FillHashes。
// 返回「路径 → MD5」映射 / 本次新增档案数 / 算不出的条目（具名出声）。
func FillHashesHub(hub *store.Hub, files []store.ModFileRec) (map[string]string, int, []string, error) {
	result := make(map[string]string, len(files))
	var errs []string
	computed := 0
	if len(files) == 0 {
		return result, 0, errs, nil
	}
	known, err := hub.Core().LoadModHashes()
	if err != nil {
		return nil, 0, nil, err
	}
	for _, f := range files {
		if f.FilePath == "" {
			continue
		}
		size := f.Size
		mtime := f.Mtime
		if fi, statErr := os.Stat(f.FilePath); statErr == nil {
			size = fi.Size()
			mtime = fi.ModTime().UTC().Format(stampUTC)
		}
		if hit, ok := known[f.FilePath]; ok && hit.Size == size && hit.Mtime == mtime && hit.Md5 != "" {
			result[f.FilePath] = hit.Md5
			continue
		}
		md5, hashErr := FileMd5(f.FilePath)
		if md5 == "" {
			reason := "算不出"
			if hashErr != nil {
				reason = hashErr.Error()
			}
			errs = append(errs, f.FileName+" → "+reason)
			continue
		}
		result[f.FilePath] = md5
		rec := store.ModHashRecord{FilePath: f.FilePath, Size: size, Mtime: mtime, Md5: md5, HashedAt: nowStampUTC()}
		if err = hub.Core().PutModHash(rec); err != nil {
			return result, computed, errs, err
		}
		computed++
	}
	return result, computed, errs, nil
}

// stampUTC 时间戳格式（对齐 C# Store.StampFormat = "yyyy-MM-ddTHH:mm:ss.fff"，UTC）。
const stampUTC = "2006-01-02T15:04:05.000"

// nowStampUTC 当前 UTC 时间戳文本（对齐 C# Store.Now）。
func nowStampUTC() string {
	return time.Now().UTC().Format(stampUTC)
}

// OldInfix 旧版文件名的中缀（加在扩展名前，如 xxx.old.zipmod）——对齐 C# RootsRules.OldInfix。
const OldInfix = ".old"

// IsOldFileName 文件名是否带旧版中缀（扩展名前的 .old 段）——对齐 C# RootsRules.IsOldFileName。
func IsOldFileName(fileName string) bool {
	if fileName == "" {
		return false
	}
	ext := filepath.Ext(fileName)
	stem := fileName
	if ext != "" {
		stem = fileName[:len(fileName)-len(ext)]
	}
	return len(stem) >= len(OldInfix) && equalFoldTail(stem, OldInfix)
}

// equalFoldTail 判断 s 的尾段是否等于 suffix（ASCII 忽略大小写）。
func equalFoldTail(s, suffix string) bool {
	if len(s) < len(suffix) {
		return false
	}
	tail := s[len(s)-len(suffix):]
	for i := 0; i < len(suffix); i++ {
		a, b := tail[i], suffix[i]
		if a >= 'A' && a <= 'Z' {
			a += 'a' - 'A'
		}
		if b >= 'A' && b <= 'Z' {
			b += 'a' - 'A'
		}
		if a != b {
			return false
		}
	}
	return true
}
