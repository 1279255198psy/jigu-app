// FILE: jigu-app/tests/TierCheck.cs
// 上/中/下分级的端到端回归：
//   1. 惰性：语料里一条分级都没有时，SearchByVerdict 必须逐条等于 SearchScored。
//      这是「标注跑起来之前本功能完全隐形」的保证 —— 它不是靠 if 判断糊出来的，
//      是本轮唯一必须可证的前提。
//   2. 反证：换一份**有**分级的夹具，graded 必须变 true 且结果与旧 Top3 不同。
//      没有这一条，上面那句「惰性」可能只是因为守卫恒真（比如 _hasVerdicts 永远
//      是 false），断言就成了摆设。
//   3. 每档取该档最高分那条；输出顺序固定 上→中→下（不是按分数排）。
//   4. 某档没有够格的候选就**少一条**，不拿无关条目顶替；tiers 里记 hit:-1。
//   5. 相关性下限 VerdictFloorRatio 真的在挡人（低分条目进不了档）。
//   6. Expand 与 ExpandTraced 的标签集合全等；why 说出「哪句原话被理解成了哪个标签」；
//      用户直接打标签名时不算「桥」，不出现 said == label 的对。
//   7. 出口：SearchToJson 吐出 verdict/verdictWhy/why/graded/tiers/verdictDocs；
//      旧分片缺字段不报错；非法取值（「优」）归空降级，「上策」按容错读成「上」。
//
// 编译（与 src 同编，Corpus / LabelTable 都是 internal）：
//   csc -main:TierCheck -out:tests\TierCheck.exe tests\TierCheck.cs src\*.cs
//   -reference:System.IO.Compression.dll
//   -reference:tests\Microsoft.Web.WebView2.Core.dll
//   -reference:tests\Microsoft.Web.WebView2.WinForms.dll
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Jigu;

internal static class TierCheck
{
    private static int _fails;
    private static int _passes;

    private static void Check(bool ok, string what, string detail)
    {
        if (ok) { _passes++; Console.WriteLine("  PASS " + what); }
        else { _fails++; Console.WriteLine("  FAIL " + what + "  —— " + detail); }
    }

    // ---- 夹具用的自造词元 ----
    // 全部是语料里不存在的生造词，唯一目的是让分数的相对高低可预料：
    // 每个词只出现在一条史料里（df=1，idf 拉满），而「通用」每一条都有（df=N，idf 最低）。
    // 不要改成真词：真词一旦被 labels.json 的 trigger 撞上，同义词桥会额外加词，
    // 下面那些算好的分数就全乱了。
    private const string Common = "通用";
    private const string TAlpha = "阿尔法";     // 只在「上策甲」
    private const string TGamma = "伽马射线";   // 只在「上策甲」
    private const string TDelta = "德尔塔";     // 只在「上策乙」
    private const string TOmega = "奥米加";     // 只在「中策甲」
    private const string TSig = "西格玛";       // 只在「下策甲」（低分版夹具里没有）

    private const string Query = "阿尔法，伽马射线，德尔塔，奥米加，西格玛，通用";

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.WriteLine("=== 上中下分级检查 ===");

        string root = Directory.GetCurrentDirectory();
        string resDir = Path.Combine(root, "resources");

        // ---- 1. 惰性：没有分级的世界 ----
        string plain = MakeDir(resDir, "jigu-tier-plain");
        File.WriteAllText(Path.Combine(plain, "corpus.json"), BuildFixture(false, false),
            new UTF8Encoding(false));
        Corpus c0 = new Corpus();
        c0.LoadFrom(plain, null);
        Console.WriteLine("惰性夹具: " + c0.DocCount + " 条");

        bool graded0;
        List<Corpus.SearchHit> byVerdict = c0.SearchByVerdict(Query, 3, out graded0);
        List<Corpus.SearchHit> byScore = c0.SearchScored(Query, 3);
        Check(!graded0, "无分级语料上 graded == false（整体走旧路径）", "实际 " + graded0);
        bool same = byVerdict.Count == byScore.Count;
        if (same)
            for (int i = 0; i < byScore.Count; i++)
            {
                if (byVerdict[i].Doc != byScore[i].Doc || byVerdict[i].Score != byScore[i].Score)
                { same = false; break; }
            }
        Check(same, "无分级语料上结果与 SearchScored 逐条相同（含分数）",
            "分级 " + byVerdict.Count + " 条 vs 直接 " + byScore.Count + " 条");
        int wv, up, mid, down;
        c0.VerdictStats(out wv, out up, out mid, out down);
        Check(wv == 0 && up == 0 && mid == 0 && down == 0, "无分级语料上 VerdictStats 全 0",
            "withVerdict=" + wv + " 上=" + up + " 中=" + mid + " 下=" + down);

        // ---- 2~5. 有分级的世界 ----
        string gradedDir = MakeDir(resDir, "jigu-tier-graded");
        File.WriteAllText(Path.Combine(gradedDir, "corpus.json"), BuildFixture(true, false),
            new UTF8Encoding(false));
        Corpus c1 = new Corpus();
        c1.LoadFrom(gradedDir, null);

        bool graded1;
        List<Corpus.SearchHit> picked = c1.SearchByVerdict(Query, 3, out graded1);
        Check(graded1, "有分级语料上 graded == true（反证：守卫不是恒真的死代码）", "实际 " + graded1);
        Console.WriteLine("分级结果: " + Describe(picked));

        Check(picked.Count == 3, "上中下三档各一条", "实际 " + picked.Count + " 条");
        string[] want = new string[] { "上", "中", "下" };
        bool order = picked.Count == 3;
        if (order)
            for (int i = 0; i < 3; i++)
                if (picked[i].Doc.Verdict != want[i]) { order = false; break; }
        Check(order, "输出顺序固定 上 → 中 → 下（不是按分数排）", Describe(picked));

        // 同档取最高分那条：「上策甲」命中三个词元（含两个 df=1 的），
        // 「上策乙」只命中一个，前者必须胜出。
        Check(picked.Count > 0 && picked[0].Doc.Title == "上策甲",
            "同一档里取分数最高的那条（上策甲 > 上策乙）",
            picked.Count > 0 ? "实际取到「" + picked[0].Doc.Title + "」" : "无结果");

        // 下限：填充条目每条都有「通用」，分数是 1.0 上下，远低于 floor，必须一条都不进。
        bool noFiller = true;
        foreach (Corpus.SearchHit h in picked)
            if (h.Doc.Verdict.Length == 0) noFiller = false;
        Check(noFiller, "没有档位的条目不会被拿来占档位", Describe(picked));
        Check(graded1 && picked.Count == 3 && !HasTitle(picked, "填充0"),
            "低于相关性下限的条目进不了档（VerdictFloorRatio 在挡人）",
            "命中填充条目=" + (HasTitle(picked, "填充0") ? "是" : "否"));

        // ---- 4. 缺档：下策不够格就少一条 ----
        string missing = MakeDir(resDir, "jigu-tier-missing");
        File.WriteAllText(Path.Combine(missing, "corpus.json"), BuildFixture(true, true),
            new UTF8Encoding(false));
        Corpus c2 = new Corpus();
        c2.LoadFrom(missing, null);
        bool graded2;
        List<Corpus.SearchHit> picked2 = c2.SearchByVerdict(Query, 3, out graded2);
        Check(graded2 && picked2.Count == 2, "下策无够格候选时只给两条（不硬凑第三条）",
            "graded=" + graded2 + " 条数=" + picked2.Count + " " + Describe(picked2));
        bool noDown = true;
        foreach (Corpus.SearchHit h in picked2) if (h.Doc.Verdict == "下") noDown = false;
        Check(noDown, "缺的那一档不拿别的条目顶替", Describe(picked2));

        string terms2; long ms2;
        string json2 = c2.SearchToJson(Query, 3, out terms2, out ms2);
        Check(json2.IndexOf("\"tiers\":[{\"verdict\":\"上\",\"hit\":0},{\"verdict\":\"中\",\"hit\":1},{\"verdict\":\"下\",\"hit\":-1}]",
                StringComparison.Ordinal) >= 0,
            "缺档在 tiers 里如实记为 hit:-1", Short(json2));
        Check(json2.IndexOf("\"verdictDocs\":4", StringComparison.Ordinal) > 0,
            "verdictDocs 报出带档位的条数（4 条）", Short(json2));

        // ---- 7. 出口 ----
        Check(json2.IndexOf("\"graded\":true", StringComparison.Ordinal) > 0, "吐出 graded:true", Short(json2));
        Check(json2.IndexOf("\"verdict\":\"上\"", StringComparison.Ordinal) > 0
            && json2.IndexOf("\"verdictWhy\":\"上策判据甲\"", StringComparison.Ordinal) > 0,
            "吐出 verdict / verdictWhy", Short(json2));
        string json0 = c0.SearchToJson(Query, 3, out terms2, out ms2);
        Check(json0.IndexOf("\"graded\":false", StringComparison.Ordinal) > 0
            && json0.IndexOf("\"tiers\"", StringComparison.Ordinal) < 0,
            "无分级时不吐 tiers（前端看不到就不渲染分栏）", Short(json0));
        Check(json0.IndexOf("\"verdict\":\"\",\"verdictWhy\":\"\"", StringComparison.Ordinal) > 0,
            "旧分片缺字段时吐出空串而不是报错", Short(json0));
        Check(json0.IndexOf("\"verdictDocs\":0", StringComparison.Ordinal) > 0, "verdictDocs:0", Short(json0));

        // 读取端：非法取值归空、容错取值归位
        Check(Corpus.NormalizeVerdict("上策") == "上" && Corpus.NormalizeVerdict(" 中 ") == "中"
            && Corpus.NormalizeVerdict("下") == "下",
            "归一：上策/ 中 /下 都能读成档位", "");
        Check(Corpus.NormalizeVerdict("优") == "" && Corpus.NormalizeVerdict("上等") == ""
            && Corpus.NormalizeVerdict("下面") == "" && Corpus.NormalizeVerdict(null) == "",
            "归一：认不出的取值一律归空（宁可当没标注，也不造幽灵档位）", "");
        string junkDir = MakeDir(resDir, "jigu-tier-junk");
        File.WriteAllText(Path.Combine(junkDir, "corpus.json"),
            "{\"book\":\"测试书\",\"items\":[" +
            "{\"title\":\"怪值\",\"original\":\"通用\",\"verdict\":\"优\",\"verdictWhy\":\"乱写的\"}," +
            "{\"title\":\"带策字\",\"original\":\"通用\",\"verdict\":\"上策\"}," +
            "{\"title\":\"没字段\",\"original\":\"通用\"}]}", new UTF8Encoding(false));
        Corpus c3 = new Corpus();
        c3.LoadFrom(junkDir, null);
        c3.VerdictStats(out wv, out up, out mid, out down);
        Check(wv == 1 && up == 1, "读入端：『优』归空、『上策』读成上策，带档位的只有 1 条",
            "withVerdict=" + wv + " 上=" + up);

        // ---- 6. 理由链路 ----
        CheckWhy(root, resDir);

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "RESULT: TIERCHECK OK (" + _passes + " checks)"
            : "RESULT: TIERCHECK FAILED (" + _fails + " failed / " + _passes + " passed)");
        Directory.Delete(plain, true);
        Directory.Delete(gradedDir, true);
        Directory.Delete(missing, true);
        Directory.Delete(junkDir, true);
        return _fails == 0 ? 0 : 1;
    }

    private static void CheckWhy(string root, string resDir)
    {
        Console.WriteLine();
        Console.WriteLine("--- 理由链路 ---");
        string dir = MakeDir(resDir, "jigu-tier-why");
        File.WriteAllText(Path.Combine(dir, "corpus.json"),
            "{\"book\":\"测试书\",\"items\":[" +
            "{\"chapter\":\"甲\",\"title\":\"挂用人的\",\"original\":\"甲乙丙丁戊己庚辛\"," +
            " \"translation\":\"白话甲\",\"decision\":\"决策甲\",\"outcome\":\"结果甲\"," +
            " \"themes\":[\"用人\"]}," +
            "{\"chapter\":\"乙\",\"title\":\"挂内部矛盾的\",\"original\":\"子丑寅卯辰巳午未\"," +
            " \"translation\":\"白话乙\",\"decision\":\"决策乙\",\"outcome\":\"结果乙\"," +
            " \"themes\":[\"内部矛盾\"]}]}", new UTF8Encoding(false));
        Corpus c = new Corpus();
        c.LoadFrom(dir, null);
        if (c.LabelCount == 0)
        {
            Check(false, "labels.json 没找到，整条同义词桥是断的，下面几问无意义",
                "在 " + resDir + " 下找不到 labels.json");
            return;
        }

        Dictionary<string, List<int>> index = IndexOf(c);
        LabelTable lt = LabelTable.Load(dir);
        string norm = Corpus.Normalize("不会用人");

        HashSet<string> expanded = lt.Expand(norm, index);
        Dictionary<string, List<string>> traced = lt.ExpandTraced(norm, index);
        HashSet<string> tracedKeys = new HashSet<string>(traced.Keys, StringComparer.Ordinal);
        Check(expanded.SetEquals(tracedKeys),
            "Expand 与 ExpandTraced 的标签集合全等（两套护栏没有漂移）",
            "Expand=" + Join(expanded) + " / ExpandTraced=" + Join(tracedKeys));
        Check(expanded.Contains("用人"), "「不会用人」桥到了标签「用人」", Join(expanded));

        // 真的挂了这个标签的条目必须被桥到，没挂的不能
        List<Corpus.SearchHit> hits = c.SearchScored("不会用人", 3);
        bool hitA = false, missB = true;
        Corpus.SearchHit a = null;
        foreach (Corpus.SearchHit h in hits)
        {
            if (h.Doc.Title == "挂用人的") { hitA = true; a = h; }
            if (h.Doc.Title == "挂内部矛盾的") missB = false;
        }
        Check(hitA && missB, "桥只命中挂了该标签的条目",
            "挂用人的=" + hitA + " 挂内部矛盾的被命中=" + !missB);

        bool hasPair = false;
        if (a != null)
            foreach (Corpus.WhyPair p in a.Why)
                if (p.Said == "不会用人" && p.Label == "用人") hasPair = true;
        Check(hasPair, "why 说出「哪句原话 → 哪个标签」",
            a == null ? "没有命中" : "why=" + WhyJoin(a.Why));

        string bridgeJson = c.BridgeToJson(norm);
        Check(bridgeJson.IndexOf("{\"said\":\"不会用人\",\"label\":\"用人\"}", StringComparison.Ordinal) >= 0,
            "BridgeToJson 吐出同一对（观其解字之法面板用）", Short(bridgeJson));

        // 用户直接打标签名 = 同义反复，不算「桥」
        string norm2 = Corpus.Normalize("用人");
        Check(c.BridgeToJson(norm2) == "[]", "用户直接打「用人」时 bridge 为空（不是桥，是同义反复）",
            Short(c.BridgeToJson(norm2)));
        bool saidEqLabel = false;
        foreach (Corpus.SearchHit h in c.SearchScored("用人", 3))
            foreach (Corpus.WhyPair p in h.Why)
                if (p.Said == p.Label) saidEqLabel = true;
        Check(!saidEqLabel, "why 里不出现 said == label 的对", "出现了");

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// _index 是私有的 —— 它是加载期写、检索期只读的内部状态，不该为测试开个口子。
    /// LabelTable.Expand / ExpandTraced 都要拿它做「标签名在不在索引里」「trigger 的 df
    /// 是不是过高」两道判断，所以这里用反射取一份只读引用。写坏了这个字段名，本测试会
    /// 明确报错，不会静默放过。
    /// </summary>
    private static Dictionary<string, List<int>> IndexOf(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._index 不在了，TierCheck 需要同步修改");
        return (Dictionary<string, List<int>>)f.GetValue(c);
    }

    private static string MakeDir(string resDir, string name)
    {
        string tmp = Path.Combine(Path.GetTempPath(), name);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        // labels.json / stopwords.json 必须跟着走，否则同义词桥整条链路静默失效，
        // 测出来的是「没有标签的世界」。
        foreach (string f in new string[] { "labels.json", "stopwords.json" })
        {
            string src = Path.Combine(resDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, f), true);
        }
        return tmp;
    }

    /// <summary>
    /// 20 条填充 + 4 条带档位。
    ///   · 填充：每条都只有「通用」—— 分数最低，一条都不该进档；
    ///   · 上策甲：命中「阿尔法」「伽马射线」两个 df=1 的词，是全场最高分；
    ///   · 上策乙：命中「德尔塔」。与它同档，用来验证「同档取最高分那条」；
    ///   · 中策甲：命中「奥米加」；
    ///   · 下策甲：命中「西格玛」；lowVerdict=true 时改成只有「通用」，让它跌到下限以下。
    /// </summary>
    private static string BuildFixture(bool withVerdicts, bool lowVerdict)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"book\":\"测试书\",\"items\":[");
        Item(sb, withVerdicts, "上策甲", Common + TAlpha + TGamma, "上", "上策判据甲");
        Item(sb, withVerdicts, "上策乙", Common + TDelta, "上", "上策判据乙");
        Item(sb, withVerdicts, "中策甲", Common + TOmega, "中", "中策判据甲");
        Item(sb, withVerdicts, "下策甲", lowVerdict ? Common : Common + TSig, "下", "下策判据甲");
        for (int i = 0; i < 20; i++) Item(sb, withVerdicts, "填充" + i, Common, "", "");
        sb.Append("]}");
        return sb.ToString();
    }

    private static void Item(StringBuilder sb, bool withVerdicts, string title, string body,
        string verdict, string verdictWhy)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '[') sb.Append(',');
        sb.Append("{\"chapter\":\"").Append(title).Append("\",\"title\":\"").Append(title)
          .Append("\",\"original\":\"").Append(body)
          .Append("\",\"translation\":\"白话\",\"decision\":\"决策\",\"outcome\":\"结果\"");
        if (withVerdicts && verdict.Length > 0)
            sb.Append(",\"verdict\":\"").Append(verdict)
              .Append("\",\"verdictWhy\":\"").Append(verdictWhy).Append('"');
        sb.Append('}');
    }

    private static bool HasTitle(List<Corpus.SearchHit> hits, string title)
    {
        foreach (Corpus.SearchHit h in hits) if (h.Doc.Title == title) return true;
        return false;
    }

    private static string Describe(List<Corpus.SearchHit> hits)
    {
        StringBuilder sb = new StringBuilder();
        foreach (Corpus.SearchHit h in hits)
        {
            if (sb.Length > 0) sb.Append(" / ");
            sb.Append(h.Doc.Verdict.Length == 0 ? "无档" : h.Doc.Verdict)
              .Append('「').Append(h.Doc.Title).Append("」").Append(h.Score.ToString("F2"));
        }
        return sb.ToString();
    }

    private static string Join(HashSet<string> set)
    {
        List<string> l = new List<string>(set);
        l.Sort(StringComparer.Ordinal);
        return l.Count == 0 ? "（空）" : string.Join("、", l.ToArray());
    }

    private static string WhyJoin(List<Corpus.WhyPair> why)
    {
        StringBuilder sb = new StringBuilder();
        foreach (Corpus.WhyPair p in why)
        {
            if (sb.Length > 0) sb.Append(" / ");
            sb.Append(p.Said).Append("→").Append(p.Label);
        }
        return sb.ToString();
    }

    private static string Short(string s)
    {
        return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
    }
}
