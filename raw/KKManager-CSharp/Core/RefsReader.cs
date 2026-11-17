using System;
using System.Collections.Generic;
using System.IO;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>引用补读结果——卡数 / 有引用数 / 引用行数 / 失败数。</summary>
    public class RefsReadResult
    {
        /// <summary>处理的卡片数。</summary>
        public long Cards { get; set; }

        /// <summary>有引用（ModRefs 非空）的卡片数。</summary>
        public long WithRefs { get; set; }

        /// <summary>写入的引用行数（未去重，含属性 / 槽位粒度）。</summary>
        public long RefRows { get; set; }

        /// <summary>读卡失败数（IO / 损坏）。</summary>
        public long Failed { get; set; }
    }

    /// <summary>
    /// 引用补读——对已入库的卡片逐张读声明区（CardReader.Parse），写 card_mod + mod_count。
    /// 一次性操作：只读 card 表列出的文件，不枚举目录、不重建卡片行；按库分组事务。
    /// </summary>
    public static class RefsReader
    {
        /// <summary>补读某库根下卡片的引用——以卡表 file_path 为准，不扫目录；onlyPending=true 时只处理无 refs 戳的卡（分批续跑），limit≤0 = 不限。</summary>
        public static RefsReadResult ReadRoot(StoreHub hub, RootsConfig cfg, string rootPath, bool onlyPending, int limit, Action<string> log)
        {
            RefsReadResult result = new RefsReadResult();
            List<StoreHub.CardTarget> cards = hub.CardsOfRoot(cfg, rootPath, onlyPending ? "refs" : null);
            if (limit > 0 && cards.Count > limit)
            {
                cards = cards.GetRange(0, limit);
            }
            if (log != null)
            {
                log("待补引用卡片：" + cards.Count + (onlyPending ? "（只含未补的）" : ""));
            }
            Store current = null;
            try
            {
                foreach (StoreHub.CardTarget t in cards)
                {
                    if (t.Store != current)
                    {
                        if (current != null)
                        {
                            current.Commit();
                        }
                        current = t.Store;
                        current.Begin();
                    }
                    result.Cards = result.Cards + 1;
                    try
                    {
                        CardInfo info = CardReader.Parse(t.FilePath);
                        t.Store.ReplaceCardRefs(t.CardId, info.ModRefs);
                        t.Store.UpdateCardRefsMeta(t.FilePath, info.DistinctModIds().Count, info.UarBlocks);
                        t.Store.MarkScanStep(t.FilePath, "refs", info.Size, "");
                        result.RefRows = result.RefRows + info.ModRefs.Count;
                        if (info.ModRefs.Count > 0)
                        {
                            result.WithRefs = result.WithRefs + 1;
                        }
                    }
                    catch (IOException ex)
                    {
                        result.Failed = result.Failed + 1;
                        if (log != null)
                        {
                            log("  ! 读卡失败：" + Path.GetFileName(t.FilePath) + "（" + ex.Message + "）");
                        }
                    }
                }
                if (current != null)
                {
                    current.Commit();
                }
            }
            catch
            {
                if (current != null)
                {
                    current.Rollback();
                }
                throw;
            }
            return result;
        }
    }
}
