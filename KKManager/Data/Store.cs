using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KKManager.Core;
using Microsoft.Data.Sqlite;

namespace KKManager.Data
{
    /// <summary>四色统计（按卡引用的 mod 去重计数）。</summary>
    public class Stats4
    {
        /// <summary>主库齐备的引用数。</summary>
        public long Green { get; set; }

        /// <summary>仅缓存库有的引用数。</summary>
        public long Yellow { get; set; }

        /// <summary>仅冷冻库有的引用数。</summary>
        public long Red { get; set; }

        /// <summary>所有库都没有的引用数。</summary>
        public long Black { get; set; }

        /// <summary>未就绪引用数（黄 + 红 + 黑）。</summary>
        public long Pending
        {
            get { return Yellow + Red + Black; }
        }
    }

    /// <summary>库快照——各表计数与四色统计。</summary>
    public class Snapshot
    {
        /// <summary>卡片总数。</summary>
        public long Cards { get; set; }

        /// <summary>mod 总数（按 guid）。</summary>
        public long Mods { get; set; }

        /// <summary>mod 文件副本总数。</summary>
        public long ModFiles { get; set; }

        /// <summary>引用条目总数（去重 guid 后的条数）。</summary>
        public long Refs { get; set; }

        /// <summary>按级别统计的 mod 数。</summary>
        public long ModsTier1 { get; set; }

        /// <summary>缓存库 mod 数。</summary>
        public long ModsTier2 { get; set; }

        /// <summary>冷冻库 mod 数。</summary>
        public long ModsTier3 { get; set; }

        /// <summary>主库齐备的卡数（就绪）。</summary>
        public long CardsReady { get; set; }

        /// <summary>有引用的卡数。</summary>
        public long CardsWithRefs { get; set; }

        /// <summary>四色汇总（按卡-去重引用计）。</summary>
        public Stats4 Colors { get; } = new Stats4();

        /// <summary>未被任何卡片引用的 mod 数。</summary>
        public long UnusedMods { get; set; }

        /// <summary>同 guid 多文件（重复安装）数。</summary>
        public long DupMods { get; set; }
    }

    /// <summary>卡片行（查询结果）。</summary>
    public class CardRow
    {
        /// <summary>卡片 id。</summary>
        public long Id { get; set; }

        /// <summary>文件名。</summary>
        public string FileName { get; set; }

        /// <summary>卡类型。</summary>
        public string CardType { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间（ISO）。</summary>
        public string Mtime { get; set; }

        /// <summary>所属库根路径。</summary>
        public string RootPath { get; set; }

        /// <summary>库级别。</summary>
        public int Tier { get; set; }

        /// <summary>相对库根的文件夹（根目录为空串）。</summary>
        public string Folder { get; set; }

        /// <summary>引用的 mod 数（去重）。</summary>
        public long ModCount { get; set; }

        /// <summary>主库齐备数。</summary>
        public long Green { get; set; }

        /// <summary>仅缓存库数。</summary>
        public long Yellow { get; set; }

        /// <summary>仅冷冻库数。</summary>
        public long Red { get; set; }

        /// <summary>全库皆无数。</summary>
        public long Black { get; set; }

        /// <summary>库位序号（0 = 主库文件；≥1 = 各附加库文件）。</summary>
        public int Lib { get; set; }

        /// <summary>是否有缩略图。</summary>
        public bool HasThumb { get; set; }
    }

    /// <summary>mod 行（查询结果）。</summary>
    public class ModRow
    {
        /// <summary>guid。</summary>
        public string Guid { get; set; }

        /// <summary>名称。</summary>
        public string Name { get; set; }

        /// <summary>作者。</summary>
        public string Author { get; set; }

        /// <summary>版本。</summary>
        public string Version { get; set; }

        /// <summary>最优级别（1 主库 / 2 缓存 / 3 冷冻）。</summary>
        public int Tier { get; set; }

        /// <summary>所在库根。</summary>
        public string RootPath { get; set; }

        /// <summary>文件名。</summary>
        public string FileName { get; set; }

        /// <summary>最优副本的文件绝对路径（资源管理器定位用）。</summary>
        public string FilePath { get; set; }

        /// <summary>字节数。</summary>
        public long Size { get; set; }

        /// <summary>副本数。</summary>
        public long DupCount { get; set; }

        /// <summary>被引用的卡数。</summary>
        public long Used { get; set; }

        /// <summary>是否有登记过的旧版（该 guid 存在被判为旧版的副本）。</summary>
        public bool HasOld { get; set; }

        /// <summary>旧版文件名（无旧版时为空）。</summary>
        public string OldName { get; set; }
    }

    /// <summary>作者行——作者聚合表（mod_author）的一行：该作者的 mod 数量。空串表示 manifest 无作者。</summary>
    public class AuthorRow
    {
        /// <summary>作者名（manifest author，空串表示无作者）。</summary>
        public string Author { get; set; }

        /// <summary>该作者的 mod 数量。</summary>
        public long Count { get; set; }
    }

    /// <summary>一条引用明细（反查 / 移动用）。</summary>
    public class RefRow
    {
        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>部件属性（如 ChaFileClothes.ClothesTop）。</summary>
        public string Property { get; set; }

        /// <summary>级别（1/2/3；0 = 无名）。</summary>
        public int Tier { get; set; }

        /// <summary>所在库根。</summary>
        public string RootPath { get; set; }

        /// <summary>文件路径（可移动的实体）。</summary>
        public string FilePath { get; set; }

        /// <summary>文件名。</summary>
        public string FileName { get; set; }

        /// <summary>卡内记录的名称。</summary>
        public string RecName { get; set; }

        /// <summary>卡内记录的作者。</summary>
        public string RecAuthor { get; set; }

        /// <summary>卡内记录的来源网址。</summary>
        public string RecWebsite { get; set; }
    }

    /// <summary>一条 mod 文件副本记录（跨库搬移时承载源行内容）。</summary>
    public class ModFileRecord
    {
        /// <summary>文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>所在级别。</summary>
        public int Tier { get; set; }

        /// <summary>所在库根。</summary>
        public string RootPath { get; set; }

        /// <summary>文件名。</summary>
        public string FileName { get; set; }

        /// <summary>字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间戳。</summary>
        public string Mtime { get; set; }

        /// <summary>扫描时刻。</summary>
        public string ScanTime { get; set; }
    }

    /// <summary>旧版登记——人工判定某份副本为旧版后留下的新旧版本关系（本系统可理解的结构化记录，不靠文件名猜）。</summary>
    public class ModOldRecord
    {
        /// <summary>旧版文件绝对路径（缓存库内，文件名带 .old）。</summary>
        public string OldPath { get; set; }

        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>旧版文件名（含 .old 段）。</summary>
        public string OldName { get; set; }

        /// <summary>旧版 manifest 版本号。</summary>
        public string OldVersion { get; set; }

        /// <summary>登记时新版文件名。</summary>
        public string NewName { get; set; }

        /// <summary>登记时新版 manifest 版本号。</summary>
        public string NewVersion { get; set; }

        /// <summary>登记时刻（UTC）。</summary>
        public string MarkedAt { get; set; }
    }

    /// <summary>卡片编辑留档——原版留在软件内部，与卡片的对应关系落库（「寻找旧版」读它）。</summary>
    public class CardEditRecord
    {
        /// <summary>留档记录 id。</summary>
        public long Id { get; set; }

        /// <summary>被编辑的卡片绝对路径（关系键）。</summary>
        public string CardPath { get; set; }

        /// <summary>被编辑的卡片文件名。</summary>
        public string CardName { get; set; }

        /// <summary>卡片所在库序号。</summary>
        public int Lib { get; set; }

        /// <summary>留档文件绝对路径（原版副本，留在软件内部）。</summary>
        public string ArchivedFile { get; set; }

        /// <summary>留档文件字节数。</summary>
        public long ArchivedSize { get; set; }

        /// <summary>留档时原文件的修改时间文本。</summary>
        public string ArchivedMtime { get; set; }

        /// <summary>改动摘要（如「姓 「藤原」 → 「藤原1」 · …」）。</summary>
        public string Changes { get; set; }

        /// <summary>留档 / 编辑时刻（UTC）。</summary>
        public string EditedAt { get; set; }
    }

    /// <summary>mod 组成档案——使用者主动查看某 mod 组成时建立：容器条目清单 + 建档时刻（文件按路径 / 大小 / 修改时间判失效，变了重建）。</summary>
    public class ModComposition
    {
        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>被分析文件的绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>被分析文件的文件名。</summary>
        public string FileName { get; set; }

        /// <summary>建档时文件字节数（失效判据之一）。</summary>
        public long Size { get; set; }

        /// <summary>建档时文件修改时间（失效判据之一）。</summary>
        public string Mtime { get; set; }

        /// <summary>容器条目数。</summary>
        public long EntryCount { get; set; }

        /// <summary>条目原始（解压后）字节数合计。</summary>
        public long TotalSize { get; set; }

        /// <summary>条目压缩后字节数合计。</summary>
        public long TotalCompressed { get; set; }

        /// <summary>条目清单——每行「路径 \t 原始字节 \t 压缩字节」。</summary>
        public string Entries { get; set; }

        /// <summary>建档时刻（UTC）。</summary>
        public string AnalyzedAt { get; set; }

        /// <summary>文本类条目的内容（每段「路径 \t 内容」——csv / xml / txt 等，供悬停预览；二进制大件不入档）。</summary>
        public string Texts { get; set; }

        /// <summary>本次结果命中已有档案（未重新分析）——读侧标记，不落库。</summary>
        public bool Cached { get; set; }
    }

    /// <summary>unity3d 解析档案——某 zipmod 内一个 .unity3d 条目的贴图清单；按 zipmod 大小 / 修改时间判失效（变了重建）。</summary>
    public class ModU3d
    {
        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>容器内条目路径（如 abdata/chara/x.unity3d）。</summary>
        public string EntryPath { get; set; }

        /// <summary>所属 zipmod 的绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>建档时 zipmod 字节数（失效判据之一）。</summary>
        public long Size { get; set; }

        /// <summary>建档时 zipmod 修改时间（失效判据之一）。</summary>
        public string Mtime { get; set; }

        /// <summary>贴图张数。</summary>
        public long TextureCount { get; set; }

        /// <summary>贴图清单——每行「pathID \t 名称 \t 宽 \t 高 \t 格式 \t 数据字节」。 </summary>
        public string Textures { get; set; }

        /// <summary>各 classID 对象数摘要（如「28:6 · 1:120」）——转网格 / 材质解析的扩展位。</summary>
        public string ClassSummary { get; set; }

        /// <summary>建档时刻（UTC）。</summary>
        public string ParsedAt { get; set; }

        /// <summary>本次结果命中已有档案（未重新解析）——读侧标记，不落库。</summary>
        public bool Cached { get; set; }
    }

    /// <summary>组成档案的目录聚合——一条目录下有多少条目、合计多大（展示派生，不落库）。</summary>
    public class CompositionDir
    {
        /// <summary>目录路径（条目路径去掉文件名；根目录条目为「(容器根)」）。</summary>
        public string Path { get; set; }

        /// <summary>该目录下的条目数。</summary>
        public long Count { get; set; }

        /// <summary>该目录下条目的原始字节数合计。</summary>
        public long Size { get; set; }
    }

    /// <summary>重复副本组——同一 guid 的多份 mod 文件（跨库）。</summary>
    public class DupGroup
    {
        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>各库的副本（按级别升序）。</summary>
        public List<ModFileRecord> Files { get; } = new List<ModFileRecord>();

        /// <summary>主库副本数。</summary>
        public int MainCount { get; set; }
    }

    /// <summary>某 guid 的副本计数（重复副本判定用——总数 / 非旧版数）。</summary>
    public class ModFileCount
    {
        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>该 guid 在本库的副本总数。</summary>
        public long Total { get; set; }

        /// <summary>其中非旧版（文件名不带 .old 段）的份数。</summary>
        public long Live { get; set; }
    }

    /// <summary>待办条目——软件认为需要使用者处理的一件事（目前只有「离线库」一类）。</summary>
    public class TodoRow
    {
        /// <summary>条目 id（关闭用）。</summary>
        public long Id { get; set; }

        /// <summary>类型——`offline` = 库根被判为离线（库根路径失效或为空）。</summary>
        public string Kind { get; set; }

        /// <summary>关联键——离线库类型下是库根路径（小写归一）。</summary>
        public string Key { get; set; }

        /// <summary>登记时刻。</summary>
        public string CreatedAt { get; set; }
    }

    /// <summary>SQLite 存储——库根配置 / mod / 卡片 / 引用 / 设置。</summary>
    public class Store : IDisposable
    {
        private const int SchemaVersion = 2;
        private const string StampFormat = "yyyy-MM-ddTHH:mm:ss.fff";
        private readonly SqliteConnection _conn;

        /// <summary>是否为主库连接（库文件连接会 ATTACH 主库为 core）。</summary>
        private readonly bool _isCore;
        private SqliteTransaction _tx;

        /// <summary>打开（不存在则建）数据库；结构版本不符时重建。corePath 非空表示本库为「库文件」——ATTACH 主库为 core，设置表与 mod 主表由主库托管。</summary>
        public Store(string dbPath, string corePath = null)
        {
            string full = Path.GetFullPath(dbPath);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            DbPath = full;
            CorePath = string.IsNullOrWhiteSpace(corePath) ? null : Path.GetFullPath(corePath);
            _isCore = CorePath == null;
            _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full }.ToString());
            _conn.Open();
            EnsureSchema();
            if (!_isCore)
            {
                Exec("ATTACH DATABASE '" + CorePath.Replace("'", "''") + "' AS core");
            }
        }

        /// <summary>数据库文件绝对路径。</summary>
        public string DbPath { get; }

        /// <summary>主库文件绝对路径（库文件返回其主库；主库自身返回 null）。</summary>
        public string CorePath { get; }

        /// <summary>是否为主库文件——主库持设置表、mod 主表与预置库根（主库 / 缓存库槽位）的数据。</summary>
        public bool IsCore
        {
            get { return _isCore; }
        }

        /// <summary>mod 主表在本次连接里的引用名——主库是本地表，库文件走 core 前缀。</summary>
        private string ModTable
        {
            get { return _isCore ? "mod" : "core.mod"; }
        }

        /// <summary>设置表在本次连接里的引用名。</summary>
        private string SettingTable
        {
            get { return _isCore ? "setting" : "core.setting"; }
        }

        /// <summary>作者聚合表在本次连接里的引用名——表恒在主库（库文件走 core 前缀）。</summary>
        private string AuthorTable
        {
            get { return _isCore ? "mod_author" : "core.mod_author"; }
        }

        /// <summary>当前 UTC 时间戳文本。</summary>
        public static string Now()
        {
            return DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>取文件的修改时间戳文本（UTC）。</summary>
        public static string StampOf(FileInfo fi)
        {
            return fi.LastWriteTimeUtc.ToString(StampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>可读文本类条目的扩展名（组成档案顺带捞内容做悬停预览——二进制大件不碰）。</summary>
        private static readonly string[] TextEntryExts = { ".csv", ".xml", ".txt", ".json", ".ini", ".yml", ".yaml", ".md", ".list" };

        /// <summary>单条目文本入库上限（字符）——超长截断并留标记，避免档案表膨胀。</summary>
        private const int TextEntryLimit = 20000;

        /// <summary>一条条目是否可读文本类（按扩展名判）。</summary>
        public static bool IsTextEntry(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            foreach (string ext in TextEntryExts)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>截断条目文本（超上限时留标记——不静默丢内容）。</summary>
        public static string ClampTextEntry(string text)
        {
            if (text == null)
            {
                return "";
            }
            if (text.Length <= TextEntryLimit)
            {
                return text;
            }
            return text.Substring(0, TextEntryLimit) + "\n…（已截断，共 " + text.Length + " 字符）";
        }

        private void EnsureSchema()
        {
            Exec("PRAGMA journal_mode=WAL");
            long version = Convert.ToInt64(ExecScalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (version != SchemaVersion)
            {
                DropAll();
            }
            if (_isCore)
            {
                Exec("CREATE TABLE IF NOT EXISTS setting(key TEXT PRIMARY KEY, value TEXT)");                Exec(@"CREATE TABLE IF NOT EXISTS mod(
                                 guid TEXT PRIMARY KEY,
                                 name TEXT, version TEXT, author TEXT, website TEXT, description TEXT, schema_ver TEXT,
                                 tier INTEGER, root_path TEXT, file_path TEXT, file_name TEXT,
                                 size INTEGER, mtime TEXT, entry_count INTEGER, dup_count INTEGER,
                                 scan_time TEXT, error TEXT)");
            }
            Exec(@"CREATE TABLE IF NOT EXISTS mod_file(
                             file_path TEXT PRIMARY KEY, guid TEXT, tier INTEGER, root_path TEXT,
                             file_name TEXT, size INTEGER, mtime TEXT, scan_time TEXT)");
            Exec(@"CREATE TABLE IF NOT EXISTS card(
                             id INTEGER PRIMARY KEY AUTOINCREMENT,
                             file_path TEXT UNIQUE, file_name TEXT, size INTEGER, mtime TEXT,
                             tier INTEGER, root_path TEXT, folder TEXT,
                             card_type TEXT, data_version TEXT, image_end INTEGER, uar_blocks INTEGER,
                             mod_count INTEGER, thumb BLOB, scan_time TEXT, error TEXT)");
            Exec(@"CREATE TABLE IF NOT EXISTS card_mod(
                             card_id INTEGER, mod_guid TEXT,
                             property TEXT, slot INTEGER, local_slot INTEGER, category_no INTEGER,
                             rec_name TEXT, rec_author TEXT, rec_website TEXT,
                             PRIMARY KEY(card_id, property, slot, local_slot, mod_guid))");
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mod_guid ON card_mod(mod_guid)");
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mod_card ON card_mod(card_id)");
            if (_isCore)
            {
                Exec("CREATE INDEX IF NOT EXISTS ix_mod_tier ON mod(tier)");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_old(
                                 old_path TEXT PRIMARY KEY, guid TEXT, old_name TEXT, old_version TEXT,
                                 new_name TEXT, new_version TEXT, marked_at TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS ix_mod_old_guid ON mod_old(guid)");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_author(
                                 author TEXT PRIMARY KEY, mod_count INTEGER)");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_composition(
                                 guid TEXT PRIMARY KEY, file_path TEXT, file_name TEXT, size INTEGER, mtime TEXT,
                                 entry_count INTEGER, total_size INTEGER, total_compressed INTEGER,
                                 entries TEXT, analyzed_at TEXT)");
                // 增量补列：旧库（v0.17.3 建的 mod_composition）没有 texts 列——按列存在性判定后补上，不重建整库
                if (Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM pragma_table_info('mod_composition') WHERE name='texts'"), CultureInfo.InvariantCulture) == 0)
                {
                    Exec("ALTER TABLE mod_composition ADD COLUMN texts TEXT");
                }
                Exec(@"CREATE TABLE IF NOT EXISTS todo(
                                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                                 kind TEXT, key TEXT, created_at TEXT,
                                 UNIQUE(kind, key))");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_u3d(
                                 guid TEXT, entry_path TEXT,
                                 file_path TEXT, size INTEGER, mtime TEXT,
                                 texture_count INTEGER, textures TEXT, class_summary TEXT,
                                 parsed_at TEXT,
                                 PRIMARY KEY(guid, entry_path))");
                Exec(@"CREATE TABLE IF NOT EXISTS card_edit(
                                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                                 card_path TEXT, card_name TEXT, lib INTEGER,
                                 archived_file TEXT, archived_size INTEGER, archived_mtime TEXT,
                                 changes TEXT, edited_at TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS ix_card_edit_path ON card_edit(card_path)");
            }
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mtime ON card(mtime)");
            Exec("PRAGMA user_version=" + SchemaVersion);
        }

        private void DropAll()
        {
            Exec("DROP TABLE IF EXISTS card_mod");
            Exec("DROP TABLE IF EXISTS card");
            Exec("DROP TABLE IF EXISTS mod_file");
            if (_isCore)
            {
                Exec("DROP TABLE IF EXISTS mod_old");
                Exec("DROP TABLE IF EXISTS mod_composition");
                Exec("DROP TABLE IF EXISTS mod_u3d");
                Exec("DROP TABLE IF EXISTS todo");
                Exec("DROP TABLE IF EXISTS mod");
                Exec("DROP TABLE IF EXISTS setting");
            }
        }

        /// <summary>开启写入事务。</summary>
        public void Begin()
        {
            _tx = _conn.BeginTransaction();
        }

        /// <summary>提交写入事务。</summary>
        public void Commit()
        {
            if (_tx != null)
            {
                _tx.Commit();
                _tx.Dispose();
                _tx = null;
            }
        }

        /// <summary>回滚写入事务。</summary>
        public void Rollback()
        {
            if (_tx != null)
            {
                _tx.Rollback();
                _tx.Dispose();
                _tx = null;
            }
        }

        private SqliteCommand NewCommand(string sql)
        {
            SqliteCommand cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _tx;
            return cmd;
        }

        private void Exec(string sql)
        {
            using (SqliteCommand cmd = NewCommand(sql))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private object ExecScalar(string sql)
        {
            using (SqliteCommand cmd = NewCommand(sql))
            {
                return cmd.ExecuteScalar();
            }
        }

        /// <summary>读设置项（无则返回 null）。</summary>
        public string GetSetting(string key)
        {
            using (SqliteCommand cmd = NewCommand("SELECT value FROM " + SettingTable + " WHERE key=$k"))
            {
                cmd.Parameters.AddWithValue("$k", key);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? null : (string)v;
            }
        }

        /// <summary>写设置项。</summary>
        public void SetSetting(string key, string value)
        {
            using (SqliteCommand cmd = NewCommand("INSERT INTO " + SettingTable + "(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value"))
            {
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>删除设置项（不存在时静默）。</summary>
        public void DeleteSetting(string key)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM " + SettingTable + " WHERE key=$k"))
            {
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>登记一条待办——同类型 + 同键已存在则原样保留（重复自动离线不叠加消息）。</summary>
        public void AddTodo(string kind, string key, string createdAt)
        {
            using (SqliteCommand cmd = NewCommand("INSERT INTO todo(kind,key,created_at) VALUES($k,$v,$t) ON CONFLICT(kind,key) DO NOTHING"))
            {
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$v", key);
                cmd.Parameters.AddWithValue("$t", createdAt ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>全部待办（按登记顺序）。</summary>
        public List<TodoRow> ListTodos()
        {
            List<TodoRow> list = new List<TodoRow>();
            using (SqliteCommand cmd = NewCommand("SELECT id, kind, key, created_at FROM todo ORDER BY id"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    TodoRow t = new TodoRow();
                    t.Id = r.GetInt64(0);
                    t.Kind = r.IsDBNull(1) ? "" : r.GetString(1);
                    t.Key = r.IsDBNull(2) ? "" : r.GetString(2);
                    t.CreatedAt = r.IsDBNull(3) ? "" : r.GetString(3);
                    list.Add(t);
                }
            }
            return list;
        }

        /// <summary>关闭一条待办（按 id）。</summary>
        public void CloseTodo(long id)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM todo WHERE id=$i"))
            {
                cmd.Parameters.AddWithValue("$i", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按类型 + 键关闭待办——库上线 / 被彻底删除时自动消解，不留痕。</summary>
        public void CloseTodoByKey(string kind, string key)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM todo WHERE kind=$k AND key=$v"))
            {
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$v", key);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>读库根配置。</summary>
        public RootsConfig LoadRoots()
        {
            return RootsConfig.FromJson(GetSetting("roots"));
        }

        /// <summary>写库根配置。</summary>
        public void SaveRoots(RootsConfig cfg)
        {
            SetSetting("roots", cfg.ToJson());
        }

        /// <summary>载入 mod 文件时间戳索引——file_path → [size, mtime]。</summary>
        public Dictionary<string, string[]> LoadModStamps()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, size, mtime FROM mod_file"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = new[] { r.GetInt64(1).ToString(CultureInfo.InvariantCulture), r.IsDBNull(2) ? "" : r.GetString(2) };
                }
            }
            return map;
        }

        /// <summary>载入卡片时间戳索引——file_path → [size, mtime]。</summary>
        public Dictionary<string, string[]> LoadCardStamps()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, size, mtime FROM card"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = new[] { r.GetInt64(1).ToString(CultureInfo.InvariantCulture), r.IsDBNull(2) ? "" : r.GetString(2) };
                }
            }
            return map;
        }

        /// <summary>删除某个库根下的全部 mod 副本记录，返回受影响的 guid（由调用方跨库重算 mod 主表）。</summary>
        public List<string> DeleteModFileRowsUnderRoot(string rootPath)
        {
            List<string> guids = new List<string>();
            using (SqliteCommand cmd = NewCommand("SELECT DISTINCT guid FROM mod_file WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        guids.Add(r.GetString(0));
                    }
                }
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM mod_file WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                cmd.ExecuteNonQuery();
            }
            return guids;
        }

        /// <summary>删除某个库根下的全部卡片记录。</summary>
        public void DeleteCardsUnderRoot(string rootPath)
        {
            using (SqliteCommand cmd = NewCommand(@"DELETE FROM card_mod WHERE card_id IN (SELECT id FROM card WHERE root_path=$p)"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                cmd.ExecuteNonQuery();
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM card WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>清扫某库根下已从磁盘消失的卡片记录——present 为本次枚举到的文件全路径集合；返回删除的卡片数（含引用行）。</summary>
        public int DeleteCardsMissingUnderRoot(string rootPath, HashSet<string> present)
        {
            List<long> gone = new List<long>();
            using (SqliteCommand cmd = NewCommand("SELECT id, file_path FROM card WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string path = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (path.Length == 0 || present == null || !present.Contains(path))
                        {
                            gone.Add(r.GetInt64(0));
                        }
                    }
                }
            }
            if (gone.Count == 0)
            {
                return 0;
            }
            using (SqliteCommand refs = NewCommand("DELETE FROM card_mod WHERE card_id=$id"))
            using (SqliteCommand cards = NewCommand("DELETE FROM card WHERE id=$id"))
            {
                SqliteParameter refId = refs.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter cardId = cards.Parameters.Add("$id", SqliteType.Integer);
                foreach (long id in gone)
                {
                    refId.Value = id;
                    refs.ExecuteNonQuery();
                    cardId.Value = id;
                    cards.ExecuteNonQuery();
                }
            }
            return gone.Count;
        }
        /// <summary>清扫某库根下已从磁盘消失的 mod 副本记录——present 为本次枚举到的文件全路径集合；返回受影响的 guid（去重，由调用方跨库重算 mod 主表）。</summary>
        public List<string> DeleteModFilesMissingUnderRoot(string rootPath, HashSet<string> present)
        {
            List<string> paths = new List<string>();
            List<string> guids = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, guid FROM mod_file WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string path = r.IsDBNull(0) ? "" : r.GetString(0);
                        if (path.Length > 0 && (present == null || !present.Contains(path)))
                        {
                            paths.Add(path);
                        }
                        else
                        {
                            continue;
                        }
                        string guid = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (guid.Length > 0 && seen.Add(guid))
                        {
                            guids.Add(guid);
                        }
                    }
                }
            }
            if (paths.Count == 0)
            {
                return guids;
            }
            using (SqliteCommand del = NewCommand("DELETE FROM mod_file WHERE file_path=$f"))
            {
                SqliteParameter fileParam = del.Parameters.Add("$f", SqliteType.Text);
                foreach (string path in paths)
                {
                    fileParam.Value = path;
                    del.ExecuteNonQuery();
                }
            }
            return guids;
        }
        /// <summary>清扫落在某库根下、文件已从磁盘消失的旧版登记——返回清理条数（旧版登记是全局表，只在主库连接上有效）。</summary>
        public int DeleteModOldMissingUnderRoot(string rootPath, HashSet<string> present)
        {
            if (!_isCore)
            {
                return 0;
            }
            string prefix = (rootPath ?? "").TrimEnd('\\', '/');
            if (prefix.Length == 0)
            {
                return 0;
            }
            List<string> gone = new List<string>();
            using (SqliteCommand cmd = NewCommand("SELECT old_path FROM mod_old"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string path = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (path.Length <= prefix.Length + 1)
                    {
                        continue;
                    }
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (path[prefix.Length] != '\\' && path[prefix.Length] != '/')
                    {
                        continue;
                    }
                    if (present == null || !present.Contains(path))
                    {
                        gone.Add(path);
                    }
                }
            }
            if (gone.Count == 0)
            {
                return 0;
            }
            using (SqliteCommand del = NewCommand("DELETE FROM mod_old WHERE old_path=$p"))
            {
                SqliteParameter param = del.Parameters.Add("$p", SqliteType.Text);
                foreach (string path in gone)
                {
                    param.Value = path;
                    del.ExecuteNonQuery();
                }
            }
            return gone.Count;
        }

        /// <summary>写入一个 mod 文件副本，并刷新 mod 主表的最优级别。</summary>
        public void UpsertModFile(ModInfo m, RootEntry root, string mtime)
        {
            bool existed = false;
            using (SqliteCommand cmd = NewCommand("SELECT COUNT(*) FROM mod_file WHERE file_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", m.FilePath);
                existed = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
                             VALUES($path,$guid,$tier,$root,$file,$size,$mtime,$now)
                             ON CONFLICT(file_path) DO UPDATE SET
                               guid=excluded.guid, tier=excluded.tier, root_path=excluded.root_path,
                               file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime, scan_time=excluded.scan_time"))
            {
                cmd.Parameters.AddWithValue("$path", m.FilePath);
                cmd.Parameters.AddWithValue("$guid", m.Guid);
                cmd.Parameters.AddWithValue("$tier", root.tier);
                cmd.Parameters.AddWithValue("$root", root.path);
                cmd.Parameters.AddWithValue("$file", m.FileName);
                cmd.Parameters.AddWithValue("$size", m.Size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
            long delta = 0;
            if (!existed)
            {
                delta = 1;
            }
            UpdateModRowForFile(m.Guid, root, m.FilePath, m.FileName, m.Size, mtime, delta);
        }

        /// <summary>增量更新 mod 主表——副本数按 delta 调整（新增副本传 1）；tier 与最佳副本取更优者，不跨库读副本表。</summary>
        private void UpdateModRowForFile(string guid, RootEntry root, string filePath, string fileName, long size, string mtime, long delta)
        {
            int oldTier = -1;
            long dup = 0;
            string oldBestPath = null;
            using (SqliteCommand cmd = NewCommand("SELECT tier, dup_count, file_path FROM " + ModTable + " WHERE guid=$g"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        oldTier = r.IsDBNull(0) ? -1 : r.GetInt32(0);
                        dup = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                        oldBestPath = r.IsDBNull(2) ? null : r.GetString(2);
                    }
                }
            }

            long dupNew = dup + delta;
            if (dupNew < 0)
            {
                dupNew = 0;
            }

            if (oldTier >= 0)
            {
                bool takeBest = root.tier < oldTier;
                if (!takeBest && root.tier == oldTier && string.Equals(oldBestPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    takeBest = true;
                }
                if (takeBest)
                {
                    using (SqliteCommand cmd = NewCommand(@"UPDATE " + ModTable + @" SET tier=$tier, root_path=$root, file_path=$path,
                             file_name=$file, size=$size, mtime=$mtime, dup_count=$dup, scan_time=$now WHERE guid=$g"))
                    {
                        cmd.Parameters.AddWithValue("$tier", root.tier);
                        cmd.Parameters.AddWithValue("$root", root.path);
                        cmd.Parameters.AddWithValue("$path", filePath);
                        cmd.Parameters.AddWithValue("$file", (object)fileName ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$size", size);
                        cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                        cmd.Parameters.AddWithValue("$dup", dupNew);
                        cmd.Parameters.AddWithValue("$now", Now());
                        cmd.Parameters.AddWithValue("$g", guid);
                        cmd.ExecuteNonQuery();
                    }
                    return;
                }
                using (SqliteCommand cmd = NewCommand("UPDATE " + ModTable + " SET dup_count=$dup, scan_time=$now WHERE guid=$g"))
                {
                    cmd.Parameters.AddWithValue("$dup", dupNew);
                    cmd.Parameters.AddWithValue("$now", Now());
                    cmd.Parameters.AddWithValue("$g", guid);
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            using (SqliteCommand cmd = NewCommand(@"INSERT INTO " + ModTable + @"(guid,tier,root_path,file_path,file_name,size,mtime,dup_count,scan_time)
                     VALUES($guid,$tier,$root,$path,$file,$size,$mtime,$dup,$now)"))
            {
                cmd.Parameters.AddWithValue("$guid", guid);
                cmd.Parameters.AddWithValue("$tier", root.tier);
                cmd.Parameters.AddWithValue("$root", root.path);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$file", (object)fileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$dup", dupNew);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按跨库重算结果写回 mod 主表（副本被删 / 搬移后调用）。</summary>
        public void WriteModRow(string guid, int tier, string rootPath, string filePath, string fileName, long size, string mtime, long dupCount)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO " + ModTable + @"(guid,tier,root_path,file_path,file_name,size,mtime,dup_count,scan_time)
                     VALUES($guid,$tier,$root,$path,$file,$size,$mtime,$dup,$now)
                     ON CONFLICT(guid) DO UPDATE SET
                       tier=excluded.tier, root_path=excluded.root_path, file_path=excluded.file_path,
                       file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
                       dup_count=excluded.dup_count, scan_time=excluded.scan_time"))
            {
                cmd.Parameters.AddWithValue("$guid", guid);
                cmd.Parameters.AddWithValue("$tier", tier);
                cmd.Parameters.AddWithValue("$root", (object)rootPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$path", (object)filePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$file", (object)fileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$dup", dupCount);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>删除 mod 主表里的某条记录（该 guid 已无任何副本时用）。</summary>
        public void DeleteModRow(string guid)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM " + ModTable + " WHERE guid=$g"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>填充 mod 主表的 manifest 元数据（扫描 zipmod 时调用）。</summary>
        public void FillModMeta(ModInfo m)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE " + ModTable + @" SET name=$name, version=$ver, author=$author, website=$web,
                     description=$desc, schema_ver=$schema, entry_count=$entries, error=$err WHERE guid=$guid"))
            {
                cmd.Parameters.AddWithValue("$name", (object)m.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ver", (object)m.Version ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$author", (object)m.Author ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$web", (object)m.Website ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$desc", (object)m.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$schema", (object)m.SchemaVer ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$entries", m.EntryCount);
                cmd.Parameters.AddWithValue("$err", (object)m.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$guid", m.Guid);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>写入一张卡片，返回卡片 id。</summary>
        public long UpsertCard(CardInfo c, RootEntry root, string folder, byte[] thumb, string mtime)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO card(file_path,file_name,size,mtime,tier,root_path,folder,
                     card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error)
                     VALUES($path,$file,$size,$mtime,$tier,$root,$folder,$type,$ver,$img,$uar,$cnt,$thumb,$now,NULL)
                     ON CONFLICT(file_path) DO UPDATE SET
                       file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
                       tier=excluded.tier, root_path=excluded.root_path, folder=excluded.folder,
                       card_type=excluded.card_type, data_version=excluded.data_version, image_end=excluded.image_end,
                       uar_blocks=excluded.uar_blocks, mod_count=excluded.mod_count, scan_time=excluded.scan_time,
                       thumb=COALESCE(excluded.thumb, card.thumb)"))
            {
                cmd.Parameters.AddWithValue("$path", c.FilePath);
                cmd.Parameters.AddWithValue("$file", c.FileName);
                cmd.Parameters.AddWithValue("$size", c.Size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$tier", root.tier);
                cmd.Parameters.AddWithValue("$root", root.path);
                cmd.Parameters.AddWithValue("$folder", folder ?? "");
                cmd.Parameters.AddWithValue("$type", (object)c.CardType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ver", (object)c.DataVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$img", c.ImageEnd);
                cmd.Parameters.AddWithValue("$uar", c.UarBlocks);
                cmd.Parameters.AddWithValue("$cnt", c.DistinctModIds().Count);
                cmd.Parameters.AddWithValue("$thumb", thumb == null ? (object)DBNull.Value : thumb);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }

            using (SqliteCommand cmd = NewCommand("SELECT id FROM card WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$path", c.FilePath);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>替换一张卡片的全部 mod 引用。</summary>
        public void ReplaceCardRefs(long cardId, IReadOnlyList<ModRef> refs)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM card_mod WHERE card_id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.ExecuteNonQuery();
            }
            if (refs == null || refs.Count == 0)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT OR REPLACE INTO card_mod(card_id,mod_guid,property,slot,local_slot,category_no,rec_name,rec_author,rec_website)
                     VALUES($id,$guid,$prop,$slot,$local,$cat,$name,$author,$site)"))
            {
                SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter pGuid = cmd.Parameters.Add("$guid", SqliteType.Text);
                SqliteParameter pProp = cmd.Parameters.Add("$prop", SqliteType.Text);
                SqliteParameter pSlot = cmd.Parameters.Add("$slot", SqliteType.Integer);
                SqliteParameter pLocal = cmd.Parameters.Add("$local", SqliteType.Integer);
                SqliteParameter pCat = cmd.Parameters.Add("$cat", SqliteType.Integer);
                SqliteParameter pName = cmd.Parameters.Add("$name", SqliteType.Text);
                SqliteParameter pAuthor = cmd.Parameters.Add("$author", SqliteType.Text);
                SqliteParameter pSite = cmd.Parameters.Add("$site", SqliteType.Text);
                foreach (ModRef r in refs)
                {
                    if (string.IsNullOrEmpty(r.ModId))
                    {
                        continue;
                    }
                    pId.Value = cardId;
                    pGuid.Value = r.ModId;
                    pProp.Value = (object)r.Property ?? DBNull.Value;
                    pSlot.Value = r.Slot;
                    pLocal.Value = r.LocalSlot;
                    pCat.Value = r.CategoryNo;
                    pName.Value = (object)r.Name ?? DBNull.Value;
                    pAuthor.Value = (object)r.Author ?? DBNull.Value;
                    pSite.Value = (object)r.Website ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>四色聚合 CTE——mod 引用按所在连接前缀（主库本地表 / 库文件走 core）。</summary>
        private string ColorCte
        {
            get
            {
                return @"
            WITH per AS (
              SELECT cm.card_id, cm.mod_guid, MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) AS tier
              FROM card_mod cm LEFT JOIN " + ModTable + @" m ON m.guid = cm.mod_guid
              GROUP BY cm.card_id, cm.mod_guid
            ),
            agg AS (
              SELECT card_id,
                SUM(CASE WHEN tier=1 THEN 1 ELSE 0 END) AS green,
                SUM(CASE WHEN tier=2 THEN 1 ELSE 0 END) AS yellow,
                SUM(CASE WHEN tier=3 THEN 1 ELSE 0 END) AS red,
                SUM(CASE WHEN tier=9 THEN 1 ELSE 0 END) AS black
              FROM per GROUP BY card_id
            )";
            }
        }

        /// <summary>分页查询卡片（filter：all / pending / ready / black / nomod / nothumb；folder：文件夹过滤；root：库根过滤；order：排序键——mtime（默认）/ size；desc：组内方向，只对 size 生效，mtime 恒倒序）；size ≤ 0 = 不限条数。</summary>
        public List<CardRow> QueryCards(int page, int size, string filter, string q, string folder, string root, string order, bool desc)
        {
            var list = new List<CardRow>();
            string where = "WHERE 1=1";
            bool hasQ = !string.IsNullOrEmpty(q);
            bool hasFolder = folder != null;
            bool hasRoot = !string.IsNullOrEmpty(root);
            if (hasQ)
            {
                where += " AND c.file_name LIKE $q";
            }
            if (hasFolder)
            {
                where += " AND c.folder = $folder";
            }
            if (hasRoot)
            {
                where += " AND c.root_path = $root";
            }
            if (filter == "pending")
            {
                where += " AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0)) > 0";
            }
            else if (filter == "ready")
            {
                where += " AND c.mod_count > 0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0)) = 0";
            }
            else if (filter == "black")
            {
                where += " AND COALESCE(a.black,0) > 0";
            }
            else if (filter == "nomod")
            {
                where += " AND c.mod_count = 0";
            }
            else if (filter == "nothumb")
            {
                where += " AND c.thumb IS NULL";
            }

            string orderBy = "ORDER BY c.folder, c.mtime DESC, c.id";
            if (order == "size" && desc)
            {
                orderBy = "ORDER BY c.folder, c.size DESC, c.id";
            }
            else if (order == "size")
            {
                orderBy = "ORDER BY c.folder, c.size ASC, c.id";
            }
            string sql = ColorCte + @" SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL)
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id " + where + @" " + orderBy + @"
                     LIMIT $size OFFSET $off";

            using (SqliteCommand cmd = NewCommand(sql))
            {
                if (hasQ)
                {
                    cmd.Parameters.AddWithValue("$q", "%" + q + "%");
                }
                if (hasFolder)
                {
                    cmd.Parameters.AddWithValue("$folder", folder);
                }
                if (hasRoot)
                {
                    cmd.Parameters.AddWithValue("$root", root);
                }
                int limit = size <= 0 ? -1 : size;
                cmd.Parameters.AddWithValue("$size", limit);
                cmd.Parameters.AddWithValue("$off", (limit < 0 || page <= 1) ? 0 : (page - 1) * limit);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new CardRow
                        {
                            Id = r.GetInt64(0),
                            FileName = r.GetString(1),
                            CardType = r.IsDBNull(2) ? null : r.GetString(2),
                            Size = r.GetInt64(3),
                            Mtime = r.IsDBNull(4) ? null : r.GetString(4),
                            RootPath = r.IsDBNull(5) ? null : r.GetString(5),
                            Tier = r.GetInt32(6),
                            Folder = r.IsDBNull(7) ? "" : r.GetString(7),
                            ModCount = r.GetInt64(8),
                            Green = r.GetInt64(9),
                            Yellow = r.GetInt64(10),
                            Red = r.GetInt64(11),
                            Black = r.GetInt64(12),
                            HasThumb = r.GetInt64(13) != 0
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>卡片文件夹清单（含数量与未就绪数），供前端分类展示。</summary>
        public List<object> QueryFolders()
        {
            var list = new List<object>();
            string sql = ColorCte + @" SELECT c.root_path, c.folder, COUNT(*) AS n,
                     SUM(CASE WHEN c.mod_count > 0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0))=0 THEN 1 ELSE 0 END) AS ready,
                     SUM(CASE WHEN COALESCE(a.black,0) > 0 THEN 1 ELSE 0 END) AS black
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     GROUP BY c.root_path, c.folder ORDER BY c.root_path, c.folder";
            using (SqliteCommand cmd = NewCommand(sql))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new
                    {
                        rootPath = r.IsDBNull(0) ? "" : r.GetString(0),
                        folder = r.IsDBNull(1) ? "" : r.GetString(1),
                        count = r.GetInt64(2),
                        ready = r.GetInt64(3),
                        black = r.GetInt64(4)
                    });
                }
            }
            return list;
        }

        /// <summary>分页查询 mod（filter：all / used / unused / tier1 / tier2 / tier3 / dup）；size ≤ 0 = 不限条数。unused = 未被引用且最优副本在主库（级别 1）。</summary>
        public List<ModRow> QueryMods(int page, int size, string filter, string q)
        {
            var list = new List<ModRow>();
            string where = "WHERE 1=1";
            bool hasQ = !string.IsNullOrEmpty(q);
            if (hasQ)
            {
                where += " AND (m.guid LIKE $q OR m.file_name LIKE $q OR m.name LIKE $q)";
            }
            if (filter == "unused")
            {
                where += " AND m.tier=1 AND NOT EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)";
            }
            else if (filter == "used")
            {
                where += " AND EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)";
            }
            else if (filter == "tier1")
            {
                where += " AND m.tier=1";
            }
            else if (filter == "tier2")
            {
                where += " AND m.tier=2";
            }
            else if (filter == "tier3")
            {
                where += " AND m.tier=3";
            }
            else if (filter == "dup")
            {
                where += " AND m.dup_count > 1";
            }

            using (SqliteCommand cmd = NewCommand(@"SELECT m.guid, m.name, m.author, m.version, m.tier, m.root_path, m.file_name, m.file_path, m.size, m.dup_count,
                     (SELECT COUNT(DISTINCT cm.card_id) FROM card_mod cm WHERE cm.mod_guid=m.guid) AS used
                     FROM " + ModTable + @" m " + where + " ORDER BY used DESC, m.guid LIMIT $size OFFSET $off"))
            {
                if (hasQ)
                {
                    cmd.Parameters.AddWithValue("$q", "%" + q + "%");
                }
                int limit = size <= 0 ? -1 : size;
                cmd.Parameters.AddWithValue("$size", limit);
                cmd.Parameters.AddWithValue("$off", (limit < 0 || page <= 1) ? 0 : (page - 1) * limit);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ModRow
                        {
                            Guid = r.GetString(0),
                            Name = r.IsDBNull(1) ? null : r.GetString(1),
                            Author = r.IsDBNull(2) ? null : r.GetString(2),
                            Version = r.IsDBNull(3) ? null : r.GetString(3),
                            Tier = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                            RootPath = r.IsDBNull(5) ? null : r.GetString(5),
                            FileName = r.IsDBNull(6) ? null : r.GetString(6),
                            FilePath = r.IsDBNull(7) ? null : r.GetString(7),
                            Size = r.GetInt64(8),
                            DupCount = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                            Used = r.GetInt64(10)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>某张卡的引用明细（可按级别过滤：0=全部 / 2=仅缓存 / 3=仅冷冻 / 9=全库皆无）。</summary>
        public List<RefRow> QueryCardRefs(long cardId, int tierFilter)
        {
            var list = new List<RefRow>();
            string having = "";
            switch (tierFilter)
            {
                case 2:
                    having = "HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 2";
                    break;
                case 3:
                    having = "HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 3";
                    break;
                case 9:
                    having = "HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 9";
                    break;
                case 1:
                    having = "HAVING MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) = 1";
                    break;
            }

            string sql = @"SELECT cm.mod_guid, MIN(CASE WHEN m.tier IS NULL THEN 9 ELSE m.tier END) AS tier,
                     MIN(COALESCE(m.root_path,'')), MIN(COALESCE(m.file_path,'')), MIN(COALESCE(m.file_name,'')),
                     MIN(COALESCE(cm.property,'')), MIN(COALESCE(cm.rec_name,'')), MIN(COALESCE(cm.rec_author,'')), MIN(COALESCE(cm.rec_website,''))
                     FROM card_mod cm LEFT JOIN " + ModTable + @" m ON m.guid = cm.mod_guid
                     WHERE cm.card_id=$id
                     GROUP BY cm.mod_guid " + having + " ORDER BY tier, cm.mod_guid";
            using (SqliteCommand cmd = NewCommand(sql))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RefRow
                        {
                            Guid = r.GetString(0),
                            Tier = r.GetInt32(1),
                            RootPath = r.GetString(2),
                            FilePath = r.GetString(3),
                            FileName = r.GetString(4),
                            Property = r.GetString(5),
                            RecName = r.GetString(6),
                            RecAuthor = r.GetString(7),
                            RecWebsite = r.GetString(8)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>读取卡片缩略图（JPEG 字节）；无则返回 null。</summary>
        public byte[] LoadThumb(long cardId)
        {
            using (SqliteCommand cmd = NewCommand("SELECT thumb FROM card WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? null : (byte[])v;
            }
        }

        /// <summary>库快照——计数与四色统计。</summary>
        public Snapshot Snapshot()
        {
            var s = new Snapshot();
            using (SqliteCommand cmd = NewCommand(ColorCte + @" SELECT
                     (SELECT COUNT(*) FROM card),
                     (SELECT COUNT(*) FROM " + ModTable + @"),
                     (SELECT COUNT(*) FROM mod_file),
                     (SELECT COUNT(*) FROM per),
                     (SELECT COUNT(*) FROM " + ModTable + @" WHERE tier=1),
                     (SELECT COUNT(*) FROM " + ModTable + @" WHERE tier=2),
                     (SELECT COUNT(*) FROM " + ModTable + @" WHERE tier=3),
                     (SELECT COUNT(*) FROM card c LEFT JOIN agg a ON a.card_id=c.id WHERE c.mod_count>0 AND (COALESCE(a.yellow,0)+COALESCE(a.red,0)+COALESCE(a.black,0))=0),
                     (SELECT COUNT(*) FROM card WHERE mod_count>0),
                     (SELECT COALESCE(SUM(green),0) FROM agg),
                     (SELECT COALESCE(SUM(yellow),0) FROM agg),
                     (SELECT COALESCE(SUM(red),0) FROM agg),
                     (SELECT COALESCE(SUM(black),0) FROM agg),
                     (SELECT COUNT(*) FROM " + ModTable + @" m WHERE NOT EXISTS(SELECT 1 FROM card_mod cm WHERE cm.mod_guid=m.guid)),
                     (SELECT COUNT(*) FROM " + ModTable + @" WHERE dup_count>1)"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    s.Cards = r.GetInt64(0);
                    s.Mods = r.GetInt64(1);
                    s.ModFiles = r.GetInt64(2);
                    s.Refs = r.GetInt64(3);
                    s.ModsTier1 = r.GetInt64(4);
                    s.ModsTier2 = r.GetInt64(5);
                    s.ModsTier3 = r.GetInt64(6);
                    s.CardsReady = r.GetInt64(7);
                    s.CardsWithRefs = r.GetInt64(8);
                    s.Colors.Green = r.GetInt64(9);
                    s.Colors.Yellow = r.GetInt64(10);
                    s.Colors.Red = r.GetInt64(11);
                    s.Colors.Black = r.GetInt64(12);
                    s.UnusedMods = r.GetInt64(13);
                    s.DupMods = r.GetInt64(14);
                }
            }
            return s;
        }

        /// <summary>缺失 guid 排行（全库皆无，按被引用卡片数倒序）；top ≤ 0 = 不限条数。</summary>
        public List<KeyValuePair<string, long>> MissingRanking(int top)
        {
            var list = new List<KeyValuePair<string, long>>();
            using (SqliteCommand cmd = NewCommand(@"SELECT cm.mod_guid, COUNT(DISTINCT cm.card_id) AS c
                     FROM card_mod cm WHERE NOT EXISTS(SELECT 1 FROM " + ModTable + @" m WHERE m.guid=cm.mod_guid)
                     GROUP BY cm.mod_guid ORDER BY c DESC, cm.mod_guid LIMIT $top"))
            {
                cmd.Parameters.AddWithValue("$top", top <= 0 ? -1 : top);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new KeyValuePair<string, long>(r.GetString(0), r.GetInt64(1)));
                    }
                }
            }
            return list;
        }

        /// <summary>删掉一条 mod 副本行，返回被删内容（找不到返回 null）——跨库搬移的源侧用，mod 主表由调用方重算。</summary>
        public ModFileRecord RemoveModFileRow(string filePath)
        {
            ModFileRecord rec = null;
            using (SqliteCommand cmd = NewCommand("SELECT guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file WHERE file_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", filePath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        rec = new ModFileRecord
                        {
                            FilePath = filePath,
                            Guid = r.GetString(0),
                            Tier = r.GetInt32(1),
                            RootPath = r.IsDBNull(2) ? "" : r.GetString(2),
                            FileName = r.IsDBNull(3) ? null : r.GetString(3),
                            Size = r.GetInt64(4),
                            Mtime = r.IsDBNull(5) ? "" : r.GetString(5),
                            ScanTime = r.IsDBNull(6) ? "" : r.GetString(6)
                        };
                    }
                }
            }
            if (rec == null)
            {
                return null;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM mod_file WHERE file_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", filePath);
                cmd.ExecuteNonQuery();
            }
            return rec;
        }

        /// <summary>插入一条 mod 副本行（跨库搬移的目标侧用），并增量维护 mod 主表。</summary>
        public void InsertModFileRow(ModFileRecord rec, int tier, string rootPath, string newPath)
        {
            if (rec == null)
            {
                return;
            }
            bool existed = false;
            using (SqliteCommand cmd = NewCommand("SELECT COUNT(*) FROM mod_file WHERE file_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", newPath);
                existed = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
            string fileName = rec.FileName;
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Path.GetFileName(newPath);
            }
            string stamp = rec.ScanTime;
            if (string.IsNullOrEmpty(stamp))
            {
                stamp = Now();
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT OR REPLACE INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
                     VALUES($path,$guid,$tier,$root,$file,$size,$mtime,$now)"))
            {
                cmd.Parameters.AddWithValue("$path", newPath);
                cmd.Parameters.AddWithValue("$guid", rec.Guid);
                cmd.Parameters.AddWithValue("$tier", tier);
                cmd.Parameters.AddWithValue("$root", rootPath);
                cmd.Parameters.AddWithValue("$file", (object)fileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$size", rec.Size);
                cmd.Parameters.AddWithValue("$mtime", rec.Mtime ?? "");
                cmd.Parameters.AddWithValue("$now", stamp);
                cmd.ExecuteNonQuery();
            }
            if (!existed)
            {
                RootEntry root = new RootEntry { tier = tier, path = rootPath };
                UpdateModRowForFile(rec.Guid, root, newPath, fileName, rec.Size, rec.Mtime, 1);
            }
        }

        /// <summary>列出设置表全部条目（键前缀由调用方筛）。</summary>
        public Dictionary<string, string> LoadSettings()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT key, value FROM " + SettingTable))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }
            }
            return map;
        }

        /// <summary>本库里 card_mod 出现过的 mod guid 集合（跨库算「未被引用的 mod」用）。</summary>
        public HashSet<string> DistinctRefGuids()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            using (SqliteCommand cmd = NewCommand("SELECT DISTINCT mod_guid FROM card_mod"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    set.Add(r.GetString(0));
                }
            }
            return set;
        }

        /// <summary>本库 mod 副本行数。</summary>
        public long CountModFiles()
        {
            return Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM mod_file"), CultureInfo.InvariantCulture);
        }

        /// <summary>某 guid 的全部文件副本。</summary>
        public List<RefRow> QueryModFiles(string guid)
        {
            var list = new List<RefRow>();
            using (SqliteCommand cmd = NewCommand("SELECT guid, tier, root_path, file_path, file_name FROM mod_file WHERE guid=$g ORDER BY tier"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RefRow
                        {
                            Guid = r.GetString(0),
                            Tier = r.GetInt32(1),
                            RootPath = r.IsDBNull(2) ? "" : r.GetString(2),
                            FilePath = r.GetString(3),
                            FileName = r.IsDBNull(4) ? "" : r.GetString(4)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>单张卡片（含四色聚合；不存在返回 null）。</summary>
        public CardRow GetCard(long id)
        {
            string sql = ColorCte + @" SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL)
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     WHERE c.id = $id";
            using (SqliteCommand cmd = NewCommand(sql))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return null;
                    }
                    return new CardRow
                    {
                        Id = r.GetInt64(0),
                        FileName = r.GetString(1),
                        CardType = r.IsDBNull(2) ? null : r.GetString(2),
                        Size = r.GetInt64(3),
                        Mtime = r.IsDBNull(4) ? null : r.GetString(4),
                        RootPath = r.IsDBNull(5) ? null : r.GetString(5),
                        Tier = r.GetInt32(6),
                        Folder = r.IsDBNull(7) ? "" : r.GetString(7),
                        ModCount = r.GetInt64(8),
                        Green = r.GetInt64(9),
                        Yellow = r.GetInt64(10),
                        Red = r.GetInt64(11),
                        Black = r.GetInt64(12),
                        HasThumb = r.GetInt64(13) != 0
                    };
                }
            }
        }

        /// <summary>某 guid 被哪些卡片引用（分页）；size ≤ 0 = 不限条数。</summary>
        public List<CardRow> QueryCardsByMod(string guid, int page, int size)
        {
            var list = new List<CardRow>();
            string sql = ColorCte + @" SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL)
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     WHERE c.id IN (SELECT card_id FROM card_mod WHERE mod_guid = $guid)
                     ORDER BY c.root_path, c.folder, c.file_name LIMIT $size OFFSET $off";
            using (SqliteCommand cmd = NewCommand(sql))
            {
                cmd.Parameters.AddWithValue("$guid", guid);
                int limit = size <= 0 ? -1 : size;
                cmd.Parameters.AddWithValue("$size", limit);
                cmd.Parameters.AddWithValue("$off", (limit < 0 || page <= 1) ? 0 : (page - 1) * limit);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new CardRow
                        {
                            Id = r.GetInt64(0),
                            FileName = r.GetString(1),
                            CardType = r.IsDBNull(2) ? null : r.GetString(2),
                            Size = r.GetInt64(3),
                            Mtime = r.IsDBNull(4) ? null : r.GetString(4),
                            RootPath = r.IsDBNull(5) ? null : r.GetString(5),
                            Tier = r.GetInt32(6),
                            Folder = r.IsDBNull(7) ? "" : r.GetString(7),
                            ModCount = r.GetInt64(8),
                            Green = r.GetInt64(9),
                            Yellow = r.GetInt64(10),
                            Red = r.GetInt64(11),
                            Black = r.GetInt64(12),
                            HasThumb = r.GetInt64(13) != 0
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>某 guid 被引用的卡片总数。</summary>
        public long CountCardsByMod(string guid)
        {
            using (SqliteCommand cmd = NewCommand("SELECT COUNT(DISTINCT card_id) FROM card_mod WHERE mod_guid = $guid"))
            {
                cmd.Parameters.AddWithValue("$guid", guid);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value)
                {
                    return 0;
                }
                return Convert.ToInt64(v);
            }
        }

        /// <summary>某个 guid 在本库的副本行（含 size / mtime，供跨库重算用）。</summary>
        public List<ModFileRecord> QueryModFileRecords(string guid)
        {
            List<ModFileRecord> list = new List<ModFileRecord>();
            using (SqliteCommand cmd = NewCommand("SELECT file_path, guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file WHERE guid=$g ORDER BY tier"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ModFileRecord
                        {
                            FilePath = r.GetString(0),
                            Guid = r.GetString(1),
                            Tier = r.GetInt32(2),
                            RootPath = r.IsDBNull(3) ? "" : r.GetString(3),
                            FileName = r.IsDBNull(4) ? null : r.GetString(4),
                            Size = r.GetInt64(5),
                            Mtime = r.IsDBNull(6) ? "" : r.GetString(6),
                            ScanTime = r.IsDBNull(7) ? "" : r.GetString(7)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>本库全部 mod 副本行（跨库重复副本分组用）。</summary>
        public List<ModFileRecord> ListModFiles()
        {
            List<ModFileRecord> list = new List<ModFileRecord>();
            using (SqliteCommand cmd = NewCommand("SELECT file_path, guid, tier, root_path, file_name, size, mtime, scan_time FROM mod_file ORDER BY guid, tier"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new ModFileRecord
                    {
                        FilePath = r.GetString(0),
                        Guid = r.GetString(1),
                        Tier = r.GetInt32(2),
                        RootPath = r.IsDBNull(3) ? "" : r.GetString(3),
                        FileName = r.IsDBNull(4) ? null : r.GetString(4),
                        Size = r.GetInt64(5),
                        Mtime = r.IsDBNull(6) ? "" : r.GetString(6),
                        ScanTime = r.IsDBNull(7) ? "" : r.GetString(7)
                    });
                }
            }
            return list;
        }
        /// <summary>本库按 guid 的副本计数（总数 / 非旧版数）——重复副本组数与「待确认」组数用（跨库由 hub 合并）。</summary>
        public List<ModFileCount> ListModFileCounts()
        {
            List<ModFileCount> list = new List<ModFileCount>();
            using (SqliteCommand cmd = NewCommand(@"SELECT guid, COUNT(*), SUM(CASE WHEN file_name LIKE '%.old.zipmod' THEN 0 ELSE 1 END)
                             FROM mod_file WHERE guid IS NOT NULL AND guid <> '' GROUP BY guid"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new ModFileCount
                    {
                        Guid = r.GetString(0),
                        Total = r.GetInt64(1),
                        Live = r.IsDBNull(2) ? 0 : r.GetInt64(2)
                    });
                }
            }
            return list;
        }

        /// <summary>登记一条旧版记录（同一旧版路径覆盖写）——旧版登记是全局表，只在主库连接上调用。</summary>
        public void SetModOld(ModOldRecord rec)
        {
            if (!_isCore || rec == null || string.IsNullOrEmpty(rec.OldPath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_old(old_path,guid,old_name,old_version,new_name,new_version,marked_at)
                     VALUES($p,$g,$on,$ov,$nn,$nv,$at)
                     ON CONFLICT(old_path) DO UPDATE SET
                       guid=excluded.guid, old_name=excluded.old_name, old_version=excluded.old_version,
                       new_name=excluded.new_name, new_version=excluded.new_version, marked_at=excluded.marked_at"))
            {
                cmd.Parameters.AddWithValue("$p", rec.OldPath);
                cmd.Parameters.AddWithValue("$g", (object)rec.Guid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$on", (object)rec.OldName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ov", (object)rec.OldVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$nn", (object)rec.NewName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$nv", (object)rec.NewVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", string.IsNullOrEmpty(rec.MarkedAt) ? Now() : rec.MarkedAt);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按旧版文件路径销掉登记（该文件被正名 / 移除时）。</summary>
        public void DeleteModOld(string oldPath)
        {
            if (!_isCore || string.IsNullOrEmpty(oldPath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM mod_old WHERE old_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", oldPath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按 guid 销掉全部旧版登记（该 guid 已无旧版时）。</summary>
        public void DeleteModOldByGuid(string guid)
        {
            if (!_isCore || string.IsNullOrEmpty(guid))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM mod_old WHERE guid=$g"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>某 guid 的旧版登记（按登记时刻倒序）。</summary>
        public List<ModOldRecord> QueryModOld(string guid)
        {
            List<ModOldRecord> list = new List<ModOldRecord>();
            if (!_isCore || string.IsNullOrEmpty(guid))
            {
                return list;
            }
            using (SqliteCommand cmd = NewCommand("SELECT old_path, guid, old_name, old_version, new_name, new_version, marked_at FROM mod_old WHERE guid=$g ORDER BY marked_at DESC"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(ReadModOld(r));
                    }
                }
            }
            return list;
        }

        /// <summary>全部旧版登记——guid → 最近一条（mod 列表标记「有旧版」用）。</summary>
        public Dictionary<string, ModOldRecord> LoadModOldMap()
        {
            Dictionary<string, ModOldRecord> map = new Dictionary<string, ModOldRecord>(StringComparer.Ordinal);
            if (!_isCore)
            {
                return map;
            }
            using (SqliteCommand cmd = NewCommand("SELECT old_path, guid, old_name, old_version, new_name, new_version, marked_at FROM mod_old ORDER BY marked_at"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    ModOldRecord rec = ReadModOld(r);
                    if (string.IsNullOrEmpty(rec.Guid))
                    {
                        continue;
                    }
                    map[rec.Guid] = rec;
                }
            }
            return map;
        }
        /// <summary>全部旧版登记（逐条——同一 guid 可有多条，按登记时刻排序；重复副本弹窗的「已登记」标记用）。</summary>
        public List<ModOldRecord> ListModOld()
        {
            List<ModOldRecord> list = new List<ModOldRecord>();
            if (!_isCore)
            {
                return list;
            }
            using (SqliteCommand cmd = NewCommand("SELECT old_path, guid, old_name, old_version, new_name, new_version, marked_at FROM mod_old ORDER BY marked_at"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(ReadModOld(r));
                }
            }
            return list;
        }
        /// <summary>写一条组成档案（同一 guid 覆盖写）——组成档案是主库表，只在主库连接上调用。</summary>
        public void SaveComposition(ModComposition rec)
        {
            if (!_isCore || rec == null || string.IsNullOrEmpty(rec.Guid))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_composition(guid,file_path,file_name,size,mtime,entry_count,total_size,total_compressed,entries,analyzed_at,texts)
                             VALUES($g,$fp,$fn,$sz,$mt,$ec,$ts,$tc,$en,$at,$tx)
                             ON CONFLICT(guid) DO UPDATE SET
                               file_path=excluded.file_path, file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
                               entry_count=excluded.entry_count, total_size=excluded.total_size, total_compressed=excluded.total_compressed,
                               entries=excluded.entries, analyzed_at=excluded.analyzed_at, texts=excluded.texts"))
            {
                cmd.Parameters.AddWithValue("$g", rec.Guid);
                cmd.Parameters.AddWithValue("$fp", (object)rec.FilePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fn", (object)rec.FileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sz", rec.Size);
                cmd.Parameters.AddWithValue("$mt", (object)rec.Mtime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ec", rec.EntryCount);
                cmd.Parameters.AddWithValue("$ts", rec.TotalSize);
                cmd.Parameters.AddWithValue("$tc", rec.TotalCompressed);
                cmd.Parameters.AddWithValue("$en", (object)rec.Entries ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", string.IsNullOrEmpty(rec.AnalyzedAt) ? Now() : rec.AnalyzedAt);
                cmd.Parameters.AddWithValue("$tx", (object)rec.Texts ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>读某 guid 的组成档案（没有返回 null）——组成档案是主库表，只在主库连接上调用。</summary>
        public ModComposition LoadComposition(string guid)
        {
            if (!_isCore || string.IsNullOrEmpty(guid))
            {
                return null;
            }
            using (SqliteCommand cmd = NewCommand("SELECT guid, file_path, file_name, size, mtime, entry_count, total_size, total_compressed, entries, analyzed_at, texts FROM mod_composition WHERE guid=$g"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return null;
                    }
                    return new ModComposition
                    {
                        Guid = r.GetString(0),
                        FilePath = r.IsDBNull(1) ? null : r.GetString(1),
                        FileName = r.IsDBNull(2) ? null : r.GetString(2),
                        Size = r.GetInt64(3),
                        Mtime = r.IsDBNull(4) ? null : r.GetString(4),
                        EntryCount = r.GetInt64(5),
                        TotalSize = r.GetInt64(6),
                        TotalCompressed = r.GetInt64(7),
                        Entries = r.IsDBNull(8) ? null : r.GetString(8),
                        AnalyzedAt = r.IsDBNull(9) ? "" : r.GetString(9),
                        Texts = r.IsDBNull(10) ? null : r.GetString(10)
                    };
                }
            }
        }

        /// <summary>写一条 unity3d 解析档案（同一 guid + 条目路径覆盖写）——主库表，只在主库连接上调用。</summary>
        public void SaveU3d(ModU3d rec)
        {
            if (!_isCore || rec == null || string.IsNullOrEmpty(rec.Guid) || string.IsNullOrEmpty(rec.EntryPath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_u3d(guid,entry_path,file_path,size,mtime,texture_count,textures,class_summary,parsed_at)
                             VALUES($g,$ep,$fp,$sz,$mt,$tc,$tx,$cs,$pa)
                             ON CONFLICT(guid,entry_path) DO UPDATE SET
                               file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
                               texture_count=excluded.texture_count, textures=excluded.textures,
                               class_summary=excluded.class_summary, parsed_at=excluded.parsed_at"))
            {
                cmd.Parameters.AddWithValue("$g", rec.Guid);
                cmd.Parameters.AddWithValue("$ep", rec.EntryPath);
                cmd.Parameters.AddWithValue("$fp", (object)rec.FilePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sz", rec.Size);
                cmd.Parameters.AddWithValue("$mt", (object)rec.Mtime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tc", rec.TextureCount);
                cmd.Parameters.AddWithValue("$tx", (object)rec.Textures ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cs", (object)rec.ClassSummary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pa", string.IsNullOrEmpty(rec.ParsedAt) ? Now() : rec.ParsedAt);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>读某 guid + 条目路径的 unity3d 解析档案（没有返回 null）——主库表。</summary>
        public ModU3d LoadU3d(string guid, string entryPath)
        {
            if (!_isCore || string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(entryPath))
            {
                return null;
            }
            using (SqliteCommand cmd = NewCommand("SELECT guid, entry_path, file_path, size, mtime, texture_count, textures, class_summary, parsed_at FROM mod_u3d WHERE guid=$g AND entry_path=$ep"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                cmd.Parameters.AddWithValue("$ep", entryPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return null;
                    }
                    return ReadU3d(r);
                }
            }
        }

        /// <summary>读某 guid 的全部 unity3d 解析档案（条目路径 → 档案）——供组成区「已解析」标蓝判定。</summary>
        public Dictionary<string, ModU3d> LoadU3dMap(string guid)
        {
            Dictionary<string, ModU3d> map = new Dictionary<string, ModU3d>(StringComparer.Ordinal);
            if (!_isCore || string.IsNullOrEmpty(guid))
            {
                return map;
            }
            using (SqliteCommand cmd = NewCommand("SELECT guid, entry_path, file_path, size, mtime, texture_count, textures, class_summary, parsed_at FROM mod_u3d WHERE guid=$g"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        ModU3d rec = ReadU3d(r);
                        map[rec.EntryPath] = rec;
                    }
                }
            }
            return map;
        }

        /// <summary>读一行 unity3d 解析档案。</summary>
        private static ModU3d ReadU3d(SqliteDataReader r)
        {
            return new ModU3d
            {
                Guid = r.GetString(0),
                EntryPath = r.GetString(1),
                FilePath = r.IsDBNull(2) ? null : r.GetString(2),
                Size = r.GetInt64(3),
                Mtime = r.IsDBNull(4) ? null : r.GetString(4),
                TextureCount = r.GetInt64(5),
                Textures = r.IsDBNull(6) ? null : r.GetString(6),
                ClassSummary = r.IsDBNull(7) ? null : r.GetString(7),
                ParsedAt = r.IsDBNull(8) ? "" : r.GetString(8)
            };
        }

        /// <summary>读一行旧版登记。</summary>
        private static ModOldRecord ReadModOld(SqliteDataReader r)
        {
            return new ModOldRecord
            {
                OldPath = r.GetString(0),
                Guid = r.IsDBNull(1) ? null : r.GetString(1),
                OldName = r.IsDBNull(2) ? null : r.GetString(2),
                OldVersion = r.IsDBNull(3) ? null : r.GetString(3),
                NewName = r.IsDBNull(4) ? null : r.GetString(4),
                NewVersion = r.IsDBNull(5) ? null : r.GetString(5),
                MarkedAt = r.IsDBNull(6) ? "" : r.GetString(6)
            };
        }

        /// <summary>本库里各 mod guid 被引用的卡片数（跨库合并 used 用）。</summary>
        public Dictionary<string, long> RefCountsByGuid()
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.Ordinal);
            using (SqliteCommand cmd = NewCommand("SELECT mod_guid, COUNT(DISTINCT card_id) FROM card_mod GROUP BY mod_guid"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = r.GetInt64(1);
                }
            }
            return map;
        }

        /// <summary>主库级 mod 的 guid 与字节数（最优副本在主库 · 级别 1），按字节数降序——「未引用 mod 移到缓存库」的清单来源（须在主库连接上调用）。</summary>
        public List<KeyValuePair<string, long>> MainModSizes()
        {
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
            using (SqliteCommand cmd = NewCommand("SELECT guid, size FROM " + ModTable + " WHERE tier=1 ORDER BY size DESC"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new KeyValuePair<string, long>(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt64(1)));
                }
            }
            return list;
        }

        /// <summary>重建作者聚合表（整表重建——mod 主表是唯一真相源，聚合表只作读侧缓存；扫描末尾调用一次即可）。</summary>
        public void RefreshModAuthors()
        {
            Exec("DELETE FROM " + AuthorTable);
            Exec(@"INSERT INTO " + AuthorTable + @"(author, mod_count)
                     SELECT COALESCE(TRIM(author),''), COUNT(*) FROM " + ModTable + @" GROUP BY COALESCE(TRIM(author),'')");
        }

        /// <summary>作者清单（按发布的 mod 数量倒序，同数量按作者名）。</summary>
        public List<AuthorRow> QueryAuthors()
        {
            var list = new List<AuthorRow>();
            using (SqliteCommand cmd = NewCommand("SELECT author, mod_count FROM " + AuthorTable + " ORDER BY mod_count DESC, author"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new AuthorRow
                    {
                        Author = r.IsDBNull(0) ? "" : r.GetString(0),
                        Count = r.IsDBNull(1) ? 0 : r.GetInt64(1)
                    });
                }
            }
            return list;
        }

        /// <summary>回收空间（迁移搬行后调用）。</summary>
        public void Vacuum()
        {
            Exec("VACUUM");
        }

        /// <summary>把主库里某个库根的数据整段搬进本库文件，再从主库删掉这些行（存量迁移用）——本库连接必须 ATTACH 了主库。</summary>
        public void AdoptRootFromCore(string rootPath)
        {
            if (_isCore)
            {
                return;
            }
            ExecWithPath(@"INSERT OR IGNORE INTO card(id,file_path,file_name,size,mtime,tier,root_path,folder,
                     card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error)
                   SELECT id,file_path,file_name,size,mtime,tier,root_path,folder,
                     card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error
                   FROM core.card WHERE root_path=$p", rootPath);
            ExecWithPath(@"INSERT OR IGNORE INTO card_mod(card_id,mod_guid,property,slot,local_slot,category_no,rec_name,rec_author,rec_website)
                   SELECT cm.card_id,cm.mod_guid,cm.property,cm.slot,cm.local_slot,cm.category_no,cm.rec_name,cm.rec_author,cm.rec_website
                   FROM core.card_mod cm JOIN core.card c ON c.id = cm.card_id WHERE c.root_path=$p", rootPath);
            ExecWithPath(@"INSERT OR IGNORE INTO mod_file(file_path,guid,tier,root_path,file_name,size,mtime,scan_time)
                   SELECT file_path,guid,tier,root_path,file_name,size,mtime,scan_time
                   FROM core.mod_file WHERE root_path=$p", rootPath);
            ExecWithPath("DELETE FROM core.card_mod WHERE card_id IN (SELECT id FROM core.card WHERE root_path=$p)", rootPath);
            ExecWithPath("DELETE FROM core.card WHERE root_path=$p", rootPath);
            ExecWithPath("DELETE FROM core.mod_file WHERE root_path=$p", rootPath);
        }

        /// <summary>带单个路径参数的写语句。</summary>
        private void ExecWithPath(string sql, string path)
        {
            using (SqliteCommand cmd = NewCommand(sql))
            {
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>释放连接。</summary>
        public void Dispose()
        {
            Rollback();
            _conn.Dispose();
        }
        /// <summary>登记一条卡片编辑留档（原版留在软件内部 + 与卡片的对应关系）——留档表是主库表，只在主库连接上调用。</summary>
        public void AddCardEdit(CardEditRecord rec)
        {
            if (!_isCore || rec == null || string.IsNullOrEmpty(rec.CardPath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO card_edit(card_path,card_name,lib,archived_file,archived_size,archived_mtime,changes,edited_at)
                             VALUES($p,$n,$l,$f,$s,$m,$c,$at)"))
            {
                cmd.Parameters.AddWithValue("$p", rec.CardPath);
                cmd.Parameters.AddWithValue("$n", (object)rec.CardName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", rec.Lib);
                cmd.Parameters.AddWithValue("$f", (object)rec.ArchivedFile ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", rec.ArchivedSize);
                cmd.Parameters.AddWithValue("$m", (object)rec.ArchivedMtime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object)rec.Changes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", string.IsNullOrEmpty(rec.EditedAt) ? Now() : rec.EditedAt);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>某张卡片的编辑留档（按留档时刻倒序）——「寻找旧版」用。</summary>
        public List<CardEditRecord> ListCardEdits(string cardPath)
        {
            List<CardEditRecord> list = new List<CardEditRecord>();
            if (!_isCore || string.IsNullOrEmpty(cardPath))
            {
                return list;
            }
            using (SqliteCommand cmd = NewCommand("SELECT id,card_path,card_name,lib,archived_file,archived_size,archived_mtime,changes,edited_at FROM card_edit WHERE card_path=$p ORDER BY edited_at DESC, id DESC"))
            {
                cmd.Parameters.AddWithValue("$p", cardPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        CardEditRecord rec = new CardEditRecord();
                        rec.Id = r.GetInt64(0);
                        rec.CardPath = r.IsDBNull(1) ? null : r.GetString(1);
                        rec.CardName = r.IsDBNull(2) ? null : r.GetString(2);
                        rec.Lib = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                        rec.ArchivedFile = r.IsDBNull(4) ? null : r.GetString(4);
                        rec.ArchivedSize = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                        rec.ArchivedMtime = r.IsDBNull(6) ? null : r.GetString(6);
                        rec.Changes = r.IsDBNull(7) ? null : r.GetString(7);
                        rec.EditedAt = r.IsDBNull(8) ? null : r.GetString(8);
                        list.Add(rec);
                    }
                }
            }
            return list;
        }
        /// <summary>更新一张卡片记录的字节数——编辑后文件长度变了；修改时间按最小改动原则还原，故只更新字节数。</summary>
        public void UpdateCardSize(long id, long size)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE card SET size=$s WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$s", size);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
