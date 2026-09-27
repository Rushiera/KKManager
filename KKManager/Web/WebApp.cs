using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        /// <summary>失败数。</summary>
        public int Failed { get; set; }

        /// <summary>引用条目数。</summary>
        public long Refs { get; set; }

        /// <summary>缩略图字节数。</summary>
        public long ThumbBytes { get; set; }

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
        private static ScanState _scan = new ScanState();
        private static readonly object ScanLock = new object();
        /// <summary>「未引用 mod 移到缓存库」后台任务状态（含最近一次结果）。</summary>
        private static MoveJobState _moveJob = new MoveJobState();
        /// <summary>搬运任务状态的读写锁。</summary>
        private static readonly object MoveJobLock = new object();
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

        /// <summary>启动本地服务（阻塞）。</summary>
        public static int Run(string dbPath, int port, bool openBrowser)
        {
            _dbPath = dbPath;
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
        /// <summary>内嵌图片缩略图缓存（键 = 路径 + 偏移 + 长度 + 宽度）——同一张图重复请求直接命中。</summary>
        private static readonly Dictionary<string, byte[]> ThumbCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
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
                string error = StartScan(target, force, root);
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
                    failed = s.Failed,
                    refs = s.Refs,
                    thumbMB = Math.Round(s.ThumbBytes / 1024.0 / 1024.0, 1),
                    startedAt = s.StartedAt,
                    finishedAt = s.FinishedAt,
                    errors = s.Errors
                });
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
                    modRoots = RootsRules.RecommendModRoots(root),
                    cardRoots = RootsRules.RecommendCardRoots(root)
                });
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

            app.MapGet("/api/cards", (int page, int size, string filter, string q, string folder, string root) =>
            {
                if (size <= 0)
                {
                    size = NoLimit;
                }
                if (page <= 0)
                {
                    page = 1;
                }
                return Results.Json(_hub.QueryCards(LoadConfig(), page, size, filter, q, folder, root));
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
                if (st.Error == null && st.ImageEnd > 0)
                {
                    detail = CardDetail.Parse(path, st.ImageEnd);
                }
                return Results.Json(new { ok = st.Error == null, error = st.Error, structure = st, detail = detail });
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
                // 防叠：上一个选择框未关闭时不再弹新的（满屏看不见的模态框是最坏情况）
                if (System.Threading.Interlocked.CompareExchange(ref BrowseActive, 1, 0) != 0)
                {
                    Console.WriteLine("[浏览] 拒绝——上一个选择框仍未关闭");
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
                        Console.WriteLine("[浏览] 弹框线程启动——目标屏 " + work.Width + "x" + work.Height + " @" + work.X + "," + work.Y);
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
                            SetForegroundWindow(host.Handle);
                            Console.WriteLine("[浏览] 宿主窗已显示 @" + host.Location.X + "," + host.Location.Y);
                            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                dlg.Description = "选择文件夹";
                                dlg.ShowNewFolderButton = true;
                                System.Windows.Forms.DialogResult result = dlg.ShowDialog(host);
                                if (result == System.Windows.Forms.DialogResult.OK)
                                {
                                    picked = dlg.SelectedPath;
                                }
                                Console.WriteLine("[浏览] 对话框已关闭——" + (picked == null ? "取消 / 未选" : "已选 " + picked));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                        Console.WriteLine("[浏览] 异常——" + error);
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
                    Console.WriteLine("[浏览] 等待超时（300 秒）");
                    await context.Response.WriteAsync("{\"ok\":false,\"stage\":\"timeout\",\"error\":\"300 秒内未操作——框可能仍在屏幕上（按 Esc 关掉），之后可再点一次\"}");
                    return;
                }
                if (error != null)
                {
                    await context.Response.WriteAsync("{\"ok\":false,\"stage\":\"exception\",\"error\":\"" + EscapeJson(error) + "\"}");
                    return;
                }
                await context.Response.WriteAsync("{\"ok\":" + (picked != null ? "true" : "false") + ",\"stage\":\"done\",\"path\":\"" + EscapeJson(picked == null ? "" : picked) + "\"}");
            });

            // 重复副本——同 guid 多份文件；版本 / 作者实时读 manifest（人工筛旧版的判据一次看全）
            app.MapGet("/api/dup", () =>
            {
                RootsConfig cfg = LoadConfig();
                List<DupGroup> groups = _hub.ListDuplicateGroups(cfg);
                List<object> items = new List<object>();
                foreach (DupGroup g in groups)
                {
                    List<object> files = new List<object>();
                    foreach (ModFileRecord f in g.Files)
                    {
                        ModInfo info = ZipModReader.Parse(f.FilePath);
                        files.Add(new
                        {
                            tier = f.Tier,
                            tierName = Tier.Name(f.Tier),
                            rootPath = f.RootPath,
                            filePath = f.FilePath,
                            fileName = f.FileName,
                            size = f.Size,
                            mtime = f.Mtime,
                            version = info.Version,
                            author = info.Author,
                            name = info.Name,
                            isOld = RootsRules.IsOldFileName(f.FileName),
                            error = info.Error
                        });
                    }
                    items.Add(new { guid = g.Guid, mainCount = g.MainCount, files = files });
                }
                List<ModOldRecord> olds = new List<ModOldRecord>();
                foreach (ModOldRecord o in _hub.Core.ListModOld())
                {
                    olds.Add(o);
                }
                return Results.Json(new { total = items.Count, items = items, olds = olds });
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
                if (!StoreHub.IsUnderModRoot(LoadConfig(), dto.path) && !IsUnderArchive(dto.path))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"路径不在受管的 mod 库根或编辑留档目录内\"}");
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

            app.MapGet("/api/thumb/{id}", (long id, int lib) =>
            {
                byte[] data = _hub.LoadThumb(lib, id);
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
                detail = "文件操作失败：" + ex.GetType().Name + " " + ex.Message;
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

        private static string StartScan(string target, bool force, string rootPath)
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
                        bool isMods = target == "mods";
                        List<RootEntry> roots = isMods ? cfg.ModRootsOrdered() : cfg.CardRootsOrdered();
                        if (roots.Count == 0)
                        {
                            throw new InvalidOperationException(isMods ? "未设置 mod 库根" : "未设置卡片库根");
                        }
                        // 单库更新（面板「更新本库」）——只扫这一条；离线库不参与扫描
                        RootEntry only = null;
                        if (!string.IsNullOrWhiteSpace(rootPath))
                        {
                            foreach (RootEntry e in roots)
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
                        lock (ScanLock)
                        {
                            _scan.RootCount = only == null ? roots.Count : 1;
                        }

                        Action<string> log = m => SetMessage(m);
                        ScanAction scan;
                        if (isMods)
                        {
                            scan = () => Scanner.ScanMods(hub, cfg, only, force, log);
                        }
                        else
                        {
                            int thumbWidth = int.Parse(hub.Core.GetSetting("thumbWidth") ?? "256");
                            int quality = int.Parse(hub.Core.GetSetting("quality") ?? "82");
                            scan = () => Scanner.ScanCards(hub, cfg, only, force, thumbWidth, quality, log);
                        }

                        ScanResult r = scan();
                        lock (ScanLock)
                        {
                            _scan.Seen = r.Seen;
                            _scan.Added = r.Added;
                            _scan.Skipped = r.Skipped;
                            _scan.NonCard = r.NonCard;
                            _scan.Failed = r.Failed;
                            _scan.Refs = r.RefEntries;
                            _scan.ThumbBytes = r.ThumbBytes;
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
                System.Text.RegularExpressions.Match m = ProgressRx.Match(message);
                if (m.Success)
                {
                    int left;
                    int right;
                    if (int.TryParse(m.Groups[1].Value, out left) && left > _scan.Added)
                    {
                        _scan.Added = left;
                    }
                    if (int.TryParse(m.Groups[2].Value, out right))
                    {
                        _scan.Seen = right;
                    }
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex ProgressRx =
            new System.Text.RegularExpressions.Regex(@"(\d+)\s*/\s*(\d+)",
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
    }

    /// <summary>关闭待办的请求体。</summary>
    public class TodoCloseDto
    {
        /// <summary>待办条目 id。</summary>
        public long id { get; set; }
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
}
