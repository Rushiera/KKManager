using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using KKManager.Data;

namespace KKManager.Core
{
    /// <summary>
    /// 卡片分项分析落库——服装槽位（coord）/ 卡片分析（detail）/ 场景深度（scene）。
    /// 各存一份 JSON（键为驼峰）；消费面按 kind 反序列化后复用原渲染函数，不重做解析。
    /// </summary>
    public static class CardAnalysis
    {
        private static readonly JsonSerializerOptions Opts = MakeOptions();

        private static JsonSerializerOptions MakeOptions()
        {
            JsonSerializerOptions o = new JsonSerializerOptions();
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            return o;
        }

        /// <summary>执行一个分项步——返回 true 表示该文件该步已完成（可落完成戳）。</summary>
        public static bool Run(Store store, ScanFile f, string stepId, ScanResult result, CardFileSession session)
        {
            if (f.CardId <= 0)
            {
                f.CardId = store.CardIdOf(f.Path);
            }
            if (f.CardId <= 0)
            {
                return false;
            }
            if (stepId == "coord")
            {
                if (!IsChara(f))
                {
                    return true;
                }
                CardCoordinateResult co = CardCoordinate.Read(f.Path, session);
                if (co == null)
                {
                    return false;
                }
                return Save(store, f, "coord", co);
            }
            if (stepId == "detail")
            {
                CardDetailResult det = CardDetail.Parse(f.Path, f.ImageEnd, session);
                if (det == null)
                {
                    return false;
                }
                return Save(store, f, "detail", det);
            }
            if (stepId == "scene")
            {
                if (f.CardType != CardReader.SceneCardType)
                {
                    return true;
                }
                SceneInfoResult scene = SceneReader.Read(f.Path, f.ImageEnd, session);
                if (scene == null)
                {
                    return false;
                }
                // 逐份内嵌角色卡面图——与数据区图片清单对宽高 / 字节（同一遍 detail 扫描供图；对不上由 Attach 出声）
                CardDetailResult det = CardDetail.Parse(f.Path, f.ImageEnd, session);
                if (det != null)
                {
                    scene.AttachCharaFaces(det.Images);
                }
                return Save(store, f, "scene", scene);
            }
            return false;
        }

        /// <summary>序列化并落一份分项分析。</summary>
        private static bool Save(Store store, ScanFile f, string kind, object value)
        {
            string json = JsonSerializer.Serialize(value, value.GetType(), Opts);
            store.SaveCardAnalysis(f.CardId, kind, f.Path, f.Size, f.Mtime, json);
            return true;
        }

        /// <summary>是否人物卡（只有人物卡有 Coordinate 块）。</summary>
        private static bool IsChara(ScanFile f)
        {
            if (f.CardType == null)
            {
                return false;
            }
            return f.CardType.IndexOf("Chara", StringComparison.Ordinal) >= 0;
        }
    }
}
