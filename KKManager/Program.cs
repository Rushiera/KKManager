using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KKManager.Core;
using KKManager.Data;

namespace KKManager
{
    /// <summary>KKManager 命令行入口——解析自检 / 库扫描 / 配置 / 服务。</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // [段1] 注册代码页编码（GBK / 936 等）——Koikatsu 的 csv 有本地编码形态，不注册时 GetEncoding 直接抛
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length < 1)
            {
#if RELEASEPACK
                // [段1b] 发布版（-p:ReleasePack=true）：双击即用——无参等价于 serve --open
                // 复用下方既有参数解析链，默认库路径 / 端口单点不重复
                args = new string[] { "serve", "--open" };
#else
                Usage();
                return 2;
#endif
            }

            var rest = new List<string>(args);
            string cmd = rest[0];
            rest.RemoveAt(0);

            string db = Take(rest, "--db") ?? Path.Combine(AppContext.BaseDirectory, "data", "kkmanager.db");
            int thumb = int.Parse(Take(rest, "--thumb") ?? "256");
            int quality = int.Parse(Take(rest, "--quality") ?? "82");
            int port = int.Parse(Take(rest, "--port") ?? "8539");
            bool open = rest.Remove("--open");
            bool browseDebug = rest.Remove("--browse-debug");
            bool force = rest.Remove("--force");

            switch (cmd)
            {
                case "modinfo":
                    return ModInfoCommand(rest);
                case "cardinfo":
                    return CardInfoCommand(rest);
                case "card-structure":
                    return CardStructureCommand(rest);
                case "card-detail":
                    return CardDetailCommand(rest);
                case "card-timeline":
                    return CardTimelineCommand(rest);
                case "card-scene":
                    return CardSceneCommand(rest);
                case "card-coord":
                    return CardCoordinateCommand(rest);
                case "card-params":
                    return CardParamsCommand(rest);
                case "card-edit":
                    return CardEditCommand(rest);
                case "thumb":
                    return ThumbCommand(rest, thumb, quality);
                case "scan-mods":
                    return ScanModsCommand(db, rest, force);
                case "scan-cards":
                    return ScanCardsCommand(db, rest, force, thumb, quality);
                case "scan-main":
                    return ScanScopeCommand(db, force, ScanScope.Preset, thumb, quality);
                case "scan-extra":
                    return ScanScopeCommand(db, force, ScanScope.Extra, thumb, quality);
                case "stats":
                    return StatsCommand(db);
                case "authors":
                    return AuthorsCommand(db);
                case "cards":
                    return CardsCommand(db, rest);
                case "todos":
                    return TodosCommand(db);
                case "dup":
                    return DupCommand(db);
                case "composition":
                    return CompositionCommand(db, rest, force);
                case "u3d":
                    return U3dCommand(rest);
                case "u3d-db":
                    return U3dDbCommand(db, rest, force);
                case "keep-version":
                    return OldOpCommand(db, rest, "keep-version");
                case "mark-old":
                    return OldOpCommand(db, rest, "mark-old");
                case "swap":
                    return OldOpCommand(db, rest, "swap");
                case "promote":
                    return OldOpCommand(db, rest, "promote");
                case "sort":
                    return SortCommand(db, rest);
                case "roots-list":
                    return RootsListCommand(db);
                case "roots-add":
                    return RootsAddCommand(db, rest);
                case "roots-clear":
                    return RootsClearCommand(db, rest);
                case "set-game-root":
                    return SetGameRootCommand(db, rest);
                case "serve":
                    return KKManager.Web.WebApp.Run(db, port, open, browseDebug);
                case "help":
#if RELEASEPACK
                    Console.WriteLine("发布版（ReleasePack 编译）——双击本 exe 即起面板。");
#endif
                    Usage();
                    return 0;
                default:
                    Console.Error.WriteLine("未知命令: " + cmd);
                    Usage();
                    return 2;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("KK Manager by Rushiera —— Koikatsu 卡片 / mod 关联管理器");
            Console.WriteLine();
            Console.WriteLine("  modinfo   <zipmod>                    解析单个 zipmod 的 manifest");
            Console.WriteLine("  cardinfo  <card.png>                  解析单张卡片的声明区");
            Console.WriteLine("  card-structure <card.png>             卡片文件结构（PNG 块表 + 图片区 / 数据区划分）");
            Console.WriteLine("  card-timeline <card.png>              场景卡（sd）的 timeline 长度（Timeline 插件条目 duration / timeScale——与面板同一实现）");
            Console.WriteLine("  card-scene <card.png>                 场景卡（sd）深度分析（插件数据条目 timeline / kkpe / vnge_* + 内嵌角色卡数据份数 + timeline 深度）");
            Console.WriteLine("  card-coord <card.png> [--all]         卡片服装 / 饰品（Coordinate 块七套槽位：服装 9 槽 + 饰品 20 槽 + 化妆）");
            Console.WriteLine("  thumb     <card.png> <out.jpg>        导出缩略图（离线核对）");
            Console.WriteLine("  scan-mods [目录...]                   扫描 mod 库（无目录则用已配置的库根）");
            Console.WriteLine("  scan-cards [目录...]                  扫描卡片库（无目录则用已配置的库根）");
            Console.WriteLine("  scan-main                             扫描主要库（预置条目——mod 主库 + 缓存库槽位 + 卡片 female / coordinate / Studio）");
            Console.WriteLine("  scan-extra                            扫描追加库（使用者添加的库根——mod 冷冻库 + 卡片附加库；离线库跳过）");
            Console.WriteLine("  stats                                 库统计：四色（绿/黄/红/黑）+ 就绪卡数");
            Console.WriteLine("  authors                               作者清单（按发布的 mod 数量倒序——与面板「按作者筛选」同一数据源）");
            Console.WriteLine("  cards [--order mtime|size|file|chara|timeline] [--desc] [--q 关键词] [--limit N]  列出卡片（与面板「卡片排序」同一实现；timeline 只有场景卡有，需先扫描主要库读到）");
            Console.WriteLine("  dup                                   列出重复副本组（同 guid 多份文件，含版本 / 作者 / 创建时间 / MD5 + 引用卡片数与前三个卡片名——MD5 只算冲突文件，算过就忽略）");
            Console.WriteLine("  composition <guid>                    查看某 mod 的组成（按需建档：容器条目清单 + 目录聚合 + 文本条目内容；--force 强制重读容器）");
            Console.WriteLine("  u3d       <zipmod> [--tex <目录>]     列出容器内 unity3d 包中的贴图（--tex 导出缩小版 PNG · --max N 限张数）");
            Console.WriteLine("  u3d-db    <guid> <条目路径>           按库副本解析 unity3d 条目并落档（与面板端点共用同一实现；--force 强制重读）");
            Console.WriteLine("  keep-version <guid> <文件路径>        指定这个版本：该份成为主库当前版本，同 guid 其余非旧版副本判为旧版并移入缓存库");
            Console.WriteLine("  mark-old <guid> <文件路径>            把该副本判为旧版：加 .old 段 + 移到缓存库 + 留新旧版本记录");
            Console.WriteLine("  swap     <guid> <文件路径>            把这份旧版与主库当前版本完全互换（位置 + 名字）");
            Console.WriteLine("  promote  <guid> <文件路径>            把这份旧版搬入主库并正名（去掉 .old 段）");
            Console.WriteLine("  sort plan [目录...]                   生成「按作者整理」计划（无目录 = 全部可整理的 mod 库根；平铺 + 作者文件夹 + 冲突检测）");
            Console.WriteLine("  sort show                             读整理计划表（做一次快照核对——已在快照位置的行标 √）");
            Console.WriteLine("  sort exec --yes                       执行整理计划（逐条搬运 + 完成后清空目录；--yes 表示已关游戏）");
            Console.WriteLine("  sort conflict <seq>                   列出这一处整理冲突的候选副本（版本 / 作者 / MD5 实时读——与面板小窗同一实现）");
            Console.WriteLine("  sort keep <seq>                       保留这一份：其余同 guid 非旧版副本判旧版进缓存库，计划条目跟着改指新位置（与面板同一实现）");
            Console.WriteLine("  roots-list                            列出已配置的库根");
            Console.WriteLine("  roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]");
            Console.WriteLine("  roots-clear <mods|cards|all>          清空库根配置");
            Console.WriteLine("  set-game-root <路径>                  设置游戏根（预置主库条目路径随它重派生）");
            Console.WriteLine("  serve [--port 8539] [--open] [--browse-debug]  启动本地面板（--browse-debug 打印浏览框调试日志）");
            Console.WriteLine();
            Console.WriteLine("  通用: --db <路径>（默认 exe 目录下 data/kkmanager.db）· --force 全量重扫");
            Console.WriteLine("  级别: 1=主库（游戏读取）· 2=缓存库（可一键搬入主库）· 3=冷冻库（只读）");
            Console.WriteLine("  规则: 预置主库（mods / female / coordinate / Studio）锁定——不可改不可删 · 缓存库槽位不可删（路径与勾选归使用者）· 新增 mod 库根固定冷冻库、卡片库根固定附加库");
        }

        private static string Take(List<string> rest, string name)
        {
            int i = rest.IndexOf(name);
            if (i < 0 || i + 1 >= rest.Count)
            {
                return null;
            }
            string value = rest[i + 1];
            rest.RemoveRange(i, 2);
            return value;
        }
        /// <summary>取出全部同名参数值（可重复出现的选项）并移出列表——如 --denial 可给多项。</summary>
        private static List<string> TakeAll(List<string> rest, string name)
        {
            List<string> list = new List<string>();
            while (true)
            {
                string v = Take(rest, name);
                if (v == null)
                {
                    break;
                }
                list.Add(v);
            }
            return list;
        }
        /// <summary>「是否接受」键名在 DenialKeys 里的下标（未知返回 -1）。</summary>
        private static int DenialIndexOf(string key)
        {
            for (int i = 0; i < CardEdit.DenialKeys.Length; i = i + 1)
            {
                if (key == CardEdit.DenialKeys[i])
                {
                    return i;
                }
            }
            return -1;
        }
        /// <summary>可编辑字段的一行摘要（编辑前后打印用）。</summary>
        private static string CardFieldSummary(CardParamInfo info)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("姓「" + info.LastName + "」 名「" + info.FirstName + "」 爱称「" + info.NickName + "」");
            sb.Append(" 性格 " + info.Personality);
            string wpn = WeakPoint.NameOf(info.WeakPoint);
            sb.Append(" 敏感带 " + info.WeakPoint + " " + (wpn == null ? "(未收录)" : wpn));
            sb.Append(" 接受[");
            for (int i = 0; i < CardEdit.DenialKeys.Length; i = i + 1)
            {
                if (i > 0)
                {
                    sb.Append(" ");
                }
                sb.Append(CardEdit.DenialNames[i] + "=");
                if (info.Denial[i])
                {
                    sb.Append("是");
                }
                else
                {
                    sb.Append("否");
                }
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>u3d 命令——列出 zipmod 容器内 unity3d 包中的贴图（可选导出缩小版 PNG）。</summary>
        private static int U3dCommand(List<string> rest)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("用法: u3d <zipmod> [--tex <目录>] [--max N]");
                return 2;
            }
            string zipPath = rest[0];
            string outDir = Take(rest, "--tex");
            int maxCount = int.Parse(Take(rest, "--max") ?? "0");
            if (!File.Exists(zipPath))
            {
                Console.Error.WriteLine("文件不存在: " + zipPath);
                return 2;
            }
            string listErr;
            List<ZipEntryInfo> entries = ZipModReader.ListEntries(zipPath, out listErr);
            if (entries == null)
            {
                Console.Error.WriteLine("读取容器失败: " + listErr);
                return 2;
            }

            // [段1] 逐 unity3d 条目解析
            int found = 0;
            for (int i = 0; i < entries.Count; i = i + 1)
            {
                string entryPath = entries[i].Path;
                if (!entryPath.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                found = found + 1;
                Console.WriteLine("== " + entryPath + " · " + entries[i].Size.ToString("N0") + " 字节");
                string readErr;
                byte[] bytes = ZipModReader.ReadEntryBytes(zipPath, entryPath, 536870912, out readErr);
                if (bytes == null)
                {
                    Console.WriteLine("  读取失败：" + readErr);
                    continue;
                }
                string parseErr;
                List<Unity3dTexture> texs = Unity3dReader.ListTextures(bytes, out parseErr);
                if (texs == null)
                {
                    Console.WriteLine("  解析失败：" + parseErr);
                    continue;
                }
                Console.WriteLine("  贴图 " + texs.Count.ToString() + " 张");
                for (int k = 0; k < texs.Count; k = k + 1)
                {
                    Unity3dTexture t = texs[k];
                    Console.WriteLine("   #" + t.PathId.ToString() + " " + t.Name + " · " + t.Width.ToString() + "×" + t.Height.ToString()
                        + " · 格式 " + t.Format.ToString() + " · " + t.DataLength.ToString("N0") + " 字节");
                    if (!string.IsNullOrEmpty(outDir) && (maxCount <= 0 || k < maxCount))
                    {
                        string pngErr;
                        byte[] png = Unity3dReader.RenderTexturePng(bytes, t.PathId, 512, out pngErr);
                        if (png == null)
                        {
                            Console.WriteLine("     导出失败：" + pngErr);
                        }
                        else
                        {
                            Directory.CreateDirectory(outDir);
                            string file = Path.Combine(outDir, "u3d_" + t.PathId.ToString() + ".png");
                            File.WriteAllBytes(file, png);
                            Console.WriteLine("     → " + file);
                        }
                    }
                }
            }
            if (found == 0)
            {
                Console.WriteLine("容器内没有 .unity3d 条目");
            }
            return 0;
        }

        /// <summary>按库副本解析某 mod 的 unity3d 条目并落档（与面板端点共用同一实现）——数据面验证用。</summary>
        private static int U3dDbCommand(string db, List<string> rest, bool force)
        {
            RequireArgs(rest, "u3d-db <guid> <条目路径> [--force]", 2);
            string guid = rest[0];
            string entryPath = rest[1];
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                string error;
                ModU3d rec = hub.AnalyzeU3d(cfg, guid, entryPath, force, out error);
                if (rec == null)
                {
                    Console.Error.WriteLine("失败: " + error);
                    return 3;
                }
                Console.WriteLine("guid       : " + rec.Guid);
                Console.WriteLine("条目       : " + rec.EntryPath);
                Console.WriteLine("容器文件   : " + rec.FilePath);
                Console.WriteLine("大小       : " + rec.Size.ToString("N0") + " 字节");
                Console.WriteLine("贴图张数   : " + rec.TextureCount.ToString("N0"));
                Console.WriteLine("建档于     : " + rec.ParsedAt + (rec.Cached ? "（命中已有档案）" : "（本次新建）"));
                Console.WriteLine();
                Console.WriteLine("贴图清单：");
                foreach (Unity3dTexture t in StoreHub.ParseU3dTextures(rec.Textures))
                {
                    Console.WriteLine("  #" + t.PathId.ToString() + "  " + t.Width.ToString() + "×" + t.Height.ToString()
                        + "  格式 " + t.Format.ToString() + "  " + t.DataLength.ToString("N0") + " 字节");
                }
            }
            return 0;
        }

        private static void RequireArgs(List<string> rest, string usage, int min)
        {
            if (rest.Count < min)
            {
                Console.Error.WriteLine("用法: " + usage);
                Environment.Exit(2);
            }
        }

        private static int ModInfoCommand(List<string> rest)
        {
            RequireArgs(rest, "modinfo <zipmod>", 1);
            ModInfo m = ZipModReader.Parse(rest[0]);
            Console.WriteLine("文件       : " + m.FileName);
            Console.WriteLine("大小       : " + m.Size.ToString("N0") + " 字节");
            Console.WriteLine("guid       : " + (m.Guid ?? "<无>"));
            Console.WriteLine("name       : " + (m.Name ?? "<无>"));
            Console.WriteLine("version    : " + (m.Version ?? "<无>"));
            Console.WriteLine("author     : " + (m.Author ?? "<无>"));
            Console.WriteLine("website    : " + (m.Website ?? "<无>"));
            Console.WriteLine("schema-ver : " + (m.SchemaVer ?? "<无>"));
            Console.WriteLine("条目数     : " + m.EntryCount);
            Console.WriteLine("文件名=guid: " + (m.FileNameMatchesGuid ? "是" : "否"));
            if (!string.IsNullOrEmpty(m.Error))
            {
                Console.WriteLine("诊断       : " + m.Error);
                return 1;
            }
            return 0;
        }

        private static int CardInfoCommand(List<string> rest)
        {
            RequireArgs(rest, "cardinfo <card.png>", 1);
            CardInfo c = CardReader.Parse(rest[0]);
            Console.WriteLine("文件       : " + c.FileName);
            Console.WriteLine("大小       : " + c.Size.ToString("N0") + " 字节");
            Console.WriteLine("图片区结束 : " + c.ImageEnd.ToString("N0") + "（数据区 " + (c.Size - c.ImageEnd).ToString("N0") + " 字节）");
            Console.WriteLine("卡类型     : " + (c.CardType ?? "<无>"));
            Console.WriteLine("数据标记   : " + (c.DataVersion ?? "<无>"));
            Console.WriteLine("声明区数   : " + c.UarBlocks);
            Console.WriteLine("引用条目   : " + c.ModRefs.Count);
            IReadOnlyList<string> ids = c.DistinctModIds();
            Console.WriteLine("去重 mod   : " + ids.Count);
            foreach (string id in ids)
            {
                Console.WriteLine("  - " + id);
            }
            return 0;
        }

        /// <summary>卡片文件结构——PNG 块表 + 图片区 / 数据区划分（只读）。</summary>
        private static int CardStructureCommand(List<string> rest)
        {
            RequireArgs(rest, "card-structure <card.png>", 1);
            CardStructure st = CardDocument.Parse(rest[0]);
            Console.WriteLine("文件       : " + (st.FilePath == null ? rest[0] : st.FilePath));
            Console.WriteLine("大小       : " + st.Size.ToString("N0") + " 字节 · 改于 " + (st.Mtime == null ? "?" : st.Mtime));
            Console.WriteLine();
            Console.WriteLine("PNG 块表   : " + st.Chunks.Count + " 个");
            foreach (PngChunk ck in st.Chunks)
            {
                string crc = ck.CrcChecked ? (ck.CrcOk ? "OK" : "不符") : "未校验";
                Console.WriteLine("  @" + ck.Offset.ToString("N0").PadLeft(14) + "  " + ck.Type.PadRight(5)
                    + " len=" + ck.Length.ToString("N0").PadLeft(12) + "  CRC=" + crc);
            }
            Console.WriteLine();
            Console.WriteLine("图片区结束 : " + (st.ImageEnd < 0 ? "<未找到>" : st.ImageEnd.ToString("N0")));
            Console.WriteLine("数据区     : " + st.DataSize.ToString("N0") + " 字节（起点 " + (st.ImageEnd < 0 ? "?" : st.ImageEnd.ToString("N0")) + "）");
            Console.WriteLine("卡类型     : " + (st.CardType == null ? "<无>" : st.CardType));
            Console.WriteLine("数据标记   : " + (st.DataVersion == null ? "<无>" : st.DataVersion));
            Console.WriteLine("数据区首64 : " + (st.DataHeadHex == null ? "<无>" : st.DataHeadHex));
            foreach (string w in st.Warnings)
            {
                Console.WriteLine("提示       : " + w);
            }
            if (!string.IsNullOrEmpty(st.Error))
            {
                Console.WriteLine("诊断       : " + st.Error);
                return 1;
            }
            return 0;
        }
        /// <summary>场景卡 timeline 长度——Timeline 插件条目里的 duration / timeScale（只读；与面板同一实现）。</summary>
        private static int CardTimelineCommand(List<string> rest)
        {
            RequireArgs(rest, "card-timeline <card.png>", 1);
            CardStructure st = CardDocument.Parse(rest[0]);
            if (st.Error != null)
            {
                Console.WriteLine("诊断       : " + st.Error);
                return 3;
            }
            string path = st.FilePath == null ? rest[0] : st.FilePath;
            Console.WriteLine("文件       : " + path);
            Console.WriteLine("卡类型     : " + (st.CardType == null ? "<无>" : st.CardType)
                + " · 数据标记 " + (st.DataVersion == null ? "<无>" : st.DataVersion));
            if (st.CardType != CardReader.SceneCardType)
            {
                Console.WriteLine("timeline   : 不是场景卡——只有 Studio 场景卡带 timeline 数据");
                return 0;
            }
            TimelineInfo t = TimelineReader.Read(path, st.ImageEnd);
            Console.WriteLine("timeline   : " + TimelineReader.Describe(t));
            if (t.Error == null && t.HasEntry)
            {
                Console.WriteLine("  原时长   : " + TimelineReader.SecondsText(t.Duration) + " 秒（" + TimelineReader.FormatSeconds(t.Duration) + "）");
                Console.WriteLine("  timeScale: " + TimelineReader.SecondsText(t.TimeScale) + "（实际播放 " + TimelineReader.FormatSeconds(t.RealSeconds) + "）");
                Console.WriteLine("  关键帧   : " + t.Keyframes.ToString("N0") + " 个 · 插值组 " + t.Groups
                    + " · 最长关键帧 " + TimelineReader.FormatSeconds(t.MaxKeyframeTime)
                    + " · sceneInfo XML " + t.XmlLength.ToString("N0") + " 字节 · 命中阶段 " + (t.HitStage == null ? "<无>" : t.HitStage));
            }
            return 0;
        }
        /// <summary>场景卡（sd）深度分析——插件数据条目（timeline / kkpe / vnge_*）+ 内嵌角色卡数据份数 + timeline 深度（只读；与面板同一实现）。</summary>
        private static int CardSceneCommand(List<string> rest)
        {
            RequireArgs(rest, "card-scene <card.png>", 1);
            CardStructure st = CardDocument.Parse(rest[0]);
            if (st.Error != null)
            {
                Console.WriteLine("诊断       : " + st.Error);
                return 3;
            }
            string path = st.FilePath == null ? rest[0] : st.FilePath;
            Console.WriteLine("文件       : " + path);
            Console.WriteLine("卡类型     : " + (st.CardType == null ? "<无>" : st.CardType)
                + " · 数据标记 " + (st.DataVersion == null ? "<无>" : st.DataVersion));
            if (st.CardType != CardReader.SceneCardType)
            {
                Console.WriteLine("场景分析   : 不是场景卡——只有 Studio 场景卡（sd）带插件数据条目与内嵌角色数据");
                return 0;
            }
            SceneInfoResult s = SceneReader.Read(path, st.ImageEnd);
            if (s.Error != null)
            {
                Console.WriteLine("诊断       : " + s.Error);
                return 3;
            }
            Console.WriteLine("数据区     : " + st.DataSize.ToString("N0") + " 字节 · 已扫描 " + s.ScannedBytes.ToString("N0") + " 字节");
            Console.WriteLine("内嵌角色   : " + s.CharaDataCount + " 份（数据块目录命中）");
            CardDetailResult cd = CardDetail.Parse(path, st.ImageEnd);
            s.AttachCharaFaces(cd.Images);
            Console.WriteLine("内嵌角色首图: " + s.CharaFaces.Count + " 张（每份一张——该份角色卡的卡面图）");
            foreach (CardImageInfo face in s.CharaFaces)
            {
                Console.WriteLine("  #" + face.Index.ToString().PadLeft(3) + " @" + face.Offset.ToString("N0").PadLeft(14)
                    + "  " + face.Size.ToString("N0").PadLeft(12) + " 字节  " + face.Width + "×" + face.Height
                    + (face.IsFace ? "  [脸图]" : ""));
            }
            if (s.CharaData.Count > 0)
            {
                Console.WriteLine("内嵌角色明细: " + s.CharaData.Count + " 份（份头段 × 数据块目录）");
                foreach (SceneCharaData chd in s.CharaData)
                {
                    Console.Write("  第 " + chd.Index + " 份 · 数据版本 " + (chd.DataVersion == null ? "?" : chd.DataVersion)
                        + " · 卡面图 @" + chd.FaceOffset.ToString("N0") + "（" + chd.FaceSize.ToString("N0") + " 字节）"
                        + " · 块 " + chd.Blocks.Count + " 个：");
                    for (int bi = 0; bi < chd.Blocks.Count; bi = bi + 1)
                    {
                        Console.Write((bi == 0 ? "" : " · ") + chd.Blocks[bi].Name + " v" + chd.Blocks[bi].Version
                            + "（" + chd.Blocks[bi].Size.ToString("N0") + " 字节）");
                    }
                    Console.WriteLine();
                }
            }
            Console.WriteLine("插件条目   : " + s.Plugins.Count + " 条");
            foreach (ScenePluginItem it in s.Plugins)
            {
                Console.WriteLine("  @" + it.Offset.ToString("N0").PadLeft(14) + "  " + it.Key.PadRight(22)
                    + (string.IsNullOrEmpty(it.Version) ? "        " : ("v" + it.Version).PadRight(8)) + "  " + it.Shape.PadRight(18)
                    + (it.Bytes > 0 ? "  " + it.Bytes.ToString("N0").PadLeft(12) + " 字节" : "              ")
                    + (string.IsNullOrEmpty(it.Summary) ? "" : "  " + it.Summary));
            }
            if (s.KkpeItemCount >= 0)
            {
                Console.WriteLine("场景道具   : itemInfo " + s.KkpeItemCount.ToString("N0") + " 条 · 名字 " + s.PropNames.Count + " 种（kkpe 记录）");
                int shown = 0;
                foreach (SceneNameCount nc in s.PropNames)
                {
                    if (shown >= 30)
                    {
                        break;
                    }
                    Console.WriteLine("  ×" + nc.Count.ToString().PadLeft(4) + "  " + nc.Name);
                    shown = shown + 1;
                }
            }
            if (s.SssbObjects >= 0)
            {
                Console.WriteLine("场景快照   : 对象 " + s.SssbObjects + " 个（相机 " + s.SssbCameras + " 个——vnge_sssb）");
            }
            TimelineInfo t = TimelineReader.Read(path, st.ImageEnd);
            Console.WriteLine("timeline   : " + TimelineReader.Describe(t));
            if (t.Error == null && t.HasEntry && !t.IsEmpty)
            {
                Console.WriteLine("  timeline 深度: 关键帧 " + t.Keyframes.ToString("N0") + " · 插值组 " + t.Groups
                    + " · 最长关键帧 " + TimelineReader.FormatSeconds(t.MaxKeyframeTime));
                if (t.GroupNames.Count > 0)
                {
                    Console.WriteLine("  轨道组名 : " + string.Join(" · ", t.GroupNames));
                }
            }
            foreach (string w in s.Warnings)
            {
                Console.WriteLine("提示       : " + w);
            }
            return 0;
        }

        /// <summary>卡片服装 / 饰品——Coordinate 块的七套槽位（服装 9 槽 + 饰品 20 槽 + 化妆开关与化妆 id）。</summary>
        private static int CardCoordinateCommand(List<string> rest)
        {
            bool all = rest.Remove("--all");
            RequireArgs(rest, "card-coord <card.png> [--all]", 1);
            CardCoordinateResult r = CardCoordinate.Read(rest[0]);
            Console.WriteLine("文件       : " + (r.FilePath == null ? rest[0] : r.FilePath));
            if (r.Error != null)
            {
                Console.WriteLine("诊断       : " + r.Error);
                return 3;
            }
            Console.WriteLine("Coordinate : v" + (r.BlockVersion == null ? "?" : r.BlockVersion)
                + " · " + r.BlockSize.ToString("N0") + " 字节 · " + r.Outfits.Count + " 套"
                + (all ? "（全量列出）" : "（只列非空槽位 · --all 列全部）"));
            for (int i = 0; i < r.Outfits.Count; i = i + 1)
            {
                CardCoordinateOutfit o = r.Outfits[i];
                Console.WriteLine();
                Console.WriteLine("[" + o.Index + "] " + o.TypeName
                    + " — 服装 " + CardCoordinateResult.ClothesCount(o) + " 件 · 饰品 " + CardCoordinateResult.AccessoryCount(o) + " 件"
                    + " · 化妆 " + (o.EnableMakeup ? "开" : "关") + (o.MakeupText == null ? "" : "（" + o.MakeupText + "）"));
                for (int k = 0; k < o.Clothes.Count; k = k + 1)
                {
                    CardClothesPart p = o.Clothes[k];
                    if (!all && p.Id == 0)
                    {
                        continue;
                    }
                    Console.WriteLine("  服装 " + p.Slot + "： id=" + p.Id
                        + " · 袖型 " + p.SleevesType
                        + " · 徽章 " + p.EmblemeId + "/" + p.EmblemeId2
                        + " · 隐藏 " + (p.HideOptA ? "是" : "否") + "/" + (p.HideOptB ? "是" : "否"));
                    for (int c = 0; c < p.Colors.Count; c = c + 1)
                    {
                        Console.WriteLine("        " + p.Colors[c]);
                    }
                }
                for (int k = 0; k < o.Accessories.Count; k = k + 1)
                {
                    CardAccessoryPart p = o.Accessories[k];
                    if (!all && p.Id == 0)
                    {
                        continue;
                    }
                    Console.WriteLine("  饰品 #" + p.Index + " " + p.TypeName + "： id=" + p.Id
                        + " · 挂点 " + p.ParentName + (p.ParentKey == null ? "" : "（" + p.ParentKey + "）")
                        + " · 不晃 " + (p.NoShake ? "是" : "否")
                        + " · 隐藏类别 " + p.HideCategory);
                    for (int c = 0; c < p.Colors.Count; c = c + 1)
                    {
                        Console.WriteLine("        " + p.Colors[c]);
                    }
                }
                for (int w = 0; w < o.Warnings.Count; w = w + 1)
                {
                    Console.WriteLine("  提示： " + o.Warnings[w]);
                }
            }
            for (int w = 0; w < r.Warnings.Count; w = w + 1)
            {
                Console.WriteLine("提示       : " + r.Warnings[w]);
            }
            return 0;
        }

        /// <summary>卡片数据区内容——部件 / 声明区 / 数据块目录 / 内嵌贴图 / 插件块（只读）。</summary>
        private static int CardDetailCommand(List<string> rest)
        {
            RequireArgs(rest, "card-detail <card.png>", 1);
            CardStructure st = CardDocument.Parse(rest[0]);
            if (st.ImageEnd <= 0)
            {
                Console.WriteLine("图片区终点未知——无法解析数据区：" + (st.Error == null ? "<无>" : st.Error));
                return 3;
            }
            string path = st.FilePath == null ? rest[0] : st.FilePath;
            CardDetailResult d = CardDetail.Parse(path, st.ImageEnd);
            Console.WriteLine("文件       : " + path);
            Console.WriteLine("卡类型     : " + (st.CardType == null ? "<无>" : st.CardType)
                + " · 数据标记 " + (st.DataVersion == null ? "<无>" : st.DataVersion));
            Console.WriteLine("角色名     : " + (d.CharName == null ? "<无>" : d.CharName));
            if (d.FaceImageSize > 0)
            {
                Console.WriteLine("脸图       : " + d.FaceImageSize.ToString("N0") + " 字节 @" + d.FaceImageOffset.ToString("N0"));
            }
            Console.WriteLine("数据区     : " + st.DataSize.ToString("N0") + " 字节（起点 " + st.ImageEnd.ToString("N0") + "）");
            Console.WriteLine();
            Console.WriteLine("内嵌图片   : " + d.Images.Count + " 张 · 合计 " + d.ImageBytesTotal.ToString("N0") + " 字节"
                + (st.DataSize > 0 ? "（占数据区 " + (d.ImageBytesTotal * 100.0 / st.DataSize).ToString("F1") + "%）" : ""));
            foreach (CardImageInfo img in d.Images)
            {
                Console.WriteLine("  #" + img.Index.ToString().PadLeft(3) + " @" + img.Offset.ToString("N0").PadLeft(14)
                    + "  " + img.Size.ToString("N0").PadLeft(12) + " 字节  " + img.Width + "×" + img.Height
                    + "  位深 " + img.BitDepth + " 色型 " + img.ColorType + "  块 " + img.Chunks
                    + (img.Ended ? "" : "  [未闭合]") + (img.IsFace ? "  [脸图]" : ""));
            }
            Console.WriteLine();
            Console.WriteLine("服装部件   : " + d.Parts.Count + " 件");
            foreach (CardPart p in d.Parts)
            {
                Console.WriteLine("  #" + p.Index + "  id=" + (p.Id < 0 ? "?" : p.Id.ToString()) + "  键: " + string.Join(", ", p.Keys));
            }
            Console.WriteLine();
            Console.WriteLine("数据块目录 : " + d.Blocks.Count + " 条");
            foreach (CardBlockInfo b in d.Blocks)
            {
                Console.WriteLine("  " + b.Name + "  v" + (b.Version == null ? "?" : b.Version)
                    + "  pos=" + b.Pos.ToString("N0") + "  size=" + b.Size.ToString("N0"));
            }
            Console.WriteLine();
            Console.WriteLine("插件块     : " + d.KkxOffsets.Count + " 处 · 插件名 " + d.PluginNames.Count + " 个");
            foreach (string n in d.PluginNames)
            {
                Console.WriteLine("  - " + n);
            }
            Console.WriteLine();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> distinct = new List<string>();
            foreach (CardModDetail m in d.ModRefs)
            {
                if (m.ModId != null && seen.Add(m.ModId))
                {
                    distinct.Add(m.ModId);
                }
            }
            Console.WriteLine("声明区条目 : " + d.ModRefs.Count + " 条（去重 " + distinct.Count + " 个 mod）");
            foreach (CardModDetail m in d.ModRefs)
            {
                Console.WriteLine("  " + (m.Property == null ? "?" : m.Property) + "  slot=" + m.Slot + "  " + m.ModId
                    + (m.Author == null ? "" : "  作者 " + m.Author));
            }
            foreach (string w in d.Warnings)
            {
                Console.WriteLine("提示       : " + w);
            }
            return 0;
        }

        private static int ScanModsCommand(string db, List<string> dirs, bool force)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig disk = hub.Core.LoadRoots();
                RootsRules.Normalize(disk);
                hub.EnsureMigrated(disk);
                RootsConfig cfg = BuildConfig(hub, dirs, true);
                Console.WriteLine("库: " + hub.CorePath);
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                ScanResult r = Scanner.ScanMods(hub, cfg, force, m => Console.WriteLine(m));
                Console.WriteLine();
                Console.WriteLine("mod 扫描完成：" + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒");
                Console.WriteLine("  枚举 " + r.Seen + "  新增/更新 " + r.Added + "  跳过 " + r.Skipped + "  失败 " + r.Failed
                    + "  清理 " + r.Removed);
                foreach (string e in r.Errors)
                {
                    Console.WriteLine("  ! " + e);
                }
            }
            return 0;
        }

        private static int ScanCardsCommand(string db, List<string> dirs, bool force, int thumb, int quality)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig disk = hub.Core.LoadRoots();
                RootsRules.Normalize(disk);
                hub.EnsureMigrated(disk);
                RootsConfig cfg = BuildConfig(hub, dirs, false);
                Console.WriteLine("库: " + hub.CorePath);
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                ScanResult r = Scanner.ScanCards(hub, cfg, force, thumb, quality, m => Console.WriteLine(m));
                Console.WriteLine();
                Console.WriteLine("卡片扫描完成：" + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒");
                Console.WriteLine("  枚举 " + r.Seen + "  新增/更新 " + r.Added + "  跳过 " + r.Skipped
                    + "  非卡 " + r.NonCard + "  失败 " + r.Failed + "  清理 " + r.Removed);
                Console.WriteLine("  引用条目 " + r.RefEntries + "  缩略图 " + (r.ThumbBytes / 1024.0 / 1024.0).ToString("F1") + " MB");
                Console.WriteLine("  角色名：新读 " + r.NamesRead + "  存量补读 " + r.NamesFilled);
                Console.WriteLine("  卡类型补正 " + r.TypesFixed);
                Console.WriteLine("  timeline 读取 " + r.TimelineRead);
                foreach (string e in r.Errors)
                {
                    Console.WriteLine("  ! " + e);
                }
            }
            return 0;
        }
        /// <summary>按范围扫描（面板「扫描主要 / 追加库扫描」的 CLI 通道）——mod 与卡片两轮，结果合并展示；离线库一律跳过。</summary>
        private static int ScanScopeCommand(string db, bool force, ScanScope scope, int thumb, int quality)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig disk = hub.Core.LoadRoots();
                RootsRules.Normalize(disk);
                hub.EnsureMigrated(disk);
                Console.WriteLine("库: " + hub.CorePath);
                Console.WriteLine(scope == ScanScope.Preset
                    ? "范围: 主要库——预置条目（mod 主库 / 缓存库槽位 + 卡片 female / coordinate / Studio）"
                    : "范围: 追加库——使用者添加的库根（mod 冷冻库 + 卡片附加库）");
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                ScanResult r = new ScanResult();
                r.Merge(Scanner.ScanMods(hub, disk, null, scope, force, m => Console.WriteLine(m)));
                r.Merge(Scanner.ScanCards(hub, disk, null, scope, force, thumb, quality, m => Console.WriteLine(m)));
                Console.WriteLine();
                Console.WriteLine("扫描完成：" + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒");
                Console.WriteLine("  枚举 " + r.Seen + "  新增/更新 " + r.Added + "  跳过 " + r.Skipped
                    + "  非卡 " + r.NonCard + "  失败 " + r.Failed + "  清理 " + r.Removed);
                Console.WriteLine("  角色名：新读 " + r.NamesRead + "  存量补读 " + r.NamesFilled);
                Console.WriteLine("  卡类型补正 " + r.TypesFixed);
                Console.WriteLine("  timeline 读取 " + r.TimelineRead);
                foreach (string e in r.Errors)
                {
                    Console.WriteLine("  ! " + e);
                }
            }
            return 0;
        }

        /// <summary>命令行给了目录则临时按级别 1 构造配置，否则用已保存的库根配置。</summary>
        private static RootsConfig BuildConfig(StoreHub hub, List<string> dirs, bool isMods)
        {
            if (dirs.Count == 0)
            {
                return hub.Core.LoadRoots();
            }
            RootsConfig cfg = hub.Core.LoadRoots();
            List<RootEntry> list = new List<RootEntry>();
            foreach (string d in dirs)
            {
                list.Add(new RootEntry { tier = Tier.Main, path = d, recurse = true });
            }
            if (isMods)
            {
                cfg.modRoots = list;
            }
            else
            {
                cfg.cardRoots = list;
            }
            return cfg;
        }

        /// <summary>列出重复副本组——同 guid 多份文件（版本 / 作者实时读 manifest，人工筛旧版的判据）。</summary>
        private static int DupCommand(string db)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                List<DupGroup> groups = hub.ListDuplicateGroups(cfg);
                List<ModFileRecord> allFiles = new List<ModFileRecord>();
                foreach (DupGroup g in groups)
                {
                    allFiles.AddRange(g.Files);
                }
                // 与面板端点同一实现：MD5 只算冲突文件、算过就忽略（档案落主库 mod_hash）
                int computed = 0;
                List<string> hashErrors = new List<string>();
                Dictionary<string, string> hashes = hub.FillHashes(allFiles, out computed, out hashErrors);
                int oldCount = hub.Core.ListModOld().Count;
                Console.WriteLine("库: " + hub.CorePath);
                Console.WriteLine("重复副本组: " + groups.Count + "（已登记旧版 " + oldCount + " 条）· MD5 本次新增 " + computed + " 条档案");
                foreach (string e in hashErrors)
                {
                    Console.WriteLine("[哈希] 算不出：" + e);
                }
                // 引用卡片——与面板组标题行最右侧的缩略图区同一数据源、同一批量查询：每组总数 + 前三张卡片名（文本面核对通道）
                List<string> dupGuids = new List<string>();
                foreach (DupGroup g in groups)
                {
                    dupGuids.Add(g.Guid);
                }
                Dictionary<string, DupCardRefs> cardRefs = hub.QueryDupCardRefs(cfg, dupGuids, 3);
                foreach (DupGroup g in groups)
                {
                    Console.WriteLine();
                    Console.WriteLine("● " + g.Guid + "（" + g.Files.Count + " 份 · 主库 " + g.MainCount + "）");
                    DupCardRefs refs = null;
                    cardRefs.TryGetValue(g.Guid, out refs);
                    string refNames = "";
                    long refTotal = 0;
                    if (refs != null)
                    {
                        refTotal = refs.Total;
                        foreach (CardRow c in refs.Top)
                        {
                            if (refNames.Length > 0)
                            {
                                refNames = refNames + " · ";
                            }
                            refNames = refNames + c.FileName;
                        }
                    }
                    Console.WriteLine("    引用卡片 " + refTotal + " 张" + (refNames.Length > 0 ? "（前三张 " + refNames + "）" : ""));
                    foreach (ModFileRecord f in g.Files)
                    {
                        ModInfo info = ZipModReader.Parse(f.FilePath);
                        string tail = "";
                        if (RootsRules.IsOldFileName(f.FileName))
                        {
                            tail = tail + "  [旧版]";
                        }
                        if (!string.IsNullOrEmpty(info.Error))
                        {
                            tail = tail + "  ! " + info.Error;
                        }
                        Console.WriteLine("    [" + Tier.Name(f.Tier) + "] " + f.FileName
                            + "  版本 " + (info.Version ?? "<无>")
                            + "  作者 " + (info.Author ?? "<无>")
                            + "  " + (f.Size / 1024 / 1024) + " MB" + tail);
                        string md5 = null;
                        hashes.TryGetValue(f.FilePath, out md5);
                        string ctime = "";
                        FileInfo fi = new FileInfo(f.FilePath);
                        if (fi.Exists)
                        {
                            ctime = Store.CreatedStampOf(fi);
                        }
                        Console.WriteLine("        路径 " + f.FilePath);
                        Console.WriteLine("        建 " + ctime + " · 改 " + f.Mtime + " · md5 " + (md5 ?? "<算不出>"));
                    }
                }
                return 0;
            }
        }

        /// <summary>旧版操作（指定这个版本 / 判为旧版 / 换用此版本 / 正名搬入主库）——与面板端点共用同一实现。</summary>
        private static int OldOpCommand(string db, List<string> rest, string op)
        {
            RequireArgs(rest, op + " <guid> <文件路径>", 2);
            string guid = rest[0];
            string path = rest[1];
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                string detail;
                string error;
                if (op == "keep-version")
                {
                    error = hub.KeepVersion(cfg, guid, path, out detail);
                }
                else if (op == "mark-old")
                {
                    error = hub.MarkOld(cfg, guid, path, out detail);
                }
                else if (op == "swap")
                {
                    error = hub.SwapVersion(cfg, guid, path, out detail);
                }
                else
                {
                    error = hub.PromoteOld(cfg, guid, path, out detail);
                }
                if (error != null)
                {
                    Console.Error.WriteLine("失败: " + error);
                    return 3;
                }
                Console.WriteLine("完成: " + detail);
                return 0;
            }
        }
        /// <summary>查看某 mod 的组成（按需建档，与面板端点共用同一实现）——目录聚合 + 条目数 / 大小合计。</summary>
        private static int CompositionCommand(string db, List<string> rest, bool force)
        {
            RequireArgs(rest, "composition <guid> [--force] [--text]", 1);
            string guid = rest[0];
            bool showText = rest.Remove("--text");
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                string error;
                ModComposition rec = hub.AnalyzeComposition(cfg, guid, force, out error);
                if (rec == null)
                {
                    Console.Error.WriteLine("失败: " + error);
                    return 3;
                }
                Console.WriteLine("guid       : " + rec.Guid);
                Console.WriteLine("文件       : " + rec.FilePath);
                Console.WriteLine("大小       : " + rec.Size.ToString("N0") + " 字节");
                Console.WriteLine("条目数     : " + rec.EntryCount.ToString("N0"));
                Console.WriteLine("原始合计   : " + rec.TotalSize.ToString("N0") + " 字节");
                Console.WriteLine("压缩合计   : " + rec.TotalCompressed.ToString("N0") + " 字节");
                Console.WriteLine("文本条目   : " + StoreHub.TextEntryCount(rec.Texts) + " 份");
                Console.WriteLine("建档于     : " + rec.AnalyzedAt + (rec.Cached ? "（命中已有档案）" : "（本次新建）"));
                Console.WriteLine();
                Console.WriteLine("目录聚合（按条目数降序，前 15）：");
                List<CompositionDir> dirs = StoreHub.BuildDirs(rec.Entries);
                int n = 0;
                foreach (CompositionDir d in dirs)
                {
                    Console.WriteLine("  " + d.Count.ToString().PadLeft(6) + " 项  " + d.Size.ToString("N0").PadLeft(14) + " 字节  " + d.Path);
                    n = n + 1;
                    if (n >= 15)
                    {
                        break;
                    }
                }
                // [段1] 文本条目内容抽样（实测「内容确实捞到档案里」——默认不打印，--text 时出）
                if (showText)
                {
                    Console.WriteLine();
                    Console.WriteLine("文本条目内容（前 200 字符）：");
                    Dictionary<string, string> map = StoreHub.ParseTexts(rec.Texts);
                    foreach (KeyValuePair<string, string> kv in map)
                    {
                        string body = kv.Value.Replace("\r", "").Replace("\n", " | ");
                        if (body.Length > 200)
                        {
                            body = body.Substring(0, 200) + "…";
                        }
                        Console.WriteLine("  ● " + kv.Key + "（" + kv.Value.Length.ToString("N0") + " 字符）");
                        Console.WriteLine("    " + body);
                    }
                }
                return 0;
            }
        }

        private static int StatsCommand(string db)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                Console.WriteLine("库: " + hub.CorePath);
                Snapshot s = hub.Snapshot(cfg);
                Console.WriteLine();
                Console.WriteLine("卡片总数     : " + s.Cards.ToString("N0") + "（有引用 " + s.CardsWithRefs.ToString("N0") + "，主库就绪 " + s.CardsReady.ToString("N0") + "）");
                Console.WriteLine("mod 总数     : " + s.Mods.ToString("N0") + "（主库 " + s.ModsTier1 + " / 缓存 " + s.ModsTier2 + " / 冷冻 " + s.ModsTier3 + "）");
                long dupGroups = 0;
                long dupPending = 0;
                hub.DupCounts(cfg, out dupGroups, out dupPending);
                Console.WriteLine("mod 文件副本 : " + s.ModFiles.ToString("N0") + "（重复副本 " + dupGroups.ToString("N0") + " 组 · " + dupPending.ToString("N0") + " 待确认）");
                Console.WriteLine("引用（去重） : " + s.Refs.ToString("N0"));
                Console.WriteLine("  绿（主库） : " + s.Colors.Green.ToString("N0"));
                Console.WriteLine("  黄（缓存） : " + s.Colors.Yellow.ToString("N0"));
                Console.WriteLine("  红（冷冻） : " + s.Colors.Red.ToString("N0"));
                Console.WriteLine("  黑（全无） : " + s.Colors.Black.ToString("N0"));
                Console.WriteLine("未被引用的 mod: " + s.UnusedMods.ToString("N0"));
                Console.WriteLine();
                Console.WriteLine("缺失排行（全库皆无，按被引用卡片数）：");
                foreach (KeyValuePair<string, long> kv in hub.MissingRanking(cfg, 30))
                {
                    Console.WriteLine("  " + kv.Value.ToString().PadLeft(5) + " 张  " + kv.Key);
                }
            }
            return 0;
        }

        /// <summary>列出作者清单（按发布的 mod 数量倒序）——与面板「按作者筛选」同一数据源（聚合表 mod_author）。</summary>
        private static int AuthorsCommand(string db)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                Console.WriteLine("库: " + hub.CorePath);
                List<AuthorRow> list = hub.QueryAuthors();
                Console.WriteLine("作者数: " + list.Count.ToString("N0"));
                long total = 0;
                foreach (AuthorRow a in list)
                {
                    total += a.Count;
                }
                Console.WriteLine("mod 行合计: " + total.ToString("N0"));
                Console.WriteLine();
                int shown = 0;
                foreach (AuthorRow a in list)
                {
                    if (shown >= 30)
                    {
                        break;
                    }
                    string name = string.IsNullOrWhiteSpace(a.Author) ? "（无作者）" : a.Author;
                    Console.WriteLine("  " + a.Count.ToString().PadLeft(5) + " 个  " + name);
                    shown++;
                }
            }
            return 0;
        }
        /// <summary>列出卡片（按指定排序键）——与面板「卡片排序」同一实现（StoreHub.QueryCards），供文本面核对排序结果。</summary>
        private static int CardsCommand(string db, List<string> rest)
        {
            string order = Take(rest, "--order") ?? "mtime";
            bool desc = rest.Remove("--desc");
            string q = Take(rest, "--q") ?? "";
            int limit = int.Parse(Take(rest, "--limit") ?? "40");
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);
                Console.WriteLine("库: " + hub.CorePath);
                Console.WriteLine("排序: " + order + (order == "mtime" ? "（恒倒序）" : (desc ? " 降序" : " 升序")) + " · 关键词: " + (q.Length > 0 ? q : "（无）"));
                Console.WriteLine();
                List<CardRow> rows = hub.QueryCards(cfg, 1, limit, "all", q, null, null, order, desc);
                Console.WriteLine("共列 " + rows.Count + " 张");
                foreach (CardRow r in rows)
                {
                    string name = string.IsNullOrEmpty(r.CharaName) ? "（无名）" : r.CharaName;
                    string tl = r.TimelineSeconds.HasValue
                        ? "  |  timeline " + r.TimelineSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " 秒"
                        : "";
                    Console.WriteLine("  " + name + "  |  " + r.Folder + "  |  " + r.FileName + tl);
                }
            }
            return 0;
        }

        /// <summary>「按作者整理」命令行通道——plan 生成计划 / show 读计划表并核对 / exec 执行（与面板端点同一实现）。</summary>
        private static int SortCommand(string db, List<string> rest)
        {
            string sub = rest.Count > 0 ? rest[0] : "show";
            if (rest.Count > 0)
            {
                rest.RemoveAt(0);
            }
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                hub.EnsureMigrated(cfg);

                if (sub == "plan")
                {
                    List<string> roots = new List<string>(rest);
                    if (roots.Count == 0)
                    {
                        foreach (RootEntry e in cfg.ModRootsOrdered())
                        {
                            if (e.readOnly || e.offline)
                            {
                                continue;
                            }
                            roots.Add(e.path);
                        }
                    }
                    if (roots.Count == 0)
                    {
                        Console.Error.WriteLine("没有可整理的库根（只读 / 离线库不参与）");
                        return 2;
                    }
                    hub.Core.ClearSortPlans();
                    SortJobState job = new SortJobState();
                    SortOrganizer.BuildPlan(hub, cfg, roots, job);
                    Console.WriteLine("计划 #" + job.PlanId.ToString() + "：" + job.Message);
                    Console.WriteLine("  条目 " + job.Total.ToString() + " · 冲突 " + job.Conflicts.ToString());
                    return job.Conflicts > 0 ? 1 : 0;
                }

                SortPlanRow plan = hub.Core.LatestSortPlan();
                if (plan == null)
                {
                    Console.WriteLine("还没有整理计划——先跑 sort plan");
                    return 0;
                }

                if (sub == "conflict")
                {
                    if (rest.Count < 1)
                    {
                        Console.Error.WriteLine("用法: sort conflict <seq>");
                        return 2;
                    }
                    long keepSeq = long.Parse(rest[0]);
                    string groupError = null;
                    SortConflictGroup group = SortOrganizer.ConflictGroup(hub, plan.Id, keepSeq, out groupError);
                    if (groupError != null)
                    {
                        Console.Error.WriteLine(groupError);
                        return 2;
                    }
                    Console.WriteLine("冲突组 guid " + group.Guid + " ⇒ " + group.DestPath);
                    /* 展示数据与面板同源：manifest / 创建时间走 ModCopyReader，MD5 走同一档哈希档案 */
                    List<ModFileRecord> recs = new List<ModFileRecord>();
                    foreach (SortConflictCandidate c in group.Items)
                    {
                        ModFileRecord rec = new ModFileRecord();
                        rec.Guid = group.Guid;
                        rec.Tier = c.Tier;
                        rec.RootPath = c.RootPath;
                        rec.FilePath = c.FilePath;
                        rec.FileName = c.FileName;
                        rec.Size = c.Size;
                        rec.Mtime = c.Mtime;
                        recs.Add(rec);
                    }
                    int computed = 0;
                    List<string> hashErrors = new List<string>();
                    Dictionary<string, string> hashes = hub.FillHashes(recs, out computed, out hashErrors);
                    foreach (SortConflictCandidate c in group.Items)
                    {
                        ModCopyInfo info = ModCopyReader.Read(c.FilePath);
                        Console.WriteLine("  [seq " + c.Seq.ToString() + " · " + c.Tier.ToString() + " 级] " + c.FilePath);
                        Console.WriteLine("      版本 " + (info.Version == null ? "<无>" : info.Version)
                            + " · 作者 " + (info.Author == null ? "<无>" : info.Author)
                            + " · " + c.Size.ToString() + " 字节 · 建 " + (info.Ctime == null || info.Ctime.Length == 0 ? "<读不到>" : info.Ctime) + " · 改 " + c.Mtime);
                        string md5 = null;
                        hashes.TryGetValue(c.FilePath, out md5);
                        if (md5 != null)
                        {
                            Console.WriteLine("      MD5 " + md5);
                        }
                        else
                        {
                            Console.WriteLine("      MD5 未算出（档案缺失或读不到）");
                        }
                        if (info.Error != null)
                        {
                            Console.WriteLine("      manifest 读不到：" + info.Error);
                        }
                    }
                    foreach (string e in hashErrors)
                    {
                        Console.WriteLine("  ! MD5 算不出：" + e);
                    }
                    Console.WriteLine("挑一份保留：sort keep <seq>");
                    return 0;
                }

                if (sub == "keep")
                {
                    if (rest.Count < 1)
                    {
                        Console.Error.WriteLine("用法: sort keep <seq>");
                        return 2;
                    }
                    long keepSeq = long.Parse(rest[0]);
                    string keepDetail = null;
                    string keepError = SortOrganizer.ResolveConflict(hub, cfg, plan.Id, keepSeq, out keepDetail);
                    if (keepError != null)
                    {
                        Console.Error.WriteLine(keepError);
                        return 2;
                    }
                    Console.WriteLine("已处理：" + keepDetail);
                    return 0;
                }

                if (sub == "show")
                {
                    long conflicts = SortOrganizer.RecheckPlan(hub, plan.Id);
                    hub.Core.FinishSortPlan(plan.Id, plan.State, plan.ItemCount, conflicts, plan.Note);
                    Console.WriteLine("计划 #" + plan.Id.ToString() + " · " + plan.CreatedAt + " · 状态 " + plan.State + " · 条目 " + plan.ItemCount.ToString());
                    foreach (string line in plan.Scope.Split('\n'))
                    {
                        if (line.Trim().Length > 0)
                        {
                            Console.WriteLine("  范围: " + line);
                        }
                    }
                    long pending = 0;
                    long conflict = 0;
                    long moved = 0;
                    long failed = 0;
                    long skipped = 0;
                    List<SortPlanItemRow> all = hub.Core.QuerySortPlanItems(plan.Id);
                    foreach (SortPlanItemRow r in all)
                    {
                        if (r.State == SortOrganizer.StateMoved)
                        {
                            moved = moved + 1;
                        }
                        else if (r.State == SortOrganizer.StateConflict)
                        {
                            conflict = conflict + 1;
                        }
                        else if (r.State == SortOrganizer.StateFailed)
                        {
                            failed = failed + 1;
                        }
                        else if (r.State == SortOrganizer.StateSkipped)
                        {
                            skipped = skipped + 1;
                        }
                        else
                        {
                            pending = pending + 1;
                        }
                    }
                    long shownConflict = 0;
                    foreach (SortPlanItemRow r in all)
                    {
                        if (r.State != SortOrganizer.StateConflict || shownConflict >= 40)
                        {
                            continue;
                        }
                        Console.WriteLine("  [冲突] seq " + r.Seq.ToString() + "  " + r.SrcPath + " ⇒ " + r.DestPath + "（" + r.Note + "）");
                        shownConflict = shownConflict + 1;
                    }
                    long shown = 0;
                    foreach (SortPlanItemRow r in all)
                    {
                        if (r.State != SortOrganizer.StatePending || shown >= 20)
                        {
                            continue;
                        }
                        Console.WriteLine("  [待搬] " + r.Folder + " ← " + r.SrcPath);
                        shown = shown + 1;
                    }
                    Console.WriteLine("统计：待搬 " + pending.ToString() + " · 就位 " + moved.ToString()
                        + " · 冲突 " + conflict.ToString() + " · 失败 " + failed.ToString() + " · 跳过 " + skipped.ToString());
                    if (conflict > 0)
                    {
                        Console.Error.WriteLine("冲突未清零——处理完（或改名 / 删掉目标同名文件）再跑一次 sort show 核对");
                        return 1;
                    }
                    return 0;
                }

                if (sub == "exec")
                {
                    bool yes = false;
                    foreach (string a in rest)
                    {
                        if (a == "--yes")
                        {
                            yes = true;
                        }
                    }
                    if (!yes)
                    {
                        Console.Error.WriteLine("执行会移动磁盘文件——请加 --yes 确认（并确保 koikatsu 已关闭）");
                        return 2;
                    }
                    long conflicts = hub.Core.CountSortPlanItems(plan.Id, SortOrganizer.StateConflict);
                    if (conflicts > 0)
                    {
                        Console.Error.WriteLine("还有 " + conflicts.ToString() + " 条冲突未解决——先处理再执行");
                        return 2;
                    }
                    SortJobState job = new SortJobState();
                    SortOrganizer.ExecutePlan(hub, cfg, plan.Id, job);
                    Console.WriteLine("执行：就位 " + job.Moved.ToString() + " · 跳过 " + job.Skipped.ToString()
                        + " · 失败 " + job.Failed.ToString() + " · 冲突 " + job.Conflicts.ToString()
                        + " · 清空目录 " + job.PrunedDirs.ToString());
                    foreach (string err in job.Errors)
                    {
                        Console.Error.WriteLine("  失败：" + err);
                    }
                    return job.Failed > 0 ? 1 : 0;
                }

                Console.Error.WriteLine("未知子命令: " + sub + "（可用：plan / show / exec）");
                return 2;
            }
        }

        private static int RootsListCommand(string db)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                Console.WriteLine("库: " + hub.CorePath);
                Console.WriteLine("游戏根: " + cfg.gameRoot);
                Console.WriteLine();
                Console.WriteLine("## mod 库根");
                foreach (RootEntry e in cfg.ModRootsOrdered())
                {
                    Console.WriteLine("  级别 " + e.tier + "（" + Tier.Name(e.tier) + "）  " + e.path
                        + (e.recurse ? "  [含子目录]" : "  [仅本目录]")
                        + (e.readOnly ? "  [只读]" : "")
                        + (e.locked ? "  [预置·锁定]" : "")
                        + (e.tier == Tier.Cache ? "  [缓存库·不可删]" : "")
                        + OfflineTag(e));
                }
                Console.WriteLine("## 卡片库根");
                foreach (RootEntry e in cfg.CardRootsOrdered())
                {
                    Console.WriteLine("  级别 " + e.tier + "  " + e.path
                        + (e.recurse ? "  [含子目录]" : "  [仅本目录]")
                        + (e.readOnly ? "  [只读]" : "")
                        + (e.locked ? "  [预置·锁定]" : "")
                        + OfflineTag(e));
                }
            }
            return 0;
        }

        /// <summary>列出待办（软件认为需要使用者处理的事）——目前只有「离线库」一类。</summary>
        private static int TodosCommand(string db)
        {
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                List<TodoRow> rows = hub.Core.ListTodos();
                Console.WriteLine("库: " + hub.CorePath);
                Console.WriteLine("待办: " + rows.Count + " 条");
                foreach (TodoRow t in rows)
                {
                    string path = t.Key;
                    string note = "";
                    List<RootEntry> all = new List<RootEntry>(cfg.ModRootsOrdered());
                    all.AddRange(cfg.CardRootsOrdered());
                    foreach (RootEntry e in all)
                    {
                        if (string.Equals((e.path ?? "").Trim().ToLowerInvariant(), t.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            path = e.path;
                            note = e.note ?? "";
                            break;
                        }
                    }
                    Console.WriteLine("  #" + t.Id + "  [" + t.Kind + "]  " + path
                        + (string.IsNullOrWhiteSpace(note) ? "" : "  备注：" + note)
                        + "  （登记于 " + t.CreatedAt + "）");
                }
            }
            return 0;
        }

        /// <summary>库根列表的离线标记——离线库附备注（内容取数据库，不随扫描更新）。</summary>
        private static string OfflineTag(RootEntry e)
        {
            if (e == null || !e.offline)
            {
                return "";
            }
            if (string.IsNullOrWhiteSpace(e.note))
            {
                return "  [已离线]";
            }
            return "  [已离线]（备注：" + e.note + "）";
        }

        private static int RootsAddCommand(string db, List<string> rest)
        {
            if (rest.Count < 3)
            {
                Console.Error.WriteLine("用法: roots-add <mods|cards> <级别> <路径> [含子目录|仅本目录] [只读]");
                return 2;
            }
            string kind = rest[0];
            int tier = int.Parse(rest[1]);
            RootEntry entry = new RootEntry { tier = tier, path = rest[2], recurse = true };
            for (int i = 3; i < rest.Count; i++)
            {
                switch (rest[i])
                {
                    case "含子目录":
                    case "recurse":
                        entry.recurse = true;
                        break;
                    case "仅本目录":
                    case "norecurse":
                        entry.recurse = false;
                        break;
                    case "只读":
                    case "readonly":
                        entry.readOnly = true;
                        break;
                }
            }

            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                List<RootEntry> list = kind == "mods" ? cfg.modRoots : cfg.cardRoots;
                list.RemoveAll(e => string.Equals(e.path, entry.path, StringComparison.OrdinalIgnoreCase));
                list.Add(entry);
                if (kind == "mods")
                {
                    cfg.modRoots = list;
                }
                else
                {
                    cfg.cardRoots = list;
                }
                RootsRules.Normalize(cfg);
                hub.Core.SaveRoots(cfg);
                Console.WriteLine("已登记 " + (kind == "mods" ? "mod" : "卡片") + " 库根：级别 " + tier + " · " + entry.path);
                Console.WriteLine("（库根由你添加——扫描只覆盖你添加的文件夹，不做全库盲扫）");
                Console.WriteLine("当前 mod 库根 " + cfg.modRoots.Count + " 个 / 卡片库根 " + cfg.cardRoots.Count + " 个");
            }
            return 0;
        }

        private static int RootsClearCommand(string db, List<string> rest)
        {
            RequireArgs(rest, "roots-clear <mods|cards|all>", 1);
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                string kind = rest[0];
                int kept = 0;
                if (kind == "mods" || kind == "all")
                {
                    kept = kept + KeepPresets(cfg.modRoots, true);
                }
                if (kind == "cards" || kind == "all")
                {
                    kept = kept + KeepPresets(cfg.cardRoots, false);
                }
                hub.Core.SaveRoots(cfg);
                Console.WriteLine("已清空：" + kind + "（保留预置条目 " + kept + " 条——预置主库 / 缓存库槽位不可删除）");
            }
            return 0;
        }
        /// <summary>移除列表里的普通条目（清理配置时保留预置条目；keepCacheSlot=true 时连缓存库槽位一并保留）；返回保留条数。</summary>
        private static int KeepPresets(List<RootEntry> list, bool keepCacheSlot)
        {
            int kept = 0;
            for (int i = list.Count - 1; i >= 0; i = i - 1)
            {
                bool keep = list[i].locked || (keepCacheSlot && list[i].tier == Tier.Cache);
                if (keep)
                {
                    kept = kept + 1;
                }
                else
                {
                    list.RemoveAt(i);
                }
            }
            return kept;
        }

        /// <summary>设置游戏根地址——预置条目处置与面板保存同一实现（旧根的锁定条目按新根重派生；mod 缓存库槽位未被使用者改过时跟随新根）。</summary>
        private static int SetGameRootCommand(string db, List<string> rest)
        {
            RequireArgs(rest, "set-game-root <路径>", 1);
            using (StoreHub hub = new StoreHub(db))
            {
                RootsConfig cfg = hub.Core.LoadRoots();
                RootsRules.Normalize(cfg);
                string previousRoot = cfg.gameRoot;
                cfg.gameRoot = RootsRules.NormalizeGameRoot(rest[0]);
                // 预置条目处置与面板保存同一实现——旧根的锁定条目重派生、缓存库槽位未被改过则跟随
                List<string> warnings = RootsRules.ApplyGameRootChange(cfg, previousRoot, out _);
                RootsRules.Normalize(cfg);
                hub.Core.SaveRoots(cfg);
                Console.WriteLine("游戏根: " + cfg.gameRoot);
                Console.WriteLine("mod 库根 " + cfg.modRoots.Count + " 个 / 卡片库根 " + cfg.cardRoots.Count + " 个");
                foreach (string w in warnings)
                {
                    Console.WriteLine("  ! " + w);
                }
                foreach (RootEntry e in cfg.ModRootsOrdered())
                {
                    Console.WriteLine("  级别 " + e.tier + "（" + Tier.Name(e.tier) + "）  " + e.path);
                }
            }
            return 0;
        }

        private static int ThumbCommand(List<string> rest, int thumbWidth, int quality)
        {
            RequireArgs(rest, "thumb <card.png> <out.jpg> [--thumb N] [--quality Q]", 2);
            string cardPath = rest[0];
            string outPath = rest[1];
            CardInfo c = CardReader.Parse(cardPath);
            if (c.ImageEnd <= 0)
            {
                Console.Error.WriteLine("未找到图片区");
                return 1;
            }
            byte[] jpg = Thumbnail.FromCard(cardPath, c.ImageEnd, thumbWidth, quality);
            if (jpg == null)
            {
                Console.Error.WriteLine("缩略图生成失败: " + Thumbnail.LastError);
                return 1;
            }
            File.WriteAllBytes(outPath, jpg);
            string size = "?";
            try
            {
                using (SixLabors.ImageSharp.Image img = SixLabors.ImageSharp.Image.Load(jpg))
                {
                    size = img.Width + "x" + img.Height;
                }
            }
            catch (Exception ex)
            {
                size = "回验失败: " + ex.Message;
            }
            Console.WriteLine("卡类型     : " + (c.CardType ?? "<无>"));
            Console.WriteLine("图片区     : " + c.ImageEnd.ToString("N0") + " 字节");
            Console.WriteLine("缩略图     : " + jpg.Length.ToString("N0") + " 字节  " + size);
            Console.WriteLine("输出       : " + outPath);
            return 0;
        }
        /// <summary>收集目录下的卡片文件（.png）——recurse 为真时进子目录。</summary>
        private static void CollectCards(string dir, List<string> files, bool recurse)
        {
            try
            {
                string[] pngs = Directory.GetFiles(dir, "*.png", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                Array.Sort(pngs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pngs.Length; i = i + 1)
                {
                    files.Add(pngs[i]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("目录枚举失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        /// <summary>读卡片可编辑字段（姓 / 名 / 爱称 / 性格）——单个文件给详情；目录给全量清单 + 性格取值分布。</summary>
        private static int CardParamsCommand(List<string> rest)
        {
            RequireArgs(rest, "card-params <card.png|dir> [--recurse]", 1);
            string target = rest[0];
            if (Directory.Exists(target))
            {
                List<string> files = new List<string>();
                CollectCards(target, files, rest.Contains("--recurse"));
                Console.WriteLine("目录       : " + target + " · 卡片 " + files.Count + " 张");
                Dictionary<int, int> dist = new Dictionary<int, int>();
                Dictionary<int, int> wpDist = new Dictionary<int, int>();
                int[] denialYes = new int[CardEdit.DenialKeys.Length];
                int noParam = 0;
                foreach (string f in files)
                {
                    CardLayout layout = CardEdit.Parse(f);
                    CardParamInfo info = CardEdit.ReadParams(f, layout);
                    if (!info.HasParameter)
                    {
                        noParam = noParam + 1;
                        Console.WriteLine("  " + Path.GetFileName(f) + "  —  " + (info.Error == null ? "无 Parameter 块" : info.Error));
                        continue;
                    }
                    string pname = Personality.NameOf(info.Personality);
                    string wpname = WeakPoint.NameOf(info.WeakPoint);
                    Console.WriteLine("  " + Path.GetFileName(f) + "  姓「" + info.LastName + "」 名「" + info.FirstName
                        + "」 爱称「" + info.NickName + "」  性格 " + info.Personality + " " + (pname == null ? "(未收录)" : pname)
                        + "  敏感带 " + info.WeakPoint + " " + (wpname == null ? "(未收录)" : wpname));
                    int wc;
                    if (wpDist.TryGetValue(info.WeakPoint, out wc))
                    {
                        wpDist[info.WeakPoint] = wc + 1;
                    }
                    else
                    {
                        wpDist[info.WeakPoint] = 1;
                    }
                    for (int i = 0; i < CardEdit.DenialKeys.Length; i = i + 1)
                    {
                        if (info.Denial[i])
                        {
                            denialYes[i] = denialYes[i] + 1;
                        }
                    }
                    int n;
                    if (dist.TryGetValue(info.Personality, out n))
                    {
                        dist[info.Personality] = n + 1;
                    }
                    else
                    {
                        dist[info.Personality] = 1;
                    }
                }
                Console.WriteLine();
                Console.WriteLine("性格分布   : " + dist.Count + " 种 · 无 Parameter " + noParam + " 张");
                List<int> ids = new List<int>(dist.Keys);
                ids.Sort();
                foreach (int id in ids)
                {
                    string name = Personality.NameOf(id);
                    Console.WriteLine("  ID " + id.ToString().PadLeft(3) + "  " + (name == null ? "未收录" : name).PadRight(18) + "  " + dist[id] + " 张");
                }
                Console.WriteLine();
                Console.WriteLine("敏感带分布 : " + wpDist.Count + " 种");
                List<int> wids = new List<int>(wpDist.Keys);
                wids.Sort();
                foreach (int id in wids)
                {
                    string name = WeakPoint.NameOf(id);
                    Console.WriteLine("  ID " + id.ToString().PadLeft(3) + "  " + (name == null ? "未收录" : name).PadRight(18) + "  " + wpDist[id] + " 张");
                }
                Console.WriteLine("是否接受分布（是 / 卡片数）: ");
                for (int i = 0; i < CardEdit.DenialKeys.Length; i = i + 1)
                {
                    Console.WriteLine("  " + CardEdit.DenialNames[i].PadRight(8) + " " + denialYes[i] + " / " + files.Count);
                }
                return 0;
            }
            CardLayout one = CardEdit.Parse(target);
            if (one.Error != null)
            {
                Console.WriteLine("布局解析失败：" + one.Error);
                return 3;
            }
            CardParamInfo cur = CardEdit.ReadParams(target, one);
            Console.WriteLine("文件       : " + target);
            Console.WriteLine("卡类型     : " + (one.CardType == null ? "<无>" : one.CardType) + " · 数据标记 " + (one.DataVersion == null ? "<无>" : one.DataVersion));
            Console.WriteLine("图片区     : " + one.ImageEnd.ToString("N0") + " 字节 · 脸图 " + one.FaceSize.ToString("N0") + " 字节 @" + one.FaceOffset.ToString("N0"));
            Console.WriteLine("块表       : " + one.TableSize.ToString("N0") + " 字节 @" + one.TableAt.ToString("N0") + " · 载荷起点 " + one.PayloadStart.ToString("N0") + " · 载荷 " + one.PayloadSize.ToString("N0") + " 字节");
            foreach (CardEditBlock b in one.Blocks)
            {
                Console.WriteLine("  " + b.Name.PadRight(12) + " v" + b.Version + "  pos=" + b.Pos.ToString("N0") + "  size=" + b.Size.ToString("N0"));
            }
            if (cur.Error != null)
            {
                Console.WriteLine("字段读取失败：" + cur.Error);
                return 3;
            }
            string pn = Personality.NameOf(cur.Personality);
            Console.WriteLine("字段       : 姓「" + cur.LastName + "」 名「" + cur.FirstName + "」 爱称「" + cur.NickName + "」");
            Console.WriteLine("性格       : " + cur.Personality + " " + (pn == null ? "(未收录——表外 ID)" : pn));
            Console.WriteLine("值区间     : 姓 @" + cur.LastNameAt.ToString("N0") + " +" + cur.LastNameLen
                + " · 名 @" + cur.FirstNameAt.ToString("N0") + " +" + cur.FirstNameLen
                + " · 爱称 @" + cur.NickNameAt.ToString("N0") + " +" + cur.NickNameLen
                + " · 性格 @" + cur.PersonalityAt.ToString("N0") + " +" + cur.PersonalityLen);
            string wpn = WeakPoint.NameOf(cur.WeakPoint);
            Console.WriteLine("敏感带     : " + cur.WeakPoint + " " + (wpn == null ? "(未收录——表外 ID)" : wpn)
                + " · 值区间 @" + cur.WeakPointAt.ToString("N0") + " +" + cur.WeakPointLen);
            StringBuilder den = new StringBuilder();
            for (int i = 0; i < CardEdit.DenialKeys.Length; i = i + 1)
            {
                if (i > 0)
                {
                    den.Append(" · ");
                }
                den.Append("接受「" + CardEdit.DenialNames[i] + "」");
                if (cur.Denial[i])
                {
                    den.Append("是");
                }
                else
                {
                    den.Append("否");
                }
                den.Append(" @" + cur.DenialAt[i].ToString("N0") + " +" + cur.DenialLen[i]);
            }
            Console.WriteLine("是否接受   : " + den.ToString());
            return 0;
        }
        /// <summary>改卡片字段（姓 / 名 / 爱称 / 性格）——原版留档到 --archive 目录后最小改动写回。</summary>
        private static int CardEditCommand(List<string> rest)
        {
            RequireArgs(rest, "card-edit <card.png> [--last X] [--first Y] [--nick Z] [--personality N] [--weak-point N] [--denial <键>=<yes|no>] [--archive <dir>]", 1);
            string path = rest[0];
            string archive = Take(rest, "--archive");
            CardParamEdit edit = new CardParamEdit();
            edit.LastName = Take(rest, "--last");
            edit.FirstName = Take(rest, "--first");
            edit.NickName = Take(rest, "--nick");
            string pers = Take(rest, "--personality");
            if (pers != null)
            {
                edit.Personality = int.Parse(pers);
            }
            string weak = Take(rest, "--weak-point");
            if (weak != null)
            {
                edit.WeakPoint = int.Parse(weak);
            }
            List<string> denials = TakeAll(rest, "--denial");
            if (denials.Count > 0)
            {
                bool?[] deny = new bool?[CardEdit.DenialKeys.Length];
                foreach (string d in denials)
                {
                    int eq = d.IndexOf('=');
                    if (eq <= 0)
                    {
                        Console.WriteLine("--denial 参数格式应为 <键>=<yes|no>：" + d);
                        return 2;
                    }
                    string key = d.Substring(0, eq);
                    string val = d.Substring(eq + 1);
                    int slot = DenialIndexOf(key);
                    if (slot < 0)
                    {
                        Console.WriteLine("未知的「是否接受」键：" + key + "（可用：" + string.Join(" ", CardEdit.DenialKeys) + "）");
                        return 2;
                    }
                    if (val == "yes" || val == "true")
                    {
                        deny[slot] = true;
                    }
                    else if (val == "no" || val == "false")
                    {
                        deny[slot] = false;
                    }
                    else
                    {
                        Console.WriteLine("--denial 的值应为 yes 或 no：" + d);
                        return 2;
                    }
                }
                edit.Denial = deny;
            }
            CardLayout layout = CardEdit.Parse(path);
            if (layout.Error != null)
            {
                Console.WriteLine("布局解析失败：" + layout.Error);
                return 3;
            }
            CardParamInfo cur = CardEdit.ReadParams(path, layout);
            if (cur.Error != null)
            {
                Console.WriteLine("字段读取失败：" + cur.Error);
                return 3;
            }
            Console.WriteLine("编辑前     : " + CardFieldSummary(cur));
            CardEditResult r = CardEdit.Apply(path, layout, cur, edit, archive);
            if (!r.Ok)
            {
                Console.WriteLine("编辑失败   : " + r.Error);
                return 3;
            }
            Console.WriteLine("改动       : " + string.Join(" · ", r.Changes));
            Console.WriteLine("字节增量   : " + r.Delta + "（新大小 " + r.NewSize.ToString("N0") + " 字节）");
            if (r.ArchivedFile != null)
            {
                Console.WriteLine("留档       : " + r.ArchivedFile + "（" + r.ArchivedSize.ToString("N0") + " 字节）");
            }
            CardLayout after = CardEdit.Parse(path);
            CardParamInfo now = CardEdit.ReadParams(path, after);
            Console.WriteLine("编辑后     : " + CardFieldSummary(now));
            Console.WriteLine("结构复验   : 块表 " + after.Blocks.Count + " 条 · 载荷 " + after.PayloadSize.ToString("N0") + " 字节 · 载荷起点 " + after.PayloadStart.ToString("N0"));
            foreach (CardEditBlock b in after.Blocks)
            {
                Console.WriteLine("  " + b.Name.PadRight(12) + " pos=" + b.Pos.ToString("N0") + "  size=" + b.Size.ToString("N0"));
            }
            return 0;
        }
    }
}
