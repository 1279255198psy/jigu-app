// FILE: jigu-app/tests/ShardCheck.cs
// 二十四史分片自检：
//   1. 结构：条目数、正文字数、空 original、空 translation、段长分布
//   2. 卫生：有没有残留 HTML 标签/实体、有没有非中日韩的乱码字符
//   3. 内存：把选定分片与精选集合并成一个临时 corpus.json，交给**原版 Corpus**载入，
//      读它自己的 IndexBytes —— 这是真实占用，不是按字数外推。
//
// 编译（与 src 同编，Corpus 是 internal）：
//   csc -main:ShardCheck -out:tests\ShardCheck.exe tests\ShardCheck.cs src\*.cs
//   -reference:tests\Microsoft.Web.WebView2.Core.dll
//   -reference:tests\Microsoft.Web.WebView2.WinForms.dll
// 用法：
//   ShardCheck.exe --list                 逐片结构报告
//   ShardCheck.exe --ram 史记,汉书,...     指定书目的真实内存
//   ShardCheck.exe --ram-default          精选 + 前四史（默认加载组合）
//   ShardCheck.exe --ram-all              全量
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jigu;

internal static class ShardCheck
{
    private const string ShardDir = @"build\corpus";
    private const string DataDir = @"resources\data";
    private static readonly string DefaultBooks = "史记,汉书,后汉书,三国志";
    // 转换器的 MaxPairLen 是 900；转换器切不动时（句读太少）会留下超长条目，实测最长 2456 字。
    // 这里留出余量，只当作「又出现了两万字级条目」的回归绊线。
    private const int PairFailLen = 3000;

    private static List<string[]> _manifest = new List<string[]>();
    private static int _problems;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        string root = Directory.GetCurrentDirectory();
        string shardDir = Path.Combine(root, ShardDir);
        if (!Directory.Exists(shardDir)) { Console.WriteLine("FAIL: 找不到 " + shardDir + "，先跑 tools/convert-24histories"); return 1; }

        LoadManifest(Path.Combine(shardDir, "index.json"));
        if (_manifest.Count == 0) { Console.WriteLine("FAIL: index.json 里没有书"); return 1; }

        string cmd = args.Length > 0 ? args[0] : "--list";
        int rc = 0;
        if (cmd == "--list") rc = List(shardDir);
        else if (cmd == "--ram") rc = Ram(shardDir, root, args.Length > 1 ? args[1] : DefaultBooks);
        else if (cmd == "--ram-default") rc = Ram(shardDir, root, DefaultBooks);
        else if (cmd == "--ram-all") rc = Ram(shardDir, root, null);
        else if (cmd == "--library") rc = LibraryCheck(args.Length > 1 ? args[1] : Path.Combine(root, @"dist\稽古"));
        else { Console.WriteLine("未知参数 " + cmd); return 2; }
        return rc;
    }

    // ---------------------------------------------------------------- 清单

    private static void LoadManifest(string path)
    {
        if (!File.Exists(path)) return;
        string json = File.ReadAllText(path, Encoding.UTF8);
        int i = 0;
        while (true)
        {
            i = json.IndexOf("\"slug\"", i, StringComparison.Ordinal);
            if (i < 0) break;
            int end = json.IndexOf('}', i);
            if (end < 0) break;
            string row = json.Substring(i, end - i);
            string slug = Field(row, "slug");
            string book = Field(row, "book");
            string dynasty = Field(row, "dynasty");
            string docs = FieldRaw(row, "docs");
            string bytes = FieldRaw(row, "bytes");
            string pairing = Field(row, "pairing");
            if (slug.Length > 0) _manifest.Add(new string[] { slug, book, dynasty, docs, bytes, pairing });
            i = end;
        }
    }

    private static string Field(string row, string key)
    {
        int i = row.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return "";
        i = row.IndexOf(':', i) + 1;
        while (i < row.Length && row[i] == ' ') i++;
        if (i >= row.Length || row[i] != '"') return "";
        int j = row.IndexOf('"', i + 1);
        return j < 0 ? "" : row.Substring(i + 1, j - i - 1);
    }

    private static string FieldRaw(string row, string key)
    {
        int i = row.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return "0";
        i = row.IndexOf(':', i) + 1;
        StringBuilder sb = new StringBuilder();
        while (i < row.Length && (row[i] == ' ' || char.IsDigit(row[i]))) { if (char.IsDigit(row[i])) sb.Append(row[i]); i++; }
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    // ---------------------------------------------------------------- 结构报告

    private static int List(string shardDir)
    {
        Console.WriteLine("分片结构报告（" + shardDir + "）");
        Console.WriteLine();
        Console.WriteLine("书名       条目数   正文字数  无译文   最短  中位   90分位   最长  最长译文  标签残留 乱码 生僻字  状态");
        Console.WriteLine("-----------------------------------------------------------------------------------------------------");
        long totItems = 0, totChars = 0, totTransChars = 0, totNoTrans = 0, totNav = 0, totOver = 0, maxOrig = 0, maxTrans = 0;
        foreach (string[] m in _manifest)
        {
            string path = Path.Combine(shardDir, m[0] + ".json");
            if (!File.Exists(path)) { Console.WriteLine(m[1].PadRight(9) + " —— 缺文件"); _problems++; continue; }

            List<int> lens = new List<int>();
            List<int> tlens = new List<int>();
            int items = 0, noTrans = 0, html = 0, bad = 0, rare = 0, nav = 0, over = 0;
            long chars = 0, tchars = 0;
            foreach (CorpusDoc d in ReadShard(path))
            {
                items++;
                chars += d.Original.Length;
                tchars += (d.Translation ?? "").Length;
                lens.Add(d.Original.Length);
                if (string.IsNullOrEmpty(d.Translation)) noTrans++;
                else tlens.Add(d.Translation.Length);
                if (HasHtml(d.Original)) html++;
                if (HasBadChar(d.Original)) bad++;
                if (HasRareChar(d.Original)) rare++;
                if (HasNav(d.Original) || HasNav(d.Translation)) nav++;
                if (d.Original.Length + d.Translation.Length > PairFailLen) over++;
            }
            lens.Sort();
            tlens.Sort();
            long p50 = lens.Count > 0 ? lens[lens.Count / 2] : 0;
            long p90 = lens.Count > 0 ? lens[(int)(lens.Count * 0.9)] : 0;
            long mx = lens.Count > 0 ? lens[lens.Count - 1] : 0;
            long mn = lens.Count > 0 ? lens[0] : 0;
            long tm = tlens.Count > 0 ? tlens[tlens.Count - 1] : 0;

            string status = "OK";
            if (items == 0) { status = "空片"; _problems++; }
            else if (html > 0 || bad > 0) { status = "有残留"; _problems++; }
            else if (nav > 0) { status = "带导航 " + nav; _problems++; }
            else if (over > 0) { status = "超长 " + over; _problems++; }
            else if (noTrans == items) status = "仅原文";
            else if (noTrans > 0) status = "部分无译文";

            Console.WriteLine(m[1].PadRight(9)
                + items.ToString().PadLeft(7) + "  "
                + chars.ToString().PadLeft(9) + "  "
                + noTrans.ToString().PadLeft(6) + "  "
                + mn.ToString().PadLeft(5) + " "
                + p50.ToString().PadLeft(5) + " "
                + p90.ToString().PadLeft(6) + " "
                + mx.ToString().PadLeft(7) + " "
                + tm.ToString().PadLeft(6) + "  "
                + html.ToString().PadLeft(8) + " "
                + bad.ToString().PadLeft(4) + " "
                + rare.ToString().PadLeft(6) + "  " + status);

            totItems += items; totChars += chars; totTransChars += tchars; totNoTrans += noTrans;
            totNav += nav; totOver += over;
            if (mx > maxOrig) maxOrig = mx;
            if (tm > maxTrans) maxTrans = tm;
        }
        Console.WriteLine("--------------------------------------------------------------------------------------");
        // 原文与译文分开报，再给合计。FEATURES.md 里「二十四史共多少字」那个数就取自这里，
        // 所以两者都印出来 —— 只印原文的话，照着文档那句「原文 + 白话译文」找不到出处。
        // 译文会缺（无译文 N 条），缺的那部分按 0 计，所以合计是「实际有的字数」，不是满配。
        Console.WriteLine("合计 " + totItems + " 条，原文 " + totChars + " 字 + 译文 " + totTransChars
            + " 字 = " + (totChars + totTransChars) + " 字，其中无译文 " + totNoTrans
            + " 条（" + (totItems == 0 ? 0 : totNoTrans * 100 / totItems) + "%）");
        Console.WriteLine("最长正文 " + maxOrig + " 字，最长译文 " + maxTrans + " 字");
        // Phase 1 漏掉的两条：译文侧此前完全没有上限，实测最长一条 22,919 字（原文只剩 32 字的
        // 表序 + 上游导航），把整章白话摊到了一个条目里；上游导航也跟着正文落进索引，污染 2178 条。
        // 两条都不改评分公式，只保证喂进索引的分布是干净的。
        if (totNav > 0) { Console.WriteLine("FAIL: " + totNav + " 条残留上游导航（上一节/下一节）"); _problems++; }
        if (totOver > 0) { Console.WriteLine("FAIL: " + totOver + " 条正文+译文超过 " + PairFailLen + " 字"); _problems++; }
        Console.WriteLine(_problems == 0 ? "=== 结构检查通过 ===" : "=== 有 " + _problems + " 处问题 ===");
        return _problems == 0 ? 0 : 1;
    }

    // 上游每个页面正文之后挂一对站内导航，形如「上一节：第六章-卷六」。带冒号的才是导航 ——
    // 「求下一节气」这类历志正文里也有「下一节」三字，那不是导航。
    private static bool HasNav(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.IndexOf("上一节：", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("下一节：", StringComparison.Ordinal) >= 0) return true;
        foreach (string line in s.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("上一节：", StringComparison.Ordinal)) return true;
            if (t.StartsWith("下一节：", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // 只认真标签：`<` 后面跟 ASCII 字母或 `/`。上游用 `<召隹>` 这种尖括号记生僻字
    // （该字不在常用字集里，繁体站点的习惯写法），那是正文不是标签，不能当成残留。
    private static bool HasHtml(string s)
    {
        if (s.IndexOf("&amp;", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("&nbsp;", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("&#", StringComparison.Ordinal) >= 0) return true;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '<' || i + 1 >= s.Length) continue;
            char n = s[i + 1];
            if (n == '/') return true;
            if ((n >= 'a' && n <= 'z') || (n >= 'A' && n <= 'Z')) return true;
        }
        return false;
    }

    // 「乱码」只认真正解不出来的东西：U+FFFD、控制字符、落单的代理项。
    // 扩展 B 区及以上的生僻字是成对代理项，属合法汉字，单独计为「生僻字」供参考。
    private static bool HasBadChar(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '�') return true;
            if (c >= 0xD800 && c <= 0xDBFF)
            {
                if (i + 1 < s.Length && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) { i++; continue; }
                return true;                                    // 高代理项后没跟低代理项
            }
            if (c >= 0xDC00 && c <= 0xDFFF) return true;         // 落单的低代理项
            if (c < 0x20 && c != '\n' && c != '\r' && c != '\t') return true;
            if (c == 0x7F) return true;
        }
        return false;
    }

    private static bool HasRareChar(string s)
    {
        foreach (char c in s)
            if (c >= 0xD800 && c <= 0xDFFF) return true;
        return false;
    }

    // ---------------------------------------------------------------- 藏书阁接口
    //
    // 藏书阁界面拿到的就是 Library.ToJson 这一串，勾选变化走 SetLibrarySelection ->
    // RebuildCorpus -> Library.SaveSelection。这里把「清单 -> 勾选 -> 落盘 -> 再读回」
    // 整条链路在临时目录里跑一遍，界面那边出问题就只可能是界面自己的事。
    private static int LibraryCheck(string baseDir)
    {
        Console.WriteLine("藏书阁自检（安装目录 " + baseDir + "）");
        Console.WriteLine();
        if (!Library.IsInstalled(baseDir))
        {
            Console.WriteLine("FAIL: 该目录没有 " + Library.DirName + "\\" + Library.ManifestName);
            return 1;
        }
        int problems = 0;

        List<Library.Book> books = Library.Available(baseDir);
        Console.WriteLine("可选书目 " + books.Count + " 部：");
        foreach (Library.Book b in books)
            Console.WriteLine("  " + b.Slug.PadRight(14) + b.Name.PadRight(6) + b.Dynasty.PadRight(6)
                + b.Docs.ToString().PadLeft(6) + " 条  " + (b.Bytes / 1048576) + " MB  pairing=" + b.Pairing);
        if (books.Count == 0) { Console.WriteLine("FAIL: 清单读不出书目"); return 1; }

        // 清单里每一部的分片文件都必须在
        foreach (string p in Library.ShardPaths(baseDir, SlugsOf(books)))
            if (!File.Exists(p)) { Console.WriteLine("FAIL: 缺文件 " + p); problems++; }

        // ToJson：字段齐全、勾选状态与汇总对得上
        List<string> sel = new List<string>();
        sel.Add(books[0].Slug);
        string json = Library.ToJson(baseDir, sel);
        Console.WriteLine();
        Console.WriteLine("ToJson(" + books[0].Slug + ") 前 160 字：");
        Console.WriteLine("  " + (json.Length <= 160 ? json : json.Substring(0, 160) + " …"));
        int selCount = int.Parse(FieldRaw(json.Substring(json.IndexOf("\"selectedCount\"", StringComparison.Ordinal)), "selectedCount"));
        long selBytes = long.Parse(FieldRaw(json.Substring(json.IndexOf("\"selectedBytes\"", StringComparison.Ordinal)), "selectedBytes"));
        if (selCount != 1) { Console.WriteLine("FAIL: selectedCount=" + selCount + "，应为 1"); problems++; }
        if (selBytes != books[0].Bytes) { Console.WriteLine("FAIL: selectedBytes=" + selBytes + "，应为 " + books[0].Bytes); problems++; }
        if (json.IndexOf("\"selected\":true", StringComparison.Ordinal) < 0)
        { Console.WriteLine("FAIL: ToJson 里没有一部是 selected=true"); problems++; }

        // 勾选落盘再读回；空勾选必须原样读回空数组，不能回落成默认组合
        string tmp = Path.Combine(Path.GetTempPath(), "jigu-libcheck-" + Environment.TickCount);
        Directory.CreateDirectory(tmp);
        try
        {
            Library.SaveSelection(tmp, sel);
            List<string> back = Library.LoadSelection(baseDir, tmp);
            Console.WriteLine();
            Console.WriteLine("落盘再读回 : [" + string.Join(",", back.ToArray()) + "]  （写入 ["
                + string.Join(",", sel.ToArray()) + "]）");
            if (back.Count != 1 || back[0] != sel[0])
            { Console.WriteLine("FAIL: 勾选没能原样读回"); problems++; }

            Library.SaveSelection(tmp, new List<string>());
            List<string> empty = Library.LoadSelection(baseDir, tmp);
            Console.WriteLine("空勾选读回 : " + empty.Count + " 部（必须为 0 —— 用户全不选是合法选择，"
                + "不能悄悄回落成默认组合）");
            if (empty.Count != 0) { Console.WriteLine("FAIL: 空勾选被当成了「没有记录」"); problems++; }

            // 手改坏了 / 写了不存在的 slug：过滤掉，不能让检索出问题
            List<string> bogus = new List<string> { "shiji", "no-such-book", "", null };
            List<string> sane = Library.Sanitize(baseDir, bogus);
            Console.WriteLine("脏数据过滤 : [" + string.Join(",", sane.ToArray()) + "]（输入含不存在的 slug）");
            if (sane.Count > 1 || (sane.Count == 1 && sane[0] != "shiji"))
            { Console.WriteLine("FAIL: Sanitize 没把不存在的 slug 滤掉"); problems++; }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "=== 藏书阁检查通过 ===" : "=== 有 " + problems + " 处问题 ===");
        return problems == 0 ? 0 : 1;
    }

    private static List<string> SlugsOf(List<Library.Book> books)
    {
        List<string> s = new List<string>();
        foreach (Library.Book b in books) s.Add(b.Slug);
        return s;
    }

    // ---------------------------------------------------------------- 真实内存

    private static int Ram(string shardDir, string root, string booksCsv)
    {
        List<string> slugs = new List<string>();
        if (booksCsv == null)
        {
            foreach (string[] m in _manifest) slugs.Add(m[0]);
            Console.WriteLine("组合：全量 " + slugs.Count + " 部");
        }
        else
        {
            HashSet<string> want = new HashSet<string>(StringComparer.Ordinal);
            foreach (string s in booksCsv.Split(',')) want.Add(s.Trim());
            foreach (string[] m in _manifest)
                if (want.Contains(m[1]) || want.Contains(m[0])) slugs.Add(m[0]);
            Console.WriteLine("组合：" + booksCsv + "（命中 " + slugs.Count + " 部）");
        }
        if (slugs.Count == 0) { Console.WriteLine("FAIL: 没有命中任何书"); return 1; }

        // 合并成一份临时 corpus.json：先写精选集，再逐片追加 items 数组的内容。
        string tmp = Path.Combine(Path.GetTempPath(), "jigu-shardcheck-" + Environment.TickCount);
        Directory.CreateDirectory(tmp);
        try
        {
            string merged = Path.Combine(tmp, Corpus.DataFileName);
            using (FileStream fs = new FileStream(merged, FileMode.Create, FileAccess.Write))
            {
                byte[] head = Encoding.UTF8.GetBytes("{\"book\":\"corpus\",\"items\":[");
                fs.Write(head, 0, head.Length);
                bool first = true;
                foreach (string f in CuratedFiles(root))
                {
                    foreach (string items in ItemsArrays(f))
                    { if (!first) fs.WriteByte((byte)','); first = false; Write(fs, items); }
                }
                foreach (string slug in slugs)
                {
                    string p = Path.Combine(shardDir, slug + ".json");
                    if (!File.Exists(p)) continue;
                    foreach (string items in ItemsArrays(p))
                    { if (!first) fs.WriteByte((byte)','); first = false; Write(fs, items); }
                }
                byte[] tail = Encoding.UTF8.GetBytes("]}");
                fs.Write(tail, 0, tail.Length);
            }

            // 同义词表/停用词若有就一起放进去，让测量贴近真实运行环境。
            foreach (string name in new string[] { "labels.json", "stopwords.json" })
            {
                string src = Path.Combine(root, @"resources\" + name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, name), true);
            }

            long fileBytes = new FileInfo(merged).Length;
            Corpus c = new Corpus();
            c.LoadFrom(tmp);
            long idx = c.IndexBytes;

            Console.WriteLine("语料文件      " + (fileBytes / 1048576.0).ToString("0.0") + " MB");
            Console.WriteLine("文档数        " + c.DocCount);
            Console.WriteLine("词表          " + c.TermCount);
            Console.WriteLine("索引运行内存  " + (idx / 1048576.0).ToString("0.0") + " MB   （IndexBytes 实物计数）");
            Console.WriteLine("每字均摊      " + (CharCount(merged) == 0 ? "0" : (idx / CharCount(merged)).ToString("0.0")) + " 字节/字");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void Write(FileStream fs, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        fs.Write(b, 0, b.Length);
    }

    private static IEnumerable<string> CuratedFiles(string root)
    {
        string dir = Path.Combine(root, DataDir);
        List<string> files = new List<string>();
        if (Directory.Exists(dir))
            foreach (string f in Directory.GetFiles(dir, "*.json"))
                if (Path.GetFileName(f).ToLowerInvariant().IndexOf("version") < 0) files.Add(f);
        files.Sort();
        return files;
    }

    private static long CharCount(string mergedPath)
    {
        long n = 0;
        foreach (CorpusDoc d in ReadShard(mergedPath)) n += d.Original.Length + d.Translation.Length;
        return n;
    }

    // 从 {"book":..,"items":[...]} 里抠出 items 数组的内容（不含外层方括号）。
    private static List<string> ItemsArrays(string path)
    {
        List<string> list = new List<string>();
        string text = File.ReadAllText(path, Encoding.UTF8);
        const string key = "\"items\"";
        int i = 0;
        while (true)
        {
            i = text.IndexOf(key, i, StringComparison.Ordinal);
            if (i < 0) break;
            int lb = text.IndexOf('[', i);
            if (lb < 0) break;
            int depth = 0, j = lb;
            bool inStr = false;
            for (; j < text.Length; j++)
            {
                char ch = text[j];
                if (inStr)
                {
                    if (ch == '\\') { j++; continue; }
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '[') depth++;
                else if (ch == ']') { depth--; if (depth == 0) break; }
            }
            if (j >= text.Length) break;
            string inner = text.Substring(lb + 1, j - lb - 1).Trim();
            if (inner.Length > 0) list.Add(inner);
            i = j + 1;
        }
        return list;
    }

    private static IEnumerable<CorpusDoc> ReadShard(string path)
    {
        List<CorpusDoc> docs = new List<CorpusDoc>();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            JsonScan.ForEachDocument(bytes, delegate(CorpusDoc doc, long start, long end) { docs.Add(doc); });
        }
        catch (Exception ex)
        {
            Console.WriteLine("  解析失败 " + Path.GetFileName(path) + ": " + ex.Message);
        }
        return docs;
    }
}
