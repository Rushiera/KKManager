using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>卡片数据区的一个块（lstInfo 目录条目）——名称 / 版本 / 载荷内位置 / 字节数。</summary>
    public class CardEditBlock
    {
        /// <summary>块名（Custom / Coordinate / Parameter / Status / KKEx）。</summary>
        public string Name { get; set; }

        /// <summary>块版本（如 0.0.5）。</summary>
        public string Version { get; set; }

        /// <summary>块在载荷里的位置（相对载荷起点）。</summary>
        public long Pos { get; set; }

        /// <summary>块字节数。</summary>
        public long Size { get; set; }
    }

    /// <summary>卡片数据区布局——图片区终点 / 头段 / 块表 / 载荷起点（只读解析结果，写盘时按它定位）。</summary>
    public class CardLayout
    {
        /// <summary>文件绝对路径。</summary>
        public string FilePath { get; set; }

        /// <summary>文件字节数。</summary>
        public long FileSize { get; set; }

        /// <summary>图片区终点（PNG 的 IEND 结束处）——数据区起点。</summary>
        public long ImageEnd { get; set; }

        /// <summary>卡类型标记（如 【KoiKatuChara】）。</summary>
        public string CardType { get; set; }

        /// <summary>数据区版本标记。</summary>
        public string DataVersion { get; set; }

        /// <summary>脸图在文件里的偏移（人物卡头段记录的 int32 之后）。</summary>
        public long FaceOffset { get; set; }

        /// <summary>脸图字节数。</summary>
        public long FaceSize { get; set; }

        /// <summary>块表字节数（int32）所在偏移。</summary>
        public long TableSizeAt { get; set; }

        /// <summary>块表（MessagePack）起点。</summary>
        public long TableAt { get; set; }

        /// <summary>块表字节数。</summary>
        public long TableSize { get; set; }

        /// <summary>8 字节尾的起点（块表之后）。</summary>
        public long TailAt { get; set; }

        /// <summary>尾部第一个 int32（实测 = 载荷字节数 + 一个常量）。</summary>
        public long TailRaw { get; set; }

        /// <summary>尾部第二个 int32（原样保留）。</summary>
        public long TailSecond { get; set; }

        /// <summary>尾部常量（TailRaw − 载荷字节数）——写盘时按新载荷长度还原该字段。</summary>
        public long TailConst { get; set; }

        /// <summary>载荷起点（块 pos 的基准）。</summary>
        public long PayloadStart { get; set; }

        /// <summary>载荷字节数（文件尾 − 载荷起点）。</summary>
        public long PayloadSize { get; set; }

        /// <summary>块表条目（按表内出现顺序）。</summary>
        public List<CardEditBlock> Blocks { get; } = new List<CardEditBlock>();

        /// <summary>解析错误（null 表示可编辑布局读出）。</summary>
        public string Error { get; set; }
    }

    /// <summary>人物卡 Parameter 块里的可编辑字段（含值在文件里的字节区间——写入按它原位替换）。</summary>
    public class CardParamInfo
    {
        /// <summary>是否读到了 Parameter 块。</summary>
        public bool HasParameter { get; set; }

        /// <summary>姓（lastname）。</summary>
        public string LastName { get; set; }

        /// <summary>名（firstname）。</summary>
        public string FirstName { get; set; }

        /// <summary>爱称（nickname）。</summary>
        public string NickName { get; set; }

        /// <summary>性格（personality）——数值，名称见 <see cref="Personality"/>。</summary>
        public int Personality { get; set; }

        /// <summary>Parameter 块起点（文件内绝对偏移）。</summary>
        public long BlockStart { get; set; }

        /// <summary>Parameter 块字节数。</summary>
        public long BlockSize { get; set; }

        /// <summary>Parameter 块在载荷里的位置（相对载荷起点）。</summary>
        public long BlockPos { get; set; }

        /// <summary>姓的值区间起点（含 MessagePack 头）。</summary>
        public long LastNameAt { get; set; }

        /// <summary>姓的值区间字节数。</summary>
        public int LastNameLen { get; set; }

        /// <summary>名的值区间起点。</summary>
        public long FirstNameAt { get; set; }

        /// <summary>名的值区间字节数。</summary>
        public int FirstNameLen { get; set; }

        /// <summary>爱称的值区间起点。</summary>
        public long NickNameAt { get; set; }

        /// <summary>爱称的值区间字节数。</summary>
        public int NickNameLen { get; set; }

        /// <summary>性格的值区间起点。</summary>
        public long PersonalityAt { get; set; }

        /// <summary>性格的值区间字节数。</summary>
        public int PersonalityLen { get; set; }

        /// <summary>敏感带 / 弱点部位（weakPoint）——数值，名称见 <see cref="WeakPoint"/>。</summary>
        public int WeakPoint { get; set; }

        /// <summary>敏感带的值区间起点。</summary>
        public long WeakPointAt { get; set; }

        /// <summary>敏感带的值区间字节数。</summary>
        public int WeakPointLen { get; set; }

        /// <summary>五项「是否接受」的当前值——顺序见 <see cref="CardEdit.DenialKeys"/>。</summary>
        public bool[] Denial { get; set; } = new bool[5];

        /// <summary>五项「是否接受」各自的值区间起点。</summary>
        public long[] DenialAt { get; set; } = new long[5];

        /// <summary>五项「是否接受」各自的值区间字节数。</summary>
        public int[] DenialLen { get; set; } = new int[5];

        /// <summary>读取错误（null 表示读到了字段）。</summary>
        public string Error { get; set; }
    }

    /// <summary>编辑请求——null 表示不改这一项（空字符串表示改成空）。</summary>
    public class CardParamEdit
    {
        /// <summary>新姓（null = 不改）。</summary>
        public string LastName { get; set; }

        /// <summary>新名（null = 不改）。</summary>
        public string FirstName { get; set; }

        /// <summary>新爱称（null = 不改）。</summary>
        public string NickName { get; set; }

        /// <summary>新性格 ID（null = 不改）。</summary>
        public int? Personality { get; set; }

        /// <summary>新敏感带 ID（null = 不改）。</summary>
        public int? WeakPoint { get; set; }

        /// <summary>五项「是否接受」的新值（null = 不改该项；顺序同 <see cref="CardEdit.DenialKeys"/>）。</summary>
        public bool?[] Denial { get; set; }
    }

    /// <summary>编辑结果——改动摘要 / 留档位置 / 字节增量 / 失败原因。</summary>
    public class CardEditResult
    {
        /// <summary>是否写盘成功。</summary>
        public bool Ok { get; set; }

        /// <summary>失败原因（成功为 null）。</summary>
        public string Error { get; set; }

        /// <summary>改动摘要（如「姓 「藤原」 → 「藤原1」」）。</summary>
        public List<string> Changes { get; } = new List<string>();

        /// <summary>留档文件绝对路径（原版留在这份副本里；未留档为 null）。</summary>
        public string ArchivedFile { get; set; }

        /// <summary>留档文件字节数。</summary>
        public long ArchivedSize { get; set; }

        /// <summary>文件字节增量（新 − 旧）。</summary>
        public long Delta { get; set; }

        /// <summary>写盘后的文件字节数。</summary>
        public long NewSize { get; set; }
    }

    /// <summary>
    /// 卡片数据区字段读写——人物卡 Parameter 块里的姓 / 名 / 爱称 / 性格。
    /// 最小改动原则：只替换被编辑的字节，其余原样搬运；块长变化时同步块表 pos 与尾部载荷字节数；
    /// 写完后把文件的创建 / 修改 / 访问时间与属性还原（附加改动改回跟原来一样）。
    /// 边界：只碰 Parameter 块与块表，不碰图片区与内嵌贴图；卡面预览图 / 脸图替换未实现（留口）。
    /// </summary>
    public static class CardEdit
    {
        /// <summary>块表里的目录键（fixstr(7) "lstInfo"）。</summary>
        private static readonly byte[] LstInfoMark = { 0xA7, 0x6C, 0x73, 0x74, 0x49, 0x6E, 0x66, 0x6F };

        /// <summary>参数块的名字（可编辑字段所在块）。</summary>
        private const string ParamBlockName = "Parameter";

        /// <summary>五项「是否接受」的键名（数组顺序即界面顺序）。</summary>
        public static readonly string[] DenialKeys = { "kiss", "aibu", "anal", "massage", "notCondom" };

        /// <summary>五项「是否接受」的显示名（与 DenialKeys 同序）。</summary>
        public static readonly string[] DenialNames = { "接吻", "爱抚", "肛门", "按摩", "不戴套" };

        /// <summary>解析数据区布局——人物卡：头段 + 脸图 + 块表 + 8 字节尾 + 载荷。失败时 Error 说明原因。</summary>
        public static CardLayout Parse(string path)
        {
            return Parse(path, null);
        }

        /// <summary>解析数据区布局（会话版——图片区终点 / 卡类型走会话缓存，不重复定位；session 为 null 时自行解析）。</summary>
        public static CardLayout Parse(string path, CardFileSession session)
        {
            CardLayout layout = new CardLayout();
            layout.FilePath = path;
            CardStructure st;
            if (session == null || !session.HeadOk)
            {
                if (!File.Exists(path))
                {
                    layout.Error = "卡片文件不存在：" + path;
                    return layout;
                }
                st = CardDocument.Parse(path);
                if (st.Error != null)
                {
                    layout.Error = st.Error;
                    return layout;
                }
            }
            else
            {
                st = new CardStructure();
                st.Size = session.Length;
                st.ImageEnd = session.ImageEnd;
                st.CardType = session.CardType;
                st.DataVersion = session.DataVersion;
            }
            layout.FileSize = st.Size;
            layout.ImageEnd = st.ImageEnd;
            layout.CardType = st.CardType;
            layout.DataVersion = st.DataVersion;
            if (st.ImageEnd <= 0)
            {
                layout.Error = "未找到 PNG 的 IEND——图片区终点未知";
                return layout;
            }
            if (st.CardType == null || st.CardType.IndexOf("Chara", StringComparison.Ordinal) < 0)
            {
                layout.Error = "不是人物卡（卡类型 " + (st.CardType == null ? "<未识别>" : st.CardType) + "）——本轮编辑面只覆盖人物卡的姓 / 名 / 爱称 / 性格";
                return layout;
            }
            FileStream fs = null;
            bool ownStream = !(session != null && session.HeadOk);
            try
            {
                fs = ownStream ? File.OpenRead(path) : session.Stream;
                fs.Position = st.ImageEnd;
                BinaryReader br = new BinaryReader(fs);
                br.ReadInt32();
                CardReader.Read7BitString(br);
                CardReader.Read7BitString(br);
                layout.FaceSize = br.ReadInt32();
                layout.FaceOffset = fs.Position;
                if (layout.FaceSize <= 0 || layout.FaceOffset + layout.FaceSize > st.Size)
                {
                    layout.Error = "脸图长度异常（" + layout.FaceSize + " 字节）——不是预期的人物卡布局";
                    return layout;
                }
                fs.Position = layout.FaceOffset + layout.FaceSize;
                layout.TableSizeAt = fs.Position;
                int tableSize = br.ReadInt32();
                if (tableSize <= 0 || tableSize > 1048576 || layout.TableSizeAt + 4 + tableSize > st.Size)
                {
                    layout.Error = "块表长度异常（" + tableSize + " 字节）——不是预期的人物卡布局";
                    return layout;
                }
                layout.TableSize = tableSize;
                layout.TableAt = fs.Position;
                byte[] table = new byte[tableSize];
                if (ReadExact(fs, table, tableSize) != tableSize)
                {
                    layout.Error = "块表读取不完整";
                    return layout;
                }
                if (!ParseTable(table, layout))
                {
                    layout.Error = "块表不是预期的 lstInfo 结构（" + Hex(table, 24) + "）";
                    return layout;
                }
                layout.TailAt = layout.TableAt + tableSize;
                fs.Position = layout.TailAt;
                layout.TailRaw = br.ReadInt32();
                layout.TailSecond = br.ReadInt32();
                layout.PayloadStart = layout.TailAt + 8;
                layout.PayloadSize = st.Size - layout.PayloadStart;
                layout.TailConst = layout.TailRaw - layout.PayloadSize;
                if (layout.PayloadSize <= 0)
                {
                    layout.Error = "载荷长度为 0——不是预期的人物卡布局";
                    return layout;
                }
            }
            catch (Exception ex)
            {
                layout.Error = "数据区布局解析异常：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                if (ownStream && fs != null)
                {
                    fs.Dispose();
                }
            }
            return layout;
        }

        /// <summary>解析块表（MessagePack：{"lstInfo":[{name,version,pos,size}…]}）。</summary>
        private static bool ParseTable(byte[] buf, CardLayout layout)
        {
            if (buf.Length < 16 || !MatchAt(buf, 0, LstInfoMark, 1))
            {
                return false;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                return false;
            }
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                if (key != "lstInfo")
                {
                    if (!cur.TrySkipValue())
                    {
                        return false;
                    }
                    continue;
                }
                int count;
                if (!cur.TryReadArrayHeader(out count))
                {
                    return false;
                }
                for (int k = 0; k < count; k = k + 1)
                {
                    if (!ReadBlockEntry(cur, layout))
                    {
                        return false;
                    }
                }
            }
            return layout.Blocks.Count > 0;
        }

        /// <summary>读一条块表条目（map：name / version / pos / size）。</summary>
        private static bool ReadBlockEntry(MsgPackCursor cur, CardLayout layout)
        {
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                return false;
            }
            CardEditBlock b = new CardEditBlock();
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                if (key == "name" || key == "version")
                {
                    string v;
                    if (!cur.TryReadString(out v))
                    {
                        return false;
                    }
                    if (key == "name")
                    {
                        b.Name = v;
                    }
                    else
                    {
                        b.Version = v;
                    }
                }
                else if (key == "pos" || key == "size")
                {
                    long v;
                    if (!cur.TryReadLong(out v))
                    {
                        return false;
                    }
                    if (key == "pos")
                    {
                        b.Pos = v;
                    }
                    else
                    {
                        b.Size = v;
                    }
                }
                else if (!cur.TrySkipValue())
                {
                    return false;
                }
            }
            if (b.Name == null || b.Name.Length == 0)
            {
                return false;
            }
            layout.Blocks.Add(b);
            return true;
        }

        /// <summary>读 Parameter 块里的四个字段（含值在文件里的字节区间）。</summary>
        public static CardParamInfo ReadParams(string path, CardLayout layout)
        {
            return ReadParams(path, layout, null);
        }

        /// <summary>读 Parameter 块里的字段（会话版——复用段内已打开的句柄）。</summary>
        public static CardParamInfo ReadParams(string path, CardLayout layout, CardFileSession session)
        {
            CardParamInfo info = new CardParamInfo();
            if (layout == null || layout.Error != null)
            {
                info.Error = layout == null ? "布局未解析" : layout.Error;
                return info;
            }
            CardEditBlock block = FindBlock(layout, ParamBlockName);
            if (block == null)
            {
                info.Error = "这张卡的数据块里没有 " + ParamBlockName + "（现有块：" + BlockNames(layout) + "）";
                return info;
            }
            info.BlockPos = block.Pos;
            info.BlockSize = block.Size;
            info.BlockStart = layout.PayloadStart + block.Pos;
            byte[] buf;
            if (session != null)
            {
                buf = session.Read(info.BlockStart, (int)block.Size);
            }
            else
            {
                buf = ReadRange(path, info.BlockStart, block.Size);
            }
            if (buf == null)
            {
                info.Error = "Parameter 块读取失败";
                return info;
            }
            MsgPackCursor cur = new MsgPackCursor(buf, 0);
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                info.Error = "Parameter 块不是 MessagePack 映射";
                return info;
            }
            info.HasParameter = true;
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    break;
                }
                int at = cur.Position;
                if (key == "lastname" || key == "firstname" || key == "nickname")
                {
                    string v;
                    if (!cur.TryReadString(out v))
                    {
                        break;
                    }
                    if (key == "lastname")
                    {
                        info.LastName = v;
                        info.LastNameAt = info.BlockStart + at;
                        info.LastNameLen = cur.Position - at;
                    }
                    else if (key == "firstname")
                    {
                        info.FirstName = v;
                        info.FirstNameAt = info.BlockStart + at;
                        info.FirstNameLen = cur.Position - at;
                    }
                    else
                    {
                        info.NickName = v;
                        info.NickNameAt = info.BlockStart + at;
                        info.NickNameLen = cur.Position - at;
                    }
                }
                else if (key == "personality")
                {
                    long v;
                    if (!cur.TryReadLong(out v))
                    {
                        break;
                    }
                    info.Personality = (int)v;
                    info.PersonalityAt = info.BlockStart + at;
                    info.PersonalityLen = cur.Position - at;
                }
                else if (key == "weakPoint")
                {
                    long v;
                    if (!cur.TryReadLong(out v))
                    {
                        break;
                    }
                    info.WeakPoint = (int)v;
                    info.WeakPointAt = info.BlockStart + at;
                    info.WeakPointLen = cur.Position - at;
                }
                else if (key == "denial")
                {
                    if (!ReadDenial(cur, info))
                    {
                        break;
                    }
                }
                else if (!cur.TrySkipValue())
                {
                    break;
                }
            }
            return info;
        }

        /// <summary>读「是否接受」五项（denial 嵌套映射：kiss / aibu / anal / massage / notCondom）；失败返回 false。</summary>
        private static bool ReadDenial(MsgPackCursor cur, CardParamInfo info)
        {
            int keys;
            if (!cur.TryReadMapHeader(out keys))
            {
                return false;
            }
            for (int i = 0; i < keys; i = i + 1)
            {
                string key;
                if (!cur.TryReadString(out key))
                {
                    return false;
                }
                int at = cur.Position;
                bool v;
                if (!cur.TryReadBool(out v))
                {
                    return false;
                }
                int slot = DenialSlot(key);
                if (slot >= 0)
                {
                    info.Denial[slot] = v;
                    info.DenialAt[slot] = info.BlockStart + at;
                    info.DenialLen[slot] = cur.Position - at;
                }
            }
            return true;
        }

        /// <summary>「是否接受」某项在数组里的位置（未知键返回 -1）。</summary>
        private static int DenialSlot(string key)
        {
            for (int i = 0; i < DenialKeys.Length; i = i + 1)
            {
                if (key == DenialKeys[i])
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>布尔字段的显示文本（是 / 否）。</summary>
        private static string ShowBool(bool v)
        {
            if (v)
            {
                return "是";
            }
            return "否";
        }

        /// <summary>把布尔编成 MessagePack（true = 0xC3 / false = 0xC2）——与游戏写卡片时一致。</summary>
        private static byte[] MsgPackBool(bool v)
        {
            byte[] one = new byte[1];
            if (v)
            {
                one[0] = 0xC3;
            }
            else
            {
                one[0] = 0xC2;
            }
            return one;
        }

        /// <summary>敏感带的显示文本（名称 + ID；表外显示为未命名）。</summary>
        private static string ShowWeakPoint(int id)
        {
            string name = WeakPoint.NameOf(id);
            if (name == null)
            {
                return "未命名（值 " + id + "）";
            }
            return name;
        }

        /// <summary>
        /// 写入可编辑字段（null = 不改这一项）。原版先留档到 archiveDir（非空时），再最小改动写回，最后还原文件时间与属性。
        /// </summary>
        public static CardEditResult Apply(string path, CardLayout layout, CardParamInfo cur, CardParamEdit edit, string archiveDir)
        {
            CardEditResult result = new CardEditResult();
            if (layout == null || layout.Error != null)
            {
                result.Error = layout == null ? "布局未解析" : layout.Error;
                return result;
            }
            if (cur == null || cur.Error != null)
            {
                result.Error = cur == null ? "字段未读取" : cur.Error;
                return result;
            }
            if (!cur.HasParameter)
            {
                result.Error = "这张卡没有可编辑的 " + ParamBlockName + " 块";
                return result;
            }

            // [段1] 逐字段算替换——值没变就不动（最小改动）
            List<CardValueReplace> reps = new List<CardValueReplace>();
            AddTextReplace(reps, "姓", cur.LastName, edit.LastName, cur.LastNameAt, cur.LastNameLen, cur.BlockStart, result.Changes);
            AddTextReplace(reps, "名", cur.FirstName, edit.FirstName, cur.FirstNameAt, cur.FirstNameLen, cur.BlockStart, result.Changes);
            AddTextReplace(reps, "爱称", cur.NickName, edit.NickName, cur.NickNameAt, cur.NickNameLen, cur.BlockStart, result.Changes);
            if (edit.Personality.HasValue && edit.Personality.Value != cur.Personality)
            {
                CardValueReplace r = new CardValueReplace();
                r.At = cur.PersonalityAt - cur.BlockStart;
                r.Len = cur.PersonalityLen;
                r.Bytes = MsgPackInt(edit.Personality.Value);
                reps.Add(r);
                result.Changes.Add("性格 " + ShowPersonality(cur.Personality) + " → " + ShowPersonality(edit.Personality.Value));
            }
            if (edit.WeakPoint.HasValue && edit.WeakPoint.Value != cur.WeakPoint)
            {
                if (cur.WeakPointLen == 0)
                {
                    result.Error = "这张卡里没有敏感带字段——未写盘";
                    return result;
                }
                CardValueReplace r = new CardValueReplace();
                r.At = cur.WeakPointAt - cur.BlockStart;
                r.Len = cur.WeakPointLen;
                r.Bytes = MsgPackInt(edit.WeakPoint.Value);
                reps.Add(r);
                result.Changes.Add("敏感带 " + ShowWeakPoint(cur.WeakPoint) + " → " + ShowWeakPoint(edit.WeakPoint.Value));
            }
            if (edit.Denial != null)
            {
                for (int i = 0; i < DenialKeys.Length && i < edit.Denial.Length; i = i + 1)
                {
                    if (!edit.Denial[i].HasValue || edit.Denial[i].Value == cur.Denial[i])
                    {
                        continue;
                    }
                    if (cur.DenialLen[i] == 0)
                    {
                        result.Error = "这张卡里没有「接受" + DenialNames[i] + "」字段——未写盘";
                        return result;
                    }
                    CardValueReplace r = new CardValueReplace();
                    r.At = cur.DenialAt[i] - cur.BlockStart;
                    r.Len = cur.DenialLen[i];
                    r.Bytes = MsgPackBool(edit.Denial[i].Value);
                    reps.Add(r);
                    result.Changes.Add("是否接受「" + DenialNames[i] + "」 " + ShowBool(cur.Denial[i]) + " → " + ShowBool(edit.Denial[i].Value));
                }
            }
            if (reps.Count == 0)
            {
                result.Error = "没有字段发生变化——未写盘";
                return result;
            }
            if (cur.BlockSize > 16777216)
            {
                result.Error = "Parameter 块过大（" + cur.BlockSize + " 字节）——拒绝改写";
                return result;
            }

            // [段2] 组装新块字节（按偏移升序拼接）
            byte[] block = ReadRange(path, cur.BlockStart, cur.BlockSize);
            if (block == null)
            {
                result.Error = "Parameter 块读取失败";
                return result;
            }
            reps.Sort(CompareReplace);
            MemoryStream ms = new MemoryStream();
            long cursor = 0;
            for (int i = 0; i < reps.Count; i = i + 1)
            {
                CardValueReplace r = reps[i];
                if (r.At < cursor || r.At + r.Len > cur.BlockSize)
                {
                    result.Error = "字段区间越界（@" + r.At + " +" + r.Len + "）——拒绝改写";
                    return result;
                }
                ms.Write(block, (int)cursor, (int)(r.At - cursor));
                ms.Write(r.Bytes, 0, r.Bytes.Length);
                cursor = r.At + r.Len;
            }
            ms.Write(block, (int)cursor, (int)(cur.BlockSize - cursor));
            byte[] newBlock = ms.ToArray();
            result.Delta = newBlock.Length - cur.BlockSize;

            // [段3] 原版留档（原版留在软件内部）+ 写盘
            FileInfo fi = new FileInfo(path);
            DateTime created = fi.CreationTimeUtc;
            DateTime written = fi.LastWriteTimeUtc;
            DateTime accessed = fi.LastAccessTimeUtc;
            FileAttributes attrs = fi.Attributes;
            if (!string.IsNullOrEmpty(archiveDir))
            {
                string archived = Archive(path, archiveDir, DateTime.Now);
                if (archived == null)
                {
                    result.Error = "原版留档失败——未写盘（留档目录不可写？" + archiveDir + "）";
                    return result;
                }
                result.ArchivedFile = archived;
                result.ArchivedSize = new FileInfo(archived).Length;
            }
            if (result.Delta == 0)
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    fs.Position = cur.BlockStart;
                    fs.Write(newBlock, 0, newBlock.Length);
                    fs.Flush(true);
                }
            }
            else
            {
                string werr = RewriteFile(path, layout, cur, newBlock, result.Delta);
                if (werr != null)
                {
                    result.Error = werr;
                    return result;
                }
            }
            result.NewSize = fi.Exists ? new FileInfo(path).Length : layout.FileSize + result.Delta;

            // [段4] 还原时间与属性——附加改动改回跟原来一样
            File.SetCreationTimeUtc(path, created);
            File.SetLastWriteTimeUtc(path, written);
            File.SetLastAccessTimeUtc(path, accessed);
            File.SetAttributes(path, attrs);
            result.Ok = true;
            return result;
        }

        /// <summary>整文件重写——块长变化时用：头段原样搬运，块表与尾部按新值重编码，载荷逐块搬（Parameter 用新字节）。</summary>
        private static string RewriteFile(string path, CardLayout layout, CardParamInfo cur, byte[] newBlock, long delta)
        {
            List<CardEditBlock> orig = new List<CardEditBlock>();
            List<CardEditBlock> next = new List<CardEditBlock>();
            long payloadSize = 0;
            for (int i = 0; i < layout.Blocks.Count; i = i + 1)
            {
                CardEditBlock b = layout.Blocks[i];
                orig.Add(b);
                CardEditBlock n = new CardEditBlock();
                n.Name = b.Name;
                n.Version = b.Version;
                n.Pos = b.Pos;
                n.Size = b.Size;
                if (b.Name == ParamBlockName)
                {
                    n.Size = newBlock.Length;
                }
                else if (b.Pos > cur.BlockPos)
                {
                    n.Pos = b.Pos + delta;
                }
                payloadSize = payloadSize + n.Size;
                next.Add(n);
            }
            orig.Sort(CompareBlock);
            byte[] table = EncodeTable(next);
            long newTailRaw = payloadSize + layout.TailConst;
            string tmp = path + ".kcmedit.tmp";
            try
            {
                using (FileStream src = File.OpenRead(path))
                using (FileStream dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    CopyRange(src, dst, 0, layout.TableSizeAt);
                    WriteInt32(dst, table.Length);
                    dst.Write(table, 0, table.Length);
                    WriteInt32(dst, newTailRaw);
                    WriteInt32(dst, layout.TailSecond);
                    for (int i = 0; i < orig.Count; i = i + 1)
                    {
                        CardEditBlock b = orig[i];
                        if (b.Name == ParamBlockName)
                        {
                            dst.Write(newBlock, 0, newBlock.Length);
                        }
                        else
                        {
                            CopyRange(src, dst, layout.PayloadStart + b.Pos, b.Size);
                        }
                    }
                    dst.Flush(true);
                }
                File.Move(tmp, path, true);
                return null;
            }
            catch (Exception ex)
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (IOException cleanup)
                    {
                        // 清理失败不掩盖主错误——出声留痕（主错误由返回值带出）
                        Console.WriteLine("[卡片编辑] 临时文件清理失败：" + cleanup.Message);
                    }
                }
                return "整文件重写失败：" + ex.GetType().Name + " " + ex.Message;
            }
        }

        /// <summary>把原文件复制进留档目录（原版留在软件内部）；失败返回 null（不静默——调用方出声）。</summary>
        public static string Archive(string path, string archiveDir, DateTime stamp)
        {
            try
            {
                Directory.CreateDirectory(archiveDir);
                string name = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                string target = Path.Combine(archiveDir, stamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + name + ext);
                File.Copy(path, target, false);
                return target;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[卡片编辑] 留档失败：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        /// <summary>把字符串字段加进替换清单（值没变则不加）。</summary>
        private static void AddTextReplace(List<CardValueReplace> reps, string label, string oldValue, string newValue, long valueAt, int valueLen, long blockStart, List<string> changes)
        {
            if (newValue == null)
            {
                return;
            }
            string old = oldValue == null ? "" : oldValue;
            if (old == newValue)
            {
                return;
            }
            CardValueReplace r = new CardValueReplace();
            r.At = valueAt - blockStart;
            r.Len = valueLen;
            r.Bytes = MsgPackStr(newValue);
            reps.Add(r);
            changes.Add(label + " " + ShowText(old) + " → " + ShowText(newValue));
        }

        /// <summary>性格显示文本（名称 + ID）。</summary>
        private static string ShowPersonality(int id)
        {
            string name = Personality.NameOf(id);
            string tag = id.ToString(CultureInfo.InvariantCulture);
            if (name == null)
            {
                return "未收录（ID " + tag + "）";
            }
            return name + "（ID " + tag + "）";
        }

        /// <summary>字段值显示文本（空值标注，避免「空 vs 未改」看不出来）。</summary>
        private static string ShowText(string s)
        {
            if (s == null || s.Length == 0)
            {
                return "<空>";
            }
            return "「" + s + "」";
        }

        /// <summary>按名字取块。</summary>
        private static CardEditBlock FindBlock(CardLayout layout, string name)
        {
            for (int i = 0; i < layout.Blocks.Count; i = i + 1)
            {
                if (layout.Blocks[i].Name == name)
                {
                    return layout.Blocks[i];
                }
            }
            return null;
        }

        /// <summary>现有块名清单（出声用）。</summary>
        private static string BlockNames(CardLayout layout)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < layout.Blocks.Count; i = i + 1)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" · ");
                }
                sb.Append(layout.Blocks[i].Name);
            }
            return sb.Length == 0 ? "<无>" : sb.ToString();
        }

        /// <summary>编码块表（与游戏同形：{"lstInfo":[{name,version,pos,size}…]}）——值未变时与原表逐字节相同。</summary>
        private static byte[] EncodeTable(List<CardEditBlock> blocks)
        {
            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0x81);
            WriteStr(ms, "lstInfo");
            WriteArrayHeader(ms, blocks.Count);
            for (int i = 0; i < blocks.Count; i = i + 1)
            {
                CardEditBlock b = blocks[i];
                ms.WriteByte(0x84);
                WriteStr(ms, "name");
                WriteStr(ms, b.Name == null ? "" : b.Name);
                WriteStr(ms, "version");
                WriteStr(ms, b.Version == null ? "" : b.Version);
                WriteStr(ms, "pos");
                byte[] pos = MsgPackInt(b.Pos);
                ms.Write(pos, 0, pos.Length);
                WriteStr(ms, "size");
                byte[] size = MsgPackInt(b.Size);
                ms.Write(size, 0, size.Length);
            }
            return ms.ToArray();
        }

        /// <summary>写数组头（≤15 用 fixarray，其余 array16）。</summary>
        private static void WriteArrayHeader(MemoryStream ms, int count)
        {
            if (count <= 15)
            {
                ms.WriteByte((byte)(0x90 | count));
                return;
            }
            ms.WriteByte(0xDC);
            ms.WriteByte((byte)(count >> 8));
            ms.WriteByte((byte)count);
        }

        /// <summary>写 MessagePack 字符串（最短形态）。</summary>
        private static void WriteStr(MemoryStream ms, string s)
        {
            byte[] body = Encoding.UTF8.GetBytes(s == null ? "" : s);
            if (body.Length <= 31)
            {
                ms.WriteByte((byte)(0xA0 | body.Length));
            }
            else if (body.Length <= 255)
            {
                ms.WriteByte(0xD9);
                ms.WriteByte((byte)body.Length);
            }
            else if (body.Length <= 65535)
            {
                ms.WriteByte(0xDA);
                ms.WriteByte((byte)(body.Length >> 8));
                ms.WriteByte((byte)body.Length);
            }
            else
            {
                ms.WriteByte(0xDB);
                ms.WriteByte((byte)(body.Length >> 24));
                ms.WriteByte((byte)(body.Length >> 16));
                ms.WriteByte((byte)(body.Length >> 8));
                ms.WriteByte((byte)body.Length);
            }
            ms.Write(body, 0, body.Length);
        }

        /// <summary>把字符串编成 MessagePack（最短形态——与游戏写卡片时一致）。</summary>
        private static byte[] MsgPackStr(string s)
        {
            MemoryStream ms = new MemoryStream();
            WriteStr(ms, s);
            return ms.ToArray();
        }

        /// <summary>把整数编成 MessagePack（最短形态——与游戏写卡片时一致）。</summary>
        private static byte[] MsgPackInt(long v)
        {
            if (v >= 0)
            {
                if (v <= 127)
                {
                    return new byte[] { (byte)v };
                }
                if (v <= 255)
                {
                    return new byte[] { 0xCC, (byte)v };
                }
                if (v <= 65535)
                {
                    return new byte[] { 0xCD, (byte)(v >> 8), (byte)v };
                }
                if (v <= 4294967295L)
                {
                    return new byte[] { 0xCE, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                }
                return new byte[] { 0xCF, (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            }
            if (v >= -32)
            {
                return new byte[] { (byte)(0x100 + v) };
            }
            if (v >= -128)
            {
                return new byte[] { 0xD0, (byte)v };
            }
            if (v >= -32768)
            {
                return new byte[] { 0xD1, (byte)(v >> 8), (byte)v };
            }
            if (v >= -2147483648L)
            {
                return new byte[] { 0xD2, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            }
            return new byte[] { 0xD3, (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        /// <summary>替换项排序——按偏移升序。</summary>
        private static int CompareReplace(CardValueReplace a, CardValueReplace b)
        {
            return a.At.CompareTo(b.At);
        }

        /// <summary>块按 pos 升序。</summary>
        private static int CompareBlock(CardEditBlock a, CardEditBlock b)
        {
            return a.Pos.CompareTo(b.Pos);
        }

        /// <summary>读文件的一个区间（失败返回 null——失败可见由调用方出声）。</summary>
        private static byte[] ReadRange(string path, long at, long count)
        {
            if (at < 0 || count < 0 || count > int.MaxValue)
            {
                return null;
            }
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (at + count > fs.Length)
                    {
                        return null;
                    }
                    fs.Position = at;
                    byte[] buf = new byte[count];
                    if (ReadExact(fs, buf, (int)count) != count)
                    {
                        return null;
                    }
                    return buf;
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>从源文件搬一段到目标流。</summary>
        private static void CopyRange(FileStream src, FileStream dst, long at, long count)
        {
            src.Position = at;
            byte[] buf = new byte[1 << 20];
            long left = count;
            while (left > 0)
            {
                int take = left < buf.Length ? (int)left : buf.Length;
                int n = src.Read(buf, 0, take);
                if (n <= 0)
                {
                    throw new IOException("读取源文件失败 @" + src.Position + " 剩 " + left + " 字节");
                }
                dst.Write(buf, 0, n);
                left = left - n;
            }
        }

        /// <summary>写小端 32 位整数（卡片头段与尾部字段的字节序）。</summary>
        private static void WriteInt32(FileStream fs, long value)
        {
            byte[] b = new byte[4];
            b[0] = (byte)value;
            b[1] = (byte)(value >> 8);
            b[2] = (byte)(value >> 16);
            b[3] = (byte)(value >> 24);
            fs.Write(b, 0, 4);
        }

        /// <summary>读满指定字节数（不足返回实际字节数）。</summary>
        private static int ReadExact(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total = total + n;
            }
            return total;
        }

        /// <summary>在缓冲区指定位置匹配字节序列。</summary>
        private static bool MatchAt(byte[] buf, int at, byte[] pattern, int offset)
        {
            if (at + offset + pattern.Length > buf.Length)
            {
                return false;
            }
            for (int i = 0; i < pattern.Length; i = i + 1)
            {
                if (buf[at + offset + i] != pattern[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>缓冲区前 n 字节的十六进制（诊断用）。</summary>
        private static string Hex(byte[] buf, int n)
        {
            int take = buf.Length < n ? buf.Length : n;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < take; i = i + 1)
            {
                sb.Append(buf[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>一处待替换的字段值——块内偏移 / 旧长度 / 新字节。</summary>
        private class CardValueReplace
        {
            /// <summary>块内偏移。</summary>
            public long At;

            /// <summary>旧值字节数。</summary>
            public int Len;

            /// <summary>新值的 MessagePack 字节。</summary>
            public byte[] Bytes;
        }
    }
}
