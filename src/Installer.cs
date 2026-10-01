// FILE: jigu-app/src/Installer.cs
// 稽古 · Windows 安装向导（自包含：负载以压缩 zip 资源编译进本安装包）
//
// 提供与常规安装程序等价的能力：
//   · 中文安装向导（欢迎页 / 选择安装位置 / 创建桌面快捷方式 / 进度 / 完成）
//   · 默认安装到 %LOCALAPPDATA%\Programs\稽古（免管理员，不会触发 UAC）
//   · 桌面快捷方式 + 开始菜单快捷方式（带“稽古”图标）
//   · 在“设置 → 应用”中可正常卸载（写入 HKCU 卸载项）
//   · 卸载向导（稽古-卸载.exe /uninstall）与静默安装（/silent）
//   · 全程不联网、不需要管理员权限
//
// 编译：见 build.ps1 第 6 步（负载与分片各是一个 /resource:，不再是生成的 .g.cs）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Jigu;

namespace JiguSetup
{
    internal static class SetupInfo
    {
        public const string AppNameCn = "稽古";
        public const string AppNameEn = "Jigu";
        /// <summary>由 build.ps1 生成的 BuildInfo 提供，勿手写（见 src/Version.g.cs）</summary>
        public const string Version = BuildInfo.Version;
        /// <summary>
        /// 图标文件名带版本号：Windows 会按路径缓存图标，换版本必须换路径。
        /// 从 BuildInfo.Version 拼出来，跟 build.ps1 的 $iconName 是同一个字符串 ——
        /// 手写过一版 "Jigu-1.6.ico"，而程序版本早就不是 1.6 了，图标缓存也就一直没换。
        /// </summary>
        public const string IconFileName = "Jigu-" + BuildInfo.Version + ".ico";
        public const string Publisher = "稽古";
        public const string AppId = "{7A3D53E4-9F21-4C7B-9C3E-1B2A5D8E4F60}";
        public const string ExeName = "稽古.exe";
        public const string UninstallerName = "稽古-卸载.exe";
        public const string AppDescription = "二十四史情境检索：把现实困境交给本地史书语料，"
            + "检索最相似的历史事件并给出规则化建议。运行时零 API 调用。";

        /// <summary>默认安装目录（每用户，无需管理员）</summary>
        public static string DefaultDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(local, "Programs"), AppNameCn);
        }

        public static string DesktopDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        public static string StartMenuDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppNameCn);
        }

        public static string UninstallKey()
        {
            return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
        }
    }

    /// <summary>快捷方式与卸载登记</summary>
    internal static class Shell
    {
        /// <summary>用 WScript.Shell 创建 .lnk（系统自带，无需额外安装）</summary>
        public static bool CreateShortcut(string lnkPath, string target, string workingDir, string iconPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                try
                {
                    object shortcut = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                        null, shell, new object[] { lnkPath });
                    Type st = shortcut.GetType();
                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                        new object[] { workingDir });
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                        new object[] { SetupInfo.AppNameCn + " · 二十四史情境检索" });
                    if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                            new object[] { iconPath + ",0" });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    return true;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
            catch { return false; }
        }

        public static void RegisterUninstall(string installDir)
        {
            try
            {
                string uninstaller = Path.Combine(installDir, SetupInfo.UninstallerName);
                string iconPath = Path.Combine(installDir, SetupInfo.IconFileName);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SetupInfo.UninstallKey()))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", SetupInfo.AppNameCn + "（二十四史情境检索）");
                    k.SetValue("DisplayVersion", SetupInfo.Version);
                    k.SetValue("Publisher", SetupInfo.Publisher);
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("DisplayIcon", File.Exists(iconPath) ? iconPath : Path.Combine(installDir, SetupInfo.ExeName));
                    k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                    k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 4096, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void RemoveUninstall()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(SetupInfo.UninstallKey(), false); }
            catch { }
        }
    }

    /// <summary>把编译进本安装包的负载释放到安装目录</summary>
    internal static class Payload
    {
        public static int FileCount
        {
            get { return InstallerPayload.Table().Count; }
        }

        public static long TotalBytes
        {
            get
            {
                long n = 0;
                foreach (KeyValuePair<string, byte[]> kv in InstallerPayload.Table())
                    n += (long)kv.Value.Length;
                return n;
            }
        }

        /// <summary>释放全部文件；onFile 用于回报进度</summary>
        public static int Extract(string targetDir, Action<int, int, string> onFile)
        {
            Dictionary<string, byte[]> table = InstallerPayload.Table();
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
            int done = 0, total = table.Count;
            foreach (KeyValuePair<string, byte[]> kv in table)
            {
                string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                string path = Path.Combine(targetDir, rel);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, kv.Value);
                done++;
                if (onFile != null) onFile(done, total, rel);
            }
            // 史书分片走 /resource: 的 zip，不在这张表里，单独解到 corpus\
            InstallerPayload.ExtractShards(targetDir);
            return done;
        }

        /// <summary>把图标单独写成文件，供快捷方式与卸载项使用</summary>
        public static string WriteIcon(string targetDir)
        {
            try
            {
                Dictionary<string, byte[]> table = InstallerPayload.Table();
                byte[] bytes;
                string key = SetupInfo.IconFileName;
                if (!table.TryGetValue(key, out bytes)) return null;
                string path = Path.Combine(targetDir, SetupInfo.IconFileName);
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch { return null; }
        }
    }

    /// <summary>安装向导主窗体</summary>
    internal sealed class SetupForm : Form
    {
        private readonly bool _silent;
        private readonly string _silentDir;

        private Panel _pageWelcome;
        private Panel _pageProgress;
        private Panel _pageDone;
        private TextBox _pathBox;
        private CheckBox _desktopCheck;
        private CheckBox _menuCheck;
        private CheckBox _launchCheck;
        private ProgressBar _bar;
        private Label _status;
        private Button _installBtn;
        private Button _browseBtn;
        private Label _doneText;

        public SetupForm(bool silent, string silentDir)
        {
            _silent = silent;
            _silentDir = silentDir;

            Text = SetupInfo.AppNameCn + " 安装向导";
            ClientSize = new Size(620, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(252, 251, 248);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                ShowIcon = Icon != null;
            }
            catch { }

            BuildHeader();
            BuildWelcome();
            BuildProgress();
            BuildDone();

            ShowPage(_pageWelcome);
        }

        private void BuildHeader()
        {
            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 82;
            head.BackColor = Color.FromArgb(24, 28, 34);
            Controls.Add(head);

            PictureBox logo = new PictureBox();
            logo.Size = new Size(46, 46);
            logo.Location = new Point(22, 18);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try { logo.Image = Icon != null ? Icon.ToBitmap() : null; }
            catch { }
            head.Controls.Add(logo);

            Label title = new Label();
            title.Text = SetupInfo.AppNameCn + " · 二十四史情境检索";
            title.ForeColor = Color.FromArgb(224, 196, 106);
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(82, 18);
            head.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "版本 " + SetupInfo.Version + "　安装向导";
            sub.ForeColor = Color.FromArgb(180, 175, 165);
            sub.AutoSize = true;
            sub.Location = new Point(84, 48);
            head.Controls.Add(sub);
        }

        private Panel NewPage()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Color.FromArgb(252, 251, 248);
            Controls.Add(p);
            p.BringToFront();
            p.Visible = false;
            return p;
        }

        private void ShowPage(Panel page)
        {
            _pageWelcome.Visible = false;
            _pageProgress.Visible = false;
            _pageDone.Visible = false;
            page.Visible = true;
            page.BringToFront();
        }

        private void BuildWelcome()
        {
            _pageWelcome = NewPage();

            Label intro = new Label();
            intro.Text = "即将在你的电脑上安装「" + SetupInfo.AppNameCn + "」。\r\n\r\n"
                + "· 全部文件安装到一个文件夹，不修改系统设置\r\n"
                + "· 无需管理员权限，不会弹出权限提示\r\n"
                + "· 安装后桌面会出现「" + SetupInfo.AppNameCn + "」图标，双击即可使用\r\n"
                + "· 程序完全离线运行，不调用任何 AI 接口";
            intro.Location = new Point(24, 14);
            intro.Size = new Size(570, 110);
            _pageWelcome.Controls.Add(intro);

            Label lblPath = new Label();
            lblPath.Text = "安装位置：";
            lblPath.Location = new Point(24, 138);
            lblPath.AutoSize = true;
            _pageWelcome.Controls.Add(lblPath);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(24, 160);
            _pathBox.Size = new Size(470, 26);
            _pathBox.Text = SetupInfo.DefaultDir();
            _pageWelcome.Controls.Add(_pathBox);

            _browseBtn = new Button();
            _browseBtn.Text = "浏览…";
            _browseBtn.Location = new Point(504, 159);
            _browseBtn.Size = new Size(90, 28);
            _browseBtn.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择安装位置";
                    d.SelectedPath = _pathBox.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _pathBox.Text = d.SelectedPath;
                }
            };
            _pageWelcome.Controls.Add(_browseBtn);

            _desktopCheck = new CheckBox();
            _desktopCheck.Text = "创建桌面快捷方式（推荐）";
            _desktopCheck.Checked = true;
            _desktopCheck.Location = new Point(24, 200);
            _desktopCheck.AutoSize = true;
            _pageWelcome.Controls.Add(_desktopCheck);

            _menuCheck = new CheckBox();
            _menuCheck.Text = "在开始菜单中创建「" + SetupInfo.AppNameCn + "」";
            _menuCheck.Checked = true;
            _menuCheck.Location = new Point(24, 226);
            _menuCheck.AutoSize = true;
            _pageWelcome.Controls.Add(_menuCheck);

            Label av = new Label();
            av.Text = "提示：若安装或首次运行时安全软件询问是否允许，请选择「允许 / 信任」。\r\n"
                + "本程序不联网、不写系统目录、不修改注册表启动项。";
            av.ForeColor = Color.FromArgb(150, 110, 30);
            av.Location = new Point(24, 258);
            av.Size = new Size(570, 46);
            _pageWelcome.Controls.Add(av);

            _installBtn = new Button();
            _installBtn.Text = "开始安装";
            _installBtn.Size = new Size(120, 36);
            _installBtn.Location = new Point(474, 320);
            _installBtn.BackColor = Color.FromArgb(201, 162, 39);
            _installBtn.ForeColor = Color.FromArgb(26, 21, 9);
            _installBtn.FlatStyle = FlatStyle.Flat;
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += delegate { BeginInstall(); };
            _pageWelcome.Controls.Add(_installBtn);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(90, 36);
            cancel.Location = new Point(374, 320);
            cancel.Click += delegate { Close(); };
            _pageWelcome.Controls.Add(cancel);
        }

        private void BuildProgress()
        {
            _pageProgress = NewPage();

            Label t = new Label();
            t.Text = "正在安装，请稍候…";
            t.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            t.AutoSize = true;
            t.Location = new Point(24, 40);
            _pageProgress.Controls.Add(t);

            _bar = new ProgressBar();
            _bar.Location = new Point(24, 90);
            _bar.Size = new Size(570, 24);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _pageProgress.Controls.Add(_bar);

            _status = new Label();
            _status.Text = "准备中…";
            _status.Location = new Point(24, 126);
            _status.Size = new Size(570, 40);
            _status.ForeColor = Color.FromArgb(110, 105, 95);
            _pageProgress.Controls.Add(_status);
        }

        private void BuildDone()
        {
            _pageDone = NewPage();

            Label t = new Label();
            t.Text = "安装完成";
            t.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(60, 120, 80);
            t.AutoSize = true;
            t.Location = new Point(24, 34);
            _pageDone.Controls.Add(t);

            _doneText = new Label();
            _doneText.Location = new Point(24, 80);
            _doneText.Size = new Size(570, 110);
            _pageDone.Controls.Add(_doneText);

            _launchCheck = new CheckBox();
            _launchCheck.Text = "立即启动「" + SetupInfo.AppNameCn + "」";
            _launchCheck.Checked = true;
            _launchCheck.Location = new Point(24, 200);
            _launchCheck.AutoSize = true;
            _pageDone.Controls.Add(_launchCheck);

            Button finish = new Button();
            finish.Text = "完成";
            finish.Size = new Size(120, 36);
            finish.Location = new Point(474, 320);
            finish.BackColor = Color.FromArgb(201, 162, 39);
            finish.ForeColor = Color.FromArgb(26, 21, 9);
            finish.FlatStyle = FlatStyle.Flat;
            finish.FlatAppearance.BorderSize = 0;
            finish.Click += delegate { Finish(); };
            _pageDone.Controls.Add(finish);
        }

        private void Finish()
        {
            if (_launchCheck.Checked)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(_pathBox.Text.Trim(), SetupInfo.ExeName),
                        WorkingDirectory = _pathBox.Text.Trim()
                    });
                }
                catch { }
            }
            Close();
        }

        /// <summary>静默模式：不显示界面直接安装</summary>
        public int RunSilent()
        {
            string dir = string.IsNullOrEmpty(_silentDir) ? SetupInfo.DefaultDir() : _silentDir;
            _pathBox.Text = dir;
            bool ok = DoInstall(dir, true, true);
            return ok ? 0 : 1;
        }

        private void BeginInstall()
        {
            string dir = _pathBox.Text.Trim();
            if (dir.Length == 0)
            {
                MessageBox.Show(this, "请填写安装位置。", SetupInfo.AppNameCn,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ShowPage(_pageProgress);
            Application.DoEvents();
            bool ok = DoInstall(dir, _desktopCheck.Checked, _menuCheck.Checked);
            if (!ok)
            {
                MessageBox.Show(this, "安装过程中出现错误，请换一个安装位置后重试。",
                    SetupInfo.AppNameCn, MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowPage(_pageWelcome);
                return;
            }
            _doneText.Text = "「" + SetupInfo.AppNameCn + "」已安装到：\r\n"
                + "　" + dir + "\r\n\r\n"
                + (_desktopCheck.Checked ? "· 桌面已创建快捷方式\r\n" : "")
                + (_menuCheck.Checked ? "· 开始菜单已创建快捷方式\r\n" : "")
                + "· 可在「设置 → 应用」中卸载";
            ShowPage(_pageDone);
        }

        private bool DoInstall(string dir, bool desktop, bool startMenu)
        {
            try
            {
                SetProgress(0, "正在准备…");
                int total = Payload.FileCount;
                int done = 0;
                Payload.Extract(dir, delegate(int d, int t, string rel)
                {
                    done = d;
                    int pct = t == 0 ? 100 : (int)(d * 80.0 / t);
                    SetProgress(pct, "正在安装… (" + d + "/" + t + ")  " + rel);
                });

                SetProgress(84, "正在写入程序图标…");
                string icon = Payload.WriteIcon(dir);
                string exePath = Path.Combine(dir, SetupInfo.ExeName);

                SetProgress(88, "正在创建快捷方式…");
                if (desktop)
                {
                    Shell.CreateShortcut(
                        Path.Combine(SetupInfo.DesktopDir(), SetupInfo.AppNameCn + ".lnk"),
                        exePath, dir, icon);
                }
                if (startMenu)
                {
                    Shell.CreateShortcut(
                        Path.Combine(SetupInfo.StartMenuDir(), SetupInfo.AppNameCn + ".lnk"),
                        exePath, dir, icon);
                }

                SetProgress(92, "正在注册卸载信息…");
                // 把自身复制成卸载程序（同一份二进制，带 /uninstall 参数即进入卸载流程）
                try
                {
                    string unins = Path.Combine(dir, SetupInfo.UninstallerName);
                    if (!string.Equals(Application.ExecutablePath, unins, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(Application.ExecutablePath, unins, true);
                    }
                }
                catch { }
                Shell.RegisterUninstall(dir);

                SetProgress(100, "安装完成，共 " + done + " 个文件。");
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "jigu-setup-error.log"),
                        DateTime.Now + "  " + ex + "\r\n", Encoding.UTF8);
                }
                catch { }
                return false;
            }
        }

        private void SetProgress(int pct, string text)
        {
            try
            {
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                _bar.Value = pct;
                _status.Text = text;
                Application.DoEvents();
            }
            catch { }
        }
    }

    /// <summary>卸载向导</summary>
    internal sealed class UninstallForm : Form
    {
        private readonly bool _silent;
        private Label _status;
        private ProgressBar _bar;

        public UninstallForm(bool silent)
        {
            _silent = silent;
            Text = SetupInfo.AppNameCn + " 卸载";
            ClientSize = new Size(520, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(252, 251, 248);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                ShowIcon = Icon != null;
            }
            catch { }

            Label t = new Label();
            t.Text = "卸载「" + SetupInfo.AppNameCn + "」";
            t.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            t.AutoSize = true;
            t.Location = new Point(22, 20);
            Controls.Add(t);

            Label info = new Label();
            info.Text = "将删除程序文件夹、桌面与开始菜单快捷方式，以及用户数据目录。\r\n"
                + "是否继续？";
            info.Location = new Point(24, 62);
            info.Size = new Size(470, 46);
            Controls.Add(info);

            _bar = new ProgressBar();
            _bar.Location = new Point(24, 120);
            _bar.Size = new Size(470, 20);
            _bar.Visible = false;
            Controls.Add(_bar);

            _status = new Label();
            _status.Location = new Point(24, 146);
            _status.Size = new Size(470, 40);
            _status.ForeColor = Color.FromArgb(110, 105, 95);
            Controls.Add(_status);

            Button yes = new Button();
            yes.Text = "卸载";
            yes.Size = new Size(110, 34);
            yes.Location = new Point(384, 196);
            yes.Click += delegate { DoUninstall(); };
            Controls.Add(yes);

            Button no = new Button();
            no.Text = "取消";
            no.Size = new Size(90, 34);
            no.Location = new Point(286, 196);
            no.Click += delegate { Close(); };
            Controls.Add(no);
        }

        public int RunSilent()
        {
            DoUninstall();
            return 0;
        }

        private void DoUninstall()
        {
            _bar.Visible = true;
            _bar.Value = 10;
            _status.Text = "正在删除…";
            Application.DoEvents();

            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            try
            {
                // 快捷方式
                TryDelete(Path.Combine(SetupInfo.DesktopDir(), SetupInfo.AppNameCn + ".lnk"));
                try
                {
                    string sm = SetupInfo.StartMenuDir();
                    if (Directory.Exists(sm)) Directory.Delete(sm, true);
                }
                catch { }
                Shell.RemoveUninstall();
                _bar.Value = 40;

                // 程序目录（先把自己挪到临时目录，避免占用）
                string self = Application.ExecutablePath;
                string tmp = Path.Combine(Path.GetTempPath(), "jigu-unins-" + Guid.NewGuid().ToString("N") + ".exe");
                try { File.Copy(self, tmp, true); } catch { }
                _bar.Value = 60;

                // 安全护栏。安装目录是用户任选的，而下面这条 rmdir /s /q 会把整个目录递归删掉。
                // 如果用户把程序装在了「桌面 / 文档 / 盘根」这类本来就有自己文件的地方，
                // 无条件递归删除就会连他的桌面一起清空 —— 实测安装到桌面后卸载即触发。
                // 所以先确认顶层每一项都是我们装的（子目录只认 data/），确认不了就只删自己的文件。
                string cmd;
                if (IsOurInstallDir(dir))
                {
                    // cmd 自己的当前目录也在安装目录里（继承自启动它的进程，而快捷方式把
                    // 工作目录设成了安装目录），而 Windows 不允许删除任何进程的当前目录——
                    // 不先 cd 走，rmdir 会删光内容却留下一个空目录。
                    cmd = "/c cd /d \"" + Path.GetTempPath() + "\" & ping 127.0.0.1 -n 3 >nul & rmdir /s /q \""
                        + dir + "\" & del /f /q \"" + tmp + "\"";
                    _status.Text = "已卸载。";
                }
                else
                {
                    RemoveOwnFilesOnly(dir);
                    // 卸载程序自身还在运行、删不掉，等进程退出后再删
                    cmd = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \""
                        + Path.Combine(dir, SetupInfo.UninstallerName) + "\" & del /f /q \"" + tmp + "\"";
                    _status.Text = "已移除程序文件。"
                        + "该目录还含其它文件，为安全起见没有删除目录本身。";
                }
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                Process.Start(psi);

                _bar.Value = 100;
            }
            catch (Exception ex)
            {
                _status.Text = "卸载时出现问题：" + ex.Message;
                MessageBox.Show(this, "卸载未完全成功：" + ex.Message + "\r\n可手动删除文件夹：\r\n" + dir,
                    SetupInfo.AppNameCn, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (!_silent) Close();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// 我们装进安装目录的东西，按「相对安装目录、正斜杠分隔」的键记账（如 data/00_种子.json）。
        /// 必须保留相对路径：装载荷的键原样存的是 data/00_种子.json 这种嵌套形式，
        /// 而比对时手里只有文件名（00_种子.json），永远对不上 —— 卸载就会认定
        /// 「目录里不全是我们的东西」，从而留下整份残留。
        /// </summary>
        private static HashSet<string> OurFileNames()
        {
            HashSet<string> ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (KeyValuePair<string, byte[]> kv in InstallerPayload.Table())
                {
                    string key = (kv.Key ?? "").Replace('\\', '/').TrimStart('/');
                    if (key.Length > 0) ours.Add(key);
                }
            }
            catch { }
            // 卸载程序是安装时由安装包自己复制出来的，不在清单里
            ours.Add(SetupInfo.UninstallerName);
            return ours;
        }

        /// <summary>运行期由程序自己生成的文件（同样不是用户的东西）</summary>
        private static bool IsOwnArtifact(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n == "jigu-debug.log" || n == "jigu-debug.log.1" || n == "error.log"
                || n == "app_version.json" || n == "app_version.example.json"
                || n == "data_base.txt"
                || (n.StartsWith("jigu-selfcheck") && n.EndsWith(".txt"));
        }

        /// <summary>
        /// 替换过程中断（断电、强杀）可能留下的副本，形如 稽古.exe.old / corpus.json.tmp。
        /// 只认「本体本来就是我们装的」那种 —— 用户自己起的 notes.tmp 不算我们的东西，
        /// 绝不能因为后缀像就删掉。relKey 与 ours 是同一套键（相对安装目录）。
        /// </summary>
        private static bool IsOwnTempCopy(string relKey, HashSet<string> ours)
        {
            string n = (relKey ?? "").ToLowerInvariant();
            string stem;
            if (n.EndsWith(".old", StringComparison.Ordinal)) stem = n.Substring(0, n.Length - 4);
            else if (n.EndsWith(".tmp", StringComparison.Ordinal)) stem = n.Substring(0, n.Length - 4);
            else return false;
            if (stem.Length == 0) return false;
            return ours.Contains(stem);
        }

        /// <summary>
        /// pending\ 里的东西：待应用的更新包与它的 sidecar 元数据，都是程序自己下进来的。
        /// 只认这两个精确名字（外加原子写的 .tmp）—— 认宽了，用户往里放的文件就会被递归删掉。
        /// </summary>
        private static bool IsOwnPendingEntry(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n == AppUpdater.PendingMetaName.ToLowerInvariant()
                || n == AppUpdater.PendingExeName.ToLowerInvariant()
                || n.EndsWith(".tmp", StringComparison.Ordinal);
        }

        private static bool IsOnlyPendingRecursive(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    if (!IsOwnPendingEntry(Path.GetFileName(f))) return false;
                // pending\ 下不该有子目录；出现了说明拿不准，按既有原则一律不认
                return Directory.GetDirectories(dir).Length == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// corpus\ 里的东西：随包分发的史书分片 —— 一份清单 index.json 加各 <slug>.json。
        /// 它不在负载表里（分片走 /resource: 的 zip，不走 base64 表），所以是单独认的一类，
        /// 与 pending\ 同理。只认「清单里确实列到的 slug」，用户在 corpus\ 里自己放的文件
        /// 一律不认 —— 拿不准就不删，这是这个文件里一贯的规矩。
        /// </summary>
        /// <param name="installDir">安装目录本身（不是 corpus\ 子目录）——清单在
        /// <installDir>\corpus\index.json，传错了就永远读不出书目。</param>
        private static bool IsOnlyCorpusRecursive(string installDir)
        {
            string dir = Path.Combine(installDir, Library.DirName);
            HashSet<string> have = CorpusSlugs(installDir);
            if (have.Count == 0) return false;      // 清单读不出来就当作不是我们的
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string n = Path.GetFileName(f);
                    if (string.Equals(n, Library.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && have.Contains(n.Substring(0, n.Length - 5))) continue;
                    return false;
                }
                return Directory.GetDirectories(dir).Length == 0;
            }
            catch { return false; }
        }

        /// <summary>清单里列到的分片 slug。清单缺失（旧版本装的）时返回空集。</summary>
        private static HashSet<string> CorpusSlugs(string dir)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Library.Book b in Library.Available(dir)) set.Add(b.Slug);
            }
            catch { }
            return set;
        }

        /// <summary>把绝对路径换算成与 OurFileNames() 同一套键（相对安装目录、正斜杠分隔）。</summary>
        private static string RelKey(string root, string fullPath)
        {
            string rel = fullPath.Substring(root.Length);
            return rel.TrimStart('\\', '/').Replace('\\', '/');
        }

        /// <summary>清单里有没有「落在这个子目录下」的东西。</summary>
        private static bool HasKeyUnder(HashSet<string> ours, string dirName)
        {
            string prefix = dirName + "/";
            foreach (string o in ours)
                if (o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 这个文件是不是我们装的。按相对路径比对，所以 data\version.json(我们的)
        /// 与用户自己放在安装目录顶层的 version.json(他的) 不会被混为一谈。
        /// </summary>
        private static bool IsOurs(string root, string file, HashSet<string> ours)
        {
            string rel = RelKey(root, file);
            if (ours.Contains(rel)) return true;
            // 运行期自己生成的文件（日志、自检报告等）只可能出现在安装目录顶层
            if (rel.IndexOf('/') < 0 && IsOwnArtifact(rel)) return true;
            return IsOwnTempCopy(rel, ours);
        }

        /// <summary>
        /// 安装目录里是不是只有我们的东西：顶层每个文件都必须出自我们的清单或由我们生成，
        /// 子目录只认 pending/ 与「清单里确实有东西放在里面」的那个（正常就是 data/），
        /// 且其中每一项也必须是我们的。
        /// 拿不准一律返回 false —— 宁可把文件留下，也不能递归删掉用户的目录。
        /// </summary>
        private static bool IsOurInstallDir(string dir)
        {
            try
            {
                HashSet<string> ours = OurFileNames();
                foreach (string f in Directory.GetFiles(dir))
                    if (!IsOurs(dir, f, ours)) return false;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(d);
                    // pending/ 是「下载好了、还没应用」的更新包。用户点了「稍后」它就会
                    // 一直留着 —— 不认它的话，只要动过一次更新，卸载就再也删不掉安装目录。
                    if (string.Equals(n, AppUpdater.PendingDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsOnlyPendingRecursive(d)) return false;
                    }
                    else if (string.Equals(n, Library.DirName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsOnlyCorpusRecursive(dir)) return false;
                    }
                    else if (HasKeyUnder(ours, n))
                    {
                        if (!IsOnlyOursRecursive(d, dir, ours)) return false;
                    }
                    else return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsOnlyOursRecursive(string dir, string root, HashSet<string> ours)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    if (!IsOurs(root, f, ours)) return false;
                foreach (string d in Directory.GetDirectories(dir))
                    if (!IsOnlyOursRecursive(d, root, ours)) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>目录归用户所有时的降级卸载：只删我们的文件，绝不删目录本身。</summary>
        private static void RemoveOwnFilesOnly(string dir)
        {
            try
            {
                HashSet<string> ours = OurFileNames();
                foreach (string f in Directory.GetFiles(dir))
                    if (IsOurs(dir, f, ours)) TryDelete(f);
                string data = Path.Combine(dir, "data");
                if (Directory.Exists(data) && IsOnlyOursRecursive(data, dir, ours))
                {
                    try { Directory.Delete(data, true); } catch { }
                }
                // 待应用的更新包也归我们。以前这里漏了它，导致用户一旦点过「稍后」，
                // 卸载就会把一份完整的安装包留在人家的磁盘上。
                string pending = Path.Combine(dir, AppUpdater.PendingDirName);
                if (Directory.Exists(pending) && IsOnlyPendingRecursive(pending))
                {
                    try { Directory.Delete(pending, true); } catch { }
                }
                // 史书分片同样归我们：它是随安装包解出来的，留着就是上百兆的残留
                string corpus = Path.Combine(dir, Library.DirName);
                if (Directory.Exists(corpus) && IsOnlyCorpusRecursive(dir))
                {
                    try { Directory.Delete(corpus, true); } catch { }
                }
            }
            catch { }
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // ---- 热替换入口：把本安装包的内容解压到指定目录（供自动更新使用） ----
            // 用法：稽古-安装包.exe --extract-to "安装目录" [--sha256 <期望值>]
            if (args != null && args.Length >= 2 && args[0] == "--extract-to")
            {
                string dest = args[1].Trim('"');
                if (args.Length >= 4 && args[2] == "--sha256")
                {
                    string want = args[3];
                    string mine = Net.Sha256File(Application.ExecutablePath);
                    if (!string.Equals(want, mine, StringComparison.OrdinalIgnoreCase))
                    {
                        Environment.ExitCode = 3;
                        return;
                    }
                }
                int failed;
                int n = InstallerPayload.ExtractTo(dest, out failed);
                // 同 AppMain：2 = 写了一部分，调用方据此判断这次释放不算成功
                Environment.ExitCode = failed > 0 ? 2 : (n > 0 ? 0 : 1);
                return;
            }

            // ---- 静默安装：稽古-安装包.exe /silent /dir=<目录> ----
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstall = false, silent = false;
            string dir = null;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "/uninstall" || s == "-uninstall") uninstall = true;
                else if (s == "/silent" || s == "-silent" || s == "/s") silent = true;
                else if (s.StartsWith("/dir=")) dir = a.Substring(5).Trim('"');
            }

            try
            {
                if (uninstall)
                {
                    UninstallForm u = new UninstallForm(silent);
                    if (silent) { Environment.ExitCode = u.RunSilent(); return; }
                    Application.Run(u);
                    return;
                }

                SetupForm f = new SetupForm(silent, dir);
                if (silent) { Environment.ExitCode = f.RunSilent(); return; }
                Application.Run(f);
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show("安装程序出错：" + ex.Message, SetupInfo.AppNameCn,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
        }
    }
}
