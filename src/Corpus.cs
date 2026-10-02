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
        /// <summary>登场人物及其身份角色，形如「项羽（西楚霸王·主帅）」。为空时界面回落 Figures</summary>
        public string[] Cast = new string[0];
        /// <summary>决策的起因</summary>
        public string Cause = "";
        /// <summary>决策的经过</summary>
        public string Process = "";
        /// <summary>对当下现实困境的指导意义</summary>
        public string Significance = "";
        /// <summary>
        /// 构建期判定的分级：上 / 中 / 下。判的是**当事人对那次困境处理得好不好**
        /// （是否达成目的、代价是否可控），不是史事本身重不重要。
        /// 空串是合法值，表示「这条没档位」—— 绝不能拿它去凑「下策」。
        /// </summary>
        public string Verdict = "";
        /// <summary>为什么判成这一档，一句话。运行时只呈现，不参与打分。</summary>
        public string VerdictWhy = "";
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

        /// <summary>
        /// trigger 在语料里的文档频次超过这个数就失去触发资格（可调）。
        /// LabelTable 与查询侧的泛词门槛共用同一个值，别让两处阈值漂移。
        /// </summary>
        internal const int HighDf = 15;

        /// <summary>
        /// 触发词匹配允许在相邻两个命中字之间夹几个字（可调）。
        /// 用户不会照着 labels.json 的写法说话：「有人传话」他写成「有人不断传话」，
        /// 「骨干想走」他写成「骨干已经想走了」。填充词现已由 StopWords.Mask 摘掉，
        /// 这个预算留给摘不干净的那些（「想」这种既非停用词、又只是口气的字）。
        ///
        /// 为什么必须有上限、且**必须有序**：无预算的无序匹配会退化成「字符袋」，
        /// 「团队」这类两字词就会到处命中 —— 那正是当初把它挡在门外的原因。
        /// 上限 2 是保守起点，撤掉它的条件写在 docs 与本次改动的验收里：
        /// 只要用例上出现一处不该有的桥接，就降回 0（只保留摘填充词）。
        /// </summary>
        internal const int TriggerGap = 2;

        /// <summary>
        /// 覆盖重排的入组门槛（可调）：一条史料要至少覆盖到这么多个本次查询的处境，
        /// 才有资格被提前。只覆盖 1 个的不动 —— 否则「蹭上最泛的那个标签」也能被抬到
        /// 前面，那又变成了「撞上一个词就赢」，正是这次要修的毛病。
        ///
        /// **只用于多处境查询。**单处境查询另有门槛，见 SearchSituated 里的 minCov：
        /// 那里「覆盖 1 个」不是蹭标签，而是覆盖到了查询的**全部**处境，意义完全不同。
        /// </summary>
        internal const int MinCoverage = 2;

        /// <summary>
        /// 处境集合的上限（可调）。实测自然值是 1–4 个；上限只为兜底 ——
        /// 一句很长的话可能触发一大片标签，那时「覆盖了几个」就不再是有效信号。
        /// </summary>
        internal const int MaxConcerns = 6;

        /// <summary>
        /// 分词时「一个单字」的额外代价（可调）。单字几乎不会是用户想检索的词，
        /// 它在这里唯一的作用是让每个块都有解 —— 一个切不动的字不该让整块被丢弃，
        /// 那会连带丢掉块里真正的词。代价必须大到「两个单字」永远比不过「一个双字词」。
        /// </summary>
        private const double SingleCharPenalty = 4.0;

        /// <summary>
        /// 分级选取的候选池大小（可调）。池要大，才能找到「本来排名很靠后」的
        /// 那一档的最佳代表；取 3 就退化成「Top3 里凑档位」，等于没改。
        /// 200 这个量级 RunRankDiff 已在生产路径上跑过，成本可接受。
        /// </summary>
        private const int GradedPool = 200;

        /// <summary>
        /// 相关性下限（可调）：候选的有效分须达到池内最高分的这个比例，才允许进档位。
        /// 用相对值而非绝对值 —— idf = ln((n+1)/(df+1))+1 随已加载文档数漂移
        /// （201 条精选 vs 7.8 万条分片，同一个词的 idf 能差一倍），绝对阈值必然失准。
        /// 0.35 是保守起点；定死它需要评测集，见 docs 里的评测说明。
        /// </summary>
        private const double VerdictFloorRatio = 0.35;

        /// <summary>档位的固定展示顺序：上策在最前。按档排，不按分排。</summary>
        private static readonly string[] TierOrder = new string[] { "上", "中", "下" };

        /// <summary>
        /// 语料里是否至少有一条带分级。在 IndexDoc 内累积 —— 三处调用都在
        /// lock(_gate) 内，符合现有「写只在加载期」的约定。为 false 时检索整体
        /// 走旧路径，这是「标注还没跑起来之前完全惰性」的开关。
        /// 不要改成「首次检索时惰性计算」：那会从 ClassicForm 的线程池线程写字段。
        /// </summary>
        private bool _hasVerdicts;

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

        /// <summary>
        /// 标注覆盖率体检。判据是「有任一标注字段」而不是「四个字段齐全」——
        /// 分片是分批标注的，一批可能只补了 themes，用全齐当门槛会把进度报成 0。
        /// themes 与 cast/cause/process/significance 任一非空即算已标注。
        /// </summary>
        public void AnnotationStats(out int annotated, out int withCast, out int withThemes)
        {
            annotated = 0; withCast = 0; withThemes = 0;
            foreach (CorpusDoc d in _docs)
            {
                if (d.Cast.Length > 0) withCast++;
                if (d.Themes.Length > 0) withThemes++;
                if (d.Cast.Length > 0 || d.Themes.Length > 0
                    || d.Cause.Length > 0 || d.Process.Length > 0 || d.Significance.Length > 0)
                    annotated++;
            }
        }

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
            LoadFrom(baseDir, null);
        }

        /// <summary>
        /// 加载精选语料，再按需追加史书分片。分片是「全塞进安装包、按需加载」的那一半：
        /// 只把勾选的几部读进来，没勾的不占运行内存也不占启动时间。
        /// </summary>
        public void LoadFrom(string baseDir, IList<string> shardPaths)
        {
            _labels = LabelTable.Load(baseDir);
            _stop = StopWords.Load(baseDir);
            // 触发词匹配要拿停用词摘填充词（见 LabelTable.MatchingTriggers）。
            // 必须在两张表都加载完之后调：各自 Load 一份会在两边漂移。
            _labels.BindStopWords(_stop);
            string path = Path.Combine(baseDir, DataFileName);
            if (File.Exists(path))
            {
                try
                {
                    LoadFile(path);
                    if (shardPaths != null && shardPaths.Count > 0) AppendFiles(shardPaths);
                    Log.Write("corpus loaded: " + _docs.Count + " docs, " + _index.Count
                        + " terms, index≈" + (_docs.Count == 0 ? 0 : IndexBytes / 1024) + " KB, file=" + path
                        + (shardPaths != null && shardPaths.Count > 0 ? ", shards=" + shardPaths.Count : ""));
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

        /// <summary>追加若干分片文件，不清空已有语料。单份失败只记日志，不影响其余。</summary>
        public void AppendFiles(IList<string> paths)
        {
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                try { AppendFile(p); }
                catch (Exception ex) { Log.Error("shard load failed: " + p, ex); }
            }
        }

        /// <summary>把一份语料 JSON 追加进现有索引；文档号接着已有文档往下排。</summary>
        public void AppendFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            lock (_gate)
            {
                JsonScan.ForEachDocument(bytes, delegate(CorpusDoc doc, long start, long end)
                {
                    doc.No = _docs.Count;
                    _docs.Add(doc);
                    IndexDoc(doc);
                });
            }
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
            _labels.BindStopWords(_stop);   // 同上：摘填充词要用的那一份词表
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
            // 标注字段一并进索引。同义词桥（LabelTable.Expand）只认「索引里已存在的
            // 词」，所以标注不提词就白标 —— 分片此前对同义词桥完全隐形就是这个原因。
            // 身份角色词（「主帅」「谋臣」「宦官」）本身也是有用的检索信号。
            AddTerms(seen, doc.Cause);
            AddTerms(seen, doc.Process);
            AddTerms(seen, doc.Significance);
            // VerdictWhy 也进索引，与其它标注字段一视同仁（见上：标注不提词就白标）。
            // Verdict 本身不进 —— 那是分类不是文本，用户不会拿「上」「中」「下」去搜。
            AddTerms(seen, doc.VerdictWhy);
            foreach (string f in doc.Figures) AddTerms(seen, f);
            foreach (string t in doc.Themes) AddTerms(seen, t);
            foreach (string c in doc.Cast) AddTerms(seen, c);

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

            // 分级开关：整个语料只要有一条带档位，分级选取就接管。写在 IndexDoc 内
            // 是因为它只在加载期被调用（三处调用都在 lock(_gate) 内），符合现有
            // 「写只在加载期」的约定；放到检索路径上去惰性计算会破坏这一点。
            if (doc.Verdict.Length > 0) _hasVerdicts = true;
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

        /// <summary>一条命中理由：用户说的原话，与它被系统理解成的标签名。</summary>
        internal sealed class WhyPair
        {
            public string Said = "";
            public string Label = "";
        }

        internal sealed class SearchHit
        {
            public CorpusDoc Doc;
            public double Score;
            public List<string> Terms = new List<string>();
            /// <summary>
            /// 这条命中携带的理由子集：只有**真正桥到这条文档**的标签才算它的理由，
            /// 不是把整句命中的标签一股脑挂上去。空表示「这条不是靠同义词桥命中的」。
            /// </summary>
            public List<WhyPair> Why = new List<WhyPair>();
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
            // 走 ExpandTraced 而不是 Expand：标签集合完全相同，但额外留住
            // 「是哪句用户原话触发它的」，界面要靠它解释「为什么给你看这条」。
            Dictionary<string, List<string>> trace =
                _labels == null ? null : _labels.ExpandTraced(norm, _index);
            HashSet<string> syn = null;
            // 用户自己打出来的词元。同义桥的折扣只能扣在**桥进来**的词上：
            // 若用户打的词恰好与标签名同名（Q4 的「口碑」就是），旧写法只看
            // 「这个词在不在标签表里」，于是把用户的原话也打了 0.75 折 ——
            // 恰好压低的是查询里最贴题的那个词。BuildWhy 早就区分了这两种情况
            // （见那里「同义反复」的注释），打分这里当初漏了。
            HashSet<string> userSaid = new HashSet<string>(terms, StringComparer.Ordinal);
            if (trace != null)
            {
                syn = new HashSet<string>(trace.Keys, StringComparer.Ordinal);
                foreach (string t in trace.Keys)
                    if (!terms.Contains(t)) terms.Add(t);
            }

            int n = Math.Max(_docs.Count, 1);

            // 这里曾经有一道「2 字泛词门槛」（词元长度 < 3 且 df 超过全库 2% 就丢弃）。
            // 它已被删除，因为它从来没有生效过：实测把它加上去之后，自检输出与改动前
            // 逐字节相同。原因是它瞄错了人群 —— 污染词元的 df 不是「太高」而是「极低」
            // （新产/大投/我加 的 df 都是 1，正因罕见，idf 才高得离谱）。
            // 真正的成因在 Tokenize：见那里对 Viterbi 分词的说明。
            List<string> used = new List<string>(terms.Count);
            foreach (string term in terms)
                if (_index.ContainsKey(term)) used.Add(term);
            Dictionary<int, double> score = new Dictionary<int, double>();
            Dictionary<int, double> best = new Dictionary<int, double>();
            Dictionary<int, List<string>> matched = new Dictionary<int, List<string>>();

            foreach (string term in used)
            {
                List<int> list;
                if (!_index.TryGetValue(term, out list)) continue;
                double idf = Math.Log((n + 1.0) / (list.Count + 1.0)) + 1.0;
                double w = idf * (term.Length >= 3 ? 1.35 : 1.0);
                // 折扣只扣一次：只对「由标签桥加进来的词」打 0.75 折（用户自己打出来的词不扣）。
                // 曾经这里还叠了一层「标签名再打 0.6 折」，结果同一个词被扣成 0.45，
                // 查询里最贴题的词（如「管理失控」）反而不如一个偶发的生僻词值钱 —— 实测到才发现的。
                bool fromSyn = syn != null && syn.Contains(term) && !userSaid.Contains(term);
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

            // 这里曾经有一段「主词元命中不足三条时用 2 字滑窗补足差额」的逻辑，
            // 理由是「产品承诺永远给三条」。那段已经删掉，因为承诺本身被推翻了：
            // 凑出来的第三条与用户的问题无关，比诚实地只给两条更糟 ——
            // 本项目自己的自检（AppMain.RunSearchCases）就是这么写的，CHANGELOG 0.1.0
            // 也向用户承诺过「不拿无关条目凑数」。缺的那条由界面渲染成空档说明。
            // 删掉之后有个副作用是好的：每条结果都是真命中，不再需要「补足项」标记。

            int take = Math.Min(topK, ranked.Count);
            for (int i = 0; i < take; i++)
            {
                SearchHit h = new SearchHit();
                h.Doc = _docs[ranked[i].Key];
                h.Score = ranked[i].Value;
                List<string> mt;
                if (matched.TryGetValue(ranked[i].Key, out mt)) h.Terms = mt;
                h.Why = BuildWhy(h.Terms, trace, syn);
                result.Add(h);
            }
            return result;
        }

        /// <summary>
        /// 这次查询**认出了哪些处境**，按 labels.json 里的顺序（稳定、可复现）。
        /// 处境 = 真正被触发到的标签名。注意这里包含「用户自己打了标签名」那一种 ——
        /// ExpandTraced 保留 said == label 的自匹配，而 BridgeToJson / BuildWhy 才过滤它，
        /// 因为那两处的用途是「解释桥从哪来」，这里是「你这句话在说哪些处境」。
        /// </summary>
        internal List<string> ConcernsOf(string norm)
        {
            List<string> r = new List<string>(4);
            if (_labels == null || string.IsNullOrEmpty(norm)) return r;
            HashSet<string> hit = _labels.Expand(norm, _index);
            if (hit.Count == 0) return r;
            foreach (string name in _labels.Names())
            {
                if (!hit.Contains(name)) continue;
                r.Add(name);
                if (r.Count >= MaxConcerns) break;
            }
            return r;
        }

        /// <summary>
        /// 按**处境覆盖数**重排的一层包装。SearchScored 一行不改 ——
        /// 它是共享的无分级评分路径（RunRankDiff / AnnoCheck / 内置兜底界面都在用）。
        ///
        /// 规则只有一条新排序键：
        ///   覆盖 ≥ minCov 的条目排在最前（覆盖多的在前），
        ///   其余按今天的原样跟在后面。
        /// 覆盖数相同的条目之间，排序键完全回落到今天那套有效分 ——
        /// 一个字节都不变。这是「粗标签负责对得上、细 themes 负责分得开」那条教训的防护：
        /// 把 将帅不和 并成 内部矛盾 曾让 将相和 从 Top3 整个消失，覆盖重排不能重演它。
        ///
        /// 门槛**按处境数分档**（实测决定的，不是拍的）：
        ///   · 多处境（≥2 个）：门槛 MinCoverage = 2。在 3 个处境里只蹭到最泛的那个，
        ///     证据太弱 —— 这正是当初否掉「覆盖 1 个就提前」的理由。
        ///   · 单处境：门槛 1。「覆盖 1 个」在这里不是蹭标签，而是覆盖到了查询的
        ///     **全部**处境，意义完全不同。这一档原先整个退回 SearchScored，
        ///     于是排序全由碰巧命中的泛词决定：C4「核心员工被竞争对手高薪挖走了」
        ///     的实词几乎全不在索引里（员工/竞争/高薪/挖走 均 df=0），
        ///     只剩 核心(df=4)、对手(df=7) 两个词在排序，正解排到了十名开外。
        ///     降为 1 之后 萧何月下追韩信 升到第一。
        /// 一个处境都没有时（concerns 为空）仍然退回 SearchScored ——「覆盖几个」
        /// 那时才真的没有意义。
        /// </summary>
        internal List<SearchHit> SearchSituated(string query, int topK)
        {
            List<string> concerns = ConcernsOf(Normalize(query));
            if (concerns.Count == 0) return SearchScored(query, topK);

            // 池要开到全部命中：一条覆盖 3 个处境的史料完全可能靠纯分数排在很后面，
            // 取个「够大」的常数就等于赌它不会掉出去。命中数本来就被语料规模限死，
            // 全取的开销与 GradedPool 同量级。
            List<SearchHit> pool = SearchScored(query, _docs.Count);

            pool.Sort(delegate(SearchHit a, SearchHit b)
            {
                int ca = Coverage(a.Doc, concerns), cb = Coverage(b.Doc, concerns);
                int mc = concerns.Count >= 2 ? MinCoverage : 1;
                bool qa = ca >= mc, qb = cb >= mc;
                if (qa != qb) return qa ? -1 : 1;
                if (qa && ca != cb) return cb.CompareTo(ca);
                int c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = b.Terms.Count.CompareTo(a.Terms.Count);
                if (c != 0) return c;
                return a.Doc.No.CompareTo(b.Doc.No);
            });

            if (pool.Count > topK) pool.RemoveRange(topK, pool.Count - topK);
            return pool;
        }

        /// <summary>这条史料的 themes 里命中了几个本次查询的处境。</summary>
        private static int Coverage(CorpusDoc d, List<string> concerns)
        {
            if (d == null || d.Themes == null) return 0;
            int n = 0;
            for (int i = 0; i < concerns.Count; i++)
                if (Array.IndexOf(d.Themes, concerns[i]) >= 0) n++;
            return n;
        }

        /// <summary>
        /// 从这条命中的词元里挑出「为什么给你看这条」：某个词元是标签名，就说明
        /// 这条史料是靠挂在它身上的情境标签被找到的。
        ///
        /// 判据是 **syn（标签名集合）**，不是「（同义）」后缀 —— 后缀只标「打了折的
        /// 桥接词」，那是个打分概念。用后缀当出处会漏掉一种真实情况：用户打的是
        /// 「不会用人」，分词把它切成了词元「用人」，而「用人」正好是标签名 ——
        /// 它进了 terms 却没有后缀（用户自己说的话不打折），于是这条最该被解释的
        /// 命中反而给不出理由。实测 TierCheck 的理由链路一节就是这样红的。
        /// </summary>
        private static List<WhyPair> BuildWhy(
            List<string> terms, Dictionary<string, List<string>> trace, HashSet<string> labels)
        {
            List<WhyPair> why = new List<WhyPair>();
            if (trace == null || terms == null || labels == null) return why;
            const string Suffix = "（同义）";
            foreach (string shown in terms)
            {
                string label = shown.EndsWith(Suffix, StringComparison.Ordinal)
                    ? shown.Substring(0, shown.Length - Suffix.Length) : shown;
                if (!labels.Contains(label)) continue;
                List<string> said;
                if (!trace.TryGetValue(label, out said)) continue;
                foreach (string s in said)
                {
                    // 用户直接打出了标签名，那不是「桥」出来的理解，是同义反复
                    if (string.Equals(s, label, StringComparison.Ordinal)) continue;
                    WhyPair p = new WhyPair();
                    p.Said = s;
                    p.Label = label;
                    why.Add(p);
                    if (why.Count >= 4) return why;   // 上限，避免界面爆行
                }
            }
            return why;
        }

        /// <summary>
        /// 上/中/下分级选取：每档取最高分的那条，而不是笼统取分数前三。
        /// 这是 SearchSituated 之上的一层包装，不改 SearchScored 本身 ——
        /// 后者是「按分数排序」的评分对照路径，被自检的排序比对、AnnoCheck
        /// 和内置兜底界面共用，动它会同时改掉四处行为。
        ///
        /// graded 为 false 表示「没走分档」，此时返回的是 SearchSituated 的结果
        /// （单处境查询下与 SearchScored 逐条相同）。语料里一条分级都没有时**必然**
        /// 为 false —— 这是分级功能在标注跑起来之前完全惰性的保证。
        /// </summary>
        internal List<SearchHit> SearchByVerdict(string query, int topK, out bool graded)
        {
            graded = false;
            if (!_hasVerdicts) return SearchSituated(query, topK);

            // 池必须开得比 topK 大：某一档的最佳代表本来可能排在第 40 名，
            // 只取前三就等于「在 Top3 里凑档位」，那和没改一样。
            List<SearchHit> pool = SearchSituated(query, GradedPool);
            double top = 0;
            foreach (SearchHit h in pool) if (h.Score > top) top = h.Score;
            if (top <= 0) return SearchSituated(query, topK);

            double floor = top * VerdictFloorRatio;
            Dictionary<string, SearchHit> byTier =
                new Dictionary<string, SearchHit>(StringComparer.Ordinal);
            foreach (SearchHit h in pool)
            {
                // 不能 break。这里曾经写的是 break，理由是「pool 已按有效分降序，
                // 后面只会更小」—— 那个前提在 SearchSituated 接入覆盖重排之后就没了：
                // 池先按覆盖数排，高分条目完全可能排在低分条目**后面**。
                // 早退会把它们整批切掉，实测表现为单处境查询放宽后 C3 由 3 条缩成 1 条。
                if (h.Score < floor) continue;
                string v = h.Doc == null ? "" : h.Doc.Verdict;
                // 没档位的条目不能占档位；同档只留第一条（即最高分那条）
                if (v.Length == 0 || byTier.ContainsKey(v)) continue;
                byTier[v] = h;
            }
            // 一档都没选出来就退回旧路径 —— 宁可给不出三策，也不能返回空。
            if (byTier.Count == 0) return SearchSituated(query, topK);

            graded = true;
            List<SearchHit> picked = new List<SearchHit>(3);
            foreach (string tier in TierOrder)
            {
                SearchHit h;
                // 缺档就少一条，不拿无关条目顶替（界面会渲染成空档说明）
                if (byTier.TryGetValue(tier, out h)) picked.Add(h);
            }
            return picked;
        }

        /// <summary>
        /// 同义词桥的全貌，形如 [{"said":"各自为政","label":"内部矛盾"}]，供
        /// 「观其解字之法」面板使用。放在 Corpus 上而不是让调用方去够 _labels/_index，
        /// 是为了让这两个字段维持私有 —— 它们只在加载期被写。
        /// </summary>
        public string BridgeToJson(string norm)
        {
            StringBuilder sb = new StringBuilder("[");
            Dictionary<string, List<string>> trace =
                _labels == null ? null : _labels.ExpandTraced(norm, _index);
            if (trace != null)
            {
                bool first = true;
                foreach (KeyValuePair<string, List<string>> kv in trace)
                {
                    foreach (string said in kv.Value)
                    {
                        // 用户直接打出了标签名，那不是桥出来的理解，是同义反复
                        if (string.Equals(said, kv.Key, StringComparison.Ordinal)) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"said\":\"").Append(Json.Escape(said))
                          .Append("\",\"label\":\"").Append(Json.Escape(kv.Key)).Append("\"}");
                    }
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// 分级覆盖率体检。刻意不并进 AnnotationStats —— 那个的判据「有没有任一
        /// 标注字段」已经在 AnnoCheck 里被钉了期望值（ann==2），把分级折进去会
        /// 让那几条断言凭空变红。分级是后加的、进度也独立，分开报更清楚。
        /// </summary>
        public void VerdictStats(out int withVerdict, out int up, out int mid, out int down)
        {
            withVerdict = 0; up = 0; mid = 0; down = 0;
            foreach (CorpusDoc d in _docs)
            {
                if (d.Verdict.Length == 0) continue;
                withVerdict++;
                if (d.Verdict == "上") up++;
                else if (d.Verdict == "中") mid++;
                else if (d.Verdict == "下") down++;
            }
        }

        /// <summary>
        /// 分级取值归一：只认字面的 上 / 中 / 下，其余（「上策」「上等」「优」「"上 "」）
        /// 一律归空串，即「没标注」。
        /// 为什么宁可丢掉也不猜：档位是拿去做三策分栏的键，一个归不进来的取值会造出
        /// 一个永远排不进 TierOrder 的幽灵档位 —— 那条史料就再也进不了任何一栏。
        /// 归空则是安全的：它退回未分级池，与没标注的条目行为完全相同。
        /// </summary>
        public static string NormalizeVerdict(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            // 白名单而不是「取首字」：后者会把「下面」「中学」这类词误判成档位。
            // 只多认一个「上策」写法 —— 标注提示词里要求的是「上/中/下」，
            // 但模型偶尔会带上「策」字，这属于可预期的漂移，顺手容错。
            if (s == "上" || s == "上策") return "上";
            if (s == "中" || s == "中策") return "中";
            if (s == "下" || s == "下策") return "下";
            return "";
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
        /// 查询分词：对每个块做一元语言模型 + Viterbi，取整块代价最小的切法。
        ///
        /// 词表就是索引自己的词表（2~4 字），一个词的代价是 -log(df/N) —— 越常见的词
        /// 越便宜。这替换掉了原来的「从左到右取词表里存在的最长词元」。
        ///
        /// 换掉的原因：最长匹配只看「这个词表里有没有」，而索引对每个块发的是**全部**
        /// 2~4 字滑窗（见 EmitBlock），于是跨词边界的巧合碎片常常比真词长，被优先选中，
        /// 真词反被切碎。实测到的后果是「关联太小」的主要来源：
        ///   · `管理失控` 被切成 `现在管 | 理失控`；
        ///   · `新产品上线后被用户集中投诉` 被切成 `新产 | 后被 | 用户 | 集中`；
        ///   · `骨干员工要离职` 被切成 `骨干 | 员工 | 要离`。
        /// 这些碎片（新产 / 大投 / 我加 …）df 都只有 1~2，正因为罕见，idf 高得离谱 ——
        /// 每个碎片都把一条与问题毫无关系的史料单独顶上榜首（实测 Q4：平津侯主父列传 /
        /// 范雎蔡泽列传 / 任城陈萧王传 三条并列 9.24，靠的只是各自撞上一个 df=1 的碎片）。
        /// Viterbi 会拿常见词去覆盖这些位置，上例分别切回 `现在 / 管理失控`、
        /// `产品 / 加大 / 投入`、`骨干 / 员工 / 离职`。
        ///
        /// 单字也能作为一步（见 SingleCharPenalty），它保证每个块都有解，
        /// 但代价高到两个单字永远比不过一个双字词 —— 单字只是退路，不会被当成词元吐出来。
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
                foreach (string t in Segment(block))
                    if (!outTerms.Contains(t)) outTerms.Add(t);
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
        /// 把一个块切成 2~4 字的词元（单字只用作退路，不吐出来）。
        /// 动态规划：best[i] = 覆盖前 i 个字的最小代价，代价见 Tokenize 的说明。
        /// 词表里没有的候选直接跳过（代价无穷），所以走不通的切法自然不会被选中。
        /// </summary>
        private List<string> Segment(string block)
        {
            List<string> words = new List<string>();
            int len = block.Length;
            double n = Math.Max(_docs.Count, 1) + 2.0;
            // 单字按「最罕见」定价：它只是让路径存在，不是一个候选词。
            double single = -Math.Log(2.0 / n) + SingleCharPenalty;

            double[] best = new double[len + 1];
            int[] from = new int[len + 1];
            for (int i = 1; i <= len; i++) best[i] = double.MaxValue;

            for (int i = 1; i <= len; i++)
            {
                double v = best[i - 1] + single;          // 单字退路，永远可行
                int back = i - 1;
                int maxLen = Math.Min(4, i);
                for (int k = 2; k <= maxLen; k++)
                {
                    string cand = block.Substring(i - k, k);
                    List<int> posting;
                    if (!_index.TryGetValue(cand, out posting)) continue;
                    double c = best[i - k] - Math.Log((posting.Count + 1.0) / n);
                    if (c < v) { v = c; back = i - k; }
                }
                best[i] = v;
                from[i] = back;
            }

            for (int i = len; i > 0; i = from[i])
            {
                int w = i - from[i];
                // 倒退压栈，最后反转即可 —— 这里直接往前插，量小无所谓。
                if (w >= 2) words.Insert(0, block.Substring(from[i], w));
            }
            return words;
        }

        /// <summary>
        /// 2 字滑窗。只在一种情况下用到：分词一个词元都切不出来时的召回兜底。
        /// （它曾经还负责「命中不足三条时补足差额」，那段已删 —— 见 SearchScored 里的说明。）
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
            bool graded;
            List<SearchHit> hits = SearchByVerdict(query, topK, out graded);
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
              .Append(elapsedMs);
            // graded 是前端分流的开关：true 走三策三栏，false 走旧的按分列表。
            // 两者在界面上是同一条渲染路径，差别只在徽章文字与是否出现空档。
            sb.Append(",\"graded\":").Append(graded ? "true" : "false");
            // 已加载语料里有多少条带档位。界面据此如实说明「分级待标注」，
            // 而不是假装软件没有这个功能。0 就是当前的真实状态。
            int withVerdict, cUp, cMid, cDown;
            VerdictStats(out withVerdict, out cUp, out cMid, out cDown);
            sb.Append(",\"verdictDocs\":").Append(withVerdict);
            // 这次查询认出了哪些处境。界面靠它把「你的描述」分组说明、并给每条结果
            // 标出「占了几个处境」—— 排序依据摊开给用户看，正是「意义不明」的解药。
            // 加法：前端忽略未知键，老界面不受影响。
            List<string> concerns = ConcernsOf(Normalize(query));
            sb.Append(",\"concerns\":[");
            for (int i = 0; i < concerns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(concerns[i])).Append('"');
            }
            sb.Append(']');
            // 每条结果覆盖了几个处境，与 concerns 一一对应（同序）。
            sb.Append(",\"coverage\":[");
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Coverage(hits[i].Doc, concerns));
            }
            sb.Append(']');
            if (graded)
            {
                sb.Append(",\"tiers\":[");
                for (int t = 0; t < TierOrder.Length; t++)
                {
                    if (t > 0) sb.Append(',');
                    int at = -1;
                    for (int i = 0; i < hits.Count; i++)
                        if (hits[i].Doc != null && hits[i].Doc.Verdict == TierOrder[t]) { at = i; break; }
                    sb.Append("{\"verdict\":\"").Append(TierOrder[t]).Append("\",\"hit\":").Append(at).Append('}');
                }
                sb.Append(']');
            }
            sb.Append(",\"hits\":[");
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
            sb.Append(",\"cause\":\"").Append(Json.Escape(d.Cause)).Append('"');
            sb.Append(",\"process\":\"").Append(Json.Escape(d.Process)).Append('"');
            sb.Append(",\"significance\":\"").Append(Json.Escape(d.Significance)).Append('"');
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
            sb.Append("],\"cast\":[");
            for (int i = 0; i < d.Cast.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(d.Cast[i])).Append('"');
            }
            sb.Append("],\"terms\":[");
            for (int i = 0; i < hit.Terms.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Json.Escape(hit.Terms[i])).Append('"');
            }
            // 分级与判据。没档位时 verdict 是空串，前端据此不渲染徽章。
            sb.Append("],\"verdict\":\"").Append(Json.Escape(d.Verdict)).Append('"');
            sb.Append(",\"verdictWhy\":\"").Append(Json.Escape(d.VerdictWhy)).Append('"');
            // 「为什么给你看这条」：用户原话 -> 系统把它理解成的标签。
            sb.Append(",\"why\":[");
            for (int i = 0; i < hit.Why.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"said\":\"").Append(Json.Escape(hit.Why[i].Said))
                  .Append("\",\"label\":\"").Append(Json.Escape(hit.Why[i].Label)).Append("\"}");
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
    /// 只作用于**查询侧**，不动索引；打分时也仍然只作用于查询词元。
    /// 触发词匹配（LabelTable）另外用它做「填充词摘除」，见 Mask —— 那一步同样不改索引。
    /// 列表刻意保守：凡可能独立指代一种处境的词（不利 / 不足 / 不如 / 不能用其人…）一律不收。
    /// </summary>
    internal sealed class StopWords
    {
        public const string FileName = "stopwords.json";

        private HashSet<string> _words;
        /// <summary>同一批词，按长度倒序。Mask 必须长词优先，理由见 Mask 的注释。</summary>
        private string[] _ordered;

        public int Count { get { return _words == null ? 0 : _words.Count; } }

        public bool Contains(string term)
        {
            return _words != null && term != null && _words.Contains(term);
        }

        /// <summary>
        /// 把文本里出现过的停用词整段摘掉，返回剩下的「骨架」。
        ///
        /// 为什么需要它：触发词匹配本来要求用户一字不差连着写。实测 Q1
        /// 「我和合伙人互相猜忌，团队里有人不断传话挑拨，骨干已经想走了」里，
        /// 【谗言构陷】的触发词「有人传话」被 「有人**不断**传话」挡死 —— 而「不断」
        /// 正是这张表自己认定「对想找什么相似情境毫无信息」的词。一边认定它无信息，
        /// 一边让它挡死匹配，是这套停用词一直没被用对的地方。两侧都摘掉填充词再比，
        /// 才是它该有的用法。
        ///
        /// **必须长词优先**：词表里既有「有人」也有「不断」，若先删短词，
        /// 「有人不断」这串就不再连续，长词永远删不掉、留下残渣。
        /// </summary>
        public string Mask(string text)
        {
            if (_ordered == null || string.IsNullOrEmpty(text)) return text == null ? "" : text;
            string s = text;
            for (int i = 0; i < _ordered.Length; i++)
            {
                string w = _ordered[i];
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) s = s.Replace(w, "");
            }
            return s;
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
            string[] ordered = new string[sw._words.Count];
            sw._words.CopyTo(ordered);
            Array.Sort(ordered, delegate(string a, string b)
            {
                int d = b.Length - a.Length;
                return d != 0 ? d : string.CompareOrdinal(a, b);
            });
            sw._ordered = ordered;
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

        /// <summary>
        /// 与 _entries 一一对应的「摘掉停用词之后的触发词」。
        /// 只在 BindStopWords 里建一次，不在每次检索时现算 —— 检索路径上
        /// 每个标签每条触发词都要比一次，现算等于把 578 次字符串替换放进热路径。
        /// </summary>
        private List<string[]> _masked;

        /// <summary>
        /// 标签名 -> 准许放行的两字触发词（labels.json 的 short_triggers）。
        /// 两字词不做间隔匹配（那会退化成字符袋），只在整句里字面出现才算命中，
        /// 且必须在这张显式清单里 —— 清单由 tools 的指向性审计产出、人工过目，
        /// 取代了原先「非标签名一律须 ≥3 字」的字数护栏。
        /// </summary>
        private Dictionary<string, HashSet<string>> _shortAllowed;

        private StopWords _stop;

        public int LabelCount { get { return _entries == null ? 0 : _entries.Count; } }

        /// <summary>放行的两字触发词数（体检用，便于看住那 177 个词的放行比例）</summary>
        public int ShortTriggerCount
        {
            get
            {
                if (_shortAllowed == null) return 0;
                int n = 0;
                foreach (KeyValuePair<string, HashSet<string>> kv in _shortAllowed) n += kv.Value.Count;
                return n;
            }
        }

        /// <summary>
        /// 把查询侧的停用词表交给标签表 —— 触发词匹配要拿它摘填充词。
        /// 由 Corpus.LoadFrom 在两张表都加载完之后调一次，保证全程序只有一份词表：
        /// 各自 Load 一份会在两边漂移，而漂移的后果是匹配口径悄悄不一致。
        /// </summary>
        public void BindStopWords(StopWords sw)
        {
            _stop = sw;
            _masked = null;
            if (_entries == null || _stop == null) return;
            _masked = new List<string[]>(_entries.Count);
            foreach (string[] e in _entries)
            {
                string[] m = new string[e.Length];
                for (int i = 0; i < e.Length; i++) m[i] = _stop.Mask(e[i]);
                _masked.Add(m);
            }
        }

        /// <summary>查询句的骨架（摘掉停用词），与 _masked 里的触发词骨架同一套口径。</summary>
        private string MaskNorm(string norm)
        {
            return _stop == null ? norm : _stop.Mask(norm);
        }

        /// <summary>
        /// 第 k 条的触发词骨架。没绑过停用词表（单测直接 Load 的场合）时返回 null，
        /// MatchingTriggers 会退回用触发词原样比较。
        /// </summary>
        private string[] EntryMasked(int k)
        {
            if (_masked == null || k >= _masked.Count) return null;
            return _masked[k];
        }

        private bool ShortAllowed(string label, string trigger)
        {
            HashSet<string> set;
            return _shortAllowed != null && _shortAllowed.TryGetValue(label, out set) && set.Contains(trigger);
        }

        /// <summary>这个词是不是某个标签名（标签是粗层，打分要降权）</summary>
        public bool IsLabel(string term)
        {
            if (_entries == null || string.IsNullOrEmpty(term)) return false;
            foreach (string[] e in _entries)
                if (string.Equals(e[0], term, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 全部标签名，按 labels.json 里的顺序。
        /// 标注工具要拿它当**闭集**喂给模型（只许从这里挑主题词）；放在这里而不是
        /// 在工具里另抄一份，是为了两者不可能对不上 —— 抄一份就会各自漂移，
        /// 而漂移的后果是模型给出的词不在索引里，标注静默失效。
        /// </summary>
        public List<string> Names()
        {
            List<string> r = new List<string>();
            if (_entries == null) return r;
            foreach (string[] e in _entries) r.Add(e[0]);
            return r;
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

                // short_triggers 是 triggers 的**子集**（放行的两字词在 triggers 里原样留着，
                // 只是另外列一遍）。不做成「把两字词从 triggers 里搬走」是有意的：
                // tests/DataCheck.cs 按 triggers 的条数做断言，搬走会连它的计数一起改掉。
                IList shortList = g["short_triggers"] as IList;
                if (shortList != null)
                {
                    HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object item in shortList)
                    {
                        string s = Convert.ToString(item);
                        if (!string.IsNullOrEmpty(s)) set.Add(s);
                    }
                    if (set.Count > 0)
                    {
                        if (t._shortAllowed == null)
                            t._shortAllowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                        t._shortAllowed[name] = set;
                    }
                }
            }
            t._entries = list;
            // 自带一份停用词表。LabelTable 会被独立使用（tests/TierCheck.cs 直接
            // LabelTable.Load(dir) 之后调 Expand），这里不绑的话那条路径就是「不去填充词」
            // 的，跟产品走的那条**静默分叉** —— 正是本文件注释里反复讲的「两套护栏漂移」。
            // Corpus.LoadFrom 稍后会用自己那份 _stop 再 Bind 一次覆盖掉这里，所以产品里
            // 全程序仍然只有一份词表；这里这次加载只为「独立使用」的那条路径兜底。
            t.BindStopWords(StopWords.Load(baseDir));
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

            string masked = MaskNorm(norm);
            for (int k = 0; k < _entries.Count; k++)
            {
                string[] e = _entries[k];
                if (MatchingTriggers(e, EntryMasked(k), norm, masked, index).Count == 0) continue;
                // 方案 B 的关键：并入标签名，而不是 trigger 本身。
                // trigger 常常不在语料里（「留不住人」这类用户说法就是），并进去也匹配不到东西。
                if (index.ContainsKey(e[0])) sink.Add(e[0]);
            }
            return sink;
        }

        /// <summary>
        /// 与 Expand 合并的是同一套标签集合，但保留「是哪句用户原话触发它的」。
        /// 这是界面上「为什么给你看这条」的唯一来源 —— 只并标签名的话，
        /// 那句关键的用户原话在 Expand 返回时就已经丢了。
        /// 返回：标签名 -> 触发它的用户原话列表（已去重，可能多条）。
        /// </summary>
        public Dictionary<string, List<string>> ExpandTraced(
            string norm, Dictionary<string, List<int>> index)
        {
            Dictionary<string, List<string>> sink =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (_entries == null || string.IsNullOrEmpty(norm) || index == null) return sink;

            string masked = MaskNorm(norm);
            for (int k = 0; k < _entries.Count; k++)
            {
                string[] e = _entries[k];
                List<string> hits = MatchingTriggers(e, EntryMasked(k), norm, masked, index);
                if (hits.Count == 0) continue;
                if (!index.ContainsKey(e[0])) continue;
                List<string> list;
                if (!sink.TryGetValue(e[0], out list))
                {
                    list = new List<string>(hits.Count);
                    sink[e[0]] = list;
                }
                foreach (string said in hits) if (!list.Contains(said)) list.Add(said);
            }
            return sink;
        }

        /// <summary>
        /// 命中了哪些 trigger。护栏分工：
        ///   · 索引 0（标签名本身）—— 它是语料里真有的词，按原样精确匹配。不去填充词、
        ///     不容许夹字：标签名是「史料挂的那套词」，桥的两个方向口径必须不一样。
        ///   · 字面两字触发词 —— 只有在 short_triggers 的放行清单里才可能触发，
        ///     清单由 tools/label-audit 按指向性算出（放行清单存在前一律不算）。
        ///   · 长触发词 —— 在**摘掉填充词之后的骨架**上比，容忍夹 TriggerGap 个字；
        ///     骨架自己若已在索引里且 df 超过 HighDf（泛词），退回字面匹配。
        /// Expand 与 ExpandTraced 共用这一个实现，避免两套护栏日后漂移。
        /// </summary>
        private List<string> MatchingTriggers(
            string[] e, string[] me, string norm, string normMasked,
            Dictionary<string, List<int>> index)
        {
            List<string> hits = new List<string>(2);
            for (int i = 0; i < e.Length; i++)
            {
                string m = e[i];
                if (string.IsNullOrEmpty(m)) continue;

                if (i == 0)
                {
                    if (Hit(m, norm)) hits.Add(m);
                    continue;
                }

                // 字面两字触发词。不管骨架：这类词本来就没有填充词可摘。
                if (m.Length < 3)
                {
                    if (!ShortAllowed(e[0], m)) continue;
                    List<int> p1;
                    if (index.TryGetValue(m, out p1) && p1.Count > Corpus.HighDf) continue;
                    if (Hit(m, normMasked)) hits.Add(m);
                    continue;
                }

                string core = me == null ? m : me[i];

                // 骨架太短或太泛，就**退回字面匹配** —— 也就是改造之前的行为。
                // 这一步同时挡住两类险，两类都是实测出来的：
                //   · 骨架不足两字：`打不过`→`打`、`怎么退`→`退`。一字骨架会到处命中，
                //     但直接丢弃又会让这两个触发词**连照原样写都不再命中**（纯退步）。
                //   · 骨架是语料里的泛词：`什么时候动手`→`动手`（df=21）、
                //     `怎么用人`→`用人`（df=26）。护栏挂在原始触发词上时它们 df=0 一路
                //     绿灯，挂在骨架上才挡得住。
                // 退回字面不会漏掉「用户照原样写」的情形，所以整条是纯收紧，不是放宽。
                bool gated = core.Length < 2;
                if (!gated)
                {
                    List<int> p2;
                    if (index.TryGetValue(core, out p2) && p2.Count > Corpus.HighDf) gated = true;
                }
                if (gated)
                {
                    if (Hit(m, norm)) hits.Add(m);
                    continue;
                }

                // 摘掉填充词后跟标签名一字不差 —— 这个「说法」其实只是标签名加了点口气词
                // （「怎么用人」→「用人」）。它不含任何新信息，而且照收会**编造引语**：
                // 用户只打了「用人」，界面却会说「你说到『怎么用人』」。标签名本身由索引 0
                // 那条原样匹配，所以这里跳过不会漏掉任何命中。
                if (string.Equals(core, e[0], StringComparison.Ordinal)) continue;

                // 两字骨架只认字面出现：不给它们间隔预算，否则「不听」这种会到处命中。
                if (core.Length == 2)
                {
                    if (Hit(core, normMasked)) hits.Add(m);
                    continue;
                }
                // 夹字匹配这一支**不做**否定判断：它没有单一命中位置（骨架的字是散开的），
                // 「紧邻」无从谈起。宁可漏压，不可错压。
                if (MatchesWithGap(core, normMasked)) hits.Add(m);
            }
            return hits;
        }

        /// <summary>
        /// 字面命中：出现过就算，除非**每一处**出现都紧贴着一个否定词。
        ///
        /// 否定识别只做这一件事 —— **只压制，不反向加分**。没有反义表，
        /// 凭空给反向标签加分等于编数据。
        ///
        /// 「紧邻」= 否定词在触发词**外面**且直接相邻。两点讲究：
        ///   · 词内的否定不算：「不信任」的 不 在词里，它是触发词的一部分，
        ///     照压会把 C17「他们并不信任对方」的【猜忌】压掉 —— 那是本规则的
        ///     杀弃判据，压了 C17 掉回 0 个处境、直接变红。所以判据挂在
        ///     「否定字符是否在 m 的起止之外」，不是「句子里有没有否定词」。
        ///   · 隔字不压：「不…信任」这种中文歧义太大，只在直接相邻时才敢动手。
        /// </summary>
        private static bool Hit(string m, string hay)
        {
            int at = hay.IndexOf(m, StringComparison.Ordinal);
            if (at < 0) return false;
            while (at >= 0)
            {
                char before = at > 0 ? hay[at - 1] : '\0';
                char after = at + m.Length < hay.Length ? hay[at + m.Length] : '\0';
                if (!IsNegation(before) && !IsNegation(after)) return true;
                at = hay.IndexOf(m, at + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsNegation(char ch)
        {
            return ch == '不' || ch == '没' || ch == '无' || ch == '别' || ch == '勿' || ch == '莫';
        }

        /// <summary>
        /// core 的每个字按顺序出现在 hay 里，相邻两字之间合计最多夹 TriggerGap 个字。
        /// **必须有序**：无序匹配会退化成「字符袋」，那正是当初把两字词全挡在门外的原因。
        /// 累计跳过量即总跨度约束，不必再单独限跨度。
        /// </summary>
        private static bool MatchesWithGap(string core, string hay)
        {
            if (string.IsNullOrEmpty(core) || string.IsNullOrEmpty(hay)) return false;
            int qi = 0, skipped = 0;
            for (int k = 0; k < core.Length; k++)
            {
                int j = hay.IndexOf(core[k], qi);
                if (j < 0) return false;
                if (k > 0) skipped += j - qi;
                qi = j + 1;
            }
            return skipped <= Corpus.TriggerGap;
        }

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
