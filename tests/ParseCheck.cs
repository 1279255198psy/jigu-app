// FILE: jigu-app/tests/ParseCheck.cs
// 解析层健壮性回归，覆盖两个真实发生过的缺陷：
//
//   1. UTF-8 BOM。外部语料 / 标签文件如果是带 BOM 的 UTF-8（记事本另存为就很容易这样），
//      旧解析器会把 BOM 当成非法首字符，于是整份文件被静默当成空 —— corpus.json 带 BOM
//      会解析出 0 篇而**不抛异常**，连"回退到内置种子"这条兜底都不会触发；labels.json
//      带 BOM 则让标签表全空、检索悄悄降级。修法是 SkipWs / Skip 视 U+FEFF 为空白。
//
//   2. 深嵌套。MiniJson 递归下降，一方是恶意/损坏的输入灌进几万层 `[[[[…` 时递归爆栈，
//      栈溢出在 .NET 里**不可捕获**，进程直接被杀（实测 10000 层 → exit 127）。修法是
//      加深度上限，超限返回 null —— 调用方本来就有解析失败的分支，会优雅降级。
//
// 编译（和 OffsetCheck 一样只需要 Json.cs + MiniJson.cs；用反斜杠路径，这个 shell 会
// 把正斜杠路径的目录部分吃掉）：
//   csc -nologo -out:ParseCheck.exe ParseCheck.cs ..\src\Json.cs ..\src\MiniJson.cs
using System;
using System.Collections.Generic;
using System.Text;
using Jigu;

namespace Jigu
{
    /// <summary>
    /// 只为了让本探针能单独编译：JsonScan 依赖 CorpusDoc，而真的那个在 Corpus.cs 里，
    /// 拉进来会连带 Host.cs / WebView2。字段与 src/Corpus.cs 的 CorpusDoc 一致。
    /// </summary>
    internal sealed class CorpusDoc
    {
        public int No;
        public string Book = "";
        public string Chapter = "";
        public string Title = "";
        public string Original = "";
        public string Translation = "";
        public string[] Figures = new string[0];
        public string Decision = "";
        public string Outcome = "";
        public string[] Themes = new string[0];
        public string[] Pros = new string[0];
        public string[] Cons = new string[0];
    }
}

internal static class ParseCheck
{
    private static int _fails;
    private static void Say(string s) { Console.WriteLine(s); }
    private static void Ok(string s) { Console.WriteLine("  OK   " + s); }
    private static void Fail(string s) { _fails++; Console.WriteLine("  FAIL " + s); }

    // 写成转义序列，绝不在源码里放字面的 U+FEFF：那个字节看不见，
    // 编辑器保存时还可能把它当成文件开头的 BOM 吃掉。
    private const string Bom = "\uFEFF";

    /// <summary>语料文本：两条文档，够验证 BOM 不会吃掉第一条。</summary>
    private const string Corpus =
        "[{\"no\":1,\"book\":\"左传\",\"title\":\"一\",\"original\":\"甲\"}," +
        " {\"no\":2,\"book\":\"史记\",\"title\":\"二\",\"original\":\"乙\"}]";

    private static int CountDocs(string text)
    {
        int n = 0;
        JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end) { n++; });
        return n;
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Say("== 解析层健壮性回归 ==");
        Say("");

        // ---------------------------------------------------------- 1. BOM
        Say("--- 1. UTF-8 BOM ---");

        object o = MiniJson.Parse(Bom + "{\"a\":1}");
        Dictionary<string, object> d = o as Dictionary<string, object>;
        if (d != null && d.Count == 1 && Convert.ToDouble(d["a"]) == 1.0)
            Ok("MiniJson.Parse 带 BOM 的对象解析正常");
        else
            Fail("MiniJson.Parse 带 BOM 的对象解析失败（返回 " + (o == null ? "null" : o.GetType().Name) + "）");

        o = MiniJson.Parse(Bom + "[1,2,3]");
        List<object> a = o as List<object>;
        if (a != null && a.Count == 3) Ok("MiniJson.Parse 带 BOM 的数组解析正常");
        else Fail("MiniJson.Parse 带 BOM 的数组解析失败");

        // 语料扫描器走的是 JsonScan.SkipWs：BOM 若不算空白，第一条文档就被吞掉
        int nPlain = CountDocs(Corpus);
        int nBom = CountDocs(Bom + Corpus);
        if (nPlain == 2 && nBom == 2) Ok("JsonScan 带 BOM 的语料仍是 2 篇（不含 BOM " + nPlain + " 篇）");
        else Fail("JsonScan 带 BOM 的语料只解析出 " + nBom + " 篇（不带 BOM 是 " + nPlain + " 篇）");

        // 文件级：真实场景 BOM 是以字节打头的
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF };
        bytes = Concat(bytes, Encoding.UTF8.GetBytes(Corpus));
        int nBytes = CountDocs(JsonScan.Decode(bytes));
        if (nBytes == 2) Ok("JsonScan 对带 BOM 字节流的语料仍是 2 篇");
        else Fail("JsonScan 对带 BOM 字节流只解析出 " + nBytes + " 篇");

        // BOM 后面跟空白的组合，以及 BOM 出现在值前
        if (MiniJson.Parse(Bom + "  \r\n {\"a\":2}") is Dictionary<string, object>)
            Ok("BOM + 空白混合的前导仍能解析");
        else Fail("BOM 后跟空白时解析失败");

        // ---------------------------------------------------------- 2. 深嵌套
        Say("");
        Say("--- 2. 深嵌套（必须优雅返回 null，不能爆栈杀进程）---");

        // 不写死那个界线（写成常量就得跟着 MaxDepth 改，改错了还没人知道）。
        // 断言的是**性质**：浅的照常解析、深的降级成 null、界线本身落在安全的区间里。
        // 这个循环能跑到最后一行，本身就说明没有爆栈。
        foreach (int depth in new int[] { 2, 10, 32 })
        {
            o = MiniJson.Parse(new string('[', depth) + new string(']', depth));
            if (o is List<object>) Ok("深度 " + depth + "：正常解析");
            else Fail("深度 " + depth + "：本该正常解析，却返回 " + (o == null ? "null" : o.GetType().Name));
        }

        int cutoff = -1;
        for (int n = 1; n <= 400; n++)
        {
            if (MiniJson.Parse(new string('[', n) + new string(']', n)) == null) { cutoff = n; break; }
        }
        if (cutoff < 0) Fail("一路涨到 400 层都没触发上限 —— 上限形同虚设，又回到爆栈的老路");
        else if (cutoff < 32) Fail("深度上限只有 " + cutoff + " 层，正常清单也可能被判为畸形");
        else if (cutoff > 256) Fail("深度上限高达 " + cutoff + " 层，还不到安全的余量");
        else Ok("深度上限约 " + cutoff + " 层（正常清单够用，又远低于爆栈的万层量级）");

        foreach (int depth in new int[] { 1000, 10000, 200000 })
        {
            o = MiniJson.Parse(new string('[', depth) + new string(']', depth));
            if (o == null) Ok("深度 " + depth + "：超限返回 null（降级，未崩溃）");
            else Fail("深度 " + depth + "：本该超限返回 null，却返回 " + o.GetType().Name);
        }

        // 深嵌套但截断的输入（真实损坏文件常这样）：同样不许崩
        string truncated = new string('[', 50000);
        o = MiniJson.Parse(truncated);
        if (o == null || o is List<object>) Ok("5 万层未闭合：未崩溃");
        else Fail("5 万层未闭合：返回了意外类型");

        // 深层对象里嵌字符串，确认深度计数对对象同样生效
        StringBuilder sb = new StringBuilder();
        const int objDepth = 8000;
        for (int i = 0; i < objDepth; i++) sb.Append("{\"k\":");
        sb.Append("1");
        for (int i = 0; i < objDepth; i++) sb.Append('}');
        o = MiniJson.Parse(sb.ToString());
        if (o == null || o is Dictionary<string, object>) Ok("8000 层对象嵌套：未崩溃");
        else Fail("8000 层对象嵌套：返回了意外类型");

        // ---------------------------------------------------------- 3. 正常输入没被误伤
        Say("");
        Say("--- 3. 正常输入不受影响 ---");
        o = MiniJson.Parse("{\"版本\":\"0.1.0\",\"书目\":{\"00_种子\":{\"哈希\":\"ab\"}},\"列表\":[1,2]}");
        d = o as Dictionary<string, object>;
        if (d != null && MiniJson.Str(d, "版本") == "0.1.0")
            Ok("常规嵌套对象解析正常（中文键）");
        else
            Fail("常规嵌套对象解析异常");

        Say("");
        Say(_fails == 0 ? "RESULT: PARSE CHECK OK (0 fails)"
                        : "RESULT: PARSE CHECK FAILURES = " + _fails);
        return _fails == 0 ? 0 : 1;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
