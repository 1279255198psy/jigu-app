// FILE: jigu-app/tests/UpdateLockCheck.cs
// 验证多开时的互斥：两个实例同时更新同一个安装目录，会互相覆盖 pending 包、
// 并各自拉起一个替换助手去改同一批文件 —— 这是真能把安装目录改坏的场景。
// Host.AcquireUpdateLock 用命名互斥量把「下载 + 替换」整段串起来，这里验证它确实排他。
//
// 关于「另一个实例」怎么模拟：命名互斥量的所有权是**按线程**的，同一个线程
// 重复获取会成功（可重入），所以本探针一律换一个线程去抢。内核对象是会话级的，
// 另一个线程与另一个进程争的是同一个对象，语义一致。
//
// 用法：UpdateLockCheck.exe [稽古.exe 路径]
// 编译：csc -out:UpdateLockCheck.exe UpdateLockCheck.cs
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class UpdateLockCheck
{
    private static int _fails;
    private static void Say(string s) { Console.WriteLine(s); }
    private static void Ok(string s) { Console.WriteLine("  OK   " + s); }
    private static void Fail(string s) { _fails++; Console.WriteLine("  FAIL " + s); }

    private static MethodInfo _acquire;

    // 只需要当成「两个安装目录」的名字，不必真的存在：锁名只由路径文本推出
    private const string DirA = @"D:\jigu-lock-test\A";
    private const string DirB = @"D:\jigu-lock-test\B";

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

        Say("== 更新互斥量校验 ==");
        Say("exe: " + exe);
        Say("");

        Assembly asm;
        try { asm = Assembly.LoadFile(Path.GetFullPath(exe)); }
        catch (Exception ex) { Say("无法加载程序集: " + ex.Message); return 2; }

        Type form = asm.GetType("Jigu.MainForm");
        if (form == null) { Say("程序集里找不到 Jigu.MainForm"); return 2; }
        _acquire = form.GetMethod("AcquireUpdateLock", BindingFlags.NonPublic | BindingFlags.Static);
        if (_acquire == null) { Say("找不到 MainForm.AcquireUpdateLock"); return 2; }

        // 注意：跨线程拿到的锁不在这里释放。Mutex 的释放必须由持有它的线程做，
        // 而本探针的工作线程拿到锁后就结束 —— 线程退出即释放。主线程只负责
        // 释放自己拿的那把。
        object main = Acquire(DirA);
        if (main == null) { Fail("第一个持有者自己都没拿到锁"); Say(""); return 1; }
        Say("--- 1. 第一个持有者 ---");
        Ok("拿到锁: " + DirA);

        Say("");
        Say("--- 2. 另一个持有者抢同一个目录（多开的场景）---");
        object second = OnOtherThread(DirA);
        if (second != null) Fail("也拿到了锁 —— 排他失效，两个实例会同时改同一个安装目录");
        else Ok("被挡住（静默退让，由先到的那个去下载/替换）");

        Say("");
        Say("--- 3. 装在不同路径的副本不该互相阻塞 ---");
        object other = OnOtherThread(DirB);
        if (other == null) Fail("不同安装目录之间误伤了");
        else Ok("互不干扰: " + DirB + " 可独立更新");

        Say("");
        Say("--- 4. 路径写法不同（大小写、结尾反斜杠）必须算同一个安装目录 ---");
        object variant = OnOtherThread(DirA.ToLowerInvariant() + "\\");
        if (variant != null) Fail("同一个目录的另一种写法被当成了另一个目录 —— 排他会漏");
        else Ok(@"'d:\jigu-lock-test\a\' 与 'D:\jigu-lock-test\A' 视为同一目录");

        Say("");
        Say("--- 5. 释放后必须能被接手（否则一次异常退出会把更新永久卡死）---");
        if (!Release(main)) { Fail("释放自己持有的锁失败"); }
        else
        {
            object again = OnOtherThread(DirA);
            if (again == null) Fail("释放后仍拿不到锁");
            else Ok("释放后可被接手");
        }

        Say("");
        Say(_fails == 0 ? "RESULT: UPDATE LOCK OK (0 fails)"
                        : "RESULT: UPDATE LOCK FAILURES = " + _fails);
        return _fails == 0 ? 0 : 1;
    }

    private static object Acquire(string dir)
    {
        try { return _acquire.Invoke(null, new object[] { dir }); }
        catch (Exception ex) { Say("  调用异常: " + ex.Message); return null; }
    }

    /// <summary>在工作线程上抢一次锁，把结果带回主线程</summary>
    private static object OnOtherThread(string dir)
    {
        object result = null;
        Thread t = new Thread(delegate() { result = Acquire(dir); });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(10000)) { Say("  工作线程超时"); }
        return result;
    }

    private static bool Release(object m)
    {
        try { ((Mutex)m).ReleaseMutex(); return true; }
        catch (Exception ex) { Say("  释放异常: " + ex.Message); return false; }
    }
}
