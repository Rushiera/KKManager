using System;

namespace KKManager.Core
{
    /// <summary>
    /// 卡片角色名读取——游戏内显示的角色名（面板「按角色名」排序的依据）。
    /// 人物卡：名字在数据区 Parameter 块的 lastname / firstname（复用 CardEdit 的布局解析与字段读取）；
    /// 服装卡：名字在数据区头段的角色名段（CardReader 顺带读出，落在 CardInfo.CharaName）。
    /// 读不到一律返回 null——调用方按 null 记「未读到」，不编造名字。
    /// </summary>
    public static class CardName
    {
        /// <summary>人物卡的姓 / 名拼成游戏内显示名（两者都空返回 null；只有一个则用它；两个都有则以空格分隔）。</summary>
        public static string Join(string lastName, string firstName)
        {
            string last = (lastName ?? "").Trim();
            string first = (firstName ?? "").Trim();
            if (last.Length == 0 && first.Length == 0)
            {
                return null;
            }
            if (last.Length == 0)
            {
                return first;
            }
            if (first.Length == 0)
            {
                return last;
            }
            return last + " " + first;
        }

        /// <summary>读一张人物卡的角色名（数据区 Parameter 块）；布局或字段读不到一律返回 null（是否人物卡由调用方判定）。</summary>
        public static string ReadCharacter(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                CardLayout layout = CardEdit.Parse(path);
                if (layout == null || layout.Error != null)
                {
                    return null;
                }
                CardParamInfo info = CardEdit.ReadParams(path, layout);
                if (info == null || info.Error != null)
                {
                    return null;
                }
                return Join(info.LastName, info.FirstName);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
