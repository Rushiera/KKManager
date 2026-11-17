using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>Unity3d 包（UnityFS AssetBundle）内的一张贴图——只读元数据。</summary>
    public class Unity3dTexture
    {
        /// <summary>对象路径 ID（包内唯一）。</summary>
        public long PathId { get; set; }

        /// <summary>贴图名称（m_Name）。</summary>
        public string Name { get; set; }

        /// <summary>宽（像素）。</summary>
        public int Width { get; set; }

        /// <summary>高（像素）。</summary>
        public int Height { get; set; }

        /// <summary>Unity 贴图格式码（4 = RGBA32 · 10 = DXT1 · 12 = DXT5 · 14 = BGRA32 · 25 = BC7）。</summary>
        public int Format { get; set; }

        /// <summary>像素数据字节数（含 mipmap）。</summary>
        public int DataLength { get; set; }

        /// <summary>像素数据在解包数据区内的偏移。</summary>
        public int DataOffset { get; set; }
    }

    /// <summary>Unity3d（AssetBundle）只读解析器——UnityFS 容器解包 + SerializedFile 类型树 + Texture2D 提取与解码。</summary>
    public static class Unity3dReader
    {
        /// <summary>Texture2D 的 Unity classID。</summary>
        private const int ClassIdTexture2D = 28;

        /// <summary>单张贴图导出上限（像素数据字节数）——超限拒绝并出声。</summary>
        private const int MaxTextureBytes = 268435456;

        /// <summary>整包解包后的数据区上限（字节）——超限拒绝并出声；实测最大 mod 包解包后 1.42 GB，留余量到 1.9 GB（仍小于 byte[] 上限）。</summary>
        private const long MaxBundleBytes = 1900000000L;

        /// <summary>列出包内全部 Texture2D——失败返回 null 并在 error 给出诊断。</summary>
        public static List<Unity3dTexture> ListTextures(byte[] bundle, out string error)
        {
            error = null;
            if (bundle == null || bundle.Length < 64)
            {
                error = "数据过短";
                return null;
            }
            string loadErr;
            byte[] data = UnpackBundle(bundle, out loadErr);
            if (loadErr != null)
            {
                error = loadErr;
                return null;
            }
            int dataOffset;
            List<SfType> types;
            List<SfObject> objects;
            string parseErr;
            ParseSerializedFile(data, out dataOffset, out types, out objects, out parseErr);
            if (parseErr != null)
            {
                error = parseErr;
                return null;
            }

            // [段1] 逐对象筛 Texture2D
            List<Unity3dTexture> list = new List<Unity3dTexture>();
            for (int i = 0; i < objects.Count; i = i + 1)
            {
                SfObject o = objects[i];
                if (o.TypeId < 0 || o.TypeId >= types.Count)
                {
                    continue;
                }
                if (types[o.TypeId].ClassId != ClassIdTexture2D)
                {
                    continue;
                }
                Unity3dTexture tex = ReadTextureMeta(data, dataOffset + o.ByteStart, types[o.TypeId], o.PathId);
                if (tex != null)
                {
                    list.Add(tex);
                }
            }
            return list;
        }

        /// <summary>把包内指定贴图渲染为 PNG——maxEdge &gt; 0 时按最长边等比缩小（最近邻采样）。</summary>
        public static byte[] RenderTexturePng(byte[] bundle, long pathId, int maxEdge, out string error)
        {
            error = null;
            if (bundle == null || bundle.Length < 64)
            {
                error = "数据过短";
                return null;
            }
            string loadErr;
            byte[] data = UnpackBundle(bundle, out loadErr);
            if (loadErr != null)
            {
                error = loadErr;
                return null;
            }
            int dataOffset;
            List<SfType> types;
            List<SfObject> objects;
            string parseErr;
            ParseSerializedFile(data, out dataOffset, out types, out objects, out parseErr);
            if (parseErr != null)
            {
                error = parseErr;
                return null;
            }

            // [段1] 定位目标对象
            for (int i = 0; i < objects.Count; i = i + 1)
            {
                SfObject o = objects[i];
                if (o.PathId != pathId)
                {
                    continue;
                }
                if (o.TypeId < 0 || o.TypeId >= types.Count || types[o.TypeId].ClassId != ClassIdTexture2D)
                {
                    error = "该对象不是贴图";
                    return null;
                }
                Unity3dTexture meta = ReadTextureMeta(data, dataOffset + o.ByteStart, types[o.TypeId], o.PathId);
                if (meta == null)
                {
                    error = "贴图元数据解析失败";
                    return null;
                }
                string decErr;
                byte[] rgba = DecodeTexture(data, meta.DataOffset, meta.DataLength, meta.Width, meta.Height, meta.Format, out decErr);
                if (decErr != null)
                {
                    error = decErr;
                    return null;
                }
                if (maxEdge > 0 && (meta.Width > maxEdge || meta.Height > maxEdge))
                {
                    int nw = meta.Width;
                    int nh = meta.Height;
                    if (nw >= nh)
                    {
                        nh = nh * maxEdge / nw;
                        nw = maxEdge;
                    }
                    else
                    {
                        nw = nw * maxEdge / nh;
                        nh = maxEdge;
                    }
                    if (nw < 1)
                    {
                        nw = 1;
                    }
                    if (nh < 1)
                    {
                        nh = 1;
                    }
                    rgba = Downscale(rgba, meta.Width, meta.Height, nw, nh);
                    return WritePng(rgba, nw, nh);
                }
                return WritePng(rgba, meta.Width, meta.Height);
            }
            error = "包内没有该贴图对象";
            return null;
        }

        /// <summary>解包 UnityFS 容器数据区（blocksInfo 用 LZ4 时自解压）——失败返回 null 并给诊断。</summary>
        private static byte[] UnpackBundle(byte[] bundle, out string error)
        {
            error = null;
            int at = 0;
            if (bundle.Length < 48 || bundle[0] != (byte)'U' || bundle[1] != (byte)'n' || bundle[2] != (byte)'i' || bundle[3] != (byte)'t' || bundle[4] != (byte)'y' || bundle[5] != (byte)'F' || bundle[6] != (byte)'S')
            {
                error = "不是 UnityFS 容器（签名不符）";
                return null;
            }
            at = 8;
            int version = ReadInt32Big(bundle, at);
            at = at + 4;
            at = SkipCString(bundle, at);
            at = SkipCString(bundle, at);
            long totalSize = ReadInt64Big(bundle, at);
            at = at + 8;
            int compInfoSize = ReadInt32Big(bundle, at);
            at = at + 4;
            int uncompInfoSize = ReadInt32Big(bundle, at);
            at = at + 4;
            int flags = ReadInt32Big(bundle, at);
            at = at + 4;
            if (version >= 7)
            {
                at = at + 4;
            }
            int headerEnd = at;
            int infoPos = headerEnd;
            if ((flags & 0x80) != 0)
            {
                infoPos = (int)totalSize - compInfoSize;
            }
            if (infoPos < 0 || infoPos + compInfoSize > bundle.Length || uncompInfoSize <= 0 || uncompInfoSize > 4194304)
            {
                error = "blocksInfo 越界（位置 " + infoPos.ToString() + " · 压缩 " + compInfoSize.ToString() + " · 解压 " + uncompInfoSize.ToString() + "）";
                return null;
            }

            // [段1] blocksInfo 解压
            byte[] info;
            int infoComp = flags & 0x3F;
            if (infoComp == 0)
            {
                info = new byte[compInfoSize];
                Array.Copy(bundle, infoPos, info, 0, compInfoSize);
            }
            else if (infoComp == 2 || infoComp == 3)
            {
                byte[] dst = new byte[uncompInfoSize];
                string lz4Err;
                int got = Lz4BlockDecompress(bundle, infoPos, compInfoSize, dst, 0, uncompInfoSize, out lz4Err);
                if (lz4Err != null)
                {
                    error = "blocksInfo 解压失败：" + lz4Err;
                    return null;
                }
                info = new byte[got];
                Array.Copy(dst, info, got);
            }
            else
            {
                error = "blocksInfo 压缩方式不支持（" + infoComp.ToString() + "）";
                return null;
            }

            // [段2] 块表——先过「大小自洽」哨兵：块表本身必须落在解压后的 blocksInfo 内（不设人为上限）
            int p = 16;
            int blockCount = ReadInt32Big(info, p);
            p = p + 4;
            long tableEnd = 16L + 4 + (long)blockCount * 10;
            if (blockCount <= 0 || tableEnd > info.Length)
            {
                error = "数据块数异常（" + blockCount.ToString() + " · blocksInfo 仅 " + info.Length.ToString() + " 字节）";
                return null;
            }
            int[] blockUncomp = new int[blockCount];
            int[] blockComp = new int[blockCount];
            int[] blockFlags = new int[blockCount];
            for (int i = 0; i < blockCount; i = i + 1)
            {
                blockUncomp[i] = ReadInt32Big(info, p);
                p = p + 4;
                blockComp[i] = ReadInt32Big(info, p);
                p = p + 4;
                blockFlags[i] = (info[p] << 8) | info[p + 1];
                p = p + 2;
            }
            int dataStart = infoPos + compInfoSize;
            if ((flags & 0x80) != 0)
            {
                dataStart = headerEnd;
            }

            // [段3] 逐块解压拼接
            long total = 0;
            for (int i = 0; i < blockCount; i = i + 1)
            {
                total = total + blockUncomp[i];
            }
            if (total <= 0 || total > MaxBundleBytes)
            {
                error = "数据区大小异常（" + total.ToString() + " 字节）";
                return null;
            }
            byte[] data = new byte[total];
            int pos = dataStart;
            int writeAt = 0;
            for (int i = 0; i < blockCount; i = i + 1)
            {
                if (pos + blockComp[i] > bundle.Length)
                {
                    error = "数据块 #" + i.ToString() + " 越界";
                    return null;
                }
                int comp = blockFlags[i] & 0x3F;
                if (comp == 0)
                {
                    Array.Copy(bundle, pos, data, writeAt, blockComp[i]);
                }
                else if (comp == 2 || comp == 3)
                {
                    string lz4Err;
                    int got = Lz4BlockDecompress(bundle, pos, blockComp[i], data, writeAt, blockUncomp[i], out lz4Err);
                    if (lz4Err != null)
                    {
                        error = "数据块 #" + i.ToString() + " 解压失败：" + lz4Err;
                        return null;
                    }
                    if (got != blockUncomp[i])
                    {
                        error = "数据块 #" + i.ToString() + " 解压长度不符（" + got.ToString() + " ≠ " + blockUncomp[i].ToString() + "）";
                        return null;
                    }
                }
                else
                {
                    error = "数据块压缩方式不支持（" + comp.ToString() + "）";
                    return null;
                }
                pos = pos + blockComp[i];
                writeAt = writeAt + blockUncomp[i];
            }
            return data;
        }

        /// <summary>解析 SerializedFile——输出数据偏移、类型表与对象表。</summary>
        private static void ParseSerializedFile(byte[] data, out int dataOffset, out List<SfType> types, out List<SfObject> objects, out string error)
        {
            dataOffset = 0;
            types = new List<SfType>();
            objects = new List<SfObject>();
            error = null;
            if (data.Length < 32)
            {
                error = "SerializedFile 过短";
                return;
            }
            int metadataSize = ReadInt32Big(data, 0);
            ReadInt32Big(data, 8);
            dataOffset = ReadInt32Big(data, 12);
            if (metadataSize <= 0 || metadataSize > data.Length || dataOffset <= 0 || dataOffset > data.Length)
            {
                error = "SerializedFile 头部异常（元数据 " + metadataSize.ToString() + " · 数据偏移 " + dataOffset.ToString() + "）";
                return;
            }
            int at = 20;
            at = SkipCString(data, at);
            at = at + 4;
            if (at >= data.Length)
            {
                error = "SerializedFile 头部越界";
                return;
            }
            int enableTypeTree = data[at];
            at = at + 1;
            int typeCount = ReadInt32Little(data, at);
            at = at + 4;
            if (typeCount <= 0 || typeCount > 4096)
            {
                error = "类型数异常（" + typeCount.ToString() + "）";
                return;
            }

            // [段1] 类型表——classID + isStripped + scriptTypeIndex + [114 专属 16 字节] + 旧类型哈希 + TypeTree
            for (int i = 0; i < typeCount; i = i + 1)
            {
                if (at + 23 > data.Length)
                {
                    error = "类型表越界（#" + i.ToString() + "）";
                    return;
                }
                SfType t = new SfType();
                t.ClassId = ReadInt32Little(data, at);
                at = at + 4;
                at = at + 1;
                at = at + 2;
                if (t.ClassId == 114)
                {
                    at = at + 16;
                }
                at = at + 16;
                if (enableTypeTree != 0)
                {
                    t.NodeCount = ReadInt32Little(data, at);
                    at = at + 4;
                    t.StringBufferSize = ReadInt32Little(data, at);
                    at = at + 4;
                    if (t.NodeCount < 0 || t.NodeCount > 20000 || t.StringBufferSize < 0 || t.StringBufferSize > 1048576)
                    {
                        error = "类型 #" + i.ToString() + " 节点数 / 字段名池异常（" + t.NodeCount.ToString() + " / " + t.StringBufferSize.ToString() + "）";
                        return;
                    }
                    if (at + t.NodeCount * 24 + t.StringBufferSize > data.Length)
                    {
                        error = "类型 #" + i.ToString() + " 节点区越界";
                        return;
                    }
                    for (int k = 0; k < t.NodeCount; k = k + 1)
                    {
                        SfNode n = new SfNode();
                        n.Level = data[at + 2];
                        n.TypeStrOffset = (long)(uint)ReadInt32Little(data, at + 4);
                        n.NameStrOffset = (long)(uint)ReadInt32Little(data, at + 8);
                        n.ByteSize = ReadInt32Little(data, at + 12);
                        n.MetaFlag = ReadInt32Little(data, at + 20);
                        at = at + 24;
                        t.Nodes.Add(n);
                    }
                    t.StringBuffer = new byte[t.StringBufferSize];
                    Array.Copy(data, at, t.StringBuffer, 0, t.StringBufferSize);
                    at = at + t.StringBufferSize;
                }
                types.Add(t);
            }

            // [段2] 对象表——objectCount 之后按 4 字节对齐
            if (at + 4 > data.Length)
            {
                error = "对象表越界";
                return;
            }
            int objectCount = ReadInt32Little(data, at);
            at = at + 4;
            at = (at + 3) / 4 * 4;
            if (objectCount < 0 || objectCount > 1000000 || at + objectCount * 20 > data.Length)
            {
                error = "对象数异常（" + objectCount.ToString() + "）";
                return;
            }
            for (int i = 0; i < objectCount; i = i + 1)
            {
                SfObject o = new SfObject();
                o.PathId = ReadInt64Little(data, at);
                at = at + 8;
                o.ByteStart = ReadInt32Little(data, at);
                at = at + 4;
                o.ByteSize = ReadInt32Little(data, at);
                at = at + 4;
                o.TypeId = ReadInt32Little(data, at);
                at = at + 4;
                objects.Add(o);
            }
        }

        /// <summary>按 TypeTree 节点序列读取一个 Texture2D 的关键字段——失败返回 null。</summary>
        private static Unity3dTexture ReadTextureMeta(byte[] data, int at, SfType t, long pathId)
        {
            if (at < 0 || at >= data.Length)
            {
                return null;
            }
            Unity3dTexture tex = new Unity3dTexture();
            tex.PathId = pathId;
            tex.Name = "";
            int k = 1;
            while (k < t.Nodes.Count)
            {
                SfNode n = t.Nodes[k];
                string name = NodeName(t, n.NameStrOffset);
                int next = k + 1;
                while (next < t.Nodes.Count && t.Nodes[next].Level > n.Level)
                {
                    next = next + 1;
                }

                // [段1] 变长字段（string / 字节数组）：长度 + 数据 + 对齐，整段连子节点一起跳过
                if (name == "m_Name")
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    int safe = len;
                    if (safe < 0 || safe > 4096 || at + safe > data.Length)
                    {
                        safe = 0;
                    }
                    tex.Name = Encoding.UTF8.GetString(data, at, safe);
                    at = SkipAligned(data, at, len);
                    k = next;
                    continue;
                }
                if (name == "image data")
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    tex.DataLength = len;
                    tex.DataOffset = at;
                    at = SkipAligned(data, at, len);
                    k = next;
                    continue;
                }
                if (name == "m_StreamData")
                {
                    at = at + 8;
                    at = SkipCString(data, at);
                    k = next;
                    continue;
                }

                // [段2] 已知简单字段
                if (name == "m_Width" || name == "m_Height" || name == "m_TextureFormat")
                {
                    int v = ReadInt32Little(data, at);
                    at = at + 4;
                    if (name == "m_Width")
                    {
                        tex.Width = v;
                    }
                    if (name == "m_Height")
                    {
                        tex.Height = v;
                    }
                    if (name == "m_TextureFormat")
                    {
                        tex.Format = v;
                    }
                    k = k + 1;
                    continue;
                }
                if (name == "m_CompleteImageSize" || name == "m_MipCount" || name == "m_ImageCount" || name == "m_TextureDimension"
                    || name == "m_LightmapFormat" || name == "m_ColorSpace" || name == "m_ForcedFallbackFormat")
                {
                    at = at + 4;
                    k = k + 1;
                    continue;
                }
                if (name == "m_DownscaleFallback" || name == "m_IsReadable")
                {
                    at = at + 1;
                    at = (at + 3) / 4 * 4;
                    k = k + 1;
                    continue;
                }

                // [段3] 其余按结构规则跳过——变长整段跳；容器不占数据；基础类型按声明字节数跳
                if (n.ByteSize == -1)
                {
                    int len = ReadInt32Little(data, at);
                    at = at + 4;
                    at = SkipAligned(data, at, len);
                    k = next;
                    continue;
                }
                if (n.ByteSize > 0 && next > k + 1)
                {
                    k = k + 1;
                    continue;
                }
                if (n.ByteSize > 0)
                {
                    at = at + n.ByteSize;
                    k = k + 1;
                    continue;
                }
                k = k + 1;
            }
            if (tex.Width <= 0 || tex.Height <= 0 || tex.DataLength <= 0)
            {
                return null;
            }
            if (tex.Width > 16384 || tex.Height > 16384 || tex.DataLength > MaxTextureBytes)
            {
                return null;
            }
            if (tex.DataOffset + tex.DataLength > data.Length)
            {
                return null;
            }
            return tex;
        }

        /// <summary>跳过 len 字节并按 4 字节对齐（带边界钳制）。</summary>
        private static int SkipAligned(byte[] data, int at, int len)
        {
            if (len > 0 && len < MaxTextureBytes && at + len <= data.Length)
            {
                at = at + len;
            }
            at = (at + 3) / 4 * 4;
            if (at > data.Length)
            {
                at = data.Length;
            }
            return at;
        }

        /// <summary>解析 TypeTree 名字——高位 0x80 为 Unity 内置类型名表索引（本实现不带该表，返回占位），否则取本类型字段名池。</summary>
        private static string NodeName(SfType t, long offset)
        {
            if ((offset & 0x80000000L) != 0)
            {
                return "#" + (offset & 0x7FFFFFFFL).ToString();
            }
            if (t.StringBuffer == null || offset >= t.StringBuffer.Length)
            {
                return "?";
            }
            int start = (int)offset;
            int q = start;
            while (q < t.StringBuffer.Length && t.StringBuffer[q] != 0)
            {
                q = q + 1;
            }
            return Encoding.UTF8.GetString(t.StringBuffer, start, q - start);
        }

        /// <summary>按 Unity 贴图格式码解码为 RGBA32（支持 RGBA32 / RGB24 / BGRA32 / ARGB32 / RGB565 / DXT1 / DXT5 / BC7）。</summary>
        private static byte[] DecodeTexture(byte[] data, int at, int len, int width, int height, int format, out string error)
        {
            error = null;
            if (at <= 0 || len <= 0 || at + len > data.Length)
            {
                error = "像素数据越界（@ " + at.ToString() + " · " + len.ToString() + " 字节）";
                return null;
            }
            byte[] src = new byte[len];
            Array.Copy(data, at, src, 0, len);
            if (format == 4)
            {
                return DecodeRgba32(src, width, height);
            }
            if (format == 3)
            {
                return DecodeRgb24(src, width, height);
            }
            if (format == 14)
            {
                return DecodeBgra32(src, width, height);
            }
            if (format == 5)
            {
                return DecodeArgb32(src, width, height);
            }
            if (format == 7)
            {
                return DecodeRgb565(src, width, height);
            }
            if (format == 10)
            {
                return DecodeDxt1(src, width, height);
            }
            if (format == 12)
            {
                return DecodeDxt5(src, width, height);
            }
            if (format == 25)
            {
                return DecodeBc7(src, width, height);
            }
            error = "暂不支持的贴图格式 " + format.ToString();
            return null;
        }

        /// <summary>BC7 的 2 子集分区表——每项 16 位，位 i 置位表示像素 i 属于子集 1（否则子集 0）。</summary>
        private static readonly ushort[] Bc7Partition2 =
        {
            0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80,
            0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
            0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce,
            0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
            0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
            0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
            0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c,
            0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22,
        };

        /// <summary>BC7 的 3 子集分区表——每项 32 位，每像素 2 位给出所属子集（0 / 1 / 2）。</summary>
        private static readonly uint[] Bc7Partition3 =
        {
            0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
            0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
            0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
            0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
            0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
            0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
            0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
            0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254,
        };

        /// <summary>BC7 的 2 子集锚点表——每项给出子集 1 的锚点像素序号（该像素的索引少读 1 位）。</summary>
        private static readonly byte[] Bc7Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15,
            2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15,
            2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2,
            15, 15, 15, 15, 15, 2, 2, 15,
        };

        /// <summary>BC7 的 3 子集锚点表——[子集 - 1][分区号] 给出该子集的锚点像素序号。</summary>
        private static readonly byte[][] Bc7Anchor3 =
        {
            new byte[]
            {
                3, 3, 15, 15, 8, 3, 15, 15,
                8, 8, 6, 6, 6, 5, 3, 3,
                3, 3, 8, 15, 3, 3, 6, 10,
                5, 8, 8, 6, 8, 5, 15, 15,
                8, 15, 3, 5, 6, 10, 8, 15,
                15, 3, 15, 5, 15, 15, 15, 15,
                3, 15, 5, 5, 5, 8, 5, 10,
                5, 10, 8, 13, 15, 12, 3, 3,
            },
            new byte[]
            {
                15, 8, 8, 3, 15, 15, 3, 8,
                15, 15, 15, 15, 15, 15, 15, 8,
                15, 8, 15, 3, 15, 8, 15, 8,
                3, 15, 6, 10, 15, 15, 10, 8,
                15, 3, 15, 10, 10, 8, 9, 10,
                6, 15, 8, 15, 3, 6, 6, 8,
                15, 3, 15, 15, 15, 15, 15, 15,
                15, 15, 15, 15, 3, 15, 15, 8,
            },
        };

        /// <summary>BC7 的插值权重表——[索引位宽 - 2][索引值] 给出 0..64 的权重（按 64 归一）。</summary>
        private static readonly byte[][] Bc7Factors =
        {
            new byte[] { 0, 21, 43, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            new byte[] { 0, 9, 18, 27, 37, 46, 55, 64, 0, 0, 0, 0, 0, 0, 0, 0 },
            new byte[] { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 },
        };

        /// <summary>BC7 单个模式的字段布局（位序：模式前缀 → 分区 → 旋转 → 索引选择 → 颜色端点 → Alpha 端点 → P 位 → 索引）。</summary>
        private struct Bc7ModeInfo
        {
            /// <summary>子集数（1 / 2 / 3）。</summary>
            public int Subsets;

            /// <summary>分区号位数。</summary>
            public int PartitionBits;

            /// <summary>旋转位数。</summary>
            public int RotationBits;

            /// <summary>索引选择位数。</summary>
            public int IndexSelectionBits;

            /// <summary>颜色端点的每通道位数。</summary>
            public int ColorBits;

            /// <summary>Alpha 端点位（0 = 无 Alpha 端点，全部不透明）。</summary>
            public int AlphaBits;

            /// <summary>每端点的 P 位数（0 = 本模式不用逐端点 P 位）。</summary>
            public int EndpointPBits;

            /// <summary>每子集共享的 P 位数（0 = 本模式不用共享 P 位）。</summary>
            public int SharedPBits;

            /// <summary>主索引位数（颜色流）。</summary>
            public int IndexBits0;

            /// <summary>次索引位数（Alpha 流；0 = 与颜色流共用同一索引）。</summary>
            public int IndexBits1;

            /// <summary>按模式表填一项布局。</summary>
            public Bc7ModeInfo(int subsets, int partitionBits, int rotationBits, int indexSelectionBits, int colorBits,
                int alphaBits, int endpointPBits, int sharedPBits, int indexBits0, int indexBits1)
            {
                Subsets = subsets;
                PartitionBits = partitionBits;
                RotationBits = rotationBits;
                IndexSelectionBits = indexSelectionBits;
                ColorBits = colorBits;
                AlphaBits = alphaBits;
                EndpointPBits = endpointPBits;
                SharedPBits = sharedPBits;
                IndexBits0 = indexBits0;
                IndexBits1 = indexBits1;
            }
        }

        /// <summary>BC7 的 8 种模式布局表（索引 = 模式号）。</summary>
        private static readonly Bc7ModeInfo[] Bc7ModeInfos =
        {
            new Bc7ModeInfo(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
            new Bc7ModeInfo(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
            new Bc7ModeInfo(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
            new Bc7ModeInfo(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
            new Bc7ModeInfo(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
            new Bc7ModeInfo(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
            new Bc7ModeInfo(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
            new Bc7ModeInfo(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
        };

        /// <summary>BC7 解码为 RGBA32——16 字节一块，覆盖全部 8 种模式（表值与位序按 BC7 规格）。</summary>
        private static byte[] DecodeBc7(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            byte[] block = new byte[16];
            byte[] pixels = new byte[64];
            int[] epR = new int[6];
            int[] epG = new int[6];
            int[] epB = new int[6];
            int[] epA = new int[6];
            int[] index = new int[2];
            int[] offset = new int[2];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int at = 0;
            for (int by = 0; by < bh; by = by + 1)
            {
                for (int bx = 0; bx < bw; bx = bx + 1)
                {
                    if (at + 16 > src.Length)
                    {
                        return dst;
                    }
                    Array.Copy(src, at, block, 0, 16);
                    at = at + 16;
                    DecodeBc7Block(block, pixels, epR, epG, epB, epA, index, offset);
                    CopyBc7Block(pixels, dst, width, height, bx * 4, by * 4);
                }
            }
            return dst;
        }

        /// <summary>解一个 BC7 块（16 字节）为 4×4 的 RGBA 像素（64 字节）——端点与索引缓冲由调用方复用，避免逐块分配。</summary>
        private static void DecodeBc7Block(byte[] block, byte[] pixels, int[] epR, int[] epG, int[] epB, int[] epA, int[] index, int[] offset)
        {
            // [段1] 模式前缀——连续 0 的个数即模式号，收尾的 1 也要吃掉（读到 8 = 无效块，按全透明黑处理）
            int bitPos = 0;
            int mode = 0;
            while (mode < 8 && Bc7ReadBits(block, bitPos, 1) == 0)
            {
                bitPos = bitPos + 1;
                mode = mode + 1;
            }
            if (mode == 8)
            {
                for (int i = 0; i < 64; i = i + 1)
                {
                    pixels[i] = 0;
                }
                return;
            }
            bitPos = bitPos + 1;
            Bc7ModeInfo mi = Bc7ModeInfos[mode];
            int pBits = mi.EndpointPBits;
            if (pBits == 0)
            {
                pBits = mi.SharedPBits;
            }

            // [段2] 分区号 / 旋转模式 / 索引选择
            int partitionSetIdx = Bc7ReadBits(block, bitPos, mi.PartitionBits);
            bitPos = bitPos + mi.PartitionBits;
            int rotationMode = Bc7ReadBits(block, bitPos, mi.RotationBits);
            bitPos = bitPos + mi.RotationBits;
            int indexSelectionMode = Bc7ReadBits(block, bitPos, mi.IndexSelectionBits);
            bitPos = bitPos + mi.IndexSelectionBits;

            // [段3] 端点——每子集两个，读序为 R（全子集）→ G → B → A
            for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
            {
                epR[ii * 2] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
                epR[ii * 2 + 1] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
            }
            for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
            {
                epG[ii * 2] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
                epG[ii * 2 + 1] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
            }
            for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
            {
                epB[ii * 2] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
                epB[ii * 2 + 1] = Bc7ReadBits(block, bitPos, mi.ColorBits) << pBits;
                bitPos = bitPos + mi.ColorBits;
            }
            if (mi.AlphaBits != 0)
            {
                for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
                {
                    epA[ii * 2] = Bc7ReadBits(block, bitPos, mi.AlphaBits) << pBits;
                    bitPos = bitPos + mi.AlphaBits;
                    epA[ii * 2 + 1] = Bc7ReadBits(block, bitPos, mi.AlphaBits) << pBits;
                    bitPos = bitPos + mi.AlphaBits;
                }
            }
            else
            {
                for (int ii = 0; ii < 6; ii = ii + 1)
                {
                    epA[ii] = 255;
                }
            }

            // [段4] P 位——逐端点各一位，或每子集共享一位（共享时两个端点同值）
            if (pBits != 0)
            {
                for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
                {
                    int pda = Bc7ReadBits(block, bitPos, pBits);
                    bitPos = bitPos + pBits;
                    int pdb = pda;
                    if (mi.SharedPBits == 0)
                    {
                        pdb = Bc7ReadBits(block, bitPos, pBits);
                        bitPos = bitPos + pBits;
                    }
                    epR[ii * 2] = epR[ii * 2] | pda;
                    epR[ii * 2 + 1] = epR[ii * 2 + 1] | pdb;
                    epG[ii * 2] = epG[ii * 2] | pda;
                    epG[ii * 2 + 1] = epG[ii * 2 + 1] | pdb;
                    epB[ii * 2] = epB[ii * 2] | pda;
                    epB[ii * 2 + 1] = epB[ii * 2 + 1] | pdb;
                    epA[ii * 2] = epA[ii * 2] | pda;
                    epA[ii * 2 + 1] = epA[ii * 2 + 1] | pdb;
                }
            }

            // [段5] 端点量化扩展（颜色与 Alpha 各按自己的位宽）
            int colorBits = mi.ColorBits + pBits;
            for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
            {
                epR[ii * 2] = Bc7Expand(epR[ii * 2], colorBits);
                epR[ii * 2 + 1] = Bc7Expand(epR[ii * 2 + 1], colorBits);
                epG[ii * 2] = Bc7Expand(epG[ii * 2], colorBits);
                epG[ii * 2 + 1] = Bc7Expand(epG[ii * 2 + 1], colorBits);
                epB[ii * 2] = Bc7Expand(epB[ii * 2], colorBits);
                epB[ii * 2 + 1] = Bc7Expand(epB[ii * 2 + 1], colorBits);
            }
            if (mi.AlphaBits != 0)
            {
                int alphaBits = mi.AlphaBits + pBits;
                for (int ii = 0; ii < mi.Subsets; ii = ii + 1)
                {
                    epA[ii * 2] = Bc7Expand(epA[ii * 2], alphaBits);
                    epA[ii * 2 + 1] = Bc7Expand(epA[ii * 2 + 1], alphaBits);
                }
            }

            // [段6] 逐像素取分区与索引 → 插值 → 按旋转模式换通道
            bool hasIndexBits1 = mi.IndexBits1 != 0;
            byte[] factors0 = Bc7Factors[mi.IndexBits0 - 2];
            byte[] factors1 = factors0;
            if (hasIndexBits1)
            {
                factors1 = Bc7Factors[mi.IndexBits1 - 2];
            }
            offset[0] = 0;
            offset[1] = mi.Subsets * (16 * mi.IndexBits0 - 1);
            for (int py = 0; py < 4; py = py + 1)
            {
                for (int px = 0; px < 4; px = px + 1)
                {
                    int idx = py * 4 + px;
                    int subsetIndex = 0;
                    int indexAnchor = 0;
                    if (mi.Subsets == 2)
                    {
                        subsetIndex = (Bc7Partition2[partitionSetIdx] >> idx) & 1;
                        if (subsetIndex != 0)
                        {
                            indexAnchor = Bc7Anchor2[partitionSetIdx];
                        }
                    }
                    else if (mi.Subsets == 3)
                    {
                        subsetIndex = (int)((Bc7Partition3[partitionSetIdx] >> (2 * idx)) & 3);
                        if (subsetIndex != 0)
                        {
                            indexAnchor = Bc7Anchor3[subsetIndex - 1][partitionSetIdx];
                        }
                    }
                    int anchor = 0;
                    if (idx == indexAnchor)
                    {
                        anchor = 1;
                    }
                    int num0 = mi.IndexBits0 - anchor;
                    int num1 = 0;
                    if (hasIndexBits1)
                    {
                        num1 = mi.IndexBits1 - anchor;
                    }
                    index[0] = Bc7ReadBits(block, bitPos + offset[0], num0);
                    index[1] = index[0];
                    if (hasIndexBits1)
                    {
                        index[1] = Bc7ReadBits(block, bitPos + offset[1], num1);
                    }
                    offset[0] = offset[0] + num0;
                    offset[1] = offset[1] + num1;
                    int fc = factors0[index[0]];
                    int fa = factors1[index[1]];
                    if (indexSelectionMode != 0)
                    {
                        fc = factors1[index[1]];
                        fa = factors0[index[0]];
                    }
                    int fca = 64 - fc;
                    int faa = 64 - fa;
                    int s = subsetIndex * 2;
                    int rr = (epR[s] * fca + epR[s + 1] * fc + 32) >> 6;
                    int gg = (epG[s] * fca + epG[s + 1] * fc + 32) >> 6;
                    int bb = (epB[s] * fca + epB[s + 1] * fc + 32) >> 6;
                    int aa = (epA[s] * faa + epA[s + 1] * fa + 32) >> 6;
                    if (rotationMode == 1)
                    {
                        int t = aa;
                        aa = rr;
                        rr = t;
                    }
                    else if (rotationMode == 2)
                    {
                        int t = aa;
                        aa = gg;
                        gg = t;
                    }
                    else if (rotationMode == 3)
                    {
                        int t = aa;
                        aa = bb;
                        bb = t;
                    }
                    pixels[idx * 4] = (byte)rr;
                    pixels[idx * 4 + 1] = (byte)gg;
                    pixels[idx * 4 + 2] = (byte)bb;
                    pixels[idx * 4 + 3] = (byte)aa;
                }
            }
        }

        /// <summary>从 16 字节块里按位序取 numBits 位（低位在前，跨字节按小端拼接；越界返回 0）。</summary>
        private static int Bc7ReadBits(byte[] block, int bitPos, int numBits)
        {
            if (numBits <= 0)
            {
                return 0;
            }
            int pos = bitPos / 8;
            int shift = bitPos & 7;
            int avail = 16 - pos;
            if (avail > 4)
            {
                avail = 4;
            }
            uint data = 0;
            for (int i = 0; i < avail; i = i + 1)
            {
                data = data | ((uint)block[pos + i] << (8 * i));
            }
            return (int)((data >> shift) & ((1u << numBits) - 1u));
        }

        /// <summary>BC7 端点量化扩展——把 bits 位的值扩到 8 位（左移补高位，再复制高位补低位）。</summary>
        private static int Bc7Expand(int value, int bits)
        {
            int v = value << (8 - bits);
            return v | (v >> bits);
        }

        /// <summary>把一块 4×4 的 RGBA 像素写进目标图像（超出边界的像素丢弃）。</summary>
        private static void CopyBc7Block(byte[] pixels, byte[] dst, int width, int height, int x0, int y0)
        {
            for (int py = 0; py < 4; py = py + 1)
            {
                int y = y0 + py;
                if (y < 0 || y >= height)
                {
                    continue;
                }
                for (int px = 0; px < 4; px = px + 1)
                {
                    int x = x0 + px;
                    if (x < 0 || x >= width)
                    {
                        continue;
                    }
                    int s = (py * 4 + px) * 4;
                    int d = (y * width + x) * 4;
                    dst[d] = pixels[s];
                    dst[d + 1] = pixels[s + 1];
                    dst[d + 2] = pixels[s + 2];
                    dst[d + 3] = pixels[s + 3];
                }
            }
        }

        /// <summary>RGBA32 直读。</summary>
        private static byte[] DecodeRgba32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int n = dst.Length;
            if (src.Length < n)
            {
                n = src.Length;
            }
            Array.Copy(src, dst, n);
            return dst;
        }

        /// <summary>RGB24 展开为 RGBA32。</summary>
        private static byte[] DecodeRgb24(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 3 + 2 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 3];
                dst[i * 4 + 1] = src[i * 3 + 1];
                dst[i * 4 + 2] = src[i * 3 + 2];
                dst[i * 4 + 3] = 255;
            }
            return dst;
        }

        /// <summary>BGRA32 换序为 RGBA32。</summary>
        private static byte[] DecodeBgra32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 4 + 3 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 4 + 2];
                dst[i * 4 + 1] = src[i * 4 + 1];
                dst[i * 4 + 2] = src[i * 4];
                dst[i * 4 + 3] = src[i * 4 + 3];
            }
            return dst;
        }

        /// <summary>ARGB32 换序为 RGBA32。</summary>
        private static byte[] DecodeArgb32(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 4 + 3 >= src.Length)
                {
                    break;
                }
                dst[i * 4] = src[i * 4 + 1];
                dst[i * 4 + 1] = src[i * 4 + 2];
                dst[i * 4 + 2] = src[i * 4 + 3];
                dst[i * 4 + 3] = src[i * 4];
            }
            return dst;
        }

        /// <summary>RGB565 展开为 RGBA32。</summary>
        private static byte[] DecodeRgb565(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i = i + 1)
            {
                if (i * 2 + 1 >= src.Length)
                {
                    break;
                }
                int c = src[i * 2] | (src[i * 2 + 1] << 8);
                dst[i * 4] = (byte)(((c >> 11) & 0x1F) * 255 / 31);
                dst[i * 4 + 1] = (byte)(((c >> 5) & 0x3F) * 255 / 63);
                dst[i * 4 + 2] = (byte)((c & 0x1F) * 255 / 31);
                dst[i * 4 + 3] = 255;
            }
            return dst;
        }

        /// <summary>DXT1（BC1）解码为 RGBA32——每 8 字节一个 4×4 块。</summary>
        private static byte[] DecodeDxt1(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int at = 0;
            for (int by = 0; by < bh; by = by + 1)
            {
                for (int bx = 0; bx < bw; bx = bx + 1)
                {
                    if (at + 8 > src.Length)
                    {
                        return dst;
                    }
                    int c0 = src[at] | (src[at + 1] << 8);
                    int c1 = src[at + 2] | (src[at + 3] << 8);
                    uint bits = (uint)(src[at + 4] | (src[at + 5] << 8) | (src[at + 6] << 16) | (src[at + 7] << 24));
                    at = at + 8;
                    int r0 = ((c0 >> 11) & 0x1F) * 255 / 31;
                    int g0 = ((c0 >> 5) & 0x3F) * 255 / 63;
                    int b0 = (c0 & 0x1F) * 255 / 31;
                    int r1 = ((c1 >> 11) & 0x1F) * 255 / 31;
                    int g1 = ((c1 >> 5) & 0x3F) * 255 / 63;
                    int b1 = (c1 & 0x1F) * 255 / 31;
                    for (int py = 0; py < 4; py = py + 1)
                    {
                        for (int px = 0; px < 4; px = px + 1)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height)
                            {
                                continue;
                            }
                            int code = (int)((bits >> (2 * (py * 4 + px))) & 3);
                            int r;
                            int g;
                            int b;
                            int a = 255;
                            if (code == 0)
                            {
                                r = r0;
                                g = g0;
                                b = b0;
                            }
                            else if (code == 1)
                            {
                                r = r1;
                                g = g1;
                                b = b1;
                            }
                            else if (code == 2)
                            {
                                r = (2 * r0 + r1) / 3;
                                g = (2 * g0 + g1) / 3;
                                b = (2 * b0 + b1) / 3;
                            }
                            else
                            {
                                if (c0 > c1)
                                {
                                    r = (r0 + 2 * r1) / 3;
                                    g = (g0 + 2 * g1) / 3;
                                    b = (b0 + 2 * b1) / 3;
                                }
                                else
                                {
                                    r = 0;
                                    g = 0;
                                    b = 0;
                                    a = 0;
                                }
                            }
                            int di = (y * width + x) * 4;
                            dst[di] = (byte)r;
                            dst[di + 1] = (byte)g;
                            dst[di + 2] = (byte)b;
                            dst[di + 3] = (byte)a;
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>DXT5（BC3）解码为 RGBA32——每 16 字节一个 4×4 块。</summary>
        private static byte[] DecodeDxt5(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int at = 0;
            for (int by = 0; by < bh; by = by + 1)
            {
                for (int bx = 0; bx < bw; bx = bx + 1)
                {
                    if (at + 16 > src.Length)
                    {
                        return dst;
                    }
                    int a0 = src[at];
                    int a1 = src[at + 1];
                    int c0 = src[at + 8] | (src[at + 9] << 8);
                    int c1 = src[at + 10] | (src[at + 11] << 8);
                    uint bits = (uint)(src[at + 12] | (src[at + 13] << 8) | (src[at + 14] << 16) | (src[at + 15] << 24));
                    ulong abits = 0;
                    for (int i = 0; i < 6; i = i + 1)
                    {
                        abits = abits | ((ulong)src[at + 2 + i] << (8 * i));
                    }
                    at = at + 16;

                    int[] alpha = new int[8];
                    alpha[0] = a0;
                    alpha[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i < 7; i = i + 1)
                        {
                            alpha[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        }
                    }
                    else
                    {
                        for (int i = 1; i < 5; i = i + 1)
                        {
                            alpha[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                        }
                        alpha[6] = 0;
                        alpha[7] = 255;
                    }

                    int r0 = ((c0 >> 11) & 0x1F) * 255 / 31;
                    int g0 = ((c0 >> 5) & 0x3F) * 255 / 63;
                    int b0 = (c0 & 0x1F) * 255 / 31;
                    int r1 = ((c1 >> 11) & 0x1F) * 255 / 31;
                    int g1 = ((c1 >> 5) & 0x3F) * 255 / 63;
                    int b1 = (c1 & 0x1F) * 255 / 31;

                    for (int py = 0; py < 4; py = py + 1)
                    {
                        for (int px = 0; px < 4; px = px + 1)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height)
                            {
                                continue;
                            }
                            int idx = py * 4 + px;
                            int code = (int)((bits >> (2 * idx)) & 3);
                            int r;
                            int g;
                            int b;
                            if (code == 0)
                            {
                                r = r0;
                                g = g0;
                                b = b0;
                            }
                            else if (code == 1)
                            {
                                r = r1;
                                g = g1;
                                b = b1;
                            }
                            else if (code == 2)
                            {
                                r = (2 * r0 + r1) / 3;
                                g = (2 * g0 + g1) / 3;
                                b = (2 * b0 + b1) / 3;
                            }
                            else
                            {
                                r = (r0 + 2 * r1) / 3;
                                g = (g0 + 2 * g1) / 3;
                                b = (b0 + 2 * b1) / 3;
                            }
                            int acode = (int)((abits >> (3 * idx)) & 7);
                            int di = (y * width + x) * 4;
                            dst[di] = (byte)r;
                            dst[di + 1] = (byte)g;
                            dst[di + 2] = (byte)b;
                            dst[di + 3] = (byte)alpha[acode];
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>等比缩小 RGBA32 位图（盒式平均）。</summary>
        private static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
        {
            byte[] dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y = y + 1)
            {
                int sy0 = y * sh / dh;
                int sy1 = (y + 1) * sh / dh;
                if (sy1 <= sy0)
                {
                    sy1 = sy0 + 1;
                }
                for (int x = 0; x < dw; x = x + 1)
                {
                    int sx0 = x * sw / dw;
                    int sx1 = (x + 1) * sw / dw;
                    if (sx1 <= sx0)
                    {
                        sx1 = sx0 + 1;
                    }
                    long r = 0;
                    long g = 0;
                    long b = 0;
                    long a = 0;
                    int cnt = 0;
                    for (int sy = sy0; sy < sy1 && sy < sh; sy = sy + 1)
                    {
                        for (int sx = sx0; sx < sx1 && sx < sw; sx = sx + 1)
                        {
                            int si = (sy * sw + sx) * 4;
                            r = r + src[si];
                            g = g + src[si + 1];
                            b = b + src[si + 2];
                            a = a + src[si + 3];
                            cnt = cnt + 1;
                        }
                    }
                    if (cnt <= 0)
                    {
                        cnt = 1;
                    }
                    int di = (y * dw + x) * 4;
                    dst[di] = (byte)(r / cnt);
                    dst[di + 1] = (byte)(g / cnt);
                    dst[di + 2] = (byte)(b / cnt);
                    dst[di + 3] = (byte)(a / cnt);
                }
            }
            return dst;
        }

        /// <summary>把 RGBA32 位图编码为 PNG 字节（8 位 RGBA）。</summary>
        private static byte[] WritePng(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            byte[] raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y = y + 1)
            {
                raw[y * (stride + 1)] = 0;
                Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
            }
            byte[] comp;
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.IO.Compression.ZLibStream zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    zs.Write(raw, 0, raw.Length);
                }
                comp = ms.ToArray();
            }
            using (MemoryStream outMs = new MemoryStream())
            {
                outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                byte[] ihdr = new byte[13];
                WriteInt32Big(ihdr, 0, width);
                WriteInt32Big(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(outMs, "IHDR", ihdr);
                WriteChunk(outMs, "IDAT", comp);
                WriteChunk(outMs, "IEND", new byte[0]);
                return outMs.ToArray();
            }
        }

        /// <summary>写一个 PNG 块（长度 + 类型 + 数据 + CRC）。</summary>
        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] hdr = new byte[8];
            WriteInt32Big(hdr, 0, data.Length);
            Array.Copy(typeBytes, 0, hdr, 4, 4);
            s.Write(hdr, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc32Update(0xFFFFFFFFu, typeBytes, 0, typeBytes.Length);
            crc = Crc32Update(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;
            byte[] crcBytes = new byte[4];
            WriteInt32Big(crcBytes, 0, unchecked((int)crc));
            s.Write(crcBytes, 0, 4);
        }

        /// <summary>CRC32 累计（PNG 用的反射多项式）。</summary>
        private static uint Crc32Update(uint crc, byte[] buf, int offset, int count)
        {
            uint c = crc;
            for (int i = 0; i < count; i = i + 1)
            {
                c = c ^ buf[offset + i];
                for (int k = 0; k < 8; k = k + 1)
                {
                    if ((c & 1u) != 0)
                    {
                        c = 0xEDB88320u ^ (c >> 1);
                    }
                    else
                    {
                        c = c >> 1;
                    }
                }
            }
            return c;
        }

        /// <summary>LZ4 块解压（Unity 使用的 block 格式，无帧头）——写出到 dst 的 dstAt 起，成功返回写出字节数。</summary>
        private static int Lz4BlockDecompress(byte[] src, int srcAt, int srcLen, byte[] dst, int dstAt, int dstLen, out string error)
        {
            error = null;
            int ip = srcAt;
            int end = srcAt + srcLen;
            int op = dstAt;
            int opEnd = dstAt + dstLen;
            while (ip < end)
            {
                int token = src[ip];
                ip = ip + 1;
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b = 255;
                    while (b == 255)
                    {
                        if (ip >= end)
                        {
                            error = "字面量长度越界";
                            return 0;
                        }
                        b = src[ip];
                        ip = ip + 1;
                        litLen = litLen + b;
                    }
                }
                if (ip + litLen > end || op + litLen > opEnd)
                {
                    error = "字面量越界（ip " + ip.ToString() + " · lit " + litLen.ToString() + " · op " + op.ToString() + "）";
                    return 0;
                }
                Array.Copy(src, ip, dst, op, litLen);
                ip = ip + litLen;
                op = op + litLen;
                if (ip >= end)
                {
                    break;
                }
                if (ip + 2 > end)
                {
                    error = "匹配偏移越界";
                    return 0;
                }
                int offset = src[ip] | (src[ip + 1] << 8);
                ip = ip + 2;
                if (offset == 0 || offset > op - dstAt)
                {
                    error = "匹配偏移非法（" + offset.ToString() + "）";
                    return 0;
                }
                int matchLen = token & 0x0F;
                if (matchLen == 15)
                {
                    int b = 255;
                    while (b == 255)
                    {
                        if (ip >= end)
                        {
                            error = "匹配长度越界";
                            return 0;
                        }
                        b = src[ip];
                        ip = ip + 1;
                        matchLen = matchLen + b;
                    }
                }
                matchLen = matchLen + 4;
                if (op + matchLen > opEnd)
                {
                    error = "匹配越界";
                    return 0;
                }
                int from = op - offset;
                for (int i = 0; i < matchLen; i = i + 1)
                {
                    dst[op + i] = dst[from + i];
                }
                op = op + matchLen;
            }
            return op - dstAt;
        }

        /// <summary>读大端 32 位整数（带边界钳制）。</summary>
        private static int ReadInt32Big(byte[] buf, int at)
        {
            if (at < 0 || at + 4 > buf.Length)
            {
                return 0;
            }
            return (buf[at] << 24) | (buf[at + 1] << 16) | (buf[at + 2] << 8) | buf[at + 3];
        }

        /// <summary>读大端 64 位整数（带边界钳制）。</summary>
        private static long ReadInt64Big(byte[] buf, int at)
        {
            if (at < 0 || at + 8 > buf.Length)
            {
                return 0;
            }
            long v = 0;
            for (int i = 0; i < 8; i = i + 1)
            {
                v = (v << 8) | buf[at + i];
            }
            return v;
        }

        /// <summary>读小端 32 位整数（带边界钳制）。</summary>
        private static int ReadInt32Little(byte[] buf, int at)
        {
            if (at < 0 || at + 4 > buf.Length)
            {
                return 0;
            }
            return buf[at] | (buf[at + 1] << 8) | (buf[at + 2] << 16) | (buf[at + 3] << 24);
        }

        /// <summary>读小端 64 位整数（带边界钳制）。</summary>
        private static long ReadInt64Little(byte[] buf, int at)
        {
            if (at < 0 || at + 8 > buf.Length)
            {
                return 0;
            }
            long v = 0;
            for (int i = 7; i >= 0; i = i - 1)
            {
                v = (v << 8) | buf[at + i];
            }
            return v;
        }

        /// <summary>跳过 null 终止字符串（返回终止符之后的位置）。</summary>
        private static int SkipCString(byte[] buf, int at)
        {
            while (at < buf.Length && buf[at] != 0)
            {
                at = at + 1;
            }
            return at + 1;
        }

        /// <summary>写大端 32 位整数。</summary>
        private static void WriteInt32Big(byte[] buf, int at, int value)
        {
            buf[at] = (byte)((value >> 24) & 0xFF);
            buf[at + 1] = (byte)((value >> 16) & 0xFF);
            buf[at + 2] = (byte)((value >> 8) & 0xFF);
            buf[at + 3] = (byte)(value & 0xFF);
        }

        /// <summary>SerializedFile 的 TypeTree 节点（v17 布局：24 字节）。</summary>
        private sealed class SfNode
        {
            /// <summary>层级深度（0 = 根节点）。</summary>
            public int Level;

            /// <summary>类型名偏移（高位 0x80 = Unity 内置类型名表索引）。</summary>
            public long TypeStrOffset;

            /// <summary>字段名偏移（编码同 TypeStrOffset）。</summary>
            public long NameStrOffset;

            /// <summary>字段字节数（-1 = 变长）。</summary>
            public int ByteSize;

            /// <summary>元标志。</summary>
            public int MetaFlag;
        }

        /// <summary>SerializedFile 的类型条目。</summary>
        private sealed class SfType
        {
            /// <summary>Unity classID（28 = Texture2D · 43 = Mesh · 21 = Material）。</summary>
            public int ClassId;

            /// <summary>TypeTree 节点数。</summary>
            public int NodeCount;

            /// <summary>节点列表。</summary>
            public List<SfNode> Nodes = new List<SfNode>();

            /// <summary>本类型字段名池。</summary>
            public byte[] StringBuffer;

            /// <summary>字段名池字节数。</summary>
            public int StringBufferSize;
        }

        /// <summary>SerializedFile 的对象条目。</summary>
        private sealed class SfObject
        {
            /// <summary>对象路径 ID。</summary>
            public long PathId;

            /// <summary>数据相对数据区起点。</summary>
            public int ByteStart;

            /// <summary>数据字节数。</summary>
            public int ByteSize;

            /// <summary>类型下标。</summary>
            public int TypeId;
        }
    }
}
