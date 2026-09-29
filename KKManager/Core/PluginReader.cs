using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace KKManager.Core
{
    /// <summary>一个插件 dll 里的一项插件元数据（只读解析所得——不加载程序集、不执行任何代码）。</summary>
    public class PluginInfo
    {
        /// <summary>dll 绝对路径。</summary>
        public string filePath { get; set; } = "";

        /// <summary>文件名（含扩展名）。</summary>
        public string fileName { get; set; } = "";

        /// <summary>插件 GUID（BepInPlugin 第一参数；IPA 插件为「IPA.&lt;名&gt;」）。</summary>
        public string guid { get; set; } = "";

        /// <summary>插件显示名。</summary>
        public string name { get; set; } = "";

        /// <summary>插件版本。</summary>
        public string version { get; set; } = "";

        /// <summary>进程过滤（BepInProcess 参数；空 = 所有进程都加载）。</summary>
        public List<string> processes { get; set; } = new List<string>();

        /// <summary>依赖的插件 GUID（BepInDependency 第一参数）。</summary>
        public List<string> dependencies { get; set; } = new List<string>();

        /// <summary>是否为 IPA 插件（旧框架，无 BepInPlugin 特性）。</summary>
        public bool isIpa { get; set; }
    }

    /// <summary>插件 dll 元数据解析——走 PE / 元数据只读通道（System.Reflection.Metadata），不加载程序集、不执行代码。</summary>
    public static class PluginReader
    {
        /// <summary>元数据里固定参数的类型提供者（解码 CustomAttribute 用——只取类型名，不解析真实类型）。</summary>
        private sealed class AttrTypeProvider : ICustomAttributeTypeProvider<string>
        {
            /// <summary>取基元类型名。</summary>
            public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                return typeCode.ToString();
            }

            /// <summary>取 System.Type 名。</summary>
            public string GetSystemType()
            {
                return "System.Type";
            }

            /// <summary>取数组类型名。</summary>
            public string GetSZArrayType(string elementType)
            {
                return elementType + "[]";
            }

            /// <summary>按类型定义取类型名。</summary>
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                return reader.GetString(reader.GetTypeDefinition(handle).Name);
            }

            /// <summary>按类型引用取类型名。</summary>
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                return reader.GetString(reader.GetTypeReference(handle).Name);
            }

            /// <summary>按序列化名取类型名。</summary>
            public string GetTypeFromSerializedName(string name)
            {
                return name;
            }

            /// <summary>取枚举底层类型（本解析不关心——一律按 Int32 处理）。</summary>
            public PrimitiveTypeCode GetUnderlyingEnumType(string type)
            {
                return PrimitiveTypeCode.Int32;
            }

            /// <summary>判断是否 System.Type。</summary>
            public bool IsSystemType(string type)
            {
                return type == "System.Type";
            }
        }

        /// <summary>类型提供者单例（无状态，跨调用复用）。</summary>
        private static readonly AttrTypeProvider Provider = new AttrTypeProvider();

        /// <summary>解析一个 dll 里的全部插件——一个 dll 可含多个插件类（如 KKAPI.dll 含三个）；失败返回空列表并给出原因。</summary>
        public static List<PluginInfo> ReadAll(string path, out string error)
        {
            List<PluginInfo> list = new List<PluginInfo>();
            error = "";
            if (!File.Exists(path))
            {
                error = "文件不存在";
                return list;
            }
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    using (PEReader pe = new PEReader(fs))
                    {
                        // [段1] 只读元数据——原生 dll（无元数据）如实出声，不猜
                        if (!pe.HasMetadata)
                        {
                            error = "非 .NET 程序集（无元数据）";
                            return list;
                        }
                        MetadataReader md = pe.GetMetadataReader();
                        // [段2] 快速过滤——不引用插件框架的程序集不可能是插件（省去全类型遍历，大 dll 秒过）
                        if (!ReferencesPluginFramework(md))
                        {
                            error = "未引用插件框架（BepInEx / IllusionPlugin）";
                            return list;
                        }
                        // [段3] 遍历类型定义读插件特性
                        ReadPluginAttributes(md, path, list);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return list;
            }
            if (list.Count == 0 && error.Length == 0)
            {
                error = "未找到插件特性（BepInPlugin / IPA Plugin）";
            }
            return list;
        }

        /// <summary>遍历全部类型定义读插件特性（BepInPlugin 加在插件类上，不在程序集上）——每个带特性的类产出一项。</summary>
        private static void ReadPluginAttributes(MetadataReader md, string path, List<PluginInfo> list)
        {
            foreach (TypeDefinitionHandle typeHandle in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(typeHandle);
                PluginInfo info = null;
                List<string> processes = new List<string>();
                List<string> dependencies = new List<string>();
                foreach (CustomAttributeHandle handle in td.GetCustomAttributes())
                {
                    CustomAttribute attr = md.GetCustomAttribute(handle);
                    string typeName = AttributeOwnerName(md, attr, false);
                    if (typeName == "BepInPlugin")
                    {
                        List<string> args = StringArgs(attr);
                        if (args.Count >= 3 && info == null)
                        {
                            info = new PluginInfo();
                            info.guid = args[0];
                            info.name = args[1];
                            info.version = args[2];
                        }
                        continue;
                    }
                    if (typeName == "BepInProcess")
                    {
                        List<string> args = StringArgs(attr);
                        if (args.Count >= 1)
                        {
                            processes.Add(args[0]);
                        }
                        continue;
                    }
                    if (typeName == "BepInDependency")
                    {
                        List<string> args = StringArgs(attr);
                        if (args.Count >= 1)
                        {
                            dependencies.Add(args[0]);
                        }
                        continue;
                    }
                    // IPA 插件（旧框架）——特性名 Plugin / PluginAttribute 且命名空间为 IllusionPlugin
                    if ((typeName == "Plugin" || typeName == "PluginAttribute") && AttributeOwnerName(md, attr, true) == "IllusionPlugin")
                    {
                        List<string> args = StringArgs(attr);
                        if (args.Count >= 2 && info == null)
                        {
                            info = new PluginInfo();
                            info.isIpa = true;
                            info.name = args[0];
                            info.version = args[1];
                            info.guid = "IPA." + args[0];
                        }
                    }
                }
                if (info != null)
                {
                    info.filePath = path;
                    info.fileName = Path.GetFileName(path);
                    info.processes = processes;
                    info.dependencies = dependencies;
                    list.Add(info);
                }
            }
        }

        /// <summary>取特性的固定参数字符串表（非字符串参数跳过——插件特性只用到字符串）。</summary>
        private static List<string> StringArgs(CustomAttribute attr)
        {
            List<string> list = new List<string>();
            CustomAttributeValue<string> value = attr.DecodeValue(Provider);
            foreach (CustomAttributeTypedArgument<string> arg in value.FixedArguments)
            {
                string text = arg.Value as string;
                if (!string.IsNullOrEmpty(text))
                {
                    list.Add(text);
                }
            }
            return list;
        }

        /// <summary>取特性构造函数所属类型的名（wantNamespace=false）或命名空间（true）。</summary>
        private static string AttributeOwnerName(MetadataReader md, CustomAttribute attr, bool wantNamespace)
        {
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    return wantNamespace ? md.GetString(tr.Namespace) : md.GetString(tr.Name);
                }
                if (mr.Parent.Kind == HandleKind.TypeDefinition)
                {
                    TypeDefinition td = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                    return wantNamespace ? md.GetString(td.Namespace) : md.GetString(td.Name);
                }
            }
            if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinition mdef = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                TypeDefinition owner = md.GetTypeDefinition(mdef.GetDeclaringType());
                return wantNamespace ? md.GetString(owner.Namespace) : md.GetString(owner.Name);
            }
            return "";
        }
        /// <summary>该程序集是否引用了插件框架（BepInEx / IllusionPlugin）——不引用的不可能是插件，用它省去全类型遍历。</summary>
        /// <param name="md">元数据读取器。</param>
        /// <returns>引用了任一插件框架返回 true。</returns>
        private static bool ReferencesPluginFramework(MetadataReader md)
        {
            foreach (AssemblyReferenceHandle handle in md.AssemblyReferences)
            {
                AssemblyReference reference = md.GetAssemblyReference(handle);
                string name = md.GetString(reference.Name);
                if (name == "BepInEx" || name == "IllusionPlugin" || name == "IPA")
                {
                    return true;
                }
            }
            return false;
        }
    }
}
