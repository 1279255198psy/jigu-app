// FILE: jigu-app/src/MiniJson.cs
// 极简 JSON 解析（仅用于读取更新清单这类小文件；语料走 JsonScan 流式扫描）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jigu
{
    internal static class MiniJson
    {
        /// <summary>
        /// 嵌套深度上限。清单类文件都很浅，超过就是畸形输入。
        /// 为什么必须拦：解析是递归下降，一层嵌套两帧；约一万层就能把 1MB 栈耗尽，
        /// 而 StackOverflowException 在 .NET 里**不可捕获** —— 进程当场死，界面上
        /// 连个错误框都没有。这里主动抛，让 Parse 的 catch 把它变成 null，
        /// 调用方照常走「解析失败」的降级路径。（语料扫描器 JsonScan 是迭代实现，不受此限。）
        /// </summary>
        private const int MaxDepth = 64;

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            try { return Value(text, ref i, 0); }
            catch { return null; }
        }

        public static string Str(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return "";
        }

        public static bool Bool(Dictionary<string, object> obj, string key, bool fallback)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is bool) return (bool)v;
            return fallback;
        }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON 嵌套过深（>" + MaxDepth + " 层）");
            Skip(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return Obj(s, ref i, depth);
            if (c == '[') return Arr(s, ref i, depth);
            if (c == '"') return Str2(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return Num(s, ref i);
        }

        private static Dictionary<string, object> Obj(string s, ref int i, int depth)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            while (i < s.Length)
            {
                Skip(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; }
                string key = Str2(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                map[key] = Value(s, ref i, depth + 1);
            }
            return map;
        }

        private static List<object> Arr(string s, ref int i, int depth)
        {
            List<object> list = new List<object>();
            i++;
            while (i < s.Length)
            {
                Skip(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                list.Add(Value(s, ref i, depth + 1));
            }
            return list;
        }

        private static string Str2(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            StringBuilder sb = new StringBuilder(64);
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
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

        private static object Num(string s, ref int i)
        {
            int start = i;
            while (i < s.Length &&
                (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            if (i == start) { i++; return null; }
            double d;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                // U+FEFF：UTF-8 BOM 解码成 string 之后就是它。不跳过去的话，开头那个 BOM
                // 会让文档既不像 { 也不像 [，解析直接返回 null —— 表现为「整张表静默变空」。
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\uFEFF') { i++; continue; }
                break;
            }
        }
    }
}
