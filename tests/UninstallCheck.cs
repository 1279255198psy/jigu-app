// FILE: jigu-app/tests/UninstallCheck.cs
// 卸载判定回归：安装目录归我们时，卸载必须能删掉整个目录（含 data\）；
// 目录里有任何一点用户自己的东西时，必须退化成「只删我们的文件」，绝不递归删用户目录。
//
// 为什么单独一个探针：这条逻辑曾经因为「载荷键是 data/00_种子.json 这种嵌套形式，
// 而比对时手里只有文件名」而永远判 false —— 症状是卸载后 data\ 与安装目录原地留下，
// 界面却显示「已卸载」。原有 8 个探针没有一个覆盖它，所以加这一个。
//
// 与别的探针不同，本探针必须和安装向导的源码编译在一起（要直接读 InstallerPayload
// 的真键表，并通过反射调用 UninstallForm 的私有静态方法）。编译命令见 README.md。
//
// 注意命名空间：InstallerPayload 在 Jigu，而 UninstallForm / SetupInfo 在 JiguSetup
// （安装向导与主程序共用 Host.cs、Update.cs 等，所以那些还在 Jigu）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using JiguSetup;

namespace Jigu
{
    internal static class UninstallCheck
    {
        private static int _fails;
        private static readonly StringBuilder Out = new StringBuilder();

        private static void Say(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        private static void Ok(string s) { Say("  OK   " + s); }
        private static void Fail(string s) { _fails++; Say("  FAIL " + s); }

        private static readonly Type Form = typeof(UninstallForm);
        private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

        private static bool IsOurInstallDir(string dir)
        {
            MethodInfo m = Form.GetMethod("IsOurInstallDir", Flags);
            if (m == null) throw new MissingMethodException("UninstallForm.IsOurInstallDir 不见了");
            return (bool)m.Invoke(null, new object[] { dir });
        }

        private static void RemoveOwnFilesOnly(string dir)
        {
            MethodInfo m = Form.GetMethod("RemoveOwnFilesOnly", Flags);
            if (m == null) throw new MissingMethodException("UninstallForm.RemoveOwnFilesOnly 不见了");
            m.Invoke(null, new object[] { dir });
        }

        // ---------------------------------------------------------------- 夹具

        private static string _root;

        /// <summary>按真实载荷键表铺一个「刚装好」的安装目录。</summary>
        private static string MakeInstallDir(string tag, bool withUninstaller)
        {
            string dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            foreach (KeyValuePair<string, string> kv in InstallerPayload.Table())
            {
                string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                string p = Path.Combine(dir, rel);
                string parent = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(p, "payload");
            }
            // 卸载程序是安装时由安装包自己复制出来的，不在载荷清单里
            if (withUninstaller) File.WriteAllText(Path.Combine(dir, SetupInfo.UninstallerName), "setup");
            return dir;
        }

        private static void Put(string dir, string rel, string content)
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            string parent = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(p, content);
        }

        private static bool Exists(string dir, string rel)
        {
            return File.Exists(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>目录里还有没有载荷带进来的那些文件（抽查几个有代表性的）。</summary>
        private static int CountLeftoverOurs(string dir)
        {
            int n = 0;
            foreach (string rel in new string[] {
                "corpus.json", "data/00_种子.json", "data/06_清.json",
                "data/version.json", "update_base.txt", "data_base.txt", "data_version.json" })
            {
                if (Exists(dir, rel)) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------- 断言

        /// <summary>必须认成「整目录都是我们的」：卸载要能删掉目录本身。</summary>
        private static void MustBeOurs(string tag, string dir)
        {
            if (IsOurInstallDir(dir)) Ok(tag + "：判定为我们的安装目录（可整目录删除）");
            else Fail(tag + "：应判为我们的安装目录，实际判 false —— 卸载会留下残留");
        }

        /// <summary>必须认成「不全是我们的」：只许删我们的文件，不许删目录。</summary>
        private static void MustNotBeOurs(string tag, string dir, string why)
        {
            if (IsOurInstallDir(dir)) Fail(tag + "：不该判为我们的目录（" + why + "），实际判 true —— 有递归删用户文件的风险");
            else Ok(tag + "：不判为我们的目录（" + why + "）");
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            _root = args.Length > 0 ? args[0]
                : Path.Combine(Path.GetTempPath(), "jigu-uninstall-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);

            Say("== 卸载判定回归 ==");
            Say("沙箱: " + _root);
            Say("载荷文件数: " + InstallerPayload.Table().Count);
            Say("");

            try
            {
                // ---------- 1. 干净的安装目录 ----------
                Say("--- 1. 干净的安装目录（只有我们的东西）---");
                string a = MakeInstallDir("clean", true);
                MustBeOurs("干净目录", a);
                RemoveOwnFilesOnly(a);
                int left = CountLeftoverOurs(a);
                if (left == 0) Ok("降级卸载后我们的文件已清空");
                else Fail("降级卸载后仍有 " + left + " 个我们的文件残留");
                if (Directory.Exists(Path.Combine(a, "data"))) Fail("data\\ 仍在");
                else Ok("data\\ 已删除");

                // ---------- 2. 顶层有用户自己的文件 ----------
                Say("");
                Say("--- 2. 用户在安装目录顶层放了自己的文件 ---");
                string b = MakeInstallDir("usertop", true);
                Put(b, "user-notes.txt", "mine");
                MustNotBeOurs("顶层用户文件", b, "顶层有 user-notes.txt");
                RemoveOwnFilesOnly(b);
                if (Exists(b, "user-notes.txt")) Ok("用户文件保留");
                else Fail("用户文件被删掉了");
                if (Exists(b, "corpus.json")) Fail("我们的 corpus.json 没删掉");
                else Ok("我们的文件已删除");

                // ---------- 3. data\ 里有用户自己的文件 ----------
                Say("");
                Say("--- 3. 用户在 data\\ 里放了自己的文件 ---");
                string c = MakeInstallDir("userdata", true);
                Put(c, "data/user-extra.json", "mine");
                MustNotBeOurs("data 内用户文件", c, "data\\ 内有 user-extra.json");
                RemoveOwnFilesOnly(c);
                if (Exists(c, "data/user-extra.json")) Ok("data\\ 里的用户文件保留");
                else Fail("data\\ 里的用户文件被删掉了");

                // ---------- 4. 中断留下的 .tmp / .old ----------
                Say("");
                Say("--- 4. 热替换中断留下的副本 ---");
                string d = MakeInstallDir("strays", true);
                Put(d, "corpus.json.tmp", "x");
                Put(d, "data/00_种子.json.old", "x");
                MustBeOurs("中断副本", d);
                RemoveOwnFilesOnly(d);
                if (Exists(d, "corpus.json.tmp")) Fail("corpus.json.tmp 没清掉");
                else Ok("corpus.json.tmp 已清掉");
                if (Exists(d, "data/00_种子.json.old")) Fail("data\\ 下的 .old 没清掉");
                else Ok("data\\ 下的 .old 已清掉");

                // 用户自己的 notes.tmp 不是我们的，绝不能被当成中断副本删掉
                string e = MakeInstallDir("usertmp", true);
                Put(e, "notes.tmp", "mine");
                RemoveOwnFilesOnly(e);
                if (Exists(e, "notes.tmp")) Ok("用户的 notes.tmp 保留（没被当成我们的中断副本）");
                else Fail("用户的 notes.tmp 被误删");

                // ---------- 5. 相对路径键：顶层 version.json 是用户的 ----------
                // 载荷里有 data/version.json。改按相对路径比对之前，拿 Path.GetFileName
                // 去比，用户顶层的 version.json 会被当成我们的文件；反过来 data\ 里的
                // 那个又永远比不上 —— 两头都错。
                Say("");
                Say("--- 5. 用户顶层 version.json 与我们的 data\\version.json 同名 ---");
                string f = MakeInstallDir("sameleaf", true);
                Put(f, "version.json", "mine");
                MustNotBeOurs("同名不同层", f, "顶层 version.json 是用户的");
                RemoveOwnFilesOnly(f);
                if (Exists(f, "version.json")) Ok("用户顶层的 version.json 保留");
                else Fail("用户顶层的 version.json 被误删");

                // ---------- 6. pending\ ----------
                Say("");
                Say("--- 6. 待应用的更新包目录 ---");
                string g = MakeInstallDir("pending", true);
                Put(g, AppUpdater.PendingDirName + "/" + AppUpdater.PendingExeName, "pkg");
                Put(g, AppUpdater.PendingDirName + "/" + AppUpdater.PendingMetaName, "{}");
                MustBeOurs("pending 归我们", g);
                Put(g, "pending/user-file.txt", "mine");
                MustNotBeOurs("pending 里有用户文件", g, "pending\\ 内有 user-file.txt");

                // ---------- 7. 陌生子目录 ----------
                Say("");
                Say("--- 7. 用户自己的子目录 ---");
                string h = MakeInstallDir("userdir", true);
                Put(h, "photos/a.jpg", "mine");
                MustNotBeOurs("用户子目录", h, "有 photos\\");
            }
            catch (Exception ex)
            {
                Fail("异常: " + ex.GetType().Name + " / " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }

            Say("");
            Say(_fails == 0 ? "RESULT: UNINSTALL CHECK OK (0 fails)"
                : ("RESULT: UNINSTALL CHECK FAILURES = " + _fails));
            return _fails;
        }
    }
}
