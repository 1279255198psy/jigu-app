// FILE: jigu-app/tools/convert-24histories/Convert24Histories.cs
// 把 alephpi/24histories-simplified-chinese 的 HTML 站点页转成稽古的语料分片。
// 这是**发布期**的离线工具，不随程序分发，也不参与 build.ps1 的编译。
//
// 上游两种排布：
//   A 组（史记/汉书/三国志）  第N章-篇名-{原文,段译,译文}.html
//        段译是段落级对照：<p>原文</p><p style="color:#967d63;">译文</p> 交替出现，
//        配对精确，直接取用。
//   B/C/D 组（其余 21 部）    原文与白话各是一整个章的文件，正文只有**一个** <p> 块，
//        内部没有分段（实测后汉书 6816 字、魏书 9214 字、明史 11500 字都是单块）。
//        于是配对只能到章级，而章级太长不能直接当条目，必须切。
//
// 切分与对齐（B/C/D 组）：两边各自按句读（。！？；）切句，原文按 ~300 字并成 N 块；
// 白话也切成**同样 N 块**，但每块的边界取「按字数比例最接近」的句读处。两边是同一段
// 内容的单调改写，等比例切分能把对应跨度对上——实测各书原文:白话的长度比在
// 0.73～1.55 之间浮动（明史白话比原文还短），按序号硬切必然错位。
//
// 宁可少配也不错配：白话缺失或对不上的章，只出原文（translation 留空），
// 由上层界面按既有逻辑降级显示。
//
// 用法：Convert24Histories.exe <上游根目录> <输出目录> [--only 史记,汉书]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

internal static class Convert24Histories
{
    // 二十四史次序；slug 用作分片文件名，dynasty 供藏书阁分组显示。
    private static readonly string[][] Books = new string[][]
    {
        new string[] { "史记",     "shiji",        "西汉" },
        new string[] { "汉书",     "hanshu",       "东汉" },
        new string[] { "后汉书",   "houhanshu",    "南朝宋" },
        new string[] { "三国志",   "sanguozhi",    "西晋" },
        new string[] { "晋书",     "jinshu",       "唐" },
        new string[] { "宋书",     "songshu",      "南朝梁" },
        new string[] { "南齐书",   "nanqishu",     "南朝梁" },
        new string[] { "梁书",     "liangshu",     "唐" },
        new string[] { "陈书",     "chenshu",      "唐" },
        new string[] { "魏书",     "weishu",       "北齐" },
        new string[] { "北齐书",   "beiqishu",     "唐" },
        new string[] { "周书",     "zhoushu",      "唐" },
        new string[] { "隋书",     "suishu",       "唐" },
        new string[] { "南史",     "nanshi",       "唐" },
        new string[] { "北史",     "beishi",       "唐" },
        new string[] { "旧唐书",   "jiutangshu",   "后晋" },
        new string[] { "新唐书",   "xintangshu",   "北宋" },
        new string[] { "旧五代史", "jiuwudaishi",  "北宋" },
        new string[] { "新五代史", "xinwudaishi",  "北宋" },
        new string[] { "宋史",     "songshi",      "元" },
        new string[] { "辽史",     "liaoshi",      "元" },
        new string[] { "金史",     "jinshi",       "元" },
        new string[] { "元史",     "yuanshi",      "明" },
        new string[] { "明史",     "mingshi",      "清" },
    };

    private const int TargetChunk = 300;   // 每块目标字数
    private const int MinChunk = 120;      // 并块下限：短于此继续并
    private const int DedupMin = 12;       // 与精选集判重的重叠字数门槛
    private const int MaxPairLen = 900;    // 一条成对结果的字数上限，超出按比例再切

    // 成对合理性：白话与原文的字数比落在这个区间外，说明配错了章，宁可退回「仅原文」。
    // 上游同名文件散在「本纪/志/列传/表」等体裁目录里（明史 表/第七章-卷七 与 志/第七章-卷七
    // 同号不同文），而白话侧缺整个体裁目录时，按文件名主干兜底就会把志的白话配到表的原文上。
    private const double PairMaxRatio = 5.0;   // 白话不该比原文长五倍以上
    private const double PairMinRatio = 0.15;  // 也不该短到只剩零头
    private const int PairSlack = 400;         // 短章的抖动余量

    private static readonly List<string> Curated = new List<string>();

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        if (args.Length < 2)
        {
            Console.WriteLine("用法: Convert24Histories.exe <上游根目录> <输出目录> [--only 史记,汉书]");
            return 2;
        }

        string src = args[0];
        string outDir = args[1];
        HashSet<string> only = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--only" && i + 1 < args.Length)
            {
                only = new HashSet<string>(StringComparer.Ordinal);
                foreach (string s in args[i + 1].Split(',')) only.Add(s.Trim());
            }
        }

        if (!Directory.Exists(src)) { Console.WriteLine("FAIL: 找不到上游目录 " + src); return 1; }
        Directory.CreateDirectory(outDir);
        LoadCurated(src);

        Console.WriteLine("上游: " + src);
        Console.WriteLine("输出: " + outDir);
        Console.WriteLine("精选集原文 " + Curated.Count + " 条，用于判重");
        Console.WriteLine();
        Console.WriteLine("书名     章数   条目数    正文字数    落盘字节  配对方式      备注");
        Console.WriteLine("--------------------------------------------------------------------------");

        List<string[]> manifest = new List<string[]>();
        long totalBytes = 0;
        long totalItems = 0;

        foreach (string[] meta in Books)
        {
            string book = meta[0], slug = meta[1], dynasty = meta[2];
            if (only != null && !only.Contains(book)) continue;

            List<Item> items;
            string mode, note;
            int chapters = Convert(Path.Combine(src, book), Path.Combine(src, book + "-白话"), out items, out mode, out note);
            if (chapters == 0) { Console.WriteLine(book.PadRight(8) + " —— 上游缺目录，跳过"); continue; }

            string json = BuildJson(book, items);
            string path = Path.Combine(outDir, slug + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));

            long bytes = new FileInfo(path).Length;
            long chars = 0;
            foreach (Item it in items) chars += it.Original.Length;
            totalBytes += bytes;
            totalItems += items.Count;

            Console.WriteLine(book.PadRight(8)
                + chapters.ToString().PadLeft(4) + "  "
                + items.Count.ToString().PadLeft(7) + "  "
                + chars.ToString().PadLeft(10) + "  "
                + bytes.ToString().PadLeft(10) + "  "
                + mode.PadRight(12) + "  " + note);

            manifest.Add(new string[] { slug, book, dynasty, items.Count.ToString(), chars.ToString(), bytes.ToString(), mode });
        }

        WriteManifest(Path.Combine(outDir, "index.json"), manifest);
        Console.WriteLine("--------------------------------------------------------------------------");
        Console.WriteLine("合计 " + totalItems + " 条，" + (totalBytes / 1048576.0).ToString("0.0") + " MB（未压缩）");
        long deflated = EstimateDeflated(outDir);
        if (deflated > 0)
            Console.WriteLine("deflate 后约 " + (deflated / 1048576.0).ToString("0.0") + " MB —— 安装包按此预估");
        return 0;
    }

    // ------------------------------------------------------------------ 配对与切分

    private sealed class Item
    {
        public string Chapter;
        public string Title;
        public string Original;
        public string Translation;
    }

    private static int Convert(string bookDir, string baiDir, out List<Item> items, out string mode, out string note)
    {
        items = new List<Item>();
        mode = "";
        note = "";
        if (!Directory.Exists(bookDir)) return 0;

        List<string> ywFiles = Collect(bookDir, "原文");
        List<string> duanFiles = Collect(bookDir, "段译");
        int chapters = ywFiles.Count;

        // A 组：段译给出段落级对照，优先用。
        if (duanFiles.Count > 0)
        {
            mode = "段译配对";
            int dropped = 0, cappedA = 0;
            foreach (string f in duanFiles)
            {
                List<Item> pairs = ParseDuanYi(f);
                foreach (Item it in pairs)
                {
                    if (it.Original.Length < 16) continue;
                    if (IsDuplicate(it.Original)) { dropped++; continue; }
                    items.Add(it);
                }
            }
            CapPairs(items, ref cappedA);
            note = dropped > 0 ? "判重剔除 " + dropped : "";
            if (cappedA > 0) note = (note.Length > 0 ? note + "，" : "") + "超长再切 " + cappedA;
            return chapters;
        }

        // B/C/D 组：原文按句切成 N 块，白话按同一 N 等比例切，逐块对齐。
        Dictionary<string, string> bai = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(baiDir)) IndexBai(baiDir, bai);

        int paired = 0, solo = 0, dropped2 = 0, mismatched = 0, capped = 0;
        foreach (string f in ywFiles)
        {
            string name = Path.GetFileName(f);
            string title = TitleOf(name);
            string ywText = ExtractBody(File.ReadAllText(f, Encoding.UTF8));
            if (ywText.Length < 8) continue;

            List<string> ywChunks = ChunkBySentence(ywText, TargetChunk);
            if (ywChunks.Count == 0) continue;

            string baiText;
            List<string> baiChunks = null;
            // 用原文根目录取相对键：白话那边也是相对它自己的根取键，两侧字符串才可比。
            if (LookupBai(bai, bookDir, f, name, title, out baiText) && baiText.Length >= 20)
            {
                if (PairPlausible(ywText.Length, baiText.Length))
                    baiChunks = ChunkBySentenceInto(baiText, ywChunks.Count);
                else
                {
                    mismatched++;
                    Console.WriteLine("  配错章退回仅原文: " + name
                        + "  原文 " + ywText.Length + " 字 / 白话 " + baiText.Length + " 字");
                }
            }

            for (int i = 0; i < ywChunks.Count; i++)
            {
                string orig = ywChunks[i];
                if (orig.Length < 16) continue;
                if (IsDuplicate(orig)) { dropped2++; continue; }
                Item it = new Item();
                it.Chapter = title + " · 段 " + (i + 1);
                it.Title = title;
                it.Original = orig;
                it.Translation = (baiChunks != null && i < baiChunks.Count) ? baiChunks[i] : "";
                items.Add(it);
            }
            if (baiChunks != null) paired++; else solo++;
        }

        CapPairs(items, ref capped);

        mode = paired > 0 && solo == 0 ? "整章对齐" : (paired > 0 ? "部分对齐" : "仅原文");
        if (solo > 0) note = "无白话 " + solo + " 章";
        if (mismatched > 0) note = (note.Length > 0 ? note + "，" : "") + "配错章退回 " + mismatched;
        if (capped > 0) note = (note.Length > 0 ? note + "，" : "") + "超长再切 " + capped;
        if (dropped2 > 0) note = (note.Length > 0 ? note + "，" : "") + "判重剔除 " + dropped2;
        return chapters;
    }

    private static List<string> Collect(string dir, string kind)
    {
        List<string> list = new List<string>();
        Walk(dir, list);
        List<string> kept = new List<string>();
        foreach (string f in list)
        {
            string n = Path.GetFileName(f);
            if (n == Path.GetFileName(dir) + ".html") continue;      // 目录页
            if (n.EndsWith(kind + ".html", StringComparison.Ordinal)) kept.Add(f);
        }
        kept.Sort(StringComparer.Ordinal);
        return kept;
    }

    private static void Walk(string dir, List<string> acc)
    {
        foreach (string d in Directory.GetDirectories(dir)) Walk(d, acc);
        foreach (string f in Directory.GetFiles(dir, "*.html")) acc.Add(f);
    }

    // 白话侧命名各书不一，按可靠性从高到低建四种键：
    //   R: 相对路径主干（同书同体裁同章号）—— 晋书这类「第N章--原文」没有任何篇名，
    //      只有连子目录一起看才区分得开（帝纪/第一章 vs 志/第一章，光看文件名会撞车）
    //   S: 文件名主干
    //   ~: 去掉章号后的篇名
    //   V: 卷号 —— 旧唐书/新唐书两边章号各排各的，只有卷号是对齐的
    // 卷号键容易撞（一卷可能拆成上下），所以另外记数，只在两边都唯一时才用。
    private static void IndexBai(string dir, Dictionary<string, string> map)
    {
        List<string> files = new List<string>();
        Walk(dir, files);
        foreach (string f in files)
        {
            string n = Path.GetFileName(f);
            if (!n.EndsWith(".html", StringComparison.Ordinal)) continue;
            if (n.IndexOf("简介") >= 0) continue;
            if (n == Path.GetFileName(dir) + ".html") continue;
            string text;
            try { text = ExtractBody(File.ReadAllText(f, Encoding.UTF8)); } catch { continue; }
            if (text.Length < 20) continue;

            Put(map, "R:" + RelKey(dir, f), text);
            string stem = Stem(n);
            Put(map, "S:" + stem, text);
            string title = TitleOf(n);
            if (title.Length > 0) Put(map, "~" + title, text);
            int vol = VolNumber(stem);
            if (vol > 0)
            {
                // 两个键都记：带体裁作用域的先试（本纪/志/列传各自从卷一重排），
                // 不带作用域的兜底（有的书体裁目录与内容不符，如新唐书「本纪」里放礼乐志）。
                string scoped = "V:" + VolScope(dir, f) + vol;
                Put(map, scoped, text); Tally(map, "#" + scoped);
                string plain = "V:" + vol;
                Put(map, plain, text); Tally(map, "#" + plain);
            }
        }
    }

    private static void Put(Dictionary<string, string> map, string key, string text)
    {
        if (key.Length > 2 && !map.ContainsKey(key)) map[key] = text;
    }

    private static void Tally(Dictionary<string, string> map, string key)
    {
        string cur;
        int n = 0;
        if (map.TryGetValue(key, out cur)) int.TryParse(cur, out n);
        map[key] = (n + 1).ToString();
    }

    private static bool LookupBai(Dictionary<string, string> map, string baseDir, string full, string name, string title, out string text)
    {
        if (map.TryGetValue("R:" + RelKey(baseDir, full), out text)) return true;
        if (map.TryGetValue("S:" + Stem(name), out text)) return true;
        if (title.Length > 0 && map.TryGetValue("~" + title, out text)) return true;
        int vol = VolNumber(Stem(name));
        if (vol > 0)
        {
            foreach (string k in new string[] { "V:" + VolScope(baseDir, full) + vol, "V:" + vol })
            {
                string one = "1";
                map.TryGetValue("#" + k, out one);
                if (one == "1" && map.TryGetValue(k, out text)) return true;
            }
        }
        text = null;
        return false;
    }

    // 卷号在「本纪/志/列传」里各自从卷一重新起算（旧唐书白话就是这么排的），
    // 所以卷键必须带体裁作用域，否则三套卷一互相撞车、计数全部大于 1 而作废。
    private static string VolScope(string baseDir, string full)
    {
        string rel = full.Substring(baseDir.Length).TrimStart('\\', '/').Replace('\\', '/');
        int slash = rel.IndexOf('/');
        return slash > 0 ? rel.Substring(0, slash) + ":" : "";
    }

    // 相对路径主干：子目录 + 去掉后缀的文件名。两边目录结构一致时才能对上（晋书）。
    private static string RelKey(string baseDir, string full)
    {
        string rel = full.Substring(baseDir.Length).TrimStart('\\', '/');
        string dirPart = Path.GetDirectoryName(rel);
        string stem = Stem(Path.GetFileName(rel));
        return (string.IsNullOrEmpty(dirPart) ? "" : dirPart.Replace('\\', '/') + "/") + stem;
    }

    // 从「…卷一百一十一…」里取卷号；支持中文数字与阿拉伯数字。取不到返回 -1。
    private static int VolNumber(string s)
    {
        int k = s.IndexOf('卷');
        if (k < 0) return -1;
        int i = k + 1;
        if (i >= s.Length) return -1;
        if (s[i] >= '0' && s[i] <= '9')
        {
            int v = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; }
            return v > 0 ? v : -1;
        }
        int section = 0, num = 0;
        bool any = false;
        while (i < s.Length)
        {
            char c = s[i];
            int d = CnDigit(c);
            if (d > 0) { num = d; any = true; }
            else if (d == 0) { num = 0; any = true; }        // 零
            else if (c == '十') { section += (num == 0 ? 1 : num) * 10; num = 0; any = true; }
            else if (c == '百') { section += (num == 0 ? 1 : num) * 100; num = 0; any = true; }
            else if (c == '千') { section += (num == 0 ? 1 : num) * 1000; num = 0; any = true; }
            else break;
            i++;
        }
        if (!any) return -1;
        int total = section + num;
        return total > 0 ? total : -1;
    }

    private static int CnDigit(char c)
    {
        const string d = "零一二三四五六七八九";
        int i = d.IndexOf(c);
        return i < 0 ? -1 : i;
    }

    // 去掉 -原文 / -白话 / -译文 / -段译 后缀。
    private static string Stem(string name)
    {
        string s = name;
        foreach (string suf in new string[] { "-原文.html", "-白话.html", "-译文.html", "-段译.html", ".html" })
            if (s.EndsWith(suf, StringComparison.Ordinal)) { s = s.Substring(0, s.Length - suf.Length); break; }
        return s;
    }

    // 去掉前导「第N章-」与「卷N-」，得到篇名。
    private static string TitleOf(string name)
    {
        string s = Stem(name);
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            string head = s.Substring(0, dash);
            if (head.StartsWith("第", StringComparison.Ordinal) && head.EndsWith("章", StringComparison.Ordinal))
                s = s.Substring(dash + 1);
        }
        if (s.StartsWith("-", StringComparison.Ordinal)) s = s.Substring(1);
        return s.Trim();
    }

    // ------------------------------------------------------------------ 段译

    private static List<Item> ParseDuanYi(string path)
    {
        List<Item> items = new List<Item>();
        string html = File.ReadAllText(path, Encoding.UTF8);
        int h1 = html.IndexOf("</h1>", StringComparison.OrdinalIgnoreCase);
        if (h1 < 0) return items;
        string body = html.Substring(h1 + 5);

        string title = TitleOf(Path.GetFileName(path));
        string pendingYuan = null;
        int idx = 0;
        int pos = 0;
        while (true)
        {
            int lt = body.IndexOf("<p", pos, StringComparison.OrdinalIgnoreCase);
            if (lt < 0) break;
            int gt = body.IndexOf('>', lt);
            if (gt < 0) break;
            int end = body.IndexOf("</p>", gt, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;
            string tag = body.Substring(lt, gt - lt);
            string inner = body.Substring(gt + 1, end - gt - 1);
            pos = end + 4;

            string text = Clean(inner);
            if (text.Length == 0) continue;
            bool isTrans = tag.IndexOf("#967d63", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isTrans)
            {
                if (pendingYuan != null && pendingYuan.Length >= 8)
                {
                    idx++;
                    Item it = new Item();
                    it.Chapter = title + " · 段 " + idx;
                    it.Title = title;
                    it.Original = pendingYuan;
                    it.Translation = text;
                    items.Add(it);
                }
                pendingYuan = null;
            }
            else
            {
                pendingYuan = text;
            }
        }
        return items;
    }

    // ------------------------------------------------------------------ 切分对齐

    private static readonly char[] Enders = new char[] { '。', '！', '？', '；', '!', '?' };

    private static List<string> SplitSentences(string text)
    {
        List<string> sents = new List<string>();
        StringBuilder cur = new StringBuilder();
        foreach (char c in text)
        {
            cur.Append(c);
            if (Array.IndexOf(Enders, c) >= 0) { sents.Add(cur.ToString()); cur.Length = 0; }
        }
        if (cur.Length > 0) sents.Add(cur.ToString());
        return sents;
    }

    // 按句读并块，每块约 target 字。
    private static List<string> ChunkBySentence(string text, int target)
    {
        List<string> sents = SplitSentences(text);
        List<string> chunks = new List<string>();
        StringBuilder cur = new StringBuilder();
        foreach (string s in sents)
        {
            cur.Append(s);
            if (cur.Length >= target) { chunks.Add(cur.ToString()); cur.Length = 0; }
        }
        if (cur.Length > 0)
        {
            if (chunks.Count > 0 && cur.Length < MinChunk) chunks[chunks.Count - 1] += cur.ToString();
            else chunks.Add(cur.ToString());
        }
        return SplitOverlong(chunks, target * 2);
    }

    private static readonly char[] SoftBreaks = new char[] { '，', '、', '；', '：', ',', ';' };

    // 上游有整段无句号的清单式长文（实测新唐书单段 7506 字），只在句号处切切不开，
    // 这里退到逗号/顿号，仍切不动才硬切——否则一条目能顶整章。
    private static List<string> SplitOverlong(List<string> chunks, int max)
    {
        List<string> outp = new List<string>();
        foreach (string c in chunks)
        {
            if (c.Length <= max) { outp.Add(c); continue; }
            int pos = 0;
            while (c.Length - pos > max)
            {
                int cut = -1;
                for (int i = Math.Min(pos + max, c.Length - 1); i > pos + max / 2; i--)
                    if (Array.IndexOf(SoftBreaks, c[i]) >= 0) { cut = i + 1; break; }
                if (cut < 0) cut = pos + max;
                outp.Add(c.Substring(pos, cut - pos));
                pos = cut;
            }
            if (pos < c.Length) outp.Add(c.Substring(pos));
        }
        return outp;
    }

    // 切成**恰好 n 块**：边界取按字数比例最接近目标位置的句读处（单调等比例对齐）。
    private static List<string> ChunkBySentenceInto(string text, int n)
    {
        if (n <= 0) return new List<string>();
        List<string> sents = SplitSentences(text);
        if (sents.Count == 0) return new List<string>();
        if (n == 1) return new List<string> { Join(sents, 0, sents.Count) };
        if (sents.Count <= n)
        {
            List<string> raw = new List<string>(sents);
            while (raw.Count < n) raw.Add("");     // 句数不足则留空块，上层会退成仅原文
            return raw;
        }

        int total = 0;
        foreach (string s in sents) total += s.Length;

        List<string> chunks = new List<string>();
        int start = 0;
        int[] prefix = new int[sents.Count + 1];
        for (int i = 0; i < sents.Count; i++) prefix[i + 1] = prefix[i] + sents[i].Length;

        for (int k = 1; k <= n; k++)
        {
            int target = (int)Math.Round((double)total * k / n);
            if (k == n) { chunks.Add(Join(sents, start, sents.Count)); break; }
            int cut = start + 1;
            int best = int.MaxValue;
            for (int j = start + 1; j < sents.Count; j++)
            {
                int d = Math.Abs(prefix[j] - target);
                if (d < best) { best = d; cut = j; }
                if (prefix[j] > target) break;
            }
            chunks.Add(Join(sents, start, cut));
            start = cut;
        }
        while (chunks.Count < n) chunks.Add("");
        return chunks;
    }

    private static string Join(List<string> sents, int from, int to)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = from; i < to; i++) sb.Append(sents[i]);
        return sb.ToString();
    }

    // 白话与原文的字数比是否正常。只用在「整章对齐」这条路上：那里的白话是等比例
    // 摊到各段上的，比数失衡就说明两边的章根本不是同一章。此时宁可退回「仅原文」——
    // 配错的白话比没有白话更坏，它会把无关的词引进来参与检索。
    private static bool PairPlausible(int yw, int bai)
    {
        if (yw <= 0 || bai <= 0) return false;
        if (bai > yw * PairMaxRatio + PairSlack) return false;
        if (bai < yw * PairMinRatio - PairSlack) return false;
        return true;
    }

    // 一条成对结果不该比一章还长。白话侧此前完全没有上限，实测最长一条 22,951 字
    // （原文只剩 32 字的表序 + 导航），超出就按同一套等比例切法把两侧各切成 k 块 ——
    // 切点都取按字数比例最接近目标位置的句读处，切开后配对关系不散。
    private static void CapPairs(List<Item> items, ref int capped)
    {
        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            int total = it.Original.Length + it.Translation.Length;
            if (total <= MaxPairLen) continue;

            int k = (total + MaxPairLen - 1) / MaxPairLen;
            // 两侧都要留出余量：句数不够时 ChunkBySentenceInto 会给切不动的一侧补空块。
            int room = SplitSentences(it.Original).Count - 1;
            if (it.Translation.Length > 0)
            {
                int t = SplitSentences(it.Translation).Count - 1;
                if (t < room) room = t;
            }
            if (room < k) k = room;
            if (k < 2) continue;   // 句读太少，切不动；原文侧已由 SplitOverlong 压过上限

            List<string> po = ChunkBySentenceInto(it.Original, k);
            List<string> pt = it.Translation.Length > 0 ? ChunkBySentenceInto(it.Translation, k) : null;
            items.RemoveAt(i);
            for (int j = 0; j < k; j++)
            {
                Item sub = new Item();
                sub.Chapter = it.Chapter + "." + (j + 1);
                sub.Title = it.Title;
                sub.Original = po[j];
                sub.Translation = pt != null && j < pt.Count ? pt[j] : "";
                items.Insert(i + j, sub);
            }
            capped++;
            i += k - 1;
        }
    }

    // ------------------------------------------------------------------ 正文抽取

    private static string ExtractBody(string html)
    {
        int h1 = html.IndexOf("</h1>", StringComparison.OrdinalIgnoreCase);
        string body = h1 >= 0 ? html.Substring(h1 + 5) : html;
        return Clean(body);
    }

    // 去标签、解实体、删拼音夹注、压缩空白。
    private static string Clean(string s)
    {
        int i = s.IndexOf("<style", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            int j = s.IndexOf("</style>", i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) break;
            s = s.Substring(0, i) + s.Substring(j + 8);
            i = s.IndexOf("<style", StringComparison.OrdinalIgnoreCase);
        }
        i = s.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            int j = s.IndexOf("</script>", i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) break;
            s = s.Substring(0, i) + s.Substring(j + 9);
            i = s.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        }

        s = ReplaceTag(s, "br", "\n");
        s = ReplaceTag(s, "p", "\n");
        s = StripTags(s);
        s = DecodeEntities(s);
        s = StripPinyin(s);
        s = Collapse(s);
        s = StripNav(s);
        return s;
    }

    // 上游页面在正文之后挂了一对站内导航（「上一节：第六章-卷六 / 下一节：第八章-卷八」），
    // 整章抽取时它会跟着正文一起落进来，实测污染 2178 个条目。它不是史料，一律整行丢掉。
    private static string StripNav(string s)
    {
        if (s.IndexOf("上一节", StringComparison.Ordinal) < 0
            && s.IndexOf("下一节", StringComparison.Ordinal) < 0) return s;
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (string line in s.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("上一节", StringComparison.Ordinal)) continue;
            if (t.StartsWith("下一节", StringComparison.Ordinal)) continue;
            if (t.StartsWith("上一章", StringComparison.Ordinal)) continue;
            if (t.StartsWith("下一章", StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    private static string ReplaceTag(string s, string tag, string with)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "<" + tag + "[^>]*>", with, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "</" + tag + "\\s*>", with, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    private static string StripTags(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string DecodeEntities(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '&') { sb.Append(s[i]); continue; }
            int sc = s.IndexOf(';', i);
            if (sc < 0 || sc - i > 10) { sb.Append(s[i]); continue; }
            string ent = s.Substring(i + 1, sc - i - 1);
            if (ent.Length > 1 && ent[0] == '#')
            {
                int code;
                bool ok = ent.Length > 2 && (ent[1] == 'x' || ent[1] == 'X')
                    ? int.TryParse(ent.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                    : int.TryParse(ent.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
                if (ok && code > 0 && code <= 0x10FFFF) { sb.Append(char.ConvertFromUtf32(code)); i = sc; continue; }
            }
            string rep = null;
            switch (ent.ToLowerInvariant())
            {
                case "amp": rep = "&"; break;
                case "lt": rep = "<"; break;
                case "gt": rep = ">"; break;
                case "quot": rep = "\""; break;
                case "apos": rep = "'"; break;
                case "nbsp": rep = " "; break;
                case "mdash": rep = "—"; break;
                case "hellip": rep = "…"; break;
            }
            if (rep != null) { sb.Append(rep); i = sc; } else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // 删掉纯拼音的夹注，如「荤粥(xūn yǔ)」「冒顿(mòdú)」——ASCII 与声调字母组成的括号组。
    private static string StripPinyin(string s)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            s, @"\(\s*[a-zA-ZāáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜüĀÁǍÀĒÉĚÈĪÍǏÌŌÓǑÒŪÚǓÙÜ\s'·-]{1,40}\)", "");
    }

    private static string Collapse(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        bool prevNl = false;
        foreach (char c in s)
        {
            if (c == '\n')
            {
                if (!prevNl) sb.Append('\n');
                prevNl = true;
                continue;
            }
            if (c == '\r' || c == '\t' || c == ' ' || c == '　')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n' && sb[sb.Length - 1] != ' ') sb.Append(' ');
                prevNl = false;
                continue;
            }
            prevNl = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ 判重

    private static void LoadCurated(string srcRoot)
    {
        // 精选集在项目里的 resources\data\*.json。上游目录在项目外，按相对位置回找。
        string[] cands = new string[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), @"resources\data"),
            Path.Combine(Directory.GetCurrentDirectory(), @"..\resources\data"),
            Path.Combine(srcRoot, @"..\jigu-app\resources\data"),
        };
        foreach (string dir in cands)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                if (Path.GetFileName(f).ToLowerInvariant().IndexOf("version") >= 0) continue;
                CollectOriginals(File.ReadAllText(f, Encoding.UTF8));
            }
            if (Curated.Count > 0) return;
        }
    }

    // 极简扫描：只取 "original" 的字符串值，不引入 JSON 依赖。
    private static void CollectOriginals(string json)
    {
        string key = "\"original\"";
        int i = 0;
        while (true)
        {
            i = json.IndexOf(key, i, StringComparison.Ordinal);
            if (i < 0) break;
            i += key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':' || json[i] == '\t')) i++;
            if (i >= json.Length || json[i] != '"') continue;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
                    char c = json[i];
                    if (c == 'n') sb.Append('\n');
                    else if (c == 't') sb.Append('\t');
                    else if (c == 'u' && i + 4 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        { sb.Append((char)code); i += 4; }
                    }
                    else sb.Append(c);
                }
                else sb.Append(json[i]);
                i++;
            }
            i++;
            if (sb.Length > 0) Curated.Add(sb.ToString());
        }
    }

    // 双向子串判重：新条目与精选条目有一方包含另一方（重叠 >= DedupMin）即视为同一则。
    private static bool IsDuplicate(string text)
    {
        if (Curated.Count == 0) return false;
        string t = text.Length > 120 ? text.Substring(0, 120) : text;
        foreach (string c in Curated)
        {
            if (c.Length < DedupMin) continue;
            string cc = c.Length > 120 ? c.Substring(0, 120) : c;
            if (t.Length >= DedupMin && cc.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
            if (cc.Length >= DedupMin && t.IndexOf(cc, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ 输出

    private const string SourceUrl = "https://github.com/alephpi/24histories-simplified-chinese";

    private static string BuildJson(string book, List<Item> items)
    {
        StringBuilder sb = new StringBuilder(1 << 20);
        sb.Append("{\"book\":").Append(Str(book));
        sb.Append(",\"source_url\":").Append(Str(SourceUrl));
        sb.Append(",\"items\":[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Item it = items[i];
            sb.Append("{\"chapter\":").Append(Str(it.Chapter));
            sb.Append(",\"title\":").Append(Str(it.Title));
            sb.Append(",\"original\":").Append(Str(it.Original));
            sb.Append(",\"translation\":").Append(Str(it.Translation));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string Str(string s)
    {
        if (s == null) return "\"\"";
        StringBuilder sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteManifest(string path, List<string[]> rows)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"note\": \"由 tools/convert-24histories 生成，勿手改\",\n");
        sb.Append("  \"source_url\": ").Append(Str(SourceUrl)).Append(",\n");
        sb.Append("  \"books\": [\n");
        for (int i = 0; i < rows.Count; i++)
        {
            string[] r = rows[i];
            sb.Append("    {\"slug\":").Append(Str(r[0]))
              .Append(", \"book\":").Append(Str(r[1]))
              .Append(", \"dynasty\":").Append(Str(r[2]))
              .Append(", \"docs\":").Append(r[3])
              .Append(", \"chars\":").Append(r[4])
              .Append(", \"bytes\":").Append(r[5])
              .Append(", \"pairing\":").Append(Str(r[6]))
              .Append('}');
            if (i < rows.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static long EstimateDeflated(string dir)
    {
        long raw = 0, comp = 0;
        foreach (string f in Directory.GetFiles(dir, "*.json"))
        {
            byte[] b = File.ReadAllBytes(f);
            raw += b.Length;
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.IO.Compression.DeflateStream ds = new System.IO.Compression.DeflateStream(
                    ms, System.IO.Compression.CompressionMode.Compress, true))
                {
                    ds.Write(b, 0, b.Length);
                }
                comp += ms.Length;
            }
        }
        Console.WriteLine("原始 " + (raw / 1048576.0).ToString("0.0") + " MB");
        return comp;
    }
}
