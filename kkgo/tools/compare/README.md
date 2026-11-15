# kkgo 对拍工具

G 段验收工具：同一样本、同一参数下跑 `kkgo.exe` 与 `KMR_B.exe`（C# 0.21.9 调试产物 = 当前源码）对比输出。

> 🔴 基准槽位：`dist\KMR_A.exe` 是 0.21.6 旧产物（缺 refs-read / card-sort / index-import / idx / sort / u3d-db 等命令）；
> 复核轮起基准切到 `dist\KMR_B.exe`（0.21.9，2026-11-15 编译入槽），A 槽保留回退。

## 判据

| 项 | 口径 |
|:--|:--|
| 退出码 | 必须相等 |
| 行数 | 必须相等 |
| 逐行内容 | 剔除非 ASCII 后逐字相同（C# 控制台中文是 GBK 输出被按 UTF-8 读，属编码伪差异——两侧各按真实编码解码） |
| thumb | 只比尺寸 / 卡类型 / 图片区；字节数差异属编码器不同（ImageSharp vs stdlib），不作判据 |
| 诊断文本 | 异常 message 措辞差异只比对分类，不比对原文 |
| card-analysis | C# 命令面**没有**这条命令——对拍落库产物：临时库单卡扫描后比 `card_analysis.data` 的 JSON（键集 / 键序 / 值 / 类型四层） |
| roots / scan-plan | **交互相**：C# 从模板建库 → 同一串写操作分别由 C# 与 Go 写到两份库 → 每份库再由两侧各读一遍（四向读回）；首行「库: <路径>」不比 |
| libmap | C# 命令面**没有** `modfiles`——真相源侧用 Python 按 C# 规则直读库算副本清单，与 `kkgo modfiles <guid>` 逐行比 |
| composition | 真库复制两份 → 各自跑一次 → 输出行比对（「建档于」归一 `<TS>`）+ 落库 10 字段比对 |
| plugin-log | 真日志被游戏独占——走临时库 + 日志副本；含「日志缺失」分支（两侧 `解析失败` + exit 1） |
| plugin-match | 真库直比（只读主库 `plugin_file`） |
| stats | ① 裁剪临时库（预置条目 + 7000 系列 32 片）43 行逐行全同；② 真库全量 43 行——基准切到 0.21.9 后「mod 文件副本」一行也应一致（0.21.6 时代的代际差异已消失，脚本保留该行归一逻辑以备回退） |
| scan-mods | 三根样本（main / cache / cold + 同 guid 跨根副本 + 子目录 + 非 mod 文本 + 坏 zip + 无 manifest 容器）三轮：首扫 / 增量复扫 / 删文件后复扫 |
| scan-cards | 卡片样本（人物 ×2 / 服装 ×1 / 场景 ×1 + 纯图片 + 截断 + 非 PNG）两轮；`scan_shards=4` · `KKM_SCAN_WORKERS=1` |
| scan-main / scan-extra | 五根合一（mod 三根 + 卡片两根）+ 插件根不存在口径；两个范围各一轮 |
| scan-plugins | 临时 BepInEx 三轮；`plugin_config.sections` 逐字节（含 `\uXXXX` 大写转义） |
| scan-cards 分析组 | 同 `scan-cards` 样本 + 开分析组（coord,detail,scene）；`card_analysis.data` 逐字节 |
| scan-main 插件段 | 同 `scan-main` 样本但插件根存在——验证 withPlugins 编排 |
| **query（读面四族）** | 真库只读：authors / cards（mtime·chara·miss·size·file 五个排序键 + `--desc` + `--q` 关键词）/ todos / noncards（含 `--moved`）。非卡清单的**并列行按 id 归一**（同体积次序属 SQLite 引擎差异，见已知差异） |
| **dup** | 临时库 + scan 三根样本（同 guid 跨根副本）两轮：首跑算 MD5 落档案 / 二跑命中（新增 0）。判据 = 输出逐行 + `mod_hash` 表行（路径折相对、MD5 逐字） |
| **refs-read** | 临时库 + 卡片样本，扫描时**关掉 refs 步** → 三轮：全量补读 / 幂等复跑 / `--only-pending`。判据 = 输出逐行 + `card_mod` 表行；引用行按**部件粒度**写（同 guid 多部件 = 多行，不去重） |
| **index-import / idx** | 两侧临时库（模板建库 + roots-add kkc 库根）→ 真 kkc 索引只读导入 → 比导入输出（「预建库文件」耗时归一 `<MS>`）+ `card` / `card_mod` / `index_source` 三表行 + `idx` 五组查询（kind / q / wide / root） |
| **sort（按作者整理）** | 两侧各造同形样本（main 根 a + b + sub/a 目标撞车 + cache 跨根副本）→ plan / show / conflict / keep / show / exec 全链；判据 = 每步输出（样本根与时间戳归一）+ `mod_sort_plan_item` / `mod_old` / `mod_file` 三表 + 终态文件树（`*.db` 不比字节） |
| **card-sort（整理链核心）** | 两侧各造同形样本（三张真卡 + zipmod + 文本）→ rules 播种 / 列表 / 导出 / plan / show / exec / moves / refs / rollback / delete / delete-list；判据 = 每步输出 + 计划 TSV / 规则 TSV / 引用表 TSV 逐行 + `card` / `card_rule` / `card_move` / `card_delete` 四表 + 终态文件树。exec 在样本未导索引时被闸门拒绝（两侧同 exit 3 = 行为对拍通过） |
| **card-edit（最小改动写面）** | 两侧各一份卡片副本 → 两轮字段编辑（姓 / 名 / 爱称 / 性格 / 敏感带 / 五项接受）→ **编辑后文件 SHA256 必须相同** + 输出逐行 + 留档件（名归一 + 内容哈希） |
| **u3d / u3d-db** | 文件级列表（`u3d` 含 `--tex`：导出件数与 PNG 尺寸）+ 库级落档（首建 / 命中已有档案）→ 输出逐行 + `mod_u3d` 表行 |

**已知差异（不作判据）**

- **并行 worker 段**：C# 卡片侧按 `scan_workers` 多线程跑分片；Go 侧串行逐片。对拍固定 `KKM_SCAN_WORKERS=1`。
- **缩略图 / 导出 PNG 字节**：C# ImageSharp / Go stdlib 编码器不同——只比尺寸与张数。
- **非卡清单同体积行次序**：C# 侧 SQL 是 `ORDER BY size DESC`（无兜底键），同体积行的相对次序由 SQLite 引擎排序实现决定（C# SQLitePCLRaw vs Go modernc 不保证同序）。语义判据是「按体积降序」，并列次序无定义 → 对拍按 id 归一后再比。
- **「预建库文件 N 个 · K ms」的耗时**：真实时钟差 → 归一 `<MS>`。
- 🔴 **C# 0.21.9 既有缺陷（Go 不复刻）**：库根目录为空（或不存在）时 `scan-mods` 走离线标记 → `Store.AddTodo` 的 upsert SQL 与 `todo` 表约束不匹配 → `SQLite Error 1: ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint` → 未处理异常、进程崩溃（exit `0xE0434352`）。**对拍样本一律给空库根放文件规避**；Go 侧同路径只落库不出声、不崩。

### card-analysis 的对拍管线（产物对拍，非命令对拍）

```
样本卡 → 复制到临时目录 → C# roots-add cards 1 <tmp> 仅本目录 --db <tmp>\t.db
       → C# scan-plan set <全集序> coord,detail,scene --db t.db
       → C# scan-cards --db t.db
       → 读分片库 lib_<rootId>_<shard>.db 的 card_analysis(kind,data)
       → 与 kkgo card-analysis <样本> 的「kind<TAB>JSON」逐分项比
```

## 用法

```bat
py tools\compare\compare.py all                  :: 批量跑 CASES + 全部专项（当前 38 项）
py tools\compare\compare.py card-detail <card>   :: 单命令
py tools\compare\compare.py thumb                :: 缩略图专项
py tools\compare\compare.py timeline             :: 轨道专项（自动挑关键帧最多的场景卡）
py tools\compare\compare.py card-analysis [card] :: 分项分析落库 JSON 对拍
py tools\compare\compare.py roots                :: 库根 / 扫描计划交互相（四向读回）
py tools\compare\compare.py libmap               :: 分片库读面对拍
py tools\compare\compare.py composition          :: 组成档案对拍
py tools\compare\compare.py pluginlog            :: BepInEx 日志对拍（含日志缺失分支）
py tools\compare\compare.py stats                :: 库统计对拍
py tools\compare\compare.py scanmods             :: mod 扫描对拍（三轮）
py tools\compare\compare.py scancards            :: 卡片扫描对拍（两轮）
py tools\compare\compare.py scanall              :: 统一扫描对拍（scan-main / scan-extra）
py tools\compare\compare.py scanplugins          :: 插件库对拍（三轮）
py tools\compare\compare.py scancardsanalysis    :: 卡片分析组对拍（两轮）
py tools\compare\compare.py scanallplugins       :: scan-main 插件段对拍（两轮）
py tools\compare\compare.py query                :: 读面四族（authors / cards / todos / noncards）
py tools\compare\compare.py dup                  :: 重复副本 + MD5 档案（首跑 / 二跑）
py tools\compare\compare.py refs                 :: 引用补读（补读 / 幂等 / --only-pending）
py tools\compare\compare.py index                :: 索引导入 + idx 查询
py tools\compare\compare.py sort                 :: 按作者整理全链（plan / show / conflict / keep / exec）
py tools\compare\compare.py cardsort             :: 卡片整理链核心（rules / plan / show / exec / moves / rollback / refs / delete）
py tools\compare\compare.py cardedit             :: 卡片最小改动写面（两轮编辑 + 文件哈希 + 留档）
py tools\compare\compare.py u3d                  :: unity3d 列表 / 导出 / 落档（首建 / 命中）
```

## 前置

- `kkgo\kkgo.exe` 已构建（`go build -o kkgo.exe .`，cwd = `kkgo\`）；
- 对照产物 `dist\KMR_B.exe` 在位（0.21.9 调试产物；A 槽 0.21.6 为回退保留）；
- 样本路径是本机现值，写在 `compare.py` 顶部 `SAMPLE_*` / `U3D_SAMPLE` 常量里，换机时改这里。

## 为什么留这个脚本

G2–G8 每落一段都要「与 C# 同输入对拍」验收（design-kkm-go.md §五）。脚本里 CASES 表 + 各专项函数就是对拍面清单——新增命令时往表里加一行或加一个专项函数。
