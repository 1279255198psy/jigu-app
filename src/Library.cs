// FILE: jigu-app/src/Library.cs
// 藏书阁：二十四史分片的发现、勾选与持久化。
//
// 「装了什么」与「加载什么」是两件事：
//   · 分片随安装包全部装进去（离线可用，不依赖任何网络），落在 <安装目录>\corpus\ 下，
//     一部史书一个 JSON，外加一份 index.json 清单。
//   · 只把勾选的几部读进内存。索引是常驻的，实测全量二十四史要约 1.4 GB ——
//     默认全开不现实，所以默认只开精选 201 则 + 史记（约 100 MB）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jigu
{
    internal static class Library
    {
        public const string DirName = "corpus";
        public const string ManifestName = "index.json";
        public const string SelectionName = "library.json";

        /// <summary>
        /// 装好之后的默认组合。史记覆盖先秦到汉武、题材最杂，实测索引约 100 MB。
        /// 其余各史在藏书阁里勾选后按需载入（约 2–8 秒）。
        /// </summary>
        public static readonly string[] DefaultSelection = new string[] { "shiji" };

        /// <summary>可选书目的一项，对应 index.json 里的一条</summary>
        internal sealed class Book
        {
            public string Slug = "";
            public string Name = "";
            public string Dynasty = "";
            public string Pairing = "";
            public int Docs;
            public long Bytes;
        }

        public static string ShardDir(string baseDir)
        {
            return string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, DirName);
        }

        /// <summary>这一份安装里带没带分片（便携版或旧版本可能没有）</summary>
        public static bool IsInstalled(string baseDir)
        {
            string d = ShardDir(baseDir);
            return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, ManifestName));
        }

        /// <summary>读取随包分发的清单，得到可选书目</summary>
        public static List<Book> Available(string baseDir)
        {
            List<Book> list = new List<Book>();
            string d = ShardDir(baseDir);
            if (string.IsNullOrEmpty(d)) return list;
            string path = Path.Combine(d, ManifestName);
            if (!File.Exists(path)) return list;

            Dictionary<string, object> root = null;
            try { root = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception ex) { Log.Error("library manifest read failed: " + path, ex); return list; }
            if (root == null) return list;

            object arrObj;
            if (!root.TryGetValue("books", out arrObj)) return list;
            List<object> arr = arrObj as List<object>;
            if (arr == null) return list;

            foreach (object o in arr)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                Book b = new Book();
                b.Slug = MiniJson.Str(m, "slug");
                b.Name = MiniJson.Str(m, "book");
                b.Dynasty = MiniJson.Str(m, "dynasty");
                b.Pairing = MiniJson.Str(m, "pairing");
                b.Docs = (int)Num(m, "docs");
                b.Bytes = Num(m, "bytes");
                if (b.Slug.Length > 0 && b.Name.Length > 0) list.Add(b);
            }
            return list;
        }

        private static long Num(Dictionary<string, object> m, string key)
        {
            object v;
            if (m == null || !m.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        /// <summary>分片文件的完整路径；清单里没有或文件缺失的会被跳掉</summary>
        public static List<string> ShardPaths(string baseDir, IList<string> slugs)
        {
            List<string> paths = new List<string>();
            string d = ShardDir(baseDir);
            if (string.IsNullOrEmpty(d) || slugs == null) return paths;
            foreach (string s in slugs)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string p = Path.Combine(d, s + ".json");
                if (File.Exists(p)) paths.Add(p);
            }
            return paths;
        }

        /// <summary>
        /// 把 slug 过滤成清单里真实存在的。用处在两处：手改过 library.json 写进了
        /// 不存在的书；以及便携版根本没带分片 —— 这两种都不该让检索出问题。
        /// </summary>
        public static List<string> Sanitize(string baseDir, IList<string> slugs)
        {
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Book b in Available(baseDir)) have.Add(b.Slug);

            List<string> outp = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (slugs != null)
                foreach (string s in slugs)
                    if (!string.IsNullOrEmpty(s) && have.Contains(s) && seen.Add(s)) outp.Add(s);
            return outp;
        }

        /// <summary>读取勾选记录；没有记录时返回默认组合。</summary>
        public static List<string> LoadSelection(string baseDir, string dataRoot)
        {
            string path = SelectionPath(dataRoot);
            if (path != null && File.Exists(path))
            {
                try
                {
                    Dictionary<string, object> root =
                        MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    object v;
                    List<object> arr = (root != null && root.TryGetValue("selected", out v)) ? v as List<object> : null;
                    if (arr != null)
                    {
                        // 空数组是合法选择（用户在藏书阁里全不选），不能当成「没有记录」而回落默认。
                        List<string> sel = new List<string>();
                        foreach (object o in arr)
                        {
                            string s = Convert.ToString(o, CultureInfo.InvariantCulture);
                            if (!string.IsNullOrEmpty(s)) sel.Add(s);
                        }
                        return Sanitize(baseDir, sel);
                    }
                }
                catch (Exception ex) { Log.Error("library selection read failed: " + path, ex); }
            }
            return Sanitize(baseDir, DefaultSelection);
        }

        public static void SaveSelection(string dataRoot, IList<string> slugs)
        {
            string path = SelectionPath(dataRoot);
            if (path == null || slugs == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"selected\":[");
                for (int i = 0; i < slugs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Json.Escape(slugs[i])).Append('"');
                }
                sb.Append("]}");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("library selection write failed", ex); }
        }

        private static string SelectionPath(string dataRoot)
        {
            string dir = WritableDir(dataRoot);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SelectionName);
        }

        /// <summary>
        /// 程序可能装在 Program Files 这类只读位置，逐个候选探测可写性
        /// （与 ErrorLog.Bind 同一做法，候选顺序对齐 AppMain 的工作目录回退）。
        /// </summary>
        public static string WritableDir(string preferred)
        {
            List<string> cands = new List<string>();
            if (!string.IsNullOrEmpty(preferred)) cands.Add(preferred);
            try { cands.Add(Environment.GetEnvironmentVariable("JIGU_HOME")); } catch { }
            try
            {
                cands.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jigu"));
            }
            catch { }
            try { cands.Add(Path.Combine(Path.GetTempPath(), "Jigu")); } catch { }

            foreach (string d in cands)
            {
                try
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".library-probe");
                    File.WriteAllText(probe, "1", Encoding.UTF8);
                    File.Delete(probe);
                    return d;
                }
                catch { }
            }
            return null;
        }

        /// <summary>藏书阁界面用的 JSON：可选书目 + 当前勾选 + 汇总</summary>
        public static string ToJson(string baseDir, IList<string> selected)
        {
            HashSet<string> sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selected != null) foreach (string s in selected) sel.Add(s);

            List<Book> books = Available(baseDir);
            long installed = 0, loaded = 0;
            int selCount = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"installed\":").Append(IsInstalled(baseDir) ? "true" : "false");
            sb.Append(",\"books\":[");
            for (int i = 0; i < books.Count; i++)
            {
                Book b = books[i];
                bool on = sel.Contains(b.Slug);
                installed += b.Bytes;
                if (on) { loaded += b.Bytes; selCount++; }
                if (i > 0) sb.Append(',');
                sb.Append("{\"slug\":\"").Append(Json.Escape(b.Slug)).Append('"');
                sb.Append(",\"book\":\"").Append(Json.Escape(b.Name)).Append('"');
                sb.Append(",\"dynasty\":\"").Append(Json.Escape(b.Dynasty)).Append('"');
                sb.Append(",\"pairing\":\"").Append(Json.Escape(b.Pairing)).Append('"');
                sb.Append(",\"docs\":").Append(b.Docs);
                sb.Append(",\"bytes\":").Append(b.Bytes);
                sb.Append(",\"selected\":").Append(on ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],\"selectedCount\":").Append(selCount);
            sb.Append(",\"installedBytes\":").Append(installed);
            sb.Append(",\"selectedBytes\":").Append(loaded);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
