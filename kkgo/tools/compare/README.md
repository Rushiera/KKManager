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

## 用法

```bat
py tools\compare\compare.py all                  :: 批量跑 CASES 全表
py tools\compare\compare.py card-detail <card>   :: 单命令
py tools\compare\compare.py thumb                :: 缩略图专项
py tools\compare\compare.py timeline             :: 轨道专项（自动挑关键帧最多的场景卡）
```

## 前置

- `kkgo\kkgo.exe` 已构建（`go build -o kkgo.exe .`，cwd = `kkgo\`）；
- 对照产物 `dist\KMR_A.exe` 在位（C# 冻结参照，不进 Git）；
- 样本路径是本机现值，写在 `compare.py` 顶部 `SAMPLE_*` 常量里，换机时改这里。

## 为什么留这个脚本

G2–G7 每落一段都要「与 C# 同输入对拍」验收（design-kkm-go.md §五）。脚本里 CASES 表就是当前已达成的对拍面清单——新增命令时往表里加一行即可。
