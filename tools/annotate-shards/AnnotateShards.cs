// FILE: jigu-app/tools/annotate-shards/AnnotateShards.cs
// 把史书分片送去做**构建期标注**，产出 cast/cause/process/significance/themes 五个字段。
//
// 为什么是构建期而不是运行时：程序本身不联网、不带 AI，检索是本机倒排索引。
// 标注是一次性的离线加工，产物冻结进分片 —— 用户机器上不开销、不依赖网络。
//
// 标注的第一价值不是「界面多几行字」，而是**让分片能被检索到**。
// 分片原先只有 chapter/title/original/translation 四字段，一条 themes 都没挂；
// 而 Corpus.IndexDoc 只对已存在的词做同义词桥（LabelTable.Expand），
// 于是「留不住人」这类用户说法永远桥不到任何分片上 —— 分片对同义词桥是隐形的。
// themes 用闭集（labels.json 的 74 个标签名）而不是自由发挥，就是为了保证
// 产出的词必然落在索引里、必然能被桥接。
//
// 编译（与 src 同编，要用到 JsonScan / MiniJson / LabelTable）：
//   csc -main:AnnotateShards -out:tools\annotate-shards\AnnotateShards.exe
//     tools\annotate-shards\AnnotateShards.cs src\*.cs
//     -reference:tests\Microsoft.Web.WebView2.Core.dll
//     -reference:tests\Microsoft.Web.WebView2.WinForms.dll
//
// 用法：
//   AnnotateShards.exe <分片目录> <标注输出目录> [--only 史记,汉书] [--limit N]
//                      [--concurrency N] [--resume] [--estimate]
//                      [--model deepseek-chat] [--endpoint URL] [--api-key K]
//                      [--price-in 2] [--price-out 8] [--cache-discount 0.1]
//
//   --estimate   只算 token 与费用，不发一个请求。先看清楚要花多少钱再决定。
//   --resume     已有的 <slug>.jsonl 结果跳过，随时可断可续。
//   --apply      不做标注，把 <标注输出目录> 的结果回填进分片，写到 <分片目录>.annotated
//
// API key 取 --api-key，其次环境变量 DEEPSEEK_API_KEY / JIGU_ANNOT_KEY。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Jigu;

internal static class AnnotateShards
{
    // ------------------------------------------------------------------ 常量

    /// <summary>
    /// 中文 token 估算比例。DeepSeek 官方口径是「1 个中文字符 ≈ 0.6 token，
    /// 1 个英文字符 ≈ 0.3 token」，这里照用。
    /// **这是估算，不是分词结果** —— 报价前请按官方文档自行核实单价。
    /// </summary>
    private const double TokensPerCjkChar = 0.6;
    private const double TokensPerAsciiChar = 0.3;

    /// <summary>输出长度上限。超出会被服务端截断，导致 JSON 解析失败而不是拿到半条。</summary>
    private const int MaxOutputTokens = 900;

    /// <summary>单条史料的正文上限（字符）。史记 p99 是 872，正常不会触发；
    /// 这条是给「转换器切不动」留下的超长条目兜底，宁可截断也不要一个请求打爆上下文。</summary>
    private const int MaxBodyChars = 4000;

    private sealed class Item
    {
        public string Slug = "";
        public string Book = "";
        public string Chapter = "";
        public string Title = "";
        public int Index;
        public string Original = "";
        public string Translation = "";
        /// <summary>回填写主键：书名 + 章节 + 条目序号。同书同章同序号的条目唯一。</summary>
        public string Key { get { return Book + "\u0001" + Chapter + "\u0001" + Index.ToString(CultureInfo.InvariantCulture); } }
    }

    private sealed class Result
    {
        public string Book = "", Chapter = "";
        public int Index;
        public string[] Cast = new string[0];
        public string Cause = "", Process = "", Decision = "", Outcome = "", Significance = "";
        public string[] Themes = new string[0];
        public string Error = "";
    }

    private static string _endpoint = "https://api.deepseek.com/chat/completions";
    // 模型名以官方文档为准：deepseek-flash（旧名 deepseek-v4-flash 已下线但仍可调用）。
    // 单价取 flash 的**高峰**档，宁可报高不报低；空闲时段正好是半价，
    // 估算里会把两档都印出来。
    private static string _model = "deepseek-flash";
    private static string _apiKey = "";
    private static double _priceIn = 2.0, _priceOut = 8.0, _cacheDiscount = 0.02;
    private static bool _thinking;
    private static int _concurrency = 8;
    private static int _limit = 0;
    private static bool _resume, _estimate, _apply;
    private static List<string> _only = new List<string>();

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        if (args.Length < 2) { Usage(); return 2; }

        string shardDir = args[0], outDir = args[1];
        if (!ParseFlags(args, 2)) return 2;

        if (!Directory.Exists(shardDir))
        {
            Console.WriteLine("FAIL: 找不到分片目录 " + shardDir);
            return 2;
        }
        if (_apply) return Apply(shardDir, outDir);

        List<Item> items = LoadItems(shardDir);
        if (items.Count == 0) { Console.WriteLine("FAIL: 分片目录里没有可标注的条目"); return 2; }

        LabelTable labels = Labels();          // 先加载，让「标签表 : …」打在表头之前
        if (labels.LabelCount == 0)
        {
            Console.WriteLine("FAIL: 读不到标签表，闭集为空会让 themes 全部被丢弃。");
            return 2;
        }

        string prefix = BuildPrefix();
        int prefixTokens = EstimateTokens(prefix);

        Console.WriteLine("=== 稽古 · 分片标注 ===");
        Console.WriteLine("分片目录 : " + shardDir);
        Console.WriteLine("条目     : " + items.Count + " 条（" + _only.Count + " 部书筛选后）");
        Console.WriteLine("共享前缀 : " + prefix.Length + " 字 ≈ " + prefixTokens + " tokens（命中前缀缓存）");

        // 正文长度分布：报价按均值算，但均值会被长尾带偏，所以分位一起给出。
        List<int> lens = new List<int>();
        foreach (Item it in items) lens.Add(it.Original.Length + it.Translation.Length);
        lens.Sort();
        double bodyAvg = 0; foreach (int l in lens) bodyAvg += l; bodyAvg /= lens.Count;
        Console.WriteLine("正文字数 : 均 " + bodyAvg.ToString("F0") + " / 中位 " + lens[lens.Count / 2]
            + " / p90 " + lens[(int)(lens.Count * 0.9)] + " / 最长 " + lens[lens.Count - 1]);
        Console.WriteLine();

        if (_estimate) { PrintEstimate(items, prefixTokens, bodyAvg); return 0; }

        if (_apiKey.Length == 0)
        {
            Console.WriteLine("FAIL: 没有 API key。用 --api-key，或设环境变量 DEEPSEEK_API_KEY。");
            Console.WriteLine("      （想先看要花多少钱，加 --estimate，它不发请求。）");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        return Run(items, prefix, outDir);
    }

    private static void Usage()
    {
        Console.WriteLine("用法: AnnotateShards.exe <分片目录> <标注输出目录> [选项]");
        Console.WriteLine("  --only 史记,汉书   只处理这几部（按分片内的书名匹配）");
        Console.WriteLine("  --limit N          每部最多处理 N 条（先小样本验收用）");
        Console.WriteLine("  --concurrency N    并发数，默认 8");
        Console.WriteLine("  --resume           跳过已有结果，可断可续");
        Console.WriteLine("  --estimate         只算 token 与费用，不发请求");
        Console.WriteLine("  --apply            把标注结果回填进分片（写到 <分片目录>.annotated）");
        Console.WriteLine("  --model NAME       默认 deepseek-chat");
        Console.WriteLine("  --endpoint URL     默认 api.deepseek.com");
        Console.WriteLine("  --price-in / --price-out / --cache-discount   报价用单价（元/百万 token）");
    }

    private static bool ParseFlags(string[] args, int start)
    {
        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            string v = (i + 1 < args.Length) ? args[i + 1] : "";
            switch (a)
            {
                case "--only":
                    foreach (string s in v.Split(','))
                    {
                        string t = s.Trim();
                        if (t.Length > 0) _only.Add(t);
                    }
                    i++; break;
                case "--limit": _limit = Int(v, 0); i++; break;
                case "--concurrency": _concurrency = Math.Max(1, Int(v, 8)); i++; break;
                case "--resume": _resume = true; break;
                case "--thinking": _thinking = true; break;
                case "--estimate": _estimate = true; break;
                case "--apply": _apply = true; break;
                case "--model": if (v.Length > 0) { _model = v; i++; } break;
                case "--endpoint": if (v.Length > 0) { _endpoint = v; i++; } break;
                case "--api-key": if (v.Length > 0) { _apiKey = v; i++; } break;
                case "--price-in": _priceIn = Dbl(v, _priceIn); i++; break;
                case "--price-out": _priceOut = Dbl(v, _priceOut); i++; break;
                case "--cache-discount": _cacheDiscount = Dbl(v, _cacheDiscount); i++; break;
                default:
                    Console.WriteLine("FAIL: 不认识的选项 " + a);
                    Usage();
                    return false;
            }
        }
        if (_apiKey.Length == 0)
        {
            _apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrEmpty(_apiKey)) _apiKey = Environment.GetEnvironmentVariable("JIGU_ANNOT_KEY");
            if (_apiKey == null) _apiKey = "";
        }
        return true;
    }

    private static int Int(string s, int fallback)
    {
        int v;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
    }

    private static double Dbl(string s, double fallback)
    {
        double v;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
    }

    // ------------------------------------------------------------------ 读分片

    private static List<Item> LoadItems(string shardDir)
    {
        List<Item> items = new List<Item>();
        string[] files = Directory.GetFiles(shardDir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string f in files)
        {
            // index.json 是分片清单，不是分片本身（Library 用它列书目）
            if (string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes = File.ReadAllBytes(f);
            string slug = Path.GetFileNameWithoutExtension(f);
            int n = 0;
            List<Item> fileItems = new List<Item>();
            JsonScan.ForEachDocument(bytes, delegate(CorpusDoc doc, long start, long end)
            {
                if (_only.Count > 0 && !_only.Contains(doc.Book)) { n++; return; }
                Item it = new Item();
                it.Slug = slug;
                it.Book = doc.Book;
                it.Chapter = doc.Chapter;
                it.Title = doc.Title;
                it.Index = n;
                it.Original = Truncate(doc.Original, MaxBodyChars);
                it.Translation = Truncate(doc.Translation, MaxBodyChars);
                fileItems.Add(it);
                n++;
            });
            if (_limit > 0 && fileItems.Count > _limit) fileItems.RemoveRange(_limit, fileItems.Count - _limit);
            items.AddRange(fileItems);
        }
        return items;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…（原文过长，已截断）";
    }

    // ------------------------------------------------------------------ 提示词

    /// <summary>
    /// 共享前缀：**逐字节恒定**，放在每次请求的最前面，命中 DeepSeek 的前缀缓存
    /// （缓存命中价约为未命中的 1/10）。所以这里不能出现书名、日期、随机数 ——
    /// 只要前缀里有一个字符随请求变化，整段缓存就作废，费用按全价走。
    /// 变量部分（书名/章节/原文）一律放到 user 消息里，见 BuildBody。
    /// </summary>
    private static string BuildPrefix()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("你是《稽古》的史料标注员。用户会给你一条中国史书的原始记载及其白话译文，你为它产出结构化标注。");
        sb.AppendLine();
        sb.AppendLine("只输出一个 JSON 对象，不要解释、不要 markdown 代码围栏、不要多余文字。");
        sb.AppendLine();
        sb.AppendLine("字段如下：");
        sb.AppendLine("cast        字符串数组。本条里出现的关键人物，每人写成「姓名（身份·角色）」，");
        sb.AppendLine("            身份角色取文中能确定的，例如「主帅」「谋臣」「宦官」「太后」。");
        sb.AppendLine("            只写参与决策或承受后果的人，随从、路人不要写。");
        sb.AppendLine("cause       字符串。这件事的起因，一句话，说清为什么会走到这一步。");
        sb.AppendLine("process     字符串。经过，一到两句，说清当事人做了什么、局面怎么变化的。");
        sb.AppendLine("decision    字符串。一句话概括当事人做出的关键决策。");
        sb.AppendLine("outcome     字符串。直接结局，一句话，说清这件事最后怎么样了。");
        sb.AppendLine("significance 字符串。一到两句，说清这件事对今天的现实困境有什么指导意义。");
        sb.AppendLine("            要具体到「遇到什么情况该怎么做」，不要写「以史为鉴」「值得深思」这类空话。");
        sb.AppendLine("themes      字符串数组，0~3 个。只能从下面的闭集里选，一个字都不能改，不得自创。");
        sb.AppendLine();
        sb.AppendLine("主题词闭集（74 个）：");

        // 闭集直接取自 labels.json —— 只有这些词在索引里存在，
        // 自创的词不会命中任何东西，等于白标。
        List<string> names = Labels().Names();
        StringBuilder line = new StringBuilder("  ");
        for (int i = 0; i < names.Count; i++)
        {
            line.Append(names[i]);
            if (i < names.Count - 1) line.Append('、');
            if (line.Length > 60 || i == names.Count - 1)
            {
                sb.AppendLine(line.ToString());
                line = new StringBuilder("  ");
            }
        }
        sb.AppendLine();
        sb.AppendLine("硬性要求：");
        sb.AppendLine("- 一律用现代汉语，不要用文言。");
        sb.AppendLine("- 概括，不要复述原文。");
        sb.AppendLine("- 拿不准的字段给空字符串或空数组，不要编造。");
        sb.AppendLine("- cast 里没有确切人物时给空数组。");
        sb.AppendLine("- themes 里没有贴切的标签时给空数组，不要硬凑。");
        sb.AppendLine();
        sb.AppendLine("示例一");
        sb.AppendLine("原文：项王乃疑范增与汉有私，稍夺其权。范增大怒……疽发背而死。");
        sb.AppendLine("输出：{\"cast\":[\"项羽（西楚霸王·主帅）\",\"范增（谋臣）\"],"
            + "\"cause\":\"刘邦先入关中却未得封赏，项羽军中议论纷纷，猜忌之心渐起。\","
            + "\"process\":\"项羽听信陈平的离间之言，逐步削夺范增的兵权；范增愤而请辞归乡。\","
            + "\"decision\":\"在敌我胜负未分之际，先对自己唯一的战略谋臣动手。\","
            + "\"outcome\":\"范增离开后病死在归乡途中，项羽此后再无战略层面的谋主。\","
            + "\"significance\":\"团队处于关键期时，最忌讳先动元老。若对老臣有疑虑，"
            + "应当面把疑虑摆到桌上谈清楚，而不是靠削权、冷处理逼人自己走 —— 那既留不住人，"
            + "也让还在的人看清了自己的下场。\","
            + "\"themes\":[\"内部矛盾\",\"猜忌\",\"亲信\"]}");
        sb.AppendLine();
        sb.AppendLine("示例二");
        sb.AppendLine("原文：商君相秦十年，宗室贵戚多怨望者。……后五月而秦孝公卒，太子立，"
            + "发吏捕商君。商君亡……车裂以徇。");
        sb.AppendLine("输出：{\"cast\":[\"商鞅（秦国丞相·变法主持者）\",\"秦孝公（国君·变法的唯一支持者）\","
            + "\"秦惠文王（太子·变法中以法办过的储君）\"],"
            + "\"cause\":\"商鞅变法削弱了宗室贵戚的既得利益，积怨已深，全凭秦孝公一人压着。\","
            + "\"process\":\"秦孝公一死，失去庇护的商鞅立刻被宗室反扑，出逃时因自己订下的"
            + "连坐之法无人敢收留。\","
            + "\"decision\":\"把变法的全部支撑押在国君一人的信任上，而没有为自己留下任何退路或同盟。\","
            + "\"outcome\":\"商鞅被车裂示众，但秦国沿用其法，终成强国。\","
            + "\"significance\":\"推动得罪人的改革时，别把安全感全押在某一位上级的支持上。"
            + "要提前把制度本身立住、把受益方变成同盟，让改革在推动者离开后仍能自我运转 —— "
            + "否则人一走，事就翻。\","
            + "\"themes\":[\"改革受阻\",\"集权\",\"功高震主\"]}");
        return sb.ToString();
    }

    private static string BuildBody(Item it)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("【书名】").AppendLine(it.Book);
        sb.Append("【章节】").AppendLine(it.Chapter);
        if (!string.IsNullOrEmpty(it.Title)) sb.Append("【篇名】").AppendLine(it.Title);
        sb.Append("【原文】").AppendLine(it.Original);
        if (!string.IsNullOrEmpty(it.Translation)) sb.Append("【白话】").AppendLine(it.Translation);
        else sb.AppendLine("【白话】（本条暂无译文，请只依据原文标注）");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 估算

    private static int EstimateTokens(string s)
    {
        double t = 0;
        for (int i = 0; i < s.Length; i++)
            t += (s[i] < 128) ? TokensPerAsciiChar : TokensPerCjkChar;
        return (int)Math.Ceiling(t);
    }

    private static void PrintEstimate(List<Item> items, int prefixTokens, double bodyAvg)
    {
        double bodyTokens = 0;
        foreach (Item it in items) bodyTokens += EstimateTokens(it.Original) + EstimateTokens(it.Translation);
        double avgBody = bodyTokens / items.Count;

        double inFresh = items.Count * avgBody;                       // 只有正文是新的
        double inCached = items.Count * prefixTokens;                 // 前缀每请求重复一次，但按缓存价
        double outTokens = items.Count * 250.0;                       // 输出按 250 token/条估

        double inCost = (inFresh + inCached * _cacheDiscount) / 1e6 * _priceIn;
        double outCost = outTokens / 1e6 * _priceOut;
        double total = inCost + outCost;

        Console.WriteLine("--- 费用估算（模型 " + _model + "，单价 " + _priceIn + " / " + _priceOut
            + " 元每百万 token，缓存命中按 " + (_cacheDiscount * 100).ToString("F0") + "% 计）---");
        Console.WriteLine("每请求输入 : 前缀 " + prefixTokens + " + 正文约 " + avgBody.ToString("F0")
            + " = " + (prefixTokens + avgBody).ToString("F0") + " tokens");
        Console.WriteLine("输入合计   : " + ((inFresh + inCached) / 1e6).ToString("F2") + " M（其中 "
            + (inCached / 1e6).ToString("F2") + " M 走缓存）");
        Console.WriteLine("输出合计   : " + (outTokens / 1e6).ToString("F2") + " M（按每条 250 token 估）");
        Console.WriteLine("思考模式   : " + (_thinking ? "开（思维链按输出计费，费用会明显高于下表）" : "关"));
        Console.WriteLine();
        Console.WriteLine("高峰时段   : ¥" + total.ToString("F2") + "   （" + items.Count + " 条，约 ¥"
            + (items.Count == 0 ? "0" : (total / items.Count * 1000).ToString("F2")) + " / 千条）");
        Console.WriteLine("空闲时段   : ¥" + (total / 2).ToString("F2") + "   （空闲价为高峰的一半）");
        Console.WriteLine();
        Console.WriteLine("单价来源   : DeepSeek 官方定价页（api-docs.deepseek.com）。高峰时段为");
        Console.WriteLine("             北京时间周一至周五 9:00-12:00、14:00-18:00（不含法定节假日），");
        Console.WriteLine("             其余时间含周末全天都是空闲时段 —— 同样的活挑空闲点跑就是半价。");
        Console.WriteLine("             token 数按「1 中文字 ≈ 0.6 token」估算，非真实分词，约 ±20% 偏差。");
        Console.WriteLine();
        Console.WriteLine("不加 --estimate 即开始标注（需要 API key）。建议先 --limit 100 看质量。");
    }

    // ------------------------------------------------------------------ 标注

    private static int Run(List<Item> items, string prefix, string outDir)
    {
        // 按分片分组，一个分片一个 jsonl，写完即落盘 —— 中途断了不用重来。
        Dictionary<string, List<Item>> bySlug = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (Item it in items)
        {
            List<Item> l;
            if (!bySlug.TryGetValue(it.Slug, out l)) { l = new List<Item>(); bySlug[it.Slug] = l; }
            l.Add(it);
        }

        int done = 0, failed = 0, skipped = 0;
        foreach (KeyValuePair<string, List<Item>> kv in bySlug)
        {
            string jsonlPath = Path.Combine(outDir, kv.Key + ".jsonl");
            HashSet<string> have = new HashSet<string>(StringComparer.Ordinal);
            if (_resume && File.Exists(jsonlPath))
            {
                foreach (string ln in File.ReadAllLines(jsonlPath, Encoding.UTF8))
                {
                    Result r = ParseResultLine(ln);
                    if (r != null) have.Add(r.Book + "\u0001" + r.Chapter + "\u0001"
                        + r.Index.ToString(CultureInfo.InvariantCulture));
                }
            }
            StreamWriter w = new StreamWriter(jsonlPath, _resume && File.Exists(jsonlPath), new UTF8Encoding(false));
            try
            {
                List<Item> todo = new List<Item>();
                foreach (Item it in kv.Value)
                {
                    if (have.Contains(it.Key)) { skipped++; continue; }
                    todo.Add(it);
                }
                if (todo.Count == 0) { Console.WriteLine(kv.Key + ": 已全部标注，跳过"); continue; }

                Console.WriteLine(kv.Key + ": " + todo.Count + " 条待标注（并发 " + _concurrency + "）");
                int cursor = -1;
                object gate = new object();
                List<Thread> threads = new List<Thread>();
                int n = Math.Min(_concurrency, todo.Count);
                for (int t = 0; t < n; t++)
                {
                    Thread th = new Thread(delegate()
                    {
                        while (true)
                        {
                            int i;
                            lock (gate) { cursor++; i = cursor; }
                            if (i >= todo.Count) return;
                            Item it = todo[i];
                            Result r = Annotate(it, prefix);
                            lock (gate)
                            {
                                if (r == null || r.Error.Length > 0)
                                {
                                    failed++;
                                    Console.WriteLine("  FAIL " + it.Book + " " + it.Chapter + " #" + it.Index
                                        + "  " + (r == null ? "无响应" : r.Error));
                                }
                                else
                                {
                                    done++;
                                    w.WriteLine(ToLine(r));
                                    w.Flush();   // 立刻落盘：进程被杀也不丢已完成的
                                    if (done % 25 == 0)
                                        Console.WriteLine("  已标注 " + done + "/" + todo.Count);
                                }
                            }
                        }
                    });
                    th.IsBackground = true;
                    threads.Add(th);
                    th.Start();
                }
                foreach (Thread th in threads) th.Join();
            }
            finally { w.Close(); }
        }

        Console.WriteLine();
        Console.WriteLine("完成 : " + done + " 条" + (skipped > 0 ? "，续跑跳过 " + skipped + " 条" : "")
            + (failed > 0 ? "，失败 " + failed + " 条（重跑加 --resume 即可续）" : ""));
        Console.WriteLine("输出 : " + outDir);
        Console.WriteLine("回填 : AnnotateShards.exe " + " <分片目录> " + outDir + " --apply");
        return failed == 0 ? 0 : 1;
    }

    private static Result Annotate(Item it, string prefix)
    {
        string body = BuildBody(it);
        // 前缀与正文分两条消息：前缀恒定 → 命中缓存；正文每次都变。
        //
        // thinking 显式关掉。DeepSeek 的思考模式**默认打开且 effort 为 high**，
        // 思维链按输出计费（高峰 8 元/百万）。这是个照着闭集做结构化抽取的活，
        // 不需要推理链，开着只会让费用和耗时翻几倍 —— 而且 reasoning_content
        // 走 content 之外的字段，解析逻辑也得跟着变。要开会话加 --thinking。
        string payload = "{\"model\":\"" + Json.Escape(_model) + "\",\"stream\":false"
            + ",\"max_tokens\":" + MaxOutputTokens
            + ",\"response_format\":{\"type\":\"json_object\"}"
            + ",\"thinking\":{\"type\":\"" + (_thinking ? "enabled" : "disabled") + "\"}"
            + ",\"messages\":[{\"role\":\"system\",\"content\":\"" + Json.Escape(prefix) + "\"}"
            + ",{\"role\":\"user\",\"content\":\"" + Json.Escape(body) + "\"}]}";

        string err;
        string resp = PostJson(payload, out err);
        if (resp == null) { Result bad = new Result(); bad.Error = err; bad.Book = it.Book; bad.Chapter = it.Chapter; bad.Index = it.Index; return bad; }

        try
        {
            object o = MiniJson.Parse(resp);
            Dictionary<string, object> top = o as Dictionary<string, object>;
            if (top == null) throw new Exception("响应不是 JSON 对象");
            List<object> choices = top.ContainsKey("choices") ? top["choices"] as List<object> : null;
            if (choices == null || choices.Count == 0) throw new Exception("响应没有 choices");
            Dictionary<string, object> c0 = choices[0] as Dictionary<string, object>;
            Dictionary<string, object> msg = c0["message"] as Dictionary<string, object>;
            string content = msg["content"] as string;
            if (string.IsNullOrEmpty(content)) throw new Exception("响应 content 为空");

            object ann = MiniJson.Parse(StripFence(content));
            Dictionary<string, object> a = ann as Dictionary<string, object>;
            if (a == null) throw new Exception("标注不是 JSON 对象");

            Result r = new Result();
            r.Book = it.Book; r.Chapter = it.Chapter; r.Index = it.Index;
            r.Cast = Clean(StrArray(a, "cast"));
            r.Themes = ClosedSet(StrArray(a, "themes"));
            r.Cause = Str(a, "cause");
            r.Process = Str(a, "process");
            r.Decision = Str(a, "decision");
            r.Outcome = Str(a, "outcome");
            r.Significance = Str(a, "significance");
            return r;
        }
        catch (Exception ex)
        {
            Result bad = new Result();
            bad.Book = it.Book; bad.Chapter = it.Chapter; bad.Index = it.Index;
            bad.Error = "解析失败: " + ex.Message;
            return bad;
        }
    }

    /// <summary>
    /// 闭集过滤。模型偶尔会自创主题词，那些词在索引里根本不存在 ——
    /// 放进去不但检索不到，还会让「已标注」的覆盖率虚高。宁缺毋滥，直接丢掉。
    /// </summary>
    private static string[] ClosedSet(string[] got)
    {
        LabelTable labels = Labels();
        List<string> ok = new List<string>();
        foreach (string g in got)
        {
            if (labels.IsLabel(g) && !ok.Contains(g)) ok.Add(g);
        }
        return ok.ToArray();
    }

    private static LabelTable _labels;

    /// <summary>
    /// 标签表只加载一次。优先读仓库里的 resources\labels.json（标注时人在仓库根目录，
    /// 那是最新的一份），没有再退回内嵌资源。逐条调用时每次都重新读盘会很浪费 ——
    /// 每条史料都要过一次闭集过滤。
    /// </summary>
    private static LabelTable Labels()
    {
        if (_labels != null) return _labels;
        string[] candidates = new string[] { "resources", @"..\resources", @"..\..\resources" };
        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, LabelTable.FileName)))
            {
                _labels = LabelTable.Load(dir);
                if (_labels.LabelCount > 0)
                {
                    Console.WriteLine("标签表   : " + Path.GetFullPath(Path.Combine(dir, LabelTable.FileName))
                        + "（" + _labels.LabelCount + " 个标签名）");
                    return _labels;
                }
            }
        }
        _labels = LabelTable.Load(null);
        Console.WriteLine("标签表   : 内嵌资源（" + _labels.LabelCount + " 个标签名）");
        return _labels;
    }

    private static string[] StrArray(Dictionary<string, object> o, string key)
    {
        List<object> l = o.ContainsKey(key) ? o[key] as List<object> : null;
        if (l == null) return new string[0];
        List<string> r = new List<string>();
        foreach (object x in l) if (x is string) r.Add(((string)x).Trim());
        return r.ToArray();
    }

    private static string Str(Dictionary<string, object> o, string key)
    {
        string s = o.ContainsKey(key) ? o[key] as string : null;
        return s == null ? "" : s.Trim();
    }

    private static string[] Clean(string[] a)
    {
        List<string> r = new List<string>();
        foreach (string s in a) if (!string.IsNullOrEmpty(s) && !r.Contains(s)) r.Add(s);
        return r.ToArray();
    }

    /// <summary>模型有时会无视「不要代码围栏」把 JSON 包在 ``` 里，这里剥掉。</summary>
    private static string StripFence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int nl = s.IndexOf('\n');
        if (nl < 0) return s;
        s = s.Substring(nl + 1);
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) s = s.Substring(0, end);
        return s.Trim();
    }

    // ------------------------------------------------------------------ HTTP

    private static string PostJson(string payload, out string err)
    {
        err = "";
        try
        {
            // TLS 1.2 —— Net 的静态构造里做了同一件事（这个 shell 环境走本地 MITM，
            // 只认 1.2+）。这里显式再来一次，免得工具单独编译时漏掉。
            const int Tls12 = 3072;
            try
            {
                if ((int)ServicePointManager.SecurityProtocol != 0)
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)Tls12;
            }
            catch (NotSupportedException) { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 180000;
            req.ReadWriteTimeout = 180000;
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
            try { req.Proxy = WebRequest.DefaultWebProxy; } catch { }

            byte[] body = Encoding.UTF8.GetBytes(payload);
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                return sr.ReadToEnd();
        }
        catch (WebException wex)
        {
            // 服务端的错误正文里有真正的原因（余额不足、key 无效、限流），
            // 只报「请求失败」会让人以为是网络问题。
            string detail = "";
            if (wex.Response != null)
            {
                try
                {
                    using (Stream s = wex.Response.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        detail = sr.ReadToEnd();
                }
                catch { }
            }
            if (detail.Length > 400) detail = detail.Substring(0, 400) + "…";
            err = wex.Status + " " + wex.Message + (detail.Length > 0 ? " | " + detail : "");
            return null;
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    // ------------------------------------------------------------------ 回填

    private static string ToLine(Result r)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"book\":\"").Append(Json.Escape(r.Book)).Append('"');
        sb.Append(",\"chapter\":\"").Append(Json.Escape(r.Chapter)).Append('"');
        sb.Append(",\"index\":").Append(r.Index.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"cast\":").Append(Arr(r.Cast));
        sb.Append(",\"cause\":\"").Append(Json.Escape(r.Cause)).Append('"');
        sb.Append(",\"process\":\"").Append(Json.Escape(r.Process)).Append('"');
        sb.Append(",\"decision\":\"").Append(Json.Escape(r.Decision)).Append('"');
        sb.Append(",\"outcome\":\"").Append(Json.Escape(r.Outcome)).Append('"');
        sb.Append(",\"significance\":\"").Append(Json.Escape(r.Significance)).Append('"');
        sb.Append(",\"themes\":").Append(Arr(r.Themes));
        sb.Append('}');
        return sb.ToString();
    }

    private static string Arr(string[] a)
    {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Json.Escape(a[i])).Append('"');
        }
        return sb.Append(']').ToString();
    }

    private static Result ParseResultLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) return null;
        try
        {
            Dictionary<string, object> o = MiniJson.Parse(line) as Dictionary<string, object>;
            if (o == null) return null;
            Result r = new Result();
            r.Book = Str(o, "book"); r.Chapter = Str(o, "chapter");
            r.Index = Int(MiniJson.Str(o, "index"), -1);
            r.Cast = Clean(StrArray(o, "cast"));
            r.Cause = Str(o, "cause"); r.Process = Str(o, "process");
            r.Decision = Str(o, "decision"); r.Outcome = Str(o, "outcome");
            r.Significance = Str(o, "significance");
            r.Themes = Clean(StrArray(o, "themes"));
            return r;
        }
        catch { return null; }
    }

    /// <summary>
    /// 回填：按 <书名, 章节, 条目序号> 把标注并进分片，写到 &lt;分片目录&gt;.annotated。
    /// **不原地改分片** —— 分片是 build\corpus 下的产物，要能随时重跑转换器重建；
    /// 标注是另一个阶段的结果，混在一起会分不清哪些是转换器的输出。
    /// 回填走 JsonScan 抠出原文再拼，不做「解析成对象树再序列化」，
    /// 免得把转换器写的字段顺序、数字格式在往返中改掉。
    /// </summary>
    private static int Apply(string shardDir, string annotDir)
    {
        string dest = shardDir.TrimEnd('\\', '/') + ".annotated";
        Directory.CreateDirectory(dest);

        Dictionary<string, Dictionary<string, Result>> ann =
            new Dictionary<string, Dictionary<string, Result>>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(annotDir, "*.jsonl"))
        {
            string slug = Path.GetFileNameWithoutExtension(f);
            Dictionary<string, Result> m = new Dictionary<string, Result>(StringComparer.Ordinal);
            foreach (string ln in File.ReadAllLines(f, Encoding.UTF8))
            {
                Result r = ParseResultLine(ln);
                if (r != null) m[r.Book + "\u0001" + r.Chapter + "\u0001"
                    + r.Index.ToString(CultureInfo.InvariantCulture)] = r;
            }
            ann[slug] = m;
        }

        int patched = 0, files = 0;
        foreach (string f in Directory.GetFiles(shardDir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase)) continue;
            string slug = Path.GetFileNameWithoutExtension(f);
            Dictionary<string, Result> m;
            if (!ann.TryGetValue(slug, out m)) { File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true); continue; }

            string text = JsonScan.Decode(File.ReadAllBytes(f));
            StringBuilder outText = new StringBuilder(text.Length + 1024);
            int copied = 0, n = 0, hits = 0;
            // 逐条扫描：把每个 doc 的原始 JSON 片段抄下来，在末尾插入标注字段。
            // 用 JsonScan 给出的 start/end 而不是自己找括号 —— update 的按书合并
            // 也是这么做的，同一套偏移语义。
            List<long[]> spans = new List<long[]>();
            List<Item> metas = new List<Item>();
            JsonScan.ForEachDocument(File.ReadAllBytes(f), delegate(CorpusDoc doc, long start, long end)
            {
                Item it = new Item();
                it.Book = doc.Book; it.Chapter = doc.Chapter; it.Index = n;
                metas.Add(it);
                spans.Add(new long[] { start, end });
                n++;
            });
            for (int i = 0; i < spans.Count; i++)
            {
                long start = spans[i][0], end = spans[i][1];
                if (start > copied) outText.Append(text, copied, (int)(start - copied));
                string frag = text.Substring((int)start, (int)(end - start));
                Result r;
                if (m.TryGetValue(metas[i].Key, out r))
                {
                    hits++;
                    frag = InsertFields(frag, r);
                }
                outText.Append(frag);
                copied = (int)end;
            }
            if (copied < text.Length) outText.Append(text, copied, text.Length - copied);

            File.WriteAllText(Path.Combine(dest, Path.GetFileName(f)), outText.ToString(), new UTF8Encoding(false));
            files++;
            patched += hits;
            Console.WriteLine(Path.GetFileName(f) + ": 回填 " + hits + "/" + n + " 条");
        }
        Console.WriteLine();
        Console.WriteLine("完成 : " + files + " 个分片，共回填 " + patched + " 条");
        Console.WriteLine("输出 : " + dest);
        return 0;
    }

    /// <summary>
    /// 在一条 doc 的 JSON 对象末尾插入标注字段（去掉原尾的 } 再补）。
    ///
    /// 每个字段插入前先看原片段里有没有同名键 —— **不能无脑追加**。
    /// 分片本身可能已经带 themes（转换器写的，或上一次回填留下的），
    /// 追加会写出重复键：JSON.parse 之类的解析器取最后一个，看起来「能用」，
    /// 但我们的 JsonScan.ReadDoc 是边读边覆盖，语义靠「最后写入者胜」这种巧合，
    /// 而且文件里凭空多出一份旧值。同名键一律以本次标注为准，替换掉。
    /// </summary>
    private static string InsertFields(string frag, Result r)
    {
        int close = frag.LastIndexOf('}');
        if (close < 0) return frag;
        string head = frag.Substring(0, close);
        StringBuilder sb = new StringBuilder(frag.Length + 512);
        sb.Append(head);
        // 原有字段与新增字段之间必须有逗号；空对象（{}）则不能加
        bool empty = head.Trim().EndsWith("{", StringComparison.Ordinal);
        if (!empty) sb.Append(',');

        List<string[]> parts = new List<string[]>();
        if (r.Cast.Length > 0) parts.Add(new string[] { "cast", Arr(r.Cast) });
        if (r.Cause.Length > 0) parts.Add(new string[] { "cause", "\"" + Json.Escape(r.Cause) + "\"" });
        if (r.Process.Length > 0) parts.Add(new string[] { "process", "\"" + Json.Escape(r.Process) + "\"" });
        if (r.Decision.Length > 0) parts.Add(new string[] { "decision", "\"" + Json.Escape(r.Decision) + "\"" });
        if (r.Outcome.Length > 0) parts.Add(new string[] { "outcome", "\"" + Json.Escape(r.Outcome) + "\"" });
        if (r.Significance.Length > 0) parts.Add(new string[] { "significance", "\"" + Json.Escape(r.Significance) + "\"" });
        if (r.Themes.Length > 0) parts.Add(new string[] { "themes", Arr(r.Themes) });

        // 先在 head 上就地替换已存在的键（同一份结果回填两次不会长出重复键，
        // 也不会留下上一次的旧值），再把剩下的补到末尾。
        string replaced;
        List<string[]> missing = new List<string[]>();
        foreach (string[] p in parts)
        {
            if (!TryReplace(head, p[0], p[1], out replaced)) missing.Add(p);
            else head = replaced;
        }

        sb = new StringBuilder(head.Length + 512);
        sb.Append(head);
        if (head.Trim().EndsWith("{", StringComparison.Ordinal))
        {
            for (int i = 0; i < missing.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(missing[i][0]).Append("\":").Append(missing[i][1]);
            }
        }
        else
        {
            if (missing.Count == 0) return frag;   // 什么都没变，原样返回
            for (int i = 0; i < missing.Count; i++)
                sb.Append(",\"").Append(missing[i][0]).Append("\":").Append(missing[i][1]);
        }
        return sb.Append(frag.Substring(close)).ToString();
    }

    /// <summary>把 head 里已有键的值换成 jsonValue；键不存在返回 false。</summary>
    private static bool TryReplace(string head, string key, string jsonValue, out string result)
    {
        result = head;
        string needle = "\"" + key + "\"";
        int i = 0;
        while (true)
        {
            i = head.IndexOf(needle, i, StringComparison.Ordinal);
            if (i < 0) return false;
            int j = i + needle.Length;
            while (j < head.Length && (head[j] == ' ' || head[j] == '\t')) j++;
            if (j >= head.Length || head[j] != ':') { i += needle.Length; continue; }
            j++;
            while (j < head.Length && (head[j] == ' ' || head[j] == '\t')) j++;
            int end = SkipJsonValue(head, j);
            if (end < 0) return false;
            result = head.Substring(0, j) + jsonValue + head.Substring(end);
            return true;
        }
    }

    /// <summary>从 i 开始跳过一个完整的 JSON 值，返回它的结束位置（不含）。</summary>
    private static int SkipJsonValue(string s, int i)
    {
        if (i >= s.Length) return -1;
        char c = s[i];
        if (c == '"')
        {
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') return i + 1;
                i++;
            }
            return -1;
        }
        if (c == '{' || c == '[')
        {
            char open = c, shut = (c == '{') ? '}' : ']';
            int depth = 0;
            while (i < s.Length)
            {
                char d = s[i];
                if (d == '"') { i = SkipJsonValue(s, i); if (i < 0) return -1; continue; }
                if (d == open) depth++;
                else if (d == shut) { depth--; if (depth == 0) return i + 1; }
                i++;
            }
            return -1;
        }
        while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
        return i;
    }
}
