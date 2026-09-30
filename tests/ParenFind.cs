// FILE: jigu-app/tests/ParenFind.cs
// 定位未闭合的括号：逐行累计深度，找出“从某行开始深度再也回不到 0”的分界行。
using System;
using System.IO;
using System.Text;

internal static class ParenFind
{
    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : Path.Combine(RepoRoot(), "web", "app.js");
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);

        int depth = 0;
        int[] depthAfter = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            depth += CountParens(lines[i], '(', ')');
            depthAfter[i] = depth;
        }

        Console.WriteLine("最终深度 = " + depth);
        // 从末尾回溯，找最后一个深度为 0 的行
        int lastZero = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (depthAfter[i] == 0) { lastZero = i; break; }
        }
        Console.WriteLine("最后一个深度归零的行 = " + (lastZero + 1));
        Console.WriteLine();
        Console.WriteLine("--- 该处附近（可能是未闭合的开括号）---");
        int from = Math.Max(0, lastZero);
        int to = Math.Min(lines.Length - 1, lastZero + 14);
        for (int i = from; i <= to; i++)
        {
            int c = CountParens(lines[i], '(', ')');
            Console.WriteLine(string.Format("{0,4} [{1,3}] {2}", i + 1, depthAfter[i], lines[i]));
        }

        Console.WriteLine();
        Console.WriteLine("--- 深度最大的若干行 ---");
        int best = -1, bestLine = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (depthAfter[i] > best) { best = depthAfter[i]; bestLine = i + 1; }
        }
        Console.WriteLine("最大深度 " + best + " 在第 " + bestLine + " 行");
        return 0;
    }

    private static int CountParens(string line, char open, char close)
    {
        int n = 0;
        bool inS = false, inD = false;
        char prev = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inS) { if (c == '\'' && prev != '\\') inS = false; prev = c; continue; }
            if (inD) { if (c == '"' && prev != '\\') inD = false; prev = c; continue; }
            if (c == '\'') { inS = true; prev = c; continue; }
            if (c == '"') { inD = true; prev = c; continue; }
            if (c == open) n++;
            else if (c == close) n--;
            prev = c;
        }
        return n;
    }
}
