// FILE: jigu-app/tools/label-audit/TriggerAudit.cs
// 触发词指向性审计。回答一个数据问题，而不是靠手圈词：
//
//   指向性(t, L) = 含 t 的史料里，真正挂了标签 L 的比例
//
// 为什么要它：docs/待决策清单.md 的「三之补：2 字 trigger 的护栏」挂着一件没决定的事 ——
// 非标签名的触发词原本必须 ≥3 字才可能触发，于是 177 个两字触发词（谣言 / 流言 / 反间 /
// 士气 / 决策 / 情报…）全是死代码。当初不敢放开的顾虑是「团队」会误触发【用人】。
// 这个顾虑是对的，但它不是「两字词一律危险」，而是「有些两字词指向性低」。把指向性
// 算出来，危险的那几个自然落到门槛下面，而不是靠人工记一份黑名单（黑名单是当初被否掉的方案 C）。
//
// 产出两份：
//   ① 给机器读：labels.json 的 short_triggers 字段（triggers 的子集，放行的那几个再列一遍）。
//   ② 给人读：报告，每个两字触发词一行（词 / 标签 / df / dfL / 指向性 / 放行与否），
//      按指向性排序，边界附近的单独标出来过目。
//
// df 一律取**倒排索引里的词元 df**（index[t].Count），不是「全文子串命中数」。
// 这一点拿三个已知值校准过：团队=25 / 决策=80 / 情报=5 / 谣言=2 / 士气=25，
// 与方案里写下的那组参考值一致。t 不是词元时 df=0，属于「无从判断」，按不放行处理。
//
// 语料变了要**重跑**：分片逐部标注之后指向性会漂移，这份清单会悄悄过期。
//
// 编译（与 src 同编，要用到 internal 的 StopWords / DataFiles）：
//   csc -main:TriggerAudit -out:tools\label-audit\TriggerAudit.exe
//     tools\label-audit\TriggerAudit.cs src\*.cs
//     -reference:tests\Microsoft.Web.WebView2.Core.dll
//     -reference:tests\Microsoft.Web.WebView2.WinForms.dll
//
// 用法：
//   TriggerAudit.exe <语料目录> [--report 报告路径] [--emit-short 清单路径] [--apply] [--shards]
//     <语料目录> 里要有 corpus.json / labels.json / stopwords.json
//     --apply    直接把 short_triggers 写回 <语料目录>\labels.json（按 `]` 括回原位插入，
//                不动其余任何一个字节；改完务必再跑一次 tests\DataCheck.exe 复核）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Jigu;

internal static class TriggerAudit
{
    // 放行门槛。三条理由各挡一类：
    //   df ≥ 2      挡住「只为一条史料记的词」—— 那种 p=1.00 是背下来的一条，不是统计结论。
    //   df ≤ 15     与 Corpus.HighDf 同值；这个量级的词 df 一涨就会被既有的 df 护栏跳过，
    //               放行等于白放。
    //   p ≥ 0.60    挡住「撞上一个泛词就赢」。
    const int MinDf = 2;
    const int MaxDf = 15;
    const double MinP = 0.60;
    // 边界带：落在这里的条目要人工过目，不自动定夺。
    const double EdgeP = 0.10;

    static void Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        if (args.Length == 0)
        {
            Console.WriteLine("用法: TriggerAudit.exe <语料目录> [--report 路径] [--emit-short 路径] [--apply]");
            return;
        }
        string dir = args[0];
        string reportPath = null, emitPath = null;
        bool apply = false, shards = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
            else if (args[i] == "--emit-short" && i + 1 < args.Length) emitPath = args[++i];
            else if (args[i] == "--apply") apply = true;
            else if (args[i] == "--shards") shards = true;
        }

        // 判据必须在**完整语料**（精选 + 分片）上算。只在精选那 201 条上算等于用半个
        // 语料做决定：df 随文档数缩放，精选上 df=0 的词一加载分片就 df 上百，
        // 门槛两侧的判定整个翻面。--shards 就是把分片一起加载进来。
        List<string> shardPaths = null;
        if (shards)
        {
            List<string> slugs = new List<string>();
            foreach (Library.Book b in Library.Available(dir)) slugs.Add(b.Slug);
            shardPaths = Library.ShardPaths(dir, slugs);
            Console.WriteLine("加载分片 " + slugs.Count + " 部：" + string.Join("/", slugs.ToArray()));
        }

        Corpus c = new Corpus();
        c.LoadFrom(dir, shardPaths);
        StopWords sw = StopWords.Load(dir);
        LabelTable lt = LabelTable.Load(dir);
        Dictionary<string, List<int>> index = Index(c);
        List<CorpusDoc> docs = Docs(c);

        List<Stats> shortRows = new List<Stats>();   // 两字触发词（字面）
        List<Stats> coreRows = new List<Stats>();    // 长触发词被摘成短骨架（core < 3 字）
        Dictionary<string, List<string>> allow = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        List<string[]> entries = Entries(lt);
        for (int k = 0; k < entries.Count; k++)
        {
            string[] e = entries[k];
            string label = e[0];
            for (int i = 1; i < e.Length; i++)
            {
                string t = e[i];
                if (string.IsNullOrEmpty(t)) continue;

                Stats raw = Measure(t, label, index, docs);
                if (t.Length < 3)
                {
                    raw.Edge = raw.Df == 1 || (raw.Df > 0 && raw.Df < MinDf + 1)
                        || (raw.Df > MaxDf && raw.Df <= MaxDf + 5)
                        || (raw.Df > 0 && Math.Abs(raw.P - MinP) <= EdgeP);
                    raw.Pass = raw.Df >= MinDf && raw.Df <= MaxDf && raw.P >= MinP;
                    shortRows.Add(raw);
                    if (raw.Pass)
                    {
                        List<string> list;
                        if (!allow.TryGetValue(label, out list))
                        {
                            list = new List<string>(4);
                            allow[label] = list;
                        }
                        list.Add(t);
                    }
                    continue;
                }

                // 长触发词：看摘掉填充词之后还剩几个字。判别力实际由**骨架**决定，
                // 而骨架不走两字词那道放行门槛，所以要单列出来过目。
                // 运行时的护栏（见 Corpus.MatchingTriggers）：
                //   骨架 < 2 字            → 退回字面匹配
                //   骨架 df > HighDf（泛词）→ 退回字面匹配
                //   其余                   → 按骨架匹配（两字骨架不给间隔预算）
                string core = sw.Mask(t);
                if (!string.Equals(core, t, StringComparison.Ordinal))
                {
                    Stats cs = Measure(core, label, index, docs);
                    // 只列有风险的：骨架不足 3 字，或骨架已是泛词。三个字以上、
                    // df 又在护栏内的骨架是按预期工作的，不必过目。
                    if (core.Length < 3 || cs.Df > 15)
                    {
                        cs.Note = "原始=\"" + t + "\" 骨架=\"" + core + "\""
                            + " 判定=" + CoreVerdict(core, label, cs);
                        coreRows.Add(cs);
                    }
                }
            }
        }

        shortRows.Sort(delegate(Stats a, Stats b) { return b.P.CompareTo(a.P); });
        coreRows.Sort(delegate(Stats a, Stats b) { return a.P.CompareTo(b.P); });

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("触发词审计 · " + dir);
        sb.AppendLine("语料：" + docs.Count + " 条史料 / " + index.Count + " 词元 / 标签 "
            + entries.Count + " 个");
        sb.AppendLine("放行门槛：指向性 ≥ " + MinP.ToString("0.00", CultureInfo.InvariantCulture)
            + " 且 " + MinDf + " ≤ df ≤ " + MaxDf);
        sb.AppendLine();

        int pass = 0, edge = 0;
        foreach (Stats r in shortRows) { if (r.Pass) pass++; if (r.Edge) edge++; }
        sb.AppendLine("== A. 两字触发词（" + shortRows.Count + " 个）：放行 " + pass
            + " / 挡住 " + (shortRows.Count - pass) + "，其中边界 " + edge + " 个 ==");
        foreach (Stats r in shortRows)
            sb.AppendLine(Line(r, r.Pass ? "[放行]" : "[挡住]", r.Edge));
        sb.AppendLine();

        sb.AppendLine("== B. 长触发词被摘成短骨架（骨架 < 3 字）：" + coreRows.Count + " 个 ==");
        sb.AppendLine("（这些的判别力实际由短骨架决定，而短骨架不走两字词那道门槛 —— 单独过目）");
        foreach (Stats r in coreRows)
            sb.AppendLine(r.Note + "   →  " + r.Label + "  df=" + r.Df + " dfL=" + r.DfL
                + " p=" + P(r) + (r.Df == 0 ? "  (骨架不在索引里，无从判断)" : ""));

        string report = sb.ToString();
        Console.Write(report);
        if (reportPath != null)
        {
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            Console.WriteLine("报告已写入 " + reportPath);
        }
        if (emitPath != null)
        {
            File.WriteAllText(emitPath, AllowJson(allow), new UTF8Encoding(false));
            Console.WriteLine("放行清单已写入 " + emitPath);
        }
        if (apply)
        {
            string path = Path.Combine(dir, "labels.json");
            string text = File.ReadAllText(path, Encoding.UTF8);
            string updated = Patch(text, allow);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            Console.WriteLine("short_triggers 已写回 " + path);
        }
    }

    sealed class Stats
    {
        public string Term, Label, Note;
        public int Df, DfL;
        public double P;          // df == 0 时无意义
        public bool Pass, Edge;
    }

    static Stats Measure(string term, string label,
        Dictionary<string, List<int>> index, List<CorpusDoc> docs)
    {
        Stats s = new Stats();
        s.Term = term;
        s.Label = label;
        List<int> posting;
        if (index.TryGetValue(term, out posting))
        {
            s.Df = posting.Count;
            foreach (int d in posting)
                if (Array.IndexOf(docs[d].Themes, label) >= 0) s.DfL++;
        }
        s.P = s.Df == 0 ? 0.0 : (double)s.DfL / s.Df;
        return s;
    }

    static string CoreVerdict(string core, string label, Stats s)
    {
        if (core.Length < 2) return "退回字面（骨架不足 2 字）";
        if (s.Df > 15) return "退回字面（骨架是泛词 df>" + 15 + "）";
        if (string.Equals(core, label, StringComparison.Ordinal)) return "跳过（骨架就是标签名）";
        return "按骨架匹配";
    }

    static string P(Stats r)
    {
        return r.Df == 0 ? "n/a" : r.P.ToString("0.00", CultureInfo.InvariantCulture);
    }

    static string Line(Stats r, string tag, bool edge)
    {
        return tag + (edge ? "[边界]" : "") + " " + Pad(r.Term, 12) + Pad(r.Label, 10)
            + " df=" + Pad(Convert.ToString(r.Df), 4) + " dfL=" + Pad(Convert.ToString(r.DfL), 4)
            + " p=" + P(r);
    }

    static string Pad(string s, int width)
    {
        if (s == null) s = "";
        int w = 0;
        foreach (char ch in s) w += ch > 0x7F ? 2 : 1;
        StringBuilder sb = new StringBuilder(s);
        while (w < width) { sb.Append(' '); w++; }
        return sb.ToString();
    }

    static string AllowJson(Dictionary<string, List<string>> allow)
    {
        List<string> keys = new List<string>(allow.Keys);
        keys.Sort(StringComparer.Ordinal);
        StringBuilder sb = new StringBuilder("{\n");
        for (int i = 0; i < keys.Count; i++)
        {
            sb.Append("  \"").Append(Json.Escape(keys[i])).Append("\": [");
            List<string> list = allow[keys[i]];
            for (int j = 0; j < list.Count; j++)
            {
                if (j > 0) sb.Append(", ");
                sb.Append('"').Append(Json.Escape(list[j])).Append('"');
            }
            sb.Append(']');
            if (i + 1 < keys.Count) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// 把 short_triggers 插进每个标签对象里 triggers 数组的**收尾括号之后**。
    /// 逐字节保留原文件的排版：只找 `"triggers": [` 之后与之括号配对的 `]`，
    /// 在那之后插一段。有的条目 triggers 之后还有 provenance，所以不能假设它是最后一个键。
    /// </summary>
    static string Patch(string text, Dictionary<string, List<string>> allow)
    {
        StringBuilder sb = new StringBuilder(text.Length + 2048);
        int pos = 0;
        int inserted = 0;
        while (true)
        {
            int lab = text.IndexOf("\"label\":", pos, StringComparison.Ordinal);
            if (lab < 0) break;
            int nameStart = text.IndexOf('"', lab + 8);
            int nameEnd = text.IndexOf('"', nameStart + 1);
            string label = text.Substring(nameStart + 1, nameEnd - nameStart - 1);

            int trig = text.IndexOf("\"triggers\":", nameEnd, StringComparison.Ordinal);
            if (trig < 0) break;
            int open = text.IndexOf('[', trig);
            int close = MatchBracket(text, open);

            sb.Append(text, pos, close + 1 - pos);
            List<string> list;
            if (allow.TryGetValue(label, out list) && list.Count > 0)
            {
                sb.Append(",\n      \"short_triggers\": [\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append("        \"").Append(Json.Escape(list[i])).Append('"');
                    if (i + 1 < list.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("      ]");
                inserted++;
            }
            pos = close + 1;
        }
        sb.Append(text, pos, text.Length - pos);
        Console.WriteLine("short_triggers 写入 " + inserted + " 个标签");
        return sb.ToString();
    }

    static int MatchBracket(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"')
            {
                i = SkipString(text, i);
                continue;
            }
            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        throw new InvalidOperationException("triggers 数组的收尾括号没找到");
    }

    static int SkipString(string text, int quote)
    {
        for (int i = quote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        return text.Length - 1;
    }

    static Dictionary<string, List<int>> Index(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_index",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._index 不在了，审计工具要同步修改");
        return (Dictionary<string, List<int>>)f.GetValue(c);
    }

    static List<CorpusDoc> Docs(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_docs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._docs 不在了，审计工具要同步修改");
        return (List<CorpusDoc>)f.GetValue(c);
    }

    static List<string[]> Entries(LabelTable lt)
    {
        FieldInfo f = typeof(LabelTable).GetField("_entries",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("LabelTable._entries 不在了，审计工具要同步修改");
        return (List<string[]>)f.GetValue(lt);
    }
}
