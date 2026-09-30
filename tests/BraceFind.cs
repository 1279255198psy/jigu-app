// FILE: jigu-app/tests/BraceFind.cs
// 逐行统计 CSS 的 {} 深度，找出第一个深度为负的行（即多余或缺失的括号）
using System;
using System.IO;
using System.Text;

internal static class BraceFind
{
    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : Path.Combine(RepoRoot(), "web", "styles.css");
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Console.OutputEncoding = Encoding.UTF8;

        int open = 0, close = 0, depth = 0;
        int negLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            for (int j = 0; j < l.Length; j++)
            {
                if (l[j] == '{') { open++; depth++; }
                else if (l[j] == '}') { close++; depth--; }
            }
            if (depth < 0 && negLine < 0) negLine = i + 1;
        }
        Console.WriteLine("原始 { = " + open + "   } = " + close + "   差 = " + (open - close));
        Console.WriteLine("最终深度 = " + depth + "   首次为负行 = " + negLine);
        if (negLine > 0)
        {
            Console.WriteLine("--- 附近 ---");
            for (int i = Math.Max(0, negLine - 4); i < Math.Min(lines.Length, negLine + 3); i++)
                Console.WriteLine(string.Format("{0,4}| {1}", i + 1, lines[i]));
        }
        return 0;
    }
}
