// 索引导入链的库维护编排——对齐 C# Data/StoreHub.cs
// §PurgeRoot / §PurgeSkipRows / §ConsumeCardMoves。
//
// 分层：store 只做单库读写与单行搬移；跨库编排（遍历哪些库、搬哪些记录）归本包。
package core

import (
	"path/filepath"
	"strconv"
	"strings"

	"kkgo/internal/store"
)

// PurgeRootHub 按库根前缀清理全部库的记录（换根 / 重建索引用）——
// card / mod_file / non_card / idx_meta / scan_state 逐库删，受影响 mod guid 重算主表；
// 返回受影响 guid 数。对齐 C# StoreHub.PurgeRoot。
func PurgeRootHub(hub *store.Hub, cfg *RootsConfig, rootPath string) (int, error) {
	if hub == nil {
		return 0, nil
	}
	refs := AllStoreRefs(hub, cfg)
	guids := make([]string, 0, 32)
	for _, ref := range refs {
		list, err := ref.DB.DeleteRecordsUnderRoot(rootPath)
		if err != nil {
			return 0, err
		}
		guids = append(guids, list...)
	}
	distinct := make(map[string]bool, len(guids))
	for _, g := range guids {
		if g != "" {
			distinct[g] = true
		}
	}
	dbs := make([]*store.DB, 0, len(refs))
	for _, ref := range refs {
		dbs = append(dbs, ref.DB)
	}
	for g := range distinct {
		if err := hub.RecomputeMod(dbs, g); err != nil {
			return 0, err
		}
	}
	return len(distinct), nil
}

// PurgeSkipRowsHub 清理全部库的索引跳过行——返回删除行数合计。对齐 C# StoreHub.PurgeSkipRows。
func PurgeSkipRowsHub(hub *store.Hub, cfg *RootsConfig) (int, error) {
	if hub == nil {
		return 0, nil
	}
	total := 0
	for _, ref := range AllStoreRefs(hub, cfg) {
		n, err := ref.DB.PurgeSkipRows()
		if err != nil {
			return total, err
		}
		total += n
	}
	return total, nil
}

// cardMovePlan 一条待搬的移动记录（源库 + 路径 + 目标分片 + 记录 id）。
type cardMovePlan struct {
	from     *store.DB
	fromPath string
	toPath   string
	to       *store.DB
	moveID   int64
}

// ConsumeCardMovesHub 消费已完成的卡片移动记录（done 且未消费）——
// 把 DB 行从旧路径搬到新路径（保 id / 引用），标记 consumed；返回消费条数。
// 只处理 to_path 落在指定卡片库根下的记录；源行找不到时标记消费并出声（对齐 C# 语义）。
func ConsumeCardMovesHub(hub *store.Hub, cfg *RootsConfig, rootPath string, log func(string)) (int, error) {
	if hub == nil || cfg == nil {
		return 0, nil
	}
	prefix := strings.TrimRight(rootPath, `\/`)
	if prefix == "" {
		return 0, nil
	}
	var cardRoot *RootEntry
	for i := range cfg.CardRoots {
		one := strings.TrimRight(cfg.CardRoots[i].Path, `\/`)
		if strings.EqualFold(one, prefix) {
			cardRoot = &cfg.CardRoots[i]
			break
		}
	}
	if cardRoot == nil {
		// 库根口径：配置里的 `<x>\整理` 视同库根 `<x>`（kkc 接入开关只登记 整理 子目录）
		for i := range cfg.CardRoots {
			if strings.EqualFold(LibraryRootOf(cfg.CardRoots[i].Path), prefix) {
				cardRoot = &cfg.CardRoots[i]
				break
			}
		}
	}
	if cardRoot == nil {
		return 0, nil
	}
	moves, err := hub.Core().LoadCardMoves("done")
	if err != nil {
		return 0, err
	}
	if len(moves) == 0 {
		return 0, nil
	}
	// 库序号按导入根（库根）取——`<x>\整理` 可能另有历史映射，直接用它会把行搬错片
	base, err := hub.CardBaseOf(prefix)
	if err != nil {
		return 0, err
	}
	shards, err := hub.ShardCountOf(base)
	if err != nil {
		return 0, err
	}
	if shards <= 0 {
		shards = 1
	}
	shardDBs, err := hub.ShardsOf(base, shards)
	if err != nil {
		return 0, err
	}
	if len(shardDBs) == 0 {
		return 0, nil
	}
	refs := AllStoreRefs(hub, cfg)
	plans := make([]cardMovePlan, 0, len(moves))
	consumedIDs := make([]int64, 0, len(moves))
	wantPrefix := prefix + `\`
	for _, mv := range moves {
		if mv.Consumed {
			continue
		}
		if !strings.HasPrefix(strings.ToLower(mv.ToPath), strings.ToLower(wantPrefix)) {
			continue
		}
		var fromStore *store.DB
		for _, ref := range refs {
			id, idErr := ref.DB.CardIdOf(mv.FromPath)
			if idErr != nil {
				return 0, idErr
			}
			if id > 0 {
				fromStore = ref.DB
				break
			}
		}
		if fromStore == nil {
			consumedIDs = append(consumedIDs, mv.Id)
			if log != nil {
				log("移动记录无对应 DB 行（跳过迁移）：" + mv.FromPath)
			}
			continue
		}
		shard := store.ShardOfPath(mv.ToPath, shards)
		plans = append(plans, cardMovePlan{
			from: fromStore, fromPath: mv.FromPath, toPath: mv.ToPath,
			to: shardDBs[shard], moveID: mv.Id,
		})
	}
	done := 0
	for _, p := range plans {
		rel := strings.TrimLeft(p.toPath[len(prefix):], `\/`)
		newFolder := filepath.Dir(rel)
		if newFolder == "." {
			newFolder = ""
		}
		moved, moveErr := hub.MoveCardRow(p.from, p.to, p.fromPath, p.toPath, newFolder)
		if moveErr != nil {
			return done, moveErr
		}
		if moved {
			consumedIDs = append(consumedIDs, p.moveID)
			done++
		}
	}
	if _, err = hub.Core().MarkCardMovesConsumed(consumedIDs); err != nil {
		return done, err
	}
	if log != nil && len(consumedIDs) > 0 {
		log("移动记录消费：" + strconv.Itoa(done) + " 条（DB 行已按新路径迁移，保 id）")
	}
	return done, nil
}
