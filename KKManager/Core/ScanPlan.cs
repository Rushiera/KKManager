using System;
using System.Collections.Generic;
using System.Text;

namespace KKManager.Core
{
    /// <summary>
    /// 扫描步骤的读取档——同侧同档的相邻步骤合成一段，段内共用一次文件打开（IO 账的核心）。
    /// </summary>
    public enum ScanReadTier
    {
        /// <summary>零读——只用枚举结果与文件属性。</summary>
        None = 0,

        /// <summary>局部读——头段 / 块表 / 声明区 / 坐标块（几 KB 至数 MB）。</summary>
        Local = 1,

        /// <summary>图片区读——缩略图解码。</summary>
        Image = 2,

        /// <summary>全扫——整个数据区流式扫描（成本与该文件体积成正比）。</summary>
        Full = 3
    }

    /// <summary>扫描步骤定义——id / 显示名 / 侧 / 读档 / 必选 / 默认勾选 / 灰置。</summary>
    public class ScanStepDef
    {
        /// <summary>步骤 id（落 scan_state 与 setting 用的稳定标识）。</summary>
        public string Id { get; set; }

        /// <summary>界面显示名。</summary>
        public string Name { get; set; }

        /// <summary>true = mod 侧；false = 卡片侧。</summary>
        public bool Mods { get; set; }

        /// <summary>读取档——决定段合并。</summary>
        public ScanReadTier Tier { get; set; }

        /// <summary>必选——界面勾选框锁定不可取消。</summary>
        public bool Required { get; set; }

        /// <summary>默认勾选（可选步骤用）。</summary>
        public bool DefaultOn { get; set; }

        /// <summary>灰置——可见但不可选（本轮占位）。</summary>
        public bool Grayed { get; set; }

        /// <summary>界面提示（一句话说明这步读什么 / 干什么用）。</summary>
        public string Note { get; set; }

        /// <summary>所属扫描组 id（空 = 不属于任何组——配置窗里单独勾选）。</summary>
        public string Group { get; set; }
    }

    /// <summary>
    /// 扫描组——配置窗的勾选单元：组内步骤同进同出（组勾 = 组内全部步骤启用）。
    /// 必选组不可取消；可选组默认勾选由 DefaultOn 定。
    /// </summary>
    public class ScanGroupDef
    {
        /// <summary>组 id（稳定标识）。</summary>
        public string Id { get; set; }

        /// <summary>界面显示名。</summary>
        public string Name { get; set; }

        /// <summary>界面提示（一句话说明这组做什么）。</summary>
        public string Note { get; set; }

        /// <summary>必选——界面勾选框锁定不可取消。</summary>
        public bool Required { get; set; }

        /// <summary>默认勾选（可选组用）。</summary>
        public bool DefaultOn { get; set; }
    }

    /// <summary>扫描段——同侧同档的连续步骤，段内共用一次文件打开。</summary>
    public class ScanSegment
    {
        /// <summary>true = mod 侧。</summary>
        public bool Mods { get; set; }

        /// <summary>读取档。</summary>
        public ScanReadTier Tier { get; set; }

        /// <summary>段内步骤（按执行顺序）。</summary>
        public List<ScanStepDef> Steps { get; } = new List<ScanStepDef>();

        /// <summary>段的显示名（段内步骤名以 + 连接）。</summary>
        public string Label
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                foreach (ScanStepDef s in Steps)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(" + ");
                    }
                    sb.Append(s.Name);
                }
                return sb.ToString();
            }
        }

        /// <summary>段内是否含某步。</summary>
        public bool Has(string id)
        {
            foreach (ScanStepDef s in Steps)
            {
                if (s.Id == id)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 扫描计划——全集顺序 + 启用集合（全局一套，追加库扫描同样适用）。
    /// 落 setting 两个键：scan_plan_order（全量顺序 id，逗号分隔）/ scan_plan_on（启用 id，逗号分隔）。
    /// 收尾（清扫已消失记录 + 作者索引重建）不在此表——由扫描器每侧末尾固定执行。
    /// </summary>
    public class ScanPlan
    {
        /// <summary>全集顺序（全部步骤 id，按执行顺序）。</summary>
        public List<string> Order { get; } = new List<string>();

        /// <summary>启用集合（必选项恒在，此处只记用户显式勾选的）。</summary>
        public HashSet<string> On { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>某步骤当前是否启用（必选项恒真，灰置项恒假）。</summary>
        public bool IsOn(string id)
        {
            ScanStepDef def = ScanPlanCatalog.Find(id);
            if (def == null || def.Grayed)
            {
                return false;
            }
            if (def.Required)
            {
                return true;
            }
            return On.Contains(id);
        }

        /// <summary>按当前顺序 + 启用集合切出执行段——跨侧交错按顺序走；同侧同档相邻的步骤合并为一段。</summary>
        public List<ScanSegment> Segments()
        {
            List<ScanSegment> list = new List<ScanSegment>();
            ScanSegment cur = null;
            foreach (string id in Order)
            {
                ScanStepDef def = ScanPlanCatalog.Find(id);
                if (def == null || def.Grayed || !IsOn(id))
                {
                    continue;
                }
                if (cur != null && cur.Mods == def.Mods && cur.Tier == def.Tier)
                {
                    cur.Steps.Add(def);
                    continue;
                }
                cur = new ScanSegment();
                cur.Mods = def.Mods;
                cur.Tier = def.Tier;
                cur.Steps.Add(def);
                list.Add(cur);
            }
            return list;
        }

        /// <summary>某侧启用的步骤 id 列表（按顺序；面板与 CLI 展示用）。</summary>
        public List<string> Enabled(bool mods)
        {
            List<string> list = new List<string>();
            foreach (string id in Order)
            {
                ScanStepDef def = ScanPlanCatalog.Find(id);
                if (def == null || def.Mods != mods || def.Grayed || !IsOn(id))
                {
                    continue;
                }
                list.Add(id);
            }
            return list;
        }
    }

    /// <summary>扫描步骤目录——内置定义与计划的载入 / 保存（唯一真相源）。</summary>
    public static class ScanPlanCatalog
    {
        /// <summary>设置键——全集顺序。</summary>
        public const string OrderKey = "scan_plan_order";

        /// <summary>设置键——启用集合。</summary>
        public const string OnKey = "scan_plan_on";

        /// <summary>组 id——统一扫描（必选：卡片行 + mod 行 + 卡头段 + 声明区 + 角色名 + 时间轴 + 缩略图）。</summary>
        public const string GroupCore = "core";

        /// <summary>组 id——卡片分析（可选：服装槽位 + 卡片分析 + 场景深度）。</summary>
        public const string GroupAnalyze = "analyze";

        /// <summary>内置组定义（数组顺序即配置窗展示顺序——统一扫描在最上）。</summary>
        private static readonly ScanGroupDef[] GroupDefs = new ScanGroupDef[]
        {
            MakeGroup(GroupCore, "统一扫描", true, true,
                "卡片行 + mod 行 + 卡头段 + 声明区 + 角色名 + 时间轴 + 缩略图 + 插件库（插件库随主要库扫描）——必选，一次跑完"),
            MakeGroup(GroupAnalyze, "卡片分析", false, false,
                "服装槽位 + 卡片分析 + 场景深度——整段数据区读取，耗时与卡片体积成正比")
        };

        private static readonly ScanStepDef[] Defs = new ScanStepDef[]
        {
            Make("row", "卡片行", false, ScanReadTier.None, true, true, false, GroupCore,
                "只记文件与文件夹——卡片视图先能看见卡（不读文件内容）"),
            Make("modrow", "mod 行", true, ScanReadTier.None, true, true, false, GroupCore,
                "manifest 的 guid / 名称 / 版本 / 作者——mod 总数与关联主键（库内能读到 manifest.xml 的文件都算）"),
            Make("head", "卡头段", false, ScanReadTier.Local, true, true, false, GroupCore,
                "卡类型与图片区终点——其余数据区步骤的前置"),
            Make("refs", "声明区", false, ScanReadTier.Local, true, true, false, GroupCore,
                "卡片引用的 mod——四色与缺失清单的依据"),
            Make("name", "角色名", false, ScanReadTier.Local, true, true, false, GroupCore,
                "Parameter 块的姓 / 名——按角色名排序用"),
            Make("timeline", "时间轴", false, ScanReadTier.Local, true, true, false, GroupCore,
                "场景卡的 Timeline 时长——按时间轴排序用"),
            Make("coord", "服装槽位", false, ScanReadTier.Local, false, false, false, GroupAnalyze,
                "人物卡七套 coordinate（服装 / 饰品 / 化妆）——只读分析"),
            Make("thumb", "缩略图", false, ScanReadTier.Image, true, true, false, GroupCore,
                "卡片视图的图（图片区解码）"),
            Make("detail", "卡片分析", false, ScanReadTier.Full, false, false, false, GroupAnalyze,
                "整个数据区的结构全字段（耗时与卡片体积成正比）"),
            Make("scene", "场景深度", false, ScanReadTier.Full, false, false, false, GroupAnalyze,
                "场景插件条目 / 道具 / 轨道组 / 内嵌角色数据（sd 专用）"),
            Make("composition", "组成档案", true, ScanReadTier.Local, false, false, false, null,
                "zipmod 条目清单与文本条目内容（打开 mod 时的组成视图）"),
            Make("u3d", "unity3d 贴图", true, ScanReadTier.Full, false, false, true, null,
                "容器内 unity3d 贴图解析——本轮占位，不参与扫描")
        };

        /// <summary>内置定义（数组顺序即默认顺序）。</summary>
        public static IReadOnlyList<ScanStepDef> All
        {
            get { return Defs; }
        }

        /// <summary>内置组定义（数组顺序即配置窗展示顺序——统一扫描在最上）。</summary>
        public static IReadOnlyList<ScanGroupDef> Groups
        {
            get { return GroupDefs; }
        }

        /// <summary>按 id 找组（未知返回 null）。</summary>
        public static ScanGroupDef FindGroup(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (ScanGroupDef g in GroupDefs)
            {
                if (g.Id == id)
                {
                    return g;
                }
            }
            return null;
        }

        /// <summary>某组的步骤（按内置顺序；空 id 返回空表）。</summary>
        public static List<ScanStepDef> StepsOf(string groupId)
        {
            List<ScanStepDef> list = new List<ScanStepDef>();
            if (string.IsNullOrEmpty(groupId))
            {
                return list;
            }
            foreach (ScanStepDef d in Defs)
            {
                if (d.Group == groupId)
                {
                    list.Add(d);
                }
            }
            return list;
        }

        private static ScanStepDef Make(string id, string name, bool mods, ScanReadTier tier, bool required, bool defaultOn, bool grayed, string group, string note)
        {
            ScanStepDef d = new ScanStepDef();
            d.Id = id;
            d.Name = name;
            d.Mods = mods;
            d.Tier = tier;
            d.Required = required;
            d.DefaultOn = defaultOn;
            d.Grayed = grayed;
            d.Group = group;
            d.Note = note;
            return d;
        }

        /// <summary>构造组定义。</summary>
        private static ScanGroupDef MakeGroup(string id, string name, bool required, bool defaultOn, string note)
        {
            ScanGroupDef g = new ScanGroupDef();
            g.Id = id;
            g.Name = name;
            g.Required = required;
            g.DefaultOn = defaultOn;
            g.Note = note;
            return g;
        }

        /// <summary>按 id 找定义（未知返回 null）。</summary>
        public static ScanStepDef Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (ScanStepDef d in Defs)
            {
                if (d.Id == id)
                {
                    return d;
                }
            }
            return null;
        }

        /// <summary>默认计划——内置顺序 + 默认勾选（必选与默认开项都在）。</summary>
        public static ScanPlan Default()
        {
            ScanPlan p = new ScanPlan();
            foreach (ScanStepDef d in Defs)
            {
                p.Order.Add(d.Id);
                if (d.DefaultOn && !d.Required)
                {
                    p.On.Add(d.Id);
                }
            }
            return p;
        }

        /// <summary>
        /// 从设置文本载入计划——order 给全集顺序（未知 id 丢弃、缺失项按内置顺序补在末尾），on 给启用集合。
        /// 两者都空时返回默认计划；灰置项一律不入启用集合。
        /// </summary>
        public static ScanPlan Load(string orderText, string onText)
        {
            ScanPlan p = new ScanPlan();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(orderText))
            {
                foreach (string raw in orderText.Split(','))
                {
                    string id = raw.Trim();
                    if (id.Length == 0)
                    {
                        continue;
                    }
                    ScanStepDef def = Find(id);
                    if (def == null || !seen.Add(id))
                    {
                        continue;
                    }
                    p.Order.Add(id);
                }
            }
            foreach (ScanStepDef d in Defs)
            {
                if (!seen.Contains(d.Id))
                {
                    p.Order.Add(d.Id);
                    seen.Add(d.Id);
                }
            }

            HashSet<string> on = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(onText))
            {
                foreach (string raw in onText.Split(','))
                {
                    string id = raw.Trim();
                    if (id.Length == 0 || on.Contains(id))
                    {
                        continue;
                    }
                    on.Add(id);
                }
            }
            bool explicitOn = on.Count > 0;
            foreach (ScanStepDef d in Defs)
            {
                if (d.Grayed)
                {
                    continue;
                }
                if (d.Required)
                {
                    p.On.Add(d.Id);
                    continue;
                }
                if (explicitOn)
                {
                    if (on.Contains(d.Id))
                    {
                        p.On.Add(d.Id);
                    }
                    continue;
                }
                if (d.DefaultOn)
                {
                    p.On.Add(d.Id);
                }
            }
            // 组归一——可选组只有「全开 / 全关」两态：组内步骤并非全部启用时按全关处理（旧设置里只勾了组内一部分）
            foreach (ScanGroupDef g in GroupDefs)
            {
                if (g.Required)
                {
                    continue;
                }
                List<ScanStepDef> steps = StepsOf(g.Id);
                bool all = steps.Count > 0;
                foreach (ScanStepDef d in steps)
                {
                    if (!p.On.Contains(d.Id))
                    {
                        all = false;
                        break;
                    }
                }
                if (!all)
                {
                    foreach (ScanStepDef d in steps)
                    {
                        p.On.Remove(d.Id);
                    }
                }
            }
            return p;
        }

        /// <summary>顺序文本（逗号分隔）。</summary>
        public static string OrderText(ScanPlan p)
        {
            return string.Join(",", p.Order);
        }

        /// <summary>启用文本（逗号分隔；只记非必选项）。</summary>
        public static string OnText(ScanPlan p)
        {
            List<string> list = new List<string>();
            foreach (string id in p.Order)
            {
                ScanStepDef def = Find(id);
                if (def == null || def.Required || def.Grayed)
                {
                    continue;
                }
                if (p.On.Contains(id))
                {
                    list.Add(id);
                }
            }
            return string.Join(",", list);
        }
    }
}
