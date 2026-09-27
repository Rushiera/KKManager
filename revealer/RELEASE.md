# KK Manager by Rushiera — 发布规范（revealer）

> 定位：**发布版（面客产物）的唯一权威**——目录结构 / 命名 / 转换链 / 依赖自包含 / 发布前核验。
> 通用规范：`L2/Job/Release`（铁律 / 结构骨架 / 版本号 / 核验五项 / 台账模板的**跨项目权威**）——本文件只写本项目的具体值与差异，不重抄通用要求。
> 加载：涉及发布 / 打包 / 面客交付时**必读**；调试版（编译版）日常演进不受本文件约束，但**转换按本文件执行**。
> 操作流程：`SKILL.md`——一句「发布」的完整动作链（触发语义 / 变量 / 10 步 / 失败处理 / 提示词）。收到「发布」指令 → 按 `SKILL.md` 全链执行。
> 上级：`ccbp:Project/KKManager/project.md` · 证据明细：`ccbp:Project/KKManager/design-KKManager_log.md`

---

## 一、目录与命名

```
WorkSpace:KKManager/revealer/
  RELEASE.md              本文件（发布规范）
  SKILL.md                发布技能——一句「发布」的完整动作链
  kkmanager<发布版号>/     发布版目录 —— 打包对象就是这个目录
      KMR.exe             ← 主程序（双击即起面板）
      清空所有数据.bat     ← 回到初始状态
      readme.txt          ← 面客使用说明
      tools/              ← 二级文件夹：其余内容一律进这里（启动脚本 / 附属工具 / 说明文档）
```

- **发布版号独立编号**——与编译版（0.18.x）**无关**，起始 **1.0.0**，每次发布 +1（整数递增：1.0.0 → 1.0.1 → …）
- 文件夹名 = `kkmanager` + 发布版号（如 `kkmanager1.0.0`）；打包对象 = 该目录整棵
- **打包由 Rushiera 执行**——Agent 只负责把目录整理到可打包状态，不生成压缩包

## 二、三条铁律

| # | 铁律 | 判据 |
|:--:|:--|:--|
| 1 | **只能由编译版转换** | 发布版内容一经生成即**冻结**，不再编辑；更新 = 全量重发 + 发布版号 +1（旧版本目录原样保留，不回改） |
| 2 | **根目录仅三样** | 根目录 = `KMR.exe` + `清空所有数据.bat` + `readme.txt`；**其余内容一律进二级文件夹 `tools/`**（运行时自动生成的 `data/` 除外——见 §六） |
| 3 | **调试专用功能不入发布版** | 不入包清单：双槽命名 `_A/_B`（发布版单 exe `KMR.exe`）· `启动.bat`（杀端口 + 扫 `KMR_*.exe` 双槽逻辑）· 探针工程 `KKManagerProbe`（从不进包）· CLI 诊断子命令**保留但不宣传**（readme 不提） |

## 三、发布版专用编译（启动入口）

发布版与调试版的**行为差异只在发布编译中生效**——调试期工作流照旧（`启动.bat` 显式传参起面板），发布版 exe 自带参数（双击即用）。

| 项 | 调试版 | 发布版 |
|:--|:--|:--|
| 启动方式 | `启动.bat` → `KMR_A/B.exe serve --port 8539 --open` | **双击 `KMR.exe`**（无参 = `serve --port 8539 --open`） |
| 无参行为 | 打印用法，退出码 2 | 起面板 + 打开浏览器 |
| 版本号 | csproj `<Version>`（编译版号，递增） | 编译时注入 `-p:Version=<发布版号>` |
| 编译开关 | 无 | `-p:ReleasePack=true`（定义 `RELEASEPACK` 条件编译符号） |

- 开关落地：`KKManager.csproj` 在 `ReleasePack=true` 时追加 `DefineConstants` → `Program.Main` 的无参分支按 `#if RELEASEPACK` 分流
- 🔴 **漏传开关 = 静默失败**（产物看起来正常，双击却只打印用法）——发布前核验第 1 条即为此设（§七）

## 四、转换链（固定通道）

> 逐步动作链（自检 → 编译 → 核验 → 组装 → 文档 → 报告，含失败处理与提示词）→ `SKILL.md`。

```powershell
dotnet restore -r win-x64
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:ReleasePack=true -p:Version=<发布版号> -o <CatTemp 临时输出>
```

→ 产物 `KMR.exe` 移入 `revealer/kkmanager<发布版号>/` → 复制 `清空所有数据.bat` → 写 `readme.txt` → 按 §七 核验。

- 编译前先跑交付自检（`cs-check` / `cs-build` 0/0 / `cs-format check` 零差异）——**自检不过不发布**
- `dotnet restore -r win-x64` 不可省——`cs-build`（Debug）会刷掉 RID 资产，缺则 `NETSDK1047`
- 运行中的 exe 锁文件不可覆盖 → 输出到 `CatTemp` 再移动入位

## 五、依赖自包含（面客可能没有 .NET 8）

**面客机器不要求预装 .NET 8 运行时**——一切依赖随 exe：

| 依赖 | 来源 | 随包方式 |
|:--|:--|:--|
| .NET 8 运行时（coreclr / 基础库） | `--self-contained true` + `-r win-x64` | 内嵌单文件 |
| ASP.NET Core（本地面板服务） | `FrameworkReference Microsoft.AspNetCore.App` | 同上 |
| WindowsDesktop（WinForms 文件夹选择框） | `net8.0-windows` + `UseWindowsForms` | 同上 |
| SQLite 原生库（`e_sqlite3`） | `Microsoft.Data.Sqlite` → SQLitePCLRaw | `IncludeNativeLibrariesForSelfExtract=true`（缺则原生库落在 exe 之外——实测单 exe 182.1 MB vs 完整 192.6 MB） |
| 前端资源（`index.html`） | 内嵌资源 | 编译期嵌入 |

**自包含判据（三项全绿才算发布）**：

1. `obj/Release/net8.0-windows/win-x64/KMR.runtimeconfig.json` 含 `includedFrameworks` 段（self-contained 实锤；框架依赖形态此处是 `framework`）
2. 产物体积 **≥150 MB**（框架依赖单文件仅几 MB 量级）
3. 干净目录实跑 `KMR.exe roots-list` 通（SQLite 原生库随单文件解出自证）

## 六、运行时数据目录

- 数据目录 = **exe 同目录** `data\kkmanager.db`（`Program.cs` 取 `AppContext.BaseDirectory`）——首次运行自动创建
- `清空所有数据.bat` 按**自身目录**找 `data`（`%~dp0data`）⇒ 二者**必须同级**，否则清不到
- 「根目录仅三样」= **发布包解压时的形态**；运行后根目录会多出 `data/`（软件自己的数据，非发布内容）

## 七、发布前核验（消费点实锤）

| # | 项 | 判据 |
|:--:|:--|:--|
| 1 | 无参启动 | 无参实跑 `KMR.exe` → 起面板 + 打开浏览器（不是打印用法）——**本机由 Rushiera 主持**（Agent 无起实例通道） |
| 1b | 发布标识 | `KMR.exe help` 首行含「发布版（ReleasePack 编译）」——漏传 `-p:ReleasePack=true` 时此处不出声（静默失败哨兵） |
| 2 | 版本自证 | `file-version KMR.exe` = 发布版号 · 面板右上角显示发布版号 |
| 3 | 依赖自包含 | §五 三项判据全绿 |
| 4 | 目录形态 | 根目录仅三样（+ 运行时 `data/`）· `tools/` 承载其余 |
| 5 | 冻结核对 | 生成后发布目录不再编辑——需要改 = 发布版号 +1 全量重发 |

## 八、发布台账（每次发布追加一行）

| 发布版号 | 目录 | 编译版号 | 日期 | 产物大小 | 备注 |
|:--|:--|:--|:--|--:|:--|
| 1.0.0 | `kkmanager1.0.0/` | 0.18.5 | 2026-09-27 | 192,605,581 字节 | 首个发布版——双击 exe 即起面板 · 自包含单文件 |
| 1.0.1 | `kkmanager1.0.1/` | 0.18.6 | 2026-09-27 | 192,605,581 字节 | 首用报错修复（换游戏根预置条目处置 + 保存可见性）· 双击 exe 即起面板 |
