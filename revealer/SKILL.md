# 发布技能 — KK Manager by Rushiera

> 定位：**本项目发布动作的操作流程**——把一句「发布」展开为从自检到交付的全链。
> 上级规范：通用要求 → `L2/Job/Release`（跨项目默认）；本项目具体值 → `RELEASE.md`。本文件只展开动作，不改规范。
> 触发：Rushiera 说「发布」/「发布新版本」/「重发」→ 按本文件全链执行。
> 加载：本项目发布 = Coder 主人格 + CSharp Job（编译与自检）+ 本文件 + `RELEASE.md`。

---

## 〇、触发语义（一句话）

「**发布**」= 现场核对 → 编译版自检 → 编译版号 +1 → 发布编译（自包含单文件 + ReleasePack + 发布版号）→ 五项核验 → 组装 `revealer/kkmanager<发布版号>/` → 台账与 CCBP 文档更新 → 交付报告（终态断言）。

- **不逐条问确认**——发布链是既定流程；唯一停下问人的情形 = 核验三次修正仍不收敛 / 用户点名要求改结构。
- **零功能变更也照跑全链**（重发 = 发布版号 +1 + 全量重发，旧目录原样保留）。
- 🔴 **发布必带更新记录**（2026-09-27 Rushiera 定）——三处同源、口径一致：仓库 `README.md` 的「版本更新记录」段（**置顶**——标题之后第一段，最新版在最上）· 发布包 `readme.txt` 的【本版更新】段 · `RELEASE.md` 台账「备注」列一句话。内容 = **本次发布相对上一发布版做了什么**（按发布版号口径汇总，不是编译版流水）。
- 🔴 **发布即推 GitHub**（2026-09-27 Rushiera 定）——文档与源码改动提交后 `push origin main`；exe / zip **不入仓**（`.gitignore` 已排除）→ 走 Release 附件。本机无 `gh`——附件上传由 Rushiera 在网页执行，报告里列为待办。

---

## 一、变量（每次发布先取）

| 变量 | 取值 |
|:--|:--|
| `<发布版号>` | 读 `RELEASE.md` §八 台账**最后一行** → 末位 +1（1.0.0 → 1.0.1）；用户点名跳级时按其指定 |
| `<编译版号>` | 读 `KKManager.csproj` 的 `<Version>`；本轮有源码改动 → 补丁位 +1；纯重发不动 |
| `<仓库>` | `D:\Mau\WorkSpace\KKManager`（寻址 `workspace:KKManager`）——**有 git 仓库**，远程 `https://github.com/Rushiera/KKManager.git`（`main`） |
| `<临时输出>` | `D:\Mau\WorkSpace\CatTemp\kmrpub<号去点>`（1.0.1 → `kmrpub101`） |
| `<上一版目录>` | `revealer/kkmanager<上一发布版号>/`（readme 与 tools 的复制来源） |

---

## 二、动作链（10 步）

### 步 1 — 现场核对（只读）

- `info`（受控根）· `file-tree workspace:KKManager/revealer`（上版目录 + 台账）
- `text-read_lines WorkSpace:KKManager/revealer/RELEASE.md`（取台账末行 + 复核规范未变——**实例规范档在发布区**，知识网络侧无副本）
- `git -C D:\Mau\WorkSpace\KKManager status --short`（工作区基线）+ `git log --oneline -3`（HEAD）——本项目**有 git 仓库**，发布收尾必推送（步 8b）

### 步 2 — 编译版自检（不过不发布）

| 工具 | 期望 |
|:--|:--|
| `cs-check`（csproj） | 0 error / 0 warning / 空 catch 0 |
| `cs-build`（csproj） | 0 error / 0 warning |
| `cs-format` mode=check | 0 差异 |

### 步 3 — 编译版号推进

- 本轮有源码改动 → `text-replace` 改 `KKManager/KKManager.csproj` 的 `<Version>`（补丁位 +1）→ 重跑步 2 自检
- 纯重发（无源码改动）→ 跳过本步

### 步 4 — 发布编译（固定通道，顺序不可乱）

```powershell
dotnet restore D:\Mau\WorkSpace\KKManager\KKManager\KKManager.csproj -r win-x64
```

```powershell
dotnet publish D:\Mau\WorkSpace\KKManager\KKManager\KKManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:ReleasePack=true -p:Version=<发布版号> -o D:\Mau\WorkSpace\CatTemp\kmrpub<号去点>
```

- `restore -r win-x64` 不可省——`cs-build` 会刷掉 RID 资产，缺则 `NETSDK1047`
- 输出到 CatTemp 再入位——运行中的 exe 锁文件不可覆盖

### 步 5 — 五项核验（全绿才继续）

| # | 核验 | 期望 |
|:--:|:--|:--|
| 1 | `file-version <临时输出>\KMR.exe` | = `<发布版号>` |
| 2 | `<临时输出>\KMR.exe help` | 首行含「发布版（ReleasePack 编译）」——**缺 = 漏传开关 → 重跑步 4** |
| 3 | `<临时输出>\KMR.exe roots-list` | 正常列出库根（SQLite 原生库随单文件解出） |
| 4 | `text-read KKManager/KKManager/bin/Release/net8.0-windows/win-x64/KMR.runtimeconfig.json` | `includedFrameworks` 含 NETCore.App / WindowsDesktop.App / AspNetCore.App 三项 |
| 5 | `file-tree <临时输出>` | 仅 `KMR.exe` 一个文件 |

### 步 6 — 组装发布版目录

```
revealer/kkmanager<发布版号>/
    KMR.exe                 ← file-copy 自 <临时输出>
    清空所有数据.bat         ← file-copy 自 dist/（字节复制——GBK / CRLF 保真）
    readme.txt              ← file-copy 自 <上一版目录> + text-replace 版本行（首行 + 【版本】段）
                              + 新增【本版更新】段（本次相对上一发布版做了什么——🔴 必带）
    tools/命令行用法.txt     ← file-copy 自 <上一版目录> + text-replace 版本行 + 补本轮新增子命令
```

- 🔴 **readme.txt 的【本版更新】段**与仓库 `README.md`「版本更新记录」段同源——同一次发布的同一批变更，两处口径一致（readme 面向面客、README 面向仓库访客）

- 首次发布（无上一版目录）→ readme / tools 按 `RELEASE.md` 口径新写
- 形态核验：`file-tree workspace:KKManager/revealer/kkmanager<发布版号>` → 根目录仅三样 + `tools/`
- `file-copy` 遇目标已存在会拒绝——覆盖先 `file-delete`（软删除）

### 步 7 — 台账更新

`RELEASE.md` §八 追加一行：

```
| <发布版号> | `kkmanager<发布版号>/` | <编译版号> | <日期> | <字节数> 字节 | <一句话——本次做了什么，与 README / readme.txt 同源> |
```

### 步 8 — 文档更新（CCBP 侧 + 仓库侧）

| 文件 | 动作 |
|:--|:--|
| `CHANGELOG.md` | 顶部插入 `## v<编译版号> — <日期> — <标题>` + 要点（≤6 条） |
| `design-KKManager_log.md` | 追加 `## <章号>、发布 v<发布版号>（v<编译版号>）`——转换链 / 核验值 / 目录形态 |
| `project.md` | 版本行 → v<编译版号> |
| `Project/_index.md` | KKManager 行：描述追加一句 + 版本 → v<编译版号> |
| `todo.md` | 「上一轮需确认」段重写：打包 + 双击复验 + 遗留疑问 |
| `README.md`（仓库根） | 🔴 顶部「版本更新记录」段**插入本版条目（最新在最上）** + 正文「当前 <发布版号>」行更新 |

### 步 8b — 提交与推送 GitHub

- `git -C <仓库> add -A` → `git -C <仓库> commit -m "v<编译版号>: <一句话>"` → `git -C <仓库> push origin main`
- 入仓面：源码 · `README.md` · `revealer/*.md` · 发布版文本件（`readme.txt` / `tools/*.txt` / `清空所有数据.bat`）；**exe 与 zip 不入仓**（`.gitignore` 已排除）
- 本机**无 `gh`** → Release 附件（zip / exe）由 Rushiera 在网页上传——报告列为待办

### 步 9 — 清理

- `file-delete <临时输出>`（含核验时 `roots-list` 生成的 `data/`）
- 确认 CatTemp 本轮产物已清（`fs_recycle` 是回收站，不动）

### 步 10 — 交付报告（终态断言）

- 完成项 / 落盘位置 / 遗留项去向——**零疑问句**
- 必含：发布版号 · 编译版号 · 产物字节数 · 五项核验结果 · **更新记录落点**（README / readme.txt / 台账三处同源）· **推送 commit**（步 8b）· 待用户做的（打包 zip + Release 附件上传 + 双击复验）

---

## 三、失败处理

| 症状 | 处置 |
|:--|:--|
| `help` 首行无发布标识 | 漏传 `-p:ReleasePack=true` → 重跑步 4 |
| `NETSDK1047` | 补 `dotnet restore -r win-x64` → 重跑 publish |
| publish 输出目录多出 dll | 漏 `IncludeNativeLibrariesForSelfExtract` → 重跑 |
| 自检有 error / warning | 先修再发布——**自检不过不发布** |
| 同一环节三次修正不收敛 | 停下汇总（已试方案 / 当前状态 / 卡点 / 建议）等 Rushiera 拍板 |
| 端口 8539 被占（用户复验时） | 报告里提示关闭旧实例——Agent 不起进程、不杀进程 |

---

## 四、边界（不做）

- **不改发布版内容**——生成即冻结；要改 = 新发布版号全量重发
- **不生成压缩包**——打包由 Rushiera 执行
- **不起实例 / 不杀进程**——面板启动与重启由 Rushiera 主持
- **不动旧版本目录**——历史发布版原样保留
- **不改规范**——结构 / 铁律 / 判据的变更走 `RELEASE.md`，本文件只展开动作

---

## 五、提示词（整段可用）

> 你是 Coder，主理 KK Manager by Rushiera。收到「发布」指令时，按 `WorkSpace:KKManager/revealer/SKILL.md` 全链执行，不逐条问确认：
>
> ① 读 `RELEASE.md` 台账取发布版号（末位 +1）；② 编译版自检（`cs-check` / `cs-build` / `cs-format check` 全绿）；③ 编译版号补丁位 +1（纯重发跳过）；④ `dotnet restore -r win-x64` → `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:ReleasePack=true -p:Version=<发布版号> -o CatTemp\kmrpub<号去点>`；⑤ 五项核验（`file-version` = 发布版号 · `help` 首行发布标识 · `roots-list` 通 · `runtimeconfig` 的 `includedFrameworks` 三项 · 输出目录仅 exe）；⑥ 组装 `revealer/kkmanager<发布版号>/`（exe + 清空所有数据.bat + readme.txt + tools/，根目录仅三样）；⑦ 更新 `RELEASE.md` 台账 · CCBP 文档（CHANGELOG / log / project.md / _index.md / todo）· 仓库 `README.md`「版本更新记录」段（置顶、最新在最上）· 发布包 `readme.txt` 的【本版更新】段；⑧ 提交并推送 GitHub（`git add -A` → commit → `push origin main`；exe / zip 不入仓）；⑨ 清 CatTemp；⑩ 交付报告（终态断言、零疑问句——含更新记录三处落点 + 推送 commit + 待用户做的：打包 zip + Release 附件上传 + 双击复验）。
>
> 核验三次修正仍不收敛才停下问人；自检不过不发布；不改发布版内容、不打包、不起进程。
