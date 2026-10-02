// FILE: jigu-app/tests/TriggerCheck.cs
// 触发词匹配的护栏检查。钉住四件在真实事故/实测里出现过的事：
//
//   1. 填充词夹在中间仍要命中 —— 「有人不断传话挑拨」必须认出【谗言构陷】。
//      这些填充词（有人/不断/已经/互相）本来就在 stopwords.json 里，那张表自己
//      认定它们「对想找什么相似情境毫无信息」；一边认定无信息、一边让它们挡死匹配，
//      是这套停用词一直没被用对的地方。
//
//   2. 词替换靠补词、不靠机制 —— 「骨干已经想走了」→ 触发词「骨干要走」中间是
//      要/想 的**词替换**，不是填充词，去停用词与间隔预算都救不了。它由 labels.json
//      里补的「骨干想走」命中。这条断言是为了防止有人日后把那条补词删掉。
//
//   3. 两字触发词按指向性放行 —— 放行的（谣言）要真能触发；当初担心的三个坏例子
//      （团队→用人、决策→决断、情报→信息优势）必须继续被挡在外面。
//      放行清单来自 labels.json 的 short_triggers，由 tools/label-audit 算出。
//
//   4. 骨架退化不能变成退步，也不能变成放水 —— 「打不过」摘掉填充词只剩一个「打」字，
//      一字骨架会到处命中，但直接丢弃又会让它**连照原样写都不再命中**。规则是退回字面
//      匹配。反过来，「什么时候动手」的骨架是「动手」这个泛词（df 超过 HighDf），
//      也得退回字面 —— 不能因为原触发词 df=0 就一路绿灯。
//
// 夹具是自造的：只有自造语料才能把 df 摆成想要的值（df(动手) 必须 > HighDf）。
//
// 编译（与 src 同编，要用到 internal 的 StopWords / LabelTable）：
//   csc -main:TriggerCheck -out:tests\TriggerCheck.exe tests\TriggerCheck.cs src\*.cs
//     -reference:tests\Microsoft.Web.WebView2.Core.dll
//     -reference:tests\Microsoft.Web.WebView2.WinForms.dll
// 从仓库根目录运行（要读 resources\labels.json 与 resources\stopwords.json）：
//   tests\TriggerCheck.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Jigu;

internal static class TriggerCheck
{
    private static int _failed;
    private static int _passed;

    // 填充词夹具：每条塞一个「动手」，用来把 df(动手) 顶到 HighDf 以上。
    // 16 条 + 后面那条「什么时候动手」= 17 > 15。
    private const int FillerCount = 16;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        Console.WriteLine("=== 触发词匹配检查 ===");

        string root = Directory.GetCurrentDirectory();
        string resDir = Path.Combine(root, "resources");
        if (!File.Exists(Path.Combine(resDir, "labels.json")))
        {
            Console.WriteLine("在 " + resDir + " 下找不到 labels.json，全部断言无意义");
            return 1;
        }

        string dir = MakeDir(resDir, "jigu-trigger");
        File.WriteAllText(Path.Combine(dir, "corpus.json"), BuildFixture(),
            new UTF8Encoding(false));
        Corpus c = new Corpus();
        c.LoadFrom(dir, null);
        if (c.LabelCount == 0)
        {
            Console.WriteLine("labels.json 没加载上，整条同义词桥是断的");
            return 1;
        }

        Dictionary<string, List<int>> index = IndexOf(c);
        LabelTable lt = LabelTable.Load(dir);

        // df(动手) 得真的超过 HighDf，否则第 4 组断言是在验一个不存在的护栏。
        List<int> posting;
        int dfDong = index.TryGetValue("动手", out posting) ? posting.Count : 0;
        Check(dfDong > 15, "夹具把 df(动手) 顶到 HighDf 之上（否则第 4 组断言是空跑）",
            "df(动手)=" + dfDong);

        // ---- 1. 填充词夹在中间 ----
        HashSet<string> t1 = Expand(lt, index, "团队里有人不断传话挑拨");
        Check(t1.Contains("谗言构陷"), "「有人不断传话挑拨」认出【谗言构陷】（中间夹了「不断」）",
            Join(t1));
        HashSet<string> t1b = Expand(lt, index, "被人挑拨离间");
        Check(t1b.Contains("谗言构陷"), "「被人挑拨」也认【谗言构陷】（同一个处境的另一种说法）",
            Join(t1b));

        // ---- 2. 词替换靠补词 ----
        HashSet<string> t2 = Expand(lt, index, "骨干已经想走了");
        Check(t2.Contains("骨干离职"), "「骨干已经想走了」认出【骨干离职】",
            Join(t2));
        HashSet<string> t2b = Expand(lt, index, "骨干已经要走了");
        Check(t2b.Contains("骨干离职"), "「骨干已经要走了」（有「要」字）同样认【骨干离职】",
            Join(t2b));

        // ---- 3. 两字触发词：放行的要真触发，坏例子要继续挡住 ----
        HashSet<string> t3 = Expand(lt, index, "外面谣言四起");
        Check(t3.Contains("谗言构陷"), "放行的两字词「谣言」真能触发【谗言构陷】", Join(t3));

        HashSet<string> bad1 = Expand(lt, index, "团队");
        Check(!bad1.Contains("用人"), "「团队」不触发【用人】（当初担心的例子，p=0.36）", Join(bad1));
        HashSet<string> bad2 = Expand(lt, index, "决策");
        Check(!bad2.Contains("决断"), "「决策」不触发【决断】（p=0.21）", Join(bad2));
        HashSet<string> bad3 = Expand(lt, index, "情报");
        Check(!bad3.Contains("信息优势"), "「情报」不触发【信息优势】（p=0.00）", Join(bad3));

        // ---- 4. 骨架退化：该退回字面的退回字面 ----
        HashSet<string> t4 = Expand(lt, index, "打不过对手");
        Check(t4.Contains("敌强我弱"), "「打不过」照原样写仍然命中【敌强我弱】（骨架只剩「打」字）",
            Join(t4));
        HashSet<string> t4b = Expand(lt, index, "什么时候动手");
        Check(t4b.Contains("待时而动"), "「什么时候动手」照原样写仍然命中【待时而动】",
            Join(t4b));
        HashSet<string> t4c = Expand(lt, index, "动手");
        Check(!t4c.Contains("待时而动"),
            "单写「动手」**不**触发【待时而动】（骨架 df 超过 HighDf，退回字面）", Join(t4c));

        // ---- 5. 有序 + 间隔预算 ----
        HashSet<string> t5 = Expand(lt, index, "不可承受这种巨大成本");
        Check(!t5.Contains("长期代价"), "间隔超预算不命中（「不可承受…成本」中间夹了 4 个字）",
            Join(t5));
        HashSet<string> t5b = Expand(lt, index, "成本不可承受");
        Check(!t5b.Contains("长期代价"), "乱序不命中（「成本」写在「不可承受」前面）", Join(t5b));
        HashSet<string> t5c = Expand(lt, index, "不可承受的成本");
        Check(t5c.Contains("长期代价"), "夹一个填充词（「的」）仍在预算内，命中【长期代价】",
            Join(t5c));
        HashSet<string> t5d = Expand(lt, index, "不可承受成本");
        Check(t5d.Contains("长期代价"), "照原样写命中【长期代价】（基线）", Join(t5d));

        // ---- 6. 否定识别：只压制，且只压「词外紧邻」的 ----
        // 规则在 LabelTable.Hit 里。三条边界各有一条断言，因为这三条都是踩过的：
        //   · 紧邻要真压住（否则这条规则是死代码）；
        //   · 词内的否定**不能**压 —— 「不信任」是【猜忌】的触发词，那个 不 在词里，
        //     压掉它 C17「他们并不信任对方」会掉回 0 个处境直接变红（杀弃判据）；
        //   · 隔字的不压 —— 「别再内斗」里 别 和 内斗 之间隔着 再，中文歧义太大，不动手。
        HashSet<string> t6 = Expand(lt, index, "大家别内斗了");
        Check(!t6.Contains("内部矛盾"), "否定紧邻触发词时压制（「别内斗」不认【内部矛盾】）", Join(t6));
        HashSet<string> t6b = Expand(lt, index, "大家内斗了");
        Check(t6b.Contains("内部矛盾"), "同一句去掉否定词仍然认出（对照组，证明上一条不是恒假）",
            Join(t6b));
        HashSet<string> t6c = Expand(lt, index, "他们并不信任对方");
        Check(t6c.Contains("猜忌"), "否定词在触发词**里面**时不压（「不信任」→【猜忌】，C17 的杀弃判据）",
            Join(t6c));
        HashSet<string> t6d = Expand(lt, index, "我一点不猜忌他");
        Check(!t6d.Contains("猜忌"), "标签名紧跟在否定词后面时压制（「不猜忌」）", Join(t6d));
        HashSet<string> t6e = Expand(lt, index, "我猜忌他");
        Check(t6e.Contains("猜忌"), "同一句去掉否定词仍然认出（对照组）", Join(t6e));
        HashSet<string> t6f = Expand(lt, index, "团队里别再内斗了");
        Check(t6f.Contains("内部矛盾"), "否定词与触发词之间隔了字就不压（「别再内斗」）", Join(t6f));

        // ---- 7. 两套护栏不许漂移 + 不编造引语 ----
        string[] all = {
            "团队里有人不断传话挑拨", "骨干已经想走了", "外面谣言四起", "团队", "决策", "情报",
            "打不过对手", "什么时候动手", "动手", "不可承受这种巨大成本", "成本不可承受",
            "不可承受的成本", "不可承受成本", "不会用人", "用人" };
        bool drift = false, fabricated = false;
        foreach (string q in all)
        {
            string norm = Corpus.Normalize(q);
            HashSet<string> a = lt.Expand(norm, index);
            Dictionary<string, List<string>> b = lt.ExpandTraced(norm, index);
            if (!a.SetEquals(new HashSet<string>(b.Keys, StringComparer.Ordinal))) drift = true;
            // ExpandTraced 里出现 said == label 是**索引 0**（标签名自匹配）的正常结果，
            // 过滤它的是 BridgeToJson 与 BuildWhy（TierCheck 各有一条断言钉着）。
            // 这里要挡的是另一种：用户没写那个标签名，却被某个触发词「说成」了它 ——
            // 那才是编造引语。判据就是「原句里到底有没有这个标签名」。
            foreach (KeyValuePair<string, List<string>> kv in b)
                foreach (string said in kv.Value)
                    if (string.Equals(said, kv.Key, StringComparison.Ordinal)
                        && q.IndexOf(said, StringComparison.Ordinal) < 0)
                        fabricated = true;
        }
        Check(!drift, "Expand 与 ExpandTraced 集合全等（两套护栏不漂移）", drift ? "有漂移" : "一致");
        Check(!fabricated, "没写标签名时不会凭空说成标签名（不编造引语）",
            fabricated ? "编造了" : "干净");

        Directory.Delete(dir, true);
        Console.WriteLine();
        Console.WriteLine("RESULT: TRIGGERCHECK " + (_failed == 0 ? "OK" : "FAILED")
            + " (" + _passed + " passed, " + _failed + " failed)");
        return _failed == 0 ? 0 : 1;
    }

    private static HashSet<string> Expand(LabelTable lt, Dictionary<string, List<int>> index,
        string query)
    {
        return lt.Expand(Corpus.Normalize(query), index);
    }

    /// <summary>
    /// 八条带标签的史料 + 16 条填充。带标签的每条都把标签名写进 themes，
    /// 因为 Expand 只并**索引里真有**的标签名。
    /// </summary>
    private static string BuildFixture()
    {
        StringBuilder items = new StringBuilder();
        AddDoc(items, "反间记", "有人传话挑拨，陈平用间", "谗言构陷");
        AddDoc(items, "留人记", "骨干出走，留不住人", "骨干离职");
        AddDoc(items, "用人之道", "怎么用人", "用人");
        AddDoc(items, "决断记", "当断不断，反受其乱", "决断");
        AddDoc(items, "耳目记", "情报为先", "信息优势");
        AddDoc(items, "不听号令", "下面不听，各自为政", "将不受命");
        AddDoc(items, "待时记", "什么时候动手", "待时而动");
        AddDoc(items, "以弱胜强", "打不过对手", "敌强我弱");
        AddDoc(items, "输粮记", "不可承受成本", "长期代价");
        AddDoc(items, "空耗记", "现金流断裂", "资源不足");
        // 第 6 组（否定识别）要用的两个标签：Expand 只并**索引里真有**的标签名，
        // 夹具里没有挂这两个标签的史料，触发词命中了也会被静默丢掉。
        AddDoc(items, "内斗记", "同室操戈，各自为政", "内部矛盾");
        AddDoc(items, "相疑记", "上下离心，骨肉相残", "猜忌");
        for (int i = 0; i < FillerCount; i++)
            AddDoc(items, "填充" + i, "动手动手动手", "民心");
        return "{\"book\":\"触发词夹具\",\"items\":[" + items + "]}";
    }

    private static void AddDoc(StringBuilder sb, string title, string original, string theme)
    {
        if (sb.Length > 0) sb.Append(',');
        sb.Append("{\"chapter\":\"甲\",\"title\":\"").Append(title)
          .Append("\",\"original\":\"").Append(original)
          .Append("\",\"translation\":\"白话").Append(title)
          .Append("\",\"themes\":[\"").Append(theme).Append("\"]}");
    }

    private static Dictionary<string, List<int>> IndexOf(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_index",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._index 不在了，TriggerCheck 要同步修改");
        return (Dictionary<string, List<int>>)f.GetValue(c);
    }

    private static string MakeDir(string resDir, string name)
    {
        string tmp = Path.Combine(Path.GetTempPath(), name);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        // labels.json / stopwords.json 必须跟着走，否则测出来的是「没有标签的世界」。
        foreach (string f in new string[] { "labels.json", "stopwords.json" })
        {
            string src = Path.Combine(resDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, f), true);
        }
        return tmp;
    }

    private static string Join(IEnumerable<string> items)
    {
        List<string> list = new List<string>(items);
        list.Sort(StringComparer.Ordinal);
        StringBuilder sb = new StringBuilder();
        foreach (string s in list) { if (sb.Length > 0) sb.Append('/'); sb.Append(s); }
        return sb.ToString();
    }

    private static void Check(bool ok, string what, string detail)
    {
        if (ok) { _passed++; Console.WriteLine("  PASS " + what); }
        else { _failed++; Console.WriteLine("  FAIL " + what + "  [" + detail + "]"); }
    }
}
