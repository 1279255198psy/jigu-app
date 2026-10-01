// FILE: jigu-app/tests/IconCheck.cs
// 校验图标是否真的换成了古风印章，并且沿用到 exe 与安装包：
//   1. resources/app.ico 存在、含多尺寸、尺寸正确
//   2. 从 exe 提取到的图标与源图标逐像素比对（相同则说明 exe 用的就是新图标）
//   3. 安装包 exe 的图标同样比对
// 用法: IconCheck.exe <项目根目录>
// 编译: csc -main:IconCheck -out:tests\IconCheck.exe tests\IconCheck.cs -reference:System.Drawing.dll
//       -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll
//       （ZipArchive 在前者、ZipFile 在后者，两个都要）
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;

internal static class IconCheck
{
    private static int _fails;
    private static readonly StringBuilder Out = new StringBuilder();

    private static void Say(string s) { Out.AppendLine(s); Console.WriteLine(s); }
    private static void Ok(string s) { Say("  OK   " + s); }
    private static void Fail(string s) { _fails++; Say("  FAIL " + s); }

    /// <summary>仓库根目录：探针 exe 就放在 &lt;root&gt;\tests\ 下，别写死本机的绝对路径。</summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    }

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : RepoRoot();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Say("== 图标校验 ==");

        // ---------- 1. 源图标 ----------
        string srcIco = Path.Combine(root, @"resources\app.ico");
        if (!File.Exists(srcIco)) { Fail("缺少 resources\\app.ico"); return 2; }
        FileInfo fi = new FileInfo(srcIco);
        Say("  源图标: " + srcIco + "  " + fi.Length + " bytes");

        int sizeCount = CountIcoEntries(srcIco);
        Say("  ICO 内含尺寸数 = " + sizeCount);
        if (sizeCount >= 5) Ok("多尺寸图标（含小尺寸，任务栏/资源管理器清晰）");
        else Fail("尺寸数偏少: " + sizeCount);

        Bitmap srcLarge = ExtractPngFromIco(srcIco, 256);
        if (srcLarge == null) Fail("无法从 ico 取出 256px 图");
        else Ok("取出 256px 基准图 " + srcLarge.Width + "x" + srcLarge.Height);

        // ---------- 2. exe 图标 ----------
        string exe = Path.Combine(root, @"dist\稽古\稽古.exe");
        if (!File.Exists(exe)) { Fail("缺少 exe: " + exe); return 2; }
        CompareExeIcon("稽古.exe", exe, srcLarge);

        // ---------- 3. 安装包图标 ----------
        string setup = Path.Combine(root, @"dist\稽古-安装包.exe");
        if (File.Exists(setup)) CompareExeIcon("稽古-安装包.exe", setup, srcLarge);
        else Fail("缺少安装包");

        // ---------- 4. 安装包负载内的图标文件 ----------
        // 图标文件名带版本号（Windows 按路径缓存图标，换版本必须换路径），所以这里
        // 不能写死某一个版本 —— 写死了版本一升探针就永远 FAIL，真问题反而被淹掉。
        // 改成问"交付目录里那个图标，负载里有没有同名条目"，与版本号解耦。
        string exeDir = Path.Combine(root, @"dist\稽古");
        string[] icons = Directory.Exists(exeDir)
            ? Directory.GetFiles(exeDir, "Jigu-*.ico")
            : new string[0];
        if (icons.Length == 0)
        {
            Fail("交付目录没有版本化图标 Jigu-*.ico: " + exeDir);
        }
        else if (icons.Length > 1)
        {
            // build.ps1 每轮都会把上一版留下的 Jigu-*.ico 扫掉，多出来就说明没扫干净，
            // 旧的图标会跟着交付出去。
            Fail("交付目录里有 " + icons.Length + " 个 Jigu-*.ico（旧版本图标没清干净）");
            foreach (string f in icons) Say("       " + Path.GetFileName(f));
        }
        else
        {
            string iconName = Path.GetFileName(icons[0]);
            Ok("交付目录含版本化图标 " + iconName);

            // 负载自 0.4.0 起不再是一份生成的 .cs：base64 字面量会撑爆 csc 的用户字符串堆
            // （见 build.ps1 的 New-PayloadZip）。现在它是一个 zip 资源，条目名就是相对
            // 路径。这里改查那份 zip —— 断言的意图不变，问的还是「交付出去的安装包里
            // 有没有这个图标」。
            string payloadZip = Path.Combine(root, @"obj\payload.setup.zip");
            if (!File.Exists(payloadZip))
            {
                Fail("缺少 " + payloadZip + "（先跑 build.ps1 生成安装负载）");
            }
            else
            {
                bool found = false;
                using (ZipArchive zip = ZipFile.OpenRead(payloadZip))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (string.Equals(e.FullName, iconName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (found) Ok("安装包负载内含 " + iconName + "（快捷方式与卸载项用它，避开图标缓存）");
                else Fail("安装包负载缺少版本化图标 " + iconName);
            }
        }

        // ---------- 5. 是否退化成默认图标 ----------
        if (File.Exists(Path.Combine(exeDir, "稽古.exe"))) Ok("交付目录含 稽古.exe");
        else Fail("交付目录缺少 稽古.exe");

        Say("");
        Say(_fails == 0 ? "RESULT: ICON OK (0 fails)" : ("RESULT: FAILURES = " + _fails));
        try
        {
            File.WriteAllText(Path.Combine(root, @"tests\icon-check-report.txt"),
                Out.ToString(), new UTF8Encoding(false));
        }
        catch { }
        return _fails == 0 ? 0 : 2;
    }

    private static void CompareExeIcon(string label, string exePath, Bitmap expected)
    {
        try
        {
            using (Icon ic = Icon.ExtractAssociatedIcon(exePath))
            {
                if (ic == null) { Fail(label + " 取不到图标（可能是默认空白图标）"); return; }
                using (Bitmap bmp = ic.ToBitmap())
                {
                    Say("  " + label + " 图标: " + bmp.Width + "x" + bmp.Height
                        + "  像素样本=" + Sample(bmp));
                    if (expected == null) return;
                    double diff = PixelDiff(bmp, expected);
                    Say(string.Format("  与源图标平均像素差 = {0:N1} / 255", diff));
                    if (diff < 40) Ok(label + " 图标与古风印章一致（非默认图标）");
                    else Fail(label + " 图标与源图标不符（可能仍是默认图标）");
                }
            }
        }
        catch (Exception ex) { Fail(label + " 提取失败: " + ex.Message); }
    }

    /// <summary>缩放到同一尺寸后逐像素比较平均差</summary>
    private static double PixelDiff(Bitmap a, Bitmap b)
    {
        const int N = 32;
        using (Bitmap ra = new Bitmap(a, new Size(N, N)))
        using (Bitmap rb = new Bitmap(b, new Size(N, N)))
        {
            double sum = 0;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    Color ca = ra.GetPixel(x, y);
                    Color cb = rb.GetPixel(x, y);
                    sum += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                }
            }
            return sum / (N * N * 3.0);
        }
    }

    private static string Sample(Bitmap bmp)
    {
        Color c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
    }

    /// <summary>统计 ICO 内的图像条目数</summary>
    private static int CountIcoEntries(string path)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 6) return 0;
            return b[4] | (b[5] << 8);
        }
        catch { return 0; }
    }

    /// <summary>从 ICO 中取指定尺寸的 PNG 条目</summary>
    private static Bitmap ExtractPngFromIco(string path, int want)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            int count = b[4] | (b[5] << 8);
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                int w = b[off] == 0 ? 256 : b[off];
                int len = BitConverter.ToInt32(b, off + 8);
                int dataOff = BitConverter.ToInt32(b, off + 12);
                if (w != want) continue;
                using (MemoryStream ms = new MemoryStream(b, dataOff, len))
                    return new Bitmap(ms);
            }
            // 退回最大的一张
            int best = 0, bestW = -1;
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                int w = b[off] == 0 ? 256 : b[off];
                if (w > bestW) { bestW = w; best = i; }
            }
            int o2 = 6 + best * 16;
            int l2 = BitConverter.ToInt32(b, o2 + 8);
            int d2 = BitConverter.ToInt32(b, o2 + 12);
            using (MemoryStream ms = new MemoryStream(b, d2, l2))
                return new Bitmap(ms);
        }
        catch { return null; }
    }
}
