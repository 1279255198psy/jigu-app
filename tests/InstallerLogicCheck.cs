// FILE: jigu-app/tests/InstallerLogicCheck.cs
// 独立验证安装器的两项核心逻辑（在沙箱内 HKCU 与桌面不可写，故用可写路径等价测试）：
//   1) 快捷方式创建/读取（WScript.Shell）
//   2) 卸载注册项的写入/读取/删除（HKCU，测试键）
// 编译：csc /out:InstallerLogicCheck.exe /r:System.Windows.Forms.dll InstallerLogicCheck.cs
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

internal static class InstallerLogicCheck
{
    private static int _fails;
    private static readonly StringBuilder Out = new StringBuilder();

    private static void Say(string s) { Out.AppendLine(s); Console.WriteLine(s); }
    private static void Fail(string s) { _fails++; Say("  FAIL " + s); }

    private static int Main(string[] args)
    {
        // args[0] = 沙箱工作目录（本程序往里写 .lnk / 报告），args[1] = 要检查的应用目录。
        // 两者分开：以前合成一个，于是要么把报告和快捷方式写进交付目录，要么拿沙箱去
        // 当"已安装目录"检查，怎么选都不对。args[1] 省略时沿用旧行为（等于 args[0]）。
        // 默认值不再写死某台机器的绝对路径 —— 换台机器/换了目录名就会指向不存在的树。
        string work = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "jigu-installer-logic");
        string appDir = args.Length > 1 ? args[1] : work;
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Directory.CreateDirectory(work); } catch { }

        Say("== 安装器逻辑校验 ==");
        Say("工作目录: " + work);
        Say("应用目录: " + appDir);
        Say("");

        // 图标名带版本号（Windows 按路径缓存图标，换版本必须换路径），不能写死 ——
        // 写死了版本一升就永远 FAIL，真问题反而被淹掉。到应用目录里找那个版本化图标。
        // 旧的无版本 Jigu.ico 是 build.ps1 明确要清掉的，不该再期待它存在。
        string[] iconFiles = Directory.Exists(appDir)
            ? Directory.GetFiles(appDir, "Jigu-*.ico")
            : new string[0];

        // ---------- 1. 快捷方式 ----------
        Say("--- 1. 桌面快捷方式创建 ---");
        string lnk = Path.Combine(work, "_check_desktop.lnk");
        string target = Path.Combine(appDir, "稽古.exe");
        string icon = iconFiles.Length > 0 ? iconFiles[0] : Path.Combine(appDir, "Jigu.ico");
        string iconName = Path.GetFileName(icon);
        if (CreateShortcut(lnk, target, work, icon))
        {
            Say("  已创建: " + lnk + " (" + new FileInfo(lnk).Length + " B)");
            string t, w, i;
            if (ReadShortcut(lnk, out t, out w, out i))
            {
                Say("  TargetPath       = " + t);
                Say("  WorkingDirectory = " + w);
                Say("  IconLocation     = " + i);
                if (!string.Equals(t, target, StringComparison.OrdinalIgnoreCase)) Fail("目标路径不符");
                if (!string.Equals(w, work, StringComparison.OrdinalIgnoreCase)) Fail("工作目录不符");
                if (i.IndexOf(iconName, StringComparison.OrdinalIgnoreCase) < 0)
                    Fail("图标未指向 " + iconName);

                // 目标 exe 存在才谈得上装好了；便携/交付目录里 稽古.exe 就在根下
                if (!File.Exists(target)) Fail("目标 exe 不存在: " + target);
            }
            else Fail("无法读回快捷方式");
        }
        else Fail("快捷方式创建失败");

        Say("");
        Say("--- 2. 卸载注册项（HKCU 测试键）---");
        string testKey = @"Software\JiguSetupCheck\Uninstall\{TEST-APP-ID}";
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(testKey))
            {
                if (k == null) Fail("CreateSubKey 返回 null");
                else
                {
                    k.SetValue("DisplayName", "稽古（二十四史情境检索）");
                    k.SetValue("DisplayVersion", "1.3.0");
                    k.SetValue("Publisher", "稽古");
                    k.SetValue("InstallLocation", work);
                    k.SetValue("DisplayIcon", icon);
                    k.SetValue("UninstallString", "\"" + Path.Combine(work, "稽古-卸载.exe") + "\" /uninstall");
                    k.SetValue("QuietUninstallString", "\"" + Path.Combine(work, "稽古-卸载.exe") + "\" /uninstall /silent");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    Say("  写入成功");
                }
            }

            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(testKey))
            {
                if (k == null) Fail("读回失败：键不存在");
                else
                {
                    Say("  DisplayName     = " + k.GetValue("DisplayName"));
                    Say("  DisplayVersion  = " + k.GetValue("DisplayVersion"));
                    Say("  UninstallString = " + k.GetValue("UninstallString"));
                    Say("  DisplayIcon     = " + k.GetValue("DisplayIcon"));
                    if (k.GetValue("DisplayName") == null) Fail("DisplayName 缺失");
                    if (k.GetValue("UninstallString") == null) Fail("UninstallString 缺失");
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(testKey))
            {
                if (k != null) Fail("删除后仍存在");
                else Say("  删除成功（等价卸载时的清理）");
            }
        }
        catch (Exception ex)
        {
            Fail("注册表操作异常: " + ex.GetType().Name + " / " + ex.Message);
        }

        Say("");
        Say("--- 3. 已安装目录完整性 ---");
        string exe = Path.Combine(appDir, "稽古.exe");
        string unins = Path.Combine(appDir, "稽古-卸载.exe");
        string[] need = { "稽古.exe", "使用说明.txt",
                          "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
                          "WebView2Loader.dll", "data_base.txt",
                          "data\\00_种子.json", "data\\01_先秦秦汉.json",
                          "data\\02_三国两晋.json", "data\\03_隋唐五代.json", "data\\04_两宋.json",
                          "data\\05_明.json", "data\\06_清.json" };
        // data\version.json 是旧布局的残留：build.ps1 合并语料时显式跳过它、并把它列进
        // junk 清单，也就是说当前产品就是不带这个文件的，这里不能再要求它。
        foreach (string n in need)
        {
            string p = Path.Combine(appDir, n);
            if (File.Exists(p)) Say("  OK   " + n.PadRight(38) + new FileInfo(p).Length + " B");
            else Fail("缺少: " + n);
        }
        if (iconFiles.Length == 1)
            Say("  OK   " + Path.GetFileName(iconFiles[0]).PadRight(38)
                + new FileInfo(iconFiles[0]).Length + " B");
        else if (iconFiles.Length == 0)
            Fail("缺少版本化图标 Jigu-*.ico");
        else
            Fail("有 " + iconFiles.Length + " 个 Jigu-*.ico（旧版本图标没清干净）");

        // 卸载程序由安装向导在安装时生成，便携目录里本来就没有 —— 所以只在真的缺 exe
        // 时报错，缺卸载程序只作提示，别把便携目录误判成安装损坏。
        if (!File.Exists(exe) && !File.Exists(unins))
            Fail("既没有 稽古.exe 也没有 稽古-卸载.exe，这个目录不像是安装目录: " + work);
        else if (!File.Exists(unins))
            Say("  NOTE 无 稽古-卸载.exe（便携目录不带卸载程序，安装后才有）");

        // 卸载程序应与安装包同源（同一二进制）
        if (File.Exists(unins) && File.Exists(exe))
        {
            long a = new FileInfo(unins).Length;
            Say("  卸载程序大小 = " + a + " B（与安装包同一份二进制，带 /uninstall 进入卸载流程）");
        }

        Say("");
        Say(_fails == 0 ? "RESULT: INSTALLER LOGIC OK (0 fails)" : ("RESULT: FAILURES = " + _fails));

        try
        {
            File.WriteAllText(Path.Combine(work, "installer-logic-report.txt"),
                Out.ToString(), new UTF8Encoding(false));
        }
        catch { }
        return _fails == 0 ? 0 : 2;
    }

    private static bool CreateShortcut(string lnkPath, string target, string workingDir, string iconPath)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return false;
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workingDir });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                    new object[] { "稽古 · 二十四史情境检索" });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc,
                    new object[] { iconPath + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                return File.Exists(lnkPath);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
        catch { return false; }
    }

    private static bool ReadShortcut(string lnkPath, out string target, out string workingDir, out string icon)
    {
        target = ""; workingDir = ""; icon = "";
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return false;
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                Type st = sc.GetType();
                target = Convert.ToString(st.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null));
                workingDir = Convert.ToString(st.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, sc, null));
                icon = Convert.ToString(st.InvokeMember("IconLocation", BindingFlags.GetProperty, null, sc, null));
                return true;
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
        catch { return false; }
    }
}
