# KKManager 重活跑测手册（perf-runbook）

> 建立：2026-11-11（P 流 P0 交付）· 基线：KMR **0.21.8**（git `2b8e0fb`）· 本机 16 逻辑核 · 数据根 D: 卷
> 定位：KKM 重活（索引导入 / 全量扫描 / 分片重建 / 插件扫描）的**跑测与观测口径**——用哪件、怎么跑、看什么、哪些数字是假的。
> 边界：发布流程 → `revealer/SKILL.md`；设计 → `ccbp:Project/KKManager/design-*.md`；本文件只管「怎么量」。
> 现场数字来源：`perf/import-bench.csv`（墙钟 + CPU 拆分）· `perf/import-bench-cpu.csv`（历史批次）· `WorkSpace:tools/CatTemp/perf_*.csv`（系统侧 typeperf）· `WorkSpace:tools/ThrottleStop_9.7/logs/bench_20261111.csv`（固定基准）。

---

## 〇、三分钟上手

| 我要做 | 命令（cwd / 参数照抄） |
|:--|:--|
| 建跑测件（Release） | cwd=`D:\Mau\WorkSpace\KKManager\KKManager`：`dotnet restore -r win-x64` → `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o D:\Mau\WorkSpace\tools\CatTemp\kmrperf`（**不带** `-p:ReleasePack=true`） |
| 跑一次索引导入 | `D:\Mau\WorkSpace\tools\CatTemp\kmrperf\KMR.exe index-import --root D:\Mau\WorkSpace\KKManager\dist\data\kkc --db D:\Mau\WorkSpace\KKManager\dist\data\kkmanager.db` |
| 量墙钟 + CPU 拆分 | `powershell -NoProfile -ExecutionPolicy Bypass -File D:\Mau\WorkSpace\KKManager\perf\import-bench.ps1 -Runs 3 -Tag <标签>` |
| 冷扫描对照（4 vs 32 workers） | `powershell -NoProfile -ExecutionPolicy Bypass -File D:\Mau\WorkSpace\KKManager\perf\scan-bench.ps1 -Runs 3 -CardDir <卡目录> -WorkRoot <临时库根>`（每轮重建空库；跑完把两个临时目录整棵软删除） |
| 量系统侧 | 重活期间同开：`typeperf "<7 计数器>" -sc 32 -f CSV -o D:\Mau\WorkSpace\tools\CatTemp\perf_<标签>.csv` |
| 量机器本身 | `ts_bench.ps1 -Samples 3`（重活前后各一次）· `ts_jobwatch.ps1 -Seconds <重活时长+10>`（归因到进程） |

铁律：**判据取同口径对照**——同一 `--db`、同一 `--root`、同一观测组合。跨口径的两个数不可比（见 §五-1）。

---

## 一、构建跑测件（Release，不带 ReleasePack）

### 1.1 两条已踩的坑

| 坑 | 现象 | 处置 |
|:--|:--|:--|
| `--db` 位置 | `KMR.exe --db <路径> index-import ...` → `未知命令: --db` + 打印用法 + 退出码 2 | `--db` 是**子命令之后**的全局参数：`KMR.exe index-import --root ... --db ...`（`Program.Main` 先取 `rest[0]` 当命令名） |
| 资产文件丢 RID | 先跑过 `cs-build` / `dotnet build`（无 RID），再 `dotnet publish --no-restore` → `NETSDK1047: 资产文件没有 net8.0-windows/win-x64 的目标` | 补一次 `dotnet restore -r win-x64`，或 publish 不带 `--no-restore`（会自动还原） |

### 1.2 产物判据

- `KMR.exe` 的 `FileVersion` = `0.21.8.0`，`ProductVersion` 带 git 短哈希（`0.21.8+2b8e0fb…`）；单文件约 **193 MB**。
- `KMR.exe help` 输出含 `index-import` 行（控制台是 GBK，命令行工具里会显示乱码，**按行尾数字/命令名判读**，不要因为乱码判失败）。
- 单文件自解压开销可忽略：`help` 实测 Release 680 ms / Debug 579 ms（热缓存）。

---

## 二、重活纪律（四条）

1. **`KKM_SCAN_WORKERS=4`** — 本机 16 逻辑核，默认 `DefaultScanWorkers=32` 是超配。`design-KKManager_log.md` §109.3 实测：32 片 × 32 线程 71.8 s vs 32 片 × 1 线程 66.6 s——冷启动并行不赚。重活默认压到 4，除非要复现并行实验。
2. **重活串行** — 同时只跑一个重活（扫描 / 导入 / 分片重建）。两个重活并行时 IO 队列互相污染，墙钟与计数器都失去对照意义。
3. **只用 Release 件出数字** — Debug 件留给开发自测。`index-import` 上两者实测差异落在噪声内（P1 后 Debug 2.4–2.9 s vs Release 2.2–2.5 s），但扫描类 CPU 密集作业仍按 Release 口径，避免把 JIT / 调试符号成本混进对外数字。
4. **跑前跑后各一次 `ts_bench`，重活期间挂 `ts_jobwatch` + typeperf 7 计数器** — 固定基准证明「机器本身没漂移」，jobwatch 把 CPU / IO 归因到进程，typeperf 给系统侧上下文。三件缺一，结论就只能到「这次好像快/慢」。

---

## 三、观测三件套

### 3.1 `ts_bench.ps1`（机器基线）

```
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Mau\WorkSpace\tools\ThrottleStop_9.7\ts_bench.ps1 -Samples 3 -IntervalSec 5
```

- A = 4 KB 写读 ×100（文件系统面）；B = 纯用户态 500 万次（CPU 面）。
- 判读：A 慢 B 正常 → 文件系统 / 内核路径；A、B 都慢 → 调度或内存；都正常 → 体感延迟在别处。
- 本机健康带（2026-11-11）：A 23–48 ms · B 9.2–11.6 s。

### 3.2 `ts_jobwatch.ps1`（进程归因）

```
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Mau\WorkSpace\tools\ThrottleStop_9.7\ts_jobwatch.ps1 -Seconds 40 -ScanSec 2 -CpuPct 10 -LogDir D:\Mau\WorkSpace\tools\CatTemp\jobs_<标签>
```

- 每作业一行：墙钟 / CPU 总量与 user:kernel 拆分 / 峰值线程与句柄 / IO 读写 / 系统非分页池与句柄增量。
- **限制（实测）**：发现扫描默认 5 s 一轮，且新进程要两个扫描周期才建立 CPU 基线 → **抓不到 <5 s 的作业**。P1 后的 `index-import` 只有 2.5 s，jobwatch 永远看不见它（要抓就 `-ScanSec 1 -CpuPct 1`，且作业本身 ≥5 s）。
- 短作业的 CPU 拆分改用 §3.4 的 bench 脚本（`Process.TotalProcessorTime` 在进程退出后仍可读）。

### 3.3 typeperf 7 计数器（系统侧上下文）

```
typeperf "\Memory\Pool Nonpaged Bytes" "\Process(_Total)\Handle Count" "\System\System Calls/sec" "\Memory\Cache Bytes" "\System\Processor Queue Length" "\System\Context Switches/sec" "\PhysicalDisk(_Total)\Avg. Disk Queue Length" -sc 32 -f CSV -o D:\Mau\WorkSpace\tools\CatTemp\perf_<标签>.csv
```

- 目标文件已存在时 typeperf 会**交互式问覆盖**（`overwrite? [y/n]`），在无人值守通道里会挂住直到超时——先删旧文件或换文件名。
- 判读口径见 §五-2：系统级 `System Calls/sec` **不可归因**，只当背景噪声。

### 3.4 `perf/import-bench.ps1`（墙钟 + 进程级 CPU 拆分）

```
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Mau\WorkSpace\KKManager\perf\import-bench.ps1 -Runs 3 -Tag <标签> [-ExeA <基准件> -LabelA <名>] [-ExeB <候选件> -LabelB <名>]
```

- 交替跑 A / B，同一 db、同一 index root，每行：墙钟 / CPU 总量 / user / kernel / kernel 占比 / CPU 占墙钟比 / 退出码。
- 为什么不用 `time`：**kernel 秒数是「syscall + journal 写」开销的直接证据**，墙钟分不出「算法更快」和「内核流量更少」。
- 默认 A/B = `kmrperf-p0`（改造前 Release）/ `kmrperf`（当前 Release）。换版本时把旧件留一份，否则没有对照。

---

## 四、基准数字（2026-11-11 实测）

### 4.1 `index-import` 墙钟与 CPU（同库同根，solo，无人观察）

| 件 | 样本数 | 墙钟中位 | 墙钟区间 | CPU 总量 | kernel 秒 | kernel 占比 |
|:--|:--:|--:|:--|--:|--:|--:|
| Release 0.21.8 改造前（`kmrperf-p0`） | 11 | **12,970 ms** | 10.1–17.0 s | 0.67–1.67 s | 0.33–1.05 s | 49–66% |
| Release 0.21.8 + 事务批提交（`kmrperf`） | 11 | **2,475 ms** | 2.2–5.5 s | 0.36–0.88 s | 0.03–0.08 s | 4–14% |
| Debug 0.21.7 改造前（单样本） | 1 | 12,520 ms | — | — | — | — |

- 计数判据（每轮都一样）：行 5,844 · 卡 5,125 · mod 副本 343 · 其它 376 · 引用 96,739 · 摘要 `c50b11a22f7c0d49`。
- 首跑 5,504 ms 属冷缓存 + 单文件解包，取稳定态（第 2 跑起）才算。
- 结论：**墙钟 ÷5.2；kernel 秒 ÷14**——省下来的时间在「每行一次独立事务」的 syscall / journal 路径上，不在算法。

### 4.2 同口径的历史「18 s」是怎么来的

| 口径 | 数字 |
|:--|:--|
| 2026-11-11 13:14 一次 import（typeperf 同开） | 18.0 s |
| 2026-11-11 13:35 同口径复现（Release，typeperf 同开） | 18,174 ms |
| 同一件单独跑 | 12.5–13.0 s |

→ **观测器（1 s typeperf 采样 + jobwatch 扫描）会把 13 s 抬到 18 s**。「18 s」不是代码耗时，跨口径比较会得出相反结论。

### 4.3 固定基准（`ts_bench`）

| 时刻 | A（4 KB ×100） | B（500 万次） |
|:--|--:|--:|
| 12:41 空闲初测 | 23–43 ms | 9.2–11.6 s |
| 13:12 改造前 | 48.3 / 27.1 / 37.8 ms | 10,680 / 9,781 / 9,712 ms |
| 13:56 改造后 | 35.9 ms（末次） | 10,543.6 ms（末次） |

机器本身没有漂移；改造前后差异落在噪声内。

---

## 五、口径与假信号

1. **跨口径不可比** — 同一个 `index-import`：solo 13 s、typeperf 同开 18 s、typeperf + jobwatch 同开 18.2 s。任何「快了 / 慢了」的结论必须先对齐观测组合。
2. **系统级 `System Calls/sec` 不可归因** — 改造前（18 s 窗口）峰值 705,619；改造后 3 s 窗口两轮分别 360,531 / 825,554。背景进程（eCloud / chrome / QQ）本身就在 25–45 万区间，1 s 采样也抓不到 2.5 s 作业的真实峰值。**判据用进程级 kernel 秒（§4.1），系统级只作旁证。**
3. **jobwatch 抓不到短作业** — 见 §3.2。P1 后 import 2.5 s，用 jobwatch 会得到「没跑过」的假象。
4. **`stats` ≠ `index-import` 计数** — `stats` 是全库聚合（含其它库根 / 历史行），import 是「这一次索引的行数」。核对导入完整性看导入回执那五行，不看 `stats`。
5. **首跑偏慢** — 冷缓存 / 单文件解包 / WAL 初次校验点。基准取第 2 跑起。
6. **`--db` 指真库是幂等的** — upsert + 孤儿清理，重复跑不改数据；但 `--purge-first` 会先删该根记录（破坏性，别在真库上随手加）。

---

## 六、环境侧操作清单（P3，交 Rushiera 执行）

> 我只出清单，不动系统安全配置。三项都在「重活期间」验收。

| # | 项 | 操作 | 验收判据 |
|:--:|:--|:--|:--|
| 1 | **电脑管家**（本机唯一活跃 AV；Defender 未运行） | 把三个目录加进信任区 / 白名单：`D:\Mau\WorkSpace\KKManager\dist\data`（含 `kkc` 整理根）、`D:\Mau\WorkSpace\tools\FileClean`、`D:\Mau\WorkSpace\tools\CatTemp`（跑测件与日志） | 重活期间 `ts_jobwatch` 里 `QQPCTray` / 管家进程的 IO 读不再随重活上涨；同基准墙钟降幅 ≥10% |
| 2 | **WSearch** | 确认 Windows Search 是否索引 `D:\Mau`；若是，从索引范围移除该盘（控制面板 → 索引选项 → 修改） | `Get-Service WSearch` 状态不变，但索引项不含 `D:\Mau`；重活前后 `Memory\Cache Bytes` 峰值不再被索引器抬高 |
| 3 | **eCloud** | 缩小同步范围（不同步 `D:\Mau`），或重活时退出 | `ts_jobwatch` 的 `eCloud` 行 IO 读/写归零；同基准 `System Calls/sec` 均值下降（8-11 轮实测 eCloud 单轮 23 s 可吃 22.3 s CPU / 167.9 MB 读） |

---

## 七、未决（本轮没做，别当成已做）

| 项 | 为什么没做 | 下一步 |
|:--|:--|:--|
| 冷扫描 `KKM_SCAN_WORKERS=4` vs `32` 对照 | **已做（113 张样本 · 拍板 B）**：`perf/scan-bench.ps1` + 每轮重建临时空库——4 workers 1,130 ms vs 32 workers 1,035 ms（+9.2%，≤+15% 内），CPU 0.48 vs 1.31 s；详表 → `ccbp:Project/KKManager/design-KKManager_log.md` §110.6 | 大库吞吐仍未测——要测就 `scan-main` 分段（约 10 min） |
| P2 插件扫描并行上限 | 需要一次插件库扫描做对照，属写库操作 | 与上一条同批做，或在有插件库快照时单独做 |
| `--purge-first`（首次建库 / INSERT 路径） | 对真库是破坏性动作 | 在库副本上做，别在 `dist/data` 上做 |
| 挂载盘 / 远程盘上的扫描 | 不属本轮 | 见 `ccbp:Project/FileClean/` 的盘型判据 |
