using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>索引导入结果——行数统计 / 索引目录 / 摘要。</summary>
    public class IndexImportResult
    {
        /// <summary>实际使用的索引目录。</summary>
        public string IndexDir { get; set; }

        /// <summary>索引总行数（含跳过行）。</summary>
        public long Rows { get; set; }

        /// <summary>写入的卡片行数。</summary>
        public long Cards { get; set; }

        /// <summary>写入的 mod 副本行数（有 guid 的 mod 行）。</summary>
        public long Mods { get; set; }

        /// <summary>其它 + 无 guid 的 mod + 跳过行（只进 idx_meta）。</summary>
        public long Others { get; set; }

        /// <summary>写入的引用行数（索引「引用mod」列，去重 guid 粒度）。</summary>
        public long RefRows { get; set; }

        /// <summary>索引摘要（manifest.tsv / index.tsv 的 SHA256 前 16 位）。</summary>
        public string Digest { get; set; }

        /// <summary>失败原因——非空表示未导入。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 索引导入器——读 kkc 风格索引（`整理/index/` 分片或 `整理/index.tsv` 全量）写库，**不扫文件系统**。
    /// 索引是唯一真相源：卡片 / mod 副本 / 索引元数据全部来自索引文件；文件存在性不做 stat。
    /// </summary>
    public static class IndexImporter
    {
        /// <summary>默认索引目录——&lt;root&gt;/整理/index。</summary>
        public static string DefaultIndexDir(string rootPath)
        {
            return Path.Combine(rootPath ?? "", "整理", "index");
        }

        /// <summary>提交批次行数——每 N 行提交一轮；行计数跨分片、跨文件统一累加。</summary>
        private const int CommitRows = 2000;

        /// <summary>在全部参与库上开写入事务（deferred——分片库连接都 ATTACH 主库，IMMEDIATE 会互撞主库锁）。</summary>
        private static void BeginAll(List<Store> stores)
        {
            foreach (Store s in stores)
            {
                s.Begin();
            }
        }

        /// <summary>提交全部参与库的写入事务——先提交已写入的行，下一批重新 Begin。</summary>
        private static void CommitAll(List<Store> stores)
        {
            foreach (Store s in stores)
            {
                s.Commit();
            }
        }

        /// <summary>回滚全部参与库——回滚本身失败逐库出声（不静默吞）；已提交的批次不回退，重跑按幂等修复。</summary>
        private static void RollbackAll(List<Store> stores, Action<string> log)
        {
            foreach (Store s in stores)
            {
                try
                {
                    s.Rollback();
                }
                catch (Exception ex)
                {
                    if (log != null)
                    {
                        log("回滚失败：" + s.DbPath + " · " + ex.Message);
                    }
                }
            }
        }

        /// <summary>导入一个库根的索引——modRoot 是 mod 库根条目（tier / 只读 / 路径），cardRoot 是卡片库根条目（空 = 同 modRoot）；indexDir 空 = 默认目录。</summary>
        public static IndexImportResult Import(StoreHub hub, RootsConfig cfg, RootEntry modRoot, RootEntry cardRoot, string indexDir, Action<string> log)
        {
            if (cardRoot == null)
            {
                cardRoot = modRoot;
            }
            IndexImportResult result = new IndexImportResult();
            string dir = string.IsNullOrWhiteSpace(indexDir) ? DefaultIndexDir(modRoot.path) : indexDir;
            string manifest = Path.Combine(dir, "manifest.tsv");
            string sibling = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "", "index.tsv");
            List<string> files = new List<string>();
            // 索引里的「现路径」是相对**整理根**的（index 目录的父目录）——文件绝对路径 = relBase + 现路径
            string relBase = modRoot.path;
            if (File.Exists(manifest))
            {
                result.IndexDir = dir;
                result.Digest = HashFile(manifest);
                files.AddRange(ReadManifest(manifest, dir));
                relBase = Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? modRoot.path;
            }
            else if (File.Exists(Path.Combine(dir, "index.tsv")))
            {
                result.IndexDir = dir;
                result.Digest = HashFile(Path.Combine(dir, "index.tsv"));
                files.Add(Path.Combine(dir, "index.tsv"));
                relBase = dir;
            }
            else if (File.Exists(sibling))
            {
                result.IndexDir = Path.GetDirectoryName(sibling);
                result.Digest = HashFile(sibling);
                files.Add(sibling);
                relBase = result.IndexDir ?? modRoot.path;
            }
            else
            {
                result.Error = "索引不存在：" + dir + "（manifest.tsv / index.tsv 均无）";
                return result;
            }
            if (files.Count == 0)
            {
                result.Error = "索引分片清单为空：" + manifest;
                return result;
            }

            Store modStore = hub.StoreFor(modRoot, true);
            int cardBase = hub.CardBaseOf(cardRoot);
            List<Store> cardShards = cardBase > 0 ? hub.StoresOfBase(cardBase) : null;
            // 旧库文件按「结构已就绪」打开、不跑完整 EnsureSchema——导入前补索引表（幂等）
            modStore.EnsureIndexTables();
            hub.Core.EnsureIndexTables();
            if (cardShards != null)
            {
                foreach (Store s in cardShards)
                {
                    s.EnsureIndexTables();
                }
            }
            if (log != null)
            {
                log("索引：" + result.IndexDir + "（" + files.Count + " 个分片）");
                log("库根：" + modRoot.path + " → mod " + Path.GetFileName(modStore.DbPath) +
                    (cardShards == null ? "" : " · 卡片 " + cardShards.Count + " 片"));
            }

            // 写入事务：mod 库 + 全部卡片分片一起 Begin，每 CommitRows 行提交一轮。
            // 逐条自动提交时每行都是一个独立事务（journal 写 + 锁往返），是本导入的主要开销源。
            List<Store> txStores = new List<Store>();
            txStores.Add(modStore);
            if (cardShards != null)
            {
                foreach (Store s in cardShards)
                {
                    if (!txStores.Contains(s))
                    {
                        txStores.Add(s);
                    }
                }
            }
            int pending = 0;
            try
            {
                BeginAll(txStores);
                foreach (string f in files)
                {
                    if (!File.Exists(f))
                    {
                        if (log != null)
                        {
                            log("  ! 分片缺失，跳过：" + f);
                        }
                        continue;
                    }
                    foreach (Dictionary<string, string> row in ReadRows(f))
                    {
                        ImportRow(hub, modRoot, cardRoot, relBase, row, cardShards, modStore, result);
                        pending = pending + 1;
                        if (pending >= CommitRows)
                        {
                            CommitAll(txStores);
                            BeginAll(txStores);
                            pending = 0;
                        }
                    }
                }
                CommitAll(txStores);
            }
            catch
            {
                RollbackAll(txStores, log);
                throw;
            }

            hub.Core.UpsertIndexSource(modRoot.path, result.IndexDir, "kkc-index-v1",
                result.Rows, result.Cards, result.Mods, result.Others, result.Digest);
            // 收尾：清孤儿引用（删卡 / 换根后的 card_mod 残行）——幂等
            int orphans = 0;
            if (cardShards != null)
            {
                foreach (Store s in cardShards)
                {
                    orphans = orphans + s.CleanOrphanRefs();
                }
            }
            orphans = orphans + modStore.CleanOrphanRefs();
            if (orphans > 0 && log != null)
            {
                log("清理孤儿引用行：" + orphans);
            }
            hub.Core.RefreshModAuthors();
            if (log != null)
            {
                log("导入完成：行 " + result.Rows + " · 卡 " + result.Cards + " · mod 副本 " + result.Mods + " · 其它 " + result.Others + " · 引用 " + result.RefRows);
            }
            return result;
        }

        /// <summary>读分片清单——manifest.tsv 的「文件」列；相对路径按索引目录解析。</summary>
        private static List<string> ReadManifest(string manifestPath, string dir)
        {
            List<string> list = new List<string>();
            using (StreamReader sr = new StreamReader(manifestPath, Encoding.UTF8, true))
            {
                string header = sr.ReadLine();
                if (header == null)
                {
                    return list;
                }
                string[] cols = header.Split('\t');
                int fileCol = Array.IndexOf(cols, "文件");
                if (fileCol < 0)
                {
                    fileCol = 0;
                }
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0)
                    {
                        continue;
                    }
                    string[] parts = line.Split('\t');
                    if (parts.Length <= fileCol || parts[fileCol].Trim().Length == 0)
                    {
                        continue;
                    }
                    list.Add(Path.Combine(dir, parts[fileCol].Trim().Replace('/', '\\')));
                }
            }
            return list;
        }

        /// <summary>读一个 TSV 分片——返回「列名 → 值」字典序列。</summary>
        private static IEnumerable<Dictionary<string, string>> ReadRows(string path)
        {
            using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
            {
                string header = sr.ReadLine();
                if (header == null)
                {
                    yield break;
                }
                string[] cols = header.Split('\t');
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    string[] parts = line.Split('\t');
                    Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < cols.Length; i++)
                    {
                        row[cols[i]] = i < parts.Length ? parts[i] : "";
                    }
                    yield return row;
                }
            }
        }

        /// <summary>导入一行索引——卡写卡片分片库；mod 写 mod 库（无 guid 只进 idx_meta）；其它只进 idx_meta。</summary>
        private static void ImportRow(StoreHub hub, RootEntry modRoot, RootEntry cardRoot, string relBase, Dictionary<string, string> row,
            List<Store> cardShards, Store modStore, IndexImportResult result)
        {
            result.Rows = result.Rows + 1;
            string kind = Get(row, "类型");
            string rel = Get(row, "现路径").Replace('/', '\\').Trim();
            string full = Path.Combine(relBase, rel);
            string idxId = Get(row, "ID");
            string cardType = Get(row, "卡类型");
            string chara = Get(row, "角色名");
            string work = Get(row, "作品");
            string author = Get(row, "作者");
            string source = Get(row, "来源");
            string srcRel = Get(row, "包内路径");
            string note = Get(row, "备注");
            string guid = Get(row, "guid");
            string series = Get(row, "系列");
            string vnId = Get(row, "VN_ID");
            string vnName = Get(row, "VN名");
            string vndbCharaId = Get(row, "VNDB角色ID");
            string charaIdConf = Get(row, "角色ID置信度");
            string category = Get(row, "大类");
            string subCategory = Get(row, "子类");
            long size = ParseLong(Get(row, "大小"));

            // 跳过行（现路径 = 「（跳过…）」）——只登记元数据，不写卡片 / mod
            if (rel.StartsWith("（", StringComparison.Ordinal) || rel.Length == 0)
            {
                modStore.UpsertIndexMeta("[跳过]" + idxId, idxId, kind, cardType, chara, work, author, source, srcRel, note, guid,
                    series, vnId, vnName, vndbCharaId, charaIdConf, category, subCategory);
                result.Others = result.Others + 1;
                return;
            }

            string folder = Path.GetDirectoryName(rel) ?? "";
            if (kind == "卡" && cardShards != null && cardShards.Count > 0)
            {
                int shard = StoreHub.ShardOfPath(full, cardShards.Count);
                Store cs = cardShards[shard];
                CardInfo ci = new CardInfo
                {
                    FilePath = full,
                    FileName = Path.GetFileName(full),
                    Size = size,
                    CardType = cardType,
                    CharaName = chara,
                    DataVersion = "",
                    ImageEnd = 0,
                    UarBlocks = 0
                };
                long cardId = cs.UpsertCard(ci, cardRoot, folder, null, "");
                cs.UpsertIndexMeta(full, idxId, kind, cardType, chara, work, author, source, srcRel, note, guid,
                    series, vnId, vnName, vndbCharaId, charaIdConf, category, subCategory);
                // 引用列（索引自带）——每 guid 一行写 card_mod，mod_count 同步；无引用列时留给 refs-read 补
                string refsCell = Get(row, "引用mod");
                if (!string.IsNullOrEmpty(refsCell))
                {
                    List<ModRef> refs = new List<ModRef>();
                    foreach (string g in refsCell.Split(','))
                    {
                        string id = g.Trim();
                        if (id.Length > 0)
                        {
                            refs.Add(new ModRef { ModId = id });
                        }
                    }
                    if (refs.Count > 0)
                    {
                        cs.ReplaceCardRefs(cardId, refs);
                        cs.UpdateCardRefsMeta(full, refs.Count, 0);
                        result.RefRows = result.RefRows + refs.Count;
                    }
                }
                result.Cards = result.Cards + 1;
                return;
            }
            if (kind == "mod")
            {
                // 索引的 guid 列可能为空（整理计划不解析 manifest）——按需读一次容器中央目录补 guid（不做目录扫描）
                if (string.IsNullOrEmpty(guid) && File.Exists(full))
                {
                    ModInfo parsed = ZipModReader.Parse(full);
                    if (parsed.ErrorKind == ModErrorKind.None && !string.IsNullOrEmpty(parsed.Guid))
                    {
                        guid = parsed.Guid;
                        note = string.IsNullOrEmpty(note) ? "guid 补读" : note + "；guid 补读";
                    }
                }
                if (!string.IsNullOrEmpty(guid))
                {
                    bool isNew = !modStore.HasModRow(guid);
                    ModInfo mi = new ModInfo
                    {
                        Guid = guid,
                        FilePath = full,
                        FileName = Path.GetFileName(full),
                        Size = size,
                        Name = Path.GetFileNameWithoutExtension(full),
                        Author = author
                    };
                    modStore.UpsertModFile(mi, modRoot, "");
                    if (isNew)
                    {
                        modStore.FillModMeta(mi);
                    }
                    result.Mods = result.Mods + 1;
                }
                else
                {
                    // 仍无 guid（manifest 读不到）——只进元数据，不伪造 guid
                    result.Others = result.Others + 1;
                }
                modStore.UpsertIndexMeta(full, idxId, kind, cardType, chara, work, author, source, srcRel, note, guid,
                series, vnId, vnName, vndbCharaId, charaIdConf, category, subCategory);
                return;
            }

            // 其它 / 纯图——只进元数据
            modStore.UpsertIndexMeta(full, idxId, kind, cardType, chara, work, author, source, srcRel, note, guid,
                series, vnId, vnName, vndbCharaId, charaIdConf, category, subCategory);
            result.Others = result.Others + 1;
        }

        /// <summary>取列值（缺列返回空串）。</summary>
        private static string Get(Dictionary<string, string> row, string key)
        {
            string v;
            return row.TryGetValue(key, out v) ? (v ?? "") : "";
        }

        /// <summary>解析整数（空 / 非法返回 0）。</summary>
        private static long ParseLong(string text)
        {
            long v;
            if (long.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return 0;
        }

        /// <summary>文件摘要（SHA256 前 16 位小写）——索引变更检测用。</summary>
        private static string HashFile(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < 8; i++)
                    {
                        sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    }
                    return sb.ToString();
                }
            }
            catch (IOException)
            {
                return "";
            }
        }
    }
}
