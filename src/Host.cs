// FILE: jigu-app/src/Host.cs
// 稽古 · 原生宿主（C# 5 / .NET Framework 4.x WinForms + WebView2）
//
// 架构边界（本文件是唯一的"前端 ↔ 后端"接口）：
//   · 界面（HTML/CSS/JS）只负责收集输入与渲染结果。
//   · 检索、分词、IDF 加权、规则匹配、更新比对全部在本侧 C# 完成。
//   · 语料外置为 exe 同目录的 corpus.json，只在内存里保留倒排词表。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Jigu
{
    /// <summary>
    /// 诊断日志：始终尝试写 %TEMP%\Jigu\jigu-debug.log（带体积上限，最多 1MB）。
    /// 写不进去就静默放弃，绝不影响主流程。
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _tried;

        public static string Path_ { get { return _path; } }

        /// <summary>日志目录：优先调用方给的可写目录，其次 %TEMP%\Jigu，最后程序目录</summary>
        public static void Init(string dir)
        {
            lock (Gate)
            {
                if (_tried) return;
                _tried = true;
                _path = PickPath(dir);
                // 关键：日志路径确定后立刻落盘两行，证明诊断链路真的通了。
                // 用户机器上开启诊断时，这两行就是"日志有没有写成功"的第一现场。
                Raw("log start " + DateTime.Now.ToString("s") + " cwd=" + SafeCwd());
                Raw("log path  " + (_path == null ? "(none writable)" : _path));
            }
        }

        /// <summary>不经过 _tried 判断的直接落盘</summary>
        private static void Raw(string message)
        {
            try
            {
                if (_path == null) return;
                File.AppendAllText(_path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static string PickPath(string preferred)
        {
            string[] dirs = new string[3];
            dirs[0] = preferred;
            try { dirs[1] = AppDomain.CurrentDomain.BaseDirectory; } catch { }
            try { dirs[2] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Jigu"); } catch { }
            foreach (string d in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                    string p = System.IO.Path.Combine(d, "jigu-debug.log");
                    // 必须真正试写一次：目录存在不等于文件可写
                    File.AppendAllText(p, "", Encoding.UTF8);
                    if (File.Exists(p) && new FileInfo(p).Length > MaxBytes) File.Delete(p);
                    return p;
                }
                catch { }
            }
            return null;
        }

        private static string SafeCwd()
        {
            try { return Environment.CurrentDirectory; }
            catch { return "?"; }
        }

        public static void Write(string message)
        {
            string line = null;
            try { line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n"; }
            catch { return; }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    lock (Gate)
                    {
                        if (_path == null || !File.Exists(_path))
                        {
                            string again = PickPath(null);
                            if (again != null) _path = again;
                        }
                        if (_path == null) return;
                        if (new FileInfo(_path).Length >= MaxBytes)
                        {
                            // 到达上限就滚存一份，避免日志无限增长
                            try
                            {
                                string old = _path + ".1";
                                if (File.Exists(old)) File.Delete(old);
                                File.Move(_path, old);
                            }
                            catch { try { File.Delete(_path); } catch { } }
                        }
                        File.AppendAllText(_path, line, Encoding.UTF8);
                    }
                    return;
                }
                catch
                {
                    // 写失败：丢掉路径，下一轮重新挑一个可写的目录，绝不静默丢失后续日志
                    lock (Gate) { _path = null; }
                }
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("ERROR " + where + " -> " + (ex == null ? "null" : ex.GetType().Name + ": " + ex.Message));
            ErrorLog.Write(where, ex);
        }
    }

    /// <summary>
    /// 错误台账：无条件把完整异常（含 StackTrace / InnerException）写进程序目录的 error.log。
    /// 用户机器上不开 JIGU_DEBUG 也能拿到可诊断的现场。
    /// </summary>
    internal static class ErrorLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _broadcast;

        public static void Bind(string dir)
        {
            // 程序可能装在只读目录（Program Files），所以要逐个候选目录探测可写性
            List<string> dirs = new List<string>();
            if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
            try
            {
                dirs.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jigu"));
            }
            catch { }
            try { dirs.Add(Path.Combine(Path.GetTempPath(), "Jigu")); } catch { }

            foreach (string d in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".errlog-probe");
                    File.WriteAllText(probe, "1", Encoding.UTF8);
                    File.Delete(probe);
                    _path = Path.Combine(d, "error.log");
                    break;
                }
                catch { }
            }
            if (!_broadcast)
            {
                _broadcast = true;
                try { AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Write("AppDomain", e.ExceptionObject as Exception); }; }
                catch { }
            }
        }

        public static string Path_
        {
            get { return _path; }
        }

        public static void Write(string where, Exception ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                sb.Append("----- ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("  [").Append(where).Append("] -----\r\n");
                sb.Append("exe     : ").Append(SafeBase()).Append("\r\n");
                sb.Append("os      : ").Append(Sys.Describe()).Append("\r\n");
                sb.Append("version : ").Append(AppVer.Number).Append("\r\n");
                if (ex == null) sb.Append("(null exception)\r\n");
                else
                {
                    Exception cur = ex;
                    int depth = 0;
                    while (cur != null && depth < 6)
                    {
                        sb.Append(depth == 0 ? "type    : " : "inner   : ")
                          .Append(cur.GetType().FullName).Append("\r\n");
                        sb.Append("message : ").Append(cur.Message).Append("\r\n");
                        sb.Append("stack   :\r\n").Append(cur.StackTrace).Append("\r\n");
                        cur = cur.InnerException;
                        depth++;
                    }
                }
                sb.Append("\r\n");
                lock (Gate)
                {
                    if (_path == null) Bind(SafeBase());
                    if (_path != null) File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string SafeBase()
        {
            try { return AppDomain.CurrentDomain.BaseDirectory; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 界面资源：编译进 exe 的 base64 表，解码后常驻内存。
    /// 浏览器请求由 WebResourceRequested 拦截并从内存应答，绝不访问磁盘。
    /// </summary>
    internal static class Assets
    {
        public const string BaseUri = "https://jigu.local/";

        private static Dictionary<string, byte[]> _all;
        private static readonly object Gate = new object();

        private static void EnsureIndex()
        {
            if (_all != null) return;
            lock (Gate)
            {
                if (_all != null) return;
                Dictionary<string, byte[]> all = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                int count = 0, bytes = 0, failed = 0;
                foreach (KeyValuePair<string, string> kv in EmbeddedAssets.Table())
                {
                    try
                    {
                        byte[] blob = Convert.FromBase64String(kv.Value);
                        all[kv.Key.TrimStart('/')] = blob;
                        count++; bytes += blob.Length;
                    }
                    catch (Exception ex) { failed++; Log.Error("decode " + kv.Key, ex); }
                }
                _all = all;
                Log.Write("memory assets: " + count + " items, " + bytes + " bytes, failed=" + failed);
            }
        }

        public static int TotalCount { get { EnsureIndex(); return _all.Count; } }

        public static int WebCount
        {
            get
            {
                EnsureIndex();
                int n = 0;
                foreach (string k in _all.Keys)
                    if (!k.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) n++;
                return n;
            }
        }

        public static int DataCount { get { EnsureIndex(); return _all.Count - WebCount; } }

        public static List<string> ListPaths()
        {
            EnsureIndex();
            List<string> list = new List<string>(_all.Keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public static byte[] Get(string relativePath)
        {
            EnsureIndex();
            if (relativePath == null) return null;
            string key = relativePath.TrimStart('/');
            if (key.Length == 0) key = "index.html";
            byte[] blob;
            return _all.TryGetValue(key, out blob) ? blob : null;
        }

        /// <summary>从 URL 取资源：去掉查询串/锚点，并把 %XX 还原成中文文件名</summary>
        public static byte[] GetFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string rel = url;
            try
            {
                Uri u = new Uri(url);
                rel = u.Host.Equals("jigu.local", StringComparison.OrdinalIgnoreCase)
                    ? u.PathAndQuery : u.AbsolutePath;
            }
            catch { }
            int q = rel.IndexOf('?'); if (q >= 0) rel = rel.Substring(0, q);
            int h = rel.IndexOf('#'); if (h >= 0) rel = rel.Substring(0, h);
            try { rel = Uri.UnescapeDataString(rel); } catch { }
            return Get(rel);
        }

        public static string MimeOf(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            int q = p.IndexOf('?'); if (q >= 0) p = p.Substring(0, q);
            if (p.EndsWith(".html")) return "text/html; charset=utf-8";
            if (p.EndsWith(".css")) return "text/css; charset=utf-8";
            if (p.EndsWith(".js")) return "application/javascript; charset=utf-8";
            if (p.EndsWith(".json")) return "application/json; charset=utf-8";
            if (p.EndsWith(".png")) return "image/png";
            if (p.EndsWith(".ico")) return "image/x-icon";
            if (p.EndsWith(".svg")) return "image/svg+xml";
            return "application/octet-stream";
        }
    }

    /// <summary>应用图标：由内嵌 app.png 生成，供窗体与托盘使用</summary>
    internal static class AppIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Icon _cached;

        public static Icon Get()
        {
            if (_cached != null) return _cached;
            try
            {
                byte[] png = Assets.Get("app.png");
                if (png == null || png.Length == 0) return null;
                using (MemoryStream ms = new MemoryStream(png))
                using (Bitmap bmp = new Bitmap(ms))
                using (Bitmap sized = new Bitmap(bmp, new Size(64, 64)))
                {
                    IntPtr h = sized.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h)) _cached = (Icon)tmp.Clone();
                    }
                    finally { DestroyIcon(h); }
                }
                Log.Write("app icon built");
            }
            catch (Exception ex) { Log.Error("AppIcon.Get", ex); }
            return _cached;
        }
    }

    /// <summary>WebView2 Runtime 三重检测</summary>
    internal static class WebView2Check
    {
        private const string Key64 =
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string Key32 =
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        public static bool IsInstalled(out string how)
        {
            how = "";
            string[] keys = { Key64, Key32 };
            foreach (string key in keys)
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (rk != null)
                        {
                            object pv = rk.GetValue("pv");
                            if (pv != null && Convert.ToString(pv).Length > 0)
                            { how = "registry:" + Convert.ToString(pv); return true; }
                        }
                    }
                }
                catch { }
                try
                {
                    using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                    {
                        if (rk != null)
                        {
                            object pv = rk.GetValue("pv");
                            if (pv != null && Convert.ToString(pv).Length > 0)
                            { how = "registry-user:" + Convert.ToString(pv); return true; }
                        }
                    }
                }
                catch { }
            }
            string[] roots =
            {
                @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
                @"C:\Program Files\Microsoft\EdgeWebView\Application"
            };
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                        if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        { how = "folder:" + Path.GetFileName(dir); return true; }
                }
                catch { }
            }
            foreach (string root in roots)
            {
                try { if (Directory.Exists(root)) { how = "dirname"; return true; } }
                catch { }
            }
            return false;
        }

        /// <summary>缺失时引导安装（一键下载），绝不静默闪退</summary>
        public static void PromptAndInstall()
        {
            string msg = "稽古需要一个小型组件才能显示界面：\r\n\r\n    Microsoft Edge WebView2 Runtime\r\n\r\n"
                + "Windows 10 / 11 通常自带它，您的电脑上暂时没有检测到。\r\n\r\n"
                + "点「是」会自动下载并安装（约 2 MB，需要联网）。装好后请重新打开稽古。";
            if (MessageBox.Show(msg, "稽古 · 首次运行需要安装一个小组件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
                using (System.Net.WebClient wc = new System.Net.WebClient())
                    wc.DownloadFile(Bootstrapper, tmp);
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(tmp);
                p.WaitForExit(600000);
                string how;
                MessageBox.Show(IsInstalled(out how)
                    ? "安装完成，请重新打开稽古。"
                    : "仍未检测到组件，请手动安装：\r\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                    "稽古", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("自动下载失败，请手动安装：\r\n"
                    + "https://developer.microsoft.com/microsoft-edge/webview2/\r\n\r\n" + ex.Message,
                    "稽古", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    /// <summary>
    /// 主窗口。
    /// 必须 public：AddHostObjectToScript 要求的宿主对象（HostBridge）嵌套在本类里，
    /// 而嵌套在 internal 类中的 public 类对外实际可见性仍是 internal —— COM 看不见
    /// internal 类型，无法为其生成 IDispatch，注册时会抛
    /// ArgumentException（值不在预期的范围内 / E_INVALIDARG）。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly string _dataDir;
        private readonly string _userDataDir;
        private WebView2 _view;
        private Label _boot;
        private NotifyIcon _tray;
        private Panel _fatalPanel;
        private bool _ready;
        private bool _reallyExit;
        private bool _fatalShown;
        private int _initAttempts;
        private bool _coreHandled;   // WebView2 初始化完成回调可能被触发多次，必须只处理一次
        private bool _workStarted;   // 后台重活（语料/更新/托盘）只启动一次
        private bool _navDone;
        private volatile bool _dataUpdated;
        // 数据更新的互斥门：0 = 空闲，1 = 有线程正在重写语料。只保护「会写盘」的那一路。
        // 用 Interlocked 而不是 volatile bool：两个线程可能同时通过 if 判断，
        // 而「清标志」必须是持有者的动作 —— 详见 RunDataCheck 的注释。
        private int _updateGate;
        private string _dataBase;

        // ---- 程序本体更新：后台预下载 → 就绪 → 用户确认重启 ----
        // 状态机放在字段上而不是局部变量里，是因为下载在后台线程跑，而提示与按钮在界面线程。
        private readonly object _appGate = new object();
        private volatile string _appPhase = "idle";   // idle | downloading | ready | applying
        private AppUpdater.Report _appReady;          // 已下载并校验通过、等待应用的那一份
        private string _appReadyPkg;
        private volatile int _appProgress;            // 0-100，仅用于界面显示
        // 这两个都按**版本**记，不能只存布尔：存布尔的话第一次点过「稍后」之后，
        // 以后每个版本都会被当成「已忽略」——横幅不播、稍後按钮不显示、弹框也不再弹。
        private volatile string _appSnoozed = "";     // 用户点过「稍后」的那个版本
        private volatile string _appNotified = "";    // 已经弹过就绪提示的那个版本
        private System.Threading.Timer _recheck;      // 常驻时的周期性复查
        private int _checking;                        // 检查重入保护

        /// <summary>本地语料（检索全部由它完成）</summary>
        internal Corpus _corpus;

        public MainForm(string dataDir, string userDataDir, string dataBase)
        {
            _dataDir = dataDir;
            _userDataDir = userDataDir;
            _dataBase = dataBase;

            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1020, 820);
            MinimumSize = new Size(420, 560);
            // 标题里带上日志路径：出问题时看一眼标题就知道日志写到哪了
            Text = "稽古 [" + (Log.Path_ == null ? "no-log" : Log.Path_) + "]";
            BackColor = Color.FromArgb(248, 245, 238);
            ForeColor = Color.FromArgb(43, 42, 38);
            Font = SafeFont.Sans(10f);
            Icon = AppIcon.Get();

            // 构造函数里只做一件事：把窗口显示出来。
            // 语料加载、索引、更新检查、托盘图标全部推迟到窗口显示之后，
            // 任何数据环节出问题都不会影响"窗口能不能打开"。
            _corpus = new Corpus();

            _boot = new Label();
            _boot.Text = "正在备书……";
            _boot.Dock = DockStyle.Fill;
            _boot.TextAlign = ContentAlignment.MiddleCenter;
            _boot.Font = SafeFont.Kai(14f);
            _boot.ForeColor = Color.FromArgb(192, 57, 43);
            Controls.Add(_boot);
        }

        /// <summary>窗口显示后才做重活：托盘、语料、更新。全部后台化，绝阻塞界面。</summary>
        private void StartBackgroundWork()
        {
            if (_workStarted) return;
            _workStarted = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 托盘图标是 UI 控件，必须在界面线程上建
                try
                {
                    if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { InitTray(); });
                    else InitTray();
                }
                catch (Exception ex) { Log.Error("InitTray", ex); }
                try
                {
                    // 精选语料 + 藏书阁里勾选的史书分片。没勾的不读进来 —— 索引常驻，
                    // 全量二十四史要约 1.4 GB，只能按需。
                    string corpusDir = AppDomain.CurrentDomain.BaseDirectory;
                    RebuildCorpus(Library.LoadSelection(corpusDir, null), false);
                    Log.Write("corpus ready: " + (_corpus == null ? 0 : _corpus.DocCount) + " docs, "
                        + (_corpus == null ? 0 : _corpus.TermCount) + " terms");
                }
                catch (Exception ex) { Log.Error("corpus init", ex); }
                // 先看有没有「上次已经下载好、还没应用」的包。纯本地判断，快，
                // 而且必须在网络检查之前 —— 否则会把已经下好的东西再下一次。
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    AppUpdater.Report pending = AppUpdater.FindPending(baseDir);
                    if (pending != null) SetAppUpdateReady(pending, AppUpdater.PendingPath(baseDir));
                }
                catch (Exception ex) { Log.Error("find pending update", ex); }
                try { SilentUpdate(); }
                catch (Exception ex) { Log.Error("SilentUpdate", ex); }
            });
        }

        /// <summary>当前勾选的史书 slug，供藏书阁回显与状态条计数</summary>
        private List<string> _selectedShards = new List<string>();

        internal List<string> CurrentSelection() { return _selectedShards; }

        /// <summary>
        /// 按勾选重建语料。旧实例整个被换掉（_corpus = fresh），旧索引失去引用交给 GC ——
        /// 与数据更新后的热替换是同一条路径。rebuild 是「重解析 + 重建索引」，前四史量级
        /// 约几秒，所以调用方必须先摆出忙碌态。
        /// </summary>
        private Corpus RebuildCorpus(IList<string> slugs, bool persist)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> sel = Library.Sanitize(baseDir, slugs);
            Corpus fresh = new Corpus();
            fresh.LoadFrom(baseDir, Library.ShardPaths(baseDir, sel));
            _corpus = fresh;
            _selectedShards = sel;
            if (persist) Library.SaveSelection(null, sel);
            Log.Write("library reload: " + sel.Count + " shards, " + fresh.DocCount + " docs, index≈"
                + (fresh.IndexBytes / 1024) + " KB");
            return fresh;
        }

        private void InitTray()
        {
            try
            {
                Icon trayIcon = AppIcon.Get();
                if (trayIcon == null) return;
                _tray = new NotifyIcon();
                _tray.Icon = trayIcon;
                _tray.Text = "稽古 · 二十四史情境检索";
                _tray.Visible = true;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("显示稽古", null, delegate { RestoreFromTray(); });
                menu.Items.Add("打开程序目录", null, delegate
                {
                    try { System.Diagnostics.Process.Start("explorer.exe",
                        "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); } catch { }
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { _reallyExit = true; Close(); });
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += delegate { RestoreFromTray(); };
            }
            catch (Exception ex) { Log.Error("InitTray", ex); }
        }

        private void RestoreFromTray()
        {
            try
            {
                Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log.Write("OnFormClosing reason=" + e.CloseReason + " reallyExit=" + _reallyExit);
            // 只有界面真正就绪后才最小化到托盘；失败页阶段关闭就是关闭，不让程序变成关不掉的幽灵进程
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && _ready && !_fatalShown)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } }
            catch { }
            try { if (_recheck != null) { _recheck.Dispose(); _recheck = null; } }
            catch { }
            base.OnFormClosed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log.Write("form shown, starting webview init");
            StartBackgroundWork();
            StartInit();
            StartWatchdog();
            StartRecheckTimer();
        }

        /// <summary>
        /// 常驻时的周期性复查。程序可以最小化到托盘待很久，只在启动查一次会长期漏掉新版本。
        /// 用 System.Threading.Timer（线程池）而不是 WinForms 的 Timer：SilentUpdate 里有
        /// 阻塞的网络 IO 和语料重载，放界面线程上会卡住界面。
        /// </summary>
        private void StartRecheckTimer()
        {
            try
            {
                _recheck = new System.Threading.Timer(delegate
                {
                    try
                    {
                        // 正在退出或正在替换文件时不做任何网络工作
                        if (_reallyExit || IsDisposed || _appPhase == "applying") return;
                        SilentUpdate();
                    }
                    catch (Exception ex) { Log.Error("recheck", ex); }
                }, null, RecheckIntervalMs, RecheckIntervalMs);
                Log.Write("recheck timer started: every " + (RecheckIntervalMs / 3600000) + "h");
            }
            catch (Exception ex) { Log.Error("StartRecheckTimer", ex); }
        }

        /// <summary>
        /// 看门狗：WebView2 有可能既不成功也不回调（卡在创建控制器上），
        /// 那样用户会一直看到"正在备书"。超时后强制走兜底，绝不无限等待。
        /// </summary>
        private void StartWatchdog()
        {
            try
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 20000;
                t.Tick += delegate
                {
                    t.Stop();
                    t.Dispose();
                    if (_ready || _coreHandled || _fatalShown)
                    {
                        Log.Write("watchdog: no action (ready=" + _ready
                            + " coreHandled=" + _coreHandled + " fatal=" + _fatalShown + ")");
                        return;
                    }
                    Log.Write("watchdog timeout -> fallback");
                    ShowFatal(new TimeoutException(
                        "显示组件在 20 秒内没有响应（初始化既未成功也没有返回错误）"));
                };
                t.Start();
            }
            catch (Exception ex) { Log.Error("StartWatchdog", ex); }
        }

        /// <summary>
        /// 初始化 WebView2。
        /// 这里的每一个参数都按"最保守"取值：默认环境选项、纯 ASCII 的绝对用户数据目录、
        /// 不传浏览器可执行文件路径。历史上传过含转义引号的 --js-flags 等参数，
        /// 在部分运行时上会被参数校验拒掉并抛 ArgumentException（值不在预期的范围内）。
        /// 失败时按 失败页 -> 内置原生界面 -> 系统浏览器 三级兜底，绝不空窗口卡死。
        /// </summary>
        private void StartInit()
        {
            _initAttempts++;
            if (_initAttempts > 2 || _fatalShown || _coreHandled)
            {
                Log.Write("StartInit skipped (attempt=" + _initAttempts
                    + " fatal=" + _fatalShown + " coreHandled=" + _coreHandled + ")");
                return;
            }
            try
            {
                if (_view != null)
                {
                    try { Controls.Remove(_view); _view.Dispose(); } catch { }
                    _view = null;
                }

                // 一次尝试：默认参数 + 纯 ASCII 用户数据目录
                bool started = TryEnsure(null, false);
                if (!started && !_coreHandled && !_fatalShown)
                {
                    // 二次尝试：连用户数据目录都不指定，让 WebView2 用自己的默认位置
                    Log.Write("StartInit retry with default environment");
                    started = TryEnsure(null, true);
                }
                if (!started)
                {
                    Log.Write("StartInit failed to start (sync)");
                    ShowFatal(new InvalidOperationException("WebView2 初始化未能在限定时间内完成"));
                }
            }
            catch (Exception ex)
            {
                Log.Error("StartInit", ex);
                ShowFatal(ex);
            }
        }

        private bool TryEnsure(CoreWebView2Environment env, bool defaultPath)
        {
            try
            {
                _view = new WebView2();
                _view.Dock = DockStyle.Fill;
                // 背景色这类属性设置单独兜异常：老版本运行时上它也可能抛参数异常，
                // 绝不能因为一个颜色把整个显示组件拖垮
                try { _view.DefaultBackgroundColor = Color.FromArgb(248, 245, 238); }
                catch (Exception bex) { Log.Write("DefaultBackgroundColor skipped: " + bex.Message); }
                Controls.Add(_view);
                _view.BringToFront();
                _view.CoreWebView2InitializationCompleted += OnCoreReady;

                if (env != null) { _view.EnsureCoreWebView2Async(env); return true; }
                if (defaultPath)
                {
                    Log.Write("EnsureCoreWebView2Async with default environment");
                    _view.EnsureCoreWebView2Async();
                    return true;
                }

                string dir = _userDataDir;
                try
                {
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception dex) { Log.Write("user data dir create failed: " + dex.Message); }

                Log.Write("CreateAsync userDataFolder=" + (dir == null ? "(null)" : dir));
                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
                System.Threading.Tasks.Task<CoreWebView2Environment> task =
                    CoreWebView2Environment.CreateAsync(null, dir, options);
                CoreWebView2Environment made = null;
                try { made = task.Result; }
                catch (AggregateException aex)
                {
                    throw (aex.InnerException != null ? aex.InnerException : aex);
                }
                if (made != null)
                {
                    Log.Write("environment ready, browser=" + SafeBrowserVersion(made));
                    _view.EnsureCoreWebView2Async(made);
                    return true;
                }
                Log.Write("CreateAsync returned null environment");
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("TryEnsure failed: " + ex.GetType().Name + ": " + ex.Message
                    + " | userDataFolder=" + _userDataDir);
                Log.Error("TryEnsure", ex);
                try { if (_view != null) { Controls.Remove(_view); _view.Dispose(); } } catch { }
                _view = null;
                return false;
            }
        }

        private static string SafeBrowserVersion(CoreWebView2Environment env)
        {
            try { return env == null ? "?" : env.BrowserVersionString; }
            catch { return "?"; }
        }

        private void OnCoreReady(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            // WebView2 在失败时可能多次回调；只处理第一次，避免重复建内核/重复弹失败页
            if (_coreHandled)
            {
                Log.Write("OnCoreReady ignored (already handled)");
                return;
            }
            _coreHandled = true;
            if (e != null && e.InitializationException != null)
            {
                Log.Error("OnCoreReady", e.InitializationException);
                ShowFatal(e.InitializationException);
                return;
            }
            try
            {
                CoreWebView2 core = _view != null ? _view.CoreWebView2 : null;
                if (core == null)
                {
                    Log.Write("OnCoreReady: CoreWebView2 is null");
                    ShowFatal(new InvalidOperationException("显示组件没有就绪"));
                    return;
                }
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.AreDevToolsEnabled = false;

                // 所有前端资源从内存应答，不依赖任何磁盘路径
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;

                // 宿主桥注册失败不能把整个网页界面拖去失败页：app.js 在拿不到桥时
                // 会退回「读取同目录 corpus.json 现场建索引」的离线检索，功能仍在，
                // 只是精度较低。所以这里只记日志，继续 Navigate。
                // JIGU_NO_BRIDGE=1 强制跳过注册，用来随时复验这条降级路径。
                if (Environment.GetEnvironmentVariable("JIGU_NO_BRIDGE") == "1")
                {
                    Log.Write("bridge registration skipped (JIGU_NO_BRIDGE=1) -> offline mode");
                }
                else
                {
                    try
                    {
                        core.AddHostObjectToScript("host", new HostBridge(this));
                    }
                    catch (Exception bridgeEx)
                    {
                        Log.Error("AddHostObjectToScript failed -- continuing without the bridge "
                            + "(page will use the offline corpus path)", bridgeEx);
                    }
                }

                if (_boot != null) _boot.Visible = false;
                if (_navDone)
                {
                    Log.Write("OnCoreReady: already navigated");
                    return;
                }
                _navDone = true;
                core.Navigate(Assets.BaseUri + "index.html");
                _ready = true;
                Log.Write("webview ready, navigating to " + Assets.BaseUri + "index.html");

                // 界面已经在屏幕上了，后台更新继续跑（SilentUpdate 由 StartBackgroundWork 启动）
            }
            catch (Exception ex)
            {
                Log.Error("OnCoreReady body", ex);
                ShowFatal(ex);
            }
        }

        /// <summary>拦截全部请求：命中内存就返回，未命中返回 404 并记录</summary>
        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string uri = e.Request != null ? e.Request.Uri : "";
            try
            {
                byte[] blob = Assets.GetFromUrl(uri);
                if (blob == null)
                {
                    // 离线兜底要用：宿主桥不可用时 app.js 会直接 fetch 同目录的 corpus.json
                    // 现场建索引。不在这里应答的话，那条降级路径拿到的就是 404。
                    blob = TryReadDataFile(uri);
                }
                if (blob == null)
                {
                    if ((uri ?? "").ToLowerInvariant().EndsWith("favicon.ico"))
                    {
                        e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                            null, 204, "No Content", "Cache-Control: no-cache");
                        return;
                    }
                    Log.Write("MISS " + uri);
                    e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "Content-Type: text/plain; charset=utf-8");
                    return;
                }
                e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(blob, false), 200, "OK",
                    "Content-Type: " + Assets.MimeOf(uri)
                    + "\r\nCache-Control: no-store, no-cache, must-revalidate");
            }
            catch (Exception ex)
            {
                Log.Error("OnWebResourceRequested " + uri, ex);
                try
                {
                    e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 500, "Internal Error", "Content-Type: text/plain; charset=utf-8");
                }
                catch { }
            }
        }

        // ================= 数据更新 =================

        /// <summary>
        /// 允许页面从磁盘读取的平文件白名单。
        /// 只用于「宿主桥不可用时的离线兜底」：app.js 会 fetch 同目录的 corpus.json
        /// 现场建索引。目录必须是 BaseDirectory —— 语料与数据更新都以此为基准
        /// （见 Corpus.LoadFrom / DataUpdater），换成别的目录会让页面读到另一份数据。
        /// 白名单是必要的：这里应答的是页面发起的任意请求，不能把安装目录整个暴露出去。
        /// </summary>
        private static byte[] TryReadDataFile(string uri)
        {
            string name;
            try { name = Path.GetFileName(new Uri(uri).AbsolutePath); }
            catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            if (!name.Equals("corpus.json", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("data_version.json", StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                string full = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
                if (!File.Exists(full)) return null;
                Log.Write("served from disk (offline fallback): " + name);
                return File.ReadAllBytes(full);
            }
            catch (Exception ex)
            {
                Log.Error("TryReadDataFile " + name, ex);
                return null;
            }
        }

        internal string RunDataCheck(bool apply)
        {
            // 只有真正要写盘的这一路才排队；只读的检查不必挡着别人。
            // 门必须用 Interlocked 抢，「抢不到」的那一路**绝不能**去清标志：
            // 标志是持有者设的，替它清掉等于当场放行下一个调用，于是两个线程
            // 同时跑 DataUpdater.Apply，各自持一份内存副本重写同一个 corpus.json。
            bool held = false;
            if (apply)
            {
                if (Interlocked.CompareExchange(ref _updateGate, 1, 0) != 0)
                    return "{\"busy\":true,\"message\":\"正在更新，请稍候\"}";
                held = true;
            }
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                DataUpdater.Report r = DataUpdater.Check(baseDir);
                // 门槛必须同时看「史料变化」与「平文件变化」：只看 Changed 会让
                // 「只更新了 labels.json」这种情况被静默跳过，永远不生效。
                if (apply && r.Checked && (r.Changed.Count > 0 || r.FilesChanged.Count > 0))
                {
                    r = DataUpdater.Apply(baseDir, r);
                    // 门槛必须和上面那道一致：只看 Merged 会把「只更新了 labels.json /
                    // stopwords.json」的更新挡在重建之外 —— 磁盘上的表已经是新的，内存里
                    // 的 _corpus 还是旧的，标签检索要等下次启动才生效。
                    if (r.Merged.Count > 0 || r.FilesUpdated.Count > 0)
                    {
                        // 走与启动、藏书阁同一条路。这里以前是 LoadFrom(baseDir)，只读精选集 ——
                        // 数据通道一有动静就把用户勾的史书整个丢掉（实测日志里
                        // 「corpus rebuilt: 201 docs」就是这个）。分片不在数据通道里，
                        // 磁盘上那份没被动过，按勾选重读即可。
                        Corpus fresh = RebuildCorpus(_selectedShards, false);   // 旧语料失去引用交给 GC
                        _dataUpdated = true;
                        Log.Write("corpus rebuilt: " + fresh.DocCount + " docs");
                    }
                }
                return DataReportJson(r, apply);
            }
            catch (Exception ex)
            {
                Log.Error("RunDataCheck", ex);
                return "{\"checked\":false,\"message\":\"更新未完成，继续使用本地史料\"}";
            }
            finally { if (held) Interlocked.Exchange(ref _updateGate, 0); }
        }

        private static string DataReportJson(DataUpdater.Report r, bool applied)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"checked\":").Append(r.Checked ? "true" : "false");
            sb.Append(",\"applied\":").Append(applied ? "true" : "false");
            sb.Append(",\"localVersion\":\"").Append(Json.Escape(r.LocalVersion)).Append('"');
            sb.Append(",\"remoteVersion\":\"").Append(Json.Escape(r.RemoteVersion)).Append('"');
            sb.Append(",\"changedCount\":").Append(r.Changed.Count);
            sb.Append(",\"mergedCount\":").Append(r.Merged.Count);
            sb.Append(",\"failedCount\":").Append(r.Failed.Count);
            sb.Append(",\"message\":\"").Append(Json.Escape(r.Message)).Append("\"}");
            return sb.ToString();
        }

        // ================= 程序本体更新 =================
        //
        // 分两段，为的是不让用户干等下载：
        //   1) 后台线程：检查 + 静默预下载（不提示任何东西）→ 就绪
        //   2) 就绪后才弹一次「已下载完成，现在重启更新吗？」→ 应用，或留到下次启动
        // 点「稍后」不清 pending：下次启动会由 FindPending 重新发现并再问一次。
        //
        // 状态放在 _appPhase / _appReady 上而不是局部变量里：下载在后台线程跑，
        // 而确认框、按钮、轮询都在界面线程上。

        /// <summary>常驻时的复查间隔。程序能最小化到托盘待很久，启动只查一次会长期漏掉新版本。</summary>
        private const int RecheckIntervalMs = 6 * 60 * 60 * 1000;

        /// <summary>
        /// 更新操作的命名互斥量：多开时只让一个实例下载/替换同一个安装目录。
        /// 名字由安装目录推出，装在不同路径的副本互不影响。
        /// </summary>
        private static Mutex AcquireUpdateLock(string baseDir)
        {
            try
            {
                string key = Net.Sha256Hex(Encoding.UTF8.GetBytes(
                    baseDir.TrimEnd('\\').ToLowerInvariant()));
                Mutex m = new Mutex(false, "Local\\JiguUpdate-" + key.Substring(0, 16));
                bool got;
                try { got = m.WaitOne(0); }
                catch (AbandonedMutexException) { got = true; }   // 上一个持有者崩了，归我们
                if (!got) { m.Dispose(); return null; }
                return m;
            }
            catch (Exception ex) { Log.Error("AcquireUpdateLock", ex); return null; }
        }

        /// <summary>
        /// 放掉 AcquireUpdateLock 拿到的锁。只在「不打算马上退出」的失败路径上调用 ——
        /// 成功路径靠进程退出释放，这里再放一次反而会让另一个实例插进替换过程中间。
        /// </summary>
        private static void ReleaseUpdateLock(Mutex m)
        {
            if (m == null) return;
            try { m.ReleaseMutex(); }
            catch (Exception ex) { Log.Error("ReleaseUpdateLock", ex); }
            try { m.Dispose(); } catch { }
        }

        /// <summary>静默更新：先数据，再检查程序本体的新版本并后台预下载。全程不打扰用户。</summary>
        private void SilentUpdate()
        {
            // 启动那次与定时复查可能撞车；重叠没有意义，让后来者直接退出
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
            try
            {
                // 每轮开头丢掉 Release 缓存，好让史料与程序两次检查共用一次 API 请求
                UpdateSource.Reset();
                RunDataCheck(true);

                if (_appReady != null) return;             // 已经有下好的包，不必再查
                AppUpdater.Report app = AppUpdater.Check();
                if (app.Checked && app.Available)
                {
                    Log.Write("app update available: v" + app.RemoteVersion + "，开始后台下载");
                    DownloadAppInBackground(app);
                }
            }
            catch (Exception ex) { Log.Error("SilentUpdate", ex); }
            finally { Interlocked.Exchange(ref _checking, 0); }
        }

        /// <summary>后台把新版本下到 pending/。下载期间界面照常可用，完成才通知用户。</summary>
        private void DownloadAppInBackground(AppUpdater.Report r)
        {
            // 已经在下了就别再起一个：命名互斥量能挡住第二个线程，但它会把阶段打回
            // idle，界面上的进度就此断掉。这里直接挡在最前面。
            if (_appPhase == "downloading") return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _appPhase = "downloading";
            _appProgress = 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Mutex guard = AcquireUpdateLock(baseDir);
                if (guard == null)
                {
                    // 另一个实例正在处理同一个安装目录：静默退让，它下好后大家共享
                    Log.Write("app download skipped: 另一个实例正在更新");
                    _appPhase = "idle";
                    return;
                }
                try
                {
                    string pkg = AppUpdater.Download(baseDir, r, delegate(int p) { _appProgress = p; });
                    if (pkg == null || !AppUpdater.WritePendingMeta(baseDir, r, pkg))
                    {
                        Log.Write("app download failed: v" + r.RemoteVersion);
                        // 半截包必须清掉：否则下次启动会把它当成一个有效的待应用更新
                        AppUpdater.DeletePending(baseDir);
                        _appPhase = "idle";
                        return;
                    }
                    SetAppUpdateReady(r, pkg);
                }
                catch (Exception ex)
                {
                    Log.Error("DownloadAppInBackground", ex);
                    _appPhase = "idle";
                }
                finally
                {
                    try { guard.ReleaseMutex(); } catch { }
                    guard.Dispose();
                }
            });
        }

        /// <summary>标记「已下载待重启」，并在界面线程弹一次确认框</summary>
        private void SetAppUpdateReady(AppUpdater.Report r, string pkg)
        {
            lock (_appGate) { _appReady = r; _appReadyPkg = pkg; }
            _appPhase = "ready";
            _appProgress = 0;
            Log.Write("app update ready: v" + r.RemoteVersion + " -> " + pkg);
            try { BeginInvoke((MethodInvoker)delegate { PromptReadyUpdate(); }); }
            catch (Exception ex) { Log.Error("SetAppUpdateReady", ex); }
        }

        /// <summary>就绪提示（界面线程）。同一个版本只主动弹一次，用户点过「稍后」的不再弹。</summary>
        private void PromptReadyUpdate()
        {
            AppUpdater.Report r;
            lock (_appGate) { r = _appReady; }
            if (r == null) return;
            if (string.Equals(_appNotified, r.RemoteVersion, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(_appSnoozed, r.RemoteVersion, StringComparison.OrdinalIgnoreCase)) return;
            _appNotified = r.RemoteVersion;
            if (AppUpdater.AskUser(r)) ApplyReadyUpdate();
        }

        /// <summary>应用已下载的包：拉起替换助手 → 真正退出 → 由助手换文件并重启</summary>
        private bool ApplyReadyUpdate()
        {
            string pkg;
            lock (_appGate) { pkg = _appReadyPkg; }
            if (string.IsNullOrEmpty(pkg))
            {
                Log.Write("apply skipped: 没有待应用的包");
                return false;
            }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 成功路径故意不释放：本进程马上就会退出，退出即释放。这样才能挡住另一个实例
            // 在文件替换进行到一半时也去写同一个安装目录。
            // 但失败路径必须放掉 —— 助手没拉起来就没有「进程马上退出」这回事，一直扣着锁
            // 会让本会话之后所有更新都在这里静默跳过，别的实例也被挡在门外。
            Mutex updateLock = AcquireUpdateLock(baseDir);
            if (updateLock == null)
            {
                Log.Write("apply skipped: 另一个实例正在更新");
                return false;
            }
            _appPhase = "applying";
            if (!AppUpdater.LaunchReplace(baseDir, pkg))
            {
                ReleaseUpdateLock(updateLock);
                _appPhase = "ready";
                return false;
            }
            Log.Write("app update: closing for hot swap");
            BeginInvoke((MethodInvoker)delegate { try { _reallyExit = true; Close(); } catch { } });
            return true;
        }

        /// <summary>桥接用：应用已就绪的更新，返回 JSON 结果</summary>
        internal string ApplyReadyUpdateJson()
        {
            if (_appPhase != "ready")
                return "{\"phase\":\"" + Json.Escape(_appPhase) + "\",\"updating\":false,"
                    + "\"message\":\"没有待应用的更新\"}";
            bool ok = ApplyReadyUpdate();
            return "{\"phase\":\"" + Json.Escape(_appPhase) + "\",\"updating\":"
                + (ok ? "true" : "false") + ",\"message\":\""
                + (ok ? "正在更新并重启…" : "暂时无法更新（可能有另一个实例正在更新），请稍后重试")
                + "\"}";
        }

        internal void SnoozeAppUpdate(string version)
        {
            _appSnoozed = version == null ? "" : version.Trim();
            Log.Write("app update snoozed: " + _appSnoozed);
        }

        /// <summary>只检查、不下载（界面上的「检查版本」）</summary>
        internal string RunAppCheck()
        {
            try
            {
                UpdateSource.Reset();
                AppUpdater.Report r = AppUpdater.Check();
                string msg = r.Message;
                if (r.Available && _appPhase == "ready") msg = "新版本 v" + r.RemoteVersion + " 已下载，重启后生效";
                return AppReportJson(r, _appPhase, msg);
            }
            catch (Exception ex)
            {
                Log.Error("RunAppCheck", ex);
                return "{\"checked\":false,\"phase\":\"idle\",\"message\":\"更新检查失败\"}";
            }
        }

        /// <summary>界面上的「立即更新」：已就绪就直接应用，否则检查并转后台下载</summary>
        internal string RunAppUpdate()
        {
            try
            {
                if (_appPhase == "ready")
                {
                    bool ok = ApplyReadyUpdate();
                    return "{\"checked\":true,\"available\":true,\"updating\":" + (ok ? "true" : "false")
                        + ",\"phase\":\"" + Json.Escape(_appPhase) + "\",\"message\":\""
                        + (ok ? "正在更新并重启…" : "暂时无法更新（可能有另一个实例正在更新），请稍后重试")
                        + "\"}";
                }

                UpdateSource.Reset();
                AppUpdater.Report r = AppUpdater.Check();
                if (!r.Available) return AppReportJson(r, "idle", r.Message);
                DownloadAppInBackground(r);
                return AppReportJson(r, "downloading", "正在后台下载更新…");
            }
            catch (Exception ex)
            {
                Log.Error("RunAppUpdate", ex);
                return "{\"checked\":false,\"phase\":\"idle\",\"message\":\"更新失败\"}";
            }
        }

        /// <summary>供自动化测试：不弹窗，同步走完 检查→下载→替换</summary>
        internal string ForceAppUpdate()
        {
            try
            {
                UpdateSource.Reset();
                AppUpdater.Report r = AppUpdater.Check();
                if (!r.Available) return AppReportJson(r, "idle", r.Message);

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string pkg = AppUpdater.Download(baseDir, r, null);
                if (pkg == null) return AppReportJson(r, "idle", "下载失败，请稍后重试");
                AppUpdater.WritePendingMeta(baseDir, r, pkg);
                lock (_appGate) { _appReady = r; _appReadyPkg = pkg; }
                _appNotified = r.RemoteVersion;   // 测试路径不弹框
                _appPhase = "ready";
                if (!ApplyReadyUpdate()) return AppReportJson(r, "ready", "无法启动更新程序");
                return AppReportJson(r, "applying", "正在更新并重启…");
            }
            catch (Exception ex)
            {
                Log.Error("ForceAppUpdate", ex);
                return "{\"checked\":false,\"phase\":\"idle\",\"message\":\"更新检查失败\"}";
            }
        }

        /// <summary>当前更新状态，供界面轮询渲染（幂等，不需要"消费"标志）</summary>
        internal string UpdateStateJson()
        {
            AppUpdater.Report r;
            lock (_appGate) { r = _appReady; }
            StringBuilder sb = new StringBuilder(320);
            sb.Append("{\"phase\":\"").Append(Json.Escape(_appPhase)).Append('"');
            sb.Append(",\"progress\":").Append(_appProgress);
            sb.Append(",\"ready\":").Append(r != null ? "true" : "false");
            // 只有「用户忽略的正是当前这一版」才算忽略。前端拿它挡横幅和「稍後」按钮，
            // 一旦这里含糊成「被忽略过」，下一版就再也不会提醒用户了。
            sb.Append(",\"snoozed\":").Append(
                r != null && string.Equals(_appSnoozed, r.RemoteVersion, StringComparison.OrdinalIgnoreCase)
                    ? "true" : "false");
            sb.Append(",\"localVersion\":\"").Append(Json.Escape(AppVer.Number)).Append('"');
            sb.Append(",\"remoteVersion\":\"").Append(Json.Escape(r == null ? "" : r.RemoteVersion)).Append('"');
            sb.Append(",\"notes\":\"").Append(Json.Escape(r == null ? "" : r.Notes)).Append('"');
            sb.Append(",\"message\":\"").Append(Json.Escape(r == null
                ? (_appPhase == "downloading" ? ("正在后台下载 v" + _appProgress + "%") : "")
                : ("新版本 v" + r.RemoteVersion + " 已下载，重启后生效"))).Append("\"}");
            return sb.ToString();
        }

        private static string AppReportJson(AppUpdater.Report r, string phase, string message)
        {
            StringBuilder sb = new StringBuilder(320);
            sb.Append("{\"checked\":").Append(r.Checked ? "true" : "false");
            sb.Append(",\"available\":").Append(r.Available ? "true" : "false");
            sb.Append(",\"updating\":").Append(phase == "applying" ? "true" : "false");
            sb.Append(",\"phase\":\"").Append(Json.Escape(phase)).Append('"');
            sb.Append(",\"localVersion\":\"").Append(Json.Escape(r.LocalVersion)).Append('"');
            sb.Append(",\"remoteVersion\":\"").Append(Json.Escape(r.RemoteVersion)).Append('"');
            sb.Append(",\"notes\":\"").Append(Json.Escape(r.Notes)).Append('"');
            sb.Append(",\"message\":\"").Append(Json.Escape(message)).Append("\"}");
            return sb.ToString();
        }

        // ================= 失败页（古风，只出现一次，不递归） =================

        private void ShowFatal(Exception ex)
        {
            try
            {
                if (_fatalShown) { Log.Write("ShowFatal ignored"); return; }
                _fatalShown = true;
                _initAttempts = 99;
                if (_boot != null) _boot.Visible = false;
                if (_fatalPanel != null) return;

                string how;
                bool runtimeOk = WebView2Check.IsInstalled(out how);
                string logPath = ErrorLog.Path_;
                StringBuilder detail = new StringBuilder();
                detail.AppendLine("网页显示组件这次没能启动，稽古不会停在这里——");
                detail.AppendLine("点下方「改用内置界面」直接就能查，功能与网页版完全一样。");
                detail.AppendLine();
                if (!runtimeOk)
                {
                    detail.AppendLine("本机未检测到 Microsoft Edge WebView2 显示组件。");
                    detail.AppendLine("装上它即可恢复网页界面：");
                    detail.AppendLine("https://developer.microsoft.com/microsoft-edge/webview2/");
                }
                else
                {
                    detail.AppendLine("已检测到 WebView2：" + (string.IsNullOrEmpty(how) ? "是" : how));
                    string fallback = Sys.FindFallbackBrowser();
                    if (fallback != null) detail.AppendLine("系统浏览器：" + fallback);
                }
                detail.AppendLine();
                detail.AppendLine("系统信息：" + Sys.Describe());
                detail.AppendLine("技术信息：" + (ex == null ? "未知错误" : ex.GetType().Name + ": " + ex.Message));
                if (!string.IsNullOrEmpty(logPath)) detail.AppendLine("错误记录：" + logPath);

                Panel page = new Panel();
                page.Dock = DockStyle.Fill;
                page.BackColor = Color.FromArgb(248, 245, 238);
                page.Padding = new Padding(40, 34, 40, 30);
                page.Paint += delegate(object s, PaintEventArgs pe)
                {
                    try { PaintInkWash(pe.Graphics, page.ClientSize); } catch { }
                };
                Controls.Add(page);
                page.BringToFront();
                _fatalPanel = page;

                PictureBox logo = new PictureBox();
                logo.Size = new Size(54, 54);
                logo.Location = new Point(40, 30);
                logo.SizeMode = PictureBoxSizeMode.Zoom;
                try { Icon ic = AppIcon.Get(); if (ic != null) logo.Image = ic.ToBitmap(); } catch { }
                page.Controls.Add(logo);

                Label title = new Label();
                title.Text = "稽古";
                title.Font = SafeFont.Kai(22f);
                title.ForeColor = Color.FromArgb(192, 57, 43);
                title.AutoSize = true;
                title.Location = new Point(106, 34);
                page.Controls.Add(title);

                Label sub = new Label();
                sub.Text = "网页界面未开启，可改用内置界面";
                sub.Font = SafeFont.Kai(12f);
                sub.ForeColor = Color.FromArgb(120, 112, 98);
                sub.AutoSize = true;
                sub.Location = new Point(108, 68);
                page.Controls.Add(sub);

                TextBox box = new TextBox();
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Vertical;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.BackColor = Color.FromArgb(253, 251, 244);
                box.ForeColor = Color.FromArgb(85, 82, 74);
                box.Font = SafeFont.Kai(11f);
                box.Text = detail.ToString();
                box.Location = new Point(40, 112);
                box.Size = new Size(Math.Max(300, ClientSize.Width - 80), 230);
                box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                page.Controls.Add(box);

                Button classic = MakeSealButton("改用内置界面", Math.Max(40, ClientSize.Width - 300), 356);
                classic.Click += delegate
                {
                    try
                    {
                        ClassicForm cf = new ClassicForm(_dataDir, _dataBase, "网页界面未开启，当前为内置界面");
                        cf.Show(this);
                    }
                    catch (Exception cex)
                    {
                        Log.Error("classic from fatal", cex);
                        MessageBox.Show("内置界面打开失败：" + cex.Message, "稽古");
                    }
                };
                page.Controls.Add(classic);

                Button copy = MakeSealButton("复制错误", Math.Max(40, ClientSize.Width - 152), 356);
                copy.Click += delegate
                {
                    try { Clipboard.SetText(detail.ToString()); copy.Text = "已复制"; }
                    catch { }
                };
                page.Controls.Add(copy);

                Log.Write("fatal page shown");
            }
            catch (Exception fex)
            {
                Log.Error("ShowFatal", fex);
                try { MessageBox.Show("稽古无法显示网页界面，将改用内置界面：\r\n\r\n"
                    + (ex == null ? "未知" : ex.GetType().Name + ": " + ex.Message), "稽古",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                catch { }
                // 失败页本身都画不出来时，直接开内置界面
                try { new ClassicForm(_dataDir, _dataBase, "网页界面未开启，当前为内置界面").Show(); }
                catch (Exception cex) { Log.Error("classic after fatal", cex); }
            }
        }

        /// <summary>造一个朱砂色的古风按钮</summary>
        private static Button MakeSealButton(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = SafeFont.Kai(12f);
            b.Size = new Size(140, 36);
            b.BackColor = Color.FromArgb(192, 57, 43);
            b.ForeColor = Color.FromArgb(253, 249, 240);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Location = new Point(x, y);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            return b;
        }

        /// <summary>用 GDI+ 画水墨远山，和网页背景同一调子</summary>
        private static void PaintInkWash(Graphics g, Size size)
        {
            int h = size.Height, w = size.Width;
            if (w <= 0 || h <= 0) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawRidge(g, w, h, 0.62f, 0.30f, 70, Color.FromArgb(90, 92, 96));
            DrawRidge(g, w, h, 0.82f, 0.45f, 96, Color.FromArgb(70, 74, 78));
        }

        private static void DrawRidge(Graphics g, int w, int h, float baseY, float amp, int alpha, Color ink)
        {
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float y0 = baseY * h;
                path.AddBezier(-0.05f * w, y0 + amp, 0.10f * w, y0 - amp * 1.6f,
                    0.20f * w, y0 + amp * 0.4f, 0.30f * w, y0 - amp * 0.8f);
                path.AddBezier(0.30f * w, y0 - amp * 0.8f, 0.40f * w, y0 - amp * 1.9f,
                    0.48f * w, y0 + amp * 0.5f, 0.58f * w, y0 - amp * 0.6f);
                path.AddBezier(0.58f * w, y0 - amp * 0.6f, 0.68f * w, y0 - amp * 1.5f,
                    0.78f * w, y0 + amp * 0.6f, 0.90f * w, y0 - amp * 0.4f);
                path.AddBezier(0.90f * w, y0 - amp * 0.4f, 0.96f * w, y0 - amp * 1.0f,
                    1.02f * w, y0 + amp * 0.3f, 1.06f * w, y0);
                path.AddLine(1.06f * w, h + 4, -0.05f * w, h + 4);
                path.CloseFigure();
                int top = (int)Math.Max(0, y0 - amp * 2.2f);
                using (System.Drawing.Drawing2D.LinearGradientBrush b =
                    new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, top, Math.Max(1, w), Math.Max(1, h - top)),
                        Color.FromArgb(alpha, ink), Color.FromArgb(6, ink),
                        System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                {
                    g.FillPath(b, path);
                }
            }
        }

        /// <summary>暴露给 JavaScript 的桥接对象</summary>
        [System.Runtime.InteropServices.ComVisible(true)]
        public sealed class HostBridge
        {
            private readonly MainForm _form;

            public HostBridge(MainForm form) { _form = form; }

            public string GetSnapshot()
            {
                string how;
                bool wv2 = WebView2Check.IsInstalled(out how);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"baseDir\":\"").Append(Json.Escape(AppDomain.CurrentDomain.BaseDirectory)).Append("\",");
                sb.Append("\"baseUri\":\"").Append(Assets.BaseUri).Append("\",");
                sb.Append("\"dataBase\":\"").Append(Json.Escape(Net.BaseUrl())).Append("\",");
                sb.Append("\"webview2\":").Append(wv2 ? "true" : "false").Append(',');
                sb.Append("\"webview2How\":\"").Append(Json.Escape(how)).Append("\",");
                sb.Append("\"os\":\"").Append(Json.Escape(Sys.Describe())).Append("\",");
                sb.Append("\"fallbackBrowser\":\"").Append(Json.Escape(Sys.FindFallbackBrowser() ?? "")).Append("\",");
                sb.Append("\"docs\":").Append(_form._corpus == null ? 0 : _form._corpus.DocCount).Append(',');
                sb.Append("\"books\":").Append(_form.CurrentSelection().Count).Append(',');
                sb.Append("\"libraryInstalled\":")
                  .Append(Library.IsInstalled(AppDomain.CurrentDomain.BaseDirectory) ? "true" : "false").Append(',');
                sb.Append("\"version\":\"").Append(AppVer.Number).Append("\"}");
                return sb.ToString();
            }

            /// <summary>藏书阁：可选书目 + 当前勾选 + 体积汇总</summary>
            public string GetLibrary()
            {
                try { return Library.ToJson(AppDomain.CurrentDomain.BaseDirectory, _form.CurrentSelection()); }
                catch (Exception ex)
                {
                    Log.Error("GetLibrary", ex);
                    return "{\"installed\":false,\"books\":[]}";
                }
            }

            /// <summary>设置勾选 → 立即重建语料 → 回传新统计。界面需在此期间显示忙碌态。</summary>
            public string SetLibrarySelection(string jsonArray)
            {
                try
                {
                    List<string> slugs = new List<string>();
                    List<object> arr = MiniJson.Parse(jsonArray) as List<object>;
                    if (arr != null)
                        foreach (object o in arr)
                        {
                            string s = o as string;
                            if (!string.IsNullOrEmpty(s)) slugs.Add(s);
                        }
                    Corpus c = _form.RebuildCorpus(slugs, true);
                    return "{\"ok\":true,\"docs\":" + c.DocCount + ",\"terms\":" + c.TermCount
                        + ",\"indexKB\":" + (c.IndexBytes / 1024) + "}";
                }
                catch (Exception ex)
                {
                    Log.Error("SetLibrarySelection", ex);
                    return "{\"ok\":false,\"error\":\"" + Json.Escape(ex.Message) + "\"}";
                }
            }

            /// <summary>核心检索：全部在 C# 完成，前端只拿 Top3 结果 JSON</summary>
            public string Search(string query, int topK)
            {
                try
                {
                    Corpus corpus = _form._corpus;
                    if (corpus == null || !corpus.IsReady)
                        return "{\"error\":\"书库尚未就绪\",\"hits\":[]}";
                    if (topK <= 0 || topK > 10) topK = 3;
                    string termsJson;
                    long ms;
                    return corpus.SearchToJson(query, topK, out termsJson, out ms);
                }
                catch (Exception ex)
                {
                    Log.Error("Search", ex);
                    return "{\"error\":\"检索未完成，请重试\",\"hits\":[]}";
                }
            }

            public string CorpusStats()
            {
                try
                {
                    return _form._corpus == null
                        ? "{\"docs\":0,\"terms\":0,\"books\":{}}"
                        : _form._corpus.StatsToJson();
                }
                catch (Exception ex)
                {
                    Log.Error("CorpusStats", ex);
                    return "{\"docs\":0,\"terms\":0,\"books\":{}}";
                }
            }

            public string ExplainSearch(string query)
            {
                try
                {
                    Corpus corpus = _form._corpus;
                    if (corpus == null) return "{\"terms\":[],\"normalized\":\"\"}";
                    string norm = Corpus.Normalize(query);
                    List<string> terms = corpus.Tokenize(norm);
                    StringBuilder sb = new StringBuilder(512);
                    sb.Append("{\"normalized\":\"").Append(Json.Escape(norm)).Append("\",\"terms\":[");
                    for (int i = 0; i < terms.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(Json.Escape(terms[i])).Append('"');
                    }
                    sb.Append("]}");
                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    Log.Error("ExplainSearch", ex);
                    return "{\"terms\":[],\"normalized\":\"\"}";
                }
            }

            public string CheckDataUpdate() { return _form.RunDataCheck(false); }
            public string ApplyDataUpdate() { return _form.RunDataCheck(true); }
            public string CheckAppUpdate() { return _form.RunAppCheck(); }
            /// <summary>「立即更新」：已下载好就直接应用并重启，否则转后台下载</summary>
            public string StartAppUpdate() { return _form.RunAppUpdate(); }

            /// <summary>当前更新状态（阶段 / 进度 / 待重启的版本），界面轮询用</summary>
            public string GetUpdateState() { return _form.UpdateStateJson(); }

            /// <summary>应用已经下载好的更新并重启</summary>
            public string ApplyReadyUpdate()
            {
                return _form.ApplyReadyUpdateJson();
            }

            /// <summary>用户点了「稍后」：本轮不再提示这个版本</summary>
            public void SnoozeUpdate(string version) { _form.SnoozeAppUpdate(version); }

            /// <summary>供自动化测试：跳过询问框直接执行热替换</summary>
            public string ForceAppUpdate()
            {
                return _form.ForceAppUpdate();
            }

            public void ShowDataFolder()
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "\"" + AppDomain.CurrentDomain.BaseDirectory + "\"");
                }
                catch { }
            }

            public void LogFromJs(string where, string detail)
            {
                Log.Write("[js] " + where + " -> " + detail);
            }

            public bool ConsumeDataUpdated()
            {
                if (!_form._dataUpdated) return false;
                _form._dataUpdated = false;
                return true;
            }
        }
    }

    /// <summary>
    /// 字体保险：本机缺字体时 new Font(...) 会抛 ArgumentException（值不在预期的范围内），
    /// 而这会发生在窗口构造阶段、直接把窗口弄没。这里逐个候选回退，最终一定返回可用字体。
    /// </summary>
    internal static class SafeFont
    {
        private static readonly string[] KaiNames = { "KaiTi", "STKaiti", "楷体", "SimSun", "宋体", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };

        public static Font Kai(float size) { return Pick(KaiNames, size); }

        public static Font Sans(float size)
        {
            string[] names = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun", "Arial" };
            return Pick(names, size);
        }

        private static Font Pick(string[] names, float size)
        {
            if (size <= 0f || size > 400f) size = 10f;
            foreach (string n in names)
            {
                try
                {
                    Font f = new Font(n, size);
                    if (f != null) return f;
                }
                catch { }
            }
            try { return new Font(FontFamily.GenericSansSerif, size); }
            catch { }
            try { return new Font(FontFamily.GenericSerif, size); }
            catch { }
            return SystemFonts.DefaultFont;
        }
    }

    /// <summary>
    /// 最后的兜底：把内置界面写成磁盘上的静态页面，用系统默认浏览器打开。
    /// 页面本身只负责显示，检索仍然由本程序算好后通过 fragment 传进去，本兜底不含任何检索逻辑。
    /// </summary>
    internal static class ClassicUI
    {
        /// <summary>把内置界面资源落盘，返回 index.html 路径；目录不可写时自动换下一个候选</summary>
        public static string Prepare(string dataDir)
        {
            List<string> cands = new List<string>();
            try
            {
                cands.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jigu", "web"));
            }
            catch { }
            try { cands.Add(Path.Combine(Path.GetTempPath(), "JiguWeb")); } catch { }
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir)) cands.Add(Path.Combine(baseDir, "jigu-web"));
            }
            catch { }

            foreach (string root in cands)
            {
                try
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                    int written = 0, failed = 0;
                    foreach (string rel in Assets.ListPaths())
                    {
                        if (rel == null || rel.Length == 0) continue;
                        if (string.Equals(rel, Corpus.DataFileName, StringComparison.OrdinalIgnoreCase)) continue;
                        byte[] blob = Assets.Get(rel);
                        if (blob == null) continue;
                        // 资源表里的键本来就是扁平文件名（index.html / styles.css ...），
                        // 页面里的相对引用正是按这个布局写的，直接并排落盘即可。
                        string name = Path.GetFileName(rel);
                        if (string.IsNullOrEmpty(name)) continue;
                        try { File.WriteAllBytes(Path.Combine(root, name), blob); written++; }
                        catch (Exception fex) { failed++; Log.Write("fallback write failed: " + name + " " + fex.Message); }
                    }
                    // 离线页面自己会用 fetch('corpus.json') 建索引，所以语料要放在同一目录
                    string corpusDst = Path.Combine(root, Corpus.DataFileName);
                    try
                    {
                        string corpusSrc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Corpus.DataFileName);
                        if (File.Exists(corpusSrc)) File.Copy(corpusSrc, corpusDst, true);
                        else
                        {
                            byte[] seed = Assets.Get("seed.json");
                            if (seed != null) File.WriteAllBytes(corpusDst, seed);
                        }
                    }
                    catch (Exception cex) { Log.Write("fallback corpus copy failed: " + cex.Message); }

                    string page = Path.Combine(root, "index.html");
                    if (File.Exists(page) && written > 0)
                    {
                        Log.Write("fallback page ready: " + page + " (files=" + written
                            + " failed=" + failed + ")");
                        return page;
                    }
                }
                catch (Exception ex) { Log.Write("fallback dir unusable: " + root + " -> " + ex.Message); }
            }
            Log.Error("ClassicUI.Prepare", new IOException("没有可写的目录用于落盘离线界面"));
            return null;
        }

        /// <summary>用系统默认浏览器打开（不限定 Edge/Chrome，关联到哪个就用哪个）</summary>
        public static bool OpenInBrowser(string pagePath)
        {
            if (string.IsNullOrEmpty(pagePath)) return false;
            try
            {
                string browser = Sys.FindFallbackBrowser();
                if (!string.IsNullOrEmpty(browser))
                {
                    System.Diagnostics.Process.Start(browser, "\"" + pagePath + "\"");
                    Log.Write("fallback browser: " + browser);
                    return true;
                }
            }
            catch (Exception ex) { Log.Write("fallback browser failed: " + ex.Message); }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pagePath) { UseShellExecute = true });
                Log.Write("fallback: shell open " + pagePath);
                return true;
            }
            catch (Exception ex) { Log.Error("ClassicUI.OpenInBrowser", ex); return false; }
        }
    }

    /// <summary>
    /// 内置原生界面：不依赖 WebView2，纯 WinForms 绘制的检索界面。
    /// 这是"窗口一定能打开"的保证——WebView2 出任何问题都能落到这里继续用。
    /// 检索完全由 Corpus 在后台线程完成，界面线程只负责显示。
    /// </summary>
    internal sealed class ClassicForm : Form
    {
        private readonly string _dataDir;
        private readonly string _dataBase;
        private readonly string _notice;

        private Corpus _corpus;
        private TextBox _input;
        private Button _go;
        private FlowLayoutPanel _list;
        private Label _status;
        private Label _terms;
        private volatile bool _busy;

        public ClassicForm(string dataDir, string dataBase)
            : this(dataDir, dataBase, null, null) { }

        public ClassicForm(string dataDir, string dataBase, string notice)
            : this(dataDir, dataBase, notice, null) { }

        /// <summary>prefillQuery：诊断用，窗口一显示就自动查一次，便于截图核对</summary>
        public ClassicForm(string dataDir, string dataBase, string notice, string prefillQuery)
        {
            _dataDir = dataDir;
            _dataBase = dataBase;
            _notice = notice;

            Text = "稽古 · 内置界面";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 780);
            MinimumSize = new Size(560, 520);
            BackColor = Color.FromArgb(248, 245, 238);
            ForeColor = Color.FromArgb(43, 42, 38);
            Font = SafeFont.Sans(10f);
            Icon = AppIcon.Get();

            BuildUi();
            _corpus = new Corpus();
            FormClosing += delegate(object s, FormClosingEventArgs e)
            { Log.Write("classic closing reason=" + e.CloseReason); };
            FormClosed += delegate(object s, FormClosedEventArgs e)
            { Log.Write("classic closed reason=" + e.CloseReason); };
            ThreadPool.QueueUserWorkItem(delegate
            {
                LoadCorpus();
                if (string.IsNullOrEmpty(prefillQuery)) return;
                // 预填查询：等书库真的就绪再查，避免"书库尚未就绪"
                for (int i = 0; i < 200; i++)
                {
                    Corpus c = _corpus;
                    if (c != null && c.IsReady) break;
                    Thread.Sleep(50);
                }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { _input.Text = prefillQuery; DoSearch(); }
                        catch (Exception ex) { Log.Error("prefill", ex); }
                    });
                }
                catch { }
            });
        }

        private void BuildUi()
        {
            // 停靠顺序：Fill 必须最先加入，Dock=Top 的控件才能从上方依次占位。
            // 反过来（先加 Top 再加 Fill）会让 Fill 控件吃掉整块客户区，页头会被挤到下面去。
            _list = new FlowLayoutPanel();
            _list.Dock = DockStyle.Fill;
            _list.FlowDirection = FlowDirection.TopDown;
            _list.WrapContents = false;
            _list.AutoScroll = true;
            _list.Padding = new Padding(26, 6, 26, 26);
            _list.BackColor = Color.FromArgb(248, 245, 238);
            Controls.Add(_list);

            _terms = new Label();
            _terms.Dock = DockStyle.Top;
            _terms.Height = 30;
            _terms.Padding = new Padding(32, 0, 32, 0);
            _terms.Font = SafeFont.Kai(10f);
            _terms.ForeColor = Color.FromArgb(140, 130, 114);
            _terms.Text = "写下你眼下进退两难的事，按 Ctrl+Enter 或点「稽古一问」";
            Controls.Add(_terms);

            Panel ask = new Panel();
            ask.Dock = DockStyle.Top;
            ask.Height = 88;
            ask.Padding = new Padding(30, 12, 30, 12);
            Controls.Add(ask);

            _input = new TextBox();
            _input.Multiline = true;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.BackColor = Color.FromArgb(253, 251, 244);
            _input.ForeColor = Color.FromArgb(43, 42, 38);
            _input.Font = SafeFont.Kai(12f);
            _input.Location = new Point(30, 12);
            _input.Size = new Size(760, 62);
            _input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _input.Text = "";
            ask.Controls.Add(_input);

            _go = new Button();
            _go.Text = "稽古一问";
            _go.Font = SafeFont.Kai(13f);
            _go.Size = new Size(140, 40);
            _go.Location = new Point(802, 22);
            _go.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _go.BackColor = Color.FromArgb(192, 57, 43);
            _go.ForeColor = Color.FromArgb(253, 249, 240);
            _go.FlatStyle = FlatStyle.Flat;
            _go.FlatAppearance.BorderSize = 0;
            _go.Click += delegate { DoSearch(); };
            ask.Controls.Add(_go);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 104;
            head.BackColor = Color.FromArgb(248, 245, 238);
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                try
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLine(new Pen(Color.FromArgb(196, 186, 168), 1f), 28, 96, head.Width - 28, 96);
                }
                catch { }
            };
            Controls.Add(head);

            PictureBox seal = new PictureBox();
            seal.Size = new Size(52, 52);
            seal.Location = new Point(30, 20);
            seal.SizeMode = PictureBoxSizeMode.Zoom;
            try { Icon ic = AppIcon.Get(); if (ic != null) seal.Image = ic.ToBitmap(); } catch { }
            head.Controls.Add(seal);

            Label title = new Label();
            title.Text = "稽古";
            title.Font = SafeFont.Kai(24f);
            title.ForeColor = Color.FromArgb(192, 57, 43);
            title.AutoSize = true;
            title.Location = new Point(96, 18);
            head.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "二十四史情境检索 · 内置界面";
            sub.Font = SafeFont.Kai(11f);
            sub.ForeColor = Color.FromArgb(120, 112, 98);
            sub.AutoSize = true;
            sub.Location = new Point(99, 56);
            head.Controls.Add(sub);

            _status = new Label();
            _status.Text = string.IsNullOrEmpty(_notice) ? "正在备书……" : _notice;
            _status.Font = SafeFont.Kai(10f);
            _status.ForeColor = Color.FromArgb(140, 130, 114);
            _status.AutoSize = true;
            _status.Location = new Point(100, 76);
            head.Controls.Add(_status);

            AcceptButton = null;
            _input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
            };
        }

        private void LoadCorpus()
        {
            try
            {
                // 内置界面没有藏书阁，但勾选记录是共用的：用户在外面的藏书阁选过的史书，
                // 掉到内置界面也该查得到，否则换个界面同样的词给出不一样的 Top3。
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                Corpus c = new Corpus();
                c.LoadFrom(baseDir, Library.ShardPaths(baseDir, Library.LoadSelection(baseDir, null)));
                _corpus = c;
                ShowStatus("书库已就绪：" + c.DocCount + " 条史料 · " + c.TermCount + " 个索引词");
            }
            catch (Exception ex)
            {
                Log.Error("ClassicForm corpus", ex);
                ShowStatus("书库读取失败，请检查程序目录下的 corpus.json");
            }
        }

        private void ShowStatus(string text)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate { try { _status.Text = text; } catch { } });
            }
            catch { }
        }

        private void DoSearch()
        {
            string q = _input.Text;
            if (q == null) q = "";
            q = q.Trim();
            if (q.Length == 0)
            {
                _terms.Text = "请先写下你遇到的难题。";
                return;
            }
            if (_busy) return;
            Corpus corpus = _corpus;
            if (corpus == null || !corpus.IsReady)
            {
                _terms.Text = "书库尚未就绪，请稍候再试。";
                return;
            }
            _busy = true;
            _go.Enabled = false;
            _terms.Text = "正在检索……";
            _list.Controls.Clear();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                List<Corpus.SearchHit> hits = null;
                try { hits = corpus.SearchScored(q, 3); }
                catch (Exception ex) { Log.Error("ClassicForm search", ex); err = ex.Message; }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { RenderHits(q, hits, err); }
                        catch (Exception rex) { Log.Error("ClassicForm render", rex); }
                        finally { _busy = false; _go.Enabled = true; }
                    });
                }
                catch { }
            });
        }

        private void RenderHits(string query, List<Corpus.SearchHit> hits, string err)
        {
            _list.SuspendLayout();
            _list.Controls.Clear();

            if (err != null)
            {
                _terms.Text = "检索未完成：" + err;
                _list.ResumeLayout();
                return;
            }
            if (hits == null || hits.Count == 0)
            {
                _terms.Text = "没有找到相近的旧事，换个说法再试。";
                _list.ResumeLayout();
                return;
            }

            _terms.Text = "检索：" + Corpus.Normalize(query) + "　命中 " + hits.Count + " 条";
            int rank = 0;
            foreach (Corpus.SearchHit h in hits)
            {
                rank++;
                _list.Controls.Add(BuildCard(rank, h));
            }
            _list.ResumeLayout();
        }

        private Control BuildCard(int rank, Corpus.SearchHit hit)
        {
            CorpusDoc d = hit == null ? null : hit.Doc;
            int width = Math.Max(360, _list.ClientSize.Width - 76);

            Panel card = new Panel();
            card.Width = width;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.MinimumSize = new Size(width, 0);
            card.Margin = new Padding(0, 0, 0, 16);
            card.BackColor = Color.FromArgb(253, 251, 244);
            card.Padding = new Padding(18, 14, 18, 16);
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                try
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (Pen p = new Pen(Color.FromArgb(206, 196, 178), 1f))
                        e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
                    using (Pen p = new Pen(Color.FromArgb(192, 57, 43), 3f))
                        e.Graphics.DrawLine(p, 0, 0, 0, card.Height);
                }
                catch { }
            };

            // 用两列表格排版（标签列固定，内容列自适应），窄窗口下文字自动换行而不是被裁掉
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            // 标签列必须够宽：不够时中文标签会折成竖排
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Width = Math.Max(200, width - 40);
            grid.Margin = new Padding(0);
            grid.Padding = new Padding(0);
            grid.BackColor = Color.Transparent;

            Label head = new Label();
            head.AutoSize = true;
            head.MaximumSize = new Size(Math.Max(200, width - 60), 0);
            head.Font = SafeFont.Kai(14f);
            head.ForeColor = Color.FromArgb(192, 57, 43);
            head.Margin = new Padding(0, 0, 0, 2);
            head.Text = (d == null ? "史料" : ("〔" + (string.IsNullOrEmpty(d.Book) ? "未题书名" : d.Book) + "〕"
                + (string.IsNullOrEmpty(d.Title) ? "" : d.Title)));
            grid.Controls.Add(head, 0, 0);
            grid.SetColumnSpan(head, 2);

            Label meta = new Label();
            meta.AutoSize = true;
            meta.MaximumSize = new Size(Math.Max(200, width - 60), 0);
            meta.Font = SafeFont.Kai(9.5f);
            meta.ForeColor = Color.FromArgb(140, 130, 114);
            meta.Margin = new Padding(0, 0, 0, 8);
            meta.Text = "第 " + rank + " 条 · 相关度 " + (hit == null ? 0d : Math.Round(hit.Score, 1))
                + (d != null && d.Chapter != null && d.Chapter.Length > 0 ? " · " + d.Chapter : "");
            grid.Controls.Add(meta, 0, 1);
            grid.SetColumnSpan(meta, 2);

            int row = 2;
            AddField(grid, ref row, width, "原文摘录", d == null ? "" : d.Original,
                Color.FromArgb(43, 42, 38), true);
            AddField(grid, ref row, width, "现代文翻译", d == null ? "" : d.Translation,
                Color.FromArgb(70, 66, 58), false);
            AddField(grid, ref row, width, "核心人物",
                d == null || d.Figures == null ? "" : string.Join("、", d.Figures),
                Color.FromArgb(70, 66, 58), false);
            AddField(grid, ref row, width, "关键决策", d == null ? "" : d.Decision,
                Color.FromArgb(70, 66, 58), false);
            AddField(grid, ref row, width, "最终结果", d == null ? "" : d.Outcome,
                Color.FromArgb(110, 60, 50), false);

            card.Controls.Add(grid);
            return card;
        }

        private void AddField(TableLayoutPanel grid, ref int row, int width,
            string label, string value, Color color, bool kai)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Font = SafeFont.Kai(10f);
            l.ForeColor = Color.FromArgb(160, 148, 130);
            l.Margin = new Padding(0, 2, 8, 10);
            l.Text = label;
            grid.Controls.Add(l, 0, row);

            Label v = new Label();
            v.AutoSize = true;
            v.MaximumSize = new Size(Math.Max(200, width - 130), 0);
            v.Font = kai ? SafeFont.Kai(11.5f) : SafeFont.Sans(10f);
            v.ForeColor = color;
            v.Margin = new Padding(0, 0, 0, 10);
            v.Text = string.IsNullOrEmpty(value) ? "（无）" : value;
            grid.Controls.Add(v, 1, row);

            row++;
        }
    }
}