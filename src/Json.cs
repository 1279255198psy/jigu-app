// FILE: jigu-app/src/Json.cs
// 极简 JSON 工具：流式扫描语料（逐条吐出文档，不构造整棵对象树）+ 转义。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jigu
{
    internal static class Json
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 语料文件扫描器。
    /// 支持两种结构：
    ///   { "items": [ {...}, {...} ] }
    ///   [ {...}, {...} ]
    /// 每条文档解析完立即交给回调，正文只在回调期间存在，不入全局缓存。
    /// </summary>
    internal static class JsonScan
    {
        // start/end 是这条文档的起止位置，单位是**字符**——下标走的是解码后的 string，
        // 不是传进来的 byte[]。语料全是中文（UTF-8 下一个汉字 3 字节），拿它去切 byte[]
        // 会切歪，而且偏得越靠后越多。要取原文请用下面的 ObjOf(text, start, end)，
        // 别自己拿 bytes 去切。
        public delegate void DocHandler(CorpusDoc doc, long start, long end);

        /// <summary>把语料字节解码成扫描器使用的那个 string（下标就是它上面的下标）。</summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 按 ForEachDocument 给出的 start/end 取回这条文档的原始 JSON 文本。
        /// text 必须是喂给扫描器的同一个（或内容相同的）字符串——下标只在它上面成立。
        /// </summary>
        public static string ObjOf(string text, long start, long end)
        {
            if (text == null) return "";
            int s = (int)start, e = (int)end;
            if (s < 0) s = 0;
            if (s > text.Length) s = text.Length;
            if (e > text.Length) e = text.Length;
            if (e < s) e = s;
            return text.Substring(s, e - s);
        }

        public static void ForEachDocument(byte[] bytes, DocHandler handler)
        {
            ForEachDocument(Decode(bytes), handler);
        }

        /// <summary>已解码文本的入口。要按 start/end 取原文的调用方走这个，少解码一遍。</summary>
        public static void ForEachDocument(string text, DocHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(text)) return;
            int i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length) return;

            if (text[i] == '[')
            {
                i++;
                ScanArray(text, ref i, handler, null);
                return;
            }
            if (text[i] == '{')
            {
                i++;
                string bookName = null;
                // 根对象里通常有 "book" 与 "items"。
                // 关键：先把 book 读出来，再交给 items 数组；
                // 绝不能把根对象当成一条文档去解析（否则会把 items 当成字符串跳过）。
                while (i < text.Length)
                {
                    SkipWs(text, ref i);
                    if (i >= text.Length) break;
                    if (text[i] == '}') { i++; break; }
                    if (text[i] == ',') { i++; continue; }
                    if (text[i] != '"') { i++; continue; }
                    string key = ReadString(text, ref i);
                    SkipWs(text, ref i);
                    if (i < text.Length && text[i] == ':') i++;
                    SkipWs(text, ref i);

                    if (key == "book" || key == "source_url")
                    {
                        string v = ReadString(text, ref i);
                        if (key == "book") bookName = v;
                    }
                    else if (key == "items" && i < text.Length && text[i] == '[')
                    {
                        i++;
                        ScanArray(text, ref i, handler, bookName);
                    }
                    else
                    {
                        SkipValue(text, ref i);
                    }
                }
            }
        }

        private static void ScanArray(string text, ref int i, DocHandler handler, string inheritBook)
        {
            while (i < text.Length)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == ']') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] == '{')
                {
                    long start = i;
                    CorpusDoc doc = ReadDoc(text, ref i);
                    if (doc != null)
                    {
                        if (string.IsNullOrEmpty(doc.Book) && !string.IsNullOrEmpty(inheritBook))
                            doc.Book = inheritBook;
                        handler(doc, start, i);
                    }
                }
                else
                {
                    SkipValue(text, ref i);
                }
            }
        }

        private static CorpusDoc ReadDoc(string text, ref int i)
        {
            CorpusDoc doc = new CorpusDoc();
            if (i >= text.Length || text[i] != '{') return null;
            i++;
            while (i < text.Length)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') { i++; continue; }
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ':') i++;
                SkipWs(text, ref i);

                switch (key)
                {
                    case "book": doc.Book = ReadString(text, ref i); break;
                    case "chapter": doc.Chapter = ReadString(text, ref i); break;
                    case "title": doc.Title = ReadString(text, ref i); break;
                    case "original":
                    case "text":
                    case "content":
                        doc.Original = ReadString(text, ref i); break;
                    case "translation":
                    case "modern":
                    case "trans":
                        doc.Translation = ReadString(text, ref i); break;
                    case "decision": doc.Decision = ReadString(text, ref i); break;
                    case "outcome": doc.Outcome = ReadString(text, ref i); break;
                    case "figures": doc.Figures = ReadStringArray(text, ref i); break;
                    case "themes": doc.Themes = ReadStringArray(text, ref i); break;
                    case "pros": doc.Pros = ReadStringArray(text, ref i); break;
                    case "cons": doc.Cons = ReadStringArray(text, ref i); break;
                    default: SkipValue(text, ref i); break;
                }
            }
            return doc;
        }

        private static string[] ReadStringArray(string text, ref int i)
        {
            List<string> list = new List<string>(4);
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '[') { SkipValue(text, ref i); return list.ToArray(); }
            i++;
            while (i < text.Length)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == ']') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] == '"') list.Add(ReadString(text, ref i));
                else SkipValue(text, ref i);
            }
            return list.ToArray();
        }

        private static string ReadString(string text, ref int i)
        {
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '"') return "";
            i++;
            StringBuilder sb = new StringBuilder(64);
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= text.Length) break;
                char e = text[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 <= text.Length)
                        {
                            int code;
                            if (int.TryParse(text.Substring(i, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static void SkipValue(string text, ref int i)
        {
            SkipWs(text, ref i);
            if (i >= text.Length) return;
            char c = text[i];
            if (c == '"') { ReadString(text, ref i); return; }
            if (c == '{' || c == '[')
            {
                char open = c, close = (c == '{') ? '}' : ']';
                int depth = 0;
                bool inStr = false;
                while (i < text.Length)
                {
                    char d = text[i];
                    if (inStr)
                    {
                        if (d == '\\') { i += 2; continue; }
                        if (d == '"') inStr = false;
                        i++;
                        continue;
                    }
                    if (d == '"') { inStr = true; i++; continue; }
                    if (d == open) depth++;
                    else if (d == close)
                    {
                        depth--;
                        if (depth == 0) { i++; return; }
                    }
                    i++;
                }
                return;
            }
            while (i < text.Length && text[i] != ',' && text[i] != '}' && text[i] != ']') i++;
        }

        private static void SkipWs(string text, ref int i)
        {
            while (i < text.Length)
            {
                char c = text[i];
                // U+FEFF：UTF-8 BOM 解码成 string 之后就是它。带 BOM 的语料原本会走到
                // 下面「既不是 [ 也不是 {」那条路，静默返回 0 条 —— 不抛异常，也就不会
                // 回落到内置种子，界面上表现为史料库整个空掉。
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\uFEFF') { i++; continue; }
                break;
            }
        }
    }
}
