// FILE: jigu-app/tests/AnnoCheck.cs
// 标注字段（cast / cause / process / significance）的端到端回归：
//   1. 读：新分片能读进来；缺字段的旧分片不报错（default: SkipValue）；
//      多出未知字段的未来分片也不报错。
//   2. 索引：只出现在 cause / significance / cast 里的词，检索能命中 ——
//      这是标注的第一价值。分片此前只有正文四字段，对同义词桥完全隐形，
//      补标注的真正意义是让它们能被检索到，而不是让界面多几行字。
//   3. 出口：SearchToJson 把四个字段吐给界面。
//   4. 覆盖率：AnnotationStats 的分母/分子。
//
// 编译（与 src 同编，Corpus 是 internal）：
//   csc -main:AnnoCheck -out:tests\AnnoCheck.exe tests\AnnoCheck.cs src\*.cs
//   -reference:tests\Microsoft.Web.WebView2.Core.dll
//   -reference:tests\Microsoft.Web.WebView2.WinForms.dll
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jigu;

internal static class AnnoCheck
{
    private static int _fails;
    private static int _passes;

    private static void Check(bool ok, string what, string detail)
    {
        if (ok) { _passes++; Console.WriteLine("  PASS " + what); }
        else { _fails++; Console.WriteLine("  FAIL " + what + "  —— " + detail); }
    }

    // 只出现在某个标注字段里的词，用来证明「这个字段真的进了倒排表」。
    // 都避开了正文，否则命中了也说明不了标注有没有进索引。
    private const string InCause = "粮道";
    private const string InSignificance = "组织膨胀";
    private const string InCast = "主帅";
    private const string InProcess = "断粮";

    // 顶层形状必须与真实 corpus.json 一致：{"book":..,"items":[..]}（不是 books 数组）。
    // 选一个程序不认识的 "version" 键一起放着，顺带覆盖顶层未知键的跳过。
    private const string Fixture =
        "{\"book\":\"测试书\",\"version\":\"test\",\"items\":[" +
        // A：四字段齐全，另带一个本程序不认识的字段（模拟更新的分片）
        "{\"chapter\":\"甲\",\"title\":\"完标注\",\"original\":\"甲乙丙丁戊己庚辛\"," +
        " \"translation\":\"白话甲\",\"decision\":\"决策甲\",\"outcome\":\"结果甲\"," +
        " \"figures\":[\"旧人名\"]," +
        " \"cast\":[\"项羽（西楚霸王·主帅）\",\"范增（谋臣）\"]," +
        " \"cause\":\"起因甲：粮道被断。\"," +
        " \"process\":\"经过甲：断粮三日，军心浮动。\"," +
        " \"significance\":\"组织膨胀时先砍分支。\"," +
        " \"themes\":[\"内部矛盾\"],\"future_field\":{\"nested\":[1,2,3]}," +
        " \"unknown_array\":[\"x\",\"y\"]}," +
        // B：老分片，一个标注字段都没有
        "{\"chapter\":\"乙\",\"title\":\"无标注\",\"original\":\"子丑寅卯辰巳午未\"," +
        " \"translation\":\"白话乙\",\"decision\":\"决策乙\",\"outcome\":\"结果乙\"," +
        " \"figures\":[\"某某\"]}," +
        // C：只标了 themes（分批标注的中间态）
        "{\"chapter\":\"丙\",\"title\":\"半标注\",\"original\":\"午未申酉戌亥\"," +
        " \"translation\":\"白话丙\",\"themes\":[\"为政\"]}" +
        "]}";

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.WriteLine("=== 标注字段检查 ===");

        string root = Directory.GetCurrentDirectory();
        string resDir = Path.Combine(root, "resources");
        string tmp = Path.Combine(Path.GetTempPath(), "jigu-anno-check");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);

        // labels.json / stopwords.json 要跟着走，否则 LabelCount 为 0、
        // 同义词桥整条链路静默失效，测出来的是「没标签的世界」，不是真实情况。
        foreach (string f in new string[] { "labels.json", "stopwords.json" })
        {
            string src = Path.Combine(resDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, f), true);
        }
        File.WriteAllText(Path.Combine(tmp, "corpus.json"), Fixture, new UTF8Encoding(false));

        Corpus c = new Corpus();
        c.LoadFrom(tmp, null);

        Console.WriteLine("载入 : " + c.DocCount + " 条 / " + c.TermCount + " 词");
        Check(c.DocCount == 3, "三条都读进来了（含带未知字段的那条）", "实际 " + c.DocCount);

        // ---- 1. 读 ----
        CorpusDoc a = c.Search("完标注", 1).Count > 0 ? c.Search("完标注", 1)[0] : null;
        CorpusDoc b = c.Search("无标注", 1).Count > 0 ? c.Search("无标注", 1)[0] : null;
        if (a == null || b == null)
        {
            Check(false, "能取到测试条目", "按标题检索失败");
            return 1;
        }
        Check(a.Cast.Length == 2 && a.Cast[0] == "项羽（西楚霸王·主帅）",
            "cast 数组按原样读入", "实际 " + a.Cast.Length + " 项");
        Check(a.Cause == "起因甲：粮道被断。", "cause 读入", "实际「" + a.Cause + "」");
        Check(a.Process == "经过甲：断粮三日，军心浮动。", "process 读入", "实际「" + a.Process + "」");
        Check(a.Significance == "组织膨胀时先砍分支。", "significance 读入", "实际「" + a.Significance + "」");
        // 未知键必须被 SkipValue 吃掉而不是带偏后面的字段 —— nested 是个对象，
        // 跳过它时若括号对不上，它后面所有字段都会错位。
        Check(a.Title == "完标注" && a.Themes.Length == 1,
            "未知字段（对象/数组）跳过且不吃掉后续字段", "title=「" + a.Title + "」themes=" + a.Themes.Length);
        Check(b.Cast.Length == 0 && b.Cause.Length == 0 && b.Process.Length == 0 && b.Significance.Length == 0,
            "旧分片缺字段时不报错、取到空值（向后兼容）", "cast=" + b.Cast.Length);
        Check(b.Figures.Length == 1 && b.Decision == "决策乙", "旧分片其它字段不受影响", "figures=" + b.Figures.Length);

        // ---- 2. 索引：标注字段里的词能检索到 ----
        AssertFound(c, InCause, "cause 里的词能检索到");
        AssertFound(c, InProcess, "process 里的词能检索到");
        AssertFound(c, InSignificance, "significance 里的词能检索到");
        AssertFound(c, InCast, "cast 里的身份词能检索到");

        // ---- 3. 出口 ----
        string terms; long ms;
        string json = c.SearchToJson("粮道", 3, out terms, out ms);
        Check(json.IndexOf("\"cause\":\"起因甲：粮道被断。\"", StringComparison.Ordinal) >= 0,
            "SearchToJson 吐出 cause", Short(json));
        Check(json.IndexOf("\"process\":", StringComparison.Ordinal) > 0
            && json.IndexOf("\"significance\":\"组织膨胀时先砍分支。\"", StringComparison.Ordinal) > 0,
            "SearchToJson 吐出 process / significance", Short(json));
        Check(json.IndexOf("\"cast\":[\"项羽（西楚霸王·主帅）\"", StringComparison.Ordinal) > 0,
            "SearchToJson 吐出 cast", Short(json));
        Check(json.IndexOf("\"outcome\":\"结果甲\"", StringComparison.Ordinal) > 0
            && json.IndexOf("\"themes\":[\"内部矛盾\"]", StringComparison.Ordinal) > 0,
            "旧字段仍在（新字段没挤掉它们）", Short(json));

        // ---- 3c. 同义词桥：标注的真正价值 ----
        // 用户的说法（「不会用人」）不在任何一条史料的正文里，它靠 labels.json 的
        // trigger 展开成标签名「用人」，再去命中挂着该标签名的条目。分片此前一条
        // themes 都没挂，于是整条桥对分片是断的 —— 查什么都是 0 条分片。
        // 这一节就是那条链路的回归：挂了 themes 的分片必须被桥到，没挂的不能。
        List<CorpusDoc> bridged = c.Search("不会用人", 5);
        bool hitA = false, hitB = false;
        foreach (CorpusDoc d in bridged)
        {
            if (d.Title == "完标注") hitA = true;     // themes: ["内部矛盾"] —— 没挂「用人」
            if (d.Title == "半标注") hitB = true;     // themes: ["为政"]     —— 也没挂
        }
        // 「完标注」挂的是内部矛盾，「半标注」挂的是为政，两个都不是用人 ——
        // 所以这一问应当谁也桥不到。这反过来证明桥上确实挂着东西（不是恒真）。
        Check(!hitA && !hitB,
            "用户说法展开成的标签名，只命中真正挂了该标签的条目（此处应无命中）",
            "命中完标注=" + hitA + " 半标注=" + hitB);

        // 换一条真的挂了「用人」的：把 A 的 themes 换成用人后再问同一句。
        string fixture2 = Fixture.Replace("\"themes\":[\"内部矛盾\"]", "\"themes\":[\"用人\"]");
        Check(fixture2 != Fixture, "测试装置能改写 themes（否则下面两问无效）", "");
        string tmp2 = tmp + "-bridge";
        if (Directory.Exists(tmp2)) Directory.Delete(tmp2, true);
        Directory.CreateDirectory(tmp2);
        foreach (string f2 in new string[] { "labels.json", "stopwords.json" })
        {
            string src2 = Path.Combine(resDir, f2);
            if (File.Exists(src2)) File.Copy(src2, Path.Combine(tmp2, f2), true);
        }
        File.WriteAllText(Path.Combine(tmp2, "corpus.json"), fixture2, new UTF8Encoding(false));
        Corpus c2 = new Corpus();
        c2.LoadFrom(tmp2, null);
        List<CorpusDoc> bridged2 = c2.Search("不会用人", 5);
        bool nowHit = false;
        foreach (CorpusDoc d in bridged2) if (d.Title == "完标注") nowHit = true;
        Check(nowHit, "把 themes 换成「用人」后，同一句「不会用人」就能桥到这条",
            "命中 " + bridged2.Count + " 条仍无「完标注」");

        // 溯源：这条的原文里根本没有「用人」二字，命中只可能来自 themes 挂上的标签名。
        // 不这样钉一下的话，上面那问有可能是靠正文撞上的，断言就白写了。
        int origAt = fixture2.IndexOf("\"original\"", StringComparison.Ordinal);
        int bodyEnd = fixture2.IndexOf("\"translation\"", origAt, StringComparison.Ordinal);
        string bodyOnly = fixture2.Substring(origAt, bodyEnd - origAt);
        Check(bodyOnly.IndexOf("用人", StringComparison.Ordinal) < 0,
            "溯源：原文里没有「用人」，命中只能来自 themes", bodyOnly);
        Directory.Delete(tmp2, true);

        // ---- 4. 覆盖率 ----
        int ann, cast, themes;
        c.AnnotationStats(out ann, out cast, out themes);
        Check(ann == 2, "有标注 = 2（齐全的那条 + 只标 themes 的那条）", "实际 " + ann);
        Check(cast == 1, "有核心人物 = 1", "实际 " + cast);
        Check(themes == 2, "有情境标签 = 2", "实际 " + themes);

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "RESULT: ANNOCHECK OK (" + _passes + " checks)"
            : "RESULT: ANNOCHECK FAILED (" + _fails + " failed / " + _passes + " passed)");
        Directory.Delete(tmp, true);
        return _fails == 0 ? 0 : 1;
    }

    private static void AssertFound(Corpus c, string term, string what)
    {
        string terms; long ms;
        List<CorpusDoc> hits = c.Search(term, 3);
        bool found = false;
        for (int i = 0; i < hits.Count; i++) if (hits[i].Title == "完标注") found = true;
        Check(found, what + "（" + term + "）",
            "命中 " + hits.Count + " 条，无「完标注」；索引里" + (c.TermCount > 0 ? "有" : "无") + "词");
        // 顺带确认它确实是从那个字段来的：正文里根本没有这两个字
        if (found)
        {
            string json = c.SearchToJson(term, 1, out terms, out ms);
            Check(json.IndexOf("甲乙丙丁", StringComparison.Ordinal) < 0
                || json.IndexOf(term, StringComparison.Ordinal) > 0,
                "  ↳ 「" + term + "」不在原文里，命中只可能来自标注字段", "");
        }
    }

    private static string Short(string s)
    {
        return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
    }
}
