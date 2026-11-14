# kkgo 对拍工具

G 段验收工具：同一样本、同一参数下跑 `kkgo.exe` 与 `KMR_A.exe`（C# 0.21.9 冻结参照），比对输出。

## 判据

| 项 | 口径 |
|:--|:--|
| 退出码 | 必须相等 |
| 行数 | 必须相等 |
| 逐行内容 | 剔除非 ASCII 后逐字相同（C# 控制台中文是 GBK 输出被按 UTF-8 读，属编码伪差异——两侧各按真实编码解码） |
| thumb | 只比尺寸 / 卡类型 / 图片区；字节数差异属编码器不同（ImageSharp vs stdlib），不作判据 |
| 诊断文本 | 异常 message 措辞差异只比对分类，不比对原文（本工具涉及的读面命令目前无此差异） |
| card-analysis | C# 命令面**没有**这条命令——对拍落库产物：临时库单卡扫描后比 `card_analysis.data` 的 JSON（键集 / 键序 / 值 / 类型四层） |
| roots / scan-plan | **交互相**：C# 从模板建库 → 同一串写操作分别由 C# 与 Go 写到两份库 → 每份库再由两侧各读一遍（四向读回）；首行「库: <路径>」不比（两份库文件名本就不同） |
| libmap（分片库读面） | C# 命令面**没有** `modfiles`——真相源侧用 Python 按 C# 规则（`libdb:` / `libshards:` 设置键 + roots JSON → `lib_<基>_<分片>.db`）直读库算副本清单，与 `kkgo modfiles <guid>` 逐行比；样本 = 主库前 2 个 guid + 只在 kkc 库的 3 个 guid |
| composition | **真库复制两份**（主库文件）→ 各自 C# / Go 跑一次（读容器 + 建档）→ 输出行比对（「建档于」时间戳归一为 `<TS>`）+ 落库 10 字段比对（analyzed_at 按设计不同，不比）；临时库的 roots 裁剪为只留走主库的 mod 库根（避免 C# 预建分片库文件；样本 guid 副本都在主库） |
| plugin-log | **日志被运行中的游戏独占**（C# 侧 `FileShare.Read` 打不开真日志）——故走「临时库 + 日志副本」：真库快照各复制一份（C# / Go 各一）→ 两份库的 `pluginRoots` 都指向同一个临时 BepInEx 目录 → 两侧同日志跑 → 输出逐行比；第二例「日志缺失」两侧都应 `解析失败` + exit 1 |
| plugin-match | 真库直比（只读主库 `plugin_file`）——键样本取自场景卡插件条目（vnge_sss / kkpe / timeline）+ 一个未装键 |
| stats | **两段**：① 裁剪临时库（roots 只留预置条目 + 复制 7000 系列 32 片）→ 两侧 AllStores 组成一致 → 43 行逐行必须全同（四色聚合 / 缺失排行 / 未被引用 / 重复副本都真实跑）；② 真库全量——`KMR_A.exe` 是 0.21.6 产物，mod 侧库路由在 0.21.8 修过（0.21.6 的 `AllStores` 不含 mod 侧非预置库：kkc 的 `lib_5_0.db` 423 行不算；0.21.9 源码算）→「mod 文件副本」一行是**已知代际差异**，其余行必须全同 |
| scan-mods | **三根样本**（main 锁定 tier1 走主库 / cache tier2 走主库 / cold tier3 走库文件；真实 zipmod ×7 含一份同 guid 跨根副本与一份子目录 + 非 mod 文本 + 坏 zip + 无 manifest 容器）跑三轮：首扫 / 增量复扫 / 删文件后复扫。判据 = stdout 逐行（库路径 / 耗时 / 预建行归一）+ `mod` / `mod_file` / `non_card` / `scan_state` 逐行（忽略 scan_time / done_at / id） |
| scan-cards | 卡片样本（人物 ×2 / 服装 ×1 / 场景 ×1 + 纯图片 + 截断 + 非 PNG）跑两轮（首扫 / 增量复扫）；`scan_shards=4` 两侧同值、`KKM_SCAN_WORKERS=1`。判据 = stdout 逐行 + `card` / `card_mod` / `card_timeline` / `non_card` / `scan_state` 逐行（thumb 只比「有无」，不比字节——编码器不同） |
| scan-main / scan-extra | 五根合一（mod 三根 + 卡片两根）+ 插件根**故意指向不存在目录**（两侧只报「插件库根不存在」）；按两个范围各跑一轮。判据 = stdout 逐行 + `mod` / `mod_file` / `card` / `card_mod` / `card_timeline` / `non_card` / `scan_state` 逐行 |
| scan-plugins | 临时 BepInEx（插件 dll ×2 含 `sub/` 同 guid 副本 + 非插件 dll ×1 + 四个 cfg 覆盖转义面 / 控制字符 / BOM / 空 + 子目录 cfg 不入库）跑三轮：首扫 / 增量复扫 / 删 dll + 改 cfg 后复扫。判据 = stdout/stderr 逐行 + `plugin_file` / `plugin_config` 逐行（`sections` 逐字节——含 System.Text.Json 的 `\uXXXX` 大写转义、`\u0022` 引号、7 位小数 mtime） |
| scan-cards 分析组 | 同 `scan-cards` 样本，但开「卡片分析」组（`coord,detail,scene`）——落 `card_analysis` 三步。判据 = stdout 逐行 + `card` / `card_mod` / `card_timeline` / `card_analysis`（`data` 逐字节）/ `non_card` / `scan_state` 逐行；两轮（首扫 / 增量复扫——分析步有完成戳，第二轮应跳过） |
| scan-main 插件段 | 同 `scan-main` 样本，但插件根**存在**（真 BepInEx）——验证 `withPlugins` 编排（步数 +1 / 进度行 / 计数行）。判据 = stdout/stderr 逐行 + 上述全表 + `plugin_file` / `plugin_config` 逐行；两轮（首扫 / 增量复扫） |

**已知差异（不作判据）**

- **并行 worker 段**：C# 卡片侧按 `scan_workers` 多线程跑分片（`并行扫描：卡片侧 worker 数 N`）；Go 侧当前**串行逐片**（布局与并行一致，只是同时只跑一片）。对拍固定 `KKM_SCAN_WORKERS=1` 口径。
- **缩略图字节**：C# ImageSharp / Go stdlib，只比「有无」与尺寸。
- **mod 文件副本计数（stats 真库一段）**：`KMR_A.exe` 0.21.6 的 `AllStores` 不含 mod 侧非预置库（kkc `lib_5_0.db` 423 行）；Go 照 0.21.9。已在 `stats` 专项输出里标为已知代际差异。

### card-analysis 的对拍管线（产物对拍，非命令对拍）

```
样本卡 → 复制到临时目录 → C# roots-add cards 1 <tmp> 仅本目录 --db <tmp>\t.db
       → C# scan-plan set <全集序> coord,detail,scene --db t.db   （分析组可选，全开才生效）
       → C# scan-cards --db t.db                                  （1 张卡，约 1 s）
       → 读分片库 lib_<rootId>_<shard>.db 的 card_analysis(kind,data)
       → 与 kkgo card-analysis <样本> 的「kind<TAB>JSON」逐分项比
```

分项集合也必须相等（人物卡 = coord+detail；场景卡 = detail+scene）——Go 侧的适用性判据（`isCharaCard` / `sd`）与 C# `CardAnalysis.Run` 同一套。

## 用法

```bat
py tools\compare\compare.py all                  :: 批量跑 CASES 全表
py tools\compare\compare.py card-detail <card>   :: 单命令
py tools\compare\compare.py thumb                :: 缩略图专项
py tools\compare\compare.py timeline             :: 轨道专项（自动挑关键帧最多的场景卡）
py tools\compare\compare.py card-analysis [card] :: 分项分析落库 JSON 对拍（缺省用 SAMPLE_CARD）
py tools\compare\compare.py roots                :: 库根 / 扫描计划交互相（四向读回）
py tools\compare\compare.py libmap               :: 分片库读面对拍（Python 直读库作真相源 vs Go modfiles）
py tools\compare\compare.py composition          :: 组成档案对拍（真库复制两份 → 输出行 + 落库字段）
py tools\compare\compare.py pluginlog            :: BepInEx 日志对拍（临时库 + 日志副本；含日志缺失分支）
py tools\compare\compare.py stats                :: 库统计对拍（裁剪临时库逐行 + 真库已知代际差异）
py tools\compare\compare.py scanmods             :: mod 扫描对拍（三根样本 · 首扫 / 复扫 / 删文件后复扫）
py tools\compare\compare.py scancards            :: 卡片扫描对拍（卡片样本 · 首扫 / 复扫 · 分片库逐片比）
py tools\compare\compare.py scanall              :: 统一扫描对拍（scan-main / scan-extra 两轮 · 插件根缺失口径）
py tools\compare\compare.py scanplugins          :: 插件库对拍（临时 BepInEx · 三轮 · 两表逐行含 sections 逐字节）
py tools\compare\compare.py scancardsanalysis    :: 卡片分析组对拍（coord / detail / scene 落 card_analysis · 两轮）
py tools\compare\compare.py scanallplugins       :: scan-main 插件段对拍（插件根存在 · 两轮）
```

## 前置

- `kkgo\kkgo.exe` 已构建（`go build -o kkgo.exe .`，cwd = `kkgo\`）；
- 对照产物 `dist\KMR_A.exe` 在位（C# 冻结参照，不进 Git）；
- 样本路径是本机现值，写在 `compare.py` 顶部 `SAMPLE_*` 常量里，换机时改这里。

## 为什么留这个脚本

G2–G7 每落一段都要「与 C# 同输入对拍」验收（design-kkm-go.md §五）。脚本里 CASES 表就是当前已达成的对拍面清单——新增命令时往表里加一行即可。
