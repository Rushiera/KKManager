using System.Collections.Generic;

namespace KKManager.Core
{
    /// <summary>一项敏感带——卡片里的数值与显示名称的对应。</summary>
    public class WeakPointEntry
    {
        /// <summary>敏感带 ID（卡片 Parameter 块里 weakPoint 的取值）。</summary>
        public int Id { get; set; }

        /// <summary>显示名称。</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// 敏感带字母表——卡片 weakPoint 数值 ↔ 名称的唯一对应表。
    /// 实证：样本对 82505324_p24（原卡 1 → 游戏内改成 0）· 参考卡「久川 凪」（1）——1 = 胸部 · 0 = 嘴唇；
    /// -1 = 无（Rushiera 2026-09-27 判定）。
    /// 名称来源：游戏内角色编辑界面（Rushiera 核对）；本机 448 张样本实测出现 -1 / 0 / 1 / 2 / 3 / 4 / 5，
    /// 未收录的值按「未命名（值 N）」呈现——不假装知道（2..5 名称待补）。
    /// </summary>
    public static class WeakPoint
    {
        /// <summary>无敏感带（值 -1——本机卡片库实测存在）。</summary>
        public const int NoneId = -1;

        /// <summary>空值的显示名。</summary>
        public const string NoneName = "无";

        /// <summary>敏感带名称——下标即 ID；未收录的返回 null（不猜）。</summary>
        private static readonly string[] Names = { "嘴唇", "胸部" };

        /// <summary>敏感带名称（表外返回 null）。</summary>
        public static string NameOf(int id)
        {
            if (id == NoneId)
            {
                return NoneName;
            }
            if (id >= 0 && id < Names.Length)
            {
                return Names[id];
            }
            return null;
        }

        /// <summary>全量敏感带清单（「无」在首，其余按 ID 升序）——面板下拉直接消费。</summary>
        public static List<WeakPointEntry> All()
        {
            List<WeakPointEntry> list = new List<WeakPointEntry>();
            WeakPointEntry none = new WeakPointEntry();
            none.Id = NoneId;
            none.Name = NoneName;
            list.Add(none);
            for (int i = 0; i < Names.Length; i = i + 1)
            {
                WeakPointEntry e = new WeakPointEntry();
                e.Id = i;
                e.Name = Names[i];
                list.Add(e);
            }
            return list;
        }
    }
}
