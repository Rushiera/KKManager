using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KKManager.Core;
using KKManager.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KKManager.Web
{
    /// <summary>扫描任务状态——同一时刻只允许一个扫描任务。</summary>
    public class ScanState
    {
        /// <summary>是否正在运行。</summary>
        public bool Running { get; set; }

        /// <summary>扫描目标（mods / cards）。</summary>
        public string Target { get; set; }

        /// <summary>当前进度描述。</summary>
        public string Message { get; set; }

        /// <summary>当前处理的库根。</summary>
        public string CurrentRoot { get; set; }

        /// <summary>当前级别。</summary>
        public int CurrentTier { get; set; }

        /// <summary>库根序号 / 总数。</summary>
        public int RootIndex { get; set; }

        /// <summary>库根总数。</summary>
        public int RootCount { get; set; }

        /// <summary>已枚举文件数。</summary>
        public int Seen { get; set; }

        /// <summary>已入库数。</summary>
        public int Added { get; set; }

        /// <summary>跳过数。</summary>
        public int Skipped { get; set; }

        /// <summary>非卡文件数。</summary>
        public int NonCard { get; set; }
        /// <summary>非 mod 文件数（mod 库内读不到 manifest.xml 的文件）。</summary>
        public int NonMod { get; set; }

        /// <summary>失败数。</summary>
        public int Failed { get; set; }

        /// <summary>引用条目数。</summary>
        public long Refs { get; set; }

        /// <summary>缩略图字节数。</summary>
        public long ThumbBytes { get; set; }

        /// <summary>本次扫描新读到角色名的卡片数。</summary>
        public int NamesRead { get; set; }

        /// <summary>本次扫描为存量卡片补读到角色名的数。</summary>
        public int NamesFilled { get; set; }

        /// <summary>本次扫描为存量卡片补正卡类型的数。</summary>
        public int TypesFixed { get; set; }

        /// <summary>本次扫描为场景卡读到 timeline 长度的数。</summary>
        public int TimelineRead { get; set; }

        /// <summary>插件库段已完成的 dll 数（主要库扫描连带段——面板进度用）。</summary>
        public int PluginDone { get; set; }

        /// <summary>插件库段的 dll 总数。</summary>
        public int PluginTotal { get; set; }

        /// <summary>本次扫描枚举到的插件 dll 数（主要库扫描连带段）。</summary>
        public int PluginDlls { get; set; }

        /// <summary>本次扫描落库的插件项数（有 guid 的插件行）。</summary>
        public int Plugins { get; set; }

        /// <summary>本次扫描读到的插件配置文件（cfg）数。</summary>
        public int PluginConfigs { get; set; }

        /// <summary>本次扫描清理的已消失记录数。</summary>
        public int Removed { get; set; }

        /// <summary>当前步骤序号（1 起）。</summary>
        public int StepIndex { get; set; }

        /// <summary>步骤总数。</summary>
        public int StepCount { get; set; }

        /// <summary>当前步骤名（段名）。</summary>
        public string StepName { get; set; }

        /// <summary>当前步骤内已处理文件数。</summary>
        public int StepDone { get; set; }

        /// <summary>当前步骤内文件总数。</summary>
        public int StepTotal { get; set; }

        /// <summary>开始时刻。</summary>
        public string StartedAt { get; set; }

        /// <summary>结束时刻。</summary>
        public string FinishedAt { get; set; }

        /// <summary>错误摘要（失败必须可见）。</summary>
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>「未引用 mod 移到缓存库」任务状态——后台执行，同一时刻只允许一个；页面关掉也继续跑，重开可见进度与结果。</summary>
    public class MoveJobState
    {
        /// <summary>是否正在运行。</summary>
        public bool Running { get; set; }

        /// <summary>当前阶段描述。</summary>
        public string Message { get; set; }

        /// <summary>待搬总数。</summary>
        public int Total { get; set; }

        /// <summary>已处理数。</summary>
        public int Done { get; set; }

        /// <summary>成功搬出数。</summary>
        public int Moved { get; set; }

        /// <summary>跳过数（有原因）。</summary>
        public int Skipped { get; set; }

        /// <summary>失败数。</summary>
        public int Failed { get; set; }

        /// <summary>正在处理的 guid。</summary>
        public string Current { get; set; }

        /// <summary>开始时刻。</summary>
        public string StartedAt { get; set; }

        /// <summary>结束时刻。</summary>
        public string FinishedAt { get; set; }

        /// <summary>是否收到停止请求。</summary>
        public bool StopRequested { get; set; }

        /// <summary>失败明细（上限 50 条——失败必须可见）。</summary>
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>本地 Web 面板——库根设置 / 扫描 / 统计 / 卡片浏览 / 反查与搬运。</summary>
    public static class WebApp
    {
        /// <summary>多库协调器——主库 + 各库文件；面板数据面统一走它。</summary>
        private static StoreHub _hub;
        private static string _dbPath;
        private static string _indexHtml;
        /// <summary>请求串行门——共享 Store 持有一条 SQLite 连接（SqliteConnection 非线程安全），同一时刻只放行一个请求使用它。</summary>
        private static readonly System.Threading.SemaphoreSlim RequestGate = new System.Threading.SemaphoreSlim(1, 1);
        /// <summary>文件夹选择框是否已弹出（0=空闲 1=占用）——防止一次点出多个看不见的模态框。</summary>
        private static int BrowseActive;
        /// <summary>浏览框调试日志开关——默认关闭（日志代码全部保留，命令行 `serve --browse-debug` 打开）。</summary>
        private static bool BrowseDebug;
        /// <summary>浏览框宿主窗句柄（枚举本线程顶层窗口时排除它）。</summary>
        private static IntPtr BrowseHostHandle;
        /// <summary>选择框句柄（找到后记录——超时主动关闭用；IntPtr.Zero = 未找到）。</summary>
        private static IntPtr BrowseDlgHandle;
        /// <summary>浏览框所在线程 ID（EnumThreadWindows 只枚举该线程创建的顶层窗口）。</summary>
        private static uint BrowseThreadId;
        /// <summary>置顶定时器——Tick 由选择框的模态消息循环泵动，不额外起线程。</summary>
        private static System.Windows.Forms.Timer BrowseTopTimer;
        /// <summary>置顶定时器已 Tick 次数（诊断——Tick 在推进即说明消息循环活着）。</summary>
        private static int BrowseTopTicks;
        /// <summary>置顶是否曾成功过（诊断用——不再据此停表：框可能先创建后显示，显示时会重置 z-order）。</summary>
        private static bool BrowseTopDone;
        /// <summary>上次记录的选择框可见状态（诊断——只在状态变化时出声）。</summary>
        private static bool BrowseLastVisible;
        /// <summary>本轮枚举到的对话框类窗口（优先置顶对象）。</summary>
        private static List<IntPtr> BrowseDlgFound;
        /// <summary>本轮枚举到的其它可见顶层窗口（没找到对话框类时的备选）。</summary>
        private static List<IntPtr> BrowseOtherFound;
        /// <summary>EnumThreadWindows 回调委托——存字段防 GC 回收。</summary>
        private static readonly EnumThreadProc BrowseEnumProcRef = BrowseEnumProc;
        private static ScanState _scan = new ScanState();
        private static readonly object ScanLock = new object();
        /// <summary>「未引用 mod 移到缓存库」后台任务状态（含最近一次结果）。</summary>
        private static MoveJobState _moveJob = new MoveJobState();
        /// <summary>搬运任务状态的读写锁。</summary>
        private static readonly object MoveJobLock = new object();
        /// <summary>「按作者整理」任务状态（生成计划 / 执行共用）。</summary>
        private static SortJobState _sortJob = new SortJobState();
        /// <summary>按作者整理任务状态的读写锁。</summary>
        private static readonly object SortJobLock = new object();
        /// <summary>缺失清单缓存（首次请求构建，扫描结束后失效——页签每次打开不必重算）。</summary>
        private static List<object> _missingCache;
        /// <summary>缺失清单缓存对应的 per 值（请求参数变了就重建）。</summary>
        private static int _missingCachePer;
        /// <summary>unity3d 贴图 PNG 缓存（键 = guid|条目路径|pathID|缩略标记）——一次解码多次取；超上限整体清空（简单替代 LRU，避免无界增长）。</summary>
        private static readonly Dictionary<string, byte[]> _u3dPngCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        /// <summary>unity3d 贴图 PNG 缓存条目上限。</summary>
        private const int U3dPngCacheLimit = 128;
        /// <summary>unity3d 条目读取上限（字节）——超限拒绝并出声，不无声截断。</summary>
        private const long U3dEntryMaxBytes = 536870912;

        /// <summary>单页返回上限——前端传 size ≤ 0 表示不限条数。</summary>
        private const int NoLimit = 100000;

        /// <summary>启动本地服务（阻塞）——browseDebug 打开浏览框调试日志（默认关）。</summary>
        public static int Run(string dbPath, int port, bool openBrowser, bool browseDebug)
        {
            _dbPath = dbPath;
            BrowseDebug = browseDebug;
            _hub = new StoreHub(dbPath);
            RootsConfig bootConfig = LoadConfig();
            _hub.EnsureMigrated(bootConfig);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:" + port);

            var app = builder.Build();

            // 串行门 + 失败可见——共享 Store 的连接不并发使用（SqliteConnection 非线程安全）；异常写回响应并落控制台（不再静默 500 空体）
            // 🔴 交互式端点（弹文件夹对话框、等使用者操作）不占门——它一轮可能挂几十秒，占门等于整站停工
            app.Use(async (context, next) =>
            {
                string path = context.Request.Path.Value ?? "";
                if (string.Equals(path, "/api/browse-folder", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }
                await RequestGate.WaitAsync();
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[错误] " + context.Request.Path + " → " + ex.GetType().Name + ": " + ex.Message);
                    if (!context.Response.HasStarted)
                    {
                        context.Response.Clear();
                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}");
                    }
                }
                finally
                {
                    RequestGate.Release();
                }
            });

            MapRoutes(app);

            Console.WriteLine("KK Manager by Rushiera 面板: http://127.0.0.1:" + port);
            Console.WriteLine("数据库: " + _hub.Core.DbPath);

            if (openBrowser)
            {
                string url = "http://127.0.0.1:" + port;
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("打开浏览器失败：" + ex.Message);
                }
            }

            app.Run();
            return 0;
        }
        /// <summary>把异常文本压成可安全嵌进 JSON 的单行（换行折空格、双引号折单引号，超长截断）。</summary>
        private static string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            // 反斜杠双写（JSON 合法，路径原样保留）；双引号折单引号；换行折空格；超长截断
            string s = text.Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            if (s.Length > 300)
            {
                s = s.Substring(0, 300);
            }
            return s;
        }
        /// <summary>读旧版操作请求体（guid + 路径）——缺项或格式错返回 null。</summary>
        private static async Task<ModOldRequestDto> ReadOldRequest(HttpContext context)
        {
            ModOldRequestDto dto = null;
            try
            {
                dto = await JsonSerializer.DeserializeAsync<ModOldRequestDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return null;
            }
            if (dto == null || string.IsNullOrWhiteSpace(dto.guid) || string.IsNullOrWhiteSpace(dto.path))
            {
                return null;
            }
            return dto;
        }

        /// <summary>写回旧版操作结果——成功带 detail，失败 409 带原因（失败必须可见）。</summary>
        private static async Task WriteOldResult(HttpContext context, string error, string detail)
        {
            if (error != null)
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                return;
            }
            string text = "";
            if (detail != null)
            {
                text = detail;
            }
            await context.Response.WriteAsync("{\"ok\":true,\"detail\":\"" + EscapeJson(text) + "\"}");
        }

        /// <summary>把窗口带到前台——弹框不抢焦点时把可见窗口提到最前。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        /// <summary>调整窗口 z-order / 位置 / 尺寸——HWND_TOPMOST 让选择框压过全屏浏览器（不依赖抢前台）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        /// <summary>枚举指定线程创建的顶层窗口。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadProc lpfn, IntPtr lParam);
        /// <summary>取窗口类名（Win32 对话框为 #32770）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        /// <summary>取窗口标题（诊断用）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        /// <summary>窗口是否可见（诊断用）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        /// <summary>投递窗口消息——超时主动关闭选择框（WM_CLOSE）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        /// <summary>显示窗口——SHBrowseForFolder 先创建后显示，本方法兜底把「已创建但未显示」的框显示出来。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        /// <summary>取当前系统线程 ID（EnumThreadWindows 的入参）。</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        /// <summary>EnumThreadWindows 回调签名。</summary>
        private delegate bool EnumThreadProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>z-order 目标——置顶（HWND_TOPMOST）。</summary>
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        /// <summary>SetWindowPos 标志——不移动。</summary>
        private const uint SwpNoMove = 0x0002;
        /// <summary>SetWindowPos 标志——不改尺寸。</summary>
        private const uint SwpNoSize = 0x0001;
        /// <summary>SetWindowPos 标志——需要时显示。</summary>
        private const uint SwpShowWindow = 0x0040;
        /// <summary>WM_CLOSE 消息号。</summary>
        private const uint WmClose = 0x0010;
        /// <summary>ShowWindow 的 nCmdShow——正常显示。</summary>
        private const int SwShownormal = 1;
        /// <summary>Win32 对话框窗口类名——FolderBrowserDialog 的宿主类。</summary>
        private const string DialogClassName = "#32770";

        /// <summary>浏览框调试日志——只在 --browse-debug 打开时输出；日志代码保留，便于以后排查弹框问题。</summary>
        private static void BrowseLog(string message)
        {
            if (BrowseDebug)
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>启动置顶定时器——Tick 由 ShowDialog 的模态消息循环泵动，把选择框提到最上层。</summary>
        private static void StartBrowseTopTimer()
        {
            BrowseThreadId = GetCurrentThreadId();
            BrowseTopTicks = 0;
            BrowseTopDone = false;
            BrowseDlgHandle = IntPtr.Zero;
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += BrowseTopOnTick;
            BrowseTopTimer = timer;
            timer.Start();
            BrowseLog("[浏览] 置顶定时器已启动——线程 " + BrowseThreadId.ToString() + " · 每 200 ms 扫一次");
        }

        /// <summary>停止并释放置顶定时器（幂等）。</summary>
        private static void StopBrowseTopTimer()
        {
            System.Windows.Forms.Timer timer = BrowseTopTimer;
            if (timer == null)
            {
                return;
            }
            timer.Stop();
            timer.Dispose();
            BrowseTopTimer = null;
        }

        /// <summary>置顶定时器 Tick——枚举本线程顶层窗口，把选择框（#32770）提到最上层；不可见则强制显示，持续执行到对话框关闭（框可能先创建后显示，显示时会重置 z-order，故不一次成功即停）。</summary>
        private static void BrowseTopOnTick(object sender, EventArgs e)
        {
            BrowseTopTicks = BrowseTopTicks + 1;
            BrowseDlgFound = new List<IntPtr>();
            BrowseOtherFound = new List<IntPtr>();
            EnumThreadWindows(BrowseThreadId, BrowseEnumProcRef, IntPtr.Zero);
            List<IntPtr> found = BrowseDlgFound;
            if (found.Count == 0)
            {
                found = BrowseOtherFound;
            }
            if (found.Count == 0)
            {
                if (BrowseTopTicks <= 3)
                {
                    BrowseLog("[浏览] 置顶扫描 #" + BrowseTopTicks + "：本线程还没有可置顶的顶层窗口");
                }
                return;
            }
            IntPtr dlg = found[0];
            BrowseDlgHandle = dlg;
            bool visible = IsWindowVisible(dlg);
            bool first = BrowseTopTicks <= 5;
            bool changed = visible != BrowseLastVisible;
            if (!visible)
            {
                // [段1] 框已创建但未显示——强制显示（创建与显示之间会泵消息，定时器可能插在中间）
                ShowWindow(dlg, SwShownormal);
            }
            // [段2] 持续置顶——每 200 ms 一次（幂等，代价可忽略），直到对话框关闭才停表
            bool ok = SetWindowPos(dlg, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (ok)
            {
                BrowseTopDone = true;
            }
            if (first || changed || !ok)
            {
                BrowseLog("[浏览] 置顶扫描 #" + BrowseTopTicks + "：句柄 " + dlg.ToString()
                    + " · 可见=" + (visible ? "是" : "否（已强制显示）")
                    + " · SetWindowPos " + (ok ? "成功" : "失败（Win32 错误 " + err.ToString() + "）"));
            }
            BrowseLastVisible = visible;
        }

        /// <summary>EnumThreadWindows 回调——收集本线程顶层窗口（#32770 优先 · 其它可见窗备选），并打印诊断。</summary>
        private static bool BrowseEnumProc(IntPtr hWnd, IntPtr lParam)
        {
            StringBuilder cls = new StringBuilder(256);
            GetClassName(hWnd, cls, 256);
            string clsName = cls.ToString();
            StringBuilder title = new StringBuilder(256);
            GetWindowText(hWnd, title, 256);
            bool visible = IsWindowVisible(hWnd);
            if (BrowseTopTicks <= 3)
            {
                BrowseLog("[浏览]   顶层窗 " + hWnd.ToString() + " 类=" + clsName
                    + " 标题=" + title.ToString() + " 可见=" + (visible ? "是" : "否"));
            }
            if (hWnd == BrowseHostHandle)
            {
                return true;
            }
            if (clsName == DialogClassName)
            {
                if (BrowseDlgFound != null)
                {
                    BrowseDlgFound.Add(hWnd);
                }
                return true;
            }
            if (visible && BrowseOtherFound != null)
            {
                BrowseOtherFound.Add(hWnd);
            }
            return true;
        }
        /// <summary>面板版本（程序集版本）——回给前端显示，便于一眼核对「跑的是哪一份产物」。</summary>
        private static string PanelVersion()
        {
            Version v = typeof(WebApp).Assembly.GetName().Version;
            return v == null ? "?" : v.ToString();
        }

        /// <summary>设置表里编辑授权的键。</summary>
        private const string EditAuthKey = "edit_authorized";

        /// <summary>是否已取得编辑授权——以设置表为准（前端勾选不作授权依据）；默认不授权。</summary>
        private static bool IsEditAuthorized()
        {
            return _hub.Core.GetSetting(EditAuthKey) == "1";
        }

        /// <summary>取一张卡片的磁盘绝对路径（含库根白名单校验）；取不到时 why 说明原因。</summary>
        private static string CardPathOf(RootsConfig cfg, int lib, long id, out string why)
        {
            why = null;
            CardRow row = _hub.GetCard(lib, id);
            if (row == null)
            {
                why = "库里没有这张卡片（id=" + id + " · lib=" + lib + "）";
                return null;
            }
            string path = Path.Combine(row.RootPath == null ? "" : row.RootPath, row.Folder == null ? "" : row.Folder, row.FileName);
            if (!StoreHub.IsUnderCardRoot(cfg, path))
            {
                why = "路径不在受管的卡片库根内：" + path;
                return null;
            }
            if (!File.Exists(path))
            {
                why = "磁盘上没有这个文件（可能已被移动或重命名）：" + path;
                return null;
            }
            return path;
        }
        /// <summary>
        /// 取一张场景卡的 timeline 长度——缓存优先（卡片库表 card_timeline：size + mtime 一致即命中，命中出声），
        /// 未命中读盘一次并落表；读取失败不落表（下次请求重试）。
        /// </summary>
        /// <param name="lib">卡片所在库位。</param>
        /// <param name="id">卡片 id。</param>
        /// <param name="path">卡片文件绝对路径（已过库根白名单）。</param>
        /// <param name="imageEnd">图片区结束偏移。</param>
        private static TimelineInfo ReadTimelineCached(int lib, long id, string path, long imageEnd)
        {
            var fi = new FileInfo(path);
            string mtime = Store.StampOf(fi);
            Store store = _hub.StoreByLib(lib);
            CardTimelineRow row = store == null ? null : store.GetCardTimeline(id);
            if (row != null && row.Size == fi.Length && row.Mtime == mtime)
            {
                double cached = row.TimeScale <= 0 ? 1 : row.TimeScale;
                Console.WriteLine("[timeline] 命中已有缓存：" + fi.Name + (row.Error == null ? "" : "（上次读取失败：" + row.Error + "）"));
                return new TimelineInfo
                {
                    Scanned = true,
                    HasEntry = row.HasEntry,
                    IsEmpty = row.IsEmpty,
                    Duration = row.Duration,
                    TimeScale = cached,
                    Keyframes = row.Keyframes,
                    XmlLength = row.XmlLength,
                    RealSeconds = row.Duration / cached,
                    Error = row.Error
                };
            }
            TimelineInfo t = TimelineReader.Read(path, imageEnd);
            if (t.Error == null && store != null)
            {
                store.SaveCardTimeline(id, path, fi.Length, mtime, t);
            }
            Console.WriteLine("[timeline] " + fi.Name + " → " + TimelineReader.Describe(t)
                + "（命中阶段 " + (t.HitStage == null ? "无条目" : t.HitStage) + " · XML " + t.XmlLength + " 字节）");
            return t;
        }
        /// <summary>取一张场景卡的完整 timeline 模型——内存缓存优先（路径 + 大小 + 修改时间判失效），未命中现场解析。</summary>
        private static TimelineScene ReadTimelineSceneCached(string path, long imageEnd)
        {
            FileInfo fi = new FileInfo(path);
            string key = path + "|" + fi.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Store.StampOf(fi);
            TimelineScene hit;
            if (SceneCache.TryGetValue(key, out hit))
            {
                Console.WriteLine("[timeline] 轨道模型命中内存缓存：" + fi.Name);
                return hit;
            }
            TimelineScene scene = TimelineReader.ReadScene(path, imageEnd);
            if (scene.Error == null && scene.HasEntry)
            {
                if (SceneCache.Count >= 4)
                {
                    SceneCache.Clear();
                }
                SceneCache[key] = scene;
            }
            Console.WriteLine("[timeline] 轨道模型 " + fi.Name + " → 组 " + scene.Groups.Count + " · 轨道 " + scene.Tracks.Count
                + " · XML " + scene.XmlLength + " 字节" + (scene.Error == null ? "" : "（失败：" + scene.Error + "）"));
            return scene;
        }
        /// <summary>timeline 模型 → 面板 JSON（精简视图；full = 附属性全集，供后续编辑 / 导出复用同一份格式）。</summary>
        private static object TimelineSceneJson(TimelineScene scene, bool full)
        {
            List<object> groups = new List<object>();
            for (int i = 0; i < scene.Groups.Count; i = i + 1)
            {
                TimelineGroup g = scene.Groups[i];
                groups.Add(new { n = g.Name, p = g.ParentIndex, d = g.Depth, tc = g.TrackCount });
            }
            List<object> tracks = new List<object>();
            int keyframeTotal = 0;
            for (int i = 0; i < scene.Tracks.Count; i = i + 1)
            {
                TimelineTrack t = scene.Tracks[i];
                List<object> kfs = new List<object>();
                for (int k = 0; k < t.Keyframes.Count; k = k + 1)
                {
                    TimelineKeyframe kf = t.Keyframes[k];
                    if (kf.HasXYZW)
                    {
                        kfs.Add(new object[] { kf.Time, kf.ValueX, kf.ValueY, kf.ValueZ, kf.ValueW });
                    }
                    else
                    {
                        kfs.Add(new object[] { kf.Time, kf.Value });
                    }
                }
                keyframeTotal = keyframeTotal + t.Keyframes.Count;
                if (full)
                {
                    tracks.Add(new { g = t.GroupIndex, id = t.Id, alias = t.Alias, owner = t.Owner, oi = t.ObjectIndex, path = t.GuideObjectPath, en = t.Enabled, attrs = t.Attributes, kf = kfs });
                }
                else
                {
                    tracks.Add(new { g = t.GroupIndex, id = t.Id, alias = t.Alias, owner = t.Owner, oi = t.ObjectIndex, path = t.GuideObjectPath, en = t.Enabled, kf = kfs });
                }
            }
            object rootAttrs = null;
            if (full)
            {
                rootAttrs = scene.RootAttributes;
            }
            return new
            {
                ok = scene.Error == null,
                error = scene.Error,
                hasEntry = scene.HasEntry,
                isEmpty = scene.IsEmpty,
                duration = scene.Duration,
                timeScale = scene.TimeScale,
                blockLength = scene.BlockLength,
                divisions = scene.Divisions,
                xmlLength = scene.XmlLength,
                truncated = scene.Truncated,
                keyframes = keyframeTotal,
                groups = groups,
                tracks = tracks,
                rootAttrs = rootAttrs
            };
        }

        /// <summary>内嵌图片缩略图缓存（键 = 路径 + 偏移 + 长度 + 宽度）——同一张图重复请求直接命中。</summary>
        private static readonly Dictionary<string, byte[]> ThumbCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        /// <summary>timeline 完整模型的内存缓存（键 = 路径 + 大小 + 修改时间）——分析窗按需解析，避免反复读数 MB 的 XML。</summary>
        private static readonly Dictionary<string, TimelineScene> SceneCache = new Dictionary<string, TimelineScene>(StringComparer.Ordinal);
        /// <summary>timeline 端点的 JSON 选项——中文别名 / 路径不转义（默认编码器会把每个中文字符膨胀成 \uXXXX）。</summary>
        private static readonly JsonSerializerOptions RelaxJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        /// <summary>
        /// 取卡片内嵌图片并缩放为 JPEG（只读）——区间必须落在卡片文件内且以 PNG 签名开头。
        /// </summary>
        /// <param name="path">卡片文件绝对路径（已过库根白名单）。</param>
        /// <param name="offset">图片在文件内的偏移。</param>
        /// <param name="size">图片字节数。</param>
        /// <param name="width">缩略图目标宽度。</param>
        /// <param name="why">失败原因（成功为 null）。</param>
        /// <returns>JPEG 字节；失败返回 null。</returns>
        private static byte[] InlineImage(string path, long offset, long size, int width, out string why)
        {
            why = null;
            const long MaxInlineImage = 16L * 1024 * 1024;
            if (offset < 0 || size <= 0)
            {
                why = "区间非法（offset=" + offset + " · size=" + size + "）";
                return null;
            }
            if (size > MaxInlineImage)
            {
                why = "区间过大（" + size.ToString("N0") + " 字节 > 上限 " + MaxInlineImage.ToString("N0") + "）";
                return null;
            }
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists)
            {
                why = "卡片文件不存在";
                return null;
            }
            if (offset + size > fi.Length)
            {
                why = "区间越出文件末尾";
                return null;
            }

            // 缓存命中直接返回（同一张图的重复请求不再解码）
            string key = path + "|" + offset + "|" + size + "|" + width;
            lock (ThumbCache)
            {
                byte[] hit;
                if (ThumbCache.TryGetValue(key, out hit))
                {
                    return hit;
                }
            }

            byte[] raw = new byte[(int)size];
            using (FileStream fs = File.OpenRead(path))
            {
                fs.Position = offset;
                int total = 0;
                while (total < raw.Length)
                {
                    int n = fs.Read(raw, total, raw.Length - total);
                    if (n <= 0)
                    {
                        break;
                    }
                    total = total + n;
                }
                if (total != raw.Length)
                {
                    why = "读取不足（" + total + "/" + raw.Length + "）";
                    return null;
                }
            }
            if (raw[0] != 0x89 || raw[1] != 0x50 || raw[2] != 0x4E || raw[3] != 0x47)
            {
                why = "该区间不是 PNG 图片";
                return null;
            }
            byte[] jpg = Thumbnail.FromBytes(raw, width, 82);
            if (jpg == null)
            {
                why = "缩略图生成失败：" + (Thumbnail.LastError == null ? "<无诊断>" : Thumbnail.LastError);
                return null;
            }

            lock (ThumbCache)
            {
                if (ThumbCache.Count > 256)
                {
                    ThumbCache.Clear();
                }
                ThumbCache[key] = jpg;
            }
            return jpg;
        }

        /// <summary>载入并规范化库根配置（库序号分配与按库路由的依据）。</summary>
        private static RootsConfig LoadConfig()
        {
            RootsConfig cfg = _hub.Core.LoadRoots();
            RootsRules.Normalize(cfg);
            return cfg;
        }

        /// <summary>保存配置时消费「原行改地址」标记——同一行换了地址 = 用新地址完全替换原来的：旧库数据彻底删除（含待办）。</summary>
        private static void ApplyReplacedRoots(RootsConfig cfg)
        {
            ApplyReplacedList(cfg.modRoots, true);
            ApplyReplacedList(cfg.cardRoots, false);
        }

        /// <summary>处理一组库根的替换标记——预置条目（路径不可改）与未变行直接清标记跳过。</summary>
        private static void ApplyReplacedList(List<RootEntry> list, bool isMods)
        {
            if (list == null)
            {
                return;
            }
            foreach (RootEntry e in list)
            {
                string from = (e.replacedFrom ?? "").Trim();
                e.replacedFrom = "";
                if (from.Length == 0)
                {
                    continue;
                }
                if (string.Equals(from, (e.path ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (RootsRules.UsesCoreDb(e, isMods))
                {
                    continue;
                }
                if (_hub.DropLibByPath(from))
                {
                    _hub.Core.CloseTodoByKey("offline", from.ToLowerInvariant());
                    Console.WriteLine("[换库] 原路径数据已作废：" + from + " → " + e.path);
                }
            }
        }

        /// <summary>手动设离线 / 点击上线——手动离线不登记待办；上线消解该库的待办。返回错误文案（null = 成功）。</summary>
        private static string SetRootOffline(RootOfflineDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.path))
            {
                return "缺少路径";
            }
            bool isMods = dto.kind == "mods";
            RootsConfig cfg = _hub.Core.LoadRoots();
            RootsRules.Normalize(cfg);
            List<RootEntry> list = isMods ? cfg.modRoots : cfg.cardRoots;
            if (list == null)
            {
                return "库根不在配置里：" + dto.path;
            }
            foreach (RootEntry e in list)
            {
                if (!string.Equals((e.path ?? "").Trim(), dto.path.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!RootsRules.CanOffline(e, isMods))
                {
                    return "预置条目不适用离线：" + e.path;
                }
                e.offline = dto.offline;
                _hub.Core.SaveRoots(cfg);
                if (!dto.offline)
                {
                    _hub.Core.CloseTodoByKey("offline", e.path.Trim().ToLowerInvariant());
                }
                Console.WriteLine("[离线] " + (dto.offline ? "设为离线" : "点击上线") + "：" + e.path);
                return null;
            }
            return "库根不在配置里：" + dto.path;
        }

        /// <summary>彻底删除数据——删库文件与映射，并把这条库根从配置里移除（该库内容就此消失）。返回错误文案（null = 成功）。</summary>
        private static string PurgeRoot(RootPurgeDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.path))
            {
                return "缺少路径";
            }
            bool isMods = dto.kind == "mods";
            RootsConfig cfg = _hub.Core.LoadRoots();
            RootsRules.Normalize(cfg);
            List<RootEntry> list = isMods ? cfg.modRoots : cfg.cardRoots;
            RootEntry hit = null;
            if (list != null)
            {
                foreach (RootEntry e in list)
                {
                    if (string.Equals((e.path ?? "").Trim(), dto.path.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        hit = e;
                        break;
                    }
                }
            }
            if (hit == null)
            {
                return "库根不在配置里：" + dto.path;
            }
            if (!RootsRules.CanOffline(hit, isMods))
            {
                return "预置条目不可删除数据：" + hit.path;
            }
            string key = hit.path.Trim().ToLowerInvariant();
            _hub.DropLib(hit, isMods);
            list.Remove(hit);
            _hub.Core.SaveRoots(cfg);
            _hub.Core.CloseTodoByKey("offline", key);
            Console.WriteLine("[库数据] 库根已移除：" + hit.path);
            return null;
        }

        /// <summary>构建缺失清单（全量排行 + 每条前 per 张引用卡片）——结果缓存，扫描结束后失效。</summary>
        private static List<object> BuildMissing(int per)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            RootsConfig cfg = LoadConfig();
            List<object> rows = new List<object>();
            foreach (KeyValuePair<string, long> kv in _hub.MissingRanking(cfg, 0))
            {
                rows.Add(new
                {
                    guid = kv.Key,
                    cards = kv.Value,
                    samples = _hub.QueryCardsByMod(cfg, kv.Key, 1, per)
                });
            }
            Console.WriteLine("[缺失] 缓存重建：" + rows.Count + " 条 · 用时 " + watch.ElapsedMilliseconds + " ms（每条约 " + per + " 张样本）");
            return rows;
        }
        /// <summary>贴图清单文本 → 前端 DTO（显式小写字段名，不依赖序列化策略）。</summary>
        private static List<object> BuildU3dTextureDtos(string textures)
        {
            List<object> list = new List<object>();
            foreach (Unity3dTexture t in StoreHub.ParseU3dTextures(textures))
            {
                list.Add(new
                {
                    pathId = t.PathId,
                    name = t.Name ?? "",
                    width = t.Width,
                    height = t.Height,
                    format = t.Format,
                    dataLength = t.DataLength
                });
            }
            return list;
        }

        /// <summary>条目预览的字节上限（图片缩略图等——超过则拒绝，不无声截断）。</summary>
        private const long EntryPreviewMaxBytes = 16 * 1024 * 1024;
        /// <summary>按扩展名给条目内容类型（图片直接给浏览器渲染；其余按二进制流）。</summary>
        private static string EntryContentType(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            if (p.EndsWith(".png"))
            {
                return "image/png";
            }
            if (p.EndsWith(".jpg") || p.EndsWith(".jpeg"))
            {
                return "image/jpeg";
            }
            if (p.EndsWith(".gif"))
            {
                return "image/gif";
            }
            if (p.EndsWith(".bmp"))
            {
                return "image/bmp";
            }
            return "application/octet-stream";
        }

        private static void MapRoutes(WebApplication app)
        {
            app.MapGet("/", () => Results.Content(IndexHtml(), "text/html; charset=utf-8"));

            app.MapGet("/api/settings", () =>
            {
                RootsConfig cfg = LoadConfig();
                return Results.Json(new { roots = cfg, dbPath = _dbPath, version = PanelVersion(), editAuthorized = IsEditAuthorized() });
            });

            // 编辑授权——顶部栏勾选即落盘（与库根配置分开，随时可撤销；默认不授权）
            app.MapPost("/api/edit-auth", async context =>
            {
                EditAuthDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<EditAuthDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"请求体无法解析\"}");
                    return;
                }
                _hub.Core.SetSetting(EditAuthKey, dto.granted ? "1" : "0");
                Console.WriteLine("[授权] 编辑授权 " + (dto.granted ? "已开启" : "已撤销"));
                await context.Response.WriteAsync("{\"ok\":true,\"editAuthorized\":" + (dto.granted ? "true" : "false") + "}");
            });


            app.MapPost("/api/settings", async context =>
            {
                RootsConfig cfg;
                try
                {
                    cfg = await JsonSerializer.DeserializeAsync<RootsConfig>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException ex)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("配置解析失败: " + ex.Message);
                    return;
                }
                if (cfg == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("配置为空");
                    return;
                }
                if (cfg.modRoots == null)
                {
                    cfg.modRoots = new List<RootEntry>();
                }
                if (cfg.cardRoots == null)
                {
                    cfg.cardRoots = new List<RootEntry>();
                }
                RootsConfig previous = LoadConfig();
                List<string> warnings = new List<string>();
                bool firstModCache = !RootsRules.HasModCache(previous);
                // 锁定标记只由规范按路径认定——请求带来的标记一律清掉，不信任外部输入
                RootsRules.ClearLockFlags(cfg);
                // 原行改地址 = 用新地址完全替换原来的——旧库数据作废（删库文件 + 消解待办）；须在 Normalize 之前消费
                ApplyReplacedRoots(cfg);
                RootsRules.Normalize(cfg);
                // 游戏根换了 = 预置条目按新根重派生（旧根的条目不再复活；缓存库槽位未被改过则跟随新根）
                bool cacheMoved = false;
                string prevRoot = (previous.gameRoot ?? "").Trim();
                string nextRoot = (cfg.gameRoot ?? "").Trim();
                if (!string.Equals(prevRoot, nextRoot, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.AddRange(RootsRules.ApplyGameRootChange(cfg, previous.gameRoot, out cacheMoved));
                    // RestorePresets 的基准里旧根的预置条目作废——否则会把它们当「不可删」补回来
                    previous.modRoots.RemoveAll(e => e.locked);
                    previous.cardRoots.RemoveAll(e => e.locked);
                }
                // 预置主库不可删、不可改：请求里缺失的补回、改动的一律以磁盘现状为准（被拒的改动出声）
                warnings.AddRange(RootsRules.RestorePresets(previous, cfg));
                if (firstModCache || cacheMoved)
                {
                    string cachePath = RootsRules.ModCachePath(cfg);
                    if (cachePath.Length > 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(cachePath);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("缓存库目录创建失败（" + cachePath + "）：" + ex.Message);
                        }
                    }
                }
                _hub.Core.SaveRoots(cfg);
                // 保存结果一律出声——否则「保存没成功」与「保存成功但无提示」在控制台不可区分
                Console.WriteLine("[配置] 保存完成——游戏根 " + (cfg.gameRoot.Length > 0 ? cfg.gameRoot : "（未设置）") + " · mod 库根 " + cfg.modRoots.Count + " 个 · 卡片库根 " + cfg.cardRoots.Count + " 个");
                if (warnings.Count > 0)
                {
                    Console.WriteLine("[配置] 附 " + warnings.Count + " 条提示：");
                    foreach (string w in warnings)
                    {
                        Console.WriteLine("  ! " + w);
                    }
                }
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, roots = cfg, warnings = warnings },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            });

            app.MapPost("/api/scan", async context =>
            {
                ScanRequestDto dto = await JsonSerializer.DeserializeAsync<ScanRequestDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                string target = dto == null || string.IsNullOrEmpty(dto.target) ? "cards" : dto.target;
                bool force = dto != null && dto.force;
                string root = dto == null ? null : dto.root;
                string order = dto == null ? null : dto.order;
                string on = dto == null ? null : dto.on;
                string error = StartScan(target, force, root, order, on);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(error == null
                    ? "{\"ok\":true}"
                    : "{\"ok\":false,\"error\":\"" + error.Replace("\"", "'") + "\"}");
            });

            app.MapGet("/api/scan/status", () =>
            {
                ScanState s;
                lock (ScanLock)
                {
                    s = _scan;
                }
                return Results.Json(new
                {
                    running = s.Running,
                    target = s.Target,
                    message = s.Message,
                    currentRoot = s.CurrentRoot,
                    currentTier = s.CurrentTier,
                    rootIndex = s.RootIndex,
                    rootCount = s.RootCount,
                    seen = s.Seen,
                    added = s.Added,
                    skipped = s.Skipped,
                    nonCard = s.NonCard,
                    nonMod = s.NonMod,
                    failed = s.Failed,
                    refs = s.Refs,
                    thumbMB = Math.Round(s.ThumbBytes / 1024.0 / 1024.0, 1),
                    namesRead = s.NamesRead,
                    namesFilled = s.NamesFilled,
                    typesFixed = s.TypesFixed,
                    timelineRead = s.TimelineRead,
                    pluginDlls = s.PluginDlls,
                    plugins = s.Plugins,
                    pluginConfigs = s.PluginConfigs,
                    pluginDone = s.PluginDone,
                    pluginTotal = s.PluginTotal,
                    removed = s.Removed,
                    stepIndex = s.StepIndex,
                    stepCount = s.StepCount,
                    stepName = s.StepName,
                    stepDone = s.StepDone,
                    stepTotal = s.StepTotal,
                    startedAt = s.StartedAt,
                    finishedAt = s.FinishedAt,
                    errors = s.Errors
                });
            });

            // 扫描计划（步骤定义 + 当前顺序与勾选）——面板「扫描配置」窗用
            app.MapGet("/api/scan/plan", () =>
            {
                string orderText = _hub.Core.GetSetting(ScanPlanCatalog.OrderKey);
                string onText = _hub.Core.GetSetting(ScanPlanCatalog.OnKey);
                ScanPlan plan = ScanPlanCatalog.Load(orderText, onText);
                List<object> steps = new List<object>();
                foreach (ScanStepDef d in ScanPlanCatalog.All)
                {
                    steps.Add(new
                    {
                        id = d.Id,
                        name = d.Name,
                        mods = d.Mods,
                        required = d.Required,
                        defaultOn = d.DefaultOn,
                        grayed = d.Grayed,
                        note = d.Note,
                        on = plan.IsOn(d.Id)
                    });
                }
                return Results.Json(new { order = plan.Order, on = plan.On, steps = steps });
            });

            app.MapGet("/api/stats", () =>
            {
                RootsConfig cfg = LoadConfig();
                Snapshot snap = _hub.Snapshot(cfg);
                long dupGroups = 0;
                long dupPending = 0;
                _hub.DupCounts(cfg, out dupGroups, out dupPending);
                List<object> missing = new List<object>();
                foreach (KeyValuePair<string, long> kv in _hub.MissingRanking(cfg, 50))
                {
                    missing.Add(new { guid = kv.Key, cards = kv.Value });
                }
                return Results.Json(new
                {
                    cards = snap.Cards,
                    mods = snap.Mods,
                    modFiles = snap.ModFiles,
                    refs = snap.Refs,
                    modsTier1 = snap.ModsTier1,
                    modsTier2 = snap.ModsTier2,
                    modsTier3 = snap.ModsTier3,
                    cardsReady = snap.CardsReady,
                    cardsWithRefs = snap.CardsWithRefs,
                    green = snap.Colors.Green,
                    yellow = snap.Colors.Yellow,
                    red = snap.Colors.Red,
                    black = snap.Colors.Black,
                    pending = snap.Colors.Pending,
                    unusedMods = snap.UnusedMods,
                    dupMods = snap.DupMods,
                    dupGroups = dupGroups,
                    dupPending = dupPending,
                    nonCards = _hub.CountNonCards(cfg, false),
                    todos = _hub.Core.ListTodos().Count,
                    missing
                });
            });

            // 缺失清单全量（按被引用卡片数倒序）+ 每条附前 per 张引用卡片——面板直接展示「哪些卡缺它」
            // 缓存：首次请求构建，扫描结束后失效（扫描是缺失集合唯一的变化源）
            app.MapGet("/api/missing", (int? per) =>
            {
                int perGuid = 12;
                if (per.HasValue && per.Value > 0)
                {
                    perGuid = per.Value;
                }
                if (_missingCache == null || _missingCachePer != perGuid)
                {
                    _missingCache = BuildMissing(perGuid);
                    _missingCachePer = perGuid;
                }
                return Results.Json(new { total = _missingCache.Count, per = perGuid, items = _missingCache });
            });

            // 推荐条目按游戏根派生——gameRoot 查询参数为空时取配置现值（面板选定目录后即用它取 4 条级别 1 的路径）
            app.MapGet("/api/roots/recommend", (HttpContext context) =>
            {
                RootsConfig cfg = LoadConfig();
                string asked = context.Request.Query["gameRoot"];
                string root = string.IsNullOrWhiteSpace(asked) ? cfg.gameRoot : asked;
                return Results.Json(new
                {
                    gameRoot = RootsRules.NormalizeGameRoot(root),
                    pluginRoots = RootsRules.RecommendPluginRoots(root),
                    modRoots = RootsRules.RecommendModRoots(root),
                    cardRoots = RootsRules.RecommendCardRoots(root)
                });
            });

            // 插件清单——插件库（BepInEx）下解析出的插件（只读元数据；非插件 dll 一并列出，带未解析原因）
            app.MapGet("/api/plugins", () =>
            {
                List<PluginRow> rows = _hub.Core.LoadPlugins();
                List<PluginConfigRow> cfgs = _hub.Core.LoadPluginConfigs();
                // 上次启动实况（只读解析日志，不入库）——行级标识据此着色（已加载 / 进程过滤跳过 / 有错误 / 未提及）
                PluginLogSummary log = PluginLogReader.Read(PluginLogReader.LogPathOf(LoadConfig()));
                // cfg 按 GUID / 插件名建索引——插件行据此挂上「配置文件 / 配置项数」
                Dictionary<string, PluginConfigRow> cfgByGuid = new Dictionary<string, PluginConfigRow>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, PluginConfigRow> cfgByName = new Dictionary<string, PluginConfigRow>(StringComparer.OrdinalIgnoreCase);
                foreach (PluginConfigRow cfg in cfgs)
                {
                    if (cfg.Guid.Length > 0 && !cfgByGuid.ContainsKey(cfg.Guid))
                    {
                        cfgByGuid[cfg.Guid] = cfg;
                    }
                    if (cfg.PluginName.Length > 0 && !cfgByName.ContainsKey(cfg.PluginName))
                    {
                        cfgByName[cfg.PluginName] = cfg;
                    }
                }
                List<object> items = new List<object>();
                List<object> nonPlugins = new List<object>();
                Dictionary<string, int> guidCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int withGuid = 0;
                int noted = 0;
                foreach (PluginRow row in rows)
                {
                    string note = PluginNoteReader.Lookup(row.Guid, row.Name);
                    if (note.Length > 0 || PluginNoteReader.LookupFile(row.FileName).Length > 0)
                    {
                        noted = noted + 1;
                    }
                    if (row.Guid.Length == 0)
                    {
                        // 非插件 dll——一并列出（名称取程序集标题 + 中文说明 / 未解析原因），让「装了但不是插件」可见
                        string fileNote = PluginNoteReader.LookupFile(row.FileName);
                        nonPlugins.Add(new
                        {
                            fileName = row.FileName,
                            filePath = row.FilePath,
                            title = row.Title,
                            description = row.Description,
                            company = row.Company,
                            fileVersion = row.FileVersion,
                            size = row.Size,
                            mtime = row.Mtime,
                            note = fileNote,
                            reason = row.Note
                        });
                        continue;
                    }
                    withGuid = withGuid + 1;
                    if (guidCount.ContainsKey(row.Guid))
                    {
                        guidCount[row.Guid] = guidCount[row.Guid] + 1;
                    }
                    else
                    {
                        guidCount[row.Guid] = 1;
                    }
                    PluginConfigRow cfg = null;
                    cfgByGuid.TryGetValue(row.Guid, out cfg);
                    if (cfg == null && row.Name.Length > 0)
                    {
                        cfgByName.TryGetValue(row.Name, out cfg);
                    }
                    items.Add(new
                    {
                        guid = row.Guid,
                        name = row.Name,
                        version = row.Version,
                        fileName = row.FileName,
                        filePath = row.FilePath,
                        processes = row.Processes,
                        dependencies = row.Dependencies,
                        isIpa = row.IsIpa,
                        size = row.Size,
                        mtime = row.Mtime,
                        title = row.Title,
                        description = row.Description,
                        company = row.Company,
                        copyright = row.Copyright,
                        product = row.Product,
                        fileVersion = row.FileVersion,
                        targetFramework = row.TargetFramework,
                        note = note,
                        cfgFile = cfg == null ? "" : cfg.FileName,
                        cfgSections = cfg == null ? 0 : cfg.SectionCount,
                        cfgOptions = cfg == null ? 0 : cfg.OptionCount,
                        logState = PluginLogReader.StateOf(log, row.Name)
                    });
                }
                int dupGuids = 0;
                foreach (KeyValuePair<string, int> kv in guidCount)
                {
                    if (kv.Value > 1)
                    {
                        dupGuids = dupGuids + 1;
                    }
                }
                return Results.Json(new
                {
                    dllTotal = rows.Count,
                    plugins = withGuid,
                    nonPlugin = rows.Count - withGuid,
                    dupGuids = dupGuids,
                    noted = noted,
                    noteTotal = PluginNoteReader.Count,
                    noteError = PluginNoteReader.Error,
                    configs = cfgs.Count,
                    items = items,
                    nonPluginItems = nonPlugins
                });
            });

            // 扫描插件库（只读解析 dll 元数据，不改动任何文件）——增量：未变的 dll 跳过（size + mtime 双等）
            app.MapPost("/api/plugins/scan", () =>
            {
                RootsConfig cfg = LoadConfig();
                List<string> errors = new List<string>();
                DateTime started = DateTime.Now;
                PluginScanResult scan = Scanner.ScanPlugins(_hub.Core, cfg, errors, null);
                double seconds = (DateTime.Now - started).TotalSeconds;
                List<PluginRow> rows = _hub.Core.LoadPlugins();
                int withGuid = 0;
                foreach (PluginRow row in rows)
                {
                    if (row.Guid.Length > 0)
                    {
                        withGuid = withGuid + 1;
                    }
                }
                Console.WriteLine("[插件] 扫描完成——" + scan.Dlls + " 个 dll · " + seconds.ToString("F1") + " 秒 · 插件 " + withGuid + " 项 · 配置文件 " + scan.Configs + " 个");
                foreach (string e in errors)
                {
                    Console.WriteLine("  ! " + e);
                }
                return Results.Json(new
                {
                    ok = true,
                    dllTotal = scan.Dlls,
                    seconds = Math.Round(seconds, 1),
                    plugins = withGuid,
                    nonPlugin = rows.Count - withGuid,
                    configs = scan.Configs,
                    errors = errors
                });
            });

            // 场景卡插件键 ↔ 已装插件对照（键来自卡片数据区，插件来自插件库扫描）
            app.MapGet("/api/plugins/match", (string keys) =>
            {
                List<PluginRow> plugins = _hub.Core.LoadPlugins();
                List<object> items = new List<object>();
                if (!string.IsNullOrWhiteSpace(keys))
                {
                    string[] parts = keys.Split(',');
                    foreach (string raw in parts)
                    {
                        string key = raw.Trim();
                        if (key.Length == 0)
                        {
                            continue;
                        }
                        List<PluginRow> hits = PluginMatcher.Match(plugins, key);
                        List<object> found = new List<object>();
                        foreach (PluginRow p in hits)
                        {
                            found.Add(new { guid = p.Guid, name = p.Name, version = p.Version, fileName = p.FileName });
                        }
                        items.Add(new { key = key, installed = hits.Count > 0, hits = found });
                    }
                }
                return Results.Json(new { items = items });
            });

            // BepInEx 日志实况（加载 / 进程过滤跳过 / 错误）——只读解析，不入库（日志每次启动重写）
            app.MapGet("/api/plugins/log", () =>
            {
                RootsConfig cfg = LoadConfig();
                PluginLogSummary s = PluginLogReader.Read(PluginLogReader.LogPathOf(cfg));
                return Results.Json(new
                {
                    filePath = s.filePath,
                    fileTime = s.fileTime,
                    fileSize = s.fileSize,
                    toLoad = s.toLoad,
                    loaded = s.loaded.Count,
                    skipped = s.skipped.Count,
                    errorLines = s.errors.Count,
                    error = s.error,
                    errors = s.errors,
                    skips = s.skipped
                });
            });

            // 单个插件详情（插件分析窗）——dll 元数据 + 配置文件分节 / 选项 / 作者注释 + 日志加载状态
            app.MapGet("/api/plugin/detail", (string guid, string name) =>
            {
                List<PluginRow> rows = _hub.Core.LoadPlugins();
                List<object> hits = new List<object>();
                string key = (guid == null ? "" : guid).Trim();
                string alt = (name == null ? "" : name).Trim();
                foreach (PluginRow row in rows)
                {
                    bool match = (key.Length > 0 && string.Equals(row.Guid, key, StringComparison.OrdinalIgnoreCase))
                        || (alt.Length > 0 && string.Equals(row.Name, alt, StringComparison.OrdinalIgnoreCase));
                    if (!match)
                    {
                        continue;
                    }
                    hits.Add(new
                    {
                        guid = row.Guid,
                        name = row.Name,
                        version = row.Version,
                        fileName = row.FileName,
                        filePath = row.FilePath,
                        processes = row.Processes,
                        dependencies = row.Dependencies,
                        isIpa = row.IsIpa,
                        size = row.Size,
                        mtime = row.Mtime,
                        title = row.Title,
                        description = row.Description,
                        company = row.Company,
                        copyright = row.Copyright,
                        product = row.Product,
                        fileVersion = row.FileVersion,
                        targetFramework = row.TargetFramework
                    });
                }
                // 配置文件——按 GUID 精确取；没有则按插件名在 cfg 文件头里找
                PluginConfigRow cfg = _hub.Core.LoadPluginConfigByGuid(key);
                if (cfg.FilePath.Length == 0 && alt.Length > 0)
                {
                    foreach (PluginConfigRow c in _hub.Core.LoadPluginConfigs())
                    {
                        if (string.Equals(c.PluginName, alt, StringComparison.OrdinalIgnoreCase))
                        {
                            cfg = _hub.Core.LoadPluginConfigByGuid(c.Guid);
                            if (cfg.FilePath.Length == 0)
                            {
                                cfg = c;
                            }
                            break;
                        }
                    }
                }
                PluginConfigFile parsed = PluginConfigReader.FromJson(cfg.Sections);
                // 日志实况——该插件在本次启动日志里的状态（只读解析，不入库；判据与插件清单行级标识同源）
                RootsConfig roots = LoadConfig();
                PluginLogSummary log = PluginLogReader.Read(PluginLogReader.LogPathOf(roots));
                string logState = "日志未提及";
                foreach (PluginRow row in rows)
                {
                    if (row.Guid.Length == 0)
                    {
                        continue;
                    }
                    bool match = (key.Length > 0 && string.Equals(row.Guid, key, StringComparison.OrdinalIgnoreCase))
                        || (alt.Length > 0 && string.Equals(row.Name, alt, StringComparison.OrdinalIgnoreCase));
                    if (!match)
                    {
                        continue;
                    }
                    string st = PluginLogReader.StateOf(log, row.Name);
                    if (st == "error")
                    {
                        logState = "本次启动日志里有错误行";
                    }
                    else if (st == "loaded")
                    {
                        logState = "本次已加载";
                    }
                    else if (st == "skipped")
                    {
                        logState = "进程过滤跳过（该插件只在别的进程加载）";
                    }
                    break;
                }
                return Results.Json(new
                {
                    note = PluginNoteReader.Lookup(key, alt),
                    logState = logState,
                    logTime = log.fileTime,
                    cfgFile = cfg.FileName,
                    cfgPath = cfg.FilePath,
                    cfgSize = cfg.Size,
                    cfgMtime = cfg.Mtime,
                    cfgError = cfg.Error,
                    sectionCount = parsed.sections.Count,
                    sections = parsed.sections,
                    items = hits
                });
            });

            // 写回单个插件配置项的值（插件分析窗「当前值」改完点确认 → 保存）——🔴 编辑授权为唯一闸门：
            // 未授权一律拒绝（判定在服务端，前端勾选不作授权依据）；路径须落在插件库根内（白名单）
            app.MapPost("/api/plugin/config/save", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                PluginConfigSaveDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<PluginConfigSaveDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"请求体无法解析\"}");
                    return;
                }
                // [闸门1] 编辑授权——未授权不改任何文件
                if (!IsEditAuthorized())
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"未取得编辑授权——请先在编辑授权窗里确认\"}");
                    return;
                }
                // [闸门2] 路径白名单——cfg 必须落在已配置的插件库根内
                RootsConfig roots = LoadConfig();
                if (!StoreHub.IsUnderPluginRoot(roots, dto.path))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"路径不在受管的插件库根内\"}");
                    return;
                }
                PluginConfigSaveResult saved = PluginConfigWriter.Save(dto.path, dto.section, dto.key, dto.value);
                if (!saved.ok)
                {
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(saved.error) + "\"}");
                    return;
                }
                // [段3] 读回落库——cfg 解析结果整段刷新，面板再读即见新值（不靠重扫整库）
                PluginConfigFile reread = PluginConfigReader.Read(dto.path);
                if (reread.error.Length == 0)
                {
                    PluginConfigRow row = new PluginConfigRow();
                    row.FilePath = reread.filePath;
                    row.FileName = reread.fileName;
                    row.PluginName = reread.pluginName;
                    row.PluginVersion = reread.pluginVersion;
                    row.Guid = reread.guid;
                    row.Size = reread.size;
                    row.Mtime = reread.mtime;
                    row.Sections = PluginConfigReader.ToJson(reread);
                    row.SectionCount = reread.sections.Count;
                    long optionCount = 0;
                    foreach (PluginConfigSection sec in reread.sections)
                    {
                        optionCount = optionCount + sec.options.Count;
                    }
                    row.OptionCount = optionCount;
                    _hub.Core.SavePluginConfig(row);
                }
                Console.WriteLine("[插件] 配置写回 " + saved.fileName + " [" + saved.section + "] " + saved.key
                    + "：" + saved.oldValue + " → " + saved.newValue
                    + "（" + saved.sizeBefore + " → " + saved.sizeAfter + " 字节）");
                // [段4] 回执——本次改了什么 + 重读后的全量分节（前端就地重绘，不必再发一次读取）
                string payload = JsonSerializer.Serialize(new
                {
                    ok = true,
                    section = saved.section,
                    key = saved.key,
                    oldValue = saved.oldValue,
                    newValue = saved.newValue,
                    sizeBefore = saved.sizeBefore,
                    sizeAfter = saved.sizeAfter,
                    mtimeBefore = saved.mtimeBefore,
                    mtimeAfter = saved.mtimeAfter,
                    changed = !string.Equals(saved.oldValue, saved.newValue, StringComparison.Ordinal),
                    sectionCount = reread.sections.Count,
                    sections = reread.sections
                });
                await context.Response.WriteAsync(payload);
            });

            // 待办清单——软件认为需要使用者处理的事（目前只有「离线库」一类）；条目文案由前端按 kind 组装
            app.MapGet("/api/todos", () =>
            {
                List<TodoRow> rows = _hub.Core.ListTodos();
                return Results.Json(new { count = rows.Count, items = rows });
            });

            // 关闭一条待办（使用者在弹窗里当场销掉）
            app.MapPost("/api/todos/close", async context =>
            {
                TodoCloseDto dto = await JsonSerializer.DeserializeAsync<TodoCloseDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                context.Response.ContentType = "application/json";
                if (dto == null || dto.id <= 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 id\"}");
                    return;
                }
                _hub.Core.CloseTodo(dto.id);
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            // 新建待办——手输纯文本（无 cardId）或卡片待办（带 cardId / lib——卡片路径由服务端按 id 取，前端不拼路径）
            app.MapPost("/api/todos/add", async context =>
            {
                TodoAddDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<TodoAddDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                if (dto == null)
                {
                    // 失败走 200 + ok:false（与离线 / 彻底删除同类口径——前端只读 error 文本，不解析 HTTP 码）
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"请求体解析失败\"}");
                    return;
                }
                string error;
                if (dto.cardId > 0)
                {
                    CardRow row = _hub.GetCard(dto.lib, dto.cardId);
                    if (row == null)
                    {
                        await context.Response.WriteAsync("{\"ok\":false,\"error\":\"卡片不存在\"}");
                        return;
                    }
                    error = _hub.Core.AddCardTodo(Path.Combine(row.RootPath ?? "", row.Folder ?? "", row.FileName ?? ""), row.FileName, dto.text, Store.Now());
                }
                else
                {
                    error = _hub.Core.AddManualTodo(dto.text, Store.Now());
                }
                if (error != null)
                {
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            // 手动设为离线 / 点击上线——手动离线不登记待办；上线消解该库的待办
            app.MapPost("/api/roots/offline", async context =>
            {
                RootOfflineDto dto = await JsonSerializer.DeserializeAsync<RootOfflineDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                string error = SetRootOffline(dto);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(error == null
                    ? "{\"ok\":true}"
                    : "{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
            });

            // 彻底删除数据——删该库的库文件与映射，并把这条库根从配置里移除（该库内容就此消失）
            app.MapPost("/api/roots/purge", async context =>
            {
                RootPurgeDto dto = await JsonSerializer.DeserializeAsync<RootPurgeDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                string error = PurgeRoot(dto);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(error == null
                    ? "{\"ok\":true}"
                    : "{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
            });

            app.MapGet("/api/folders", () => Results.Json(_hub.QueryFolders(LoadConfig())));

            app.MapGet("/api/cards", (int page, int size, string filter, string q, string folder, string root, string order, bool? desc) =>
            {
                if (!string.IsNullOrEmpty(order) && order != "mtime" && order != "size" && order != "file" && order != "chara" && order != "timeline")
                {
                    return Results.BadRequest(new { ok = false, error = "未知的卡片排序键：" + order + "（可用：mtime / size / file / chara / timeline）" });
                }
                bool descending = desc == null || desc.Value;
                if (size <= 0)
                {
                    size = NoLimit;
                }
                if (page <= 0)
                {
                    page = 1;
                }
                return Results.Json(_hub.QueryCards(LoadConfig(), page, size, filter, q, folder, root, order, descending));
            });

            app.MapGet("/api/mods", (int page, int size, string filter, string q) =>
            {
                if (size <= 0)
                {
                    size = NoLimit;
                }
                if (page <= 0)
                {
                    page = 1;
                }
                return Results.Json(_hub.QueryMods(LoadConfig(), page, size, filter, q));
            });

            app.MapGet("/api/authors", () => Results.Json(_hub.QueryAuthors()));

            app.MapGet("/api/card/{id}", (long id, int lib) =>
            {
                CardRow row = _hub.GetCard(lib, id);
                return row == null ? Results.NotFound() : Results.Json(row);
            });

            app.MapGet("/api/card/{id}/refs", (long id, int tier, int lib) =>
                Results.Json(_hub.QueryCardRefs(lib, id, tier)));

            // 卡片文件结构（只读）——PNG 块表 + 图片区 / 数据区划分 + 数据区内容（部件 / 声明区 / 贴图 / 插件）
            // 场景卡（sd）另带 timeline 长度（Timeline 插件条目——按卡片缓存，size + mtime 判失效）+ 场景深度（插件条目 / 内嵌角色卡数据份数）
            app.MapGet("/api/card/{id}/structure", (long id, int lib) =>
            {
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why });
                }
                CardStructure st = CardDocument.Parse(path);
                CardDetailResult detail = null;
                TimelineInfo timeline = null;
                string timelineText = null;
                CardCoordinateResult coords = null;
                SceneInfoResult scene = null;
                object coordsOut = null;
                object detailOut = null;
                object sceneOut = null;
                if (st.Error == null && st.ImageEnd > 0)
                {
                    detailOut = LoadCachedAnalysis(_hub, lib, id, "detail", path);
                    if (detailOut == null)
                    {
                        detail = CardDetail.Parse(path, st.ImageEnd);
                        detailOut = detail;
                    }
                    if (st.CardType == CardReader.SceneCardType)
                    {
                        timeline = ReadTimelineCached(lib, id, path, st.ImageEnd);
                        timelineText = TimelineReader.Describe(timeline);
                        // 场景卡（sd）深度分析——优先读扫描时落库的档案（含逐份卡面图；落库时就已 Attach）
                        sceneOut = LoadCachedAnalysis(_hub, lib, id, "scene", path);
                        if (sceneOut == null)
                        {
                            scene = SceneReader.Read(path, st.ImageEnd);
                            if (detail != null)
                            {
                                scene.AttachCharaFaces(detail.Images);
                            }
                            sceneOut = scene;
                        }
                    }
                    else
                    {
                        // 服装 / 饰品（Coordinate 块七套槽位）——人物卡才有；优先读扫描时落库的分项分析，失效或缺失再现场解析
                        object cachedCoords = LoadCachedAnalysis(_hub, lib, id, "coord", path);
                        if (cachedCoords != null)
                        {
                            coordsOut = cachedCoords;
                        }
                        else
                        {
                            coords = CardCoordinate.Read(path);
                        }
                    }
                }
                object coordsFinal = coordsOut;
                if (coordsFinal == null)
                {
                    coordsFinal = coords;
                }
                object detailFinal = detailOut;
                if (detailFinal == null)
                {
                    detailFinal = detail;
                }
                object sceneFinal = sceneOut;
                if (sceneFinal == null)
                {
                    sceneFinal = scene;
                }
                return Results.Json(new { ok = st.Error == null, error = st.Error, structure = st, detail = detailFinal, timeline = timeline, timelineText = timelineText, coords = coordsFinal, scene = sceneFinal });
            });

            // 场景卡 timeline 完整模型（只读）——组树 + 轨道 + 关键帧（分析窗轨道视图）；full 缺省 = 精简视图，full=1 附属性全集（后续编辑 / 导出复用同一份格式）
            app.MapGet("/api/card/{id}/timeline", (long id, int lib, int? full) =>
            {
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why });
                }
                CardStructure st = CardDocument.Parse(path);
                if (st.Error != null)
                {
                    return Results.Json(new { ok = false, error = st.Error });
                }
                if (st.CardType != CardReader.SceneCardType)
                {
                    return Results.Json(new { ok = false, error = "不是场景卡——只有 Studio 场景卡带 timeline 数据" });
                }
                TimelineScene scene = ReadTimelineSceneCached(path, st.ImageEnd);
                return Results.Json(TimelineSceneJson(scene, full.HasValue && full.Value != 0), RelaxJson);
            });

            // 卡片内嵌图片缩略图（只读）——按偏移 / 长度取数据区里的 PNG，缩放为 JPEG 返回
            app.MapGet("/api/card/{id}/image", (long id, int lib, long offset, long size, int w) =>
            {
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why });
                }
                int width = w <= 0 ? 96 : w;
                if (width > 512)
                {
                    width = 512;
                }
                string err;
                byte[] jpg = InlineImage(path, offset, size, width, out err);
                if (jpg == null)
                {
                    Console.WriteLine("[卡片图片] " + err + " @" + offset + " +" + size);
                    return Results.Json(new { ok = false, error = err });
                }
                return Results.Bytes(jpg, "image/jpeg");
            });

            // 性格字母表（数值 ↔ 名称）——编辑下拉与展示共用；来源见 Core/Personality.cs
            app.MapGet("/api/personalities", () => Results.Json(new { ok = true, list = Personality.All() }));

            // 敏感带字母表（数值 ↔ 名称）——编辑下拉与展示共用；来源见 Core/WeakPoint.cs
            app.MapGet("/api/weak-points", () => Results.Json(new { ok = true, list = WeakPoint.All() }));

            // 卡片可编辑字段（只读）——人物卡 Parameter 块里的姓 / 名 / 爱称 / 性格
            app.MapGet("/api/card/{id}/params", (long id, int lib) =>
            {
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why });
                }
                CardLayout layout = CardEdit.Parse(path);
                CardParamInfo info = CardEdit.ReadParams(path, layout);
                if (info.Error != null)
                {
                    return Results.Json(new { ok = false, error = info.Error, cardType = layout.CardType });
                }
                return Results.Json(new
                {
                    ok = true,
                    cardType = layout.CardType,
                    lastName = info.LastName,
                    firstName = info.FirstName,
                    nickName = info.NickName,
                    personality = info.Personality,
                    personalityName = Personality.NameOf(info.Personality),
                    weakPoint = info.WeakPoint,
                    weakPointName = WeakPoint.NameOf(info.WeakPoint),
                    denial = info.Denial,
                    denialKeys = CardEdit.DenialKeys,
                    denialNames = CardEdit.DenialNames,
                    archived = _hub.Core.ListCardEdits(path).Count
                });
            });

            // 写卡片字段——编辑授权闸门（服务端自己判定）+ 原版留档 + 最小改动写回（时间与属性还原）
            app.MapPost("/api/card/{id}/params", async (long id, HttpContext context) =>
            {
                if (!IsEditAuthorized())
                {
                    return Results.Json(new { ok = false, error = "未取得编辑授权——先在顶部栏勾选「编辑授权」" }, statusCode: 403);
                }
                CardEditDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<CardEditDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                if (dto == null)
                {
                    return Results.Json(new { ok = false, error = "请求体无法解析" }, statusCode: 400);
                }
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, dto.lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why }, statusCode: 404);
                }
                CardLayout layout = CardEdit.Parse(path);
                CardParamInfo cur = CardEdit.ReadParams(path, layout);
                CardParamEdit edit = new CardParamEdit();
                edit.LastName = dto.lastName;
                edit.FirstName = dto.firstName;
                edit.NickName = dto.nickName;
                edit.Personality = dto.personality;
                edit.WeakPoint = dto.weakPoint;
                edit.Denial = dto.denial;
                FileInfo before = new FileInfo(path);
                CardEditResult r = CardEdit.Apply(path, layout, cur, edit, CardArchiveDir());
                if (!r.Ok)
                {
                    Console.WriteLine("[卡片编辑] 失败：" + r.Error);
                    return Results.Json(new { ok = false, error = r.Error }, statusCode: 400);
                }
                _hub.Core.AddCardEdit(BuildEditRecord(path, dto.lib, before, r));
                _hub.StoreByLib(dto.lib).UpdateCardSize(id, r.NewSize);
                Console.WriteLine("[卡片编辑] " + Path.GetFileName(path) + " · " + string.Join(" · ", r.Changes)
                    + " · 字节增量 " + r.Delta + " · 留档 " + (r.ArchivedFile == null ? "<无>" : Path.GetFileName(r.ArchivedFile)));
                return Results.Json(new
                {
                    ok = true,
                    changes = r.Changes,
                    delta = r.Delta,
                    newSize = r.NewSize,
                    archivedFile = r.ArchivedFile,
                    archivedSize = r.ArchivedSize
                });
            });

            // 寻找旧版——该卡片的留档记录（原版留在软件内部；改过一次留一份）
            app.MapGet("/api/card/{id}/archives", (long id, int lib) =>
            {
                RootsConfig cfg = LoadConfig();
                string path = CardPathOf(cfg, lib, id, out string why);
                if (path == null)
                {
                    return Results.Json(new { ok = false, error = why });
                }
                return Results.Json(new { ok = true, cardPath = path, archiveDir = CardArchiveDir(), list = _hub.Core.ListCardEdits(path) });
            });


            app.MapPost("/api/browse-folder", async context =>
            {
                string picked = null;
                string error = null;
                context.Response.ContentType = "application/json; charset=utf-8";
                BrowseLog("[浏览] 请求到达——开一个文件夹选择框");
                // 防叠：上一个选择框未关闭时不再弹新的（满屏看不见的模态框是最坏情况）
                if (System.Threading.Interlocked.CompareExchange(ref BrowseActive, 1, 0) != 0)
                {
                    BrowseLog("[浏览] 拒绝——上一个选择框仍未关闭");
                    await context.Response.WriteAsync("{\"ok\":false,\"stage\":\"busy\",\"error\":\"上一个文件夹选择框仍未关闭——先关掉它（按 Esc），再点一次「浏览…」\"}");
                    return;
                }

                System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(false);
                Thread t = new Thread(() =>
                {
                    try
                    {
                        // 宿主窗必须**可见**且落在**鼠标所在屏**的工作区中心——屏外 owner / 全透明 owner 都会让 ShowDialog(owner) 把框带到看不见的地方
                        System.Drawing.Rectangle work = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
                        int hostWidth = 420;
                        int hostHeight = 96;
                        BrowseLog("[浏览] 弹框线程启动——目标屏 " + work.Width + "x" + work.Height + " @" + work.X + "," + work.Y);
                        using (System.Windows.Forms.Form host = new System.Windows.Forms.Form())
                        {
                            host.Text = "选择文件夹";
                            host.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
                            host.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                            host.Location = new System.Drawing.Point(work.X + (work.Width - hostWidth) / 2, work.Y + (work.Height - hostHeight) / 2);
                            host.Size = new System.Drawing.Size(hostWidth, hostHeight);
                            host.TopMost = true;
                            System.Windows.Forms.Label hint = new System.Windows.Forms.Label();
                            hint.Text = "文件夹选择框已打开——请在弹出的窗口中选择目录。";
                            hint.Dock = System.Windows.Forms.DockStyle.Fill;
                            hint.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
                            host.Controls.Add(hint);
                            host.Show();
                            host.Activate();
                            bool fg = SetForegroundWindow(host.Handle);
                            BrowseHostHandle = host.Handle;
                            BrowseLog("[浏览] 宿主窗已显示 @" + host.Location.X + "," + host.Location.Y
                                + " · 句柄 " + host.Handle.ToString()
                                + " · SetForegroundWindow " + (fg ? "成功" : "失败（前台锁定——不阻塞，靠置顶兜底）"));
                            StartBrowseTopTimer();
                            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                dlg.Description = "选择文件夹";
                                dlg.ShowNewFolderButton = true;
                                System.Windows.Forms.DialogResult result = dlg.ShowDialog(host);
                                StopBrowseTopTimer();
                                if (result == System.Windows.Forms.DialogResult.OK)
                                {
                                    picked = dlg.SelectedPath;
                                }
                                BrowseLog("[浏览] 对话框已关闭——" + (picked == null ? "取消 / 未选" : "已选 " + picked)
                                    + " · 置顶 " + (BrowseTopDone ? "已生效" : "未生效") + " · Tick " + BrowseTopTicks.ToString());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                        BrowseLog("[浏览] 异常——" + error);
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref BrowseActive, 0);
                        done.Set();
                    }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();

                // 等待放到线程池，不占 Kestrel 线程；上限 300 秒（翻目录可能很久），超时出声（不留永久挂起的请求）
                bool finished = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(300)));
                if (!finished)
                {
                    IntPtr stuck = BrowseDlgHandle;
                    BrowseLog("[浏览] 等待超时（300 秒）——选择框句柄 " + stuck.ToString()
                        + " · 置顶 " + (BrowseTopDone ? "已生效" : "未生效") + " · Tick " + BrowseTopTicks.ToString());
                    if (stuck != IntPtr.Zero)
                    {
                        bool sent = PostMessage(stuck, WmClose, IntPtr.Zero, IntPtr.Zero);
                        BrowseLog("[浏览] 超时主动关闭——WM_CLOSE " + (sent ? "已投递" : "投递失败"));
                    }
                    else
                    {
                        BrowseLog("[浏览] 超时主动关闭——没记录到选择框句柄（框若仍开着请按 Esc）");
                    }
                    await context.Response.WriteAsync("{\"ok\":false,\"stage\":\"timeout\",\"error\":\"300 秒内未操作——已尝试自动关闭选择框；若框仍可见请按 Esc，之后可再点一次\"}");
                    return;
                }
                if (error != null)
                {
                    await context.Response.WriteAsync("{\"ok\":false,\"stage\":\"exception\",\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                BrowseLog("[浏览] 请求完成——" + (picked == null ? "未选（取消）" : "已选 " + picked));
                await context.Response.WriteAsync("{\"ok\":" + (picked != null ? "true" : "false") + ",\"stage\":\"done\",\"path\":\"" + EscapeJson(picked == null ? "" : picked) + "\"}");
            });

            // 重复副本——同 guid 多份文件；版本 / 作者实时读 manifest（人工筛旧版的判据一次看全）
            app.MapGet("/api/dup", () =>
            {
                RootsConfig cfg = LoadConfig();
                List<DupGroup> groups = _hub.ListDuplicateGroups(cfg);
                List<ModFileRecord> allFiles = new List<ModFileRecord>();
                List<string> dupGuids = new List<string>();
                foreach (DupGroup g in groups)
                {
                    allFiles.AddRange(g.Files);
                    dupGuids.Add(g.Guid);
                }
                // 引用卡片——组标题行最右侧缩略图区：一次批量查询（每组总数 + 前三张），不再逐组重算四色聚合
                Dictionary<string, DupCardRefs> cardRefs = _hub.QueryDupCardRefs(cfg, dupGuids, 3);
                // MD5 只算冲突文件（重复副本组内），且只在打开本面板时补齐一次——算过就忽略（档案落主库 mod_hash）
                int computed = 0;
                List<string> hashErrors = new List<string>();
                Dictionary<string, string> hashes = _hub.FillHashes(allFiles, out computed, out hashErrors);
                if (computed > 0)
                {
                    Console.WriteLine("[哈希] 新增 " + computed + " 条 MD5 档案——重复副本 " + groups.Count + " 组 / " + allFiles.Count + " 份");
                }
                foreach (string e in hashErrors)
                {
                    Console.WriteLine("[哈希] 算不出：" + e);
                }
                List<object> items = new List<object>();
                foreach (DupGroup g in groups)
                {
                    List<object> files = new List<object>();
                    foreach (ModFileRecord f in g.Files)
                    {
                        ModCopyView copy = new ModCopyView();
                        FillModCopy(copy, f, hashes);
                        files.Add(copy);
                    }
                    // 引用这个 guid 的卡片——从批量查询结果取（与组标题行缩略图区同一份数据；只取前三张，其余走「查看更多」叠层弹窗）
                    DupCardRefs refs = null;
                    cardRefs.TryGetValue(g.Guid, out refs);
                    List<object> cards = new List<object>();
                    if (refs != null)
                    {
                        foreach (CardRow c in refs.Top)
                        {
                            cards.Add(new { id = c.Id, lib = c.Lib, hasThumb = c.HasThumb, fileName = c.FileName });
                        }
                    }
                    items.Add(new { guid = g.Guid, mainCount = g.MainCount, files = files, cards = cards, cardTotal = refs == null ? 0 : refs.Total });
                }
                List<ModOldRecord> olds = new List<ModOldRecord>();
                foreach (ModOldRecord o in _hub.Core.ListModOld())
                {
                    olds.Add(o);
                }
                return Results.Json(new { total = items.Count, items = items, olds = olds, hashErrors = hashErrors });
            });

            // 判为旧版——该副本改名加 .old 段并移到缓存库，落一条新旧版本记录
            app.MapPost("/api/mod/mark-old", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                ModOldRequestDto dto = await ReadOldRequest(context);
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid 或路径\"}");
                    return;
                }
                string detail;
                string error = _hub.MarkOld(LoadConfig(), dto.guid, dto.path, out detail);
                await WriteOldResult(context, error, detail);
            });

            // 指定这个版本——该副本成为主库当前版本（不在主库则搬入并正名），同 guid 其余非旧版副本判为旧版并移入缓存库
            app.MapPost("/api/mod/keep-version", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                ModOldRequestDto dto = await ReadOldRequest(context);
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid 或路径\"}");
                    return;
                }
                string detail;
                string error = _hub.KeepVersion(LoadConfig(), dto.guid, dto.path, out detail);
                await WriteOldResult(context, error, detail);
            });

            // 换用此版本——旧版与主库当前版本完全互换（位置 + 名字）；选错版本时的纠正路径
            app.MapPost("/api/mod/swap", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                ModOldRequestDto dto = await ReadOldRequest(context);
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid 或路径\"}");
                    return;
                }
                string detail;
                string error = _hub.SwapVersion(LoadConfig(), dto.guid, dto.path, out detail);
                await WriteOldResult(context, error, detail);
            });

            // 正名搬入主库——主库没有该 mod 版本时去掉 .old 段（新版已不在）
            app.MapPost("/api/mod/promote", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                ModOldRequestDto dto = await ReadOldRequest(context);
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid 或路径\"}");
                    return;
                }
                string detail;
                string error = _hub.PromoteOld(LoadConfig(), dto.guid, dto.path, out detail);
                await WriteOldResult(context, error, detail);
            });

            app.MapGet("/api/mod/{guid}/files", (string guid) =>
                Results.Json(_hub.QueryModFiles(LoadConfig(), guid)));

            app.MapGet("/api/mod/{guid}/cards", (string guid, int page, int size) =>
            {
                if (size <= 0)
                {
                    size = NoLimit;
                }
                if (page <= 0)
                {
                    page = 1;
                }
                RootsConfig cfg = LoadConfig();
                return Results.Json(new
                {
                    guid = guid,
                    total = _hub.CountCardsByMod(cfg, guid),
                    items = _hub.QueryCardsByMod(cfg, guid, page, size)
                });
            });

            app.MapPost("/api/reveal", async context =>
            {
                RevealRequestDto dto = await JsonSerializer.DeserializeAsync<RevealRequestDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                context.Response.ContentType = "application/json";
                if (dto == null || string.IsNullOrWhiteSpace(dto.path))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少路径\"}");
                    return;
                }
                RootsConfig revealCfg = LoadConfig();
                if (!StoreHub.IsUnderModRoot(revealCfg, dto.path) && !StoreHub.IsUnderCardRoot(revealCfg, dto.path)
                    && !StoreHub.IsUnderPluginRoot(revealCfg, dto.path) && !IsUnderArchive(dto.path))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"路径不在受管的 mod 库根、卡片库根、插件库根或编辑留档目录内\"}");
                    return;
                }
                if (!File.Exists(dto.path))
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"文件不存在\"}");
                    return;
                }
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dto.path + "\"") { UseShellExecute = true });
                    await context.Response.WriteAsync("{\"ok\":true}");
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                }
            });

            // 查看 mod 组成——按需建档（点「查看 mod 组成」才读容器）；文件按路径 / 大小 / 修改时间判失效，未变直接复用档案
            app.MapPost("/api/mod/composition", async context =>
            {
                CompositionRequestDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<CompositionRequestDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                string guid = dto == null ? null : dto.guid;
                if (string.IsNullOrWhiteSpace(guid))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid\"}");
                    return;
                }
                string error;
                ModComposition rec = _hub.AnalyzeComposition(LoadConfig(), guid, dto != null && dto.force, out error);
                if (rec == null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                // [段1] 条目清单在服务端切成数组（前端不再重做解析）+ 目录聚合 + 文本条目内容（悬停预览）
                List<string> entries = new List<string>();
                if (!string.IsNullOrEmpty(rec.Entries))
                {
                    entries.AddRange(rec.Entries.Split('\n'));
                }
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    guid = rec.Guid,
                    filePath = rec.FilePath,
                    fileName = rec.FileName,
                    size = rec.Size,
                    mtime = rec.Mtime,
                    entryCount = rec.EntryCount,
                    totalSize = rec.TotalSize,
                    totalCompressed = rec.TotalCompressed,
                    analyzedAt = rec.AnalyzedAt,
                    cached = rec.Cached,
                    dirs = StoreHub.BuildDirs(rec.Entries),
                    entries = entries,
                    texts = StoreHub.ParseTexts(rec.Texts)
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            });

            // 读已有组成档案（不建档、不读容器）——打开 mod 时优先展示；无档案返回 exists:false
            app.MapGet("/api/mod/{guid}/composition", (string guid) =>
            {
                ModComposition rec = _hub.LoadComposition(guid);
                if (rec == null)
                {
                    return Results.Json(new { ok = true, exists = false });
                }
                List<string> entries = new List<string>();
                if (!string.IsNullOrEmpty(rec.Entries))
                {
                    entries.AddRange(rec.Entries.Split('\n'));
                }
                return Results.Json(new
                {
                    ok = true,
                    exists = true,
                    guid = rec.Guid,
                    filePath = rec.FilePath,
                    fileName = rec.FileName,
                    size = rec.Size,
                    mtime = rec.Mtime,
                    entryCount = rec.EntryCount,
                    totalSize = rec.TotalSize,
                    totalCompressed = rec.TotalCompressed,
                    analyzedAt = rec.AnalyzedAt,
                    cached = true,
                    dirs = StoreHub.BuildDirs(rec.Entries),
                    entries = entries,
                    texts = StoreHub.ParseTexts(rec.Texts)
                });
            });

            // 取容器内某个条目的字节（图片缩略图 / 预览用）——白名单 = 该 guid 档案里的条目清单
            app.MapGet("/api/mod/{guid}/entry", (string guid, string path) =>
            {
                ModComposition rec = _hub.LoadComposition(guid);
                if (rec == null || string.IsNullOrEmpty(rec.Entries))
                {
                    return Results.NotFound();
                }
                if (string.IsNullOrEmpty(path) || rec.Entries.IndexOf("\n" + path + "\t", StringComparison.Ordinal) < 0
                    && !rec.Entries.StartsWith(path + "\t", StringComparison.Ordinal))
                {
                    return Results.StatusCode(403);
                }
                if (!StoreHub.IsUnderModRoot(LoadConfig(), rec.FilePath))
                {
                    return Results.StatusCode(403);
                }
                string error;
                byte[] data = ZipModReader.ReadEntryBytes(rec.FilePath, path, EntryPreviewMaxBytes, out error);
                if (data == null)
                {
                    Console.WriteLine("[组成] 条目读取失败：" + path + " —— " + error);
                    return Results.NotFound();
                }
                return Results.Bytes(data, EntryContentType(path));
            });

            // 解析 unity3d 条目（按需建档——面板点开条目时触发）；force=true 强制重读容器
            app.MapPost("/api/mod/u3d", async context =>
            {
                U3dRequestDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<U3dRequestDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                if (dto == null || string.IsNullOrWhiteSpace(dto.guid) || string.IsNullOrWhiteSpace(dto.path))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少 guid 或条目路径\"}");
                    return;
                }
                string error;
                ModU3d rec = _hub.AnalyzeU3d(LoadConfig(), dto.guid, dto.path, dto.force, out error);
                if (rec == null)
                {
                    Console.WriteLine("[unity3d] 解析失败：" + dto.path + " —— " + error);
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                List<object> texDtos = BuildU3dTextureDtos(rec.Textures);
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    guid = rec.Guid,
                    path = rec.EntryPath,
                    textureCount = rec.TextureCount,
                    textures = texDtos,
                    parsedAt = rec.ParsedAt,
                    cached = rec.Cached
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            });

            // 读已有 unity3d 档案（不读容器）——组成区打开时优先展示；无档案返回 exists:false
            app.MapGet("/api/mod/{guid}/u3d", (string guid, string path) =>
            {
                ModU3d rec = _hub.LoadU3d(guid, path);
                if (rec == null)
                {
                    return Results.Json(new { ok = true, exists = false });
                }
                return Results.Json(new
                {
                    ok = true,
                    exists = true,
                    guid = rec.Guid,
                    path = rec.EntryPath,
                    textureCount = rec.TextureCount,
                    textures = BuildU3dTextureDtos(rec.Textures),
                    parsedAt = rec.ParsedAt,
                    cached = true
                });
            });

            // 该 mod 已解析的 unity3d 条目清单（组成区「已解析」标蓝判定用）
            app.MapGet("/api/mod/{guid}/u3dmap", (string guid) =>
            {
                List<string> paths = new List<string>();
                foreach (KeyValuePair<string, ModU3d> kv in _hub.LoadU3dMap(guid))
                {
                    paths.Add(kv.Key);
                }
                return Results.Json(new { ok = true, paths = paths });
            });

            // 取 unity3d 内一张贴图的 PNG 字节——白名单 = 该 guid 档案里的贴图清单；size=full 给原图，其余给缩略图
            app.MapGet("/api/mod/{guid}/u3dtex", (string guid, string path, long id, string size) =>
            {
                ModU3d rec = _hub.LoadU3d(guid, path);
                if (rec == null)
                {
                    return Results.NotFound();
                }
                if (!StoreHub.IsUnderModRoot(LoadConfig(), rec.FilePath))
                {
                    return Results.StatusCode(403);
                }
                bool thumb = !string.Equals(size, "full", StringComparison.OrdinalIgnoreCase);
                bool allowed = false;
                foreach (Unity3dTexture t in StoreHub.ParseU3dTextures(rec.Textures))
                {
                    if (t.PathId == id)
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                {
                    return Results.StatusCode(403);
                }
                string key = guid + "|" + path + "|" + id.ToString() + "|" + (thumb ? "t" : "f");
                byte[] png = null;
                lock (_u3dPngCache)
                {
                    _u3dPngCache.TryGetValue(key, out png);
                }
                if (png != null)
                {
                    return Results.Bytes(png, "image/png");
                }
                string readError;
                byte[] bytes = ZipModReader.ReadEntryBytes(rec.FilePath, path, U3dEntryMaxBytes, out readError);
                if (bytes == null)
                {
                    Console.WriteLine("[unity3d] 条目读取失败：" + path + " —— " + readError);
                    return Results.NotFound();
                }
                string renderError;
                png = Unity3dReader.RenderTexturePng(bytes, id, thumb ? 512 : 0, out renderError);
                if (png == null)
                {
                    Console.WriteLine("[unity3d] 贴图渲染失败：" + path + " #" + id.ToString() + " —— " + renderError);
                    return Results.NotFound();
                }
                lock (_u3dPngCache)
                {
                    if (_u3dPngCache.Count >= U3dPngCacheLimit)
                    {
                        _u3dPngCache.Clear();
                    }
                    _u3dPngCache[key] = png;
                }
                return Results.Bytes(png, "image/png");
            });

            app.MapPost("/api/move", async context =>
            {
                MoveRequestDto dto = await JsonSerializer.DeserializeAsync<MoveRequestDto>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                MoveResult result = MoveMods(dto);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            });

            // 未引用 mod → 缓存库：后台任务（单条约数秒，页面关掉也继续跑，重开可见进度与结果）
            app.MapPost("/api/move-unused/start", async context =>
            {
                MoveStartDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<MoveStartDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                string error = StartMoveUnused(dto == null ? null : dto.guids);
                if (error != null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            app.MapPost("/api/move-unused/stop", () =>
            {
                lock (MoveJobLock)
                {
                    _moveJob.StopRequested = true;
                }
                return Results.Json(new { ok = true });
            });

            app.MapGet("/api/move-unused/status", () => Results.Json(_moveJob));

            // 按作者整理——范围库根清单 / 计划生成 / 计划读取与确认 / 执行（后台任务，生成与执行共用一条状态）
            app.MapGet("/api/sort/roots", () =>
            {
                RootsConfig cfg = LoadConfig();
                List<SortRootView> list = new List<SortRootView>();
                foreach (RootEntry r in cfg.ModRootsOrdered())
                {
                    SortRootView v = new SortRootView();
                    v.path = r.path;
                    v.tier = r.tier;
                    v.tierName = Tier.Name(r.tier);
                    v.readOnly = r.readOnly;
                    v.offline = r.offline;
                    v.locked = r.locked;
                    v.canSort = !r.readOnly && !r.offline;
                    list.Add(v);
                }
                return Results.Json(new { ok = true, roots = list });
            });

            app.MapPost("/api/sort/plan/start", async context =>
            {
                SortPlanStartDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<SortPlanStartDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                string error = StartSortPlan(dto == null ? null : dto.roots);
                if (error != null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            app.MapPost("/api/sort/stop", () =>
            {
                lock (SortJobLock)
                {
                    _sortJob.StopRequested = true;
                }
                return Results.Json(new { ok = true });
            });

            app.MapGet("/api/sort/status", () => Results.Json(_sortJob));

            // 计划读取——读表 + 一次快照核对（文件如预期就在快照位置的行标 √ 并置底）
            app.MapGet("/api/sort/plan", () => Results.Json(LoadSortPlanView(true)));

            app.MapPost("/api/sort/plan/confirm", async context =>
            {
                SortConfirmDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<SortConfirmDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                string error = ConfirmSortPlan(dto);
                if (error != null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            app.MapPost("/api/sort/exec/start", async context =>
            {
                SortExecStartDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<SortExecStartDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                string error = StartSortExec(dto);
                if (error != null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":true}");
            });

            // 按作者整理——冲突组候选（同目标路径的几份副本；版本 / 作者实时读 manifest，MD5 实时算）
            app.MapGet("/api/sort/conflict", (long planId, long seq) => Results.Json(BuildSortConflictView(planId, seq)));

            // 按作者整理——保留这一份：其余同 guid 非旧版副本判旧版入缓存库（与重复副本同一实现），计划条目改指新位置后重核
            app.MapPost("/api/sort/conflict/keep", async context =>
            {
                SortKeepDto dto = null;
                try
                {
                    dto = await JsonSerializer.DeserializeAsync<SortKeepDto>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    dto = null;
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                if (dto == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"缺少请求体\"}");
                    return;
                }
                string detail = null;
                string error = KeepSortConflict(dto.planId, dto.seq, out detail);
                if (error != null)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, detail = detail, view = LoadSortPlanView(false) }));
            });

            app.MapGet("/api/thumb/{id}", (long id, int lib) =>
            {
                byte[] data = _hub.LoadThumb(lib, id);
                return data == null ? Results.NotFound() : Results.Bytes(data, "image/jpeg");
            });

            // 非卡 / 非 mod 清单——扫描判定为「不是卡片也不是 mod」的文件（含理由与缩略图）
            app.MapGet("/api/noncards", (bool? moved) =>
            {
                RootsConfig cfg = LoadConfig();
                bool includeMoved = moved.HasValue && moved.Value;
                long pending = _hub.CountNonCards(cfg, false);
                long all = _hub.CountNonCards(cfg, true);
                List<object> items = new List<object>();
                foreach (NonCardRow r in _hub.ListNonCards(cfg, includeMoved))
                {
                    items.Add(new
                    {
                        id = r.Id,
                        lib = r.Lib,
                        path = r.FilePath,
                        root = r.RootPath,
                        side = r.Side,
                        reason = r.Reason,
                        size = r.Size,
                        mtime = r.Mtime,
                        hasThumb = r.HasThumb,
                        movedTo = r.MovedTo
                    });
                }
                return Results.Json(new { items = items, total = items.Count, pending = pending, movedCount = all - pending });
            });

            // 一键把非卡 / 非 mod 文件搬到 mod 缓存库（游戏不读的那条库根）——逐条给出失败原因，失败项文件不动
            app.MapPost("/api/noncard/move", async context =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                RootsConfig cfg = LoadConfig();
                List<NonCardRow> rows = _hub.ListNonCards(cfg, false);
                List<object> failures = new List<object>();
                int movedCount = 0;
                int skipped = 0;
                foreach (NonCardRow r in rows)
                {
                    string detail;
                    string dest = _hub.MoveNonCard(cfg, r.Lib, r.Id, out detail);
                    if (dest == null)
                    {
                        skipped = skipped + 1;
                        if (failures.Count < 50)
                        {
                            failures.Add(new { path = r.FilePath, error = detail });
                        }
                        continue;
                    }
                    movedCount = movedCount + 1;
                }
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    moved = movedCount,
                    skipped = skipped,
                    failures = failures,
                    pending = _hub.CountNonCards(cfg, false)
                }));
            });

            // 非卡文件的缩略图（图片文件才有；非图片返回 404，界面显示占位）
            app.MapGet("/api/noncard/thumb/{id}", (long id, int lib) =>
            {
                byte[] data = _hub.LoadNonCardThumb(lib, id);
                return data == null ? Results.NotFound() : Results.Bytes(data, "image/jpeg");
            });
        }

        /// <summary>把指定 guid 在 fromTier 的文件搬到 toTier（默认缓存库 → 主库）。</summary>
        private static MoveResult MoveMods(MoveRequestDto dto)
        {
            MoveResult result = new MoveResult();
            if (dto == null || dto.guids == null || dto.guids.Count == 0)
            {
                result.Errors.Add("未指定要搬运的 mod");
                return result;
            }

            RootsConfig cfg = LoadConfig();
            int toTier = dto.toTier <= 0 ? Tier.Main : dto.toTier;
            int fromTier = dto.fromTier <= 0 ? Tier.Cache : dto.fromTier;

            RootEntry target = null;
            foreach (RootEntry r in cfg.ModRootsOrdered())
            {
                if (r.tier == toTier)
                {
                    target = r;
                    break;
                }
            }
            if (target == null)
            {
                result.Errors.Add("未配置级别 " + toTier + "（" + Tier.Name(toTier) + "）的 mod 库根，无法搬运");
                return result;
            }
            if (target.readOnly)
            {
                result.Errors.Add("目标库根为只读：" + target.path);
                return result;
            }

            try
            {
                Directory.CreateDirectory(target.path);
            }
            catch (Exception ex)
            {
                result.Errors.Add("目标目录不可用：" + ex.Message);
                return result;
            }

            foreach (string guid in dto.guids)
            {
                string detail;
                int code = MoveOneMod(_hub, cfg, guid, fromTier, target, toTier, out detail);
                if (code == MoveMoved)
                {
                    result.Moved.Add(guid + " → " + detail);
                }
                else if (code == MoveCopied)
                {
                    result.Moved.Add(guid + "（复制）→ " + detail);
                }
                else if (code == MoveSkipped)
                {
                    result.Skipped.Add(guid + "（" + detail + "）");
                }
                else
                {
                    result.Errors.Add(guid + "：" + detail);
                }
            }

            return result;
        }

        /// <summary>单条搬移结果——成功（移动）。</summary>
        private const int MoveMoved = 0;

        /// <summary>单条搬移结果——成功（源只读，复制）。</summary>
        private const int MoveCopied = 1;

        /// <summary>单条搬移结果——跳过（有原因）。</summary>
        private const int MoveSkipped = 2;

        /// <summary>单条搬移结果——失败（有原因）。</summary>
        private const int MoveFailed = 3;

        /// <summary>把某个 guid 在 fromTier 的一份副本搬到目标库根——成功时 detail 为目标路径，跳过 / 失败时 detail 为原因；文件动了而数据库没找到副本行时按失败上报（不静默算成功）。</summary>
        private static int MoveOneMod(StoreHub hub, RootsConfig cfg, string guid, int fromTier, RootEntry target, int toTier, out string detail)
        {
            detail = null;
            List<RefRow> files = hub.QueryModFiles(cfg, guid);
            RefRow pick = null;
            foreach (RefRow f in files)
            {
                if (f.Tier == fromTier)
                {
                    pick = f;
                    break;
                }
            }
            if (pick == null)
            {
                detail = "级别 " + fromTier + "（" + Tier.Name(fromTier) + "）无文件";
                return MoveSkipped;
            }

            string dest = Path.Combine(target.path, Path.GetFileName(pick.FilePath));
            if (File.Exists(dest))
            {
                detail = "目标已存在同名文件";
                return MoveSkipped;
            }

            bool readOnlySource = RootsRules.IsReadOnlyRoot(cfg, pick.RootPath);
            try
            {
                if (readOnlySource)
                {
                    File.Copy(pick.FilePath, dest, false);
                }
                else
                {
                    File.Move(pick.FilePath, dest);
                }
            }
            catch (Exception ex)
            {
                detail = "文件操作失败" + StoreHub.FileBusyHint(ex);
                return MoveFailed;
            }

            try
            {
                if (readOnlySource)
                {
                    if (hub.AddModFileCopy(cfg, guid, pick.RootPath, pick.FilePath, dest, toTier, target.path) == null)
                    {
                        detail = "已复制但数据库未找到源副本行：" + pick.FilePath + "（需重扫核对）";
                        return MoveFailed;
                    }
                    detail = dest;
                    return MoveCopied;
                }
                if (hub.MoveModFile(cfg, guid, pick.FilePath, pick.RootPath, dest, toTier, target.path) == null)
                {
                    detail = "已移动但数据库未找到源副本行：" + pick.FilePath + "（需重扫核对）";
                    return MoveFailed;
                }
            }
            catch (Exception ex)
            {
                detail = "文件已" + (readOnlySource ? "复制" : "移动") + "但数据库更新失败：" + ex.GetType().Name + " " + ex.Message;
                return MoveFailed;
            }

            detail = dest;
            return MoveMoved;
        }

        /// <summary>启动「未引用 mod 移到缓存库」后台任务——清单由服务端按「未被引用 ∧ 最优副本在主库」生成（字节数降序，先搬大的），传入的 guids 只作收窄；返回 null 表示已启动，否则返回拒绝原因。</summary>
        private static string StartMoveUnused(List<string> only)
        {
            lock (MoveJobLock)
            {
                if (_moveJob.Running)
                {
                    return "已有搬运任务在运行";
                }
                _moveJob = new MoveJobState
                {
                    Running = true,
                    Message = "准备清单",
                    StartedAt = DateTime.Now.ToString("HH:mm:ss")
                };
            }

            HashSet<string> want = null;
            if (only != null && only.Count > 0)
            {
                want = new HashSet<string>(only, StringComparer.Ordinal);
            }

            Task.Run(() =>
            {
                try
                {
                    using (StoreHub hub = new StoreHub(_dbPath))
                    {
                        RootsConfig cfg = hub.Core.LoadRoots();
                        RootsRules.Normalize(cfg);
                        hub.EnsureMigrated(cfg);

                        RootEntry target = null;
                        foreach (RootEntry r in cfg.ModRootsOrdered())
                        {
                            if (r.tier == Tier.Cache)
                            {
                                target = r;
                                break;
                            }
                        }
                        if (target == null)
                        {
                            FinishMoveJob("未配置缓存库（级别 2）的 mod 库根——无法搬运", true);
                            return;
                        }
                        if (target.readOnly)
                        {
                            FinishMoveJob("目标库根为只读：" + target.path, true);
                            return;
                        }
                        Directory.CreateDirectory(target.path);

                        HashSet<string> refs = hub.RefGuids(cfg);
                        List<string> pool = new List<string>();
                        foreach (KeyValuePair<string, long> kv in hub.Core.MainModSizes())
                        {
                            if (refs.Contains(kv.Key))
                            {
                                continue;
                            }
                            if (want != null && !want.Contains(kv.Key))
                            {
                                continue;
                            }
                            pool.Add(kv.Key);
                        }

                        lock (MoveJobLock)
                        {
                            _moveJob.Total = pool.Count;
                            _moveJob.Message = "搬运中";
                        }

                        foreach (string guid in pool)
                        {
                            lock (MoveJobLock)
                            {
                                if (_moveJob.StopRequested)
                                {
                                    break;
                                }
                                _moveJob.Current = guid;
                            }

                            string detail;
                            int code = MoveOneMod(hub, cfg, guid, Tier.Main, target, Tier.Cache, out detail);

                            lock (MoveJobLock)
                            {
                                _moveJob.Done = _moveJob.Done + 1;
                                if (code == MoveMoved || code == MoveCopied)
                                {
                                    _moveJob.Moved = _moveJob.Moved + 1;
                                }
                                else if (code == MoveSkipped)
                                {
                                    _moveJob.Skipped = _moveJob.Skipped + 1;
                                }
                                else
                                {
                                    _moveJob.Failed = _moveJob.Failed + 1;
                                    if (_moveJob.Errors.Count < 50)
                                    {
                                        _moveJob.Errors.Add(guid + "：" + detail);
                                    }
                                }
                            }
                        }
                    }

                    bool stopped;
                    lock (MoveJobLock)
                    {
                        stopped = _moveJob.StopRequested;
                    }
                    FinishMoveJob(stopped ? "已停止" : "搬运完成", false);
                }
                catch (Exception ex)
                {
                    FinishMoveJob(ex.GetType().Name + "：" + ex.Message, true);
                }
            });

            return null;
        }

        /// <summary>单条 mod 库根（按路径）——找不到返回 null。</summary>
        private static RootEntry FindModRoot(RootsConfig cfg, string path)
        {
            if (cfg == null || cfg.modRoots == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            string norm = path.Trim();
            foreach (RootEntry r in cfg.modRoots)
            {
                if (string.Equals((r.path ?? "").Trim(), norm, StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>启动「按作者整理」计划生成——清旧计划，后台枚举所选库根下全部 mod 文件并算目标路径与冲突；返回 null 表示已启动，否则返回拒绝原因。</summary>
        private static string StartSortPlan(List<string> roots)
        {
            RootsConfig cfg = LoadConfig();
            List<string> want = new List<string>();
            if (roots != null)
            {
                foreach (string p in roots)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        want.Add(p.Trim());
                    }
                }
            }
            if (want.Count == 0)
            {
                foreach (RootEntry r in cfg.ModRootsOrdered())
                {
                    if (r.readOnly || r.offline)
                    {
                        continue;
                    }
                    want.Add(r.path);
                }
            }
            if (want.Count == 0)
            {
                return "没有可整理的库根（只读 / 离线库不参与）";
            }
            foreach (string p in want)
            {
                RootEntry e = FindModRoot(cfg, p);
                if (e == null)
                {
                    return "库根不在已保存的配置里：" + p + "（先在设置里保存库根）";
                }
                if (e.readOnly)
                {
                    return "库根只读，不参与整理：" + p;
                }
                if (e.offline)
                {
                    return "库根离线，不参与整理：" + p;
                }
            }

            lock (SortJobLock)
            {
                if (_sortJob.Running)
                {
                    return "已有整理任务在运行";
                }
                _sortJob = new SortJobState
                {
                    Running = true,
                    Phase = "生成计划",
                    Message = "准备生成计划",
                    StartedAt = DateTime.Now.ToString("HH:mm:ss")
                };
            }

            List<string> scope = new List<string>(want);
            Task.Run(() =>
            {
                try
                {
                    using (StoreHub hub = new StoreHub(_dbPath))
                    {
                        RootsConfig c = hub.Core.LoadRoots();
                        RootsRules.Normalize(c);
                        hub.EnsureMigrated(c);
                        hub.Core.ClearSortPlans();
                        SortOrganizer.BuildPlan(hub, c, scope, _sortJob);
                    }
                    FinishSortJob("计划完成", false);
                }
                catch (Exception ex)
                {
                    FinishSortJob(ex.GetType().Name + "：" + ex.Message, true);
                }
            });
            return null;
        }

        /// <summary>记录 / 撤销「计划已核对」确认（落 setting；执行前必须已确认且零冲突）——返回 null 表示成功。</summary>
        private static string ConfirmSortPlan(SortConfirmDto dto)
        {
            if (dto == null || dto.planId <= 0)
            {
                return "未指定计划";
            }
            SortPlanRow plan = _hub.Core.SortPlanById(dto.planId);
            if (plan == null)
            {
                return "计划不存在（可能已被重新生成）";
            }
            if (dto.confirmed)
            {
                _hub.Core.SetSetting("sort_confirm_plan", plan.Id.ToString());
                Console.WriteLine("[整理] 计划 #" + plan.Id.ToString() + " 已核对确认");
            }
            else
            {
                _hub.Core.DeleteSetting("sort_confirm_plan");
                Console.WriteLine("[整理] 计划 #" + plan.Id.ToString() + " 确认已撤销");
            }
            return null;
        }

        /// <summary>启动执行——前置三重闸门（koikatsu 已关确认 · 计划已确认 · 零冲突），后台按快照逐条搬运；返回 null 表示已启动。</summary>
        private static string StartSortExec(SortExecStartDto dto)
        {
            if (dto == null || dto.planId <= 0)
            {
                return "未指定计划";
            }
            if (!dto.koikatsuClosed)
            {
                return "请先确认已关闭 koikatsu.exe";
            }
            SortPlanRow plan = _hub.Core.SortPlanById(dto.planId);
            if (plan == null)
            {
                return "计划不存在（可能已被重新生成）";
            }
            if (plan.State != "ready")
            {
                return "计划不可执行（状态：" + plan.State + "）";
            }
            if (_hub.Core.CountSortPlanItems(plan.Id, SortOrganizer.StateConflict) > 0)
            {
                return "还有冲突未解决——先处理冲突并重新核对";
            }
            string confirmed = _hub.Core.GetSetting("sort_confirm_plan");
            if (confirmed != plan.Id.ToString())
            {
                return "计划尚未确认——先在预览里确认文件结构";
            }

            lock (SortJobLock)
            {
                if (_sortJob.Running)
                {
                    return "已有整理任务在运行";
                }
                _sortJob = new SortJobState
                {
                    Running = true,
                    Phase = "搬运",
                    Message = "准备搬运",
                    PlanId = plan.Id,
                    StartedAt = DateTime.Now.ToString("HH:mm:ss")
                };
            }

            long planId = plan.Id;
            Task.Run(() =>
            {
                try
                {
                    using (StoreHub hub = new StoreHub(_dbPath))
                    {
                        RootsConfig c = hub.Core.LoadRoots();
                        RootsRules.Normalize(c);
                        hub.EnsureMigrated(c);
                        SortOrganizer.ExecutePlan(hub, c, planId, _sortJob);
                    }
                    bool stopped;
                    lock (SortJobLock)
                    {
                        stopped = _sortJob.StopRequested;
                    }
                    FinishSortJob(stopped ? "已停止" : "整理完成", false);
                }
                catch (Exception ex)
                {
                    FinishSortJob(ex.GetType().Name + "：" + ex.Message, true);
                }
            });
            return null;
        }

        /// <summary>读整理计划视图——计划头 + 条目 + 统计；recheck 为真时先做一次快照核对（已就位的行标 √ 并置底）。</summary>
        private static SortPlanView LoadSortPlanView(bool recheck)
        {
            SortPlanView view = new SortPlanView();
            SortPlanRow plan = _hub.Core.LatestSortPlan();
            if (plan == null)
            {
                view.ok = false;
                view.error = "还没有生成计划";
                return view;
            }
            if (recheck && plan.State == "ready")
            {
                long conflicts = SortOrganizer.RecheckPlan(_hub, plan.Id);
                _hub.Core.FinishSortPlan(plan.Id, plan.State, plan.ItemCount, conflicts, plan.Note);
                plan = _hub.Core.SortPlanById(plan.Id);
                view.rechecked = true;
            }
            view.ok = true;
            view.planId = plan.Id;
            view.scope = plan.Scope;
            view.createdAt = plan.CreatedAt;
            view.state = plan.State;
            view.note = plan.Note;
            view.itemCount = plan.ItemCount;
            string confirmed = _hub.Core.GetSetting("sort_confirm_plan");
            view.confirmed = confirmed == plan.Id.ToString();
            List<SortPlanItemRow> rows = _hub.Core.QuerySortPlanItems(plan.Id);
            foreach (SortPlanItemRow r in rows)
            {
                SortPlanItemView v = new SortPlanItemView();
                v.seq = r.Seq;
                v.lib = r.Lib;
                v.tier = r.Tier;
                v.rootPath = r.RootPath;
                v.guid = r.Guid;
                v.author = r.Author;
                v.folder = r.Folder;
                v.fileName = Path.GetFileName(r.SrcPath);
                v.srcPath = r.SrcPath;
                v.destPath = r.DestPath;
                v.size = r.Size;
                v.state = r.State;
                v.note = r.Note;
                view.items.Add(v);
                if (r.State == SortOrganizer.StateMoved)
                {
                    view.movedCount = view.movedCount + 1;
                }
                else if (r.State == SortOrganizer.StateConflict)
                {
                    view.conflictCount = view.conflictCount + 1;
                }
                else if (r.State == SortOrganizer.StateFailed)
                {
                    view.failedCount = view.failedCount + 1;
                }
                else if (r.State == SortOrganizer.StateSkipped)
                {
                    view.skippedCount = view.skippedCount + 1;
                }
                else
                {
                    view.pendingCount = view.pendingCount + 1;
                }
            }
            return view;
        }

        /// <summary>一份 mod 副本的展示行——重复副本窗口与整理冲突小窗**共用同一装配**（manifest 实时读 · 创建时间实时读 · MD5 由调用方统一补齐后传入）。</summary>
        private static void FillModCopy(ModCopyView v, ModFileRecord f, Dictionary<string, string> hashes)
        {
            v.tier = f.Tier;
            v.tierName = Tier.Name(f.Tier);
            v.rootPath = f.RootPath;
            v.filePath = f.FilePath;
            v.fileName = f.FileName;
            v.size = f.Size;
            v.mtime = f.Mtime;
            ModCopyInfo info = ModCopyReader.Read(f.FilePath);
            v.version = info.Version;
            v.author = info.Author;
            v.name = info.Name;
            v.error = info.Error;
            v.ctime = info.Ctime;
            v.isOld = RootsRules.IsOldFileName(f.FileName);
            if (hashes != null)
            {
                string md5 = null;
                hashes.TryGetValue(f.FilePath, out md5);
                v.md5 = md5;
            }
        }

        /// <summary>整理冲突的一组候选视图——与重复副本窗口共用同一份副本装配与同一档 MD5 档案，另加引用卡片（前三张 + 总数）与旧版登记。</summary>
        private static SortConflictView BuildSortConflictView(long planId, long seq)
        {
            SortConflictView view = new SortConflictView();
            view.planId = planId;
            view.seq = seq;
            string error = null;
            SortConflictGroup group = SortOrganizer.ConflictGroup(_hub, planId, seq, out error);
            if (error != null)
            {
                view.ok = false;
                view.error = error;
                return view;
            }
            view.ok = true;
            view.guid = group.Guid;
            view.destPath = group.DestPath;

            // [段1] 候选 → mod 副本记录（与重复副本同一装配面）
            List<ModFileRecord> recs = new List<ModFileRecord>();
            List<SortConflictCandidate> cands = new List<SortConflictCandidate>();
            foreach (SortConflictCandidate c in group.Items)
            {
                ModFileRecord r = new ModFileRecord();
                r.Guid = group.Guid;
                r.Tier = c.Tier;
                r.RootPath = c.RootPath;
                r.FilePath = c.FilePath;
                r.FileName = c.FileName;
                r.Size = c.Size;
                r.Mtime = c.Mtime;
                recs.Add(r);
                cands.Add(c);
            }

            // [段2] MD5 走与重复副本同一档档案（算过就忽略）——算不出的明细上抛，不静默
            int computed = 0;
            List<string> hashErrors = new List<string>();
            Dictionary<string, string> hashes = _hub.FillHashes(recs, out computed, out hashErrors);
            foreach (string e in hashErrors)
            {
                Console.WriteLine("[整理] MD5 算不出：" + e);
                view.hashErrors.Add(e);
            }

            // [段3] 逐份成行（含计划条目键与状态）+ 组头信息
            int mainCount = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                SortConflictItemView item = new SortConflictItemView();
                FillModCopy(item, recs[i], hashes);
                item.seq = cands[i].Seq;
                item.state = cands[i].State;
                item.note = cands[i].Note;
                if (item.tier == Tier.Main)
                {
                    mainCount = mainCount + 1;
                }
                if (view.name == null && item.name != null)
                {
                    view.name = item.name;
                }
                view.items.Add(item);
            }
            view.mainCount = mainCount;

            // [段4] 引用卡片（前三张 + 总数）与旧版登记——与重复副本窗口同一数据源
            string guid = group.Guid == null ? "" : group.Guid;
            List<string> guids = new List<string>();
            guids.Add(guid);
            Dictionary<string, DupCardRefs> refs = _hub.QueryDupCardRefs(LoadConfig(), guids, 3);
            DupCardRefs mine = null;
            refs.TryGetValue(guid, out mine);
            if (mine != null)
            {
                foreach (CardRow c in mine.Top)
                {
                    view.cards.Add(new { id = c.Id, lib = c.Lib, hasThumb = c.HasThumb, fileName = c.FileName });
                }
                view.cardTotal = mine.Total;
            }
            foreach (ModOldRecord o in _hub.Core.ListModOld())
            {
                view.olds.Add(o);
            }
            return view;
        }

        /// <summary>解决一条整理冲突——面板侧只做「任务在跑就拒」与加载配置，判定与搬运全在 Core（与 CLI 同一实现）。</summary>
        private static string KeepSortConflict(long planId, long seq, out string detail)
        {
            detail = null;
            lock (SortJobLock)
            {
                if (_sortJob.Running)
                {
                    return "整理任务正在跑——先点「停止」或等它结束，再处理冲突";
                }
            }
            return SortOrganizer.ResolveConflict(_hub, LoadConfig(), planId, seq, out detail);
        }

        /// <summary>收尾「按作者整理」任务（running 置否 + 结束时刻 + 消息；error 为真时同时记入失败明细并落控制台）。</summary>
        private static void FinishSortJob(string message, bool error)
        {
            string line;
            lock (SortJobLock)
            {
                _sortJob.Running = false;
                _sortJob.Current = "";
                _sortJob.FinishedAt = DateTime.Now.ToString("HH:mm:ss");
                _sortJob.Message = message;
                if (error && _sortJob.Errors.Count < 50)
                {
                    _sortJob.Errors.Add(message);
                }
                line = "[整理] " + message + " · 阶段 " + _sortJob.Phase
                    + " · 就位 " + _sortJob.Moved.ToString()
                    + " · 跳过 " + _sortJob.Skipped.ToString()
                    + " · 失败 " + _sortJob.Failed.ToString()
                    + " · 冲突 " + _sortJob.Conflicts.ToString();
            }
            Console.WriteLine(line);
        }

        /// <summary>收尾搬运任务（running 置否 + 结束时刻 + 消息；error 为真时同时记入失败明细并落控制台）。</summary>
        private static void FinishMoveJob(string message, bool error)
        {
            string line;
            lock (MoveJobLock)
            {
                _moveJob.Running = false;
                _moveJob.Current = null;
                _moveJob.FinishedAt = DateTime.Now.ToString("HH:mm:ss");
                _moveJob.Message = message;
                if (error && _moveJob.Errors.Count < 50)
                {
                    _moveJob.Errors.Add(message);
                }
                line = "[搬运] " + message + " · 成功 " + _moveJob.Moved + " · 跳过 " + _moveJob.Skipped + " · 失败 " + _moveJob.Failed;
            }
            Console.WriteLine(line);
        }

        private static string StartScan(string target, bool force, string rootPath, string orderText, string onText)
        {
            lock (ScanLock)
            {
                if (_scan.Running)
                {
                    return "已有扫描任务在运行";
                }
                _scan = new ScanState
                {
                    Running = true,
                    Target = target,
                    Message = "开始扫描",
                    StartedAt = DateTime.Now.ToString("HH:mm:ss")
                };
            }

            Task.Run(() =>
            {
                try
                {
                    using (StoreHub hub = new StoreHub(_dbPath))
                    {
                        RootsConfig cfg = hub.Core.LoadRoots();
                        RootsRules.Normalize(cfg);
                        hub.EnsureMigrated(cfg);
                        int thumbWidth = int.Parse(hub.Core.GetSetting("thumbWidth") ?? "256");
                        int quality = int.Parse(hub.Core.GetSetting("quality") ?? "82");
                        // 扫描计划——请求带了顺序 / 勾选就落盘（全局一套，追加库扫描同样适用），否则读已保存的
                        string order = orderText;
                        string on = onText;
                        if (!string.IsNullOrEmpty(order) || !string.IsNullOrEmpty(on))
                        {
                            hub.Core.SetSetting(ScanPlanCatalog.OrderKey, order ?? "");
                            hub.Core.SetSetting(ScanPlanCatalog.OnKey, on ?? "");
                        }
                        else
                        {
                            order = hub.Core.GetSetting(ScanPlanCatalog.OrderKey);
                            on = hub.Core.GetSetting(ScanPlanCatalog.OnKey);
                        }
                        ScanPlan plan = ScanPlanCatalog.Load(order, on);
                        Action<string> log = m => SetMessage(m);
                        bool isMods = target == "mods";

                        // [段1] 单库更新（面板「更新本库」）——只扫这一条；离线库不参与扫描
                        RootEntry only = null;
                        if (!string.IsNullOrWhiteSpace(rootPath))
                        {
                            List<RootEntry> list = isMods ? cfg.ModRootsOrdered() : cfg.CardRootsOrdered();
                            foreach (RootEntry e in list)
                            {
                                if (string.Equals((e.path ?? "").Trim(), rootPath.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    only = e;
                                    break;
                                }
                            }
                            if (only == null)
                            {
                                throw new InvalidOperationException("该库根不在已保存的配置里——先在「库根设置」点「保存配置」，再更新本库：" + rootPath);
                            }
                            if (only.offline)
                            {
                                throw new InvalidOperationException("该库已离线——先上线再更新：" + only.path);
                            }
                        }

                        // [段2] 扫描范围——main = 预置条目（mod 主库 + 缓存库槽位 + 卡片 3 条锁定主库）· extra = 使用者添加的库根（不分卡片与 mod，离线库跳过）
                        ScanResult r = new ScanResult();
                        if (target == "main" || target == "extra")
                        {
                            ScanScope scope = target == "main" ? ScanScope.Preset : ScanScope.Extra;
                            lock (ScanLock)
                            {
                                _scan.RootCount = cfg.ModRootsOrdered().Count + cfg.CardRootsOrdered().Count;
                            }
                            r.Merge(Scanner.ScanAll(hub, cfg, null, scope, force, thumbWidth, quality, plan, log));
                        }
                        else
                        {
                            List<RootEntry> roots = isMods ? cfg.ModRootsOrdered() : cfg.CardRootsOrdered();
                            if (roots.Count == 0)
                            {
                                throw new InvalidOperationException(isMods ? "未设置 mod 库根" : "未设置卡片库根");
                            }
                            lock (ScanLock)
                            {
                                _scan.RootCount = only == null ? roots.Count : 1;
                            }
                            if (isMods)
                            {
                                r = Scanner.ScanMods(hub, cfg, only, ScanScope.All, force, plan, log);
                            }
                            else
                            {
                                r = Scanner.ScanCards(hub, cfg, only, ScanScope.All, force, thumbWidth, quality, plan, log);
                            }
                        }

                        lock (ScanLock)
                        {
                            _scan.Seen = r.Seen;
                            _scan.Added = r.Added;
                            _scan.Skipped = r.Skipped;
                            _scan.NonCard = r.NonCard;
                            _scan.NonMod = r.NonMod;
                            _scan.Failed = r.Failed;
                            _scan.Refs = r.RefEntries;
                            _scan.ThumbBytes = r.ThumbBytes;
                            _scan.NamesRead = r.NamesRead;
                            _scan.NamesFilled = r.NamesFilled;
                            _scan.TypesFixed = r.TypesFixed;
                            _scan.TimelineRead = r.TimelineRead;
                            _scan.PluginDlls = r.PluginDlls;
                            _scan.Plugins = r.Plugins;
                            _scan.PluginConfigs = r.PluginConfigs;
                            _scan.Removed = r.Removed;
                            _scan.StepIndex = r.StepIndex;
                            _scan.StepCount = r.StepCount;
                            _scan.StepName = r.StepName;
                            _scan.StepDone = r.StepDone;
                            _scan.StepTotal = r.StepTotal;
                            _scan.Errors.AddRange(r.Errors);
                            _scan.Message = "扫描完成，用时 " + r.Elapsed.TotalSeconds.ToString("F1") + " 秒";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (ScanLock)
                    {
                        _scan.Message = "扫描失败：" + ex.Message;
                        _scan.Errors.Add(ex.GetType().Name + ": " + ex.Message);
                    }
                }
                finally
                {
                    lock (ScanLock)
                    {
                        _scan.Running = false;
                        _scan.FinishedAt = DateTime.Now.ToString("HH:mm:ss");
                    }
                    // 扫描改变缺失集合（mod 库 / 卡片库都算）——缓存失效，下次打开缺失清单重建
                    _missingCache = null;
                }
            });

            return null;
        }

        /// <summary>读库里的分项分析（存在且 size + mtime 未失效 → 返回 JSON 元素；否则 null）——卡片分析窗优先读库，避免每次重算。</summary>
        private static object LoadCachedAnalysis(StoreHub hub, int lib, long id, string kind, string path)
        {
            CardAnalysisRow row = hub.LoadCardAnalysis(lib, id, kind);
            if (row == null || string.IsNullOrEmpty(row.Data))
            {
                return null;
            }
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != row.Size || !string.Equals(Store.StampOf(fi), row.Mtime, StringComparison.Ordinal))
                {
                    return null;
                }
                JsonDocument doc = JsonDocument.Parse(row.Data);
                return doc.RootElement.Clone();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetMessage(string message)
        {
            lock (ScanLock)
            {
                _scan.Message = message;
                if (message.StartsWith("级别 ", StringComparison.Ordinal))
                {
                    int i = 3;
                    int tier = 0;
                    while (i < message.Length && message[i] >= '0' && message[i] <= '9')
                    {
                        tier = tier * 10 + (message[i] - '0');
                        i++;
                    }
                    _scan.CurrentTier = tier;
                    _scan.CurrentRoot = message;
                    _scan.RootIndex++;
                }
                if (message.StartsWith("步骤 ", StringComparison.Ordinal))
                {
                    int slashAt = message.IndexOf('/');
                    int colonAt = message.IndexOf('：');
                    if (slashAt > 3 && colonAt > slashAt)
                    {
                        int stepIndex;
                        int stepCount;
                        if (int.TryParse(message.Substring(3, slashAt - 3), out stepIndex))
                        {
                            _scan.StepIndex = stepIndex;
                        }
                        if (int.TryParse(message.Substring(slashAt + 1, colonAt - slashAt - 1), out stepCount))
                        {
                            _scan.StepCount = stepCount;
                        }
                        _scan.StepName = message.Substring(colonAt + 1);
                    }
                }
                // 插件库进度行（「  插件库 done/total」）——走插件段自己的分母，不污染卡片 / mod 的累计计数
                System.Text.RegularExpressions.Match pm = PluginProgressRx.Match(message);
                if (pm.Success)
                {
                    int pdone;
                    int ptotal;
                    if (int.TryParse(pm.Groups[1].Value, out pdone))
                    {
                        _scan.StepDone = pdone;
                        _scan.PluginDone = pdone;
                    }
                    if (int.TryParse(pm.Groups[2].Value, out ptotal))
                    {
                        _scan.StepTotal = ptotal;
                        _scan.PluginTotal = ptotal;
                    }
                }
                else
                {
                    System.Text.RegularExpressions.Match m = ProgressRx.Match(message);
                    if (m.Success)
                    {
                        int left;
                        int right;
                        if (int.TryParse(m.Groups[1].Value, out left))
                        {
                            _scan.Added = left;
                            _scan.StepDone = left;
                        }
                        if (int.TryParse(m.Groups[2].Value, out right))
                        {
                            _scan.Seen = right;
                        }
                    }
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex ProgressRx =
            new System.Text.RegularExpressions.Regex(@"(\d+)\s*/\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>插件库进度行（「  插件库 done/total」）——插件段有独立分母，须与卡片 / mod 的累计进度行分开解析。</summary>
        private static readonly System.Text.RegularExpressions.Regex PluginProgressRx =
            new System.Text.RegularExpressions.Regex(@"^\s*插件库\s+(\d+)\s*/\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string IndexHtml()
        {
            if (_indexHtml == null)
            {
                var asm = typeof(WebApp).Assembly;
                using (Stream s = asm.GetManifestResourceStream("KKManager.Web.wwwroot.index.html"))
                {
                    if (s == null)
                    {
                        return "<h1>前端资源缺失</h1>";
                    }
                    using (var reader = new StreamReader(s, Encoding.UTF8))
                    {
                        _indexHtml = reader.ReadToEnd();
                    }
                }
            }
            return _indexHtml;
        }
        /// <summary>卡片编辑留档目录（软件内部——原版副本放这里，与主库同级）。</summary>
        private static string CardArchiveDir()
        {
            string dir = Path.GetDirectoryName(_dbPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = AppContext.BaseDirectory;
            }
            return Path.Combine(dir, "card_archive");
        }
        /// <summary>路径是否落在编辑留档目录内——留档文件可定位（软件自己的目录，不占库根白名单）。</summary>
        private static bool IsUnderArchive(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(CardArchiveDir()) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
        /// <summary>组装一条留档记录（关系：卡片 ↔ 原版副本）。</summary>
        private static CardEditRecord BuildEditRecord(string path, int lib, FileInfo before, CardEditResult r)
        {
            CardEditRecord rec = new CardEditRecord();
            rec.CardPath = path;
            rec.CardName = Path.GetFileName(path);
            rec.Lib = lib;
            rec.ArchivedFile = r.ArchivedFile;
            rec.ArchivedSize = r.ArchivedSize;
            rec.ArchivedMtime = before.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            rec.Changes = string.Join(" · ", r.Changes);
            rec.EditedAt = Store.Now();
            return rec;
        }
    }

    /// <summary>扫描动作委托。</summary>
    internal delegate ScanResult ScanAction();

    /// <summary>扫描请求体。</summary>
    public class ScanRequestDto
    {
        /// <summary>扫描目标（mods / cards）。</summary>
        public string target { get; set; }

        /// <summary>是否忽略增量强制重扫。</summary>
        public bool force { get; set; }

        /// <summary>只扫这一条库根（面板「更新本库」；空 = 按 target 全量扫）。</summary>
        public string root { get; set; }

        /// <summary>扫描步骤顺序（逗号分隔 id；空 = 用已保存的）。</summary>
        public string order { get; set; }

        /// <summary>扫描步骤勾选（逗号分隔 id；空 = 用已保存的）。</summary>
        public string on { get; set; }
    }

    /// <summary>关闭待办的请求体。</summary>
    public class TodoCloseDto
    {
        /// <summary>待办条目 id。</summary>
        public long id { get; set; }
    }

    /// <summary>新建待办的请求体——cardId ≤ 0 时按手输待办落盘（纯文本）。</summary>
    public class TodoAddDto
    {
        /// <summary>待办正文（手输与卡片待办共用；去首尾空白后落盘，空 / 超长在服务端拒绝）。</summary>
        public string text { get; set; }

        /// <summary>卡片 id（> 0 = 建卡片待办——卡片路径由服务端按 id 取，前端不拼路径）。</summary>
        public long cardId { get; set; }

        /// <summary>卡片所在库位（与 cardId 同用；0 = 主库文件）。</summary>
        public int lib { get; set; }
    }

    /// <summary>手动设离线 / 点击上线的请求体。</summary>
    public class RootOfflineDto
    {
        /// <summary>库别（mods / cards）。</summary>
        public string kind { get; set; }

        /// <summary>库根路径。</summary>
        public string path { get; set; }

        /// <summary>true = 设为离线；false = 点击上线。</summary>
        public bool offline { get; set; }
    }

    /// <summary>彻底删除数据的请求体。</summary>
    public class RootPurgeDto
    {
        /// <summary>库别（mods / cards）。</summary>
        public string kind { get; set; }

        /// <summary>库根路径。</summary>
        public string path { get; set; }
    }

    /// <summary>在资源管理器中打开文件的请求体。</summary>
    public class RevealRequestDto
    {
        /// <summary>要选中的文件绝对路径（须为受管的 mod 副本）。</summary>
        public string path { get; set; }
    }

    /// <summary>编辑授权请求体。</summary>
    public class EditAuthDto
    {
        /// <summary>是否授权（true 开启 / false 撤销）。</summary>
        public bool granted { get; set; }
    }

    /// <summary>插件配置值写回请求体（插件分析窗「当前值」改完点确认保存）。</summary>
    public class PluginConfigSaveDto
    {
        /// <summary>cfg 绝对路径（须落在受管的插件库根内）。</summary>
        public string path { get; set; }

        /// <summary>分节名（文件开头无分节的键给「(无分节)」）。</summary>
        public string section { get; set; }

        /// <summary>配置键。</summary>
        public string key { get; set; }

        /// <summary>新值（空串 = 改成空值）。</summary>
        public string value { get; set; }
    }

    /// <summary>卡片字段编辑请求体（null = 不改这一项；空字符串 = 改成空）。</summary>
    public class CardEditDto
    {
        /// <summary>库序号（0 = 主库）。</summary>
        public int lib { get; set; }

        /// <summary>新姓。</summary>
        public string lastName { get; set; }

        /// <summary>新名。</summary>
        public string firstName { get; set; }

        /// <summary>新爱称。</summary>
        public string nickName { get; set; }

        /// <summary>新性格 ID。</summary>
        public int? personality { get; set; }

        /// <summary>新敏感带 ID。</summary>
        public int? weakPoint { get; set; }

        /// <summary>五项「是否接受」的新值（null 元素 = 不改该项；顺序 kiss / aibu / anal / massage / notCondom）。</summary>
        public bool?[] denial { get; set; }
    }

    /// <summary>查看 mod 组成的请求体。</summary>
    public class CompositionRequestDto
    {
        /// <summary>mod guid。</summary>
        public string guid { get; set; }

        /// <summary>强制重读容器（忽略已有档案——「重新读取」按钮用）。</summary>
        public bool force { get; set; }
    }

    /// <summary>解析 unity3d 条目的请求体。</summary>
    public class U3dRequestDto
    {
        /// <summary>mod guid。</summary>
        public string guid { get; set; }

        /// <summary>容器内条目路径（.unity3d）。</summary>
        public string path { get; set; }

        /// <summary>强制重新解析（忽略已有档案）。</summary>
        public bool force { get; set; }
    }

    /// <summary>搬运请求体。</summary>
    public class MoveRequestDto
    {
        /// <summary>要搬运的 mod guid 列表。</summary>
        public List<string> guids { get; set; }

        /// <summary>源级别（默认 2 缓存库）。</summary>
        public int fromTier { get; set; }

        /// <summary>目标级别（默认 1 主库）。</summary>
        public int toTier { get; set; }
    }

    /// <summary>旧版操作请求体（判为旧版 / 换用此版本 / 正名搬入主库共用）。</summary>
    public class ModOldRequestDto
    {
        /// <summary>mod guid。</summary>
        public string guid { get; set; }

        /// <summary>目标副本的文件绝对路径。</summary>
        public string path { get; set; }
    }

    /// <summary>「未引用 mod 移到缓存库」启动请求体。</summary>
    public class MoveStartDto
    {
        /// <summary>只搬这些 guid（空 / 缺省 = 全部「主库未引用」）；服务端一律按口径复核，传入清单只作收窄。</summary>
        public List<string> guids { get; set; }
    }

    /// <summary>搬运结果。</summary>
    public class MoveResult
    {
        /// <summary>成功搬运明细。</summary>
        public List<string> Moved { get; } = new List<string>();

        /// <summary>跳过明细。</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>错误明细。</summary>
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>按作者整理——可勾选的库根一行。</summary>
    public class SortRootView
    {
        /// <summary>库根路径。</summary>
        public string path { get; set; }

        /// <summary>级别（1 主库 / 2 缓存库 / 3 冷冻库）。</summary>
        public int tier { get; set; }

        /// <summary>级别名。</summary>
        public string tierName { get; set; }

        /// <summary>是否只读（只读库不参与整理）。</summary>
        public bool readOnly { get; set; }

        /// <summary>是否离线（离线库不参与整理）。</summary>
        public bool offline { get; set; }

        /// <summary>是否预置锁定条目。</summary>
        public bool locked { get; set; }

        /// <summary>是否可参与整理（非只读且非离线）。</summary>
        public bool canSort { get; set; }
    }

    /// <summary>按作者整理计划的一条条目视图（面板用——不携带 mtime / 库序号等内部字段）。</summary>
    public class SortPlanItemView
    {
        /// <summary>展示顺序（也是执行时的定位键）。</summary>
        public long seq { get; set; }

        /// <summary>库序号（0 = 主库）。</summary>
        public int lib { get; set; }

        /// <summary>级别。</summary>
        public int tier { get; set; }

        /// <summary>所在库根。</summary>
        public string rootPath { get; set; }

        /// <summary>mod guid。</summary>
        public string guid { get; set; }

        /// <summary>作者（空 = 未标注）。</summary>
        public string author { get; set; }

        /// <summary>目标文件夹名。</summary>
        public string folder { get; set; }

        /// <summary>文件名。</summary>
        public string fileName { get; set; }

        /// <summary>现路径。</summary>
        public string srcPath { get; set; }

        /// <summary>目标路径。</summary>
        public string destPath { get; set; }

        /// <summary>字节数。</summary>
        public long size { get; set; }

        /// <summary>状态（pending / conflict / moved / failed / skipped）。</summary>
        public string state { get; set; }

        /// <summary>备注（冲突或失败原因）。</summary>
        public string note { get; set; }
    }

    /// <summary>按作者整理计划——整份视图（计划头 + 条目 + 统计）。</summary>
    public class SortPlanView
    {
        /// <summary>是否有计划可读。</summary>
        public bool ok { get; set; }

        /// <summary>不可读时的原因。</summary>
        public string error { get; set; }

        /// <summary>本次读取是否做过快照核对。</summary>
        public bool rechecked { get; set; }

        /// <summary>计划 id。</summary>
        public long planId { get; set; }

        /// <summary>范围（库根路径，换行分隔）。</summary>
        public string scope { get; set; }

        /// <summary>生成时刻。</summary>
        public string createdAt { get; set; }

        /// <summary>计划状态（building / ready / outdated）。</summary>
        public string state { get; set; }

        /// <summary>计划备注。</summary>
        public string note { get; set; }

        /// <summary>条目总数。</summary>
        public long itemCount { get; set; }

        /// <summary>冲突数（快照核对后的现状——必须为零才能执行）。</summary>
        public long conflictCount { get; set; }

        /// <summary>已就位数（√）。</summary>
        public long movedCount { get; set; }

        /// <summary>待搬数。</summary>
        public long pendingCount { get; set; }

        /// <summary>失败数。</summary>
        public long failedCount { get; set; }

        /// <summary>跳过数。</summary>
        public long skippedCount { get; set; }

        /// <summary>使用者是否已确认结构。</summary>
        public bool confirmed { get; set; }

        /// <summary>条目（展示顺序）。</summary>
        public List<SortPlanItemView> items { get; } = new List<SortPlanItemView>();
    }

    /// <summary>按作者整理——生成计划请求体。</summary>
    public class SortPlanStartDto
    {
        /// <summary>参与整理的库根路径（空 / 缺省 = 全部可整理的 mod 库根）。</summary>
        public List<string> roots { get; set; }
    }

    /// <summary>按作者整理——计划确认请求体。</summary>
    public class SortConfirmDto
    {
        /// <summary>计划 id。</summary>
        public long planId { get; set; }

        /// <summary>true = 已核对结构无误；false = 撤销确认。</summary>
        public bool confirmed { get; set; }
    }

    /// <summary>一份 mod 副本的展示行——重复副本窗口与「按作者整理」冲突小窗**共用同一形状**（字段加一处，两窗一起变）。</summary>
    public class ModCopyView
    {
        /// <summary>级别（1 主库 / 2 缓存库 / 3 冷冻库）。</summary>
        public int tier { get; set; }

        /// <summary>级别名。</summary>
        public string tierName { get; set; }

        /// <summary>所在库根。</summary>
        public string rootPath { get; set; }

        /// <summary>文件绝对路径。</summary>
        public string filePath { get; set; }

        /// <summary>文件名。</summary>
        public string fileName { get; set; }

        /// <summary>字节数。</summary>
        public long size { get; set; }

        /// <summary>修改时间。</summary>
        public string mtime { get; set; }

        /// <summary>创建时间（打开时实时读文件系统；读不到为空串）。</summary>
        public string ctime { get; set; }

        /// <summary>内容 MD5（档案命中即复用，算过就忽略；算不出为 null）。</summary>
        public string md5 { get; set; }

        /// <summary>manifest 版本（打开时实时读）。</summary>
        public string version { get; set; }

        /// <summary>manifest 作者（打开时实时读）。</summary>
        public string author { get; set; }

        /// <summary>manifest 名称。</summary>
        public string name { get; set; }

        /// <summary>文件名是否带 .old 段。</summary>
        public bool isOld { get; set; }

        /// <summary>manifest 读不到时的原因（空 = 读到了）。</summary>
        public string error { get; set; }
    }

    /// <summary>「按作者整理」冲突小窗里的一份候选——共用副本形状（ModCopyView），另加计划条目键与状态。</summary>
    public class SortConflictItemView : ModCopyView
    {
        /// <summary>计划条目的展示顺序（也是选定保留份的键）。</summary>
        public long seq { get; set; }

        /// <summary>条目状态。</summary>
        public string state { get; set; }

        /// <summary>条目备注。</summary>
        public string note { get; set; }
    }

    /// <summary>按作者整理——一条冲突的候选视图（同目标路径的几份副本 + 引用卡片 + 旧版登记——与重复副本窗口同一份参考数据）。</summary>
    public class SortConflictView
    {
        /// <summary>是否读到。</summary>
        public bool ok { get; set; }

        /// <summary>读不到的原因。</summary>
        public string error { get; set; }

        /// <summary>计划 id。</summary>
        public long planId { get; set; }

        /// <summary>发起定位的条目 seq。</summary>
        public long seq { get; set; }

        /// <summary>mod guid。</summary>
        public string guid { get; set; }

        /// <summary>manifest 名称（组标题行用）。</summary>
        public string name { get; set; }

        /// <summary>主库份数（组标题行用）。</summary>
        public int mainCount { get; set; }

        /// <summary>撞车点——目标路径。</summary>
        public string destPath { get; set; }

        /// <summary>候选副本。</summary>
        public List<SortConflictItemView> items { get; } = new List<SortConflictItemView>();

        /// <summary>引用这个 guid 的卡片（前三张——缩略图区，与重复副本同一数据源）。</summary>
        public List<object> cards { get; } = new List<object>();

        /// <summary>引用卡片总数。</summary>
        public long cardTotal { get; set; }

        /// <summary>旧版登记（行内「已登记」标记用）。</summary>
        public List<ModOldRecord> olds { get; } = new List<ModOldRecord>();

        /// <summary>MD5 算不出的明细（失败可见）。</summary>
        public List<string> hashErrors { get; } = new List<string>();
    }

    /// <summary>按作者整理——保留某一份的请求体。</summary>
    public class SortKeepDto
    {
        /// <summary>计划 id。</summary>
        public long planId { get; set; }

        /// <summary>保留这一条的 seq。</summary>
        public long seq { get; set; }
    }

    /// <summary>按作者整理——执行请求体。</summary>
    public class SortExecStartDto
    {
        /// <summary>计划 id。</summary>
        public long planId { get; set; }

        /// <summary>使用者已确认关闭 koikatsu.exe（服务端据此放行）。</summary>
        public bool koikatsuClosed { get; set; }
    }

}
