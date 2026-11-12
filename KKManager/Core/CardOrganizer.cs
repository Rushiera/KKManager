using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>卡片整理规则的匹配面。</summary>
    public enum CardRuleKind
    {
        /// <summary>源目录（相对源根第一段）→ 作品。</summary>
        Dir,

        /// <summary>关键词（角色名 + 相对路径）→ 作品。</summary>
        Keyword,

        /// <summary>角色名精确匹配 → 作品（从既有索引学出来的规则用此型，避免子串误伤）。</summary>
        Chara,

        /// <summary>源目录 → 作者。</summary>
        Author
    }

    /// <summary>一条卡片整理规则。</summary>
    public class CardRule
    {
        /// <summary>匹配面。</summary>
        public CardRuleKind Kind { get; set; }

        /// <summary>匹配串（Dir / Author 为目录名；Keyword 为关键词）。</summary>
        public string Match { get; set; }

        /// <summary>命中后填的值（作品名或作者名）。</summary>
        public string Value { get; set; }

        /// <summary>来源：builtin / learn / manual（DB 为真相源；TSV 旧三列格式导入时视为 manual）。</summary>
        public string Origin { get; set; }

        /// <summary>学到此规则时的支撑卡片数（learn 统计；非学习来源为 0）。</summary>
        public int Votes { get; set; }

        /// <summary>该 match 在学习源里出现过的作品数（1 = 单作品强证据）。</summary>
        public int Works { get; set; }

        /// <summary>是否启用（DB 停用不删，可回滚）。</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>卡片整理计划的一行。</summary>
    public class CardPlanItem
    {
        /// <summary>源文件绝对路径。</summary>
        public string SrcPath { get; set; }

        /// <summary>归类（人物卡 / 服装卡 / 场景卡 / mod / 其它）。</summary>
        public string Kind { get; set; }

        /// <summary>卡类型标记（非卡为空）。</summary>
        public string CardType { get; set; }

        /// <summary>游戏内角色名。</summary>
        public string CharaName { get; set; }

        /// <summary>作品（B1 维度）。</summary>
        public string Work { get; set; }

        /// <summary>作者（B2 维度）。</summary>
        public string Author { get; set; }

        /// <summary>来源（源根目录名 = 召回键）。</summary>
        public string SrcRoot { get; set; }

        /// <summary>相对源根的路径（召回键）。</summary>
        public string SrcRel { get; set; }

        /// <summary>归位后的目标绝对路径。</summary>
        public string DestPath { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>备注（改名 / 跳过原因）。</summary>
        public string Note { get; set; }

        /// <summary>是否搬运（false = 跳过或已在位）。</summary>
        public bool Move { get; set; }

        /// <summary>状态：pending / moved / skipped / conflict / failed。</summary>
        public string State { get; set; }
    }

    /// <summary>作品的大类 / 子类（人物卡分层：游戏/galgame、动画/番剧、其它平铺等）。</summary>
    public class WorkCategory
    {
        /// <summary>大类（游戏 / 动画 / 其它 / 待确认）。</summary>
        public string Cat { get; set; }

        /// <summary>子类（galgame / 手游 / 单机 / 番剧 / 剧场版 / VTuber；其它类为空 = 平铺）。</summary>
        public string Sub { get; set; }
    }

    /// <summary>卡片整理计划——两份 TSV 之间的一等公民（计划 / 执行 / 回读共用）。</summary>
    public class CardPlan
    {
        /// <summary>目标根（整理库根）。</summary>
        public string DestRoot { get; set; }

        /// <summary>规则文件路径（空 = 只用内置规则）。</summary>
        public string RulesPath { get; set; }

        /// <summary>计划生成时刻。</summary>
        public string CreatedAt { get; set; }

        /// <summary>全部条目。</summary>
        public List<CardPlanItem> Items { get; } = new List<CardPlanItem>();

        /// <summary>统计某状态的条目数。</summary>
        public int Count(string state)
        {
            int n = 0;
            foreach (CardPlanItem it in Items)
            {
                if (it.State == state)
                {
                    n = n + 1;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// 卡片整理——扫源目录、判卡类型与角色名、判作品与作者、出归位计划、按计划搬移。
    /// 与 FileClean 的分工：本类只负责「分类 + 归位」，索引视图由调用方从计划 TSV 生成。
    /// </summary>
    public static class CardOrganizer
    {
        /// <summary>归类：人物卡。</summary>
        public const string KindChara = "人物卡";

        /// <summary>归类：服装卡。</summary>
        public const string KindClothes = "服装卡";

        /// <summary>归类：场景卡。</summary>
        public const string KindScene = "场景卡";

        /// <summary>归类：mod 文件。</summary>
        public const string KindMod = "mod";

        /// <summary>归类：其它文件。</summary>
        public const string KindOther = "其它";

        /// <summary>作品判不出时的占位值（筛选键，不猜死）。</summary>
        public const string WorkPending = "待确认";

        /// <summary>EroMakeChara 卡类型标记——单列一类，不判作品，整体落 06-EroMakeChara。</summary>
        public const string EroMakeCardType = "【EroMakeChara】";

        /// <summary>EroMakeChara 目标目录（整理根下的单独类，不分子目录）。</summary>
        public const string EroMakeFolder = "06-EroMakeChara";

        /// <summary>状态：待搬。</summary>
        public const string StatePending = "pending";

        /// <summary>状态：已就位。</summary>
        public const string StateMoved = "moved";

        /// <summary>状态：跳过（重复 / 不在源）。</summary>
        public const string StateSkipped = "skipped";

        /// <summary>状态：冲突（目标已存在同名文件）。</summary>
        public const string StateConflict = "conflict";

        /// <summary>状态：失败。</summary>
        public const string StateFailed = "failed";

        /// <summary>
        /// 内置规则——Koikatsu 卡片领域的默认判据表。
        /// 外部规则文件（--rules）逐条追加，同 Match 同 Kind 时外部覆盖内置。
        /// </summary>
        public static List<CardRule> BuiltinRules()
        {
            var rules = new List<CardRule>();
            AddDir(rules, "genshin_impact", "原神");
            AddDir(rules, "honkai_impact", "崩坏：星穹铁道");
            AddDir(rules, "girls_frontline", "少女前线");
            AddDir(rules, "princessconnect", "公主连结");
            AddDir(rules, "Umas!", "赛马娘");
            AddDir(rules, "vtuber", "VTuber");
            AddDir(rules, "colopl_rune_story", "白猫Project");
            AddDir(rules, "itai_no_wa_iya_nano_de_bougyoryoku_ni_kyokufuri_shitai_to_omoimasu", "因为太怕痛就全点防御力了");
            AddDir(rules, "爱MAS", "偶像大师");
            AddDir(rules, "collection：ccccp_oc", "原创OC");

            AddAuthor(rules, "method", "method");
            AddAuthor(rules, "MTkano", "MTkano");
            AddAuthor(rules, "JohnDoe#1519", "JohnDoe#1519");
            AddAuthor(rules, "collection：ccccp_oc", "ccccp");

            // 关键词（角色名 / 文件名 / 相对路径）——只在目录规则未命中时参与
            AddKw(rules, "Nekopara", "Nekopara");
            AddKw(rules, "Sanoba Witch", "Sanoba Witch");
            AddKw(rules, "恋爱决胜战", "恋爱决胜战");
            AddKw(rules, "明日方舟", "明日方舟");
            AddKw(rules, "Arknights", "明日方舟");
            AddKw(rules, "suzuran", "明日方舟");
            AddKw(rules, "碧蓝航线", "碧蓝航线");
            AddKw(rules, "Azur Lane", "碧蓝航线");
            AddKw(rules, "少女前线", "少女前线");
            AddKw(rules, "原神", "原神");
            AddKw(rules, "崩坏", "崩坏：星穹铁道");
            AddKw(rules, "鸣潮", "鸣潮");
            AddKw(rules, "Wuthering", "鸣潮");
            AddKw(rules, "蔚蓝档案", "蔚蓝档案");
            AddKw(rules, "Blue Archive", "蔚蓝档案");
            AddKw(rules, "赛马娘", "赛马娘");
            AddKw(rules, "ウマ娘", "赛马娘");
            AddKw(rules, "偶像大师", "偶像大师");
            AddKw(rules, "デレマス", "偶像大师");
            AddKw(rules, "公主连结", "公主连结");
            AddKw(rules, "プリコネ", "公主连结");
            AddKw(rules, "Fate", "Fate");
            AddKw(rules, "舰これ", "舰队Collection");
            AddKw(rules, "艦これ", "舰队Collection");
            AddKw(rules, "VTuber", "VTuber");
            AddKw(rules, "ホロライブ", "Hololive");
            AddKw(rules, "Hololive", "Hololive");
            AddKw(rules, "にじさんじ", "NIJISANJI");
            return rules;
        }

        private static void AddDir(List<CardRule> rules, string match, string value)
        {
            rules.Add(new CardRule { Kind = CardRuleKind.Dir, Match = match, Value = value });
        }

        private static void AddAuthor(List<CardRule> rules, string match, string value)
        {
            rules.Add(new CardRule { Kind = CardRuleKind.Author, Match = match, Value = value });
        }

        private static void AddKw(List<CardRule> rules, string match, string value)
        {
            rules.Add(new CardRule { Kind = CardRuleKind.Keyword, Match = match, Value = value });
        }

        /// <summary>
        /// 读外部规则文件——TSV 三列（类型 / 匹配串 / 值），类型取 dir / kw / author，空行与 # 开头跳过。
        /// 文件不存在返回空表并出声（不静默）；解析失败的行逐条出声。
        /// </summary>
        public static List<CardRule> LoadRules(string path, Action<string> log)
        {
            var rules = new List<CardRule>();
            if (string.IsNullOrEmpty(path))
            {
                return rules;
            }
            if (!File.Exists(path))
            {
                if (log != null)
                {
                    log("规则文件不存在，只用内置规则：" + path);
                }
                return rules;
            }
            int lineNo = 0;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                lineNo = lineNo + 1;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = line.Split('\t');
                if (cols.Length < 3)
                {
                    if (log != null)
                    {
                        log("规则第 " + lineNo.ToString(CultureInfo.InvariantCulture) + " 行列数不足，已跳过：" + line);
                    }
                    continue;
                }
                string kind = cols[0].Trim().ToLowerInvariant();
                int votes = 0;
                int works = 0;
                string origin = cols.Length >= 4 ? cols[3].Trim().ToLowerInvariant() : "";
                if (origin.Length == 0)
                {
                    origin = "manual";
                }
                if (cols.Length >= 5)
                {
                    int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out votes);
                }
                if (cols.Length >= 6)
                {
                    int.TryParse(cols[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out works);
                }
                CardRuleKind k;
                if (kind == "dir")
                {
                    k = CardRuleKind.Dir;
                }
                else if (kind == "kw")
                {
                    k = CardRuleKind.Keyword;
                }
                else if (kind == "chara")
                {
                    k = CardRuleKind.Chara;
                }
                else if (kind == "author")
                {
                    k = CardRuleKind.Author;
                }
                else
                {
                    if (log != null)
                    {
                        log("规则第 " + lineNo.ToString(CultureInfo.InvariantCulture) + " 行类型未知，已跳过：" + kind);
                    }
                    continue;
                }
                rules.Add(new CardRule { Kind = k, Match = cols[1].Trim(), Value = cols[2].Trim(), Origin = origin, Votes = votes, Works = works });
            }
            return rules;
        }

        /// <summary>把规则表写成 TSV（自举默认规则文件用）。</summary>
        public static void WriteRules(string path, List<CardRule> rules)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var sb = new StringBuilder();
            sb.AppendLine("# 类型\tdir / kw / chara / author\t值\t来源\t票数\t作品数");
            sb.AppendLine("# dir = 源目录名 → 作品；kw = 关键词 → 作品；chara = 角色名精确 → 作品；author = 源目录名 → 作者");
            sb.AppendLine("# 来源：builtin / learn / manual；票数 / 作品数为 learn 统计（导出全量带上，导入按列补齐）");
            foreach (CardRule r in rules)
            {
                string kind = r.Kind == CardRuleKind.Dir ? "dir"
                    : (r.Kind == CardRuleKind.Author ? "author"
                    : (r.Kind == CardRuleKind.Chara ? "chara" : "kw"));
                sb.AppendLine(kind + "\t" + r.Match + "\t" + r.Value + "\t" + (r.Origin ?? "") + "\t"
                    + r.Votes.ToString(CultureInfo.InvariantCulture) + "\t" + r.Works.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>
        /// 从索引 / 计划 TSV 学「角色名 → 作品」规则——只取作品明确的行；同名多作品按出现次数取多数。
        /// 学出来的规则用 chara 型（角色名精确匹配），避免子串误伤。
        /// </summary>
        public static List<CardRule> LearnRules(string tsvPath, Action<string> log)
        {
            var rules = new List<CardRule>();
            if (string.IsNullOrEmpty(tsvPath) || !File.Exists(tsvPath))
            {
                if (log != null)
                {
                    log("索引文件不存在：" + tsvPath);
                }
                return rules;
            }
            string[] lines = File.ReadAllLines(tsvPath, Encoding.UTF8);
            if (lines.Length < 2)
            {
                return rules;
            }
            string[] head = lines[0].Split('\t');
            int nameCol = -1;
            int workCol = -1;
            for (int i = 0; i < head.Length; i++)
            {
                string h = head[i].Trim();
                if (h == "角色名")
                {
                    nameCol = i;
                }
                else if (h == "作品")
                {
                    workCol = i;
                }
            }
            if (nameCol < 0 || workCol < 0)
            {
                if (log != null)
                {
                    log("表头缺少「角色名」或「作品」列：" + tsvPath);
                }
                return rules;
            }
            int need = Math.Max(nameCol, workCol);
            var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length <= need)
                {
                    continue;
                }
                string name = c[nameCol].Trim();
                string work = c[workCol].Trim();
                if (name.Length == 0 || work.Length == 0 || work == WorkPending || work.Contains("待归包"))
                {
                    continue;
                }
                Dictionary<string, int> tally;
                if (!votes.TryGetValue(name, out tally))
                {
                    tally = new Dictionary<string, int>(StringComparer.Ordinal);
                    votes[name] = tally;
                }
                tally[work] = (tally.ContainsKey(work) ? tally[work] : 0) + 1;
            }
            foreach (KeyValuePair<string, Dictionary<string, int>> kv in votes)
            {
                string best = null;
                int bestCount = 0;
                foreach (KeyValuePair<string, int> w in kv.Value)
                {
                    if (w.Value > bestCount)
                    {
                        best = w.Key;
                        bestCount = w.Value;
                    }
                }
                rules.Add(new CardRule { Kind = CardRuleKind.Chara, Match = kv.Key, Value = best, Origin = "learn", Votes = bestCount, Works = kv.Value.Count });
            }
            if (log != null)
            {
                log("从索引学到 " + rules.Count.ToString(CultureInfo.InvariantCulture) + " 条角色名规则：" + tsvPath);
            }
            return rules;
        }

        /// <summary>把规则追加进规则文件——已有同名规则保留（人工改的优先），新规则追加。</summary>
        public static void AppendRules(string path, List<CardRule> rules)
        {
            List<CardRule> existing = LoadRules(path, null);
            List<CardRule> merged = MergeRules(rules, existing);
            WriteRules(path, merged);
        }

        /// <summary>
        /// 生成「重排计划」——把已归位的卡片按某维度分子目录（work / author / chara），或恢复扁平（flat）。
        /// 元数据读自索引 TSV（现路径 / 作品 / 作者 / 角色名），只动卡片（类型列 = 卡），不动 mod 与其它。
        /// </summary>
        public static CardPlan BuildLayoutPlan(string root, string indexTsv, string by, Action<string> log)
        {
            var plan = new CardPlan
            {
                DestRoot = root,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            if (string.IsNullOrEmpty(indexTsv) || !File.Exists(indexTsv))
            {
                if (log != null)
                {
                    log("索引文件不存在：" + indexTsv);
                }
                return plan;
            }
            string[] lines = File.ReadAllLines(indexTsv, Encoding.UTF8);
            if (lines.Length < 2)
            {
                return plan;
            }
            string[] head = lines[0].Split('\t');
            int colDest = -1;
            int colKind = -1;
            int colCtype = -1;
            int colName = -1;
            int colWork = -1;
            int colAuthor = -1;
            int colSrc = -1;
            int colInner = -1;
            int colSize = -1;
            for (int i = 0; i < head.Length; i++)
            {
                string h = head[i].Trim();
                if (h == "现路径")
                {
                    colDest = i;
                }
                else if (h == "类型")
                {
                    colKind = i;
                }
                else if (h == "卡类型")
                {
                    colCtype = i;
                }
                else if (h == "角色名")
                {
                    colName = i;
                }
                else if (h == "作品")
                {
                    colWork = i;
                }
                else if (h == "作者")
                {
                    colAuthor = i;
                }
                else if (h == "来源")
                {
                    colSrc = i;
                }
                else if (h == "包内路径")
                {
                    colInner = i;
                }
                else if (h == "大小")
                {
                    colSize = i;
                }
            }
            if (colDest < 0 || colKind < 0)
            {
                if (log != null)
                {
                    log("索引表头缺少「现路径」或「类型」列：" + indexTsv);
                }
                return plan;
            }
            int maxCol = head.Length - 1;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length > maxCol)
                {
                    maxCol = c.Length - 1;
                }
                if (c.Length <= colDest)
                {
                    continue;
                }
                if (GetCol(c, colKind) != "卡")
                {
                    continue;
                }
                string rel = GetCol(c, colDest);
                if (rel.Length == 0 || rel.StartsWith("（", StringComparison.Ordinal))
                {
                    continue;
                }
                string norm = rel.Replace('\\', '/');
                string[] parts = norm.Split('/');
                if (parts.Length < 3)
                {
                    continue;
                }
                string typeDir = parts[0] + "/" + parts[1];
                string fileName = parts[parts.Length - 1];
                string dim = "";
                if (by == "work")
                {
                    dim = SafeDirName(GetCol(c, colWork));
                }
                else if (by == "author")
                {
                    dim = SafeDirName(GetCol(c, colAuthor));
                }
                else if (by == "chara")
                {
                    dim = SafeDirName(GetCol(c, colName));
                }
                string newRel = dim.Length > 0 ? (typeDir + "/" + dim + "/" + fileName) : (typeDir + "/" + fileName);
                string srcAbs = Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar));
                string dstAbs = Path.Combine(root, newRel.Replace('/', Path.DirectorySeparatorChar));
                long size = 0;
                string sizeText = GetCol(c, colSize);
                if (sizeText.Length > 0)
                {
                    long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                }
                var item = new CardPlanItem
                {
                    SrcPath = srcAbs,
                    Kind = "卡",
                    CardType = GetCol(c, colCtype),
                    CharaName = GetCol(c, colName),
                    Work = GetCol(c, colWork),
                    Author = GetCol(c, colAuthor),
                    SrcRoot = GetCol(c, colSrc),
                    SrcRel = GetCol(c, colInner).Length > 0 ? GetCol(c, colInner) : norm,
                    DestPath = dstAbs,
                    Size = size,
                    State = StatePending
                };
                if (string.Equals(srcAbs, dstAbs, StringComparison.OrdinalIgnoreCase))
                {
                    item.Note = "已在目标位置";
                    item.Move = false;
                    item.State = StateSkipped;
                }
                else if (!File.Exists(srcAbs))
                {
                    item.Note = "源已不在";
                    item.Move = false;
                    item.State = StateSkipped;
                }
                else if (File.Exists(dstAbs))
                {
                    item.Note = "目标已存在同名文件";
                    item.Move = false;
                    item.State = StateConflict;
                }
                else
                {
                    item.Move = true;
                }
                plan.Items.Add(item);
            }
            return plan;
        }

        private static string GetCol(string[] cols, int index)
        {
            if (index < 0 || index >= cols.Length)
            {
                return "";
            }
            return (cols[index] ?? "").Trim();
        }

        /// <summary>目录名清洗——Windows 非法字符换下划线、去尾部点与空格、保留设备名加前缀、超长截断；空值给「未标注」。</summary>
        public static string SafeDirName(string name)
        {
            string s = (name ?? "").Trim();
            if (s.Length == 0)
            {
                return "未标注";
            }
            char[] bad = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                bool hit = false;
                foreach (char b in bad)
                {
                    if (ch == b)
                    {
                        hit = true;
                        break;
                    }
                }
                sb.Append(hit ? '_' : ch);
            }
            string cleaned = sb.ToString().TrimEnd('.', ' ');
            if (cleaned.Length == 0)
            {
                return "未标注";
            }
            string upper = cleaned.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL"
                || upper == "COM1" || upper == "COM2" || upper == "COM3" || upper == "COM4"
                || upper == "LPT1" || upper == "LPT2" || upper == "LPT3")
            {
                cleaned = "_" + cleaned;
            }
            if (cleaned.Length > 60)
            {
                cleaned = cleaned.Substring(0, 60).TrimEnd('.', ' ');
            }
            return cleaned;
        }

        /// <summary>删掉某根下的空目录（自下而上多轮；根本身不动）；返回清掉的目录数。</summary>
        public static int PruneEmptyDirs(string root, Action<string> log = null)
        {
            int removed = 0;
            int blocked = 0;
            for (int pass = 0; pass < 8; pass++)
            {
                bool any = false;
                List<string> dirs = new List<string>();
                try
                {
                    dirs.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    if (log != null)
                    {
                        log("枚举目录失败（" + root + "）：" + ex.Message);
                    }
                    break;
                }
                dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
                foreach (string dir in dirs)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                            removed = removed + 1;
                            any = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        blocked = blocked + 1;
                        if (log != null)
                        {
                            log("空目录删不掉（可能被占用）：" + dir + " —— " + ex.Message);
                        }
                    }
                }
                if (!any)
                {
                    break;
                }
            }
            if (blocked > 0 && log != null)
            {
                log("空目录清理：删掉 " + removed.ToString(CultureInfo.InvariantCulture)
                    + " 个 · 删不掉 " + blocked.ToString(CultureInfo.InvariantCulture) + " 次（已逐条出声）");
            }
            return removed;
        }

        /// <summary>合并规则——同 Kind 同 Match 时外部覆盖内置（外部在后）。</summary>
        public static List<CardRule> MergeRules(List<CardRule> builtin, List<CardRule> external)
        {
            var merged = new List<CardRule>(builtin);
            foreach (CardRule e in external)
            {
                int hit = -1;
                for (int i = 0; i < merged.Count; i++)
                {
                    if (merged[i].Kind == e.Kind && string.Equals(merged[i].Match, e.Match, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = i;
                        break;
                    }
                }
                if (hit >= 0)
                {
                    merged[hit] = e;
                }
                else
                {
                    merged.Add(e);
                }
            }
            return merged;
        }

        /// <summary>
        /// 生成整理计划——遍历源目录，逐文件判类型 / 角色名 / 作品 / 作者，算出目标路径并检出重名。
        /// 卡片按卡类型归位；zipmod 归 mod；其余归其它（保持源内相对路径）。
        /// </summary>
        public static CardPlan BuildPlan(IEnumerable<string> srcDirs, string destRoot, List<CardRule> rules, Action<string> log)
        {
            var plan = new CardPlan
            {
                DestRoot = destRoot,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            var destFiles = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            var usedInPlan = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WorkCategory> categories = LoadCategories(destRoot, log);

            foreach (string srcDir in srcDirs)
            {
                string src = srcDir;
                if (!Directory.Exists(src))
                {
                    if (log != null)
                    {
                        log(File.Exists(src) ? ("跳过（这是文件不是目录）：" + src) : ("跳过不存在的源目录：" + src));
                    }
                    continue;
                }
                string rootName = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(rootName))
                {
                    rootName = src;
                }
                List<string> files = new List<string>();
                try
                {
                    files.AddRange(Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    if (log != null)
                    {
                        log("枚举失败（" + src + "）：" + ex.Message);
                    }
                    continue;
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string p in files)
                {
                    CardPlanItem item = DescribeFile(p, src, rootName, rules);
                    if (item == null)
                    {
                        continue;
                    }
                    PlaceItem(item, destRoot, destFiles, usedInPlan, categories);
                    plan.Items.Add(item);
                }
                if (log != null)
                {
                    log("已扫 " + rootName + "：" + files.Count.ToString(CultureInfo.InvariantCulture) + " 个文件");
                }
            }
            return plan;
        }

        /// <summary>判定单个文件的归类与元数据（不碰磁盘写入）。</summary>
        private static CardPlanItem DescribeFile(string path, string srcRoot, string rootName, List<CardRule> rules)
        {
            string rel = Path.GetRelativePath(srcRoot, path);
            string ext = Path.GetExtension(path);
            string lower = ext.ToLowerInvariant();
            var item = new CardPlanItem
            {
                SrcPath = path,
                SrcRoot = rootName,
                SrcRel = rel.Replace('\\', '/'),
                State = StatePending
            };
            try
            {
                item.Size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                item.Size = 0;
            }

            if (lower == ".png")
            {
                string cardType = null;
                string dataVersion = null;
                long imageEnd = 0;
                bool head = false;
                try
                {
                    head = CardReader.ReadHeadInfo(path, out cardType, out dataVersion, out imageEnd);
                }
                catch (IOException)
                {
                    head = false;
                }
                if (!head || string.IsNullOrEmpty(cardType))
                {
                    item.Kind = KindOther;
                    item.Note = "非卡 PNG";
                }
                else
                {
                    item.CardType = cardType;
                    if (cardType.Contains("Clothes"))
                    {
                        item.Kind = KindClothes;
                    }
                    else if (cardType == CardReader.SceneCardType)
                    {
                        item.Kind = KindScene;
                    }
                    else if (cardType.Contains("Chara"))
                    {
                        item.Kind = KindChara;
                    }
                    else
                    {
                        item.Kind = KindOther;
                        item.Note = "未知卡类型";
                    }
                    if (item.Kind == KindChara)
                    {
                        try
                        {
                            item.CharaName = CardName.ReadCharacter(path);
                        }
                        catch (IOException)
                        {
                            item.CharaName = null;
                        }
                    }
                    else if (item.Kind == KindClothes)
                    {
                        item.CharaName = ReadClothesName(path);
                    }
                }
            }
            else if (lower == ".zipmod")
            {
                item.Kind = KindMod;
            }
            else
            {
                item.Kind = KindOther;
            }

            item.Work = ClassifyWork(item, rules);
            item.Author = ResolveAuthor(item, rules);
            item.SrcRoot = SourceOf(item, rootName);
            return item;
        }

        /// <summary>来源键——源根名 + 相对路径首段（如 recode/genshin_impact），根下直放文件只有源根名。</summary>
        private static string SourceOf(CardPlanItem item, string rootName)
        {
            string rel = item.SrcRel ?? "";
            int slash = rel.IndexOf('/');
            if (slash > 0)
            {
                return rootName + "/" + rel.Substring(0, slash);
            }
            return rootName;
        }

        /// <summary>服装卡的角色名（数据区头段第三段），读不到返回空。</summary>
        private static string ReadClothesName(string path)
        {
            try
            {
                CardInfo info = CardReader.Parse(path);
                return info.CharaName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 作品判定——先看源目录规则（目录名 = 强证据），再看关键词（角色名 + 相对路径）。
        /// 两条都不命中给「待确认」——筛选键，不猜死。
        /// </summary>
        public static string ClassifyWork(CardPlanItem item, List<CardRule> rules)
        {
            string firstDir = item.SrcRel;
            int slash = firstDir.IndexOf('/');
            if (slash > 0)
            {
                firstDir = firstDir.Substring(0, slash);
            }
            else
            {
                firstDir = "";
            }
            foreach (CardRule r in rules)
            {
                if (r.Kind != CardRuleKind.Dir)
                {
                    continue;
                }
                if (string.Equals(r.Match, firstDir, StringComparison.OrdinalIgnoreCase))
                {
                    return r.Value;
                }
            }
            string chara = (item.CharaName ?? "").Trim();
            if (chara.Length > 0)
            {
                foreach (CardRule r in rules)
                {
                    if (r.Kind != CardRuleKind.Chara)
                    {
                        continue;
                    }
                    if (string.Equals(r.Match, chara, StringComparison.OrdinalIgnoreCase))
                    {
                        return r.Value;
                    }
                }
                // 归一化（去空白 / 全角空格 / 大小写）后相等——卡名与规则只差空白形态时命中
                string charaKey = NormalizeMatchKey(chara);
                foreach (CardRule r in rules)
                {
                    if (r.Kind != CardRuleKind.Chara)
                    {
                        continue;
                    }
                    if (NormalizeMatchKey(r.Match) == charaKey)
                    {
                        return r.Value;
                    }
                }
            }
            string text = chara + " " + item.SrcRel;
            string textKey = NormalizeMatchKey(text);
            foreach (CardRule r in rules)
            {
                if (r.Kind != CardRuleKind.Keyword)
                {
                    continue;
                }
                if (text.IndexOf(r.Match, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return r.Value;
                }
                if (textKey.IndexOf(NormalizeMatchKey(r.Match), StringComparison.Ordinal) >= 0)
                {
                    return r.Value;
                }
            }
            return WorkPending;
        }

        /// <summary>匹配归一化——去所有空白（含全角空格）并小写；卡名与规则只差空白形态时视为同一名。</summary>
        private static string NormalizeMatchKey(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>作者判定——先看源目录规则，再看文件名 [作者] 前缀。</summary>
        public static string ResolveAuthor(CardPlanItem item, List<CardRule> rules)
        {
            string firstDir = item.SrcRel;
            int slash = firstDir.IndexOf('/');
            if (slash > 0)
            {
                firstDir = firstDir.Substring(0, slash);
            }
            else
            {
                firstDir = "";
            }
            foreach (CardRule r in rules)
            {
                if (r.Kind != CardRuleKind.Author)
                {
                    continue;
                }
                if (string.Equals(r.Match, firstDir, StringComparison.OrdinalIgnoreCase))
                {
                    return r.Value;
                }
            }
            string file = Path.GetFileName(item.SrcPath);
            int lb = file.IndexOf('[');
            int rb = file.IndexOf(']');
            if (lb == 0 && rb > 1)
            {
                return file.Substring(1, rb - 1);
            }
            return "";
        }

        /// <summary>卡片类归类（人物卡 / 服装卡 / 场景卡）——目标必须带作品子目录的三类。</summary>
        public static bool IsCardKind(string kind)
        {
            return kind == KindChara || kind == KindClothes || kind == KindScene;
        }

        /// <summary>目标子目录（卡类型 + 作品 → 目录）——人物卡按大类 / 子类分层（01-卡片/人物卡/大类/子类/作品；其它类平铺），mod / 其它按固定段；换领域只改这里。</summary>
        /// <summary>读大类映射（kkc 根/tools/work-category.tsv）——作品 → 大类 / 子类；kkc 根 = 整理根的父目录。文件缺失返回空表并出声。</summary>
        public static Dictionary<string, WorkCategory> LoadCategories(string destRoot, Action<string> log)
        {
            var map = new Dictionary<string, WorkCategory>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(destRoot))
            {
                return map;
            }
            string full = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string kkcRoot = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(kkcRoot))
            {
                return map;
            }
            string path = Path.Combine(kkcRoot, "tools", "work-category.tsv");
            if (!File.Exists(path))
            {
                if (log != null)
                {
                    log("大类映射不存在，人物卡将回落 其它/：" + path);
                }
                return map;
            }
            int lineNo = 0;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                lineNo = lineNo + 1;
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length < 3 || c[0] == "作品")
                {
                    continue;
                }
                string work = c[0].Trim();
                string cat = c[1].Trim();
                string sub = c[2].Trim();
                if (work.Length == 0 || cat.Length == 0)
                {
                    if (log != null)
                    {
                        log("大类映射第 " + lineNo.ToString(CultureInfo.InvariantCulture) + " 行缺作品或大类，已跳过");
                    }
                    continue;
                }
                map[work] = new WorkCategory { Cat = cat, Sub = sub };
            }
            if (log != null)
            {
                log("大类映射：" + map.Count.ToString(CultureInfo.InvariantCulture) + " 个作品（" + path + "）");
            }
            return map;
        }

        /// <summary>目标子目录（卡类型 + 作品 → 目录）——人物卡按大类 / 子类分层，mod / 其它按固定段；换领域只改这里。</summary>
        public static string TargetDirOf(string kind, string work, string cardType, Dictionary<string, WorkCategory> categories)
        {
            if (cardType == EroMakeCardType)
            {
                return EroMakeFolder;
            }
            if (kind == KindChara)
            {
                if (work == WorkPending)
                {
                    return Path.Combine("01-卡片", "人物卡", WorkPending);
                }
                WorkCategory wc;
                if (categories != null && !string.IsNullOrEmpty(work) && categories.TryGetValue(work, out wc))
                {
                    if (string.IsNullOrEmpty(wc.Sub))
                    {
                        return Path.Combine("01-卡片", "人物卡", SafeDirName(wc.Cat), SafeDirName(work));
                    }
                    return Path.Combine("01-卡片", "人物卡", SafeDirName(wc.Cat), SafeDirName(wc.Sub), SafeDirName(work));
                }
                return Path.Combine("01-卡片", "人物卡", "其它", SafeDirName(work));
            }
            if (kind == KindClothes)
            {
                return Path.Combine("01-卡片", "服装卡", SafeDirName(work));
            }
            if (kind == KindScene)
            {
                return Path.Combine("01-卡片", "场景卡", SafeDirName(work));
            }
            if (kind == KindMod)
            {
                return Path.Combine("02-mod", "散件");
            }
            return Path.Combine("05-其它", "散件");
        }

        /// <summary>布局校验——卡片条目的目标必须落在 &lt;destRoot&gt;/01-卡片/&lt;卡类型&gt;/&lt;作品&gt;/ 下（不得落扁平根）；返回不合格条数（逐条出声）。</summary>
        public static int ValidateCardLayout(string destRoot, CardPlan plan, Action<string> log)
        {
            int bad = 0;
            string rootNorm = string.IsNullOrEmpty(destRoot) ? "" : Path.GetFullPath(destRoot).Replace('\\', '/').TrimEnd('/');
            foreach (CardPlanItem it in plan.Items)
            {
                if (!IsCardKind(it.Kind))
                {
                    continue;
                }
                string destNorm = (it.DestPath ?? "").Replace('\\', '/');
                string rel = destNorm;
                if (rootNorm.Length > 0)
                {
                    if (!destNorm.StartsWith(rootNorm + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        bad = bad + 1;
                        if (log != null)
                        {
                            log("布局不合格（" + it.Kind + "）：" + (it.DestPath ?? "") + "——目标不在整理根 " + rootNorm + " 下");
                        }
                        continue;
                    }
                    rel = destNorm.Substring(rootNorm.Length + 1);
                }
                string[] parts = rel.Split('/');
                // 人物卡：01-卡片/人物卡/[大类/子类|其它]/作品/文件名；其它卡类型：01-卡片/卡类型/作品/文件名；EroMakeChara 单列
                bool ok;
                if (it.CardType == EroMakeCardType)
                {
                    ok = parts.Length >= 2
                        && parts[parts.Length - 2] == EroMakeFolder
                        && parts[parts.Length - 1].Length > 0;
                }
                else if (it.Kind == KindChara)
                {
                    ok = parts.Length >= 4
                        && parts[0] == "01-卡片"
                        && parts[1] == "人物卡"
                        && parts[parts.Length - 2].Trim().Length > 0
                        && parts[parts.Length - 1].Length > 0;
                }
                else
                {
                    ok = parts.Length >= 4
                        && parts[parts.Length - 4] == "01-卡片"
                        && parts[parts.Length - 3] == it.Kind
                        && parts[parts.Length - 2].Trim().Length > 0
                        && parts[parts.Length - 1].Length > 0;
                }
                if (!ok)
                {
                    bad = bad + 1;
                    if (log != null)
                    {
                        log("布局不合格（" + it.Kind + "）：" + (it.DestPath ?? "") + "——人物卡须为 01-卡片/人物卡/大类/子类/作品/文件名，其它卡须为 01-卡片/卡类型/作品/文件名");
                    }
                }
            }
            return bad;
        }

        /// <summary>算目标路径并检出重名（目标已存在 / 计划内撞车）。</summary>
        private static void PlaceItem(CardPlanItem item, string destRoot, Dictionary<string, Dictionary<string, long>> destFiles, Dictionary<string, HashSet<string>> usedInPlan, Dictionary<string, WorkCategory> categories)
        {
            string sub = TargetDirOf(item.Kind, item.Work, item.CardType, categories);
            string dir = Path.Combine(destRoot, sub);
            string fileName = Path.GetFileName(item.SrcPath);
            if (item.Kind == KindOther)
            {
                // 其它文件保持源内相对路径（插件包 / 说明文档的分层不拆）
                string relDir = Path.GetDirectoryName(item.SrcRel.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(relDir))
                {
                    dir = Path.Combine(dir, relDir);
                }
            }

            Dictionary<string, long> existing;
            if (!destFiles.TryGetValue(dir, out existing))
            {
                existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            existing[Path.GetFileName(f)] = new FileInfo(f).Length;
                        }
                        catch (IOException)
                        {
                            existing[Path.GetFileName(f)] = -1;
                        }
                    }
                }
                destFiles[dir] = existing;
            }
            HashSet<string> used;
            if (!usedInPlan.TryGetValue(dir, out used))
            {
                used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedInPlan[dir] = used;
            }

            long existSize;
            if (existing.TryGetValue(fileName, out existSize))
            {
                if (existSize == item.Size)
                {
                    item.DestPath = Path.Combine(dir, fileName);
                    item.Note = "跳过：目标已有同名同尺寸";
                    item.Move = false;
                    item.State = StateSkipped;
                    return;
                }
                fileName = UniqueName(fileName, existing, used);
                item.Note = "改名：目标同名不同尺寸";
            }
            if (used.Contains(fileName))
            {
                fileName = UniqueName(fileName, existing, used);
                item.Note = "改名：计划内重名";
            }
            if (fileName.Length > 240)
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                fileName = stem.Substring(0, 200) + ext;
                item.Note = "改名：超长截断";
            }
            used.Add(fileName);
            item.DestPath = Path.Combine(dir, fileName);
            item.Move = true;
        }

        /// <summary>生成不冲突的文件名——同名时依次试 [dup2] [dup3]……（保留扩展名）。</summary>
        private static string UniqueName(string fileName, Dictionary<string, long> existing, HashSet<string> used)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int n = 2; n < 1000; n++)
            {
                string cand = stem + "[dup" + n.ToString(CultureInfo.InvariantCulture) + "]" + ext;
                if (!existing.ContainsKey(cand) && !used.Contains(cand))
                {
                    return cand;
                }
            }
            return stem + "[dup" + Guid.NewGuid().ToString("N").Substring(0, 6) + "]" + ext;
        }

        /// <summary>写计划 TSV（UTF-8 BOM——与 index.tsv 同编码）。</summary>
        public static void WritePlan(string path, CardPlan plan)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var sb = new StringBuilder();
            sb.AppendLine("源路径\t类型\t卡类型\t角色名\t作品\t作者\t来源\t相对路径\t目标路径\t大小\t备注\t移动\t状态");
            foreach (CardPlanItem it in plan.Items)
            {
                sb.AppendLine(string.Join("\t", new string[]
                {
                    Clean(it.SrcPath),
                    Clean(it.Kind),
                    Clean(it.CardType),
                    Clean(it.CharaName),
                    Clean(it.Work),
                    Clean(it.Author),
                    Clean(it.SrcRoot),
                    Clean(it.SrcRel),
                    Clean(it.DestPath),
                    it.Size.ToString(CultureInfo.InvariantCulture),
                    Clean(it.Note),
                    it.Move ? "Y" : "N",
                    Clean(it.State)
                }));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>回读计划 TSV（执行 / 核对用）。</summary>
        public static CardPlan ReadPlan(string path)
        {
            var plan = new CardPlan();
            if (!File.Exists(path))
            {
                return plan;
            }
            bool head = false;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (!head)
                {
                    head = true;
                    continue;
                }
                if (line.Length == 0)
                {
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length < 13)
                {
                    continue;
                }
                long size = 0;
                long.TryParse(c[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                plan.Items.Add(new CardPlanItem
                {
                    SrcPath = c[0],
                    Kind = c[1],
                    CardType = c[2],
                    CharaName = c[3],
                    Work = c[4],
                    Author = c[5],
                    SrcRoot = c[6],
                    SrcRel = c[7],
                    DestPath = c[8],
                    Size = size,
                    Note = c[10],
                    Move = c[11] == "Y",
                    State = c[12]
                });
            }
            return plan;
        }

        /// <summary>执行计划——只搬「待搬」条目；逐条落状态，中断安全。</summary>
        public static void Execute(CardPlan plan, Action<string> log)
        {
            int done = 0;
            foreach (CardPlanItem it in plan.Items)
            {
                if (!it.Move || it.State != StatePending)
                {
                    continue;
                }
                try
                {
                    if (!File.Exists(it.SrcPath))
                    {
                        it.State = StateSkipped;
                        it.Note = AppendNote(it.Note, "源已不在");
                        continue;
                    }
                    if (File.Exists(it.DestPath))
                    {
                        it.State = StateConflict;
                        it.Note = AppendNote(it.Note, "目标已存在同名文件");
                        continue;
                    }
                    string dir = Path.GetDirectoryName(it.DestPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.Move(it.SrcPath, it.DestPath);
                    it.State = StateMoved;
                    done = done + 1;
                }
                catch (Exception ex)
                {
                    it.State = StateFailed;
                    it.Note = AppendNote(it.Note, ex.Message);
                    if (log != null)
                    {
                        log("失败：" + it.SrcPath + " —— " + ex.Message);
                    }
                }
            }
            if (log != null)
            {
                log("已搬 " + done.ToString(CultureInfo.InvariantCulture) + " 个文件");
            }
        }

        private static string AppendNote(string note, string add)
        {
            return string.IsNullOrEmpty(note) ? add : (note + "；" + add);
        }

        /// <summary>清洗一个 TSV 字段（去掉制表符与换行，避免破坏列）。</summary>
        private static string Clean(string v)
        {
            if (string.IsNullOrEmpty(v))
            {
                return "";
            }
            return v.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
        /// <summary>导出整理根的卡片引用表（现路径 → 引用 guid 列表）——索引生成的前置数据，一次性；不写库。</summary>
        public static int ExportRefs(string root, string outPath, Action<string> log)
        {
            string rootFull = Path.GetFullPath(root);
            int n = 0;
            int withRefs = 0;
            long rows = 0;
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            using (StreamWriter w = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                w.WriteLine("现路径\t引用mod\t引用行数");
                foreach (string p in Directory.EnumerateFiles(rootFull, "*.png", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(rootFull, p).Replace('\\', '/');
                    n = n + 1;
                    try
                    {
                        CardInfo info = CardReader.Parse(p);
                        IReadOnlyList<string> ids = info.DistinctModIds();
                        if (ids.Count > 0)
                        {
                            withRefs = withRefs + 1;
                        }
                        rows = rows + info.ModRefs.Count;
                        w.WriteLine(rel + "\t" + string.Join(",", ids) + "\t" + info.ModRefs.Count.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (IOException)
                    {
                        w.WriteLine(rel + "\t\t0");
                    }
                    if (log != null && n % 500 == 0)
                    {
                        log("  已读 " + n.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            if (log != null)
            {
                log("引用表：" + n.ToString(CultureInfo.InvariantCulture) + " 张 · 有引用 " + withRefs.ToString(CultureInfo.InvariantCulture) + " · 引用行 " + rows.ToString(CultureInfo.InvariantCulture));
            }
            return n;
        }
    }
}
