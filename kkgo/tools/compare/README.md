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
```

## 前置

- `kkgo\kkgo.exe` 已构建（`go build -o kkgo.exe .`，cwd = `kkgo\`）；
- 对照产物 `dist\KMR_A.exe` 在位（C# 冻结参照，不进 Git）；
- 样本路径是本机现值，写在 `compare.py` 顶部 `SAMPLE_*` 常量里，换机时改这里。

## 为什么留这个脚本

G2–G7 每落一段都要「与 C# 同输入对拍」验收（design-kkm-go.md §五）。脚本里 CASES 表就是当前已达成的对拍面清单——新增命令时往表里加一行即可。
