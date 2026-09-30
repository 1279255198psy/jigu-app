// FILE: jigu-app/src/AppMain.cs
// 程序入口：命令行分支（更新替换助手 / 自解压 / 自检 / 更新模拟）+ 启动流程。
// 单独成文件，便于安装包只编译 Installer.cs 而不引入第二个入口点。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Jigu
{    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // ---- 更新替换助手（由 AppUpdater.LaunchReplace 拉起） ----
            if (args != null && args.Length >= 3 && args[0] == "--apply-update")
            {
                Log.Init(Path.Combine(Path.GetTempPath(), "Jigu"));
                int pid = 0;
                if (args.Length >= 4) int.TryParse(args[3], out pid);
                Environment.ExitCode = AppUpdater.RunReplace(args[1], args[2], pid);
                return;
            }

            // ---- 安装包自解压（热更新用它把新文件覆盖到安装目录） ----
            if (args != null && args.Length >= 2 && args[0] == "--extract-to")
            {
                Log.Init(Path.Combine(Path.GetTempPath(), "Jigu"));
                string dest = args[1];
                if (args.Length >= 4 && args[2] == "--sha256")
                {
                    string want = args[3];
                    string mine = Net.Sha256File(Application.ExecutablePath);
                    if (!string.Equals(want, mine, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("extract aborted: hash mismatch");
                        Environment.ExitCode = 3;
                        return;
                    }
                }
                int failed;
                int n = InstallerPayload.ExtractTo(dest, out failed);
                Log.Write("extract-to " + dest + " : " + n + " files, " + failed + " failed");
                // 退出码 2 = 写了一部分但没写全。替换助手必须能区分「全好」与「半截」——
                // 半截的安装目录重启起来是坏的，宁可让它报错也不要假装成功。
                Environment.ExitCode = failed > 0 ? 2 : (n > 0 ? 0 : 1);
                return;
            }

            // ---- 自检：界面资源 / 外置语料 / C# 检索 / 更新清单 ----
            if (args != null && args.Length > 0 && args[0] == "--selfcheck")
            {
                string outPath = args.Length > 1 ? args[1] : "jigu-selfcheck.txt";
                // 退出码必须反映结果，否则调用方（CI / 包装脚本）只能去 grep 报告文本
                Environment.ExitCode = RunSelfCheck(outPath) == 0 ? 0 : 1;
                return;
            }

            // ---- 模拟更新：伪造远端版本，验证"检查->下载->替换->重启"全流程 ----
            if (args != null && args.Length >= 2 && args[0] == "--test-update")
            {
                Log.Init(AppDomain.CurrentDomain.BaseDirectory);
                Environment.ExitCode = RunUpdateSimulation(args[1]);
                return;
            }

            // ---- 诊断入口：把网页界面落盘并用系统默认浏览器打开（WebView2 完全不可用时的最后兜底）----
            if (args != null && args.Length > 0 && args[0] == "--web-fallback")
            {
                string rootW = AppDomain.CurrentDomain.BaseDirectory;
                Log.Init(rootW);
                ErrorLog.Bind(rootW);
                string page = ClassicUI.Prepare(Path.Combine(rootW, "data"));
                bool ok = ClassicUI.OpenInBrowser(page);
                Environment.ExitCode = (page != null && ok) ? 0 : 1;
                Log.Write("web fallback page=" + (page == null ? "(null)" : page) + " opened=" + ok);
                return;
            }

            // ---- 诊断入口：直接打开内置原生界面（不碰 WebView2）----
            if (args != null && args.Length > 0 && args[0] == "--built-in")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string rootB = AppDomain.CurrentDomain.BaseDirectory;
                Log.Init(rootB);
                ErrorLog.Bind(rootB);
                Application.Run(new ClassicForm(Path.Combine(rootB, "data"), Net.BaseUrl(),
                    "\u5185\u7f6e\u754c\u9762\uff08\u8bca\u65ad\u5165\u53e3\uff09",
                    args.Length > 1 ? args[1] : null));
                return;
            }

            if (!Sys.IsSupported())
            {
                try
                {
                    MessageBox.Show("稽古需要 Windows 7 或更高版本。当前系统：" + Sys.Describe(),
                        "稽古", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 工作目录回退：用户数据目录 -> 程序同目录（相对基目录，不写死盘符）-> 临时目录
            string root = null;
            string[] candidates =
            {
                Environment.GetEnvironmentVariable("JIGU_HOME"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jigu"),
                Path.Combine(baseDir, "jigu-data"),
                Path.Combine(Path.GetTempPath(), "Jigu")
            };
            foreach (string cand in candidates)
            {
                if (string.IsNullOrEmpty(cand)) continue;
                try
                {
                    string probe = Path.Combine(cand, "web");
                    if (!Directory.Exists(probe)) Directory.CreateDirectory(probe);
                    string f = Path.Combine(cand, ".writable");
                    File.WriteAllText(f, "1", Encoding.UTF8);
                    File.Delete(f);
                    root = cand;
                    break;
                }
                catch { }
            }
            if (root == null) root = Path.Combine(Path.GetTempPath(), "Jigu");

            string dataDir = Path.Combine(root, "data");

            // 日志必须先初始化：PickUserDataDir 内部会写一行诊断，
            // 而 Log.Write 在 _path 还没确定时会自己回退到「程序目录」，
            // 于是那一行会单独落在 exe 旁边，剩下全部落在真正的日志文件里，
            // 变成两个同名日志文件（窗口标题指的那个里反而没有这一行）。
            Log.Init(root);
            ErrorLog.Bind(baseDir);

            // WebView2 的用户数据目录必须由 WebView2 自己创建，且必须是绝对路径。
            // 关键：绝不能让这个目录落在可能含中文 / 空格 / 超长字符的路径下——
            // 那是 CreateAsync/EnsureCoreWebView2Async 抛 ArgumentException（值不在预期的范围内）
            // 最常见的来源。这里固定使用 %LOCALAPPDATA%\Jigu（纯 ASCII），逐个探测可用目录，
            // 任何一个不能建都能继续下一候选，绝不因为目录问题把整个程序卡死。
            string userDataDir = PickUserDataDir(root);

            Log.Write("start v" + AppVer.Number + " baseDir=" + baseDir
                + " root=" + root + " userData=" + userDataDir + " os=" + Sys.Describe());

            string how;
            if (!WebView2Check.IsInstalled(out how))
            {
                // 没有显示组件也不再把人挡在门外：先问一句要不要装，装不上就直接用内置原生界面。
                WebView2Check.PromptAndInstall();
                string recheck;
                if (!WebView2Check.IsInstalled(out recheck))
                {
                    Log.Write("webview2 missing -> built-in native UI");
                    RunBuiltIn(dataDir, userDataDir, "未检测到 Microsoft Edge WebView2 显示组件");
                    return;
                }
                how = recheck;
            }
            Log.Write("webview2=" + how);

            if (Assets.WebCount == 0)
            {
                // 界面资源都没有时，内置原生界面仍可用（它只依赖检索内核，不依赖网页资源）
                Log.Write("assets empty -> built-in native UI");
                RunBuiltIn(dataDir, userDataDir, "程序内部界面文件缺失，压缩包可能不完整");
                return;
            }

            try
            {
                Application.Run(new MainForm(dataDir, userDataDir, Net.BaseUrl()));
            }
            catch (Exception ex)
            {
                // MainForm 内部已经把 WebView2 的异常兜到内置界面了；走到这里说明连窗口都没建成。
                Log.Error("Application.Run", ex);
                try { MessageBox.Show("稽古遇到意外错误，将改用内置界面显示：\r\n\r\n"
                    + ex.GetType().Name + ": " + ex.Message, "稽古",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                try { Application.Run(new ClassicForm(dataDir, Net.BaseUrl())); }
                catch (Exception ex2)
                {
                    Log.Error("ClassicForm", ex2);
                    try
                    {
                        string page = ClassicUI.Prepare(dataDir);
                        if (page == null || !ClassicUI.OpenInBrowser(page))
                            MessageBox.Show("稽古无法显示界面。\r\n\r\n"
                                + ex.GetType().Name + ": " + ex.Message, "稽古",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch { }
                }
            }
        }

        /// <summary>内置原生界面（WebView2 不可用时的兜底，永远打得开）</summary>
        private static void RunBuiltIn(string dataDir, string userDataDir, string reason)
        {
            try
            {
                Application.Run(new ClassicForm(dataDir, Net.BaseUrl(), reason));
            }
            catch (Exception ex)
            {
                Log.Error("RunBuiltIn", ex);
                try { MessageBox.Show("稽古无法显示界面：" + ex.Message, "稽古"); } catch { }
            }
        }

        /// <summary>
        /// 挑选 WebView2 用户数据目录：必须是 WebView2 能自行创建的绝对路径，
        /// 优先 %LOCALAPPDATA%\Jigu（纯 ASCII、无空格）。含中文/空格的路径会让
        /// CreateAsync / EnsureCoreWebView2Async 抛 ArgumentException。
        /// </summary>
        private static string PickUserDataDir(string root)
        {
            List<string> cands = new List<string>();
            string localApp;
            try { localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { localApp = null; }
            if (!string.IsNullOrEmpty(localApp))
            {
                cands.Add(Path.Combine(localApp, "Jigu", "WebView2"));
                cands.Add(Path.Combine(localApp, "Jigu"));
            }
            if (!string.IsNullOrEmpty(root)) cands.Add(Path.Combine(root, "WebView2"));
            try { cands.Add(Path.Combine(Path.GetTempPath(), "JiguWebView2")); } catch { }

            string firstUsable = null;
            foreach (string c in cands)
            {
                try
                {
                    if (string.IsNullOrEmpty(c) || !Path.IsPathRooted(c)) continue;
                    // 路径必须是纯 ASCII、长度可控，否则 WebView2 会拒绝
                    bool ascii = true;
                    foreach (char ch in c) { if (ch > 126 || ch < 32) { ascii = false; break; } }
                    if (!ascii || c.Length > 150) continue;
                    if (firstUsable == null) firstUsable = c;
                }
                catch { }
            }
            if (firstUsable == null) firstUsable = Path.Combine(Path.GetTempPath(), "JiguWebView2");
            Log.Write("userDataDir chosen: " + firstUsable);
            return firstUsable;
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log.Error("AppDomain unhandled", ex);
            try
            {
                MessageBox.Show("稽古遇到意外错误，已停止运行。\r\n\r\n"
                    + (ex == null ? "未知错误" : ex.GetType().Name + ": " + ex.Message),
                    "稽古", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        /// <summary>
        /// 自检：1) 内存界面资源 2) 外置语料加载与索引 3) C# 检索 Top3 回归 4) 更新清单
        /// 返回失败项数（0 = 全通过），由调用方写入 Environment.ExitCode。
        /// </summary>
        private static int RunSelfCheck(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;

            try
            {
                sb.AppendLine("== 稽古 自检 ==");
                sb.AppendLine("版本     : v" + AppVer.Number);
                sb.AppendLine("系统     : " + Sys.Describe());
                sb.AppendLine("程序目录 : " + AppDomain.CurrentDomain.BaseDirectory);
                string how;
                sb.AppendLine("WebView2 : " + (WebView2Check.IsInstalled(out how) ? "已安装 " + how : "缺失"));
                sb.AppendLine();

                sb.AppendLine("--- 1. 界面资源（内存） ---");
                sb.AppendLine("web 资源 : " + Assets.WebCount + " 项");
                string[] urls =
                {
                    "https://jigu.local/index.html", "https://jigu.local/styles.css",
                    "https://jigu.local/ink.css", "https://jigu.local/app.js",
                    "https://jigu.local/app.png", "https://jigu.local/seed.json",
                    "https://jigu.local/index.html?selftest=1"
                };
                foreach (string u in urls)
                {
                    byte[] blob = Assets.GetFromUrl(u);
                    if (blob == null) { fails++; sb.AppendLine("  FAIL 未命中 " + u); }
                    else sb.AppendLine("  OK   " + u.PadRight(46) + blob.Length + " B");
                }

                sb.AppendLine();
                sb.AppendLine("--- 2. 外置语料（corpus.json） ---");
                string corpusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Corpus.DataFileName);
                sb.AppendLine("语料文件 : " + corpusPath + (File.Exists(corpusPath) ? "（存在）" : "（缺失->内置种子）"));
                Corpus corpus = new Corpus();
                corpus.LoadFrom(AppDomain.CurrentDomain.BaseDirectory);
                sb.AppendLine("文档数   : " + corpus.DocCount);
                sb.AppendLine("索引词数 : " + corpus.TermCount);
                sb.AppendLine("索引占用 : " + (corpus.IndexBytes / 1024) + " KB");
                if (corpus.DocCount < 50) { fails++; sb.AppendLine("  FAIL 语料过少"); }
                // 词表规模断言改成区间：原来的 "< 500" 在 24,356 词下几乎不可能触发，
                // 等于没写。区间能把 EmitBlock / 词表过滤这类结构性改动抓出来。
                // 当前实测 24,356；补全滑窗后预计升到约 44,000，故上界留到 60,000。
                if (corpus.TermCount < 10000 || corpus.TermCount > 60000)
                {
                    fails++;
                    sb.AppendLine("  FAIL 索引词数 " + corpus.TermCount + " 超出预期区间 [10000, 60000]");
                }
                // 标签表缺失是静默的：没有它检索照样跑，只是召回质量掉回「用户得
                // 碰巧说中 themes 原词」的水平。所以必须显式断言，不能让它悄悄消失。
                if (corpus.LabelCount == 0)
                {
                    fails++;
                    sb.AppendLine("  FAIL 情境标签表为空（程序目录无 labels.json，内嵌资源也缺失）");
                }
                else
                {
                    int trigTotal, trigInCorpus;
                    corpus.LabelStats(out trigTotal, out trigInCorpus);
                    sb.AppendLine("情境标签 : " + corpus.LabelCount + " 个 / " + trigTotal
                        + " 条触发说法，其中 " + trigInCorpus + " 条在语料里存在");
                    sb.AppendLine("           （触发说法不在语料里是正常的 —— 它就是「用户会怎么说」，"
                        + "如「留不住人」；命中后并入的是标签名。逐条校对见 tests\\DataCheck.cs）");
                }

                if (corpus.StopWordCount == 0)
                {
                    fails++;
                    sb.AppendLine("  FAIL 停用词表为空（程序目录无 stopwords.json，内嵌资源也缺失）");
                }
                else sb.AppendLine("停用词表 : " + corpus.StopWordCount + " 个（查询侧的现代虚词，不参与打分）");


                sb.AppendLine();
                sb.AppendLine("--- 3. C# 检索引擎 Top3（召回回归） ---");
                fails += RunSearchCases(corpus, sb);

                sb.AppendLine();
                sb.AppendLine("--- 4. 更新清单 ---");
                string dv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Corpus.DataVersionFile);
                sb.AppendLine("data_version.json : " + (File.Exists(dv) ? "存在" : "缺失"));
                string av = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppUpdater.VersionFileName);
                sb.AppendLine("app_version.json  : " + (File.Exists(av) ? "存在" : "缺失（将静默检查远端）"));
                sb.AppendLine("更新地址          : " + Net.BaseUrl());

                sb.AppendLine();
                sb.AppendLine(fails == 0 ? "RESULT: SELFCHECK OK (0 fails)" : ("RESULT: FAILURES = " + fails));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                fails++;
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
            try { Console.WriteLine(sb.ToString()); } catch { }
            return fails;
        }

        /// <summary>
        /// 检索召回回归：用例来自内嵌的 selfcheck-cases.json（源文件 tests\cases.json）。
        /// 判定沿用旧引擎脚本的宽松约定 —— want 里任意一项作为子串出现在 Top3 的 title 中
        /// 即算通过，这样调权重时不必反复改期望值。
        /// 另外覆盖两类零维护断言：产品承诺（永远给三条）与边界输入（不抛异常）。
        /// </summary>
        private static int RunSearchCases(Corpus corpus, StringBuilder sb)
        {
            int fails = 0;
            byte[] raw = Assets.Get("selfcheck-cases.json");
            if (raw == null)
            {
                sb.AppendLine("  FAIL 用例资源缺失：selfcheck-cases.json 没有内嵌进 exe");
                return 1;
            }

            IList cases = null;
            try
            {
                IDictionary root = MiniJson.Parse(Encoding.UTF8.GetString(raw)) as IDictionary;
                if (root != null) cases = root["cases"] as IList;
            }
            catch (Exception ex)
            {
                sb.AppendLine("  FAIL 用例解析失败: " + ex.Message);
                return 1;
            }
            if (cases == null || cases.Count == 0)
            {
                sb.AppendLine("  FAIL 用例为空");
                return 1;
            }

            int passed = 0;
            int reviewing = 0;
            int snapshots = 0;
            foreach (object item in cases)
            {
                IDictionary c = item as IDictionary;
                if (c == null) { fails++; continue; }
                string id = Convert.ToString(c["id"]);
                string query = Convert.ToString(c["query"]);
                string source = Convert.ToString(c["source"]);
                IList want = c["want"] as IList;

                string termsJson;
                long ms;
                corpus.SearchToJson(query, 3, out termsJson, out ms);
                List<Corpus.SearchHit> hits = corpus.SearchScored(query, 3);

                // 超过三条是 bug（产品只给三条）；少于三条只是警告 —— 已实测确认
                // 存在这样的查询：例如「骨干员工要离职，留不住人」的全部 2 字词元
                // 在全库只覆盖 2 条文档，库里确实没有第三条相关史料。
                // 硬凑第三条 = 塞一条与问题无关的史料，比诚实返回 2 条更糟。
                if (hits.Count > 3)
                {
                    fails++;
                    sb.AppendLine("  FAIL " + id + " 返回 " + hits.Count + " 条（超过 3 条）");
                }
                else if (hits.Count < 3)
                {
                    sb.AppendLine("  WARN " + id + " 只返回 " + hits.Count
                        + " 条：全库没有更多相关史料（不要把无关条目凑数）");
                }

                bool ok = false;
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0) names.Append(" / ");
                    names.Append(hits[i].Doc.Title);
                    if (want == null) continue;
                    for (int w = 0; w < want.Count; w++)
                    {
                        string expect = want[w] as string;
                        if (!string.IsNullOrEmpty(expect) && hits[i].Doc.Title != null
                            && hits[i].Doc.Title.IndexOf(expect, StringComparison.Ordinal) >= 0) ok = true;
                    }
                }

                // confidence=low 的用例：期望值本身没核对过，只报告结果、不计入失败。
                // 拿错误的期望值当护栏，只会训练人忽略红色。
                string conf = Convert.ToString(c["confidence"]);
                bool lowConf = "low".Equals(conf, StringComparison.OrdinalIgnoreCase);
                bool snapshot = "snapshot".Equals(conf, StringComparison.OrdinalIgnoreCase);
                if (snapshot) snapshots++;
                if (lowConf)
                {
                    reviewing++;
                    sb.AppendLine("  " + (ok ? "PASS*" : "REVIEW") + " " + id.PadRight(3) + " " + ms + "ms  "
                        + (source == null ? "" : source) + "  " + names);
                    if (ok)
                        sb.AppendLine("       * 命中了 want 之一，但这条 want 本身没核对过 ——"
                            + "确认它确实是正解后，把 confidence 改成 high。");
                    else
                        sb.AppendLine("       期望值待人工复核（不计入失败）：请判断是引擎该改，还是上列的"
                            + "三条其实就是正解、该改 want。");
                }
                else if (ok)
                {
                    passed++;
                    sb.AppendLine("  PASS" + (snapshot ? "~" : " ") + " " + id.PadRight(3) + " " + ms + "ms  "
                        + (source == null ? "" : source) + "  " + names);
                }
                else
                {
                    fails++;
                    sb.AppendLine("  FAIL " + id.PadRight(3) + " " + ms + "ms  "
                        + (source == null ? "" : source) + "  " + names);
                    sb.AppendLine("       查询 : " + query);
                    // 失败时列出命中词元，便于区分是分词问题还是语料缺口
                    for (int i = 0; i < hits.Count; i++)
                    {
                        string[] t = hits[i].Terms.ToArray();
                        sb.AppendLine("       第 " + (i + 1) + " 条命中词 : "
                            + (t.Length == 0 ? "(无)" : string.Join("、", t)));
                    }
                }
            }
            sb.AppendLine("  召回用例 : " + passed + "/" + (cases.Count - reviewing) + " 通过"
                + (reviewing > 0 ? "（另有 " + reviewing + " 条期望值待人工复核，不计入）" : ""));
            if (snapshots > 0)
                sb.AppendLine("  其中 " + snapshots + " 条标 PASS~ 为快照比对（期望值取自 0.1.0 引擎输出，"
                    + "作用是变更探测器，不宣称是唯一正确答案）");

            // 边界输入：不得抛异常，也不得返回超过 3 条
            string[] edge = { "", "   ", "　　", "abc DEF 123", "曹", new string('控', 300) };
            string[] edgeName = { "空串", "纯空白", "全角空格", "纯英文数字", "单个汉字", "300字重复" };
            for (int i = 0; i < edge.Length; i++)
            {
                try
                {
                    List<Corpus.SearchHit> h = corpus.SearchScored(edge[i], 3);
                    if (h.Count > 3)
                    {
                        fails++;
                        sb.AppendLine("  FAIL 边界[" + edgeName[i] + "] 返回 " + h.Count + " 条");
                    }
                }
                catch (Exception ex)
                {
                    fails++;
                    sb.AppendLine("  FAIL 边界[" + edgeName[i] + "] 抛异常 " + ex.GetType().Name);
                }
            }
            sb.AppendLine("  边界输入 : " + edge.Length + " 项已测（不抛异常、不超 3 条）");
            return fails;
        }

        /// <summary>
        /// 数一份语料里有多少条是重复的（按 title+original 判同一篇），total 为总条数。
        /// 合并把键搞错时，唯一症状就是整份语料被原样追加一遍：条数变多、每条都是合法
        /// JSON、哈希校验也全过。只看"条数有没有变少"是抓不住它的，所以单独数一次重复。
        /// </summary>
        private static int CountDuplicateDocs(string corpusPath, out int total)
        {
            int dup = 0, n = 0;
            if (!File.Exists(corpusPath)) { total = 0; return 0; }
            string text = JsonScan.Decode(File.ReadAllBytes(corpusPath));
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end)
            {
                n++;
                string key = (doc.Title ?? "") + "\u0000" + (doc.Original ?? "");
                if (!seen.Add(key)) dup++;
            });
            total = n;
            return dup;
        }

        /// <summary>
        /// 更新模拟：把远端清单指向本地静态服务器，走完"检查->下载->校验->替换"。
        /// 用于在沙箱里验证热更新链路（不启动真实 GUI）。
        /// </summary>
        private static int RunUpdateSimulation(string baseUrl)
        {
            StringBuilder sb = new StringBuilder();
            int fails = 0;
            try
            {
                sb.AppendLine("== 热更新模拟 ==");
                sb.AppendLine("远端地址 : " + baseUrl);
                // 把模拟地址接到真实的更新链路上（Net.BaseUrl 优先读这个环境变量），
                // 否则 Check() 会去读 update_base.txt / 默认 GitHub，模拟形同虚设。
                try { Environment.SetEnvironmentVariable("JIGU_UPDATE_BASE", baseUrl); }
                catch (Exception eex) { sb.AppendLine("  WARN 无法设置更新地址：" + eex.Message); }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                sb.AppendLine("安装目录 : " + baseDir);

                // 1) 数据更新
                DataUpdater.Report d = DataUpdater.Check(baseDir);
                sb.AppendLine("数据检查 : checked=" + d.Checked + " local=" + d.LocalVersion
                    + " remote=" + d.RemoteVersion + " changed=" + d.Changed.Count
                    + " filesChanged=" + d.FilesChanged.Count);
                if (!d.Checked) { fails++; sb.AppendLine("  FAIL 未取到 data_version.json"); }
                // 同 Host.RunDataCheck：只看 Changed 会漏掉「只有平文件变化」的更新
                if (d.Changed.Count > 0 || d.FilesChanged.Count > 0)
                {
                    // 更新前先数一遍。合并的语义是「用远端那一部替换本地的同名书」，
                    // 它不该让整个语料变少 —— 变少就说明合并把别的书弄丢了。
                    Corpus before = new Corpus();
                    before.LoadFrom(baseDir);

                    d = DataUpdater.Apply(baseDir, d);
                    sb.AppendLine("数据应用 : merged=" + d.Merged.Count + " filesUpdated="
                        + d.FilesUpdated.Count + " failed=" + d.Failed.Count + " msg=" + d.Message);
                    foreach (string fu in d.FilesUpdated)
                        sb.AppendLine("  OK   已替换 " + fu + " -> "
                            + Path.Combine(baseDir, fu));
                    foreach (string ff in d.Failed)
                        sb.AppendLine("  FAIL 更新失败 " + ff);
                    Corpus c = new Corpus();
                    c.LoadFrom(baseDir);
                    sb.AppendLine("重载语料 : " + c.DocCount + " 条 / 同义词表 "
                        + c.LabelCount + " 个标签" + "（更新前 " + before.DocCount + " 条）");
                    if (c.DocCount == 0)
                    {
                        fails++;
                        sb.AppendLine("  FAIL 更新后一条语料都读不出来");
                    }
                    else if (c.DocCount < before.DocCount)
                    {
                        fails++;
                        sb.AppendLine("  FAIL 更新后语料少了 "
                            + (before.DocCount - c.DocCount) + " 条（"
                            + before.DocCount + " -> " + c.DocCount
                            + "）：按书合并丢了内容");
                    }
                    // 反过来也要查：合并按错键时语料会整份翻倍，条数只增不减，
                    // 上面那条判断正好放它过去。
                    int dupTotal, dups = CountDuplicateDocs(
                        Path.Combine(baseDir, Corpus.DataFileName), out dupTotal);
                    if (dups > 0)
                    {
                        fails++;
                        sb.AppendLine("  FAIL 更新后语料有 " + dups + " 条重复（共 "
                            + dupTotal + " 条）：合并按错了键，把整份又追加了一遍");
                    }
                }

                // 2) 程序本体更新
                AppUpdater.Report a = AppUpdater.Check();
                sb.AppendLine("程序检查 : checked=" + a.Checked + " local=" + a.LocalVersion
                    + " remote=" + a.RemoteVersion + " available=" + a.Available);
                if (!a.Checked) { fails++; sb.AppendLine("  FAIL 未取到 app_version.json"); }
                if (a.Available)
                {
                    string pkg = AppUpdater.Download(baseDir, a, null);
                    sb.AppendLine("下载结果 : " + (pkg == null ? "失败" : pkg));
                    if (pkg == null) { fails++; }
                    else
                    {
                        // 3) 校验替换能力：把包解开到一个临时"安装目录"，模拟替换
                        string testDir = Path.Combine(Path.GetTempPath(),
                            "jigu-hotswap-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        System.Diagnostics.ProcessStartInfo psi =
                            new System.Diagnostics.ProcessStartInfo(pkg);
                        psi.Arguments = "--extract-to \"" + testDir + "\"";
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        System.Diagnostics.Process ex = System.Diagnostics.Process.Start(psi);
                        ex.WaitForExit(120000);
                        int n = 0;
                        if (Directory.Exists(testDir))
                            n = Directory.GetFiles(testDir, "*", SearchOption.AllDirectories).Length;
                        // 主程序名含中文，按扩展名与体积判断更稳妥
                        string[] exes = Directory.Exists(testDir)
                            ? Directory.GetFiles(testDir, "*.exe", SearchOption.TopDirectoryOnly) : new string[0];
                        sb.AppendLine("替换演练 : 释放 " + n + " 个文件到 " + testDir);
                        sb.AppendLine("主程序   : " + (exes.Length > 0
                            ? (Path.GetFileName(exes[0]) + " (" + new FileInfo(exes[0]).Length + " B)") : "缺失"));
                        if (exes.Length == 0) fails++;
                        try { Directory.Delete(testDir, true); } catch { }
                    }
                }
                else
                {
                    sb.AppendLine("说明     : 远端版本未高于本地（" + a.RemoteVersion + " <= "
                        + a.LocalVersion + "），跳过下载");
                }

                sb.AppendLine();
                sb.AppendLine(fails == 0 ? "RESULT: UPDATE SIMULATION OK (0 fails)"
                    : ("RESULT: UPDATE SIMULATION FAILURES = " + fails));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                fails++;
            }
            try { Console.WriteLine(sb.ToString()); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "jigu-update-sim.txt"),
                    sb.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-sim-report.txt"),
                    sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return fails == 0 ? 0 : 2;
        }
    }
}
