// FILE: jigu-app/src/Corpus.cs
// 全量史料语料：外置 JSON + 倒排索引 + 本地检索。
//
// 数据来源与内存实况（不要再照着旧注释想象）：
//   1. 语料是 exe 同目录的单一 JSON 文件（DataFileName，默认 corpus.json），
//      不内嵌；找不到时才回落到内置的 seed.json，保证开箱可用。
//   2. JsonScan 逐条解析，但**正文是常驻的** —— 每条 CorpusDoc 都完整持有
//      Original/Translation，全部文档留在 _docs 里。201 条 / 160 KB 的规模下
//      这完全没问题，故不再假装"正文只在解析期间存在"。
//      索引侧只存 词 -> [文档号]。
//   3. 检索 = 规范化 → 分词 → 查倒排 → IDF 加权 → 同义词桥扩展 → Top3。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jigu
{
    /// <summary>语料中的一条文档</summary>
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
        /// <summary>这条史料的决策「利」（人工预先写，运行时只挑选呈现）</summary>
        public string[] Pros = new string[0];
        /// <summary>这条史料的决策「弊」</summary>
        public string[] Cons = new string[0];
    }

    /// <summary>语料库：加载、索引、检索</summary>
    internal sealed class Corpus
    {
        public const string DataFileName = "corpus.json";
        public const string DataVersionFile = "data_version.json";

        /// <summary>
        /// 额外命中词的递减系数（可调）。得分的合成方式是：
        ///     有效分 = 最高权重的命中词 + 该系数 × 其余命中词权重之和
        /// 理由是「命中一个高权重主题词」才是主要证据，额外命中只是递减加分。
        /// 不加这一层时，得分是纯 idf 求和，会出现「撞上三个泛词」压过
        /// 「精准命中一个词」—— 实测 Q3 里 考成法与一条鞭法（管理失控+为政）
        /// 就这么输给了 湘军的组织逻辑（管理失控+团队+指挥）。
        /// 不用「除以命中词数」是因为那会反向惩罚覆盖更全的文档。
        /// </summary>
        private const double ExtraMatchWeight = 0.3;

        /// <summary>由标签桥加进来的词的折扣（可调）。只扣一次，不叠加。</summary>
        private const double LabelDiscount = 0.75;

        private readonly List<CorpusDoc> _docs = new List<CorpusDoc>();
        private readonly Dictionary<string, List<int>> _index =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private LabelTable _labels;
        private StopWords _stop;
        private string _sourcePath = "";
        private readonly object _gate = new object();

        public int DocCount { get { return _docs.Count; } }
        public int TermCount { get { return _index.Count; } }
        public int LabelCount { get { return _labels == null ? 0 : _labels.LabelCount; } }

        /// <summary>标签表体检：trigger 总数 / 其中在语料里存在的个数</summary>
        public void LabelStats(out int triggers, out int inCorpus)
        {
            if (_labels == null) { triggers = 0; inCorpus = 0; return; }
            _labels.CountTriggers(_index, out triggers, out inCorpus);
        }

        /// <summary>该词是否为用户语言标签的粗层词（打分时降权）</summary>
        public bool IsLabelTerm(string term) { return _labels != null && _labels.IsLabel(term); }

        /// <summary>查询侧停用词个数（为 0 说明这份表既不在程序目录也没内嵌）</summary>
        public int StopWordCount { get { return _stop == null ? 0 : _stop.Count; } }

        /// <summary>语料文件的实际路径（内嵌兜底时为空），用于界面显示与排错</summary>
        public string SourcePath { get { return _sourcePath; } }

        /// <summary>粗略的索引内存占用（字节），用于日志观察</summary>
        public long IndexBytes
        {
            get
            {
                long n = 0;
                foreach (KeyValuePair<string, List<int>> kv in _index)
                    n += kv.Key.Length * 2 + 24 + kv.Value.Count * 4 + 32;
                n += _docs.Count * 80L;
                return n;
            }
        }

        public bool IsReady { get { return _docs.Count > 0; } }


        // ---------------------------------------------------------------- 加载

        /// <summary>从 exe 同目录加载语料；不存在则回落到内置种子。同义词表与停用词表一并加载。</summary>
        public void LoadFrom(string baseDir)
        {
            _labels = LabelTable.Load(baseDir);
            _stop = StopWords.Load(baseDir);
            string path = Path.Combine(baseDir, DataFileName);
            if (File.Exists(path))
            {
                try
                {
                    LoadFile(path);
                    Log.Write("corpus loaded: " + _docs.Count + " docs, " + _index.Count
                        + " terms, index≈" + (_docs.Count == 0 ? 0 : IndexBytes / 1024) + " KB, file=" + path);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("corpus load failed, falling back to embedded seed", ex);
                }
            }
            else
            {
                Log.Write("corpus file not found: " + path + " -> using embedded seed");
            }
            LoadEmbedded();
        }

        /// <summary>加载一个语料 JSON 文件</summary>
        public void LoadFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            lock (_gate)
            {
                _docs.Clear();
                _index.Clear();
                _sourcePath = path;
                JsonScan.ForEachDocument(bytes, delegate(CorpusDoc doc, long start, long end)
                {
                    // start/end 只被 Update.cs 的按书合并用来原样抠出 JSON 文本，
                    // 语料侧不需要，正文本来就完整留在 CorpusDoc 里。
                    doc.No = _docs.Count;
                    _docs.Add(doc);
                    IndexDoc(doc);
                });
            }
        }

        /// <summary>内置种子语料（仅在外部文件缺失时使用，保证开箱可用）</summary>
        public void LoadEmbedded()
        {
            byte[] bytes = Assets.Get("seed.json");
            if (bytes == null)
            {
                Log.Write("embedded seed asset missing");
                return;
            }
            // 同义词表与语料互不依赖，各自独立回落（外置文件 → 内嵌资源）
            if (_labels == null) _labels = LabelTable.Load(AppDomain.CurrentDomain.BaseDirectory);
            if (_stop == null) _stop = StopWords.Load(AppDomain.CurrentDomain.BaseDirectory);
            lock (_gate)
            {
                _docs.Clear();
                _index.Clear();
                _sourcePath = "(embedded)";
                JsonScan.ForEachDocument(bytes, delegate(CorpusDoc doc, long start, long end)
                {
                    // start/end 只被 Update.cs 的按书合并用来原样抠出 JSON 文本，
                    // 语料侧不需要，正文本来就完整留在 CorpusDoc 里。
                    doc.No = _docs.Count;
                    _docs.Add(doc);
                    IndexDoc(doc);
                });
            }
            Log.Write("corpus loaded from embedded seed: " + _docs.Count + " docs");
        }

        // ---------------------------------------------------------------- 索引

        /// <summary>把一条文档加进倒排表：只存 词 -> 文档号</summary>
        private void IndexDoc(CorpusDoc doc)
        {
            // 所有字段一视同仁：旧版在调用处传了 4/1/2/3 的字段权重，但 AddTerms 的
            // weight 参数在函数体里从未使用，而且 sink 是 HashSet（去重），
            // 所以字段加权一直是个空操作。这里删掉死参数，不让代码撒谎。
            // 若日后真要字段信号，做法是走同义词桥（查询侧），不是乘性权重 ——
            // 实测乘性权重会把通用词的命中数量优势进一步放大，反而盖掉 IDF 的区分度。
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            AddTerms(seen, doc.Title);
            AddTerms(seen, doc.Original);
            AddTerms(seen, doc.Translation);
            AddTerms(seen, doc.Decision);
            AddTerms(seen, doc.Outcome);
            foreach (string f in doc.Figures) AddTerms(seen, f);
            foreach (string t in doc.Themes) AddTerms(seen, t);

            foreach (string term in seen)
            {
                List<int> list;
                if (!_index.TryGetValue(term, out list))
                {
                    list = new List<int>(4);
                    _index[term] = list;
                }
                list.Add(doc.No);
            }
        }

        /// <summary>把一段文字切成 2~4 字词元（含 2 字滑窗，覆盖最常见的中文词组）</summary>
        private static void AddTerms(HashSet<string> sink, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            StringBuilder buf = new StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                bool wordChar = i < text.Length && IsWordChar(text[i]);
                if (wordChar) { buf.Append(text[i]); continue; }
                if (buf.Length > 0) { EmitBlock(sink, buf.ToString()); buf.Length = 0; }
            }
        }

        /// <summary>
        /// 把一个词块切成 2~4 字词元，全部长度都做完整滑窗。
        ///
        /// 旧版规则是：长块只发 2 字滑窗 + 「每隔 2 字取一个 4 字串」，于是
        ///   · 所有长度 &gt; 4 的块**一个 3 字词元都不产生**；
        ///   · 一半的 4 字词元永远不产生（每隔 2 字，漏掉奇数位起点）。
        /// 后果实测：`现金流` 在全库的 df 只有 1 —— 它只在作为独立主题整串出现时
        /// 才被发出来，其余位置一律漏掉，物理上不可能成为好用的检索词。
        /// 补全滑窗后词表 24,356 → 约 44,000，df 恢复正常。
        /// </summary>
        private static void EmitBlock(HashSet<string> sink, string block)
        {
            if (block.Length < 2) return;
            int maxLen = Math.Min(4, block.Length);
            for (int len = 2; len <= maxLen; len++)
                for (int i = 0; i + len <= block.Length; i++)
                    sink.Add(block.Substring(i, len));
        }

        private static bool IsWordChar(char c)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;   // CJK
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            if (c >= 0x30 && c <= 0x39) return true;       // 0-9
            if (c >= 0x41 && c <= 0x5A) return true;       // A-Z
            if (c >= 0x61 && c <= 0x7A) return true;       // a-z
            return false;
        }

        // ---------------------------------------------------------------- 检索

        /// <summary>
        /// 本地检索：分词 -> 查倒排 -> IDF 加权 -> 取前 k 条（返回完整文档）
        /// 计算全部在 C# 完成，前端只拿结果。
        /// </summary>
        public List<CorpusDoc> Search(string query, int topK)
        {
            List<SearchHit> hits = SearchScored(query, topK);
            List<CorpusDoc> result = new List<CorpusDoc>(hits.Count);
            foreach (SearchHit h in hits) result.Add(h.Doc);
            return result;
        }

        internal sealed class SearchHit
        {
            public CorpusDoc Doc;
            public double Score;
            public List<string> Terms = new List<string>();
        }

        internal List<SearchHit> SearchScored(string query, int topK)
        {
            List<SearchHit> result = new List<SearchHit>();
            if (!IsReady || string.IsNullOrEmpty(query) || query.Trim().Length == 0) return result;
            if (topK <= 0) topK = 3;

            string norm = Normalize(query);
            List<string> terms = Tokenize(norm);
            if (terms.Count == 0) return result;

            // 情境标签桥：把用户语言映射到史料挂着的标签名（详见 LabelTable 的注释）。
            // 标签名打 0.75 折，避免它们压过查询里本来就说对了的原词。
            HashSet<string> syn = _labels == null ? null : _labels.Expand(norm, _index);
            if (syn != null)
            {
                foreach (string t in syn)
                    if (!terms.Contains(t)) terms.Add(t);
            }

            int n = Math.Max(_docs.Count, 1);
            Dictionary<int, double> score = new Dictionary<int, double>();
            Dictionary<int, double> best = new Dictionary<int, double>();
            Dictionary<int, List<string>> matched = new Dictionary<int, List<string>>();

            foreach (string term in terms)
            {
                List<int> list;
                if (!_index.TryGetValue(term, out list)) continue;
                double idf = Math.Log((n + 1.0) / (list.Count + 1.0)) + 1.0;
                double w = idf * (term.Length >= 3 ? 1.35 : 1.0);
                // 折扣只扣一次：只对「由标签桥加进来的词」打 0.75 折（用户自己打出来的词不扣）。
                // 曾经这里还叠了一层「标签名再打 0.6 折」，结果同一个词被扣成 0.45，
                // 查询里最贴题的词（如「管理失控」）反而不如一个偶发的生僻词值钱 —— 实测到才发现的。
                bool fromSyn = syn != null && syn.Contains(term);
                if (fromSyn) w *= LabelDiscount;
                string shown = fromSyn ? term + "（同义）" : term;
                for (int i = 0; i < list.Count; i++)
                {
                    int d = list[i];
                    double cur;
                    score.TryGetValue(d, out cur);
                    score[d] = cur + w;
                    double hi;
                    if (!best.TryGetValue(d, out hi) || w > hi) best[d] = w;
                    List<string> mt;
                    if (!matched.TryGetValue(d, out mt)) { mt = new List<string>(4); matched[d] = mt; }
                    if (!mt.Contains(shown)) mt.Add(shown);
                }
            }

            // 合成有效分：最高权重算满，其余递减（见 ExtraMatchWeight 的注释）
            List<KeyValuePair<int, double>> ranked = new List<KeyValuePair<int, double>>(score.Count);
            foreach (KeyValuePair<int, double> kv in score)
            {
                double hi;
                best.TryGetValue(kv.Key, out hi);
                ranked.Add(new KeyValuePair<int, double>(
                    kv.Key, hi + ExtraMatchWeight * (kv.Value - hi)));
            }
            ranked.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b)
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                // 同分时先看命中词更多的（覆盖更全面），最后才比文档号。
                // 旧版只比文档号：同一组同分文档永远按固定顺序出现，用户会觉得
                // 「怎么每次都是那几条」。Q6 那类只命中一个词元的问题尤其明显。
                List<string> ma, mb;
                matched.TryGetValue(a.Key, out ma);
                matched.TryGetValue(b.Key, out mb);
                int ca = ma == null ? 0 : ma.Count;
                int cb = mb == null ? 0 : mb.Count;
                if (ca != cb) return cb.CompareTo(ca);
                return a.Key.CompareTo(b.Key);
            });

            // 产品承诺「永远给三条」：主词元命中不足三条时，用 2 字滑窗补足差额。
            // 只在缺位时才补，且不打扰已经排好的结果（已命中的文档一律跳过）；
            // 补进来的分数按 0.5 权重算，天然排在后面。
            if (ranked.Count < topK)
            {
                Dictionary<int, double> filler = new Dictionary<int, double>();
                foreach (string term in Bigrams(norm))
                {
                    if (terms.Contains(term)) continue;
                    List<int> list;
                    if (!_index.TryGetValue(term, out list)) continue;
                    double idf = Math.Log((n + 1.0) / (list.Count + 1.0)) + 1.0;
                    double w = idf * 0.5;
                    for (int i = 0; i < list.Count; i++)
                    {
                        int d = list[i];
                        if (score.ContainsKey(d)) continue;
                        double cur;
                        filler.TryGetValue(d, out cur);
                        filler[d] = cur + w;
                        List<string> mt;
                        if (!matched.TryGetValue(d, out mt)) { mt = new List<string>(4); matched[d] = mt; }
                        if (!mt.Contains(term)) mt.Add(term);
                    }
                }
                List<KeyValuePair<int, double>> more = new List<KeyValuePair<int, double>>(filler);
                more.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b)
                {
                    int c2 = b.Value.CompareTo(a.Value);
                    return c2 != 0 ? c2 : a.Key.CompareTo(b.Key);
                });
                ranked.AddRange(more);
            }

            int take = Math.Min(topK, ranked.Count);
            for (int i = 0; i < take; i++)
            {
                SearchHit h = new SearchHit();
                h.Doc = _docs[ranked[i].Key];
                h.Score = ranked[i].Value;
                List<string> mt;
                if (matched.TryGetValue(ranked[i].Key, out mt)) h.Terms = mt;
                result.Add(h);
            }
            return result;
        }

        /// <summary>输入规范化：全角转半角、标点与空白归一</summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            StringBuilder sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                char ch = c;
                if (ch == '\u3000') ch = ' ';
                else if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0);
                sb.Append(IsWordChar(ch) ? ch : ' ');
            }
            string[] parts = sb.ToString().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// 查询分词：从左到右取「词表里存在的最长词元」。
        ///
        /// 命中不了时只前进 1 字、**不吐词元**。旧版在这里无条件吐一个 2 字窗口并前进 2，
        /// 而那个 2 字窗口根本不查词表，于是凭空造出词表里并不存在的词元，并把真词切碎：
        /// 例如「我和合伙人互相猜忌」会在「相」处吐出 `相猜`、跳过相邻的「忌」，导致
        /// `猜忌`（语料里 5 条，含 陈平反间，范增去楚 / 沙丘之变）永远进不了词元表 ——
        /// 正确答案就在库里，却从未被检索。
        ///
        /// 若整句一个词元都切不出来（如「合伙人翻脸了」），回落到 2 字滑窗：
        /// 产品承诺是「永远给三条」，空结果不可接受。
        /// </summary>
        public List<string> Tokenize(string norm)
        {
            List<string> outTerms = new List<string>(12);
            foreach (string block in norm.Split(' '))
            {
                if (block.Length < 2) continue;
                int i = 0;
                while (i < block.Length)
                {
                    int picked = 0;
                    int maxLen = Math.Min(4, block.Length - i);
                    for (int len = maxLen; len >= 2; len--)
                    {
                        string cand = block.Substring(i, len);
                        if (_index.ContainsKey(cand)) { picked = len; break; }
                    }
                    if (picked == 0) { i++; continue; }
                    string t = block.Substring(i, picked);
                    if (!outTerms.Contains(t)) outTerms.Add(t);
                    i += picked;
                }
            }

            // 丢弃查询侧的现代虚词（见 StopWords 的注释：它们 idf 高但无信息，会压过关键词）。
            // 放在这里、放在下面的兜底之前 —— 若整句都是虚词，兜底仍能给出 2 字滑窗，
            // 不至于变成空结果、破坏「永远给三条」。
            if (_stop != null && outTerms.Count > 0)
            {
                List<string> kept = new List<string>(outTerms.Count);
                for (int i = 0; i < outTerms.Count; i++)
                    if (!_stop.Contains(outTerms[i])) kept.Add(outTerms[i]);
                outTerms = kept;
            }

            if (outTerms.Count == 0)
            {
                foreach (string two in Bigrams(norm))
                {
                    if (outTerms.Count >= 24) break;
                    if (!outTerms.Contains(two)) outTerms.Add(two);
                }
            }

            if (outTerms.Count > 24) outTerms.RemoveRange(24, outTerms.Count - 24);
            return outTerms;
        }

        /// <summary>
        /// 2 字滑窗。两处用到：词元一个都切不出来时的召回兜底，
        /// 以及主词元命中不足三条时补足差额。
        /// </summary>
        private static IEnumerable<string> Bigrams(string norm)
        {
            foreach (string block in norm.Split(' '))
                for (int i = 0; i + 2 <= block.Length; i++)
                    yield return block.Substring(i, 2);
        }

        /// <summary>把结果整理成前端需要的 JSON（含原文/译文/人物/决策/结果）</summary>
        public string SearchToJson(string query, int topK, out string termsJson, out long elapsedMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            List<SearchHit> hits = SearchScored(query, topK);
            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;

            StringBuilder terms = new StringBuilder("[");
            List<string> allTerms = Tokenize(Normalize(query));
            for (int i = 0; i < allTerms.Count; i++)
            {
                if (i > 0) terms.Append(',');
                terms.Append('"').Append(Json.Escape(allTerms[i])).Append('"');
            }
            terms.Append(']');
            termsJson = terms.ToString();

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"query\":\"").Append(Json.Escape(query)).Append("\",\"tookMs\":")
              .Append(elapsedMs).Append(",\"hits\":[");
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendDoc(sb, hits[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private void AppendDoc(StringBuilder sb, SearchHit hit)
        {
            CorpusDoc d = hit.Doc;
            sb.Append("{\"no\":").Append(d.No);
            sb.Append(",\"book\":\"").Append(Json.Escape(d.Book)).Append('"');
            sb.Append(",\"chapter\":\"").Append(Json.Escape(d.Chapter)).Append('"');
            sb.Append(",\"title\":\"").Append(Json.Escape(d.Title)).Append('"');
            sb.Append(",\"original\":\"").Append(Json.Escape(d.Original)).Append('"');
            sb.Append(",\"translation\":\"").Append(Json.Escape(d.Translation)).Append('"');
            sb.Append(",\"decision\":\"").Append(Json.Escape(d.Decision)).Append('"');
            sb.Append(",\"outcome\":\"").Append(Json.Escape(d.Outcome)).Append('"');
            sb.Append(",\"score\":").Append(hit.Score.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"figures\":[");
            for (int i = 0; i < d.Figures.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(d.Figures[i])).Append('"');
            }
            sb.Append("],\"themes\":[");
            for (int i = 0; i < d.Themes.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(d.Themes[i])).Append('"');
            }
            sb.Append("],\"pros\":[");
            for (int i = 0; i < d.Pros.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(d.Pros[i])).Append('"');
            }
            sb.Append("],\"cons\":[");
            for (int i = 0; i < d.Cons.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(d.Cons[i])).Append('"');
            }
            sb.Append("],\"terms\":[");
            for (int i = 0; i < hit.Terms.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(hit.Terms[i])).Append('"');
            }
            sb.Append("]}");
        }

        /// <summary>语料统计（藏书阁用），只吐少量数字</summary>
        public string StatsToJson()
        {
            Dictionary<string, int> byBook = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CorpusDoc d in _docs)
            {
                int c;
                byBook.TryGetValue(d.Book, out c);
                byBook[d.Book] = c + 1;
            }
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"docs\":").Append(_docs.Count)
              .Append(",\"terms\":").Append(_index.Count)
              .Append(",\"indexKB\":").Append(IndexBytes / 1024)
              .Append(",\"source\":\"").Append(Json.Escape(_sourcePath)).Append('"')
              .Append(",\"books\":{");
            bool first = true;
            foreach (KeyValuePair<string, int> kv in byBook)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Json.Escape(kv.Key)).Append("\":").Append(kv.Value);
            }
            sb.Append("}}");
            return sb.ToString();
        }

    }

    /// <summary>
    /// 数据文件的统一取法：**外置优先，内嵌兜底**。
    /// 外置是为了不发版就能扩充（这两份表都是靠人往里加词来变强的），
    /// 内嵌是为了文件丢了也不至于功能消失。
    /// </summary>
    internal static class DataFiles
    {
        public static IDictionary Load(string fileName, string baseDir)
        {
            byte[] raw = null;
            string path = string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, fileName);
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    raw = File.ReadAllBytes(path);
                    Log.Write("data file " + fileName + " <- " + path);
                }
                catch (Exception ex) { Log.Error("read data file " + fileName, ex); }
            }
            if (raw == null)
            {
                raw = Assets.Get(fileName);
                if (raw != null) Log.Write("data file " + fileName + " <- embedded");
            }
            if (raw == null)
            {
                Log.Write("data file " + fileName + ": neither external nor embedded");
                return null;
            }
            try { return MiniJson.Parse(Encoding.UTF8.GetString(raw)) as IDictionary; }
            catch (Exception ex)
            {
                Log.Error("parse data file " + fileName, ex);
                return null;
            }
        }
    }

    /// <summary>
    /// 查询侧停用词表。
    ///
    /// 存在的理由是一个实测出来的失效：这个排序本质是「命中词的 IDF 之和」，而 IDF 奖励的是
    /// **在语料里稀有**。于是「互相 / 不断 / 已经 / 有人」这类现代汉语虚词 —— 在文言语料里
    /// df 只有 2~4，于是 idf 高到 4.4~4.9 —— 会把真正切题的「猜忌」（df=5, idf=4.50）挤出去。
    /// 实测「我和合伙人互相猜忌…」一条，Top3 全被 不断/互相/团队 占住，
    /// 库里 5 条含「猜忌」的史料（陈平反间、沙丘之变、自毁长城…）一条没进。
    /// 这不是调参能修的：公式给「不断」高分是它的正确行为，错的是把无信息的词当成有信息的词。
    ///
    /// 只作用于**查询侧**，不动索引，也不动同义词桥扩展出来的词。
    /// 列表刻意保守：凡可能独立指代一种处境的词（不利 / 不足 / 不如 / 不能用其人…）一律不收。
    /// </summary>
    internal sealed class StopWords
    {
        public const string FileName = "stopwords.json";

        private HashSet<string> _words;

        public int Count { get { return _words == null ? 0 : _words.Count; } }

        public bool Contains(string term)
        {
            return _words != null && term != null && _words.Contains(term);
        }

        public static StopWords Load(string baseDir)
        {
            StopWords sw = new StopWords();
            IDictionary root = DataFiles.Load(FileName, baseDir);
            if (root == null) return sw;
            IList list = root["words"] as IList;
            if (list == null) return sw;
            sw._words = new HashSet<string>(StringComparer.Ordinal);
            foreach (object o in list)
            {
                string w = Convert.ToString(o);
                if (!string.IsNullOrEmpty(w)) sw._words.Add(w);
            }
            Log.Write("stopwords loaded: " + sw._words.Count + " words");
            return sw;
        }
    }

    /// <summary>
    /// 情境标签表（方案 B）。
    ///
    /// 为什么需要它：字段检索的排序是「命中词的 IDF 之和」，而**用户的语言**和**史料标注的语言**
    /// 是两套 —— 用户写「合伙人反目 / 留不住人」，史料标的是「内部倾轧 / 谋臣出走」。
    /// 只靠字面匹配，两边永远对不上。这张表就是两边的对照，且**每个标签名 ≤4 字、直接进索引**：
    /// 史料挂标签名，用户说任何一种说法都能触发到同一个标签名。
    ///
    /// 机制：对归一化后的**整句**做子串扫描（不是对分词结果查表 —— 组键本身就是用户语言，
    /// 分词器永远不会把它们切成一个词元，词元级查表基本不触发）；命中任一 trigger 就把
    /// **标签名**并入查询。trigger 只是找路的说法，可以根本不在语料里；标签名才是史料真正挂着的词。
    ///
    /// 沿用旧同义词表的两条护栏（都是实测出来的）：
    ///   1. 非标签名的 trigger 要够长（≥3 字）才算数；标签名本身不受限，它就是用户语言。
    ///   2. 语料里本来就高频的 trigger 不算数（df 阈值）—— 它已被语料覆盖，拿它触发只会灌查询。
    ///
    /// 加载顺序照抄 Corpus.LoadFrom：先看 exe 同目录的外置 labels.json，没有才用内嵌的那份。
    /// </summary>
    internal sealed class LabelTable
    {
        public const string FileName = "labels.json";

        /// <summary>每条 = [标签名, trigger...]；标签名固定在索引 0</summary>
        private List<string[]> _entries;

        public int LabelCount { get { return _entries == null ? 0 : _entries.Count; } }

        /// <summary>这个词是不是某个标签名（标签是粗层，打分要降权）</summary>
        public bool IsLabel(string term)
        {
            if (_entries == null || string.IsNullOrEmpty(term)) return false;
            foreach (string[] e in _entries)
                if (string.Equals(e[0], term, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>trigger 总数（体检用）</summary>
        public int TriggerCount
        {
            get
            {
                if (_entries == null) return 0;
                int n = 0;
                foreach (string[] e in _entries) n += Math.Max(0, e.Length - 1);
                return n;
            }
        }

        public static LabelTable Load(string baseDir)
        {
            LabelTable t = new LabelTable();
            IDictionary root = DataFiles.Load(FileName, baseDir);
            if (root == null) return t;
            IList labels = root["labels"] as IList;
            if (labels == null) return t;
            List<string[]> list = new List<string[]>(labels.Count);
            foreach (object o in labels)
            {
                IDictionary g = o as IDictionary;
                if (g == null) continue;
                string name = Convert.ToString(g["label"]);
                if (string.IsNullOrEmpty(name)) continue;
                List<string> one = new List<string>(16);
                one.Add(name);
                IList trig = g["triggers"] as IList;
                if (trig != null)
                {
                    foreach (object item in trig)
                    {
                        string s = Convert.ToString(item);
                        if (!string.IsNullOrEmpty(s) && !one.Contains(s)) one.Add(s);
                    }
                }
                list.Add(one.ToArray());
            }
            t._entries = list;
            Log.Write("labels loaded: " + t.LabelCount + " labels, " + t.TriggerCount + " triggers");
            return t;
        }

        /// <summary>
        /// 对归一化整句做子串扫描，返回应并入查询的**标签名**。
        /// </summary>
        public HashSet<string> Expand(string norm, Dictionary<string, List<int>> index)
        {
            HashSet<string> sink = new HashSet<string>(StringComparer.Ordinal);
            if (_entries == null || string.IsNullOrEmpty(norm) || index == null) return sink;

            foreach (string[] e in _entries)
            {
                if (!Triggers(e, norm, index)) continue;
                // 方案 B 的关键：并入标签名，而不是 trigger 本身。
                // trigger 常常不在语料里（「留不住人」这类用户说法就是），并进去也匹配不到东西。
                if (index.ContainsKey(e[0])) sink.Add(e[0]);
            }
            return sink;
        }

        /// <summary>整句里字面出现任一 trigger（或标签名本身）即触发该标签。</summary>
        private static bool Triggers(string[] e, string norm, Dictionary<string, List<int>> index)
        {
            for (int i = 0; i < e.Length; i++)
            {
                string m = e[i];
                if (string.IsNullOrEmpty(m)) continue;
                if (i != 0)
                {
                    if (m.Length < 3) continue;
                    List<int> posting;
                    if (index.TryGetValue(m, out posting) && posting.Count > HighDf) continue;
                }
                if (norm.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>trigger 在语料里的文档频次超过这个数就不让它当触发器（可调）</summary>
        private const int HighDf = 15;

        /// <summary>
        /// 体检：trigger 总数，以及其中有多少是语料里真实存在的词。
        /// 方案 B 下 trigger 不在语料里是正常的（它就是用户说法），所以这不再是「死条目」，
        /// 只是提示「这条说法目前没有对应的史料词汇」。
        /// </summary>
        public void CountTriggers(Dictionary<string, List<int>> index, out int total, out int inCorpus)
        {
            total = 0;
            inCorpus = 0;
            if (_entries == null) return;
            foreach (string[] e in _entries)
            {
                for (int i = 1; i < e.Length; i++)
                {
                    if (string.IsNullOrEmpty(e[i])) continue;
                    total++;
                    if (index != null && index.ContainsKey(e[i])) inCorpus++;
                }
            }
        }
    }
}
