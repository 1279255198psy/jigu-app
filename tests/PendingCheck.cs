// FILE: jigu-app/tests/PendingCheck.cs
// 验证「后台预下载好 -> 下次启动提示应用」这条跨会话链路的判定核心。
//
// 背景：用户点了「稍后」或者直接关掉程序时，下好的包会留在 pending\ 里。
// 下次启动必须能认出它（否则用户白等一场），但也必须把陈旧/损坏/并不比当前新的
// 残留清掉（否则会反复拿一个没用的包去打扰用户，甚至装回旧版本）。
//
// 用法：PendingCheck.exe [稽古.exe 路径]
// 编译：csc -out:PendingCheck.exe PendingCheck.cs
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static class PendingCheck
{
    private static int _fails;
    private static void Say(string s) { Console.WriteLine(s); }
    private static void Ok(string s) { Console.WriteLine("  OK   " + s); }
    private static void Fail(string s) { _fails++; Console.WriteLine("  FAIL " + s); }

    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static Type _updater;      // Jigu.AppUpdater
    private static Type _report;       // Jigu.AppUpdater+Report
    private static string _pkgName;    // 稽古-update.exe

    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        string name = "稽古";
        string exe = args.Length > 0 ? args[0]
            : Path.Combine(RepoRoot(), "dist", name, name + ".exe");

        Say("== 待应用更新（跨会话）校验 ==");
        Say("exe: " + exe);
        Say("");

        Assembly asm;
        try { asm = Assembly.LoadFile(Path.GetFullPath(exe)); }
        catch (Exception ex) { Say("无法加载程序集: " + ex.Message); return 2; }

        _updater = asm.GetType("Jigu.AppUpdater", true);
        _report = _updater.GetNestedType("Report", BindingFlags.Public | BindingFlags.NonPublic);
        _pkgName = (string)_updater.GetField("PendingExeName", Any).GetValue(null);

        string localVersion = (string)asm.GetType("Jigu.AppVer", true)
            .GetField("Number", Any).GetValue(null);
        Say("当前版本   : v" + localVersion);
        Say("待应用包名 : " + _pkgName);
        Say("");

        string work = Path.Combine(Path.GetTempPath(),
            "jigu-pending-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        string pkg = Path.Combine(work, "pending", _pkgName);

        try
        {
            // 1) 干净的安装目录：没有 pending\，必须返回 null（不能凭空说有更新）
            Say("--- 1. 没有 pending 目录 ---");
            if (FindPending(work) == null) Ok("返回 null");
            else Fail("凭空报出了待应用更新");

            // 2) 写读闭环：WritePendingMeta 写下 meta，FindPending 必须原样认出来
            Say("");
            Say("--- 2. 写入后立刻读回（预下载成功的正常路径）---");
            MakePkg(pkg, 4096);
            object r = NewReport("9.9.9", "演练用更新说明");
            if (!(bool)Invoke("WritePendingMeta", work, r, pkg))
                Fail("WritePendingMeta 返回 false");
            else
            {
                object found = FindPending(work);
                if (found == null) Fail("下好的包没被认出来");
                else
                {
                    string v = (string)Field(found, "RemoteVersion");
                    string n = (string)Field(found, "Notes");
                    long sz = (long)Field(found, "Size");
                    if (v != "9.9.9") Fail("版本读回错误: " + v); else Ok("版本 v" + v);
                    if (n != "演练用更新说明") Fail("说明读回错误: " + n); else Ok("说明完好");
                    if (sz != 4096) Fail("体积读回错误: " + sz); else Ok("体积 " + sz + " B");
                    // 认出来之后不能把它删掉：用户还要靠它重启更新
                    if (File.Exists(pkg)) Ok("包仍在原地");
                    else Fail("FindPending 把有效的包删了");
                }
            }

            // 3) 包被截断/损坏：哈希不符，必须清理并返回 null
            Say("");
            Say("--- 3. 包被改坏（哈希不符）---");
            Reset(work);
            MakePkg(pkg, 4096);
            WriteMeta(work, "9.9.9", new string('0', 64), "说明");
            ExpectCleaned(work, "哈希不符");

            // 4) 版本并不比当前新：多半已经通过别的途径更新过了，清掉
            Say("");
            Say("--- 4. 版本不比当前新（" + localVersion + "）---");
            Reset(work);
            MakePkg(pkg, 4096);
            WriteMeta(work, localVersion, Sha(pkg), "说明");
            ExpectCleaned(work, "版本不更新");

            // 5) 只有 meta、没有包体：清掉
            Say("");
            Say("--- 5. 缺包体 ---");
            Reset(work);
            Directory.CreateDirectory(Path.GetDirectoryName(pkg));
            WriteMeta(work, "9.9.9", new string('a', 64), "说明");
            ExpectCleaned(work, "缺包体");

            // 6) 包体过小（半截下载）：清掉
            Say("");
            Say("--- 6. 包体过小（半截下载）---");
            Reset(work);
            MakePkg(pkg, 512);
            WriteMeta(work, "9.9.9", Sha(pkg), "说明");
            ExpectCleaned(work, "半截包");

            // 7) meta 是坏 JSON：清掉
            Say("");
            Say("--- 7. meta 是坏 JSON ---");
            Reset(work);
            MakePkg(pkg, 4096);
            File.WriteAllText(Path.Combine(work, "pending", "pending.json"), "{ 不是 json", new UTF8Encoding(false));
            ExpectCleaned(work, "坏 JSON");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        Say("");
        Say(_fails == 0 ? "RESULT: PENDING CHECK OK (0 fails)"
                        : "RESULT: PENDING CHECK FAILURES = " + _fails);
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>把工作目录恢复到「有 pending 目录、其余为空」的状态</summary>
    private static void Reset(string work)
    {
        Invoke("DeletePending", work);
        Directory.CreateDirectory(Path.Combine(work, "pending"));
    }

    /// <summary>FindPending 必须返回 null，并且把整个 pending 目录清掉</summary>
    private static void ExpectCleaned(string work, string why)
    {
        object found = FindPending(work);
        if (found != null) { Fail(why + "：仍被当成有效更新"); return; }
        if (Directory.Exists(Path.Combine(work, "pending")))
            Fail(why + "：返回了 null 但残留没清掉");
        else Ok(why + " -> 返回 null 且残留已清理");
    }

    private static void MakePkg(string path, int bytes)
    {
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        byte[] buf = new byte[bytes];
        new Random(12345).NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }

    /// <summary>手写一份 pending.json（等价于 WritePendingMeta 的产物）</summary>
    private static void WriteMeta(string work, string version, string sha, string notes)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"version\": \"").Append(version).Append("\",\n");
        sb.Append("  \"sha256\": \"").Append(sha).Append("\",\n");
        sb.Append("  \"size\": 4096,\n  \"url\": \"\",\n");
        sb.Append("  \"notes\": \"").Append(notes).Append("\"\n}\n");
        File.WriteAllText(Path.Combine(work, "pending", "pending.json"),
            sb.ToString(), new UTF8Encoding(false));
    }

    private static string Sha(string path)
    {
        using (SHA256 s = SHA256.Create())
        using (FileStream f = File.OpenRead(path))
        {
            byte[] h = s.ComputeHash(f);
            StringBuilder sb = new StringBuilder(64);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    private static object NewReport(string version, string notes)
    {
        object r = Activator.CreateInstance(_report, true);
        Field(r, "RemoteVersion", version);
        Field(r, "Notes", notes);
        Field(r, "Url", "https://example.invalid/pkg.exe");
        return r;
    }

    private static object Field(object o, string name, object value)
    {
        _report.GetField(name).SetValue(o, value);
        return o;
    }

    private static object Field(object o, string name)
    {
        return _report.GetField(name).GetValue(o);
    }

    private static object Invoke(string name, params object[] a)
    {
        return _updater.GetMethod(name, Any).Invoke(null, a);
    }

    private static object FindPending(string work)
    {
        return Invoke("FindPending", work);
    }
}
