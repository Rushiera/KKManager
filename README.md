# KK Manager by Rushiera

Koikatsu 卡片 ↔ zipmod 关联管理器——把「这张卡用了哪些 mod / 缺哪个 / 对应本地哪个文件」从人肉比对变成可查询的数据库。

## 形态

- Windows x64 单文件 exe（.NET 8 自包含，面客机器无需预装运行时）
- 双击即起本地面板（浏览器），端口 8539
- 运行数据 = exe 同目录 `data\kkmanager.db`（首次运行自动创建）

## 使用

1. 下载 Release 里的发布包，解压到任意目录
2. 双击 `KMR.exe` → 浏览器自动打开面板
3. 面板顶部「库根设置」先设游戏根（Koikatsu 安装目录），再点「更新本库」扫描
4. 卡片边框四色 = 引用状态（绿=全部在主库 · 黄=仅缓存库 · 灰=仅冷冻库 · 红=有缺失）

## 构建

```
dotnet build KKManager/KKManager.csproj
```

调试期产物走 `dist\` 双槽（`KMR_A.exe` / `KMR_B.exe` 交替发布），`dist\启动.bat` 起最新产物。

## 发布

规范与动作链在仓库内：

- `revealer/RELEASE.md`——发布版结构与命名 · 转换链 · 依赖自包含判据 · 发布前核验 · 台账
- `revealer/SKILL.md`——一句「发布」的完整动作链

发布版号独立编号（当前 1.0.1）。**exe 与压缩包不入仓库**——走 Release 附件。

## 数据与隐私

- 全部数据落在本地：`data\kkmanager.db` + 卡片编辑留档 `card_archive\`
- 软件不含遥测、不上传任何数据
- 面板与 CLI 只在本机回环端口服务

## 边界

- 卡片编辑按**最小改动**写回（只改被编辑的字节，文件时间与属性还原），原版先留档在软件内部
- 不修改、不重打包 zipmod；`abdata` 内资产只读预览
- 不含、不下载任何游戏资产与 mod——相关版权归各自作者

## 目录

```
KKManager/          主程序（Core 解析 / Data 存储 / Web 面板）
KKManagerProbe/     取证探针（不随发布）
revealer/           发布区（规范 + 发布版目录）
dist/               调试产物区（不入仓）
```
