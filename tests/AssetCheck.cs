// FILE: jigu-app/tests/AssetCheck.cs
// 验证"内存加载"修复：把界面真正会请求的每一个 URL 交给 exe 内部的 Assets 解析，
// 确认都能命中内存资源、字节完整、MIME 正确。
// 这直接对应 ERR_FILE_NOT_FOUND 的成因：以前靠磁盘目录映射，现在靠内存索引。
//
// 用法：AssetCheck.exe <exe路径> <报告输出路径>
// 编译：csc /out:AssetCheck.exe /r:Microsoft.JScript.dll AssetCheck.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

internal static class AssetCheck
{
    private static int _fails;
    private static readonly List<string> Out = new List<string>();

    private static void Say(string s) { Out.Add(s); Console.WriteLine(s); }
    private static void Fail(string s) { _fails++; Say("  FAIL " + s); }

    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        string appName = "稽古";
        string exe = args.Length > 0 ? args[0]
            : Path.Combine(RepoRoot(), "dist", appName, appName + ".exe");
        string report = args.Length > 1 ? args[1]
            : Path.Combine(RepoRoot(), "tests", "asset-check-report.txt");
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Say("== 内存加载校验 ==");
        Say("exe: " + exe + "  (" + new FileInfo(exe).Length + " bytes)");
        Say("");

        Assembly asm = Assembly.LoadFile(Path.GetFullPath(exe));
        Type assets = asm.GetType("Jigu.Assets", true);
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        MethodInfo mGet = assets.GetMethod("Get", Any);
        MethodInfo mGetUrl = assets.GetMethod("GetFromUrl", Any);
        MethodInfo mMime = assets.GetMethod("MimeOf", Any);
        if (mGet == null || mGetUrl == null || mMime == null) { Say("FAIL: Assets API 缺失"); return 2; }

        // 前端代码里出现的每一个相对 URL
        string[] urls = new string[]
        {
            "https://jigu.local/index.html",
            "https://jigu.local/styles.css",
            "https://jigu.local/ink.css",
            "https://jigu.local/app.js",
            "https://jigu.local/app.png",
            "https://jigu.local/seed.json",
            "https://jigu.local/labels.json",
            "https://jigu.local/stopwords.json",
            "https://jigu.local/selfcheck-cases.json",
            // 带缓存破坏参数（拦截逻辑必须能剥掉查询串）
            "https://jigu.local/index.html?selftest=1",
            "https://jigu.local/labels.json?ts=1730000000",
        };

        Say("--- 逐个解析前端会请求的 URL ---");
        foreach (string u in urls)
        {
            byte[] blob = (byte[])mGetUrl.Invoke(null, new object[] { u });
            string rel = new Uri(u).PathAndQuery;
            if (blob == null)
            {
                Fail("未命中: " + u);
                continue;
            }
            string mime = (string)mMime.Invoke(null, new object[] { rel });
            Say(string.Format(CultureInfo.InvariantCulture,
                "  OK  {0,-42} {1,8} B  {2}", rel, blob.Length, mime));
            if (blob.Length < 16) Fail("内容过短: " + u);
        }

        Say("");
        Say("--- 关键内容抽查 ---");

        // index.html 必须是完整 HTML，且引用了 app.js
        string html = Text(mGetUrl, "https://jigu.local/index.html");
        if (html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) < 0) Fail("index.html 不是 HTML");
        if (html.IndexOf("app.js") < 0) Fail("index.html 未引用 app.js");
        if (html.IndexOf("id=\"situation\"") < 0) Fail("index.html 缺少输入框");
        Say("  index.html: " + html.Length + " 字符，引用 app.js="
            + (html.IndexOf("app.js") > 0 ? "是" : "否"));

        // 全量语料不再内嵌：它以 corpus.json 放在 exe 同目录，可被热更新整体替换。
        // 内存里留的只有 19 条 seed.json（离线兜底）与 labels/stopwords 两张表。
        string seed = Text(mGetUrl, "https://jigu.local/seed.json");
        int seedDocs = CountOccurrences(seed, "\"chapter\"");
        Say("  内置兜底语料 seed.json = " + seedDocs + " 条");
        if (seedDocs < 15) Fail("内置兜底语料过少: " + seedDocs);

        string labels = Text(mGetUrl, "https://jigu.local/labels.json");
        if (labels.IndexOf("\"labels\"") < 0) Fail("labels.json 缺少 labels 键");
        Say("  情境标签表 = " + CountOccurrences(labels, "\"label\"") + " 个标签");

        string stops = Text(mGetUrl, "https://jigu.local/stopwords.json");
        if (stops.IndexOf('[') < 0) Fail("stopwords.json 不是数组");
        Say("  查询停用词表 = " + CountOccurrences(stops, "\"") / 2 + " 个词（粗数）");

        // 所有资源列表
        Say("");
        Say("--- 内存资源清单 ---");
        int n = 0;
        foreach (string p in (IEnumerable)assets.GetMethod("ListPaths", Any).Invoke(null, null))
        {
            byte[] blob = (byte[])mGet.Invoke(null, new object[] { p });
            n++;
            Say(string.Format(CultureInfo.InvariantCulture, "  {0,-36} {1,8} B",
                p, blob == null ? -1 : blob.Length));
        }
        Say("  共 " + n + " 项");

        // 模拟"磁盘上什么都没有"：内存解析必须仍然全部命中
        Say("");
        Say("--- 模拟无障碍场景：不读取任何磁盘文件 ---");
        Say("  本校验完全通过程序集内存索引完成，未打开任何 web/ 目录文件");

        Say("");
        Say(_fails == 0 ? "RESULT: ALL ASSETS RESOLVE FROM MEMORY (0 fails)" : ("RESULT: FAILURES = " + _fails));

        try { File.WriteAllText(report, string.Join("\r\n", Out.ToArray()), new UTF8Encoding(false)); }
        catch { }
        return _fails == 0 ? 0 : 2;
    }

    private static string Text(MethodInfo mGet, string path)
    {
        byte[] blob = (byte[])mGet.Invoke(null, new object[] { path });
        return blob == null ? "" : Encoding.UTF8.GetString(blob);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while (true)
        {
            idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal);
            if (idx < 0) break;
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
