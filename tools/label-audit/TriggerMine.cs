// FILE: jigu-app/tools/label-audit/TriggerMine.cs
// 触发词挖掘。是 tools/label-audit/TriggerAudit.cs 的反向用法：
//
//   TriggerAudit：给定已有的触发词，算它指向哪个处境 →  用来判断该不该放行。
//   TriggerMine ：给定处境（标签），算还有哪些说法指向它 → 用来补新触发词。
//
// 为什么需要它：Coverage 重排与去填充词匹配都修不了「词汇缺口」。实测的硬例子 ——
// 「核心员工被竞争对手高薪挖走了」不触发任何标签，而语料里明摆着有【骨干离职】；
// 「扩张太快…下面的人阳奉阴违」只认出【扩张】，「阳奉阴违」认不出【管理失控】，
// 而同一件事换个说法（Q3 的「各自自为政不听指挥」）就认得出。触发词表是手写的，
// 手写的东西只会覆盖写的人当时想到的说法。这个工具把「还差哪些说法」变成可算的。
//
// 判据与 TriggerAudit 完全一致，只是量的是候选短语而不是已有触发词：
//
//   指向性(g, L) = 含 g 的史料里，真正挂了标签 L 的比例
//
// 几条刻意的取舍：
//   · 挖的文本是**现代汉语字段**（译文 / 现实意义 / 起因 / 经过 / 关键决策 / 结果 /
//     主题词），外加原文。理由是用户输入的是现代汉语，从文言里挖出来的说法他自己
//     也不会说；但「兔死狗烹」这类已经进入现代汉语的文言短语确实是好触发词，所以
//     原文一并算进来，由指向性去筛。
//   · 文本与候选都先过 Corpus.Normalize + StopWords.Mask，与运行时的匹配口径**逐字
//     对齐** —— 不去填充词的话，「有人不断传话」这种说法挖出来也对不上运行时。
//   · df 一律按**史料条数**算（一条史料里出现十次也只算一次），与 TriggerAudit 的
//     词元 df 口径同源：都问「多少条史料含它」，不是「含它多少次」。
//   · 默认门槛比 TriggerAudit 严一档（df ≥ 3 而不是 2）：放行一个已有的两字词，
//     最坏是多触发一个标签；而新增一个触发词会改变所有查询的结果，宁少而准。
//
// 语料变了要**重跑**。标签是在精选 201 则上标的（分片只标了史记的一部分），
// 所以正类样本只在那 201 则里齐；df 的分母同样只算这 201 则。这是已知的口径限制，
// 写在报告抬头里，不要拿它当全库结论。
//
// 编译（与 src 同编，要用到 internal 的 Corpus / StopWords / LabelTable）：
//   csc -main:TriggerMine -out:tools\label-audit\TriggerMine.exe
//     tools\label-audit\TriggerMine.cs src\*.cs
//     -reference:tests\Microsoft.Web.WebView2.Core.dll
//     -reference:tests\Microsoft.Web.WebView2.WinForms.dll
//
// 用法：
//   TriggerMine.exe <语料目录> [--report 报告路径] [--min-df N] [--min-p X] [--shards]
//                   [--check "说法一" "说法二" ...]
//     <语料目录> 里要有 corpus.json / labels.json / stopwords.json
//       （用 tools\label-audit\make-bench.ps1 拼一个；--shards 会连同分片一起加载）
//     --min-df / --min-p  覆盖默认门槛（df ≥ 3，指向性 ≥ 0.60）
//     --check             不挖词，只量给定的几句说法：各含在哪几条史料里、那些史料
//                         各挂什么标签。这是把已知的缺口说法直接量出指向性的入口。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Jigu;

internal static class TriggerMine
{
    const int MinLen = 2;          // 候选短语最短字数
    const int MaxLen = 6;          // 最长字数
    const int MaxToks = 3;         // 最多由几个词元拼成（再长就不像一句触发说法了）
    const int MinDfDefault = 3;    // 至少出现在几条史料里，才不是「为一条史料记的说法」
    const double MinPDefault = 0.60;
    const int TopPerLabel = 20;    // 每个标签最多列几条候选
    const int ShowDocs = 3;        // 每条候选列出几条样例史料（正反各这么多）

    static void Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        if (args.Length == 0)
        {
            Console.WriteLine("用法: TriggerMine.exe <语料目录> [--report 路径] [--min-df N] [--min-p X]"
                + " [--shards] [--check \"说法\" ...]");
            return;
        }

        string dir = args[0];
        string reportPath = null;
        bool shards = false;
        bool labelMode = false;
        int minDf = MinDfDefault;
        double minP = MinPDefault;
        List<string> checks = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
            else if (args[i] == "--shards") shards = true;
            else if (args[i] == "--labels") labelMode = true;
            else if (args[i] == "--min-df" && i + 1 < args.Length) minDf = int.Parse(args[++i]);
            else if (args[i] == "--min-p" && i + 1 < args.Length)
                minP = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--check") { /* 其余位置参数都是要量的说法 */ }
            else checks.Add(args[i]);
        }

        List<string> shardPaths = null;
        if (shards)
        {
            List<string> slugs = new List<string>();
            foreach (Library.Book b in Library.Available(dir)) slugs.Add(b.Slug);
            shardPaths = Library.ShardPaths(dir, slugs);
        }

        Corpus c = new Corpus();
        c.LoadFrom(dir, shardPaths);
        StopWords sw = StopWords.Load(dir);
        LabelTable lt = LabelTable.Load(dir);
        List<CorpusDoc> docs = Docs(c);
        List<string[]> entries = Entries(lt);

        // 标签名 → 已有触发词（用于排除「已经有的」和它的近似形式）
        Dictionary<string, List<string>> known = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> labels = new List<string>();
        for (int k = 0; k < entries.Count; k++)
        {
            string[] e = entries[k];
            if (known.ContainsKey(e[0])) continue;
            List<string> ts = new List<string>();
            for (int i = 1; i < e.Length; i++) if (!string.IsNullOrEmpty(e[i])) ts.Add(e[i]);
            known[e[0]] = ts;
            labels.Add(e[0]);
        }

        int W = (docs.Count + 63) / 64;
        Dictionary<string, ulong[]> labelMask = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        for (int i = 0; i < labels.Count; i++) labelMask[labels[i]] = new ulong[W];
        for (int d = 0; d < docs.Count; d++)
        {
            string[] themes = docs[d].Themes;
            for (int i = 0; i < themes.Length; i++)
            {
                ulong[] m;
                if (labelMask.TryGetValue(themes[i], out m)) m[d >> 6] |= 1UL << (d & 63);
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("触发词挖掘 · " + dir);
        sb.AppendLine("语料：" + docs.Count + " 条史料"
            + (shards ? "（含分片）" : "（仅精选）") + " / 标签 " + labels.Count + " 个");
        // 口径限制写在抬头里：标签只在精选 201 则上标全了，分片里绝大多数史料没有 themes。
        // 把分母放大到全库会让每条候选的指向性都被未标注的史料稀释成接近 0，
        // 那是「标注没做完」的读数，不是「这个词不准」的读数。
        sb.AppendLine("门槛：df ≥ " + minDf + " 且 指向性 ≥ " + minP.ToString("0.00", CultureInfo.InvariantCulture));
        sb.AppendLine("注：正类样本（挂了标签的史料）只在已标注的条目里存在。若分母远大于标注条数，"
            + "指向性会被未标注史料稀释 —— 那是标注未完成的读数，不是词不准。");
        sb.AppendLine();

        // 统一先算一次「按运行时口径处理的史料文本」，三种模式共用。
        // 分开算的话三处口径会慢慢漂开，而它们量的是同一个东西。
        string[] masked = new string[docs.Count];
        for (int d = 0; d < docs.Count; d++)
            masked[d] = sw.Mask(Corpus.Normalize(DocText(docs[d])));

        if (checks.Count > 0)
        {
            sb.AppendLine("== 指定说法的指向性 ==");
            for (int i = 0; i < checks.Count; i++)
                sb.Append(CheckLine(checks[i], sw, masked, docs, labels));
            sb.AppendLine();
        }
        else if (labelMode)
        {
            LabelDiagnostics(sb, labels, Index(c), docs);
        }
        else
        {
            Mine(sb, c, docs, labels, known, labelMask, W, minDf, minP, masked);
        }

        string report = sb.ToString();
        Console.Write(report);
        if (reportPath != null)
        {
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            Console.WriteLine("报告已写入 " + reportPath);
        }
    }

    static void Mine(StringBuilder sb, Corpus c, List<CorpusDoc> docs,
        List<string> labels, Dictionary<string, List<string>> known,
        Dictionary<string, ulong[]> labelMask, int W, int minDf, double minP, string[] masked)
    {
        Dictionary<string, ulong[]> grams = BuildGrams(c, docs, W, masked);
        Console.WriteLine("枚举完成：" + grams.Count + " 个候选短语");

        // 候选按「指向性最高的那个标签」归位。一个短语只归最贴的那个标签 ——
        // 归多个标签等于承认它不区分处境，那正是要挡掉的东西。
        Dictionary<string, List<Cand>> byLabel = new Dictionary<string, List<Cand>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ulong[]> kv in grams)
        {
            string g = kv.Key;
            ulong[] m = kv.Value;
            int df = PopCount(m);
            if (df < minDf) continue;

            string best = null;
            int bestL = 0;
            double bestP = 0.0;
            for (int i = 0; i < labels.Count; i++)
            {
                int dfL = PopCountAnd(m, labelMask[labels[i]]);
                if (dfL == 0) continue;
                double p = (double)dfL / df;
                // 同分取覆盖面大的那个：df=8 全中比 df=3 全中更可信。
                if (p > bestP + 1e-9 || (Math.Abs(p - bestP) <= 1e-9 && dfL > bestL))
                { best = labels[i]; bestP = p; bestL = dfL; }
            }
            if (best == null || bestP < minP) continue;

            // 排除已有的触发词，以及它们的近似形式（谁包含谁）。
            // 不排的话报告会被一堆 near-duplicate 灌满，看不出真正的新说法。
            if (NearExisting(g, best, known[best])) continue;
            // 标签名自己不必当候选：运行时 Expand 本来就会并入标签名。
            if (string.Equals(g, best, StringComparison.Ordinal)) continue;

            List<Cand> list;
            if (!byLabel.TryGetValue(best, out list)) { list = new List<Cand>(); byLabel[best] = list; }
            Cand cand = new Cand();
            cand.Term = g; cand.Label = best; cand.Df = df; cand.DfL = bestL; cand.P = bestP;
            cand.Mask = m;
            list.Add(cand);
        }

        List<string> sorted = new List<string>(byLabel.Keys);
        sorted.Sort(delegate(string a, string b)
        {
            int r = byLabel[b].Count.CompareTo(byLabel[a].Count);
            return r != 0 ? r : string.CompareOrdinal(a, b);
        });

        for (int i = 0; i < sorted.Count; i++)
        {
            string label = sorted[i];
            List<Cand> list = byLabel[label];
            // 指向性高的在前；同分取**出现条数多**的。
            // 这里曾经反着写（同分取 df 小的），以为「更具体」更好 —— 那是错的：
            // 在 p=1.00 这一档里，df=3 是三条史料的巧合，df=12 才是十二次的证据。
            // 按稀有度排序，会把巧合顶到榜首。
            list.Sort(delegate(Cand a, Cand b)
            {
                int r = b.P.CompareTo(a.P);
                if (r != 0) return r;
                r = b.Df.CompareTo(a.Df);
                if (r != 0) return r;
                return string.CompareOrdinal(a.Term, b.Term);
            });
            List<Cand> kept = DeNest(list, TopPerLabel);

            sb.AppendLine("── " + label + "（已有触发词 " + known[label].Count
                + " 个，新候选 " + kept.Count + " / " + list.Count + "）");
            for (int k = 0; k < kept.Count; k++)
            {
                Cand cd = kept[k];
                sb.AppendLine("   " + Pad(cd.Term, 14)
                    + " df=" + Pad(Convert.ToString(cd.Df), 3)
                    + " dfL=" + Pad(Convert.ToString(cd.DfL), 3)
                    + " p=" + cd.P.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("        正例：" + Titles(docs, cd.Mask, cd.Label, true));
                // 反例比正例更重要：它显示的是「这个说法会把哪些不是这个处境的史料拖进来」。
                // 只看正例，任何泛词都会显得像个好触发词。
                sb.AppendLine("        反例：" + Titles(docs, cd.Mask, cd.Label, false));
            }
            sb.AppendLine();
        }
        sb.AppendLine("共 " + sorted.Count + " 个标签有候选。");
    }

    sealed class Cand
    {
        public string Term, Label;
        public int Df, DfL;
        public double P;
        public ulong[] Mask;
    }

    /// <summary>
    /// 标签名自己的诊断。这是三件事里唯一能立刻解释现有失败的那个。
    ///
    /// 触发词命中之后，Expand 并进查询的是**标签名**，不是触发词本身
    /// （它自己的注释写着：trigger 常常不在语料里，并进去也匹配不到东西）。
    /// 于是整条同义桥的成败系于一步：把标签名并进去，能不能把挂了该标签的史料捞上来。
    ///
    ///   并入有效性(L) = 索引里有这个词元、且正文含它的史料中，真正挂了 L 的比例
    ///
    /// 低有效性有两种坏法，都会让桥白搭：
    ///   · df=0      标签名根本不是索引里的词元 → Expand 的 ContainsKey 护栏直接把 L 丢掉，
    ///               触发词命中了也没用（这一步是静默的，界面上只看到「认出了处境」）。
    ///   · df 大而准头低  这个词在几十条史料正文里出现，其中一半没挂 L → 并入它等于
    ///               把这些不相干的史料一起抬上来，处境认出来了、排序反而更差。
    /// </summary>
    static void LabelDiagnostics(StringBuilder sb, List<string> labels,
        Dictionary<string, List<int>> index, List<CorpusDoc> docs)
    {
        List<Cand> rows = new List<Cand>();
        for (int i = 0; i < labels.Count; i++)
        {
            string L = labels[i];
            Cand r = new Cand();
            r.Label = L;
            r.Term = L;
            List<int> posting;
            if (index.TryGetValue(L, out posting))
            {
                r.Df = posting.Count;
                for (int k = 0; k < posting.Count; k++)
                {
                    CorpusDoc d = docs[posting[k]];
                    if (d.Themes != null && Array.IndexOf(d.Themes, L) >= 0) r.DfL++;
                }
            }
            r.P = r.Df == 0 ? 0.0 : (double)r.DfL / r.Df;
            rows.Add(r);
        }
        rows.Sort(delegate(Cand a, Cand b)
        {
            int r = a.P.CompareTo(b.P);
            if (r != 0) return r;
            return a.Df.CompareTo(b.Df);
        });

        int dead = 0;
        for (int i = 0; i < rows.Count; i++) if (rows[i].Df == 0) dead++;
        sb.AppendLine("== 标签名并入查询的有效性（" + rows.Count + " 个标签，"
            + dead + " 个词元不在索引里） ==");
        sb.AppendLine("（触发词命中后并入的是标签名本身。这一步失效，同义桥就是空转："
            + "界面上显示「认出了处境」，排序却一点没变。）");
        sb.AppendLine();

        for (int i = 0; i < rows.Count; i++)
        {
            Cand r = rows[i];
            if (r.Df == 0)
            {
                sb.AppendLine("   [死路] " + Pad(r.Label, 12)
                    + " 不在索引里 —— 触发词命中也没用，Expand 会静默丢掉它");
                continue;
            }
            sb.AppendLine("   " + Pad(r.Label, 12) + " df=" + Pad(Convert.ToString(r.Df), 4)
                + " dfL=" + Pad(Convert.ToString(r.DfL), 4)
                + " 有效率=" + r.P.ToString("0.00", CultureInfo.InvariantCulture));
            // 只给有效率低的列反例：这些才是「并入它会把不相干的史料一起抬上来」的证据。
            if (r.P < 0.75)
            {
                StringBuilder neg = new StringBuilder();
                int n = 0;
                List<int> posting;
                if (index.TryGetValue(r.Label, out posting))
                {
                    for (int k = 0; k < posting.Count && n < ShowDocs; k++)
                    {
                        CorpusDoc d = docs[posting[k]];
                        if (d.Themes != null && Array.IndexOf(d.Themes, r.Label) >= 0) continue;
                        if (n++ > 0) neg.Append("、");
                        neg.Append(d.Title);
                    }
                }
                if (n > 0) sb.AppendLine("            会被带进来但没挂这个标签：" + neg);
            }
        }
    }

    /// <summary>
    /// 抽候选短语。文本先按运行时的口径归一化 + 去填充词，然后**用产品自己的分词器切开**，
    /// 候选 = 相邻若干个词元拼起来。
    ///
    /// 一开始是直接取 2–5 字的字符子串，结果整个报告是垃圾：`一时无人` `中反` `于自`
    /// `人没` 这种跨词边界的碎片，而且因为它们稀有，指向性天然是 1.00，排在最前面 ——
    /// 按「指向性高且稀有」排序，恰好把最没意义的那些顶到榜首。词对齐之后这两个毛病
    /// 一起消失：候选都是真正说得出口的说法，稀有度也不再等于可信度。
    ///
    /// 分词这一步不能省，也不能自己写个「按标点切」凑合 —— 分词器是产品的一部分，
    /// 用它切出来的词就是检索时会被命中的词，口径才对得上。
    /// </summary>
    static Dictionary<string, ulong[]> BuildGrams(Corpus c, List<CorpusDoc> docs, int W, string[] masked)
    {
        Dictionary<string, ulong[]> grams = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        for (int d = 0; d < docs.Count; d++)
        {
            List<string> toks = c.Tokenize(masked[d]);
            for (int i = 0; i < toks.Count; i++)
            {
                if (!IsWord(toks[i])) continue;
                StringBuilder sb = new StringBuilder(MaxLen);
                for (int w = 1; w <= MaxToks && i + w <= toks.Count; w++)
                {
                    string tk = toks[i + w - 1];
                    if (!IsWord(tk)) break;          // 撞到标点 / 数字 / 拉丁就到此为止
                    sb.Append(tk);
                    if (sb.Length < MinLen) continue;
                    if (sb.Length > MaxLen) break;
                    ulong[] m;
                    string g = sb.ToString();
                    if (!grams.TryGetValue(g, out m)) { m = new ulong[W]; grams[g] = m; }
                    m[d >> 6] |= 1UL << (d & 63);
                }
            }
        }
        return grams;
    }

    /// <summary>是不是「整词都是汉字」—— 标点、数字、拉丁词元一律不参与拼接。</summary>
    static bool IsWord(string tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        for (int i = 0; i < tok.Length; i++) if (!IsCJK(tok[i])) return false;
        return true;
    }

    /// <summary>
    /// 一条史料参与挖掘的全部文本。含 themes 是有意的：主题词本身就是最好的触发词，
    /// 而这张表里哪些主题词还没被写成触发词，正是这个工具该报出来的东西。
    /// </summary>
    static string DocText(CorpusDoc d)
    {
        StringBuilder sb = new StringBuilder(1024);
        sb.Append(d.Title).Append('\n');
        sb.Append(d.Translation).Append('\n');
        sb.Append(d.Significance).Append('\n');
        sb.Append(d.Cause).Append('\n');
        sb.Append(d.Process).Append('\n');
        sb.Append(d.Decision).Append('\n');
        sb.Append(d.Outcome).Append('\n');
        sb.Append(d.Original).Append('\n');
        for (int i = 0; i < d.Themes.Length; i++) sb.Append(d.Themes[i]).Append(' ');
        for (int i = 0; i < d.Pros.Length; i++) sb.Append(d.Pros[i]).Append(' ');
        for (int i = 0; i < d.Cons.Length; i++) sb.Append(d.Cons[i]).Append(' ');
        return sb.ToString();
    }

    static string CheckLine(string said, StopWords sw, string[] masked, List<CorpusDoc> docs,
        List<string> labels)
    {
        string norm = Corpus.Normalize(said);
        string key = sw.Mask(norm);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("### " + said);
        sb.AppendLine("    归一化=\"" + norm + "\"  去填充词=\"" + key + "\"");
        if (key.Length < 2)
        {
            sb.AppendLine("    去填充词后不足 2 字：这个说法本身没有可匹配的骨架。");
            return sb.ToString();
        }

        List<int> hit = new List<int>();
        for (int d = 0; d < docs.Count; d++)
            if (masked[d].IndexOf(key, StringComparison.Ordinal) >= 0) hit.Add(d);
        sb.AppendLine("    含它的史料 " + hit.Count + " 条：");
        if (hit.Count == 0)
        {
            sb.AppendLine("      （一条也没有 —— 这个说法在语料文本里不存在，"
                + "拿它当触发词只能靠标签名间接命中，量不出指向性）");
            return sb.ToString();
        }

        Dictionary<string, int> tally = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < hit.Count; i++)
        {
            CorpusDoc d = docs[hit[i]];
            for (int t = 0; t < d.Themes.Length; t++)
            {
                if (labels.IndexOf(d.Themes[t]) < 0) continue;
                int n;
                tally.TryGetValue(d.Themes[t], out n);
                tally[d.Themes[t]] = n + 1;
            }
            if (i < ShowDocs)
                sb.AppendLine("      · " + d.Title + "  [" + string.Join(" / ", d.Themes) + "]");
        }
        if (hit.Count > ShowDocs) sb.AppendLine("      · ……另 " + (hit.Count - ShowDocs) + " 条");

        List<KeyValuePair<string, int>> rows = new List<KeyValuePair<string, int>>(tally);
        rows.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            int r = b.Value.CompareTo(a.Value);
            return r != 0 ? r : string.CompareOrdinal(a.Key, b.Key);
        });
        sb.Append("    这些史料挂的标签：");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append("，");
            sb.Append(rows[i].Key).Append(' ')
                .Append((100.0 * rows[i].Value / hit.Count).ToString("0", CultureInfo.InvariantCulture))
                .Append('%');
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>短语与已有触发词互为子串就算近似 —— 只在两者都 ≥2 字时判，避免单字误伤。</summary>
    static bool NearExisting(string g, string label, List<string> triggers)
    {
        for (int i = 0; i < triggers.Count; i++)
        {
            string t = triggers[i];
            if (t.Length < 2) continue;
            if (t.IndexOf(g, StringComparison.Ordinal) >= 0) return true;
            if (g.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    /// <summary>去掉互相嵌套的候选，只留最大的那几个，避免报告里全是同一个说法的变体。</summary>
    static List<Cand> DeNest(List<Cand> sorted, int cap)
    {
        List<Cand> kept = new List<Cand>();
        for (int i = 0; i < sorted.Count && kept.Count < cap; i++)
        {
            Cand cd = sorted[i];
            bool nested = false;
            for (int k = 0; k < kept.Count; k++)
            {
                if (kept[k].Term.IndexOf(cd.Term, StringComparison.Ordinal) >= 0
                    || cd.Term.IndexOf(kept[k].Term, StringComparison.Ordinal) >= 0) { nested = true; break; }
            }
            if (!nested) kept.Add(cd);
        }
        return kept;
    }

    /// <summary>
    /// 含这个短语的史料样例。正例 = 挂了该标签的，反例 = 没挂的。
    /// 反例那一侧才是判断的关键：它列出这个说法会拖进来哪些史料，
    /// 而指向性 p 是 1.00 还是 0.43，差别全在反例里有几条。
    /// </summary>
    static string Titles(List<CorpusDoc> docs, ulong[] mask, string label, bool positive)
    {
        StringBuilder sb = new StringBuilder();
        int n = 0;
        for (int d = 0; d < docs.Count; d++)
        {
            if ((mask[d >> 6] & (1UL << (d & 63))) == 0) continue;
            bool has = docs[d].Themes != null
                && Array.IndexOf(docs[d].Themes, label) >= 0;
            if (has != positive) continue;
            if (n > 0) sb.Append("、");
            sb.Append(docs[d].Title);
            if (++n >= ShowDocs) { sb.Append("、…"); break; }
        }
        return n == 0 ? "（无）" : sb.ToString();
    }

    static bool IsCJK(char ch)
    {
        return (ch >= '一' && ch <= '鿿') || (ch >= '㐀' && ch <= '䶿');
    }

    static int PopCount(ulong[] m)
    {
        int n = 0;
        for (int i = 0; i < m.Length; i++) n += PopCount(m[i]);
        return n;
    }

    static int PopCountAnd(ulong[] a, ulong[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) n += PopCount(a[i] & b[i]);
        return n;
    }

    static int PopCount(ulong v)
    {
        v = v - ((v >> 1) & 0x5555555555555555UL);
        v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
        v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
        return (int)((v * 0x0101010101010101UL) >> 56);
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

    static Dictionary<string, List<int>> Index(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_index",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._index 不在了，挖掘工具要同步修改");
        return (Dictionary<string, List<int>>)f.GetValue(c);
    }

    static List<CorpusDoc> Docs(Corpus c)
    {
        FieldInfo f = typeof(Corpus).GetField("_docs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("Corpus._docs 不在了，挖掘工具要同步修改");
        return (List<CorpusDoc>)f.GetValue(c);
    }

    static List<string[]> Entries(LabelTable lt)
    {
        FieldInfo f = typeof(LabelTable).GetField("_entries",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new InvalidOperationException("LabelTable._entries 不在了，挖掘工具要同步修改");
        return (List<string[]>)f.GetValue(lt);
    }
}
