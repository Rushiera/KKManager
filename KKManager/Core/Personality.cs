using System.Collections.Generic;

namespace KKManager.Core
{
    /// <summary>一项性格——卡片里的数值与显示名称的对应。</summary>
    public class PersonalityEntry
    {
        /// <summary>性格 ID（卡片 Parameter 块里 personality 的取值）。</summary>
        public int Id { get; set; }

        /// <summary>显示名称（汉化补丁的名称；额外 mod 加的性格没有名称）。</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// 性格字母表——卡片 personality 数值 ↔ 名称的唯一对应表。
    /// 名称来源：本机汉化补丁 <c>BepInEx\plugins\KK_Chinese\Text_Audio\cNN&lt;名称&gt;.txt</c> 的文件序号与名称（39 项）。
    /// 实证：样本卡逐个读出数值——电波系 5 · 大和抚子 6（两卡一致）· 正统女主角 21 · 病娇 24，与文件序号逐个吻合。
    /// 额外 ID：83（额外 mod 加的性格，汉化补丁未收录；样本卡「福丸 小糸」实测）——名称未知，界面显示为「未命名」。
    /// </summary>
    public static class Personality
    {
        /// <summary>汉化补丁收录的性格名称——下标即性格 ID（0..38）。</summary>
        private static readonly string[] PatchNames =
        {
            "性感", "大小姐", "高傲", "后辈", "神秘", "电波", "大和抚子", "男孩子气", "纯真无垢", "单纯",
            "中二病", "母性", "大姐头", "辣妹", "不良少女", "野生", "高意识冷酷", "乖僻", "不幸少女", "文学少女",
            "扭扭捏捏", "正统女主角", "追星族（时尚）", "宅女", "病娇", "怕麻烦", "无口", "顽固", "古风", "直率冷漠",
            "随和（坦率）", "好胜", "诚实", "娇艳", "归国子女", "方言娘", "抖S", "无感情", "几帐面"
        };

        /// <summary>额外 mod 加的性格 ID（汉化补丁的 39 项之外）——名称未知，按「未命名」显示；可被编辑功能选中。</summary>
        private static readonly int[] ExtraIds = { 83 };

        /// <summary>额外性格的显示名（没有权威名称——不假装知道，界面按「未命名」呈现）。</summary>
        private const string Unnamed = "未命名（额外 mod）";

        /// <summary>性格名称——补丁收录的返回中文名，额外 ID 返回「未命名」，表外返回 null。</summary>
        public static string NameOf(int id)
        {
            if (id >= 0 && id < PatchNames.Length)
            {
                return PatchNames[id];
            }
            for (int i = 0; i < ExtraIds.Length; i = i + 1)
            {
                if (ExtraIds[i] == id)
                {
                    return Unnamed;
                }
            }
            return null;
        }

        /// <summary>全量性格清单（补丁 39 项 + 额外 ID，按 ID 升序）——面板下拉直接消费。</summary>
        public static List<PersonalityEntry> All()
        {
            List<PersonalityEntry> list = new List<PersonalityEntry>();
            for (int i = 0; i < PatchNames.Length; i = i + 1)
            {
                PersonalityEntry e = new PersonalityEntry();
                e.Id = i;
                e.Name = PatchNames[i];
                list.Add(e);
            }
            for (int i = 0; i < ExtraIds.Length; i = i + 1)
            {
                PersonalityEntry e = new PersonalityEntry();
                e.Id = ExtraIds[i];
                e.Name = Unnamed;
                list.Add(e);
            }
            return list;
        }
    }
}
