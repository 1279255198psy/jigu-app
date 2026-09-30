// FILE: jigu-app/src/Update.cs
// 双路热更新：史料数据增量更新 + 程序本体热替换；以及系统自适应检测。
//
// 路径策略：一律以 AppDomain.CurrentDomain.BaseDirectory 为基准，
// 绝不出现 C:\Program Files 这类绝对路径，因此安装到任何目录都能工作。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jigu
{
    /// <summary>远端 data_version.json / 本地 data_version.json</summary>
    internal sealed class RemoteManifest
    {
        public string Version = "";
        // 清单里的 "source_url" 是给人看的来源标注（见 docs\更新服务托管说明.md 的格式说明），
        // 程序不读它 —— 更新源本来就在 UpdateSource 里，不需要再从数据文件里问一遍。
        public Dictionary<string, string> Books = new Dictionary<string, string>();
        /// <summary>
        /// 平文件（name -> sha256）：直接下载覆盖，不参与「按书切分再合并」。
        /// 目前用于 labels.json —— 情境标签表是全项目最需要频繁迭代的资产
        /// （一个词的缺口就让某类提问检索不到东西），它不该每次都靠重新编译 exe 来更新。
        /// 相对路径仍复用 Paths 映射。
        /// </summary>
        public Dictionary<string, string> Files = new Dictionary<string, string>();
        public Dictionary<string, string> Paths = new Dictionary<string, string>();
    }

    /// <summary>远端 app_version.json（或从 Release 附件推导出来的等价信息）</summary>
    internal sealed class AppManifest
    {
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string Notes = "";
        public long Size;
        public bool Mandatory;
    }

    /// <summary>GitHub Release 里的一个附件</summary>
    internal sealed class GitHubAsset
    {
        public string Name = "";
        public string Url = "";
        public long Size;
    }

    /// <summary>一个 GitHub Release（只保留更新用得上的字段）</summary>
    internal sealed class GitHubRelease
    {
        public string Tag = "";
        public string Version = "";
        public string Body = "";
        public List<GitHubAsset> Assets = new List<GitHubAsset>();

        /// <summary>按文件名找附件：先精确匹配，再退回按文件名（末段）匹配。找不到返回 null。</summary>
        public GitHubAsset Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string baseName = Path.GetFileName(name);
            GitHubAsset loose = null;
            foreach (GitHubAsset a in Assets)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
                if (loose == null && string.Equals(a.Name, baseName, StringComparison.OrdinalIgnoreCase))
                    loose = a;
            }
            // data_version.json 的 paths 可能写 "books/shiji.json"，而附件名只有末段
            return loose;
        }
    }

    /// <summary>
    /// 更新源解析：把「文件名」变成可下载的 URL。
    /// 源由一行文本决定（环境变量 → exe 同目录的配置文件 → 编译内默认值）：
    ///
    ///   github:owner/repo     从该仓库「最新 Release」的附件里查（默认）
    ///   https://… / file://…  静态基址拼接（既有行为：自建镜像、内网共享目录、离线测试）
    ///
    /// 取不到 URL（离线、该 Release 没有这个附件、被限流）一律返回 null，
    /// 调用方当作「没有更新」静默降级 —— 绝不弹框、绝不卡界面。
    /// </summary>
    internal static class UpdateSource
    {
        public const string GitHubPrefix = "github:";
        /// <summary>默认从发布仓库的 Release 抓，不再依赖任何需要手工上传的静态目录</summary>
        public const string DefaultAppSpec = "github:1279255198psy/jigu-app";
        public const string DefaultApiBase = "https://api.github.com";

        /// <summary>
        /// Release 缓存的有效期。设成几分钟而不是永久或 0：
        /// 一轮检查（史料 + 程序）在毫秒内跑完，共用同一次 API 请求，不浪费限流配额；
        /// 而常驻托盘的程序过一阵也会重新取，不会一直拿着启动时那份清单。
        /// </summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private static readonly object Gate = new object();
        private static GitHubRelease _release;
        private static bool _loaded;
        private static string _releaseSpec;
        private static DateTime _releaseAtUtc;

        /// <summary>丢掉 Release 缓存，强制下次重新取</summary>
        public static void Reset()
        {
            lock (Gate) { _release = null; _loaded = false; _releaseSpec = null; }
        }

        /// <summary>程序本体更新源</summary>
        public static string AppSpec()
        {
            string v = Env("JIGU_UPDATE_BASE");
            if (v != null) return v;
            v = FileSpec("update_base.txt");
            if (v != null) return v;
            return DefaultAppSpec;
        }

        /// <summary>
        /// 史料数据源。没单独配置时跟随程序本体源 —— 发版时数据文件与安装包挂在同一个
        /// Release 上，所以默认零配置就能工作。想给数据单独走国内镜像的，放一个
        /// data_base.txt 即可覆盖，不必重新编译程序。
        /// </summary>
        public static string DataSpec()
        {
            // JIGU_UPDATE_BASE 排最前：--test-update 的离线模拟靠它一个变量接管两条通道
            string v = Env("JIGU_UPDATE_BASE");
            if (v != null) return v;
            v = Env("JIGU_DATA_BASE");
            if (v != null) return v;
            v = FileSpec("data_base.txt");
            if (v != null) return v;
            return AppSpec();
        }

        /// <summary>GitHub REST API 的基址（测试可指向本地目录，Net.Get 认 file://）</summary>
        public static string ApiBase()
        {
            string v = Env("JIGU_GITHUB_API");
            return v == null ? DefaultApiBase : v.TrimEnd('/');
        }

        /// <summary>spec 形如 github:owner/repo 时返回 "owner/repo"，否则返回 null</summary>
        public static string RepoOf(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (!spec.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string repo = spec.Substring(GitHubPrefix.Length).Trim().Trim('/');
            // 必须是 owner/repo 两段，光一个名字不构成仓库
            return repo.IndexOf('/') > 0 ? repo : null;
        }

        /// <summary>文件名 → URL；解析不出返回 null</summary>
        public static string UrlFor(string spec, string fileName)
        {
            if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(fileName)) return null;
            string repo = RepoOf(spec);
            if (repo != null)
            {
                GitHubRelease rel = Latest(repo);
                if (rel == null) return null;
                GitHubAsset a = rel.Find(fileName);
                return a == null ? null : a.Url;
            }
            return spec.TrimEnd('/') + "/" + fileName;
        }

        public static string AppUrl(string fileName) { return UrlFor(AppSpec(), fileName); }
        public static string DataUrl(string fileName) { return UrlFor(DataSpec(), fileName); }

        /// <summary>当前最新 Release；同一个 repo 在缓存期内只取一次</summary>
        public static GitHubRelease Latest(string repo)
        {
            if (string.IsNullOrEmpty(repo)) return null;
            lock (Gate)
            {
                if (_loaded && string.Equals(_releaseSpec, repo, StringComparison.OrdinalIgnoreCase)
                    && (DateTime.UtcNow - _releaseAtUtc) < CacheTtl)
                    return _release;
                // 在锁里发请求是有意的：多个线程（数据检查与程序检查）同时到达时，
                // 只放一个出去打 API，其余复用同一份结果。
                GitHubRelease rel = GitHubApi.FetchLatest(repo);
                // 取失败也缓存（但顶多缓存 TTL 那么久）：否则每次调用都要白等一次超时
                _release = rel;
                _releaseSpec = repo;
                _loaded = true;
                _releaseAtUtc = DateTime.UtcNow;
                return _release;
            }
        }

        /// <summary>供自检与界面显示当前源</summary>
        public static string Describe()
        {
            string app = AppSpec(), data = DataSpec();
            return string.Equals(app, data, StringComparison.OrdinalIgnoreCase)
                ? app : (app + "（数据：" + data + "）");
        }

        private static string Env(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(v)) return null;
                v = v.Trim();
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        private static string FileSpec(string name)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
                if (!File.Exists(p)) return null;
                string t = File.ReadAllText(p, Encoding.UTF8).Trim();
                return t.Length == 0 ? null : t;
            }
            catch { return null; }
        }
    }

    /// <summary>GitHub Releases API 的最小客户端（只做「取最新 Release」一件事）</summary>
    internal static class GitHubApi
    {
        private const int TimeoutMs = 8000;

        public static GitHubRelease FetchLatest(string repo)
        {
            string url = UpdateSource.ApiBase() + "/repos/" + repo + "/releases/latest";
            string err;
            byte[] bytes = Net.GetEx(
                Net.Client("application/vnd.github+json"), url, TimeoutMs, out err);
            if (bytes == null)
            {
                // 记下来是为了排查「连不上」还是「被限流」，界面上不区分，都是静默降级
                Log.Write("github release fetch failed: " + repo + " (" + err + ")");
                return null;
            }
            try { return Parse(Encoding.UTF8.GetString(bytes)); }
            catch (Exception ex) { Log.Error("github release parse", ex); return null; }
        }

        public static GitHubRelease Parse(string json)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;

            GitHubRelease r = new GitHubRelease();
            r.Tag = MiniJson.Str(root, "tag_name").Trim();
            r.Version = TrimTag(r.Tag);
            r.Body = MiniJson.Str(root, "body");

            object assets;
            if (root.TryGetValue("assets", out assets))
            {
                List<object> list = assets as List<object>;
                if (list != null)
                {
                    foreach (object o in list)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        GitHubAsset a = new GitHubAsset();
                        a.Name = MiniJson.Str(d, "name");
                        a.Url = MiniJson.Str(d, "browser_download_url");
                        if (string.IsNullOrEmpty(a.Name) || string.IsNullOrEmpty(a.Url)) continue;
                        object sz;
                        // MiniJson 把数字解析成 double
                        if (d.TryGetValue("size", out sz) && sz is double && (double)sz > 0)
                            a.Size = (long)(double)sz;
                        r.Assets.Add(a);
                    }
                }
            }
            return r;
        }

        /// <summary>tag v0.2.0 → 0.2.0（版本比较只认数字段）</summary>
        public static string TrimTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            tag = tag.Trim();
            if (tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V')) tag = tag.Substring(1);
            return tag;
        }
    }

    internal static class Net
    {
        /// <summary>
        /// 打开 TLS 1.2。**这条不加，自动更新在任何机器上都永远不会成功。**
        ///
        /// 程序是用 v4.0.30319 的 csc 编的，也没有 app.config，于是 .NET 按旧版默认
        /// 只开 Ssl3 | Tls（也就是 TLS 1.0）。GitHub 和它前面的 CDN 早就拒绝 1.0/1.1，
        /// 结果是握手阶段直接失败 —— 而失败被静默降级吞掉，用户看到的现象只是
        /// 「从来没有新版本」，没有任何报错。（只有显式声明了 .NET 4.7+ 的
        /// supportedRuntime 的程序才会拿到 SystemDefault，我们没有那份配置。）
        ///
        /// 用 |= 而不是直接赋值：更新源可以被指向自建的旧镜像（update_base.txt），
        /// 把协议集收窄成「只有 1.2」会让那些镜像连不上。
        /// 3072 是 Tls12 的数值 —— 这个枚举成员在 4.0 的引用程序集里还不存在，只能从整数转。
        /// </summary>
        static Net()
        {
            const int Tls12 = 3072;
            try
            {
                // 0 == SystemDefault（.NET 4.7+ 才有），那表示「交给系统决定」，
                // 本来就能协商 1.2，反倒不该在这里覆盖成固定的协议集。
                if ((int)ServicePointManager.SecurityProtocol != 0)
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)Tls12;
                }
            }
            catch (NotSupportedException) { }
            catch (NotImplementedException) { }
        }

        /// <summary>
        /// 当前更新源的可读描述，仅供界面与自检显示。
        /// 拼 URL 一律走 <see cref="UpdateSource.UrlFor"/> —— 这个字符串对 GitHub 模式
        /// 不是一个能直接下载的地址。
        /// </summary>
        public static string BaseUrl() { return UpdateSource.Describe(); }

        public static WebClient Client() { return Client(null); }

        public static WebClient Client(string accept)
        {
            WebClient c = new WebClient();
            c.Headers.Add("User-Agent", "jigu/" + AppVer.Number);
            if (!string.IsNullOrEmpty(accept)) c.Headers.Add("Accept", accept);
            c.Encoding = Encoding.UTF8;
            try { c.Proxy = WebRequest.DefaultWebProxy; } catch { }
            return c;
        }

        /// <summary>下载字节；失败/超时返回 null（调用方一律静默降级）</summary>
        public static byte[] Get(WebClient client, string url, int timeoutMs)
        {
            string ignored;
            return GetEx(client, url, timeoutMs, out ignored);
        }

        /// <summary>同上，另外带回失败原因 —— 只写日志，用来区分「断网」与「被 GitHub 限流」</summary>
        public static byte[] GetEx(WebClient client, string url, int timeoutMs, out string error)
        {
            string reason = "";
            byte[] bytes = OffUiThread<byte[]>(delegate() { return Fetch(client, url, timeoutMs, out reason); });
            error = reason;
            return bytes;
        }

        /// <summary>GetEx 的本体。不要直接调用 —— 它「发起异步再 WaitOne」，必须脱开 UI 线程的同步上下文。</summary>
        private static byte[] Fetch(WebClient client, string url, int timeoutMs, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(url)) { error = "空地址"; return null; }

            // file:// support: the "update source" may be a local folder. Handy for offline
            // end-to-end testing and for distributing updates from a network share.
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string path = new Uri(url).LocalPath;
                    if (File.Exists(path)) return File.ReadAllBytes(path);
                    error = "文件不存在";
                    return null;
                }
                catch (Exception ex) { Log.Error("file get " + url, ex); error = ex.Message; return null; }
            }

            ManualResetEvent done = new ManualResetEvent(false);
            byte[] result = null;
            Exception failure = null;
            client.DownloadDataCompleted += delegate(object s, DownloadDataCompletedEventArgs e)
            {
                if (e.Error != null) failure = e.Error; else result = e.Result;
                done.Set();
            };
            try { client.DownloadDataAsync(new Uri(url)); }
            catch (Exception ex) { failure = ex; }
            if (failure != null) { error = Describe(failure); return null; }
            if (!done.WaitOne(timeoutMs))
            {
                try { client.CancelAsync(); } catch { }
                error = "超时 " + timeoutMs + "ms";
                return null;
            }
            // 异步失败也要在这里认一次：网络层面的失败（连不上、证书不信任、
            // 403 限流、404）几乎都是走异步回调的，上面那次检查发生在回调之前，
            // 基本永远看不到。漏掉这一句的后果不是行为错（两边都返回 null），
            // 而是日志里只剩一个空括号 —— 排查时完全看不出「断网」还是「被限流」，
            // 而这正是这个 out 参数存在的理由。
            if (failure != null) { error = Describe(failure); return null; }
            return result;
        }

        /// <summary>
        /// 在「没有同步上下文」的状态下执行 work，结束后原样还原。
        /// <para>
        /// WebClient 的 *Async 系列把完成回调 Post 到**发起时**的 SynchronizationContext 上
        /// （AsyncOperationManager 的默认行为），而 Fetch/安装包下载都是「发起异步 + 立刻
        /// WaitOne」这种写法。平时这段代码跑在后台线程上（SilentUpdate），Current 是 null，
        /// 回调走线程池，一切正常；可一旦跑在 WinForms 的 UI 线程上，Current 就是
        /// WindowsFormsSynchronizationContext，回调被塞进消息队列，而消息泵此刻正被 WaitOne
        /// 堵着 —— 回调永远执行不到，必然走到「超时」分支。
        /// </para>
        /// <para>
        /// 这条路径是真实存在的：界面上的「检查版本」按钮经 WebView2 的宿主对象桥直达 UI 线程
        /// （HostBridge.CheckAppUpdate -> RunAppCheck），于是**那个按钮在联网正常时也永远
        /// 只报「未连上更新服务器」**，且耗时恰好等于超时值（实测 8014ms / 预期 8000ms）。
        /// 断网与否都一样，跟代理、梯子、证书全无关系 —— 所以它看起来才那么像「连不上」。
        /// </para>
        /// </summary>
        internal static T OffUiThread<T>(Func<T> work)
        {
            SynchronizationContext saved = SynchronizationContext.Current;
            if (saved != null) SynchronizationContext.SetSynchronizationContext(null);
            try { return work(); }
            finally { if (saved != null) SynchronizationContext.SetSynchronizationContext(saved); }
        }

        /// <summary>把异常折成一句可读原因（含 HTTP 状态与限流余量）</summary>
        private static string Describe(Exception ex)
        {
            WebException we = ex as WebException;
            if (we == null) return ex.Message;
            HttpWebResponse resp = we.Response as HttpWebResponse;
            if (resp == null) return we.Status + ": " + we.Message;
            using (resp)
            {
                string extra = "";
                try
                {
                    string left = resp.Headers["X-RateLimit-Remaining"];
                    if (!string.IsNullOrEmpty(left)) extra = " 限流余量=" + left;
                }
                catch { }
                return we.Status + " HTTP " + (int)resp.StatusCode + extra;
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string Sha256File(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                return Sha256Hex(File.ReadAllBytes(path));
            }
            catch { return ""; }
        }
    }

    internal static class AppVer
    {
        /// <summary>
        /// 程序版本号。由 build.ps1 从 <c>$version</c> 生成到 src/Version.g.cs，
        /// 不要在这里手写 —— 版本号与构建产物不一致会让更新比对静默失效。
        /// </summary>
        public const string Number = BuildInfo.Version;
    }

    /// <summary>系统自适应：判断 Windows 版本、架构与可用的界面渲染方式</summary>
    internal static class Sys
    {
        public static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                System.Version v = Environment.OSVersion.Version;
                sb.Append("Windows ").Append(v.Major).Append('.').Append(v.Minor).Append('.')
                  .Append(v.Build);
                if (v.Major == 10 && v.Build >= 22000) sb.Append("(Win11)");
                else if (v.Major == 10) sb.Append("(Win10)");
                else if (v.Major == 6 && v.Minor == 3) sb.Append("(8.1)");
                else if (v.Major == 6 && v.Minor == 2) sb.Append("(8)");
                else if (v.Major == 6 && v.Minor == 1) sb.Append("(7)");
            }
            catch { sb.Append("Windows(unknown)"); }
            sb.Append(" / ").Append(Environment.Is64BitOperatingSystem ? "x64" : "x86");
            sb.Append(" / .NET ").Append(Environment.Version);
            return sb.ToString();
        }

        /// <summary>是否满足最低要求（Win7 SP1 及以上）</summary>
        public static bool IsSupported()
        {
            try
            {
                System.Version v = Environment.OSVersion.Version;
                if (v.Major > 6) return true;
                if (v.Major == 6 && v.Minor >= 1) return true;
                return false;
            }
            catch { return true; }
        }

        /// <summary>系统里是否有可用的 Edge / Chrome（WebView2 失效时的降级出口）</summary>
        public static string FindFallbackBrowser()
        {
            string[] cands =
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            };
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; }
                catch { }
            }
            return null;
        }
    }

    /// <summary>数据更新：比对远端 data_version.json，只下载变化的史料文件并合并进 corpus.json</summary>
    internal static class DataUpdater
    {
        public sealed class Report
        {
            public bool Checked;
            public string LocalVersion = "";
            public string RemoteVersion = "";
            public List<string> Changed = new List<string>();
            /// <summary>需要更新的平文件（区别于按书合并的 Changed）</summary>
            public List<string> FilesChanged = new List<string>();
            public List<string> Failed = new List<string>();
            public List<string> Merged = new List<string>();
            public List<string> FilesUpdated = new List<string>();
            public string Message = "";
        }

        /// <summary>
        /// 清单 books 里的保留键：它指代**整份 corpus.json**，不是某一本书。
        /// build.ps1 生成 data_version.json 时写的就是这个键，Check 也是拿它去比对
        /// 磁盘上整个 corpus.json 的哈希（见 Check 里的特判），语义多处一致。
        /// Apply 必须同样认得它 —— 详见 Apply 里组装 bookDocs 的那段注释。
        /// </summary>
        internal const string WholeCorpusKey = "corpus";

        /// <summary>读取本地 data_version.json（exe 同目录）</summary>
        private static RemoteManifest ReadLocal(string baseDir)
        {
            RemoteManifest m = new RemoteManifest();
            string p = Path.Combine(baseDir, Corpus.DataVersionFile);
            if (!File.Exists(p)) return m;
            try
            {
                Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(p, Encoding.UTF8))
                    as Dictionary<string, object>;
                if (root == null) return m;
                m.Version = MiniJson.Str(root, "version");
                object books, paths, files;
                if (root.TryGetValue("books", out books) && books is Dictionary<string, object>)
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)books)
                        m.Books[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                if (root.TryGetValue("files", out files) && files is Dictionary<string, object>)
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)files)
                        m.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                if (root.TryGetValue("paths", out paths) && paths is Dictionary<string, object>)
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)paths)
                        m.Paths[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Log.Error("read local data_version", ex); }
            return m;
        }

        public static Report Check(string baseDir)
        {
            Report r = new Report();
            RemoteManifest local = ReadLocal(baseDir);
            r.LocalVersion = local.Version;
            byte[] bytes = Net.Get(Net.Client(), UpdateSource.DataUrl("data_version.json"), 7000);
            if (bytes == null) { r.Message = "未连上数据源（离线使用本地史料）"; return r; }
            RemoteManifest remote;
            try { remote = ParseManifest(Encoding.UTF8.GetString(bytes)); }
            catch (Exception ex) { Log.Error("parse remote data_version", ex); r.Message = "远端版本文件无法解析"; return r; }
            // ParseManifest 内部对「解析不出来」是返回一份空清单，不抛异常 —— 上面那个
            // catch 因此永远走不到。以前的结果是：一份损坏的清单被当成「远端版本号为空、
            // 零项变化」，界面显示「史料库已是最新」，用户以为一切正常。这里显式认出来。
            // 合法清单必定带 version（build.ps1 写的），空版本 + 空 books/files 只可能是坏的。
            if (string.IsNullOrEmpty(remote.Version) && remote.Books.Count == 0 && remote.Files.Count == 0)
            {
                Log.Write("remote data_version 解析不出任何字段，按无法解析处理");
                r.Message = "远端版本文件无法解析";
                return r;
            }

            r.Checked = true;
            r.RemoteVersion = remote.Version;
            string corpusPath = Path.Combine(baseDir, Corpus.DataFileName);
            foreach (KeyValuePair<string, string> kv in remote.Books)
            {
                string have = local.Books.ContainsKey(kv.Key) ? local.Books[kv.Key] : "";
                if (kv.Key == WholeCorpusKey) have = Net.Sha256File(corpusPath);
                if (!string.Equals(have, kv.Value, StringComparison.OrdinalIgnoreCase)) r.Changed.Add(kv.Key);
            }
            // 平文件：拿远端清单里的 hash 比对本机实际文件的 hash
            foreach (KeyValuePair<string, string> kv in remote.Files)
            {
                string name = Path.GetFileName(kv.Key);      // 防目录穿越
                if (string.IsNullOrEmpty(name)) continue;
                string have = Net.Sha256File(Path.Combine(baseDir, name));
                if (!string.Equals(have, kv.Value, StringComparison.OrdinalIgnoreCase))
                    r.FilesChanged.Add(name);
            }

            int totalChanged = r.Changed.Count + r.FilesChanged.Count;
            r.Message = totalChanged == 0
                ? "史料库已是最新（v" + remote.Version + "）"
                : ("发现 " + totalChanged + " 项更新");
            Log.Write("data check: local=" + local.Version + " remote=" + remote.Version
                + " changed=" + r.Changed.Count + " files=" + r.FilesChanged.Count);
            return r;
        }

        private static RemoteManifest ParseManifest(string text)
        {
            RemoteManifest m = new RemoteManifest();
            Dictionary<string, object> root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null) return m;
            m.Version = MiniJson.Str(root, "version");
            object books, paths, files;
            if (root.TryGetValue("books", out books) && books is Dictionary<string, object>)
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)books)
                    m.Books[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            if (root.TryGetValue("files", out files) && files is Dictionary<string, object>)
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)files)
                    m.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            if (root.TryGetValue("paths", out paths) && paths is Dictionary<string, object>)
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)paths)
                    m.Paths[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return m;
        }

        /// <summary>
        /// 执行增量更新：下载变化的史料文件 -> 校验 -> 合并进 corpus.json -> 更新本地清单。
        /// 任何一步失败都只记录、不打断用户。
        /// </summary>
        public static Report Apply(string baseDir, Report plan)
        {
            Report r = plan ?? new Report();
            if (!r.Checked) { return r; }
            string corpusPath = Path.Combine(baseDir, Corpus.DataFileName);
            byte[] current = File.Exists(corpusPath) ? File.ReadAllBytes(corpusPath) : null;
            Dictionary<string, List<string>> bookDocs = new Dictionary<string, List<string>>();
            if (current != null)
            {
                int badLocal;
                bookDocs = SplitByBook(current, out badLocal);
                if (badLocal > 0)
                {
                    // 本地 corpus.json 自己就切不出完整文档。典型成因是早期版本那个
                    // 「拿字符下标去切字节数组」的合并 bug 把它写坏过。
                    // 这种情况下按书合并只会把坏掉的这一半原样带进新文件，而且坏的
                    // 是本地，再更新多少次都不会自愈 —— 所以整份丢掉，让下面的循环
                    // 用远端那一整份 corpus 重新填满。远端清单里 books.corpus 是拿
                    // 磁盘上 corpus.json 的真实哈希比的（见 Check），所以中招的机器
                    // 必然每一轮都会把 corpus 判为「已变化」，这条自愈路径一定走得到。
                    Log.Write("本地语料有 " + badLocal + " 条不是完整 JSON，改为整份重下");
                    bookDocs.Clear();
                }
            }

            WebClient client = Net.Client();
            // 用远端清单里的 paths 映射取相对路径；没有映射时才回退到 <book>/<book>.json
            RemoteManifest remoteManifest = null;
            try
            {
                byte[] mv = Net.Get(client, UpdateSource.DataUrl("data_version.json"), 7000);
                if (mv != null) remoteManifest = ParseManifest(Encoding.UTF8.GetString(mv));
            }
            catch (Exception ex) { Log.Error("reload remote manifest", ex); }

            // 远端这一轮取回的内容先攒着，等全部取完再决定怎么并进 bookDocs。
            // 分开攒是因为「整份语料」这个键必须整体替换本地的按书分组，
            // 而同一轮里按书下发的分组又要叠在它上面（详见下面的组装段）。
            Dictionary<string, List<string>> perBook = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> wholeCorpus = null;

            foreach (string book in r.Changed)
            {
                string rel = null;
                if (remoteManifest != null) remoteManifest.Paths.TryGetValue(book, out rel);
                if (string.IsNullOrEmpty(rel))
                    rel = book.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? book : (book + "/" + book + ".json");
                byte[] data = Net.Get(client, UpdateSource.DataUrl(rel), 20000);
                if (data == null) { r.Failed.Add(book); Log.Write("data fetch failed: " + rel); continue; }
                if (data.Length < 32) { r.Failed.Add(book); continue; }
                // 先跟清单里的哈希对上再说。下面的形状检查只能挡住「下载被截断」，
                // 挡不住「内容被整份替换成另一份合法语料」—— 清单里 books.corpus
                // 就是发布方那份 corpus.json 的 sha256，两边的字节同源，比对是可靠的。
                string wantHash;
                if (remoteManifest != null && remoteManifest.Books.TryGetValue(book, out wantHash)
                    && !string.IsNullOrEmpty(wantHash))
                {
                    string gotHash = Net.Sha256Hex(data);
                    if (!string.Equals(gotHash, wantHash, StringComparison.OrdinalIgnoreCase))
                    {
                        r.Failed.Add(book);
                        Log.Write("data rejected: " + rel + " 哈希与清单不符（清单 " + wantHash
                            + " / 实际 " + gotHash + "），已跳过");
                        continue;
                    }
                }
                // 必须是合法语料：以 [ 或 { 开头，且含 items 或 book
                string head = Encoding.UTF8.GetString(data, 0, Math.Min(256, data.Length));
                if (head.TrimStart().Length == 0) { r.Failed.Add(book); continue; }
                char first = head.TrimStart()[0];
                if (first != '[' && first != '{') { r.Failed.Add(book); continue; }

                string name = Path.GetFileNameWithoutExtension(rel);
                int badRemote;
                List<string> docBooks;
                List<string> docs = ExtractBookDocs(data, name, out docBooks, out badRemote);
                // 上面那两道头部检查只看开头 64/256 字节，挡不住「下载被截断」。
                // 真按条切一遍才知道全不全；不全就整项跳过，绝不让残片进新语料。
                if (badRemote > 0)
                {
                    r.Failed.Add(book);
                    Log.Write("data rejected: " + rel + " 有 " + badRemote + " 条不是完整 JSON，已跳过");
                    continue;
                }
                if (docs.Count == 0)
                {
                    r.Failed.Add(book);
                    Log.Write("data rejected: " + rel + " 里没有可用的史料条目");
                    continue;
                }
                bool whole = string.Equals(book, WholeCorpusKey, StringComparison.OrdinalIgnoreCase);
                if (whole) wholeCorpus = docs;
                else
                {
                    // 按文档自己的 book 分组（键见 ExtractBookDocs 的注释），
                    // 这样与本地那侧的键完全同源，第二轮不会裂成两组。
                    for (int i = 0; i < docs.Count; i++)
                    {
                        List<string> g;
                        if (!perBook.TryGetValue(docBooks[i], out g))
                        {
                            g = new List<string>(docs.Count);
                            perBook[docBooks[i]] = g;
                        }
                        g.Add(docs[i]);
                    }
                }
                r.Merged.Add(book);
                Log.Write("data updated: " + book + " -> " + docs.Count + " docs"
                    + (whole ? "（整份语料）" : ""));
            }

            // 组装最终要写出的分组。
            // 关键：清单里的 "corpus" 指的是**整份语料**，不是一本叫 corpus 的书。
            // 本地 bookDocs 的键来自每条文档自带的 book 字段（种子/先秦秦汉/…），
            // 而 "corpus" 来自清单键/文件名 —— 两套键根本碰不上。所以照原样
            // bookDocs["corpus"] = docs 的话，那整份语料会被当成「新多出来的一本书」
            // 原样追加到旧的 201 条后面：语料条数直接翻倍（实测 201 -> 403），
            // 每条又都是合法 JSON，落盘自检、条数校验全都过得去，更新还会报成功。
            // 因此这里必须整份替换，而不是新增分组。
            // 同一轮里按书下发的分组放在整份之后叠加：那是对整份里某一部的增量覆盖。
            if (wholeCorpus != null && wholeCorpus.Count > 0)
            {
                bookDocs.Clear();
                bookDocs[WholeCorpusKey] = wholeCorpus;
            }
            foreach (KeyValuePair<string, List<string>> kv in perBook)
                bookDocs[kv.Key] = kv.Value;

            // 平文件直接覆盖（不参与按书合并）。放在语料重写之前，两者互不阻塞。
            foreach (string fileName in r.FilesChanged)
            {
                try
                {
                    string rel = null;
                    if (remoteManifest != null) remoteManifest.Paths.TryGetValue(fileName, out rel);
                    if (string.IsNullOrEmpty(rel)) rel = fileName;
                    byte[] data = Net.Get(client, UpdateSource.DataUrl(rel), 20000);
                    if (data == null || data.Length < 16) { r.Failed.Add(fileName); continue; }
                    // 清单里的哈希是发布方那份文件的（同一个 Release 里取的），必须对上才落盘。
                    string wantHash;
                    if (remoteManifest != null && remoteManifest.Files.TryGetValue(fileName, out wantHash)
                        && !string.IsNullOrEmpty(wantHash))
                    {
                        string gotHash = Net.Sha256Hex(data);
                        if (!string.Equals(gotHash, wantHash, StringComparison.OrdinalIgnoreCase))
                        {
                            r.Failed.Add(fileName);
                            Log.Write("data rejected: " + rel + " 哈希与清单不符（清单 " + wantHash
                                + " / 实际 " + gotHash + "），已跳过");
                            continue;
                        }
                    }
                    // 必须是 JSON 对象/数组开头，避免把网页或二进制写进来
                    string head = Encoding.UTF8.GetString(data, 0, Math.Min(64, data.Length)).TrimStart();
                    if (head.Length == 0 || (head[0] != '{' && head[0] != '[')) { r.Failed.Add(fileName); continue; }

                    string dest = Path.Combine(baseDir, Path.GetFileName(fileName));
                    string tmpFile = dest + ".tmp";
                    File.WriteAllBytes(tmpFile, data);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmpFile, dest);
                    r.FilesUpdated.Add(fileName);
                    Log.Write("file updated: " + fileName + " (" + data.Length + " B)");
                }
                catch (Exception fex)
                {
                    r.Failed.Add(fileName);
                    Log.Error("update file " + fileName, fex);
                }
            }

            // 合并写回 corpus.json（原子替换）。
            // 只有真的合并了史料才重写：不然「仅同义词表有更新」这类情况也会把
            // 内容完全没变的 corpus.json 重写一遍（mtime 变、hash 变、白挨一次 IO）。
            if (r.Merged.Count > 0)
            {
                StringBuilder sb = new StringBuilder(1 << 20);
                // 外层写法必须与发布侧（build.ps1 生成 corpus.json）逐字节一致：
                // 文献名用保留键 corpus、不加任何空白、条目之间只隔一个逗号。
                // 否则合并出来的文件跟远端那份只是"内容相同、字节不同"，哈希永远对不上，
                // Check 每轮都判语料已变化 —— 用户每次启动都要白下 270 KB 再重写重索引一遍，
                // 永远静不下来（实测连跑三次 changed 一直是 1）。
                sb.Append("{\"book\":\"").Append(WholeCorpusKey).Append("\",\"items\":[");
                bool firstDoc = true;
                foreach (KeyValuePair<string, List<string>> kv in bookDocs)
                {
                    foreach (string obj in kv.Value)
                    {
                        if (!firstDoc) sb.Append(',');
                        firstDoc = false;
                        sb.Append(obj);
                    }
                }
                sb.Append("]}");
                try
                {
                    string merged = sb.ToString();
                    // 落盘前自检：新语料必须还能逐条切出完整 JSON 对象。
                    // 这一层是为「下标单位错、切片切歪」那类事故准备的 —— 坏数据一旦落盘，
                    // 用户那边看到的是「更新成功」，实际程序连语料都加载不出来。
                    // 校验不过就整个放弃本次合并，原文件一个字都不动。
                    int totalDocs;
                    int unreadable = CountUnreadableDocs(merged, out totalDocs);
                    if (unreadable > 0 || totalDocs == 0)
                    {
                        Log.Write("merge rejected: " + unreadable + " unreadable of "
                            + totalDocs + " doc(s); 保留原语料");
                        r.Message = "新语料未通过完整性检查，已保留原数据";
                        return r;
                    }

                    string tmp = corpusPath + ".tmp";
                    File.WriteAllText(tmp, merged, new UTF8Encoding(false));
                    if (File.Exists(corpusPath)) File.Delete(corpusPath);
                    File.Move(tmp, corpusPath);
                    Log.Write("corpus merged: " + totalDocs + " docs");
                }
                catch (Exception ex)
                {
                    Log.Error("merge corpus", ex);
                    r.Message = "史料合并失败，继续使用原数据";
                    return r;
                }
            }

            // 清单必须在「合并了史料」和「只换了文件」两种情况下都更新，
            // 否则下次启动会认为同一个文件又变了、反复重下。
            try { WriteLocalVersion(baseDir, r.RemoteVersion); }
            catch (Exception ex) { Log.Error("write local version", ex); }

            StringBuilder msg = new StringBuilder();
            msg.Append("已更新到 v").Append(r.RemoteVersion).Append("（");
            if (r.Merged.Count > 0) msg.Append("合并 ").Append(r.Merged.Count).Append(" 项史料");
            if (r.FilesUpdated.Count > 0)
            {
                if (r.Merged.Count > 0) msg.Append("，");
                msg.Append("替换 ").Append(r.FilesUpdated.Count).Append(" 个数据文件");
            }
            if (r.Merged.Count == 0 && r.FilesUpdated.Count == 0) msg.Append("无变化");
            msg.Append("）");
            if (r.Failed.Count > 0) msg.Append("；").Append(r.Failed.Count).Append(" 项失败");
            r.Message = msg.ToString();
            return r;
        }

        /// <summary>
        /// 一条从原文里切出来的文档是不是完整的 JSON 对象。
        /// 下标单位搞错、下载半截、文件本身坏掉，都会在这里露出来。
        /// 检查刻意做得很浅：正常语料不会被误伤，而坏掉的那些一定过不去。
        /// </summary>
        private static bool LooksLikeDoc(string obj)
        {
            if (string.IsNullOrEmpty(obj)) return false;
            string s = obj.Trim();
            if (s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}') return false;
            // 按字节切片切在多字节字符中间时，UTF-8 解码器会吐出替换字符 U+FFFD。
            // 这种残片首尾可能凑巧是 { 和 }，所以要单独查一次。
            // 用码点写而不是字面量：源码没有 BOM，不依赖编译器猜编码。
            for (int i = 0; i < s.Length; i++) if (s[i] == (char)0xFFFD) return false;
            return true;
        }

        /// <summary>
        /// 把一份语料文本扫一遍，返回其中不是完整 JSON 对象的条数；total 为总条数。
        /// 用于在写盘前拦住坏语料（见 Apply 的落盘自检）。
        /// </summary>
        private static int CountUnreadableDocs(string text, out int total)
        {
            int bad = 0, n = 0;
            JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end)
            {
                n++;
                if (!LooksLikeDoc(JsonScan.ObjOf(text, start, end))) bad++;
            });
            total = n;
            return bad;
        }

        /// <summary>把语料文件按 book 切开（用于合并时替换同一部史书）。invalid 为切不动的条数。</summary>
        private static Dictionary<string, List<string>> SplitByBook(byte[] bytes, out int invalid)
        {
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int bad = 0;
            // 解一次码，扫描器与 ObjOf 共用它 —— 下标只在这个 string 上成立
            string text = JsonScan.Decode(bytes);
            JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end)
            {
                string obj = JsonScan.ObjOf(text, start, end);
                if (!LooksLikeDoc(obj)) { bad++; return; }
                string key = string.IsNullOrEmpty(doc.Book) ? "史料" : doc.Book;
                List<string> list;
                if (!map.TryGetValue(key, out list)) { list = new List<string>(64); map[key] = list; }
                list.Add(obj);
            });
            invalid = bad;
            return map;
        }

        private static List<string> ExtractBookDocs(byte[] bytes, string fallbackBook,
                                                    out List<string> docBooks, out int invalid)
        {
            List<string> list = new List<string>(64);
            // 每条对应的归并键，与 list 一一对应。键取**文档自己的 book 字段**（没有才用
            // 文件名兜底），因为本地那一侧（SplitByBook）就是这么分组的。若这里改用远端
            // 文件名当键，两套键就会错开：清单里 "史记补遗" -> "books/shiji-extra.json"
            // 进来的会落在 "shiji-extra" 组里，而本地那批在 "史记补遗" 组里 —— 下一轮
            // 又变成两组并存，语料里同一部书出现两遍。全程用同一套键才收敛。
            List<string> keys = new List<string>(64);
            int bad = 0;
            string text = JsonScan.Decode(bytes);
            JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end)
            {
                string obj = JsonScan.ObjOf(text, start, end);
                string key = doc.Book;
                if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(fallbackBook))
                {
                    key = fallbackBook;
                    // 补上 book 字段，避免合并后丢失归属
                    obj = obj.TrimEnd();
                    if (obj.EndsWith("}", StringComparison.Ordinal))
                        obj = obj.Substring(0, obj.Length - 1)
                            + ",\"book\":\"" + Json.Escape(fallbackBook) + "\"}";
                }
                // 校验的是即将写进新语料的那个字符串，所以放在补字段之后
                if (!LooksLikeDoc(obj)) { bad++; return; }
                list.Add(obj);
                keys.Add(string.IsNullOrEmpty(key) ? "史料" : key);   // 与 SplitByBook 同一兜底
            });
            docBooks = keys;
            invalid = bad;
            return list;
        }

        /// <summary>写本地 data_version.json（exe 同目录）</summary>
        public static void WriteLocalVersion(string baseDir, string version)
        {
            try
            {
                string corpusPath = Path.Combine(baseDir, Corpus.DataFileName);
                string hash = Net.Sha256File(corpusPath);
                // 平文件也必须记下 hash：否则下次启动又会把它当「已变化」重下一遍
                string synPath = Path.Combine(baseDir, LabelTable.FileName);
                bool hasSyn = File.Exists(synPath);

                StringBuilder sb = new StringBuilder(512);
                sb.Append("{\n  \"version\": \"").Append(Json.Escape(version)).Append("\",\n");
                sb.Append("  \"generated_at\": \"").Append(DateTime.UtcNow.ToString("s")).Append("\",\n");
                sb.Append("  \"source_url\": \"https://github.com/1279255198psy/jigu-app\",\n");
                sb.Append("  \"books\": { \"corpus\": \"").Append(hash).Append("\" },\n");
                if (hasSyn)
                    sb.Append("  \"files\": { \"").Append(LabelTable.FileName).Append("\": \"")
                      .Append(Net.Sha256File(synPath)).Append("\" },\n");
                sb.Append("  \"paths\": { \"corpus\": \"").Append(Corpus.DataFileName).Append("\"");
                if (hasSyn)
                    sb.Append(", \"").Append(LabelTable.FileName).Append("\": \"")
                      .Append(LabelTable.FileName).Append("\"");
                sb.Append(" }\n}\n");
                File.WriteAllText(Path.Combine(baseDir, Corpus.DataVersionFile), sb.ToString(),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("write data_version", ex); }
        }
    }

    /// <summary>程序本体更新：下载新的安装包 -> 由外部替换助手完成热替换 -> 自动重启</summary>
    internal static class AppUpdater
    {
        public sealed class Report
        {
            public bool Checked;
            public bool Available;
            public string LocalVersion = AppVer.Number;
            public string RemoteVersion = "";
            public string Notes = "";
            public string Url = "";
            public string Sha256 = "";
            public long Size;
            public bool Mandatory;
            public string Message = "";
        }

        public const string VersionFileName = "app_version.json";
        public const string PendingDirName = "pending";
        public const string PendingExeName = "稽古-update.exe";
        /// <summary>待应用更新随包存放的元数据（版本 / 哈希 / 说明），用于下次启动时判断是否还值得应用</summary>
        public const string PendingMetaName = "pending.json";

        public static Report Check()
        {
            Report r = new Report();
            string spec = UpdateSource.AppSpec();

            AppManifest m = FromManifestFile(spec) ?? FromRelease(spec);
            if (m == null || string.IsNullOrEmpty(m.Version))
            {
                r.Message = "未连上更新服务器（暂时用当前版本）";
                return r;
            }

            r.Checked = true;
            r.RemoteVersion = m.Version;
            r.Notes = m.Notes;
            r.Url = m.Url;
            r.Sha256 = m.Sha256;
            r.Size = m.Size;
            r.Mandatory = m.Mandatory;
            r.Available = IsNewer(m.Version, AppVer.Number) && !string.IsNullOrEmpty(m.Url);
            r.Message = r.Available
                ? ("发现新版本 v" + m.Version)
                : "已是最新版本 v" + AppVer.Number;
            Log.Write("app check: local=" + AppVer.Number + " remote=" + m.Version
                + " available=" + r.Available + " source=" + spec);
            return r;
        }

        /// <summary>
        /// 首选路径：源上有一个显式的 app_version.json。
        /// 静态基址部署、内网共享目录与 file:// 离线测试走这条；它也是唯一能显式指定
        /// 下载地址 / 更新说明 / mandatory 的地方。取不到就返回 null 交给 Release 回退。
        /// </summary>
        private static AppManifest FromManifestFile(string spec)
        {
            string url = UpdateSource.UrlFor(spec, VersionFileName);
            if (string.IsNullOrEmpty(url)) return null;
            byte[] bytes = Net.Get(Net.Client(), url, 7000);
            if (bytes == null) return null;

            AppManifest m;
            try
            {
                Dictionary<string, object> root = MiniJson.Parse(Encoding.UTF8.GetString(bytes))
                    as Dictionary<string, object>;
                if (root == null) return null;
                m = new AppManifest();
                m.Version = MiniJson.Str(root, "version").Trim();
                m.Url = MiniJson.Str(root, "url").Trim();
                m.Sha256 = MiniJson.Str(root, "sha256").Trim().ToLowerInvariant();
                // 更新说明会整段进「新版本已下载完成」的弹窗，清单里写长了就是一个巨型
                // 对话框。CI 生成时已经限长，这里再兜一道 —— 清单来源是可替换的。
                m.Notes = Cap(MiniJson.Str(root, "notes"), 600);
                m.Mandatory = MiniJson.Bool(root, "mandatory", false);
                object sz;
                if (root.TryGetValue("size", out sz) && sz is double && (double)sz > 0)
                    m.Size = (long)(double)sz;
            }
            catch (Exception ex) { Log.Error("parse app_version", ex); return null; }

            m.Url = ResolveRelative(spec, m.Url);
            return m;
        }

        /// <summary>
        /// 回退路径：源是 github:owner/repo 但该 Release 没挂 app_version.json 时，
        /// 直接由 tag 与附件推导 —— 安装包附件提供下载地址，SHA256SUMS.txt 提供哈希。
        /// 这样「只打一个 tag」就能发版，不依赖任何手工上传的清单。
        /// </summary>
        private static AppManifest FromRelease(string spec)
        {
            string repo = UpdateSource.RepoOf(spec);
            if (repo == null) return null;
            GitHubRelease rel = UpdateSource.Latest(repo);
            if (rel == null || string.IsNullOrEmpty(rel.Version)) return null;

            GitHubAsset pkg = FindInstaller(rel);
            if (pkg == null)
            {
                Log.Write("release " + rel.Tag + " has no installer asset");
                return null;
            }

            AppManifest m = new AppManifest();
            m.Version = rel.Version;
            m.Url = pkg.Url;
            m.Size = pkg.Size;
            m.Sha256 = HashFromSums(rel, pkg.Name);
            m.Notes = FirstLine(rel.Body);
            m.Mandatory = false;
            if (m.Sha256.Length == 0)
                Log.Write("release " + rel.Tag + " 未提供 SHA256SUMS.txt，本次更新将跳过哈希校验");
            return m;
        }

        /// <summary>Release 里的安装包附件（release.yml 保证命名唯一且以 jigu-installer 开头）</summary>
        private static GitHubAsset FindInstaller(GitHubRelease rel)
        {
            foreach (GitHubAsset a in rel.Assets)
            {
                if (!a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!a.Name.StartsWith("jigu-installer", StringComparison.OrdinalIgnoreCase)) continue;
                return a;
            }
            return null;
        }

        /// <summary>从 Release 的 SHA256SUMS.txt 里取该安装包的哈希；取不到返回空串</summary>
        private static string HashFromSums(GitHubRelease rel, string pkgName)
        {
            GitHubAsset sums = rel.Find("SHA256SUMS.txt");
            if (sums == null) return "";
            byte[] bytes = Net.Get(Net.Client(), sums.Url, 8000);
            if (bytes == null) return "";
            try
            {
                string text = Encoding.UTF8.GetString(bytes);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    // sha256sum 的格式：<64 位十六进制><两空格><文件名>；二进制模式文件名前多个 *
                    int sp = line.IndexOf(' ');
                    if (sp <= 0) continue;
                    string hash = line.Substring(0, sp).Trim().ToLowerInvariant();
                    if (hash.Length != 64) continue;
                    string name = line.Substring(sp).Trim().TrimStart('*').Trim().TrimStart('.', '/');
                    if (string.Equals(name, pkgName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(name), pkgName, StringComparison.OrdinalIgnoreCase))
                        return hash;
                }
            }
            catch (Exception ex) { Log.Error("parse SHA256SUMS", ex); }
            return "";
        }

        /// <summary>清单里的 url 写成相对路径时，按当前源解析成绝对地址</summary>
        private static string ResolveRelative(string spec, string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.IndexOf("://", StringComparison.Ordinal) > 0) return url;
            string abs = UpdateSource.UrlFor(spec, url);
            return string.IsNullOrEmpty(abs) ? url : abs;
        }

        /// <summary>Release 正文取第一行有效内容当说明：整篇 Markdown 塞进弹窗没法看</summary>
        private static string FirstLine(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            foreach (string raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim().TrimStart('#', '-', '*', ' ').Trim();
                if (line.Length == 0) continue;
                return Cap(line, 120);
            }
            return "";
        }

        /// <summary>更新说明限长：超长直接截断，避免弹窗被一整篇文档撑爆</summary>
        private static string Cap(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1).TrimEnd() + "…";
        }

        public static string PendingPath(string baseDir)
        {
            return Path.Combine(baseDir, PendingDirName, PendingExeName);
        }

        /// <summary>
        /// 启动时找「上次已经下载好、还没应用」的更新包。
        /// 返回非 null 表示可用。任何一项校验不过（缺文件、哈希不符、版本并不比当前新）
        /// 都会顺手清掉 pending 目录并返回 null —— 陈旧残留不该反复打扰用户。
        /// </summary>
        public static Report FindPending(string baseDir)
        {
            string dir = Path.Combine(baseDir, PendingDirName);
            string pkg = PendingPath(baseDir);
            string meta = Path.Combine(dir, PendingMetaName);
            if (!Directory.Exists(dir)) return null;

            try
            {
                if (!File.Exists(pkg) || !File.Exists(meta)) { DeletePending(baseDir); return null; }

                Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(meta, Encoding.UTF8))
                    as Dictionary<string, object>;
                if (root == null) { DeletePending(baseDir); return null; }

                string version = MiniJson.Str(root, "version").Trim();
                string sha = MiniJson.Str(root, "sha256").Trim().ToLowerInvariant();
                string notes = MiniJson.Str(root, "notes");

                // 版本不比当前新 —— 多半已经通过别的途径更新过了，这个包没用了
                if (string.IsNullOrEmpty(version) || !IsNewer(version, AppVer.Number))
                {
                    Log.Write("pending v" + version + " 不比当前 v" + AppVer.Number + " 新，清理");
                    DeletePending(baseDir);
                    return null;
                }
                long size = new FileInfo(pkg).Length;
                if (size < 1024) { DeletePending(baseDir); return null; }
                if (sha.Length > 0 && !string.Equals(Net.Sha256File(pkg), sha, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write("pending 包哈希不符，清理");
                    DeletePending(baseDir);
                    return null;
                }

                Report r = new Report();
                r.Checked = true;
                r.Available = true;
                r.RemoteVersion = version;
                r.Notes = notes;
                r.Sha256 = sha;
                r.Size = size;
                r.Message = "已下载 v" + version + "，重启后生效";
                Log.Write("pending update found: v" + version + " (" + size + " B)");
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("FindPending", ex);
                DeletePending(baseDir);
                return null;
            }
        }

        /// <summary>把待应用更新的版本信息写进 pending/pending.json（原子替换）</summary>
        public static bool WritePendingMeta(string baseDir, Report r, string pkgPath)
        {
            try
            {
                string meta = Path.Combine(baseDir, PendingDirName, PendingMetaName);
                StringBuilder sb = new StringBuilder(256);
                sb.Append("{\n  \"version\": \"").Append(Json.Escape(r.RemoteVersion)).Append("\",\n");
                // 记的是磁盘上这个包的实际哈希：下次启动据此发现截断/损坏
                sb.Append("  \"sha256\": \"").Append(Net.Sha256File(pkgPath)).Append("\",\n");
                sb.Append("  \"size\": ").Append(new FileInfo(pkgPath).Length).Append(",\n");
                sb.Append("  \"url\": \"").Append(Json.Escape(r.Url)).Append("\",\n");
                sb.Append("  \"notes\": \"").Append(Json.Escape(r.Notes)).Append("\"\n}\n");
                string tmp = meta + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(meta)) File.Delete(meta);
                File.Move(tmp, meta);
                return true;
            }
            catch (Exception ex) { Log.Error("WritePendingMeta", ex); return false; }
        }

        /// <summary>删掉整个 pending 目录（陈旧、损坏或已不适用的残留）</summary>
        public static void DeletePending(string baseDir)
        {
            try
            {
                string dir = Path.Combine(baseDir, PendingDirName);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { Log.Error("DeletePending", ex); }
        }

        /// <summary>语义化版本比较：a 是否比 b 新</summary>
        public static bool IsNewer(string a, string b)
        {
            try
            {
                string[] pa = (a ?? "").Split('.'), pb = (b ?? "").Split('.');
                int n = Math.Max(pa.Length, pb.Length);
                for (int i = 0; i < n; i++)
                {
                    int va = i < pa.Length ? ParseInt(pa[i]) : 0;
                    int vb = i < pb.Length ? ParseInt(pb[i]) : 0;
                    if (va != vb) return va > vb;
                }
            }
            catch { }
            return false;
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>下载安装包到 pending 目录并校验</summary>
        public static string Download(string baseDir, Report r, Action<int> progress)
        {
            if (!r.Available) return null;
            string dir = Path.Combine(baseDir, PendingDirName);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, PendingExeName);
            try
            {
                if (r.Url != null && r.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] local = Net.Get(null, r.Url, 5000);
                    if (local == null) return null;
                    File.WriteAllBytes(target, local);
                }
                else
                {
                WebClient c = Net.Client();
                byte[] data = null;
                // 先试流式下载（可报进度），失败退回一次性下载。
                // 整段都套在 OffUiThread 里：DownloadFileAsync + WaitOne 是同一个坑，见那里的注释。
                data = Net.OffUiThread<byte[]>(delegate()
                {
                    try
                    {
                        c.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                        {
                            if (progress != null) progress(e.ProgressPercentage);
                        };
                        ManualResetEvent done = new ManualResetEvent(false);
                        Exception err = null;
                        c.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
                        { err = e.Error; done.Set(); };
                        c.DownloadFileAsync(new Uri(r.Url), target);
                        if (!done.WaitOne(180000) || err != null)
                        {
                            try { c.CancelAsync(); } catch { }
                            return Net.Get(Net.Client(), r.Url, 180000);
                        }
                        return null;
                    }
                    catch { return Net.Get(Net.Client(), r.Url, 180000); }
                });

                if (data != null) File.WriteAllBytes(target, data);
                }
                if (!File.Exists(target) || new FileInfo(target).Length < 1024) return null;
                if (!string.IsNullOrEmpty(r.Sha256))
                {
                    string got = Net.Sha256File(target);
                    if (!string.Equals(got, r.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("update hash mismatch: want=" + r.Sha256 + " got=" + got);
                        return null;
                    }
                }
                Log.Write("update downloaded: " + target + " (" + new FileInfo(target).Length + " bytes)");
                return target;
            }
            catch (Exception ex) { Log.Error("update download", ex); return null; }
        }

        /// <summary>
        /// 启动替换助手：把当前安装包复制到临时目录，以 --apply-update 方式运行，
        /// 由它等本进程退出后替换文件并重启。
        /// </summary>
        public static bool LaunchReplace(string installBaseDir, string newExePath)
        {
            try
            {
                string helper = Path.Combine(Path.GetTempPath(),
                    "jigu-hotswap-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Application.ExecutablePath, helper, true);
                ProcessStartInfo psi = new ProcessStartInfo(helper);
                psi.Arguments = "--apply-update \"" + installBaseDir.TrimEnd('\\') + "\" \""
                    + newExePath + "\" " + Process.GetCurrentProcess().Id;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                Log.Write("hotswap helper launched: " + helper);
                return true;
            }
            catch (Exception ex) { Log.Error("launch hotswap", ex); return false; }
        }

        /// <summary>
        /// 替换助手主体：等目标进程退出 -> 用安装包内容覆盖 installDir -> 重启主程序。
        /// 安装包是自解的：直接以 --extract-to 方式运行它即可落地全部文件。
        /// </summary>
        public static int RunReplace(string installDir, string newExePath, int targetPid)
        {
            Log.Write("hotswap start: install=" + installDir + " new=" + newExePath + " pid=" + targetPid);
            try
            {
                // 1) 等旧进程退出
                for (int i = 0; i < 120; i++)
                {
                    try
                    {
                        Process p = Process.GetProcessById(targetPid);
                        if (p.HasExited) break;
                    }
                    catch { break; }
                    Thread.Sleep(250);
                }
                Thread.Sleep(600);

                // 2) 让新安装包自解压到安装目录（覆盖旧文件）
                ProcessStartInfo psi = new ProcessStartInfo(newExePath);
                psi.Arguments = "--extract-to \"" + installDir.TrimEnd('\\') + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process ex = Process.Start(psi);
                // WaitForExit 的返回值必须看：超时后子进程还在跑，此时读 ExitCode 会抛
                // InvalidOperationException，异常被本方法末尾的 catch 吞掉，下面第 3 步
                // 「重启主程序」就永远执行不到 —— 用户看到程序关掉之后再也起不来。
                bool exited = ex.WaitForExit(180000);
                int extractCode = 0;
                if (exited)
                {
                    extractCode = ex.ExitCode;
                    Log.Write("hotswap extract exit=" + extractCode);
                }
                else
                {
                    // 没退不等于没写成功：文件可能早就落地了，只是进程卡在收尾。
                    // 记 -1，走下面的「不完整」提示，但绝不跳过重启。
                    extractCode = -1;
                    Log.Write("hotswap extract 超时：180s 内未退出，拿不到退出码；仍继续重启");
                }
                // 0 = 全部写好；1 = 一个都没写；2 = 写了一部分（见 InstallerPayload.ExtractTo）。
                // 后两种都不算更新成功。但这里没有界面，也不该把用户扔在一个空目录前面 ——
                // 旧程序多半还在原地，起来至少能用；只是把话说清楚，别在日志里假报成功。
                if (extractCode != 0)
                    Log.Write("hotswap 未完整完成（exit=" + extractCode
                        + "），仍尝试重启；本次更新不应视为成功");
                CleanupStrays(installDir);

                // 3) 重启主程序（路径全部相对 installDir，不写死盘符）
                string main = Path.Combine(installDir, "稽古.exe");
                if (File.Exists(main))
                {
                    Process.Start(new ProcessStartInfo(main) { WorkingDirectory = installDir });
                    Log.Write("hotswap restarted: " + main);
                }
                else Log.Write("hotswap: main exe not found at " + main);
            }
            catch (Exception ex) { Log.Error("hotswap body", ex); }
            return 0;
        }

        /// <summary>
        /// 清掉替换过程中可能留下的 .tmp / .old。它们本身没用，更要紧的是：
        /// 卸载器判断「这个目录里只有我们的文件」是按精确文件名比的，
        /// 多一个 稽古.exe.old 就会让它认定目录归用户所有，从此删不掉安装目录。
        /// </summary>
        private static void CleanupStrays(string dir)
        {
            try
            {
                string[] patterns = { "*.old", "*.tmp" };
                foreach (string pattern in patterns)
                {
                    foreach (string f in Directory.GetFiles(dir, pattern))
                    {
                        try { File.Delete(f); }
                        catch (Exception ex) { Log.Error("清理残留 " + f, ex); }
                    }
                }
            }
            catch (Exception ex) { Log.Error("cleanup strays", ex); }
        }

        /// <summary>
        /// 更新就绪提示。此时包**已经下载并校验完毕**，这里只是问要不要现在重启 ——
        /// 所以文案是「已下载完成」而不是「是否现在更新」，用户不必再等下载。
        /// mandatory 为真时不给「稍后」，只能确认。
        /// </summary>
        public static bool AskUser(Report r)
        {
            string notes = string.IsNullOrEmpty(r.Notes) ? "" : ("\r\n\r\n本次更新内容：\r\n" + r.Notes);
            string size = r.Size > 0
                ? ("（" + (r.Size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB）")
                : "";
            string tail = r.Mandatory
                ? "\r\n\r\n这是必须安装的更新。点「确定」后程序会自动重启并完成更新。"
                : "\r\n\r\n点「是」立即重启更新；点「否」这次先不更新，下次启动时再问你。";
            string msg = "新版本 v" + r.RemoteVersion + " 已下载完成" + size
                + "（当前 v" + AppVer.Number + "）。" + notes + tail;
            DialogResult dr = MessageBox.Show(msg, "稽古 · 软件更新",
                r.Mandatory ? MessageBoxButtons.OK : MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            return r.Mandatory || dr == DialogResult.Yes;
        }
    }
}
