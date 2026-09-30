// FILE: jigu-app/tests/OffsetCheck.cs
// 扫描器下标单位 + 切片正确性检查。
//
// 背景（这是一个真实发生过的 bug）：JsonScan 在**解码后的 string** 上用字符下标走，
// 而 DataUpdater.ObjOf 曾经拿这两个下标去切 **byte[]**。纯 ASCII 下两者相等，
// 中文语料下差约 3 倍 —— 于是按书合并时每条都切歪，拼回去的 corpus.json 成了乱码，
// 而扫描器容错不抛异常，整个过程一声不吭。
//
// 断言方式：把切片解析成 JSON 对象，然后要求它的 title / original 与扫描器解析出的
// CorpusDoc 完全一致。下标一旦错位，切片对不上这两个字段，立刻失败。
//
// 用法：
//   OffsetCheck.exe                 只跑内置夹具（纯 ASCII + 中文）
//   OffsetCheck.exe <corpus.json>   再跑一份真实语料（推荐：整份 corpus.json）
//
// 编译（注意用反斜杠路径：这个 shell 会把正斜杠路径的目录部分吃掉）：
//   csc -nologo -out:OffsetCheck.exe OffsetCheck.cs ..\src\Json.cs ..\src\MiniJson.cs
using System;
using System.Collections.Generic;
using System.IO;
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

internal static class OffsetCheck
{
    private static int _failed;
    private static int _totalDocs;

    private static void Check(string name, byte[] bytes, bool expectConsistent)
    {
        string text = JsonScan.Decode(bytes);
        int n = 0, mismatch = 0, badShape = 0;
        string firstBad = null;

        JsonScan.ForEachDocument(text, delegate(CorpusDoc doc, long start, long end)
        {
            n++;
            string slice = JsonScan.ObjOf(text, start, end);

            string trimmed = (slice ?? "").Trim();
            bool shapeOk = trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}';
            for (int i = 0; shapeOk && i < trimmed.Length; i++)
                if (trimmed[i] == (char)0xFFFD) shapeOk = false;
            if (!shapeOk)
            {
                badShape++;
                if (firstBad == null) firstBad = "切片不是完整 JSON 对象：" + Short(slice);
                return;
            }

            // 关键断言：切片自己解析出来的字段，必须和扫描器给的 doc 一致。
            // 下标错位、错到别的条目上，这里就对不上。
            Dictionary<string, object> obj = MiniJson.Parse(slice) as Dictionary<string, object>;
            if (obj == null)
            {
                mismatch++;
                if (firstBad == null) firstBad = "切片解析不出对象：" + Short(slice);
                return;
            }
            if (!Same(Str(obj, "title"), doc.Title) || !Same(Str(obj, "original"), doc.Original))
            {
                mismatch++;
                if (firstBad == null)
                    firstBad = "切片与扫描结果对不上：切片 title=[" + Str(obj, "title")
                        + "] 扫描 title=[" + doc.Title + "]";
            }
        });

        _totalDocs += n;
        int bad = mismatch + badShape;
        Console.WriteLine("  " + name);
        Console.WriteLine("    文档数        : " + n);
        if (bad > 0)
        {
            Console.WriteLine("    不一致        : " + bad + " / " + n
                + "（形状 " + badShape + "，对不上 " + mismatch + "）");
            Console.WriteLine("    首个失败      : " + firstBad);
        }
        Console.WriteLine("    结果          : " + (bad == 0 ? "OK" : "FAIL"));

        // 空心文件或解析不出任何条目时，也当作失败 —— 否则「0 条 0 错」会假通过
        if (bad > 0 || n == 0) _failed++;
        Console.WriteLine();
    }

    private static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? (v == null ? "" : v.ToString()) : "";
    }

    /// <summary>title/original 都可能是空串（原文↔翻译两种写法），空对空也算一致。</summary>
    private static bool Same(string a, string b)
    {
        return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }

    private static string Short(string s)
    {
        string t = (s ?? "").Replace("\n", " ").Replace("\r", "");
        return t.Length > 72 ? t.Substring(0, 72) + "…" : t;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("== 扫描器下标 / 切片一致性检查 ==");
        Console.WriteLine();

        Check("内置夹具：纯 ASCII（字符下标 == 字节下标）",
            Encoding.UTF8.GetBytes(
                "{\"book\":\"b\",\"items\":[{\"title\":\"aaa\",\"original\":\"bbb\"}," +
                "{\"title\":\"ccc\",\"original\":\"ddd\"}]}"), true);

        Check("内置夹具：中文（真实形态，UTF-8 下一字三字节）",
            Encoding.UTF8.GetBytes(
                "{\"book\":\"种子\",\"items\":[" +
                "{\"book\":\"种子\",\"title\":\"陈平反间，范增去楚\",\"original\":\"项王乃疑范增与汉有私，稍夺其权。\"}," +
                "{\"book\":\"史记\",\"title\":\"沙丘之变\",\"original\":\"始皇崩于沙丘平台，秘不发丧。\"}]}"), true);

        // 顶层直接是数组的形态（--test-update 的 fixture 可能长这样）
        Check("内置夹具：顶层数组 + 转义与多字节混排",
            Encoding.UTF8.GetBytes(
                "[{\"title\":\"A\\\"B\",\"original\":\"行一\\n行二\"}," +
                "{\"title\":\"汉字标题\",\"original\":\"混杂 ascii 与汉字\"}]"), true);

        if (args.Length > 0 && File.Exists(args[0]))
        {
            Check("真实语料：" + Path.GetFileName(args[0]) + "（" + new FileInfo(args[0]).Length + " B）",
                File.ReadAllBytes(args[0]), true);
        }
        else
        {
            Console.WriteLine("  （未提供语料文件，跳过真实语料一轮；建议传整份 corpus.json）");
            Console.WriteLine();
        }

        Console.WriteLine("共扫描 " + _totalDocs + " 条文档，失败 " + _failed + " 项。");
        Console.WriteLine(_failed == 0 ? "RESULT: OFFSET CHECK OK" : "RESULT: OFFSET CHECK FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
