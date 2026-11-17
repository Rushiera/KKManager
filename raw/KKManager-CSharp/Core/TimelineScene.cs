using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace KKManager.Core
{
    /// <summary>XML 元素的一个属性（名 + 原值）——属性无损保留，供后续写回重建元素。</summary>
    public class TimelineAttr
    {
        /// <summary>属性名（原样）。</summary>
        public string Name { get; set; }

        /// <summary>属性值（原样文本）。</summary>
        public string Value { get; set; }
    }

    /// <summary>插值组节点（interpolableGroup）——Timeline 的轨道分组树：对象 → 部位 → 子部位。</summary>
    public class TimelineGroup
    {
        /// <summary>组名（Timeline 界面上的组名，如 全体 / 首・頭 / 表情 / 右手）。</summary>
        public string Name { get; set; }

        /// <summary>父组下标（-1 = 顶层）。</summary>
        public int ParentIndex { get; set; }

        /// <summary>层级深度（顶层 0）。</summary>
        public int Depth { get; set; }

        /// <summary>直接挂在本组下的轨道数（解析后回填）。</summary>
        public int TrackCount { get; set; }

        /// <summary>组元素属性全集（原样）。</summary>
        public List<TimelineAttr> Attributes { get; } = new List<TimelineAttr>();
    }

    /// <summary>一条轨道（interpolable）——被驱动的对象 / 部位 / 属性，带完整关键帧序列。</summary>
    public class TimelineTrack
    {
        /// <summary>所属组下标（-1 = 不属于任何组）。</summary>
        public int GroupIndex { get; set; }

        /// <summary>写入者（owner：Timeline / KKPE / NodesConstraints 等）。</summary>
        public string Owner { get; set; }

        /// <summary>场景对象序号（objectIndex；解析不到 = -1）。</summary>
        public int ObjectIndex { get; set; }

        /// <summary>轨道类型 id（guideObjectPos / guideObjectRot / bonePos / characterEyes / cameraPos / itemColor1 / timeScale …）。</summary>
        public string Id { get; set; }

        /// <summary>轨道显示名（alias——Timeline 界面上那条轨道的名字，可为空）。</summary>
        public string Alias { get; set; }

        /// <summary>被驱动对象路径（guideObjectPath——骨骼 / 道具节点路径，可为空）。</summary>
        public string GuideObjectPath { get; set; }

        /// <summary>轨道是否启用（enabled 属性；缺省按 true）。</summary>
        public bool Enabled { get; set; }

        /// <summary>轨道元素属性全集（原样）。</summary>
        public List<TimelineAttr> Attributes { get; } = new List<TimelineAttr>();

        /// <summary>关键帧序列（按 XML 顺序）。</summary>
        public List<TimelineKeyframe> Keyframes { get; } = new List<TimelineKeyframe>();
    }

    /// <summary>插值曲线控制点（curveKeyframe）——关键帧内的插值曲线，写回时必须保留。</summary>
    public class TimelineCurveKey
    {
        /// <summary>曲线点时刻。</summary>
        public double Time { get; set; }

        /// <summary>曲线点值。</summary>
        public double Value { get; set; }

        /// <summary>入切线。</summary>
        public double InTangent { get; set; }

        /// <summary>出切线。</summary>
        public double OutTangent { get; set; }
    }

    /// <summary>一个关键帧（keyframe）——时刻 + 值（四元数 / 向量 / 标量，按轨道类型取用）。</summary>
    public class TimelineKeyframe
    {
        /// <summary>时刻（秒，XML 原值——浮点，比较需容差）。</summary>
        public double Time { get; set; }

        /// <summary>是否有标量值（value 属性）。</summary>
        public bool HasValue { get; set; }

        /// <summary>标量值（value）。</summary>
        public double Value { get; set; }

        /// <summary>是否有分量值（valueX / valueY / valueZ / valueW）。</summary>
        public bool HasXYZW { get; set; }

        /// <summary>分量 X。</summary>
        public double ValueX { get; set; }

        /// <summary>分量 Y。</summary>
        public double ValueY { get; set; }

        /// <summary>分量 Z。</summary>
        public double ValueZ { get; set; }

        /// <summary>分量 W（四元数的实部；旋转类轨道才有）。</summary>
        public double ValueW { get; set; }

        /// <summary>关键帧元素属性全集（原样）。</summary>
        public List<TimelineAttr> Attributes { get; } = new List<TimelineAttr>();

        /// <summary>插值曲线控制点（curveKeyframe）。</summary>
        public List<TimelineCurveKey> CurveKeys { get; } = new List<TimelineCurveKey>();
    }

    /// <summary>sceneInfo XML 的完整模型——组树 + 轨道 + 关键帧，属性无损保留（后续编辑 / 导出共用同一份格式）。</summary>
    public class TimelineScene
    {
        /// <summary>是否存在 timeline 条目（场景里没用过 Timeline 插件则无）。</summary>
        public bool HasEntry { get; set; }

        /// <summary>空时间轴（有条目但零关键帧——Timeline 的默认状态）。</summary>
        public bool IsEmpty { get; set; }

        /// <summary>时间轴长度（秒）。</summary>
        public double Duration { get; set; }

        /// <summary>时间缩放（1 = 原速）。</summary>
        public double TimeScale { get; set; } = 1;

        /// <summary>分块长度（root 属性 blockLength；写回时原样保留）。</summary>
        public int BlockLength { get; set; }

        /// <summary>分块数（root 属性 divisions；写回时原样保留）。</summary>
        public int Divisions { get; set; }

        /// <summary>根元素属性全集（原样）。</summary>
        public List<TimelineAttr> RootAttributes { get; } = new List<TimelineAttr>();

        /// <summary>组树（扁平存放，父子靠 ParentIndex）。</summary>
        public List<TimelineGroup> Groups { get; } = new List<TimelineGroup>();

        /// <summary>轨道清单（按 XML 顺序）。</summary>
        public List<TimelineTrack> Tracks { get; } = new List<TimelineTrack>();

        /// <summary>sceneInfo XML 的字节数。</summary>
        public long XmlLength { get; set; }

        /// <summary>XML 超出读取上限（模型只含已读部分）。</summary>
        public bool Truncated { get; set; }

        /// <summary>读取 / 解析失败的原因（失败必须可见）。</summary>
        public string Error { get; set; }
    }

    /// <summary>sceneInfo XML → TimelineScene 的解析器（XmlReader 流式；元素属性无损保留）。</summary>
    public static class TimelineSceneParser
    {
        /// <summary>解析 XML 字节（length 为有效字节数）。</summary>
        public static TimelineScene Parse(byte[] xml, int length)
        {
            TimelineScene scene = new TimelineScene();
            if (xml == null || length <= 0)
            {
                scene.Error = "sceneInfo XML 为空";
                return scene;
            }
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.IgnoreComments = true;
            settings.IgnoreWhitespace = true;
            settings.IgnoreProcessingInstructions = true;
            settings.DtdProcessing = DtdProcessing.Ignore;
            try
            {
                using (MemoryStream ms = new MemoryStream(xml, 0, length))
                {
                    using (XmlReader reader = XmlReader.Create(ms, settings))
                    {
                        ParseLoop(reader, scene);
                    }
                }
            }
            catch (Exception ex)
            {
                scene.Error = "sceneInfo 解析失败：" + ex.GetType().Name + " · " + ex.Message;
            }
            return scene;
        }

        /// <summary>主循环——按元素名分派（组栈 / 当前轨道 / 当前关键帧）。</summary>
        private static void ParseLoop(XmlReader reader, TimelineScene scene)
        {
            List<int> groupStack = new List<int>();
            int trackIndex = -1;
            int keyIndex = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string name = reader.Name;
                    if (name == "root")
                    {
                        ReadRoot(reader, scene);
                    }
                    else if (name == "interpolableGroup")
                    {
                        int gi = AddGroup(reader, scene, groupStack);
                        if (!reader.IsEmptyElement && gi >= 0)
                        {
                            groupStack.Add(gi);
                        }
                    }
                    else if (name == "interpolable")
                    {
                        trackIndex = AddTrack(reader, scene, groupStack);
                    }
                    else if (name == "keyframe")
                    {
                        keyIndex = AddKeyframe(reader, scene, trackIndex);
                    }
                    else if (name == "curveKeyframe")
                    {
                        AddCurveKey(reader, scene, trackIndex, keyIndex);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Name == "interpolableGroup")
                    {
                        if (groupStack.Count > 0)
                        {
                            groupStack.RemoveAt(groupStack.Count - 1);
                        }
                    }
                    else if (reader.Name == "interpolable")
                    {
                        trackIndex = -1;
                        keyIndex = -1;
                    }
                    else if (reader.Name == "keyframe")
                    {
                        keyIndex = -1;
                    }
                }
            }
            FillGroupTrackCounts(scene);
        }

        /// <summary>读根元素（duration / timeScale / blockLength / divisions + 属性全集）。</summary>
        private static void ReadRoot(XmlReader reader, TimelineScene scene)
        {
            if (!reader.MoveToFirstAttribute())
            {
                return;
            }
            do
            {
                string name = reader.Name;
                string text = reader.Value;
                scene.RootAttributes.Add(new TimelineAttr { Name = name, Value = text });
                double d;
                if (name == "duration" && TryNum(text, out d))
                {
                    scene.Duration = d;
                }
                else if (name == "timeScale" && TryNum(text, out d) && d > 0)
                {
                    scene.TimeScale = d;
                }
                else if (name == "blockLength" && TryNum(text, out d))
                {
                    scene.BlockLength = (int)d;
                }
                else if (name == "divisions" && TryNum(text, out d))
                {
                    scene.Divisions = (int)d;
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        /// <summary>新增组节点，返回其下标（父 = 当前组栈顶）。</summary>
        private static int AddGroup(XmlReader reader, TimelineScene scene, List<int> groupStack)
        {
            TimelineGroup g = new TimelineGroup();
            g.ParentIndex = groupStack.Count == 0 ? -1 : groupStack[groupStack.Count - 1];
            g.Depth = groupStack.Count;
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    g.Attributes.Add(new TimelineAttr { Name = reader.Name, Value = reader.Value });
                    if (reader.Name == "name")
                    {
                        g.Name = reader.Value;
                    }
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            scene.Groups.Add(g);
            return scene.Groups.Count - 1;
        }

        /// <summary>新增轨道，返回其下标（所属组 = 当前组栈顶）。</summary>
        private static int AddTrack(XmlReader reader, TimelineScene scene, List<int> groupStack)
        {
            TimelineTrack t = new TimelineTrack();
            t.GroupIndex = groupStack.Count == 0 ? -1 : groupStack[groupStack.Count - 1];
            t.ObjectIndex = -1;
            t.Enabled = true;
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    string name = reader.Name;
                    string text = reader.Value;
                    t.Attributes.Add(new TimelineAttr { Name = name, Value = text });
                    if (name == "owner")
                    {
                        t.Owner = text;
                    }
                    else if (name == "id")
                    {
                        t.Id = text;
                    }
                    else if (name == "alias")
                    {
                        t.Alias = text;
                    }
                    else if (name == "guideObjectPath")
                    {
                        t.GuideObjectPath = text;
                    }
                    else if (name == "objectIndex")
                    {
                        int oi;
                        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out oi))
                        {
                            t.ObjectIndex = oi;
                        }
                    }
                    else if (name == "enabled")
                    {
                        t.Enabled = text != "false";
                    }
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            scene.Tracks.Add(t);
            return scene.Tracks.Count - 1;
        }

        /// <summary>新增关键帧，返回其在轨道内的下标（无宿主轨道返回 -1）。</summary>
        private static int AddKeyframe(XmlReader reader, TimelineScene scene, int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= scene.Tracks.Count)
            {
                return -1;
            }
            TimelineKeyframe kf = new TimelineKeyframe();
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    string name = reader.Name;
                    string text = reader.Value;
                    kf.Attributes.Add(new TimelineAttr { Name = name, Value = text });
                    double d;
                    if (name == "time" && TryNum(text, out d))
                    {
                        kf.Time = d;
                    }
                    else if (name == "value" && TryNum(text, out d))
                    {
                        kf.HasValue = true;
                        kf.Value = d;
                    }
                    else if (name == "valueX" && TryNum(text, out d))
                    {
                        kf.HasXYZW = true;
                        kf.ValueX = d;
                    }
                    else if (name == "valueY" && TryNum(text, out d))
                    {
                        kf.HasXYZW = true;
                        kf.ValueY = d;
                    }
                    else if (name == "valueZ" && TryNum(text, out d))
                    {
                        kf.HasXYZW = true;
                        kf.ValueZ = d;
                    }
                    else if (name == "valueW" && TryNum(text, out d))
                    {
                        kf.HasXYZW = true;
                        kf.ValueW = d;
                    }
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            scene.Tracks[trackIndex].Keyframes.Add(kf);
            return scene.Tracks[trackIndex].Keyframes.Count - 1;
        }

        /// <summary>新增插值曲线控制点（挂在当前关键帧下）。</summary>
        private static void AddCurveKey(XmlReader reader, TimelineScene scene, int trackIndex, int keyIndex)
        {
            if (trackIndex < 0 || trackIndex >= scene.Tracks.Count)
            {
                return;
            }
            List<TimelineKeyframe> keys = scene.Tracks[trackIndex].Keyframes;
            if (keyIndex < 0 || keyIndex >= keys.Count)
            {
                return;
            }
            TimelineCurveKey ck = new TimelineCurveKey();
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    string name = reader.Name;
                    double d;
                    if (name == "time" && TryNum(reader.Value, out d))
                    {
                        ck.Time = d;
                    }
                    else if (name == "value" && TryNum(reader.Value, out d))
                    {
                        ck.Value = d;
                    }
                    else if (name == "inTangent" && TryNum(reader.Value, out d))
                    {
                        ck.InTangent = d;
                    }
                    else if (name == "outTangent" && TryNum(reader.Value, out d))
                    {
                        ck.OutTangent = d;
                    }
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            keys[keyIndex].CurveKeys.Add(ck);
        }

        /// <summary>回填每组直接挂载的轨道数。</summary>
        private static void FillGroupTrackCounts(TimelineScene scene)
        {
            for (int i = 0; i < scene.Tracks.Count; i = i + 1)
            {
                int gi = scene.Tracks[i].GroupIndex;
                if (gi >= 0 && gi < scene.Groups.Count)
                {
                    scene.Groups[gi].TrackCount = scene.Groups[gi].TrackCount + 1;
                }
            }
        }

        /// <summary>按不变文化解析数值。</summary>
        private static bool TryNum(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
