using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>一件服装部件——某套 coordinate 里一个槽位的读数。</summary>
    public class CardClothesPart
    {
        /// <summary>槽位下标（0 起，顺序见 <see cref="CardCoordinate.ClothesSlots"/>）。</summary>
        public int Index { get; set; }

        /// <summary>槽位名（上衣 / 下装 / …）。</summary>
        public string Slot { get; set; }

        /// <summary>部件 id（0 = 该槽位没穿东西）。</summary>
        public int Id { get; set; }

        /// <summary>袖型（sleevesType）。</summary>
        public int SleevesType { get; set; }

        /// <summary>徽章 id（第一枚）。</summary>
        public int EmblemeId { get; set; }

        /// <summary>徽章 id（第二枚）。</summary>
        public int EmblemeId2 { get; set; }

        /// <summary>隐藏开关一（hideOpt[0]）。</summary>
        public bool HideOptA { get; set; }

        /// <summary>隐藏开关二（hideOpt[1]）。</summary>
        public bool HideOptB { get; set; }

        /// <summary>四组颜色的展示文本（底色 / 图案号 / 平铺 / 图案色）。</summary>
        public List<string> Colors { get; } = new List<string>();
    }

    /// <summary>一件饰品——某套 coordinate 里一个饰品槽位的读数。</summary>
    public class CardAccessoryPart
    {
        /// <summary>饰品槽位下标（0 起，共 20 个）。</summary>
        public int Index { get; set; }

        /// <summary>饰品类别值（游戏 ChaListDefine.CategoryNo：120 无 · 121 发 · 122 头 …）。</summary>
        public int Type { get; set; }

        /// <summary>饰品类别名。</summary>
        public string TypeName { get; set; }

        /// <summary>饰品 id（0 = 该槽位空）。</summary>
        public int Id { get; set; }

        /// <summary>挂点骨骼名（a_n_headside 等）。</summary>
        public string ParentKey { get; set; }

        /// <summary>挂点中文名。</summary>
        public string ParentName { get; set; }

        /// <summary>是否不晃动（noShake）。</summary>
        public bool NoShake { get; set; }

        /// <summary>隐藏类别（hideCategory）。</summary>
        public int HideCategory { get; set; }

        /// <summary>四组颜色的展示文本。</summary>
        public List<string> Colors { get; } = new List<string>();
    }

    /// <summary>一套 coordinate（游戏内的一个服装槽位）的读数。</summary>
    public class CardCoordinateOutfit
    {
        /// <summary>槽位下标（0 起，共 7 套）。</summary>
        public int Index { get; set; }

        /// <summary>槽位名（学校制服① / 体操服 / …）。</summary>
        public string TypeName { get; set; }

        /// <summary>服装部件（9 个槽位，含空槽）。</summary>
        public List<CardClothesPart> Clothes { get; } = new List<CardClothesPart>();

        /// <summary>饰品（20 个槽位，含空槽）。</summary>
        public List<CardAccessoryPart> Accessories { get; } = new List<CardAccessoryPart>();

        /// <summary>是否启用化妆（enableMakeup）。</summary>
        public bool EnableMakeup { get; set; }

        /// <summary>化妆 id 摘要（眼影 / 脸颊 / 口红 / 涂装两项）。</summary>
        public string MakeupText { get; set; }

        /// <summary>这套 coordinate 的读取告警（结构不符时的具名出声）。</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>人物卡 coordinate（服装 / 饰品）读取结果——只读快照。</summary>
    public class CardCoordinateResult
    {
        /// <summary>卡片文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>七套 coordinate（卡里读到的顺序）。</summary>
        public List<CardCoordinateOutfit> Outfits { get; } = new List<CardCoordinateOutfit>();

        /// <summary>Coordinate 块的版本（lstInfo 里的 version）。</summary>
        public string BlockVersion { get; set; }

        /// <summary>Coordinate 块字节数。</summary>
        public long BlockSize { get; set; }

        /// <summary>解析告警（不静默）。</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>读取错误（null = 读到了）。</summary>
        public string Error { get; set; }

        /// <summary>该套 coordinate 里非空服装件数（id ≠ 0）。</summary>
        public static int ClothesCount(CardCoordinateOutfit o)
        {
            int n = 0;
            for (int i = 0; i < o.Clothes.Count; i = i + 1)
            {
                if (o.Clothes[i].Id != 0)
                {
                    n = n + 1;
                }
            }
            return n;
        }

        /// <summary>该套 coordinate 里非空饰品件数（id ≠ 0）。</summary>
        public static int AccessoryCount(CardCoordinateOutfit o)
        {
            int n = 0;
            for (int i = 0; i < o.Accessories.Count; i = i + 1)
            {
                if (o.Accessories[i].Id != 0)
                {
                    n = n + 1;
                }
            }
            return n;
        }
    }

    /// <summary>人物卡的服装 / 饰品读取——Coordinate 块的七套槽位（服装 9 槽 + 饰品 20 槽 + 化妆）。
    /// 结构依据：游戏 <c>ChaFileCoordinate.SaveBytes</c>（clothes / accessory / enableMakeup / makeup 四段）与
    /// <c>ChaFile.GetCoordinateBytes</c>（MessagePack 数组，每套一个 bin）——反编译实证。
    /// 只读：不写文件、不改内容。</summary>
    public static class CardCoordinate
    {
        /// <summary>Coordinate 块名（lstInfo 目录条目）。</summary>
        public const string BlockName = "Coordinate";

        /// <summary>块读取上限（防御性——正常 7 套约 70 KB）。</summary>
        public const long MaxBlockBytes = 64L * 1024L * 1024L;

        /// <summary>服装槽位名（顺序 = 游戏 <c>ChaFileDefine.ClothesKind</c>）。</summary>
        public static readonly string[] ClothesSlots =
        {
            "上衣", "下装", "内衣（上）", "内衣（下）", "手套", "裤袜", "袜子", "鞋（内）", "鞋（外）"
        };

        /// <summary>服装槽位的游戏枚举名（ClothesKind）。</summary>
        public static readonly string[] ClothesSlotKeys =
        {
            "top", "bot", "bra", "shorts", "gloves", "panst", "socks", "shoes_inner", "shoes_outer"
        };

        /// <summary>服装成套子件位名（游戏 <c>ChaFileDefine.ClothesSubKind</c>）。</summary>
        public static readonly string[] SubPartKeys = { "partsA", "partsB", "partsC" };

        /// <summary>Coordinate 槽位名（顺序 = 游戏 <c>ChaFileDefine.CoordinateType</c>）。</summary>
        public static readonly string[] CoordinateSlots =
        {
            "学校制服①（School01）", "学校制服②（School02）", "体操服（Gym）", "泳装（Swim）",
            "社团（Club）", "便服（Plain）", "睡衣（Pajamas）"
        };

        /// <summary>饰品类别名（游戏 <c>ChaAccessoryDefine.AccessoryTypeName</c>，值 = ChaListDefine.CategoryNo）。</summary>
        public static readonly string[] AccessoryTypeNames =
        {
            "无（120）", "发（121）", "头（122）", "脸（123）", "颈（124）", "躯干（125）",
            "腰（126）", "腿（127）", "臂（128）", "手（129）", "股间（130）"
        };

        /// <summary>饰品挂点骨骼名（顺序 = 游戏 <c>ChaAccessoryDefine.AccessoryParentKey</c>）。</summary>
        public static readonly string[] AccessoryParentKeys =
        {
            "none", "a_n_hair_pony", "a_n_hair_twin_L", "a_n_hair_twin_R", "a_n_hair_pin", "a_n_hair_pin_R",
            "a_n_headtop", "a_n_headflont", "a_n_head", "a_n_headside", "a_n_earrings_L", "a_n_earrings_R",
            "a_n_megane", "a_n_nose", "a_n_mouth", "a_n_neck", "a_n_bust_f", "a_n_bust", "a_n_nip_L", "a_n_nip_R",
            "a_n_back", "a_n_back_L", "a_n_back_R", "a_n_waist", "a_n_waist_f", "a_n_waist_b", "a_n_waist_L", "a_n_waist_R",
            "a_n_leg_L", "a_n_knee_L", "a_n_ankle_L", "a_n_heel_L", "a_n_leg_R", "a_n_knee_R", "a_n_ankle_R", "a_n_heel_R",
            "a_n_shoulder_L", "a_n_elbo_L", "a_n_arm_L", "a_n_wrist_L", "a_n_shoulder_R", "a_n_elbo_R", "a_n_arm_R", "a_n_wrist_R",
            "a_n_hand_L", "a_n_ind_L", "a_n_mid_L", "a_n_ring_L", "a_n_hand_R", "a_n_ind_R", "a_n_mid_R", "a_n_ring_R",
            "a_n_dan", "a_n_kokan", "a_n_ana"
        };

        /// <summary>饰品挂点中文名（与 <see cref="AccessoryParentKeys"/> 同序，取自游戏表）。</summary>
        public static readonly string[] AccessoryParentNames =
        {
            "未设置", "马尾", "双马尾左", "双马尾右", "发夹左", "发夹右",
            "帽子", "额头", "头顶", "头侧", "耳环左", "耳环右",
            "眼镜", "鼻", "口", "颈", "胸上", "胸上中央", "左乳首", "右乳首",
            "背中中央", "背中左", "背中右", "腰", "腰前", "腰后", "腰左", "腰右",
            "左大腿", "左膝", "左脚踝", "左脚跟", "右大腿", "右膝", "右脚踝", "右脚跟",
            "左肩", "左肘", "左上臂", "左手腕", "右肩", "右肘", "右上臂", "右手腕",
            "左手", "左食指", "左中指", "左无名指", "右手", "右食指", "右中指", "右无名指",
            "男根根部", "女性器", "后穴"
        };

        /// <summary>服装槽位名（下标越界时返回「槽位 N」）。</summary>
        public static string ClothesSlotName(int index)
        {
            if (index >= 0 && index < ClothesSlots.Length)
            {
                return ClothesSlots[index];
            }
            return "槽位 " + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Coordinate 槽位名（下标越界时返回「第 N 套」）。</summary>
        public static string OutfitName(int index)
        {
            if (index >= 0 && index < CoordinateSlots.Length)
            {
                return CoordinateSlots[index];
            }
            return "第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 套";
        }

        /// <summary>饰品类别名（表外取值如实标号，不猜名）。</summary>
        public static string AccessoryTypeName(int type)
        {
            if (type >= 120 && type - 120 < AccessoryTypeNames.Length)
            {
                return AccessoryTypeNames[type - 120];
            }
            return "未收录（" + type.ToString(CultureInfo.InvariantCulture) + "）";
        }

        /// <summary>饰品挂点中文名（表外原样呈现骨骼名）。</summary>
        public static string AccessoryParentName(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "none")
            {
                return "未设置";
            }
            for (int i = 0; i < AccessoryParentKeys.Length; i = i + 1)
            {
                if (AccessoryParentKeys[i] == key)
                {
                    return AccessoryParentNames[i];
                }
            }
            return key;
        }

        /// <summary>读取人物卡的服装 / 饰品（Coordinate 块）。只读；失败具名返回。</summary>
        public static CardCoordinateResult Read(string path)
        {
            CardCoordinateResult result = new CardCoordinateResult();
            result.FilePath = path;
            if (!File.Exists(path))
            {
                result.Error = "卡片文件不存在：" + path;
                return result;
            }
            CardLayout layout = CardEdit.Parse(path);
            if (layout.Error != null)
            {
                // 复用编辑面的布局解析——非人物卡时换成服装语境的说法（别把「编辑面只覆盖…」这类话抛给调用方）
                if (layout.CardType != null && layout.CardType.IndexOf("Chara", StringComparison.Ordinal) < 0)
                {
                    result.Error = "不是人物卡（卡类型 " + layout.CardType + "）——服装 / 饰品只在人物卡的 Coordinate 块里";
                }
                else
                {
                    result.Error = layout.Error;
                }
                return result;
            }
            CardEditBlock block = FindBlock(layout);
            if (block == null)
            {
                result.Error = "卡片里没有 " + BlockName + " 块（现有块：" + BlockNames(layout) + "）";
                return result;
            }
            result.BlockVersion = block.Version;
            result.BlockSize = block.Size;
            if (block.Size <= 0 || block.Size > MaxBlockBytes)
            {
                result.Error = BlockName + " 块大小异常（" + block.Size.ToString("N0") + " 字节）";
                return result;
            }
            byte[] buf;
            try
            {
                buf = ReadRange(path, layout.PayloadStart + block.Pos, (int)block.Size);
            }
            catch (Exception ex)
            {
                result.Error = "读取 " + BlockName + " 块失败：" + ex.GetType().Name + " " + ex.Message;
                return result;
            }
            if (buf == null || buf.Length != block.Size)
            {
                result.Error = BlockName + " 块读取不完整（应 " + block.Size.ToString("N0") + " 字节）";
                return result;
            }
            ParseOutfits(buf, result);
            return result;
        }

        /// <summary>解析 Coordinate 块——MessagePack 数组，每套是一个 bin（ChaFileCoordinate.SaveBytes 的字节）。</summary>
        private static void ParseOutfits(byte[] buf, CardCoordinateResult result)
        {
            int pos = 0;
            int count;
            if (!ReadArrayHeader(buf, ref pos, out count))
            {
                string head = buf.Length > 0 ? "0x" + buf[0].ToString("X2") : "空";
                result.Error = BlockName + " 块不是 MessagePack 数组（首字节 " + head + "）";
                return;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                CardCoordinateOutfit outfit = new CardCoordinateOutfit();
                outfit.Index = i;
                outfit.TypeName = OutfitName(i);
                result.Outfits.Add(outfit);
                int len;
                if (!ReadBinHeader(buf, ref pos, out len))
                {
                    outfit.Warnings.Add("本套不是 MessagePack bin（偏移 " + pos.ToString("N0") + "）——本套及后续跳过");
                    return;
                }
                if (len < 0 || (long)pos + len > buf.Length)
                {
                    outfit.Warnings.Add("本套字节数异常（声明 " + len.ToString("N0") + " · 剩余 " + (buf.Length - pos).ToString("N0") + "）——本套跳过");
                    return;
                }
                ParseOutfitBlob(buf, pos, len, outfit);
                pos = pos + len;
            }
            if (pos < buf.Length)
            {
                result.Warnings.Add("块内还有 " + (buf.Length - pos).ToString("N0") + " 字节未解析（超出 " + count + " 套）");
            }
        }

        /// <summary>解析一套 coordinate 的字节——ChaFileCoordinate.SaveBytes：服装段 / 饰品段 / 化妆开关 / 化妆段。</summary>
        private static void ParseOutfitBlob(byte[] buf, int at, int len, CardCoordinateOutfit outfit)
        {
            int end = at + len;
            int p = at;
            int clothesLen;
            if (!ReadInt32(buf, ref p, end, out clothesLen))
            {
                outfit.Warnings.Add("缺少服装段长度");
                return;
            }
            if (clothesLen < 0 || (long)p + clothesLen > end)
            {
                outfit.Warnings.Add("服装段长度异常（" + clothesLen.ToString("N0") + "）");
                return;
            }
            ParseClothes(buf, p, clothesLen, outfit);
            p = p + clothesLen;

            int accessoryLen;
            if (!ReadInt32(buf, ref p, end, out accessoryLen))
            {
                outfit.Warnings.Add("缺少饰品段长度");
                return;
            }
            if (accessoryLen < 0 || (long)p + accessoryLen > end)
            {
                outfit.Warnings.Add("饰品段长度异常（" + accessoryLen.ToString("N0") + "）");
                return;
            }
            ParseAccessory(buf, p, accessoryLen, outfit);
            p = p + accessoryLen;

            if (p >= end)
            {
                outfit.Warnings.Add("缺少化妆开关字节");
                return;
            }
            outfit.EnableMakeup = buf[p] != 0;
            p = p + 1;

            int makeupLen;
            if (!ReadInt32(buf, ref p, end, out makeupLen))
            {
                outfit.Warnings.Add("缺少化妆段长度");
                return;
            }
            if (makeupLen < 0 || (long)p + makeupLen > end)
            {
                outfit.Warnings.Add("化妆段长度异常（" + makeupLen.ToString("N0") + "）");
                return;
            }
            ParseMakeup(buf, p, makeupLen, outfit);
        }

        /// <summary>解析服装段（ChaFileClothes：version / parts[9] / subPartsId / hideBraOpt / hideShortsOpt）。</summary>
        private static void ParseClothes(byte[] buf, int at, int len, CardCoordinateOutfit outfit)
        {
            MsgPackCursor cur = new MsgPackCursor(buf, at);
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                outfit.Warnings.Add("服装段不是 MessagePack map");
                return;
            }
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    outfit.Warnings.Add("服装段键读取失败（第 " + (i + 1) + " 项）");
                    return;
                }
                if (key == "parts")
                {
                    ReadClothesParts(cur, outfit);
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    outfit.Warnings.Add("服装段跳过值失败（键 " + key + "）");
                    return;
                }
            }
            int consumed = cur.Position - at;
            if (consumed != len)
            {
                outfit.Warnings.Add("服装段长度不符（声明 " + len.ToString("N0") + " · 实读 " + consumed.ToString("N0") + "）");
            }
        }

        /// <summary>解析服装部件数组（9 个槽位）。</summary>
        private static void ReadClothesParts(MsgPackCursor cur, CardCoordinateOutfit outfit)
        {
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                outfit.Warnings.Add("服装 parts 不是数组");
                return;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                CardClothesPart part = new CardClothesPart();
                part.Index = i;
                part.Slot = ClothesSlotName(i);
                if (!ReadClothesPart(cur, part))
                {
                    outfit.Warnings.Add("第 " + (i + 1) + " 件服装读取中断");
                    return;
                }
                outfit.Clothes.Add(part);
            }
            if (count != ClothesSlots.Length)
            {
                outfit.Warnings.Add("服装槽位数与预期不符（读到 " + count + " · 预期 " + ClothesSlots.Length + "）");
            }
        }

        /// <summary>读一件服装部件——id / colorInfo[4] / emblemeId / hideOpt / sleevesType。</summary>
        private static bool ReadClothesPart(MsgPackCursor cur, CardClothesPart part)
        {
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                return false;
            }
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                long number;
                if (key == "id")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.Id = (int)number;
                    continue;
                }
                if (key == "sleevesType")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.SleevesType = (int)number;
                    continue;
                }
                if (key == "emblemeId")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.EmblemeId = (int)number;
                    continue;
                }
                if (key == "emblemeId2")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.EmblemeId2 = (int)number;
                    continue;
                }
                if (key == "hideOpt")
                {
                    if (!ReadHideOpt(cur, part))
                    {
                        return false;
                    }
                    continue;
                }
                if (key == "colorInfo")
                {
                    if (!ReadColors(cur, part.Colors))
                    {
                        return false;
                    }
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>读服装的隐藏开关（hideOpt 两项）。</summary>
        private static bool ReadHideOpt(MsgPackCursor cur, CardClothesPart part)
        {
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                return false;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                bool value;
                if (!cur.TryReadBool(out value))
                {
                    return false;
                }
                if (i == 0)
                {
                    part.HideOptA = value;
                }
                else if (i == 1)
                {
                    part.HideOptB = value;
                }
            }
            return true;
        }

        /// <summary>读一组颜色（4 个 ColorInfo：底色 / 图案号 / 平铺 / 图案色）。</summary>
        private static bool ReadColors(MsgPackCursor cur, List<string> colors)
        {
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                return false;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                string text;
                if (!ReadColorInfo(cur, out text))
                {
                    return false;
                }
                colors.Add(text);
            }
            return true;
        }

        /// <summary>读一个 ColorInfo（baseColor / pattern / tiling / patternColor）。</summary>
        private static bool ReadColorInfo(MsgPackCursor cur, out string text)
        {
            text = null;
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                return false;
            }
            string baseColor = null;
            string patternColor = null;
            string tiling = null;
            string pattern = null;
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                if (key == "baseColor")
                {
                    if (!ReadColorValue(cur, out baseColor))
                    {
                        return false;
                    }
                    continue;
                }
                if (key == "patternColor")
                {
                    if (!ReadColorValue(cur, out patternColor))
                    {
                        return false;
                    }
                    continue;
                }
                if (key == "tiling")
                {
                    if (!ReadTiling(cur, out tiling))
                    {
                        return false;
                    }
                    continue;
                }
                if (key == "pattern")
                {
                    long number;
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    pattern = number.ToString(CultureInfo.InvariantCulture);
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            text = "底色 " + (baseColor == null ? "?" : baseColor)
                + " · 图案 " + (pattern == null ? "?" : pattern)
                + " · 平铺 " + (tiling == null ? "?" : tiling)
                + " · 图案色 " + (patternColor == null ? "?" : patternColor);
            return true;
        }

        /// <summary>读一个颜色值（Color 序列化为 4 个浮点）——转成 #RRGGBB 文本。</summary>
        private static bool ReadColorValue(MsgPackCursor cur, out string text)
        {
            double[] rgba;
            text = null;
            if (!ReadDoubles(cur, 3, out rgba))
            {
                return false;
            }
            text = HexColor(rgba[0], rgba[1], rgba[2]);
            return true;
        }

        /// <summary>读平铺（Vector2 两个浮点）。</summary>
        private static bool ReadTiling(MsgPackCursor cur, out string text)
        {
            double[] xy;
            text = null;
            if (!ReadDoubles(cur, 2, out xy))
            {
                return false;
            }
            text = Format(xy[0]) + "×" + Format(xy[1]);
            return true;
        }

        /// <summary>读一个浮点数组（至少 want 项；多出的照样消费）。</summary>
        private static bool ReadDoubles(MsgPackCursor cur, int want, out double[] values)
        {
            values = null;
            int count;
            if (!cur.TryReadArrayHeader(out count) || count < want)
            {
                return false;
            }
            double[] all = new double[count];
            for (int i = 0; i < count; i = i + 1)
            {
                if (!cur.TryReadDouble(out all[i]))
                {
                    return false;
                }
            }
            values = all;
            return true;
        }

        /// <summary>浮点显示（最多三位小数，去尾零）。</summary>
        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>颜色文本（#RRGGBB，分量裁到 0–255）。</summary>
        private static string HexColor(double r, double g, double b)
        {
            return "#" + Channel(r).ToString("X2") + Channel(g).ToString("X2") + Channel(b).ToString("X2");
        }

        /// <summary>浮点分量 → 0–255 整数（越界裁剪）。</summary>
        private static int Channel(double value)
        {
            int n = (int)Math.Round(value * 255.0);
            if (n < 0)
            {
                n = 0;
            }
            if (n > 255)
            {
                n = 255;
            }
            return n;
        }

        /// <summary>解析饰品段（ChaFileAccessory：version / parts[20]）。</summary>
        private static void ParseAccessory(byte[] buf, int at, int len, CardCoordinateOutfit outfit)
        {
            MsgPackCursor cur = new MsgPackCursor(buf, at);
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                outfit.Warnings.Add("饰品段不是 MessagePack map");
                return;
            }
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    outfit.Warnings.Add("饰品段键读取失败（第 " + (i + 1) + " 项）");
                    return;
                }
                if (key == "parts")
                {
                    ReadAccessoryParts(cur, outfit);
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    outfit.Warnings.Add("饰品段跳过值失败（键 " + key + "）");
                    return;
                }
            }
            int consumed = cur.Position - at;
            if (consumed != len)
            {
                outfit.Warnings.Add("饰品段长度不符（声明 " + len.ToString("N0") + " · 实读 " + consumed.ToString("N0") + "）");
            }
        }

        /// <summary>解析饰品数组（20 个槽位）。</summary>
        private static void ReadAccessoryParts(MsgPackCursor cur, CardCoordinateOutfit outfit)
        {
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                outfit.Warnings.Add("饰品 parts 不是数组");
                return;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                CardAccessoryPart part = new CardAccessoryPart();
                part.Index = i;
                if (!ReadAccessoryPart(cur, part))
                {
                    outfit.Warnings.Add("第 " + (i + 1) + " 件饰品读取中断");
                    return;
                }
                outfit.Accessories.Add(part);
            }
            if (count != 20)
            {
                outfit.Warnings.Add("饰品槽位数与预期不符（读到 " + count + " · 预期 20）");
            }
        }

        /// <summary>读一件饰品——type / id / parentKey / color[4] / hideCategory / noShake。</summary>
        private static bool ReadAccessoryPart(MsgPackCursor cur, CardAccessoryPart part)
        {
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                return false;
            }
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                long number;
                if (key == "type")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.Type = (int)number;
                    part.TypeName = AccessoryTypeName(part.Type);
                    continue;
                }
                if (key == "id")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.Id = (int)number;
                    continue;
                }
                if (key == "hideCategory")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        return false;
                    }
                    part.HideCategory = (int)number;
                    continue;
                }
                if (key == "noShake")
                {
                    bool flag;
                    if (!cur.TryReadBool(out flag))
                    {
                        return false;
                    }
                    part.NoShake = flag;
                    continue;
                }
                if (key == "parentKey")
                {
                    string value;
                    if (!cur.TryReadString(out value))
                    {
                        return false;
                    }
                    part.ParentKey = value;
                    part.ParentName = AccessoryParentName(value);
                    continue;
                }
                if (key == "color")
                {
                    if (!ReadAccessoryColors(cur, part))
                    {
                        return false;
                    }
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>读饰品的四组颜色。</summary>
        private static bool ReadAccessoryColors(MsgPackCursor cur, CardAccessoryPart part)
        {
            int count;
            if (!cur.TryReadArrayHeader(out count))
            {
                return false;
            }
            for (int i = 0; i < count; i = i + 1)
            {
                string text;
                if (!ReadColorValue(cur, out text))
                {
                    return false;
                }
                part.Colors.Add(text);
            }
            return true;
        }

        /// <summary>解析化妆段（ChaFileMakeup：眼影 / 脸颊 / 口红 / 涂装两项）。</summary>
        private static void ParseMakeup(byte[] buf, int at, int len, CardCoordinateOutfit outfit)
        {
            MsgPackCursor cur = new MsgPackCursor(buf, at);
            int mapCount;
            if (!cur.TryReadMapHeader(out mapCount))
            {
                outfit.Warnings.Add("化妆段不是 MessagePack map");
                return;
            }
            int eyeshadow = -1;
            int cheek = -1;
            int lip = -1;
            List<int> paint = new List<int>();
            for (int i = 0; i < mapCount; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    outfit.Warnings.Add("化妆段键读取失败（第 " + (i + 1) + " 项）");
                    return;
                }
                long number;
                if (key == "eyeshadowId")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        outfit.Warnings.Add("化妆段眼影读取失败");
                        return;
                    }
                    eyeshadow = (int)number;
                    continue;
                }
                if (key == "cheekId")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        outfit.Warnings.Add("化妆段脸颊读取失败");
                        return;
                    }
                    cheek = (int)number;
                    continue;
                }
                if (key == "lipId")
                {
                    if (!cur.TryReadLong(out number))
                    {
                        outfit.Warnings.Add("化妆段口红读取失败");
                        return;
                    }
                    lip = (int)number;
                    continue;
                }
                if (key == "paintId")
                {
                    int count;
                    if (!cur.TryReadArrayHeader(out count))
                    {
                        outfit.Warnings.Add("化妆段涂装读取失败");
                        return;
                    }
                    for (int k = 0; k < count; k = k + 1)
                    {
                        if (!cur.TryReadLong(out number))
                        {
                            outfit.Warnings.Add("化妆段涂装项读取失败");
                            return;
                        }
                        paint.Add((int)number);
                    }
                    continue;
                }
                if (!cur.TrySkipValue())
                {
                    outfit.Warnings.Add("化妆段跳过值失败（键 " + key + "）");
                    return;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("眼影 " + Text(eyeshadow));
            sb.Append(" · 脸颊 " + Text(cheek));
            sb.Append(" · 口红 " + Text(lip));
            sb.Append(" · 涂装 ");
            if (paint.Count == 0)
            {
                sb.Append("—");
            }
            else
            {
                for (int i = 0; i < paint.Count; i = i + 1)
                {
                    if (i > 0)
                    {
                        sb.Append("/");
                    }
                    sb.Append(paint[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            outfit.MakeupText = sb.ToString();
            int consumed = cur.Position - at;
            if (consumed != len)
            {
                outfit.Warnings.Add("化妆段长度不符（声明 " + len.ToString("N0") + " · 实读 " + consumed.ToString("N0") + "）");
            }
        }

        /// <summary>读到的整数文本（-1 = 没读到）。</summary>
        private static string Text(int value)
        {
            if (value < 0)
            {
                return "—";
            }
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>读小端 32 位整数（Coordinate 分段长度）。</summary>
        private static bool ReadInt32(byte[] buf, ref int pos, int end, out int value)
        {
            value = 0;
            if (pos < 0 || pos + 4 > end || pos + 4 > buf.Length)
            {
                return false;
            }
            value = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
            pos = pos + 4;
            return true;
        }

        /// <summary>读 MessagePack 数组头（fixarray / array16 / array32）。</summary>
        private static bool ReadArrayHeader(byte[] buf, ref int pos, out int count)
        {
            count = 0;
            if (pos >= buf.Length)
            {
                return false;
            }
            byte b = buf[pos];
            if (b >= 0x90 && b <= 0x9F)
            {
                count = b - 0x90;
                pos = pos + 1;
                return true;
            }
            if (b == 0xDC && pos + 3 <= buf.Length)
            {
                count = (buf[pos + 1] << 8) | buf[pos + 2];
                pos = pos + 3;
                return true;
            }
            if (b == 0xDD && pos + 5 <= buf.Length)
            {
                count = (buf[pos + 1] << 24) | (buf[pos + 2] << 16) | (buf[pos + 3] << 8) | buf[pos + 4];
                pos = pos + 5;
                return true;
            }
            return false;
        }

        /// <summary>读 MessagePack bin 头（bin8 / bin16 / bin32）。</summary>
        private static bool ReadBinHeader(byte[] buf, ref int pos, out int length)
        {
            length = 0;
            if (pos >= buf.Length)
            {
                return false;
            }
            byte b = buf[pos];
            if (b == 0xC4 && pos + 2 <= buf.Length)
            {
                length = buf[pos + 1];
                pos = pos + 2;
                return true;
            }
            if (b == 0xC5 && pos + 3 <= buf.Length)
            {
                length = (buf[pos + 1] << 8) | buf[pos + 2];
                pos = pos + 3;
                return true;
            }
            if (b == 0xC6 && pos + 5 <= buf.Length)
            {
                length = (buf[pos + 1] << 24) | (buf[pos + 2] << 16) | (buf[pos + 3] << 8) | buf[pos + 4];
                pos = pos + 5;
                return true;
            }
            return false;
        }

        /// <summary>读文件的一段（失败抛异常——由调用方出声）。</summary>
        private static byte[] ReadRange(string path, long at, int count)
        {
            byte[] data = new byte[count];
            using (FileStream fs = File.OpenRead(path))
            {
                fs.Position = at;
                int total = 0;
                while (total < count)
                {
                    int read = fs.Read(data, total, count - total);
                    if (read <= 0)
                    {
                        break;
                    }
                    total = total + read;
                }
                if (total != count)
                {
                    byte[] partial = new byte[total];
                    Array.Copy(data, partial, total);
                    return partial;
                }
            }
            return data;
        }

        /// <summary>按名字取块（找不到返回 null）。</summary>
        private static CardEditBlock FindBlock(CardLayout layout)
        {
            for (int i = 0; i < layout.Blocks.Count; i = i + 1)
            {
                if (layout.Blocks[i].Name == BlockName)
                {
                    return layout.Blocks[i];
                }
            }
            return null;
        }

        /// <summary>现有块名（出声用）。</summary>
        private static string BlockNames(CardLayout layout)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < layout.Blocks.Count; i = i + 1)
            {
                if (i > 0)
                {
                    sb.Append(" / ");
                }
                sb.Append(layout.Blocks[i].Name);
            }
            return sb.ToString();
        }
    }
}
