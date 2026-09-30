using System;
using System.IO;
using System.Text;

// Validates web assets: presence, comment/string-aware bracket balance, required symbols.
internal static class JsSyntax
{
    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        string appDir = args != null && args.Length > 0 ? args[0] : RepoRoot();
        string webDir = Path.Combine(appDir, "web");
        StringBuilder sb = new StringBuilder();
        int fails = 0;

        string[] files = { "app.js", "index.html", "styles.css", "ink.css" };
        foreach (string f in files)
        {
            string p = Path.Combine(webDir, f);
            if (!File.Exists(p)) { sb.AppendLine("FAIL missing " + p); fails++; continue; }
            sb.AppendLine("OK   " + f + "  " + new FileInfo(p).Length + " bytes");
        }

        string js = File.ReadAllText(Path.Combine(webDir, "app.js"), Encoding.UTF8);
        int braces, parens, brackets;
        Strip(js, out braces, out parens, out brackets);
        Report(sb, ref fails, "brace", braces);
        Report(sb, ref fails, "paren", parens);
        Report(sb, ref fails, "bracket", brackets);

        string[] required = { "callJsonAuto", "localSearch", "loadLocalCorpus", "renderResults", "renderLibrary" };
        foreach (string n in required)
            if (js.IndexOf(n, StringComparison.Ordinal) < 0)
            { sb.AppendLine("FAIL app.js missing " + n); fails++; }

        // raw-JSON leaks must never be rendered
        string[] banned = { "ExecuteScriptAsync", "jigu.js", "rules.js", "innerHTML = raw" };
        foreach (string b in banned)
            if (js.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
            { sb.AppendLine("FAIL app.js references " + b); fails++; }

        sb.AppendLine();
        sb.AppendLine(fails == 0 ? "RESULT: FRONTEND OK (0 fails)" : ("RESULT: FRONTEND FAILED (" + fails + " fails)"));
        try { File.WriteAllText(Path.Combine(appDir, "tests", "frontend-check-report.txt"), sb.ToString(), Encoding.UTF8); }
        catch { }
        Console.WriteLine(sb.ToString());
        return fails == 0 ? 0 : 1;
    }

    private static void Report(StringBuilder sb, ref int fails, string what, int depth)
    {
        if (depth != 0) { sb.AppendLine("FAIL " + what + " unbalanced (depth=" + depth + ")"); fails++; }
        else sb.AppendLine("OK   " + what + " balanced");
    }

    /// <summary>Count brackets while skipping string literals, template literals and comments.</summary>
    private static void Strip(string s, out int braces, out int parens, out int brackets)
    {
        braces = parens = brackets = 0;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (c == '"' || c == '\'' || c == '`')
            {
                char q = c; i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == q) { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '{') braces++;
            else if (c == '}') braces--;
            else if (c == '(') parens++;
            else if (c == ')') parens--;
            else if (c == '[') brackets++;
            else if (c == ']') brackets--;
            i++;
        }
    }
}
