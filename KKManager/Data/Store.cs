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

        /// <summary>游戏内角色名（读自卡片数据区；null / 空串 = 未读到或卡片没有名字——排序时排最后）。</summary>
        public string CharaName { get; set; }
        /// <summary>timeline 长度（秒）——只有场景卡（sd）有；null = 未读过或卡片没有 timeline，排序时恒排组内最后。</summary>
        public double? TimelineSeconds { get; set; }

        /// <summary>是否有缩略图。</summary>
        public bool HasThumb { get; set; }
    }

    /// <summary>非卡 / 非 mod 文件行（各库本地表 non_card）——扫描判定为「不是卡片也不是 mod」的文件，供面板清单与一键搬到缓存库。</summary>
    public class NonCardRow
    {
        /// <summary>本库内自增 id（缩略图与搬运动作按它定位）。</summary>
        public long Id { get; set; }

        /// <summary>文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>所属库根路径。</summary>
        public string RootPath { get; set; }

        /// <summary>来源侧——card（卡片库） / mod（mod 库）。</summary>
        public string Side { get; set; }

        /// <summary>判定理由（人读文案，如「无数据区（纯图片）」）。</summary>
        public string Reason { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间（ISO）。</summary>
        public string Mtime { get; set; }

        /// <summary>是否有缩略图（图片文件才有）。</summary>
        public bool HasThumb { get; set; }

        /// <summary>已搬到缓存库后的新路径（空 = 尚未搬走）。</summary>
        public string MovedTo { get; set; }

        /// <summary>库位序号（跨库合并时填——0 = 主库文件；≥1 = 各附加库文件）。</summary>
        public int Lib { get; set; }
    }

    /// <summary>卡片 timeline 缓存行（卡片库表 card_timeline）——按卡片 id 存，size + mtime 变化即失效。</summary>
    public class CardTimelineRow
    {
        /// <summary>卡片 id。</summary>
        public long CardId { get; set; }

        /// <summary>卡片文件路径。</summary>
        public string FilePath { get; set; }

        /// <summary>建档时的文件字节数（失效判据）。</summary>
        public long Size { get; set; }

        /// <summary>建档时的修改时间（失效判据）。</summary>
        public string Mtime { get; set; }

        /// <summary>是否存在 timeline 条目。</summary>
        public bool HasEntry { get; set; }

        /// <summary>是否空时间轴。</summary>
        public bool IsEmpty { get; set; }

        /// <summary>timeline 长度（秒，原值）。</summary>
        public double Duration { get; set; }

        /// <summary>时间缩放（Unity Time.timeScale；1 = 原速）。</summary>
        public double TimeScale { get; set; }

        /// <summary>关键帧数。</summary>
        public int Keyframes { get; set; }

        /// <summary>sceneInfo XML 字节数。</summary>
        public long XmlLength { get; set; }

        /// <summary>读取时刻。</summary>
        public string ReadAt { get; set; }

        /// <summary>读取失败原因。</summary>
        public string Error { get; set; }
    }

    /// <summary>引用卡片轻量行——重复副本组标题行的缩略图区用（按 guid 批量取，不走四色聚合）。</summary>
    public class CardRefRow
    {
        /// <summary>被引用的 mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>卡片 id。</summary>
        public long Id { get; set; }

        /// <summary>文件名。</summary>
        public string FileName { get; set; }

        /// <summary>所属库根路径。</summary>
        public string RootPath { get; set; }

        /// <summary>相对库根的文件夹（根目录为空串）。</summary>
        public string Folder { get; set; }

        /// <summary>是否有缩略图。</summary>
        public bool HasThumb { get; set; }

        /// <summary>卡类型（sd = Studio 场景卡——缩略图按 16:9 横版显示）。</summary>
        public string CardType { get; set; }
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
        /// <summary>被引用的人物卡张数。</summary>
        public long UsedChara { get; set; }
        /// <summary>被引用的服装卡张数。</summary>
        public long UsedClothes { get; set; }
        /// <summary>被引用的场景卡（sd）张数。</summary>
        public long UsedSd { get; set; }

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

    /// <summary>按作者整理计划的头——一条计划对应一次「范围 → 平铺预览」的快照。</summary>
    public class SortPlanRow
    {
        /// <summary>计划 id。</summary>
        public long Id { get; set; }

        /// <summary>范围——参与整理的库根路径（换行分隔）。</summary>
        public string Scope { get; set; }

        /// <summary>生成时刻。</summary>
        public string CreatedAt { get; set; }

        /// <summary>条目总数。</summary>
        public long ItemCount { get; set; }

        /// <summary>冲突条目数（目标路径已存在 / 计划内撞车）——必须清零才能执行。</summary>
        public long ConflictCount { get; set; }

        /// <summary>计划状态：building 生成中 / ready 可用 / outdated 范围变更后过期。</summary>
        public string State { get; set; }

        /// <summary>备注（生成失败原因 / 提示）。</summary>
        public string Note { get; set; }
    }

    /// <summary>按作者整理计划的一条条目——一个 mod 文件（现路径 → 目标路径）的快照。</summary>
    public class SortPlanItemRow
    {
        /// <summary>展示顺序（计划内唯一）。</summary>
        public long Seq { get; set; }

        /// <summary>所属计划 id。</summary>
        public long PlanId { get; set; }

        /// <summary>库序号（0 = 主库）。</summary>
        public int Lib { get; set; }

        /// <summary>级别（1 主库 / 2 缓存库 / 3 冷冻库）。</summary>
        public int Tier { get; set; }

        /// <summary>所在库根。</summary>
        public string RootPath { get; set; }

        /// <summary>mod guid。</summary>
        public string Guid { get; set; }

        /// <summary>作者（manifest author；空 = 未标注）。</summary>
        public string Author { get; set; }

        /// <summary>目标文件夹名（库根下的一级目录）。</summary>
        public string Folder { get; set; }

        /// <summary>现路径（快照）。</summary>
        public string SrcPath { get; set; }

        /// <summary>目标路径（快照）。</summary>
        public string DestPath { get; set; }

        /// <summary>字节数（快照）。</summary>
        public long Size { get; set; }

        /// <summary>修改时间戳（快照）。</summary>
        public string Mtime { get; set; }

        /// <summary>状态：pending 待搬 / conflict 冲突 / moved 已就位 / failed 失败 / skipped 跳过。</summary>
        public string State { get; set; }

        /// <summary>备注（冲突或失败原因）。</summary>
        public string Note { get; set; }
    }

    /// <summary>判旧搬移结果——一份副本被判为旧版后的新位置（供整理计划条目跟着改指，不靠推断）。</summary>
    public class ModDemoteMove
    {
        /// <summary>判旧前的文件路径。</summary>
        public string OldPath { get; set; }

        /// <summary>判旧后的文件路径（名字含 .old 段）。</summary>
        public string NewPath { get; set; }

        /// <summary>判旧后的文件名（含 .old 段）。</summary>
        public string NewName { get; set; }

        /// <summary>判旧后所在的库根。</summary>
        public string RootPath { get; set; }

        /// <summary>判旧后所在的级别（一律缓存库）。</summary>
        public int Tier { get; set; }
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

    /// <summary>一条文件哈希档案——重复副本面板的 MD5 缓存（按 size + mtime 判失效，变了重算）。</summary>
    public class ModHashRecord
    {
        /// <summary>文件绝对路径（主键）。</summary>
        public string FilePath { get; set; }

        /// <summary>建档时的文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>建档时的修改时间戳文本（UTC）。</summary>
        public string Mtime { get; set; }

        /// <summary>MD5（小写十六进制）。</summary>
        public string Md5 { get; set; }

        /// <summary>建档时刻（UTC）。</summary>
        public string HashedAt { get; set; }
    }

    /// <summary>一行插件记录（plugin_file 表）——一个 dll 可含多项插件（file_path + guid 唯一）。</summary>
    public class PluginRow
    {
        /// <summary>dll 绝对路径。</summary>
        public string FilePath { get; set; } = "";

        /// <summary>插件 GUID（空 = 该 dll 未解析出插件特性）。</summary>
        public string Guid { get; set; } = "";

        /// <summary>所属插件库根路径。</summary>
        public string RootPath { get; set; } = "";

        /// <summary>文件名（含扩展名）。</summary>
        public string FileName { get; set; } = "";

        /// <summary>插件显示名。</summary>
        public string Name { get; set; } = "";

        /// <summary>插件版本。</summary>
        public string Version { get; set; } = "";

        /// <summary>进程过滤（逗号分隔；空 = 所有进程都加载）。</summary>
        public string Processes { get; set; } = "";

        /// <summary>依赖的插件 GUID（逗号分隔）。</summary>
        public string Dependencies { get; set; } = "";

        /// <summary>是否 IPA 插件（旧框架）。</summary>
        public bool IsIpa { get; set; }

        /// <summary>文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>修改时间戳文本（UTC）。</summary>
        public string Mtime { get; set; } = "";

        /// <summary>程序集标题（AssemblyTitle——dll 元数据）。</summary>
        public string Title { get; set; } = "";

        /// <summary>程序集说明（AssemblyDescription——作者写的一句话用途；多数插件为空）。</summary>
        public string Description { get; set; } = "";

        /// <summary>程序集公司 / 作者署名（AssemblyCompany）。</summary>
        public string Company { get; set; } = "";

        /// <summary>程序集版权（AssemblyCopyright）。</summary>
        public string Copyright { get; set; } = "";

        /// <summary>程序集产品名（AssemblyProduct）。</summary>
        public string Product { get; set; } = "";

        /// <summary>程序集版本（InformationalVersion 优先，回落程序集版本号）。</summary>
        public string FileVersion { get; set; } = "";

        /// <summary>程序集目标框架（TargetFramework 特性）。</summary>
        public string TargetFramework { get; set; } = "";

        /// <summary>未解析出插件特性时的原因（如「未引用插件框架」——非插件 dll 用它出声）。</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>一个插件配置文件的落库行（plugin_config 表——cfg 的分节 / 选项 / 作者注释以 JSON 整段存放）。</summary>
    public class PluginConfigRow
    {
        /// <summary>cfg 绝对路径（主键）。</summary>
        public string FilePath { get; set; } = "";

        /// <summary>cfg 文件名。</summary>
        public string FileName { get; set; } = "";

        /// <summary>文件头读出的插件名。</summary>
        public string PluginName { get; set; } = "";

        /// <summary>文件头读出的插件版本。</summary>
        public string PluginVersion { get; set; } = "";

        /// <summary>文件头读出的插件 GUID（老配置可能为空）。</summary>
        public string Guid { get; set; } = "";

        /// <summary>cfg 字节数。</summary>
        public long Size { get; set; }

        /// <summary>cfg 修改时间戳文本（UTC）。</summary>
        public string Mtime { get; set; } = "";

        /// <summary>解析结果 JSON（PluginConfigFile 序列化——分节 / 选项 / 注释全在内）。</summary>
        public string Sections { get; set; } = "";

        /// <summary>分节数（列表页直接用，不必反序列化）。</summary>
        public long SectionCount { get; set; }

        /// <summary>选项数（列表页直接用）。</summary>
        public long OptionCount { get; set; }

        /// <summary>入库时刻（UTC）。</summary>
        public string ReadAt { get; set; } = "";

        /// <summary>解析失败原因（空 = 成功）。</summary>
        public string Error { get; set; } = "";
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

    /// <summary>重复副本组的引用卡片——总数 + 前几张（组标题行缩略图区；批量查询，不走四色聚合）。</summary>
    public class DupCardRefs
    {
        /// <summary>引用该 guid 的卡片总数（跨库）。</summary>
        public long Total { get; set; }

        /// <summary>前几张引用卡片（按库根 / 文件夹 / 文件名排序）。</summary>
        public List<CardRow> Top { get; } = new List<CardRow>();
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

    /// <summary>待办条目——需要使用者处理的一件事（软件登记的 `offline` / 使用者手输的 `manual` / 卡片上挂的 `card`）。</summary>
    public class TodoRow
    {
        /// <summary>条目 id（关闭用）。</summary>
        public long Id { get; set; }

        /// <summary>类型——`offline` = 库根被判为离线（库根路径失效或为空）· `manual` = 使用者在待办清单里手输 · `card` = 使用者在卡片引用明细里挂的待办。</summary>
        public string Kind { get; set; }

        /// <summary>关联键——离线库类型下是库根路径（小写归一）· 手输类型下是正文（去首尾空白）· 卡片类型下是卡片文件绝对路径（小写归一）。</summary>
        public string Key { get; set; }

        /// <summary>待办正文（手输 / 卡片两类；离线库类型为空——正文由库根备注承担）。</summary>
        public string Text { get; set; }

        /// <summary>关联对象路径（卡片待办 = 卡片文件绝对路径；其余为空）。</summary>
        public string RefPath { get; set; }

        /// <summary>关联对象显示名（卡片待办 = 卡片文件名；其余为空）。</summary>
        public string RefName { get; set; }

        /// <summary>登记时刻。</summary>
        public string CreatedAt { get; set; }
    }

    /// <summary>一条「已完成步骤」记录——扫描跳步判据 = size + mtime 双等。</summary>
    public class ScanStepRow
    {
        /// <summary>步骤 id（如 row / head / thumb / refs / name / timeline / coord / detail / scene / modrow / composition）。</summary>
        public string Step { get; set; }

        /// <summary>登记时的文件字节数。</summary>
        public long Size { get; set; }

        /// <summary>登记时的文件修改时间戳（UTC 文本）。</summary>
        public string Mtime { get; set; }
    }

    /// <summary>一份卡片分项分析（服装槽位 / 卡片分析 / 场景深度）——data 为 JSON 文本，前端按 kind 反序列化后复用原渲染。</summary>
    public class CardAnalysisRow
    {
        /// <summary>分析类型（coord / detail / scene）。</summary>
        public string Kind { get; set; }

        /// <summary>读时的卡片文件路径。</summary>
        public string FilePath { get; set; }

        /// <summary>读时的文件字节数（失效判据之一）。</summary>
        public long Size { get; set; }

        /// <summary>读时的修改时间戳（失效判据之一）。</summary>
        public string Mtime { get; set; }

        /// <summary>分析结果 JSON。</summary>
        public string Data { get; set; }

        /// <summary>落库时刻。</summary>
        public string ReadAt { get; set; }
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

        /// <summary>打开（不存在则建）数据库；结构版本不符时重建。corePath 非空表示本库为「库文件」——ATTACH 主库为 core，设置表与 mod 主表由主库托管。schemaReady = 结构已就绪（预建分片库的复制件）——跳过建表，只开连接与 PRAGMA。</summary>
        public Store(string dbPath, string corePath = null, bool schemaReady = false)
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
            // Pooling=false：并行扫描时多个 Store 连接必须各自持有独立底层连接——默认连接池会把相同连接串
            // 的 SqliteConnection 复用成同一个底层连接，第二个连接的 ATTACH core 会撞「database core is already in use」
            _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, Pooling = false }.ToString());
            _conn.Open();
            if (schemaReady)
            {
                // 预建分片库的复制件——结构已就绪（模板建过一次），只补连接级 PRAGMA
                Exec("PRAGMA synchronous=NORMAL");
                Exec("PRAGMA busy_timeout=15000");
            }
            else
            {
                EnsureSchema();
            }
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

        /// <summary>取文件的创建时间戳文本（UTC）——重复副本面板展示用。</summary>
        public static string CreatedStampOf(FileInfo fi)
        {
            return fi.CreationTimeUtc.ToString(StampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>可读文本类条目的扩展名（组成档案顺带捞内容做悬停预览——二进制大件不碰）。</summary>
        private static readonly string[] TextEntryExts = { ".csv", ".xml", ".txt", ".json", ".ini", ".yml", ".yaml", ".md", ".list" };

        /// <summary>单条目文本入库上限（字符）——超长截断并留标记，避免档案表膨胀。</summary>
        private const int TextEntryLimit = 20000;

        /// <summary>待办正文长度上限（字符）——手输与卡片待办共用；超限在入口拒绝，不静默截断。</summary>
        public const int TodoTextLimit = 200;

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
            // 本机实测：单次提交的 flush ≈ 0.4–0.5 s（D: 卷）——WAL + NORMAL 只在校验点 fsync，提交不再逐次 fsync
            Exec("PRAGMA synchronous=NORMAL");
            // 并行扫描：多连接写同一库（WAL 单写者模型）——写锁等待而非立刻 SQLITE_BUSY
            Exec("PRAGMA busy_timeout=15000");
            long version = Convert.ToInt64(ExecScalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            // 🔴 建表 / 建索引整段一次提交——本机实测单次提交 ≈ 0.5 s（DDL 各自提交时 30 条 = 15 s 级）；
            // 分片库并行曾因此被判「卡死」（每 worker 一个库，建库 25–40 s）
            Begin();
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
                             mod_count INTEGER, thumb BLOB, scan_time TEXT, error TEXT, chara_name TEXT)");
            // 增量补列：旧库（本版之前建的 card 表）没有 chara_name 列——按列存在性判定后补上，不重建整库
            if (Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM pragma_table_info('card') WHERE name='chara_name'"), CultureInfo.InvariantCulture) == 0)
            {
                Exec("ALTER TABLE card ADD COLUMN chara_name TEXT");
            }
            Exec(@"CREATE TABLE IF NOT EXISTS card_mod(
                             card_id INTEGER, mod_guid TEXT,
                             property TEXT, slot INTEGER, local_slot INTEGER, category_no INTEGER,
                             rec_name TEXT, rec_author TEXT, rec_website TEXT,
                             PRIMARY KEY(card_id, property, slot, local_slot, mod_guid))");
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mod_guid ON card_mod(mod_guid)");
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mod_card ON card_mod(card_id)");
            Exec(@"CREATE TABLE IF NOT EXISTS card_timeline(
                             card_id INTEGER PRIMARY KEY, file_path TEXT, size INTEGER, mtime TEXT,
                             has_entry INTEGER, is_empty INTEGER, duration REAL, time_scale REAL,
                             keyframes INTEGER, xml_length INTEGER, hit_stage TEXT, read_at TEXT, error TEXT)");
            Exec(@"CREATE TABLE IF NOT EXISTS non_card(
                             id INTEGER PRIMARY KEY AUTOINCREMENT,
                             file_path TEXT UNIQUE, root_path TEXT, side TEXT, reason TEXT,
                             size INTEGER, mtime TEXT, thumb BLOB,
                             moved_to TEXT, moved_at TEXT, scan_time TEXT)");
            Exec(@"CREATE TABLE IF NOT EXISTS scan_state(
                             file_path TEXT NOT NULL, step TEXT NOT NULL,
                             size INTEGER, mtime TEXT, done_at TEXT,
                             PRIMARY KEY(file_path, step))");
            Exec(@"CREATE TABLE IF NOT EXISTS card_analysis(
                             card_id INTEGER NOT NULL, kind TEXT NOT NULL,
                             file_path TEXT, size INTEGER, mtime TEXT, data TEXT, read_at TEXT,
                             PRIMARY KEY(card_id, kind))");
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
                // 增量迁移（v0.20.2）：手动待办要正文 / 关联对象，且卡片待办允许一卡多条——
                // 旧表带表级 UNIQUE(kind, key)（SQLite 删不掉表级约束），故建新表搬运后重建，
                // 唯一性只在 offline 上保留（部分唯一索引）；manual 的重复由应用层出声拒绝
                if (Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM pragma_table_info('todo') WHERE name='text'"), CultureInfo.InvariantCulture) == 0)
                {
                    MigrateTodoTable();
                }
                Exec(@"CREATE TABLE IF NOT EXISTS mod_u3d(
                                 guid TEXT, entry_path TEXT,
                                 file_path TEXT, size INTEGER, mtime TEXT,
                                 texture_count INTEGER, textures TEXT, class_summary TEXT,
                                 parsed_at TEXT,
                                 PRIMARY KEY(guid, entry_path))");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_hash(
                                 file_path TEXT PRIMARY KEY, size INTEGER, mtime TEXT,
                                 md5 TEXT, hashed_at TEXT)");
                Exec(@"CREATE TABLE IF NOT EXISTS plugin_file(
                                 file_path TEXT NOT NULL, guid TEXT NOT NULL,
                                 root_path TEXT, file_name TEXT, name TEXT, version TEXT,
                                 processes TEXT, dependencies TEXT, is_ipa INTEGER,
                                 size INTEGER, mtime TEXT, scan_time TEXT,
                                 title TEXT, description TEXT, company TEXT, copyright TEXT,
                                 product TEXT, file_version TEXT, target_framework TEXT, note TEXT,
                                 PRIMARY KEY(file_path, guid))");
                Exec("CREATE INDEX IF NOT EXISTS ix_plugin_guid ON plugin_file(guid)");
                // 增量补列（v0.20.10）：dll 元数据扩项——旧库按列存在性逐列补，不重建整库
                EnsureColumn("plugin_file", "title", "TEXT");
                EnsureColumn("plugin_file", "description", "TEXT");
                EnsureColumn("plugin_file", "company", "TEXT");
                EnsureColumn("plugin_file", "copyright", "TEXT");
                EnsureColumn("plugin_file", "product", "TEXT");
                EnsureColumn("plugin_file", "file_version", "TEXT");
                EnsureColumn("plugin_file", "target_framework", "TEXT");
                EnsureColumn("plugin_file", "note", "TEXT");
                Exec(@"CREATE TABLE IF NOT EXISTS plugin_config(
                                 file_path TEXT PRIMARY KEY, file_name TEXT,
                                 plugin_name TEXT, plugin_version TEXT, guid TEXT,
                                 size INTEGER, mtime TEXT, sections TEXT,
                                 section_count INTEGER, option_count INTEGER,
                                 read_at TEXT, error TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS ix_plugin_config_guid ON plugin_config(guid)");
                Exec(@"CREATE TABLE IF NOT EXISTS card_edit(
                                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                                 card_path TEXT, card_name TEXT, lib INTEGER,
                                 archived_file TEXT, archived_size INTEGER, archived_mtime TEXT,
                                 changes TEXT, edited_at TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS ix_card_edit_path ON card_edit(card_path)");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_sort_plan(
                                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                                 scope TEXT, created_at TEXT, item_count INTEGER, conflict_count INTEGER,
                                 state TEXT, note TEXT)");
                Exec(@"CREATE TABLE IF NOT EXISTS mod_sort_plan_item(
                                 seq INTEGER, plan_id INTEGER, lib INTEGER, tier INTEGER, root_path TEXT,
                                 guid TEXT, author TEXT, folder TEXT,
                                 src_path TEXT, dest_path TEXT, size INTEGER, mtime TEXT,
                                 state TEXT, note TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS ix_sort_item_plan ON mod_sort_plan_item(plan_id)");
                Exec("CREATE INDEX IF NOT EXISTS ix_sort_item_src ON mod_sort_plan_item(plan_id, src_path)");
            }
            Exec("CREATE INDEX IF NOT EXISTS ix_card_mtime ON card(mtime)");
            Exec("PRAGMA user_version=" + SchemaVersion);
            Commit();
        }

        /// <summary>把 todo 表迁到 v0.20.2 结构——补 text / ref_path / ref_name 三列，并把「同类型同键只留一条」的唯一性缩到只对 offline 生效（卡片待办允许一卡多条）。行数不一致即抛——失败可见，不静默丢待办。</summary>
        private void MigrateTodoTable()
        {
            long before = Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM todo"), CultureInfo.InvariantCulture);
            // 迁移可能被 EnsureSchema 的建表事务包住——外层已有事务时不另开（Begin 会覆盖 _tx）
            bool own = _tx == null;
            if (own)
            {
                Begin();
            }
            try
            {
                Exec(@"CREATE TABLE todo_new(
                                         id INTEGER PRIMARY KEY AUTOINCREMENT,
                                         kind TEXT, key TEXT, text TEXT, ref_path TEXT, ref_name TEXT, created_at TEXT)");
                Exec("INSERT INTO todo_new(id, kind, key, created_at) SELECT id, kind, key, created_at FROM todo");
                long after = Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM todo_new"), CultureInfo.InvariantCulture);
                if (after != before)
                {
                    throw new InvalidOperationException("todo 表迁移行数不一致：" + before + " → " + after);
                }
                Exec("DROP TABLE todo");
                Exec("ALTER TABLE todo_new RENAME TO todo");
                Exec("CREATE UNIQUE INDEX IF NOT EXISTS ux_todo_offline ON todo(kind, key) WHERE kind = 'offline'");
                if (own)
                {
                    Commit();
                }
            }
            catch
            {
                if (own)
                {
                    Rollback();
                }
                throw;
            }
        }

        private void DropAll()
        {
            Exec("DROP TABLE IF EXISTS card_mod");
            Exec("DROP TABLE IF EXISTS card");
            Exec("DROP TABLE IF EXISTS card_analysis");
            Exec("DROP TABLE IF EXISTS scan_state");
            Exec("DROP TABLE IF EXISTS mod_file");
            Exec("DROP TABLE IF EXISTS plugin_file");
            Exec("DROP TABLE IF EXISTS plugin_config");
            if (_isCore)
            {
                Exec("DROP TABLE IF EXISTS mod_old");
                Exec("DROP TABLE IF EXISTS mod_composition");
                Exec("DROP TABLE IF EXISTS mod_u3d");
                Exec("DROP TABLE IF EXISTS mod_hash");
                Exec("DROP TABLE IF EXISTS todo");
                Exec("DROP TABLE IF EXISTS mod");
                Exec("DROP TABLE IF EXISTS setting");
            }
        }

        /// <summary>按列存在性补列（增量迁移——旧库不重建整库；列已在则不动）。</summary>
        /// <param name="table">表名。</param>
        /// <param name="column">列名。</param>
        /// <param name="type">列类型声明。</param>
        private void EnsureColumn(string table, string column, string type)
        {
            long has = Convert.ToInt64(ExecScalar("SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name='" + column + "'"), CultureInfo.InvariantCulture);
            if (has == 0)
            {
                Exec("ALTER TABLE " + table + " ADD COLUMN " + column + " " + type);
            }
        }

        /// <summary>开启写入事务——**deferred（BEGIN）**，不能 IMMEDIATE：分片库连接都 ATTACH 了主库，IMMEDIATE 会给全部已附加库加写锁，多分片同时开事务必撞主库锁（busy_timeout 对跨库加锁冲突不生效）；扫描事务只写自己的分片库、对主库只读。</summary>
        public void Begin()
        {
            // 🔴 必须 deferred（BEGIN）——不能 IMMEDIATE：分片库连接都 ATTACH 了主库，IMMEDIATE 会同时给
            // 全部已附加库加写锁 ⇒ 第 2 片必撞第 1 片持有的主库锁（busy_timeout 对跨库加锁冲突不生效）。
            // 扫描事务只写自己的分片库、对主库只读，deferred 下各片各写各库互不干扰。
            _tx = _conn.BeginTransaction(deferred: true);
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

        /// <summary>登记一条手输待办（正文去首尾空白后即关联键）——同内容已存在则回错误文本、不落盘（重复出声，不静默吞）。</summary>
        public string AddManualTodo(string text, string createdAt)
        {
            string body = (text ?? "").Trim();
            if (body.Length == 0)
            {
                return "待办内容不能为空";
            }
            if (body.Length > TodoTextLimit)
            {
                return "待办内容太长（上限 " + TodoTextLimit + " 字）";
            }
            long dup;
            using (SqliteCommand cmd = NewCommand("SELECT COUNT(*) FROM todo WHERE kind='manual' AND key=$k"))
            {
                cmd.Parameters.AddWithValue("$k", body);
                dup = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            if (dup > 0)
            {
                return "已经有一条一样的手输待办：" + body;
            }
            using (SqliteCommand cmd = NewCommand("INSERT INTO todo(kind,key,text,created_at) VALUES('manual',$k,$t,$c)"))
            {
                cmd.Parameters.AddWithValue("$k", body);
                cmd.Parameters.AddWithValue("$t", body);
                cmd.Parameters.AddWithValue("$c", createdAt ?? "");
                cmd.ExecuteNonQuery();
            }
            return null;
        }

        /// <summary>登记一条卡片待办（卡片路径归一后作关联键——一卡可多条，靠 id 区分）——正文为空 / 超长回错误文本。</summary>
        public string AddCardTodo(string cardPath, string cardName, string text, string createdAt)
        {
            string path = (cardPath ?? "").Trim();
            string body = (text ?? "").Trim();
            if (path.Length == 0)
            {
                return "缺少卡片路径";
            }
            if (body.Length == 0)
            {
                return "待办内容不能为空";
            }
            if (body.Length > TodoTextLimit)
            {
                return "待办内容太长（上限 " + TodoTextLimit + " 字）";
            }
            using (SqliteCommand cmd = NewCommand("INSERT INTO todo(kind,key,text,ref_path,ref_name,created_at) VALUES('card',$k,$t,$p,$n,$c)"))
            {
                cmd.Parameters.AddWithValue("$k", path.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$t", body);
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$n", cardName ?? "");
                cmd.Parameters.AddWithValue("$c", createdAt ?? "");
                cmd.ExecuteNonQuery();
            }
            return null;
        }

        /// <summary>全部待办（按登记顺序）。</summary>
        public List<TodoRow> ListTodos()
        {
            List<TodoRow> list = new List<TodoRow>();
            using (SqliteCommand cmd = NewCommand("SELECT id, kind, key, text, ref_path, ref_name, created_at FROM todo ORDER BY id"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    TodoRow t = new TodoRow();
                    t.Id = r.GetInt64(0);
                    t.Kind = r.IsDBNull(1) ? "" : r.GetString(1);
                    t.Key = r.IsDBNull(2) ? "" : r.GetString(2);
                    t.Text = r.IsDBNull(3) ? "" : r.GetString(3);
                    t.RefPath = r.IsDBNull(4) ? "" : r.GetString(4);
                    t.RefName = r.IsDBNull(5) ? "" : r.GetString(5);
                    t.CreatedAt = r.IsDBNull(6) ? "" : r.GetString(6);
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

        /// <summary>载入卡片时间戳索引——file_path → [size, mtime, 是否待补角色名, 卡类型, 是否待补 timeline, 卡片 id, 图片区结束偏移]（第三项 "1" = 人物卡且名字尚未读过，扫描时补读；第四项 = 库里记的卡类型，供存量类型补正；第五项 "1" = 场景卡且 timeline 尚无有效缓存，扫描时补读；后两项供补读落表用）。</summary>
        public Dictionary<string, string[]> LoadCardStamps()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand(@"SELECT c.file_path, c.size, c.mtime,
                             (c.chara_name IS NULL AND IFNULL(c.card_type,'') LIKE '%Chara%'), c.card_type,
                             CASE WHEN c.card_type = 'sd' AND (t.card_id IS NULL OR t.size <> c.size OR IFNULL(t.mtime,'') <> IFNULL(c.mtime,'')) THEN 1 ELSE 0 END,
                             c.id, c.image_end
                             FROM card c LEFT JOIN card_timeline t ON t.card_id = c.id"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = new[]
                    {
                                r.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                                r.IsDBNull(2) ? "" : r.GetString(2),
                                r.GetInt64(3) != 0 ? "1" : "0",
                                r.IsDBNull(4) ? "" : r.GetString(4),
                                r.GetInt64(5) != 0 ? "1" : "0",
                                r.GetInt64(6).ToString(CultureInfo.InvariantCulture),
                                r.IsDBNull(7) ? "0" : r.GetInt64(7).ToString(CultureInfo.InvariantCulture)
                            };
                }
            }
            return map;
        }
        /// <summary>写入一张卡片的角色名（存量补名用；name 为 null 表示没读到——落 NULL，下次扫描再试）。</summary>
        public void UpdateCardName(string filePath, string name)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE card SET chara_name=$name WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$name", name == null ? (object)DBNull.Value : name);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>改正一张卡片的类型与数据版本（存量补正用——本版之前场景卡的 card_type 记成了数据区头段的乱码）。</summary>
        public void UpdateCardType(string filePath, string cardType, string dataVersion)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE card SET card_type=$type, data_version=$ver WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$type", cardType == null ? (object)DBNull.Value : cardType);
                cmd.Parameters.AddWithValue("$ver", dataVersion == null ? (object)DBNull.Value : dataVersion);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>扫描「卡片行」步——只写文件与归属（不碰画像字段与缩略图）；已存在的行只刷新 size / mtime / 归属。返回卡片 id。</summary>
        public long UpsertCardRow(string filePath, string fileName, long size, string mtime, RootEntry root, string folder)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO card(file_path,file_name,size,mtime,tier,root_path,folder,scan_time)
                     VALUES($path,$file,$size,$mtime,$tier,$root,$folder,$now)
                     ON CONFLICT(file_path) DO UPDATE SET
                       file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
                       tier=excluded.tier, root_path=excluded.root_path, folder=excluded.folder,
                       scan_time=excluded.scan_time"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$file", fileName);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$tier", root.tier);
                cmd.Parameters.AddWithValue("$root", root.path);
                cmd.Parameters.AddWithValue("$folder", folder ?? "");
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }

            using (SqliteCommand cmd = NewCommand("SELECT id FROM card WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>扫描「卡头段」步——写卡类型 / 数据版本 / 图片区终点。</summary>
        public void UpdateCardHead(string filePath, string cardType, string dataVersion, long imageEnd)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE card SET card_type=$type, data_version=$ver, image_end=$img WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$type", cardType == null ? (object)DBNull.Value : cardType);
                cmd.Parameters.AddWithValue("$ver", dataVersion == null ? (object)DBNull.Value : dataVersion);
                cmd.Parameters.AddWithValue("$img", imageEnd);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>扫描「声明区」步——刷新引用计数与 UAR 块数（引用行由 ReplaceCardRefs 另行替换）。</summary>
        public void UpdateCardRefsMeta(string filePath, int modCount, int uarBlocks)
        {
            using (SqliteCommand cmd = NewCommand("UPDATE card SET mod_count=$cnt, uar_blocks=$uar WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$cnt", modCount);
                cmd.Parameters.AddWithValue("$uar", uarBlocks);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>扫描「缩略图」步——写缩略图（thumb 为 null 表示本次没生成，保留原值不清空）。</summary>
        public void UpdateCardThumb(string filePath, byte[] thumb)
        {
            if (thumb == null)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("UPDATE card SET thumb=$thumb WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$thumb", thumb);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>读一张卡片的 timeline 缓存（按 card_id；没有返回 null）。</summary>
        public CardTimelineRow GetCardTimeline(long cardId)
        {
            using (SqliteCommand cmd = NewCommand(@"SELECT card_id, file_path, size, mtime, has_entry, is_empty,
                     duration, time_scale, keyframes, xml_length, read_at, error FROM card_timeline WHERE card_id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return null;
                    }
                    return new CardTimelineRow
                    {
                        CardId = r.GetInt64(0),
                        FilePath = r.IsDBNull(1) ? null : r.GetString(1),
                        Size = r.GetInt64(2),
                        Mtime = r.IsDBNull(3) ? null : r.GetString(3),
                        HasEntry = !r.IsDBNull(4) && r.GetInt64(4) != 0,
                        IsEmpty = !r.IsDBNull(5) && r.GetInt64(5) != 0,
                        Duration = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                        TimeScale = r.IsDBNull(7) ? 1 : r.GetDouble(7),
                        Keyframes = r.IsDBNull(8) ? 0 : (int)r.GetInt64(8),
                        XmlLength = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                        ReadAt = r.IsDBNull(10) ? null : r.GetString(10),
                        Error = r.IsDBNull(11) ? null : r.GetString(11)
                    };
                }
            }
        }

        /// <summary>写一张卡片的 timeline 缓存（按 card_id 覆盖；size + mtime 是失效判据）。</summary>
        public void SaveCardTimeline(long cardId, string filePath, long size, string mtime, TimelineInfo t)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO card_timeline(card_id,file_path,size,mtime,has_entry,is_empty,
                     duration,time_scale,keyframes,xml_length,hit_stage,read_at,error)
                     VALUES($id,$path,$size,$mtime,$has,$empty,$dur,$scale,$keys,$xml,$stage,$now,$err)
                     ON CONFLICT(card_id) DO UPDATE SET
                       file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
                       has_entry=excluded.has_entry, is_empty=excluded.is_empty, duration=excluded.duration,
                       time_scale=excluded.time_scale, keyframes=excluded.keyframes, xml_length=excluded.xml_length,
                       hit_stage=excluded.hit_stage, read_at=excluded.read_at, error=excluded.error"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.Parameters.AddWithValue("$path", filePath == null ? (object)DBNull.Value : filePath);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime == null ? (object)DBNull.Value : mtime);
                cmd.Parameters.AddWithValue("$has", t != null && t.HasEntry ? 1 : 0);
                cmd.Parameters.AddWithValue("$empty", t != null && t.IsEmpty ? 1 : 0);
                cmd.Parameters.AddWithValue("$dur", t == null ? 0.0 : t.Duration);
                cmd.Parameters.AddWithValue("$scale", t == null ? 1.0 : t.TimeScale);
                cmd.Parameters.AddWithValue("$keys", t == null ? 0 : t.Keyframes);
                cmd.Parameters.AddWithValue("$xml", t == null ? 0 : t.XmlLength);
                cmd.Parameters.AddWithValue("$stage", t == null || t.HitStage == null ? (object)DBNull.Value : t.HitStage);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$err", t == null || t.Error == null ? (object)DBNull.Value : t.Error);
                cmd.ExecuteNonQuery();
            }
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
        /// <summary>载入「已完成步骤」索引——file_path → （步骤 id → 记录）；扫描按 step 跳步判定用（判据 = size + mtime 双等）。</summary>
        public Dictionary<string, Dictionary<string, ScanStepRow>> LoadScanSteps()
        {
            var map = new Dictionary<string, Dictionary<string, ScanStepRow>>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, step, size, mtime FROM scan_state"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string path = r.IsDBNull(0) ? "" : r.GetString(0);
                    string step = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (path.Length == 0 || step.Length == 0)
                    {
                        continue;
                    }
                    Dictionary<string, ScanStepRow> steps;
                    if (!map.TryGetValue(path, out steps))
                    {
                        steps = new Dictionary<string, ScanStepRow>(StringComparer.Ordinal);
                        map[path] = steps;
                    }
                    ScanStepRow row = new ScanStepRow();
                    row.Step = step;
                    row.Size = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    row.Mtime = r.IsDBNull(3) ? "" : r.GetString(3);
                    steps[step] = row;
                }
            }
            return map;
        }

        /// <summary>登记一步完成（同文件同步骤覆盖式更新——文件变了旧记录自然失配，不必先删）。</summary>
        public void MarkScanStep(string filePath, string step, long size, string mtime)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(step))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO scan_state(file_path,step,size,mtime,done_at)
                     VALUES($path,$step,$size,$mtime,$now)
                     ON CONFLICT(file_path,step) DO UPDATE SET
                       size=excluded.size, mtime=excluded.mtime, done_at=excluded.done_at"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$step", step);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>清扫某库根下已从磁盘消失文件的步骤记录（present 为空时不清扫——枚举失败不该误删）；返回删除条数。</summary>
        public int DeleteScanStepsMissingUnderRoot(string rootPath, HashSet<string> present)
        {
            string prefix = (rootPath ?? "").TrimEnd('\\', '/');
            if (prefix.Length == 0 || present == null || present.Count == 0)
            {
                return 0;
            }
            List<string> gone = new List<string>();
            using (SqliteCommand cmd = NewCommand("SELECT DISTINCT file_path FROM scan_state"))
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
                    if (!present.Contains(path))
                    {
                        gone.Add(path);
                    }
                }
            }
            if (gone.Count == 0)
            {
                return 0;
            }
            using (SqliteCommand del = NewCommand("DELETE FROM scan_state WHERE file_path=$p"))
            {
                SqliteParameter p = del.Parameters.Add("$p", SqliteType.Text);
                foreach (string path in gone)
                {
                    p.Value = path;
                    del.ExecuteNonQuery();
                }
            }
            return gone.Count;
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
                     card_type,data_version,image_end,uar_blocks,mod_count,thumb,scan_time,error,chara_name)
                     VALUES($path,$file,$size,$mtime,$tier,$root,$folder,$type,$ver,$img,$uar,$cnt,$thumb,$now,NULL,$chara)
                     ON CONFLICT(file_path) DO UPDATE SET
                       file_name=excluded.file_name, size=excluded.size, mtime=excluded.mtime,
                       tier=excluded.tier, root_path=excluded.root_path, folder=excluded.folder,
                       card_type=excluded.card_type, data_version=excluded.data_version, image_end=excluded.image_end,
                       uar_blocks=excluded.uar_blocks, mod_count=excluded.mod_count, scan_time=excluded.scan_time,
                       thumb=COALESCE(excluded.thumb, card.thumb), chara_name=excluded.chara_name"))
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
                cmd.Parameters.AddWithValue("$chara", c.CharaName == null ? (object)DBNull.Value : c.CharaName);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }

            using (SqliteCommand cmd = NewCommand("SELECT id FROM card WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$path", c.FilePath);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>载入 mod 副本的 guid 索引——file_path → guid（扫描组装内存态用）。</summary>
        public Dictionary<string, string> LoadModFileGuids()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, guid FROM mod_file"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string path = r.IsDBNull(0) ? "" : r.GetString(0);
                    string guid = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (path.Length > 0 && guid.Length > 0)
                    {
                        map[path] = guid;
                    }
                }
            }
            return map;
        }

        /// <summary>按路径取卡片 id（没有返回 0）。</summary>
        public long CardIdOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return 0;
            }
            using (SqliteCommand cmd = NewCommand("SELECT id FROM card WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                object v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value)
                {
                    return 0;
                }
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>按路径删除一张卡片（含引用行与分项分析行）——头段步判定为非卡时用。</summary>
        public void DeleteCardByPath(string filePath)
        {
            long id = CardIdOf(filePath);
            if (id <= 0)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM card_mod WHERE card_id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM card_analysis WHERE card_id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM card WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>登记一个非卡 / 非 mod 文件（已在表中则更新理由与体积，保留「已搬走」标记与缩略图）——卡片头段步 / mod 行步判定为非卡时用。</summary>
        public void UpsertNonCard(string filePath, string rootPath, string side, string reason, long size, string mtime, byte[] thumb)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO non_card(file_path,root_path,side,reason,size,mtime,thumb,scan_time)
                     VALUES($path,$root,$side,$reason,$size,$mtime,$thumb,$now)
                     ON CONFLICT(file_path) DO UPDATE SET
                       root_path=excluded.root_path, side=excluded.side, reason=excluded.reason,
                       size=excluded.size, mtime=excluded.mtime, scan_time=excluded.scan_time,
                       thumb=COALESCE(excluded.thumb, non_card.thumb)"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$root", rootPath ?? "");
                cmd.Parameters.AddWithValue("$side", side ?? "");
                cmd.Parameters.AddWithValue("$reason", reason ?? "");
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$thumb", (object)thumb ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按路径删除非卡登记（文件变回卡片 / 变回 mod 时核销）。</summary>
        public void DeleteNonCardByPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM non_card WHERE file_path=$path"))
            {
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>清扫某库根下已从磁盘消失的非卡登记（文件不存在即删）；返回删除条数。</summary>
        public int DeleteNonCardsMissingUnderRoot(string rootPath)
        {
            string prefix = (rootPath ?? "").TrimEnd('\\', '/');
            if (prefix.Length == 0)
            {
                return 0;
            }
            List<long> gone = new List<long>();
            using (SqliteCommand cmd = NewCommand("SELECT id, file_path FROM non_card WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string path = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (path.Length == 0 || !File.Exists(path))
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
            using (SqliteCommand del = NewCommand("DELETE FROM non_card WHERE id=$id"))
            {
                SqliteParameter p = del.Parameters.Add("$id", SqliteType.Integer);
                foreach (long id in gone)
                {
                    p.Value = id;
                    del.ExecuteNonQuery();
                }
            }
            return gone.Count;
        }

        /// <summary>读本库的非卡清单（includeMoved 为假时只列尚未搬走的）；按体积降序。</summary>
        public List<NonCardRow> ListNonCards(bool includeMoved)
        {
            List<NonCardRow> list = new List<NonCardRow>();
            string sql = "SELECT id,file_path,root_path,side,reason,size,mtime,moved_to,(thumb IS NOT NULL) FROM non_card";
            if (!includeMoved)
            {
                sql = sql + " WHERE moved_to IS NULL";
            }
            sql = sql + " ORDER BY size DESC";
            using (SqliteCommand cmd = NewCommand(sql))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(ReadNonCard(r));
                }
            }
            return list;
        }

        /// <summary>读本库的非卡条数（includeMoved 为假时只数尚未搬走的）。</summary>
        public long CountNonCards(bool includeMoved)
        {
            string sql = "SELECT COUNT(*) FROM non_card";
            if (!includeMoved)
            {
                sql = sql + " WHERE moved_to IS NULL";
            }
            return Convert.ToInt64(ExecScalar(sql), CultureInfo.InvariantCulture);
        }

        /// <summary>按 id 读一条非卡登记（搬运前定位用）；不存在返回 null。</summary>
        public NonCardRow GetNonCard(long id)
        {
            using (SqliteCommand cmd = NewCommand("SELECT id,file_path,root_path,side,reason,size,mtime,moved_to,(thumb IS NOT NULL) FROM non_card WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return ReadNonCard(r);
                    }
                }
            }
            return null;
        }

        /// <summary>读一条非卡登记的缩略图字节（无图返回 null）。</summary>
        public byte[] LoadNonCardThumb(long id)
        {
            using (SqliteCommand cmd = NewCommand("SELECT thumb FROM non_card WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read() && !r.IsDBNull(0))
                    {
                        return (byte[])r.GetValue(0);
                    }
                }
            }
            return null;
        }

        /// <summary>把一条非卡登记改指搬走后的新路径（保留「已搬走」标记与缩略图；目标路径若已有登记先删掉）。</summary>
        public void MarkNonCardMoved(long id, string dest)
        {
            if (id <= 0 || string.IsNullOrEmpty(dest))
            {
                return;
            }
            using (SqliteCommand del = NewCommand("DELETE FROM non_card WHERE file_path=$p AND id<>$id"))
            {
                del.Parameters.AddWithValue("$p", dest);
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            using (SqliteCommand cmd = NewCommand("UPDATE non_card SET file_path=$p, moved_to=$p, moved_at=$now WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$p", dest);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>把一行 non_card 读数转成行对象（列序固定：id / file_path / root_path / side / reason / size / mtime / moved_to / has_thumb）。</summary>
        private static NonCardRow ReadNonCard(SqliteDataReader r)
        {
            NonCardRow row = new NonCardRow();
            row.Id = r.GetInt64(0);
            row.FilePath = r.IsDBNull(1) ? "" : r.GetString(1);
            row.RootPath = r.IsDBNull(2) ? "" : r.GetString(2);
            row.Side = r.IsDBNull(3) ? "" : r.GetString(3);
            row.Reason = r.IsDBNull(4) ? "" : r.GetString(4);
            row.Size = r.IsDBNull(5) ? 0 : r.GetInt64(5);
            row.Mtime = r.IsDBNull(6) ? "" : r.GetString(6);
            row.MovedTo = r.IsDBNull(7) ? "" : r.GetString(7);
            row.HasThumb = !r.IsDBNull(8) && r.GetInt64(8) != 0;
            return row;
        }

        /// <summary>落一份卡片分项分析（服装槽位 / 卡片分析 / 场景深度——kind 区分，data 为 JSON 文本）。</summary>
        public void SaveCardAnalysis(long cardId, string kind, string filePath, long size, string mtime, string data)
        {
            if (cardId <= 0 || string.IsNullOrEmpty(kind))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO card_analysis(card_id,kind,file_path,size,mtime,data,read_at)
                     VALUES($id,$kind,$path,$size,$mtime,$data,$now)
                     ON CONFLICT(card_id,kind) DO UPDATE SET
                       file_path=excluded.file_path, size=excluded.size, mtime=excluded.mtime,
                       data=excluded.data, read_at=excluded.read_at"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$path", filePath);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$mtime", mtime ?? "");
                cmd.Parameters.AddWithValue("$data", (object)data ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>读一份卡片分项分析（没有返回 null）。</summary>
        public CardAnalysisRow LoadCardAnalysis(long cardId, string kind)
        {
            using (SqliteCommand cmd = NewCommand("SELECT kind,file_path,size,mtime,data,read_at FROM card_analysis WHERE card_id=$id AND kind=$kind"))
            {
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.Parameters.AddWithValue("$kind", kind);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return null;
                    }
                    CardAnalysisRow row = new CardAnalysisRow();
                    row.Kind = r.IsDBNull(0) ? "" : r.GetString(0);
                    row.FilePath = r.IsDBNull(1) ? "" : r.GetString(1);
                    row.Size = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    row.Mtime = r.IsDBNull(3) ? "" : r.GetString(3);
                    row.Data = r.IsDBNull(4) ? "" : r.GetString(4);
                    row.ReadAt = r.IsDBNull(5) ? "" : r.GetString(5);
                    return row;
                }
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

        /// <summary>分页查询卡片（filter：all / pending / ready / black / nomod / nothumb；folder：文件夹过滤；root：库根过滤；order：排序键——mtime（默认）/ size / file / chara / timeline（timeline 长度，没有 timeline 的卡恒排组内最后）/ miss（缺失严重度：黑 → 灰 → 黄 → 绿，同级按修改时间倒序，固定方向不可切）；desc：组内方向，只对可切向的键生效，mtime 与 miss 恒固定序）；size ≤ 0 = 不限条数。</summary>
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
            else if (order == "file" && desc)
            {
                orderBy = "ORDER BY c.folder, c.file_name DESC, c.id";
            }
            else if (order == "file")
            {
                orderBy = "ORDER BY c.folder, c.file_name ASC, c.id";
            }
            else if (order == "chara" && desc)
            {
                orderBy = "ORDER BY c.folder, CASE WHEN c.chara_name IS NULL OR c.chara_name = '' THEN 1 ELSE 0 END, c.chara_name DESC, c.id";
            }
            else if (order == "chara")
            {
                orderBy = "ORDER BY c.folder, CASE WHEN c.chara_name IS NULL OR c.chara_name = '' THEN 1 ELSE 0 END, c.chara_name ASC, c.id";
            }
            else if (order == "timeline" && desc)
            {
                orderBy = "ORDER BY c.folder, CASE WHEN tl.duration IS NULL THEN 1 ELSE 0 END, tl.duration DESC, c.id";
            }
            else if (order == "timeline")
            {
                orderBy = "ORDER BY c.folder, CASE WHEN tl.duration IS NULL THEN 1 ELSE 0 END, tl.duration ASC, c.id";
            }
            else if (order == "miss")
            {
                orderBy = "ORDER BY c.folder, CASE WHEN COALESCE(a.black,0) > 0 THEN 0 WHEN COALESCE(a.red,0) > 0 THEN 1 WHEN COALESCE(a.yellow,0) > 0 THEN 2 ELSE 3 END, c.mtime DESC, c.id";
            }
            string sql = ColorCte + @" SELECT c.id, c.file_name, c.card_type, c.size, c.mtime,
                     c.root_path, c.tier, c.folder, c.mod_count,
                     COALESCE(a.green,0), COALESCE(a.yellow,0), COALESCE(a.red,0), COALESCE(a.black,0),
                     (c.thumb IS NOT NULL), c.chara_name,
                     CASE WHEN tl.has_entry = 1 AND tl.is_empty = 0 THEN tl.duration END
                     FROM card c LEFT JOIN agg a ON a.card_id = c.id
                     LEFT JOIN card_timeline tl ON tl.card_id = c.id " + where + @" " + orderBy + @"
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
                            ModCount = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                            Green = r.GetInt64(9),
                            Yellow = r.GetInt64(10),
                            Red = r.GetInt64(11),
                            Black = r.GetInt64(12),
                            HasThumb = r.GetInt64(13) != 0,
                            CharaName = r.IsDBNull(14) ? null : r.GetString(14),
                            TimelineSeconds = r.IsDBNull(15) ? (double?)null : r.GetDouble(15)
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

        /// <summary>某库根下全部 mod 副本行（按路径升序）——按作者整理的计划来源。</summary>
        public List<ModFileRecord> QueryModFilesByRoot(string rootPath)
        {
            List<ModFileRecord> list = new List<ModFileRecord>();
            using (SqliteCommand cmd = NewCommand("SELECT file_path,guid,tier,root_path,file_name,size,mtime,scan_time FROM mod_file WHERE root_path=$r ORDER BY file_path"))
            {
                cmd.Parameters.AddWithValue("$r", rootPath ?? "");
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        ModFileRecord rec = new ModFileRecord();
                        rec.FilePath = r.IsDBNull(0) ? "" : r.GetString(0);
                        rec.Guid = r.IsDBNull(1) ? "" : r.GetString(1);
                        rec.Tier = r.IsDBNull(2) ? 0 : (int)r.GetInt64(2);
                        rec.RootPath = r.IsDBNull(3) ? "" : r.GetString(3);
                        rec.FileName = r.IsDBNull(4) ? "" : r.GetString(4);
                        rec.Size = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                        rec.Mtime = r.IsDBNull(6) ? "" : r.GetString(6);
                        rec.ScanTime = r.IsDBNull(7) ? "" : r.GetString(7);
                        list.Add(rec);
                    }
                }
            }
            return list;
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
                        ModCount = r.IsDBNull(8) ? 0 : r.GetInt64(8),
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
                            ModCount = r.IsDBNull(8) ? 0 : r.GetInt64(8),
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
        /// <summary>一批 guid 各自的引用卡片总数（单库口径——批量版；重复副本面板一次拿完，不再逐组查询）。</summary>
        public Dictionary<string, long> CountCardsByMods(List<string> guids)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (guids == null || guids.Count == 0)
            {
                return map;
            }
            // [段1] 参数化 IN 子句——一 guid 一参数，不拼字面量
            List<string> names = new List<string>();
            for (int i = 0; i < guids.Count; i = i + 1)
            {
                names.Add("$g" + i.ToString());
            }
            string sql = "SELECT mod_guid, COUNT(DISTINCT card_id) FROM card_mod WHERE mod_guid IN (" + string.Join(",", names) + ") GROUP BY mod_guid";
            using (SqliteCommand cmd = NewCommand(sql))
            {
                for (int i = 0; i < guids.Count; i = i + 1)
                {
                    cmd.Parameters.AddWithValue(names[i], guids[i]);
                }
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        map[r.GetString(0)] = r.GetInt64(1);
                    }
                }
            }
            return map;
        }
        /// <summary>一批 guid 的引用卡片轻量行（不含四色聚合——缩略图区专用；排序与截断由跨库合并方统一做，保证与「引用卡片」弹窗同一口径）。</summary>
        public List<CardRefRow> RefCardsByMods(List<string> guids)
        {
            List<CardRefRow> list = new List<CardRefRow>();
            if (guids == null || guids.Count == 0)
            {
                return list;
            }
            // [段1] 参数化 IN 子句——一 guid 一参数
            List<string> names = new List<string>();
            for (int i = 0; i < guids.Count; i = i + 1)
            {
                names.Add("$g" + i.ToString());
            }
            // [段2] 一次取全部匹配行——card_mod 先按 (guid, card_id) 去重，与计数口径一致（同一卡多行只算一次）
            string sql = @"SELECT cm.mod_guid, c.id, c.file_name, c.root_path, c.folder, (c.thumb IS NOT NULL), c.card_type
                             FROM (SELECT DISTINCT mod_guid, card_id FROM card_mod WHERE mod_guid IN (" + string.Join(",", names) + @")) cm
                             JOIN card c ON c.id = cm.card_id";
            using (SqliteCommand cmd = NewCommand(sql))
            {
                for (int i = 0; i < guids.Count; i = i + 1)
                {
                    cmd.Parameters.AddWithValue(names[i], guids[i]);
                }
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        CardRefRow row = new CardRefRow();
                        row.Guid = r.GetString(0);
                        row.Id = r.GetInt64(1);
                        row.FileName = r.GetString(2);
                        row.RootPath = r.IsDBNull(3) ? null : r.GetString(3);
                        row.Folder = r.IsDBNull(4) ? "" : r.GetString(4);
                        row.HasThumb = r.GetInt64(5) != 0;
                        row.CardType = r.IsDBNull(6) ? null : r.GetString(6);
                        list.Add(row);
                    }
                }
            }
            return list;
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
        /// <summary>哈希档案全表（主库表）——键 = 文件路径（大小写不敏感，跨库副本路径比对用）。</summary>
        public Dictionary<string, ModHashRecord> LoadModHashes()
        {
            Dictionary<string, ModHashRecord> map = new Dictionary<string, ModHashRecord>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = NewCommand("SELECT file_path, size, mtime, md5, hashed_at FROM mod_hash"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    ModHashRecord rec = new ModHashRecord();
                    rec.FilePath = r.GetString(0);
                    rec.Size = r.GetInt64(1);
                    rec.Mtime = r.IsDBNull(2) ? "" : r.GetString(2);
                    rec.Md5 = r.IsDBNull(3) ? null : r.GetString(3);
                    rec.HashedAt = r.IsDBNull(4) ? "" : r.GetString(4);
                    map[rec.FilePath] = rec;
                }
            }
            return map;
        }

        /// <summary>写入 / 更新一条哈希档案（主库表）。</summary>
        public void PutModHash(ModHashRecord rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.FilePath))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_hash(file_path,size,mtime,md5,hashed_at)
                             VALUES($path,$size,$mtime,$md5,$at)
                             ON CONFLICT(file_path) DO UPDATE SET
                               size=excluded.size, mtime=excluded.mtime, md5=excluded.md5, hashed_at=excluded.hashed_at"))
            {
                cmd.Parameters.AddWithValue("$path", rec.FilePath);
                cmd.Parameters.AddWithValue("$size", rec.Size);
                cmd.Parameters.AddWithValue("$mtime", rec.Mtime ?? "");
                cmd.Parameters.AddWithValue("$md5", (object)rec.Md5 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", string.IsNullOrEmpty(rec.HashedAt) ? Now() : rec.HashedAt);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>本库按 guid 的副本计数（总数 / 非旧版数）——重复副本组数与「待确认」组数用（跨库由 hub 合并）。
        /// 旧版判据与 RootsRules.IsOldFileName 对齐（扩展名前的 .old 段）——SQL 侧按容器扩展名枚举（.zipmod / .zip）。</summary>
        public List<ModFileCount> ListModFileCounts()
        {
            List<ModFileCount> list = new List<ModFileCount>();
            using (SqliteCommand cmd = NewCommand(@"SELECT guid, COUNT(*), SUM(CASE WHEN file_name LIKE '%.old.zipmod' OR file_name LIKE '%.old.zip' THEN 0 ELSE 1 END)
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

        /// <summary>本库里各 mod guid 被引用的卡片数——总数 + 按卡类型分列（人物卡 / 服装卡 / 场景卡 sd），供跨库合并。</summary>
        public Dictionary<string, (long Used, long Chara, long Clothes, long Sd)> RefCountsByTypeByGuid()
        {
            Dictionary<string, (long Used, long Chara, long Clothes, long Sd)> map =
                new Dictionary<string, (long Used, long Chara, long Clothes, long Sd)>(StringComparer.Ordinal);
            // 一次查询同时出总数与三类型分列——LEFT JOIN 保证卡片行缺失时不丢引用行（总数口径与本方法改造前一致）
            string sql = @"SELECT cm.mod_guid,
                     COUNT(DISTINCT cm.card_id),
                     COUNT(DISTINCT CASE WHEN c.card_type LIKE '%Chara%' THEN cm.card_id END),
                     COUNT(DISTINCT CASE WHEN c.card_type LIKE '%Clothes%' THEN cm.card_id END),
                     COUNT(DISTINCT CASE WHEN c.card_type = 'sd' THEN cm.card_id END)
                     FROM card_mod cm LEFT JOIN card c ON c.id = cm.card_id
                     GROUP BY cm.mod_guid";
            using (SqliteCommand cmd = NewCommand(sql))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
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

        /// <summary>guid → 作者（主库 mod 主表；空作者为 ""）——按作者整理的来源。</summary>
        public Dictionary<string, string> ModAuthorMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            using (SqliteCommand cmd = NewCommand("SELECT guid, COALESCE(author,'') FROM " + ModTable))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    map[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }
            }
            return map;
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

        /// <summary>建一条按作者整理计划的头（主库表），返回计划 id；非主库连接返回 0。</summary>
        public long AddSortPlan(string scope)
        {
            if (!_isCore)
            {
                return 0;
            }
            using (SqliteCommand cmd = NewCommand("INSERT INTO mod_sort_plan(scope,created_at,item_count,conflict_count,state,note) VALUES($s,$t,0,0,'building','')"))
            {
                cmd.Parameters.AddWithValue("$s", scope ?? "");
                cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
            return Convert.ToInt64(ExecScalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        }

        /// <summary>清空全部整理计划与条目（生成新计划前调用）。</summary>
        public void ClearSortPlans()
        {
            if (!_isCore)
            {
                return;
            }
            Exec("DELETE FROM mod_sort_plan_item");
            Exec("DELETE FROM mod_sort_plan");
        }

        /// <summary>写一条整理计划条目（快照）。</summary>
        public void AddSortPlanItem(SortPlanItemRow row)
        {
            if (!_isCore || row == null)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand(@"INSERT INTO mod_sort_plan_item(seq,plan_id,lib,tier,root_path,guid,author,folder,src_path,dest_path,size,mtime,state,note)
                     VALUES($seq,$pid,$lib,$tier,$root,$guid,$author,$folder,$src,$dest,$size,$mtime,$state,$note)"))
            {
                cmd.Parameters.AddWithValue("$seq", row.Seq);
                cmd.Parameters.AddWithValue("$pid", row.PlanId);
                cmd.Parameters.AddWithValue("$lib", row.Lib);
                cmd.Parameters.AddWithValue("$tier", row.Tier);
                cmd.Parameters.AddWithValue("$root", row.RootPath ?? "");
                cmd.Parameters.AddWithValue("$guid", row.Guid ?? "");
                cmd.Parameters.AddWithValue("$author", row.Author ?? "");
                cmd.Parameters.AddWithValue("$folder", row.Folder ?? "");
                cmd.Parameters.AddWithValue("$src", row.SrcPath ?? "");
                cmd.Parameters.AddWithValue("$dest", row.DestPath ?? "");
                cmd.Parameters.AddWithValue("$size", row.Size);
                cmd.Parameters.AddWithValue("$mtime", row.Mtime ?? "");
                cmd.Parameters.AddWithValue("$state", row.State ?? "");
                cmd.Parameters.AddWithValue("$note", row.Note ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>计划收尾——写状态 / 条目数 / 冲突数 / 备注。</summary>
        public void FinishSortPlan(long planId, string state, long itemCount, long conflictCount, string note)
        {
            if (!_isCore)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("UPDATE mod_sort_plan SET state=$state,item_count=$n,conflict_count=$c,note=$note WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$state", state ?? "");
                cmd.Parameters.AddWithValue("$n", itemCount);
                cmd.Parameters.AddWithValue("$c", conflictCount);
                cmd.Parameters.AddWithValue("$note", note ?? "");
                cmd.Parameters.AddWithValue("$id", planId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>改一条计划条目的状态与备注。</summary>
        public void SetSortItemState(long planId, long seq, string state, string note)
        {
            if (!_isCore)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("UPDATE mod_sort_plan_item SET state=$state,note=$note WHERE plan_id=$pid AND seq=$seq"))
            {
                cmd.Parameters.AddWithValue("$state", state ?? "");
                cmd.Parameters.AddWithValue("$note", note ?? "");
                cmd.Parameters.AddWithValue("$pid", planId);
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>改一条计划条目的所在位置（判旧后条目随文件改指新位置）——库 / 级别 / 库根 / 目标文件夹 / 现路径 / 目标路径 / 状态 / 备注一并写。</summary>
        public void SetSortItemLocation(long planId, long seq, int lib, int tier, string rootPath, string folder, string srcPath, string destPath, string state, string note)
        {
            if (!_isCore)
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("UPDATE mod_sort_plan_item SET lib=$lib,tier=$tier,root_path=$root,folder=$folder,src_path=$src,dest_path=$dest,state=$state,note=$note WHERE plan_id=$pid AND seq=$seq"))
            {
                cmd.Parameters.AddWithValue("$lib", lib);
                cmd.Parameters.AddWithValue("$tier", tier);
                cmd.Parameters.AddWithValue("$root", rootPath == null ? "" : rootPath);
                cmd.Parameters.AddWithValue("$folder", folder == null ? "" : folder);
                cmd.Parameters.AddWithValue("$src", srcPath == null ? "" : srcPath);
                cmd.Parameters.AddWithValue("$dest", destPath == null ? "" : destPath);
                cmd.Parameters.AddWithValue("$state", state == null ? "" : state);
                cmd.Parameters.AddWithValue("$note", note == null ? "" : note);
                cmd.Parameters.AddWithValue("$pid", planId);
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>最新一条整理计划（无则 null）。</summary>
        public SortPlanRow LatestSortPlan()
        {
            if (!_isCore)
            {
                return null;
            }
            SortPlanRow row = null;
            using (SqliteCommand cmd = NewCommand("SELECT id,scope,created_at,item_count,conflict_count,state,note FROM mod_sort_plan ORDER BY id DESC LIMIT 1"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    row = ReadSortPlan(r);
                }
            }
            return row;
        }

        /// <summary>按 id 取一条整理计划（无则 null）。</summary>
        public SortPlanRow SortPlanById(long planId)
        {
            if (!_isCore)
            {
                return null;
            }
            SortPlanRow row = null;
            using (SqliteCommand cmd = NewCommand("SELECT id,scope,created_at,item_count,conflict_count,state,note FROM mod_sort_plan WHERE id=$id"))
            {
                cmd.Parameters.AddWithValue("$id", planId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        row = ReadSortPlan(r);
                    }
                }
            }
            return row;
        }

        /// <summary>读一行计划头。</summary>
        private static SortPlanRow ReadSortPlan(SqliteDataReader r)
        {
            SortPlanRow row = new SortPlanRow();
            row.Id = r.GetInt64(0);
            row.Scope = r.IsDBNull(1) ? "" : r.GetString(1);
            row.CreatedAt = r.IsDBNull(2) ? "" : r.GetString(2);
            row.ItemCount = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            row.ConflictCount = r.IsDBNull(4) ? 0 : r.GetInt64(4);
            row.State = r.IsDBNull(5) ? "" : r.GetString(5);
            row.Note = r.IsDBNull(6) ? "" : r.GetString(6);
            return row;
        }

        /// <summary>某计划的条目（按展示顺序）。</summary>
        public List<SortPlanItemRow> QuerySortPlanItems(long planId)
        {
            List<SortPlanItemRow> list = new List<SortPlanItemRow>();
            if (!_isCore)
            {
                return list;
            }
            using (SqliteCommand cmd = NewCommand(@"SELECT seq,plan_id,lib,tier,root_path,guid,author,folder,src_path,dest_path,size,mtime,state,note
                     FROM mod_sort_plan_item WHERE plan_id=$pid ORDER BY seq"))
            {
                cmd.Parameters.AddWithValue("$pid", planId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        SortPlanItemRow row = new SortPlanItemRow();
                        row.Seq = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                        row.PlanId = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                        row.Lib = r.IsDBNull(2) ? 0 : (int)r.GetInt64(2);
                        row.Tier = r.IsDBNull(3) ? 0 : (int)r.GetInt64(3);
                        row.RootPath = r.IsDBNull(4) ? "" : r.GetString(4);
                        row.Guid = r.IsDBNull(5) ? "" : r.GetString(5);
                        row.Author = r.IsDBNull(6) ? "" : r.GetString(6);
                        row.Folder = r.IsDBNull(7) ? "" : r.GetString(7);
                        row.SrcPath = r.IsDBNull(8) ? "" : r.GetString(8);
                        row.DestPath = r.IsDBNull(9) ? "" : r.GetString(9);
                        row.Size = r.IsDBNull(10) ? 0 : r.GetInt64(10);
                        row.Mtime = r.IsDBNull(11) ? "" : r.GetString(11);
                        row.State = r.IsDBNull(12) ? "" : r.GetString(12);
                        row.Note = r.IsDBNull(13) ? "" : r.GetString(13);
                        list.Add(row);
                    }
                }
            }
            return list;
        }

        /// <summary>某计划里指定状态的条目数。</summary>
        public long CountSortPlanItems(long planId, string state)
        {
            if (!_isCore)
            {
                return 0;
            }
            using (SqliteCommand cmd = NewCommand("SELECT COUNT(*) FROM mod_sort_plan_item WHERE plan_id=$pid AND state=$state"))
            {
                cmd.Parameters.AddWithValue("$pid", planId);
                cmd.Parameters.AddWithValue("$state", state ?? "");
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
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
        /// <summary>清掉某个插件库根下的全部插件行（重扫前先清——同一 dll 的插件项可能变少）。</summary>
        /// <param name="rootPath">插件库根绝对路径。</param>
        public void DeletePluginsUnderRoot(string rootPath)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM plugin_file WHERE root_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", rootPath);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>写入一项插件（file_path + guid 唯一——一个 dll 可含多项；guid 为空表示该 dll 未解析出插件特性）。</summary>
        /// <param name="row">插件行。</param>
        public void SavePlugin(PluginRow row)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT OR REPLACE INTO plugin_file(
                                file_path, guid, root_path, file_name, name, version, processes, dependencies, is_ipa, size, mtime, scan_time,
                                title, description, company, copyright, product, file_version, target_framework, note)
                                VALUES($f, $g, $r, $n, $nm, $v, $p, $d, $i, $s, $m, $t,
                                $ti, $de, $co, $cr, $pr, $fv, $tf, $no)"))
            {
                cmd.Parameters.AddWithValue("$f", row.FilePath);
                cmd.Parameters.AddWithValue("$g", row.Guid);
                cmd.Parameters.AddWithValue("$r", row.RootPath);
                cmd.Parameters.AddWithValue("$n", row.FileName);
                cmd.Parameters.AddWithValue("$nm", row.Name);
                cmd.Parameters.AddWithValue("$v", row.Version);
                cmd.Parameters.AddWithValue("$p", row.Processes);
                cmd.Parameters.AddWithValue("$d", row.Dependencies);
                cmd.Parameters.AddWithValue("$i", row.IsIpa ? 1 : 0);
                cmd.Parameters.AddWithValue("$s", row.Size);
                cmd.Parameters.AddWithValue("$m", row.Mtime);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$ti", row.Title);
                cmd.Parameters.AddWithValue("$de", row.Description);
                cmd.Parameters.AddWithValue("$co", row.Company);
                cmd.Parameters.AddWithValue("$cr", row.Copyright);
                cmd.Parameters.AddWithValue("$pr", row.Product);
                cmd.Parameters.AddWithValue("$fv", row.FileVersion);
                cmd.Parameters.AddWithValue("$tf", row.TargetFramework);
                cmd.Parameters.AddWithValue("$no", row.Note);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>全部插件行（有 guid 的在前按 guid / 文件名排序；空 guid 的非插件 dll 排在最后）。</summary>
        /// <returns>插件行清单。</returns>
        public List<PluginRow> LoadPlugins()
        {
            List<PluginRow> list = new List<PluginRow>();
            using (SqliteCommand cmd = NewCommand(@"SELECT file_path, guid, root_path, file_name, name, version, processes, dependencies, is_ipa, size, mtime,
                                title, description, company, copyright, product, file_version, target_framework, note
                                FROM plugin_file ORDER BY CASE WHEN guid='' THEN 1 ELSE 0 END, guid, file_name"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    PluginRow row = new PluginRow();
                    row.FilePath = r.IsDBNull(0) ? "" : r.GetString(0);
                    row.Guid = r.IsDBNull(1) ? "" : r.GetString(1);
                    row.RootPath = r.IsDBNull(2) ? "" : r.GetString(2);
                    row.FileName = r.IsDBNull(3) ? "" : r.GetString(3);
                    row.Name = r.IsDBNull(4) ? "" : r.GetString(4);
                    row.Version = r.IsDBNull(5) ? "" : r.GetString(5);
                    row.Processes = r.IsDBNull(6) ? "" : r.GetString(6);
                    row.Dependencies = r.IsDBNull(7) ? "" : r.GetString(7);
                    row.IsIpa = !r.IsDBNull(8) && r.GetInt64(8) != 0;
                    row.Size = r.IsDBNull(9) ? 0 : r.GetInt64(9);
                    row.Mtime = r.IsDBNull(10) ? "" : r.GetString(10);
                    row.Title = r.IsDBNull(11) ? "" : r.GetString(11);
                    row.Description = r.IsDBNull(12) ? "" : r.GetString(12);
                    row.Company = r.IsDBNull(13) ? "" : r.GetString(13);
                    row.Copyright = r.IsDBNull(14) ? "" : r.GetString(14);
                    row.Product = r.IsDBNull(15) ? "" : r.GetString(15);
                    row.FileVersion = r.IsDBNull(16) ? "" : r.GetString(16);
                    row.TargetFramework = r.IsDBNull(17) ? "" : r.GetString(17);
                    row.Note = r.IsDBNull(18) ? "" : r.GetString(18);
                    list.Add(row);
                }
            }
            return list;
        }
        /// <summary>按 GUID 取一个配置文件（含整段 JSON——插件分析窗用；同 GUID 多份取文件名第一个）。</summary>
        /// <param name="guid">插件 GUID。</param>
        /// <returns>配置行（无则 Sections 为空）。</returns>
        public PluginConfigRow LoadPluginConfigByGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                return new PluginConfigRow();
            }
            using (SqliteCommand cmd = NewCommand(@"SELECT file_path, file_name, plugin_name, plugin_version, guid,
                                size, mtime, sections, section_count, option_count, read_at, error
                                FROM plugin_config WHERE guid=$g ORDER BY file_name LIMIT 1"))
            {
                cmd.Parameters.AddWithValue("$g", guid);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                    {
                        return new PluginConfigRow();
                    }
                    PluginConfigRow row = new PluginConfigRow();
                    row.FilePath = r.IsDBNull(0) ? "" : r.GetString(0);
                    row.FileName = r.IsDBNull(1) ? "" : r.GetString(1);
                    row.PluginName = r.IsDBNull(2) ? "" : r.GetString(2);
                    row.PluginVersion = r.IsDBNull(3) ? "" : r.GetString(3);
                    row.Guid = r.IsDBNull(4) ? "" : r.GetString(4);
                    row.Size = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                    row.Mtime = r.IsDBNull(6) ? "" : r.GetString(6);
                    row.Sections = r.IsDBNull(7) ? "" : r.GetString(7);
                    row.SectionCount = r.IsDBNull(8) ? 0 : r.GetInt64(8);
                    row.OptionCount = r.IsDBNull(9) ? 0 : r.GetInt64(9);
                    row.ReadAt = r.IsDBNull(10) ? "" : r.GetString(10);
                    row.Error = r.IsDBNull(11) ? "" : r.GetString(11);
                    return row;
                }
            }
        }
        /// <summary>全部插件配置文件（按 GUID / 文件名排序——不含整段 JSON，列表页用）。</summary>
        /// <returns>配置行清单（Sections 字段为空）。</returns>
        public List<PluginConfigRow> LoadPluginConfigs()
        {
            List<PluginConfigRow> list = new List<PluginConfigRow>();
            using (SqliteCommand cmd = NewCommand(@"SELECT file_path, file_name, plugin_name, plugin_version, guid,
                                size, mtime, section_count, option_count, read_at, error
                                FROM plugin_config ORDER BY guid, file_name"))
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    PluginConfigRow row = new PluginConfigRow();
                    row.FilePath = r.IsDBNull(0) ? "" : r.GetString(0);
                    row.FileName = r.IsDBNull(1) ? "" : r.GetString(1);
                    row.PluginName = r.IsDBNull(2) ? "" : r.GetString(2);
                    row.PluginVersion = r.IsDBNull(3) ? "" : r.GetString(3);
                    row.Guid = r.IsDBNull(4) ? "" : r.GetString(4);
                    row.Size = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                    row.Mtime = r.IsDBNull(6) ? "" : r.GetString(6);
                    row.SectionCount = r.IsDBNull(7) ? 0 : r.GetInt64(7);
                    row.OptionCount = r.IsDBNull(8) ? 0 : r.GetInt64(8);
                    row.ReadAt = r.IsDBNull(9) ? "" : r.GetString(9);
                    row.Error = r.IsDBNull(10) ? "" : r.GetString(10);
                    list.Add(row);
                }
            }
            return list;
        }
        /// <summary>写入一个插件配置文件（cfg 解析结果——分节 / 选项 / 注释整段 JSON 落库）。</summary>
        /// <param name="row">配置行。</param>
        public void SavePluginConfig(PluginConfigRow row)
        {
            using (SqliteCommand cmd = NewCommand(@"INSERT OR REPLACE INTO plugin_config(
                                file_path, file_name, plugin_name, plugin_version, guid,
                                size, mtime, sections, section_count, option_count, read_at, error)
                                VALUES($f, $n, $pn, $pv, $g, $s, $m, $se, $sc, $oc, $r, $e)"))
            {
                cmd.Parameters.AddWithValue("$f", row.FilePath);
                cmd.Parameters.AddWithValue("$n", row.FileName);
                cmd.Parameters.AddWithValue("$pn", row.PluginName);
                cmd.Parameters.AddWithValue("$pv", row.PluginVersion);
                cmd.Parameters.AddWithValue("$g", row.Guid);
                cmd.Parameters.AddWithValue("$s", row.Size);
                cmd.Parameters.AddWithValue("$m", row.Mtime);
                cmd.Parameters.AddWithValue("$se", row.Sections);
                cmd.Parameters.AddWithValue("$sc", row.SectionCount);
                cmd.Parameters.AddWithValue("$oc", row.OptionCount);
                cmd.Parameters.AddWithValue("$r", string.IsNullOrEmpty(row.ReadAt) ? DateTime.UtcNow.ToString("o") : row.ReadAt);
                cmd.Parameters.AddWithValue("$e", row.Error);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>清掉某个插件库根下的全部配置行（重扫前先清——cfg 删掉了行也要跟着没）。</summary>
        /// <param name="pluginRoot">插件库根绝对路径（cfg 在其 config 子目录下）。</param>
        public void DeletePluginConfigsUnderRoot(string pluginRoot)
        {
            if (string.IsNullOrWhiteSpace(pluginRoot))
            {
                return;
            }
            using (SqliteCommand cmd = NewCommand("DELETE FROM plugin_config WHERE file_path LIKE $p"))
            {
                cmd.Parameters.AddWithValue("$p", pluginRoot.TrimEnd('\\', '/') + "\\%");
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>删掉某个 dll 的全部插件行（重解析前先清——同一 dll 的插件项可能变少）。</summary>
        /// <param name="filePath">dll 绝对路径。</param>
        public void DeletePluginFile(string filePath)
        {
            using (SqliteCommand cmd = NewCommand("DELETE FROM plugin_file WHERE file_path=$p"))
            {
                cmd.Parameters.AddWithValue("$p", filePath);
                cmd.ExecuteNonQuery();
            }
        }
        /// <summary>把 WAL 内容归位到主库文件（预建分片库的模板复制前调用——只复制主文件时结构才完整）。</summary>
        public void Checkpoint()
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE)");
        }
    }
}
