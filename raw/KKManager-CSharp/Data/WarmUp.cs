using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KKManager.Core;

namespace KKManager.Data
{
    /// <summary>
    /// 分片库预热——把「分片库文件的 WAL 生命周期」代价挪到页面加载后的后台空闲时段。
    ///
    /// 实测（2026-10-03 · 真库 · D 卷 exFAT）：
    /// · 新建分片库的一个写入周期 = 写时创建 -wal（≈ 0.66 s/片）+ 关闭时 checkpoint 并删边车（≈ 1.66 s/片）；
    /// · 老库同一操作 ≈ 0（写 8 ms / 关 3 ms）——不是因为「写热了」，而是因为它的 -wal 一直存在、从未被 checkpoint 与删除
    ///   （实证：`dist/data/lib_1000_0.db` 主文件仍是 208,896 字节的模板副本，2.1 MB 数据全在 `-wal` 里）；
    /// · ⇒ 代价与文件数、并发度无关，只与「-wal 是否常驻」有关。
    ///
    /// 因此预热的正确形态是**让 -wal 常驻**：打开每个分片库、写一次（把 -wal 建出来）并**保持连接打开**
    /// ——关掉就 checkpoint + 删边车，等于没预热。扫描（用自己的连接）打开同一批文件时 -wal 已在 ⇒ 首写便宜；
    /// 扫描结束关自己的连接也不是「最后一个连接」⇒ 不 checkpoint、不删边车，关闭同样变快。
    ///
    /// 纪律：静默（无界面提示，只有服务端日志）· 不占请求串行门（清单在请求线程上算好，后台只开自己的连接）·
    /// 不碰共享连接（不与请求面 _hub 共用 Store）· 扫描进行中不启动（扫描按现有方式跑，预热让路）。
    /// 例外：库文件将被删除时（彻底删除数据 / 原行改地址换库）必须释放对应库的常驻连接——占用中的文件删不掉。
    /// </summary>
    public static class WarmUp
    {
        /// <summary>预热状态——0 未启动 / 已让路（可再触发）· 1 进行中 · 2 已完成。</summary>
        private static int _state;

        /// <summary>常驻预热连接（库序号 → Store）——进程内**不关闭**（关闭即 checkpoint + 删边车，-wal 没了就等于没预热）。</summary>
        private static readonly Dictionary<int, Store> Kept = new Dictionary<int, Store>();

        /// <summary>常驻连接表的读写锁（后台预热线程加，请求线程的释放清）。</summary>
        private static readonly object KeptLock = new object();

        /// <summary>上一次预热用时（毫秒）——观测用。</summary>
        public static long LastMs { get; private set; }

        /// <summary>预热是否已在跑或已跑过（面板据此避免重复触发）。</summary>
        public static bool Started
        {
            get { return Volatile.Read(ref _state) != 0; }
        }

        /// <summary>常驻连接数（观测用）。</summary>
        public static int KeptCount
        {
            get
            {
                lock (KeptLock)
                {
                    return Kept.Count;
                }
            }
        }

        /// <summary>
        /// 要预热的库序号清单——只列**卡片侧分片库**（卡片行写的就是它们；mod 数据在主库，无分片文件）。
        /// 在请求线程上调用（要读分片设置，走共享连接）；后台预热只碰自己的连接。
        /// </summary>
        public static List<int> PlanLibs(StoreHub hub, RootsConfig cfg)
        {
            List<int> list = new List<int>();
            if (hub == null || cfg == null)
            {
                return list;
            }
            int target = hub.ShardTarget();
            foreach (RootEntry e in cfg.cardRoots)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.path) || e.offline)
                {
                    continue;
                }
                int baseLib = hub.CardBaseOf(e);
                if (baseLib <= 0)
                {
                    continue;
                }
                int shards = hub.ShardCountOf(baseLib);
                if (shards <= 0)
                {
                    // 尚未定片数——按分片目标展开（与 EnsureShards 首次定片同源）
                    shards = target;
                }
                if (shards < 1)
                {
                    shards = 1;
                }
                for (int k = 0; k < shards; k = k + 1)
                {
                    list.Add(RootsRules.LibOfShard(baseLib, k));
                }
            }
            return list;
        }

        /// <summary>启动后台预热（同一进程只跑一次；清单为空 / 已在跑 / 已跑过 → 返回 false 不启动）。</summary>
        /// <param name="corePath">主库路径（分片库要 ATTACH 它，库文件与主库同目录）。</param>
        /// <param name="libs">要预热的库序号清单（PlanLibs 所得）。</param>
        /// <param name="busy">扫描是否在跑（每片之间问一次——扫描一开始就整段让路）。</param>
        public static bool Start(string corePath, List<int> libs, Func<bool> busy)
        {
            if (libs == null || libs.Count == 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return false;
            }
            Task.Run(delegate { Run(corePath, libs, busy); });
            return true;
        }

        /// <summary>释放某个库根的常驻预热连接（该库文件将被删除前调用——占用中的文件删不掉；只放该库，不动别的库）。</summary>
        public static void ReleaseLibs(StoreHub hub, string rootPath)
        {
            if (hub == null || string.IsNullOrWhiteSpace(rootPath))
            {
                return;
            }
            int baseLib = hub.LibOf(rootPath);
            if (baseLib <= 0)
            {
                return;
            }
            List<Store> gone = new List<Store>();
            lock (KeptLock)
            {
                foreach (int lib in hub.LibsOfBase(baseLib))
                {
                    Store one = null;
                    if (Kept.TryGetValue(lib, out one))
                    {
                        Kept.Remove(lib);
                        gone.Add(one);
                    }
                }
            }
            foreach (Store one in gone)
            {
                one.Dispose();
            }
            if (gone.Count > 0)
            {
                Console.WriteLine("[预热] 释放 " + rootPath + " 的常驻连接 " + gone.Count + " 个（库文件将被删除）");
            }
        }

        /// <summary>后台预热主循环——逐片把 -wal 建出来并保持连接；任一片失败只出声不中断（失败必须可见）。</summary>
        private static void Run(string corePath, List<int> libs, Func<bool> busy)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(corePath));
            Stopwatch watch = Stopwatch.StartNew();
            int warmed = 0;
            int skipped = 0;
            bool yielded = false;
            foreach (int lib in libs)
            {
                if (busy != null && busy())
                {
                    // 扫描已开始——按现有方式跑，预热整段让路（未预热的片留给下次页面加载）
                    yielded = true;
                    break;
                }
                string path = Path.Combine(dir, RootsRules.LibDbFileName(lib));
                try
                {
                    Store one = WarmOne(path, corePath);
                    lock (KeptLock)
                    {
                        if (Kept.ContainsKey(lib))
                        {
                            one.Dispose();
                        }
                        else
                        {
                            Kept[lib] = one;
                        }
                    }
                    warmed = warmed + 1;
                }
                catch (Exception ex)
                {
                    skipped = skipped + 1;
                    Console.WriteLine("[预热] 跳过 " + Path.GetFileName(path) + "：" + ex.Message);
                }
            }
            LastMs = watch.ElapsedMilliseconds;
            Volatile.Write(ref _state, yielded ? 0 : 2);
            Console.WriteLine("[预热] " + (yielded ? "让路（扫描已开始）" : "完成") + "：暖 " + warmed
                + " 片 · 跳过 " + skipped + " 片 · 用时 " + watch.ElapsedMilliseconds + " ms");
        }

        /// <summary>预热一片分片库——缺文件先从内嵌模板释放（与扫描同源），写一次把 -wal 建出来；返回的连接由调用方常驻。</summary>
        private static Store WarmOne(string path, string corePath)
        {
            bool ready = true;
            if (!File.Exists(path))
            {
                ready = Store.ReleaseTemplate(path);
            }
            Store one = new Store(path, corePath, ready);
            try
            {
                one.WarmShard();
            }
            catch
            {
                one.Dispose();
                throw;
            }
            return one;
        }
    }
}
