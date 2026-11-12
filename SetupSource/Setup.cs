using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Honor Control Setup")]
[assembly: AssemblyProduct("Honor Control")]
[assembly: AssemblyVersion("0.14.4.0")]
[assembly: AssemblyFileVersion("0.14.4.0")]
[assembly: AssemblyCopyright("Copyright 2026 wzk112")]

internal static class SetupProgram
{
    internal const string Version = "0.14.4";
#if UNINSTALL
    internal static readonly bool Uninstall = true;
#else
    internal static readonly bool Uninstall = false;
#endif
    internal static readonly string InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HonorControl");
    [STAThread]
    private static void Main()
    {
        if (Uninstall && Path.GetDirectoryName(Application.ExecutablePath).Equals(InstallPath, StringComparison.OrdinalIgnoreCase))
        {
            string directory = Path.Combine(Path.GetTempPath(), "HonorControl-Uninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string copy = Path.Combine(directory, "HonorControl.Uninstall.exe");
            File.Copy(Application.ExecutablePath, copy);
            bool silent = Array.Exists(Environment.GetCommandLineArgs(), value => value.Equals("/SILENT", StringComparison.OrdinalIgnoreCase));
            Process.Start(new ProcessStartInfo(copy, silent ? "/SILENT" : "") { UseShellExecute = false, WorkingDirectory = directory });
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private bool english = !Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private bool working, complete;
    private readonly bool silent = Array.Exists(Environment.GetCommandLineArgs(), value => value.Equals("/SILENT", StringComparison.OrdinalIgnoreCase));
    private readonly Label title = new Label(), subtitle = new Label(), path = new Label(), status = new Label();
    private readonly FlowLayoutPanel language = new FlowLayoutPanel();
    private readonly TextBox notes = new TextBox();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Button install = new Button(), cancel = new Button(), license = new Button();
    private readonly CheckBox open = new CheckBox();
    private string TextFor(string chinese, string en) { return english ? en : chinese; }

    public SetupForm()
    {
        SuspendLayout();
        Text = "Honor Control Setup";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(670, 510);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(28, 31, 38); ForeColor = Color.FromArgb(240, 243, 249);
        title.AutoSize = true;
        title.Font = new Font("Segoe UI", 23, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 12, 8);
        subtitle.AutoSize = true;
        subtitle.Margin = new Padding(0, 0, 0, 8);
        language.AutoSize = true;
        language.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        language.WrapContents = false;
        language.Margin = new Padding(0, 0, 0, 8);
        language.Anchor = AnchorStyles.Right;
        language.AccessibleName = "语言 / Language";
        var chineseOption = new RadioButton { Text = "简体中文", AutoSize = true, Checked = !english };
        var englishOption = new RadioButton { Text = "English", AutoSize = true, Checked = english };
        englishOption.CheckedChanged += delegate { english = englishOption.Checked; UpdateText(); };
        language.Controls.Add(chineseOption);
        language.Controls.Add(englishOption);
        path.AutoSize = true;
        path.Margin = new Padding(0, 0, 0, 18);
        path.ForeColor = Color.FromArgb(170, 196, 228);
        notes.Dock = DockStyle.Fill;
        notes.Margin = Padding.Empty;
        notes.Multiline = true; notes.ReadOnly = true; notes.BorderStyle = BorderStyle.None;
        notes.ScrollBars = ScrollBars.Vertical; notes.BackColor = BackColor; notes.ForeColor = Color.FromArgb(192, 201, 215);
        status.AutoSize = true;
        status.Margin = new Padding(0, 12, 0, 6);
        progress.Dock = DockStyle.Fill;
        progress.Height = 8;
        progress.Margin = new Padding(0, 0, 0, 12);
        progress.Maximum = 100;
        open.AutoSize = true;
        open.Margin = new Padding(0, 0, 0, 8);
        open.Checked = true; open.Visible = false;
        foreach (var button in new[] { install, cancel, license })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(14, 7, 14, 7);
            button.Margin = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(71, 80, 96);
        }
        install.MinimumSize = new Size(108, 0);
        install.Margin = new Padding(0, 0, 8, 0);
        install.BackColor = Color.FromArgb(109, 185, 244); install.ForeColor = Color.FromArgb(17, 29, 44);
        install.Click += async delegate { if (complete) { if (open.Checked) OpenApplication(); Close(); } else await RunAsync(); };
        cancel.Click += delegate { Close(); };
        license.Click += delegate { ShowLicense(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8,
            Padding = new Padding(26, 20, 26, 20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(row == 3 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(language, 1, 0);
        layout.SetColumnSpan(language, 2);
        layout.Controls.Add(subtitle, 0, 1);
        layout.Controls.Add(path, 0, 2);
        layout.Controls.Add(notes, 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(progress, 0, 5);
        layout.Controls.Add(open, 0, 6);
        foreach (var control in new Control[] { subtitle, path, notes, status, progress, open })
            layout.SetColumnSpan(control, 3);
        layout.Controls.Add(license, 0, 7);
        layout.Controls.Add(install, 1, 7);
        layout.Controls.Add(cancel, 2, 7);
        Controls.Add(layout);
        CancelButton = cancel;
        UpdateText();
        ResumeLayout(true);
        Shown += async delegate
        {
            FitToWorkingArea();
            if (silent) { await RunAsync(); Close(); }
        };
    }

    private void FitToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        float scale = CurrentAutoScaleDimensions.Width / 96f;
        int inset = (int)Math.Round(16 * scale);
        var available = new Size(area.Width - 2 * inset, area.Height - 2 * inset);
        var fitted = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        MinimumSize = new Size(Math.Min((int)Math.Round(520 * scale), fitted.Width),
            Math.Min((int)Math.Round(390 * scale), fitted.Height));
        Bounds = new Rectangle(area.Left + (area.Width - fitted.Width) / 2,
            area.Top + (area.Height - fitted.Height) / 2, fitted.Width, fitted.Height);
    }

    private void UpdateText()
    {
        title.Text = "Honor Control";
        subtitle.Text = (SetupProgram.Uninstall ? TextFor("卸载", "Uninstall") : TextFor("安装 / 升级", "Install / upgrade")) + " · " + SetupProgram.Version + " · Windows x64";
        path.Text = SetupProgram.InstallPath;
        notes.Text = SetupProgram.Uninstall
            ? TextFor("将停止登录任务，退出托盘并恢复自动控制所修改的设置，然后移除应用。\r\n\r\n个人偏好、日志、色彩备份与校准数据保留在 LocalAppData\\HonorControl。不会卸载硬件驱动。", "Stops sign-in tasks, exits the tray, restores automatic-control changes and removes the application.\r\n\r\nPreferences, logs, color backups and calibration stay in LocalAppData\\HonorControl. Hardware drivers are retained.")
            : TextFor("安装到 Program Files，创建开始菜单入口并注册登录启动。升级保留个人设置和本机校准文件。\r\n\r\n已验证 DRA-XX / BIOS 1.13 / 125H + RTX 4060。其他配置按功能判断。公共包不含本机 ICC/LUT，色彩切换需导入对应内屏数据。\r\n\r\n首次打开有具体使用教程和官方组件迁移清单。保留 Intel、NVIDIA、Realtek、Fortemedia、Histen 等硬件驱动。\r\n\r\n仅供非商业个人使用与研究。禁止商用。", "Installs in Program Files with a Start menu entry and sign-in startup. Upgrades preserve preferences and local calibration.\r\n\r\nVerified on DRA-XX / BIOS 1.13 / 125H + RTX 4060. Other configurations require feature-specific validation. Public packages omit panel ICC/LUT data; color switching requires local import.\r\n\r\nThe first-launch tutorial includes usage steps and an OEM migration checklist. Keep Intel, NVIDIA, Realtek, Fortemedia and Histen hardware drivers.\r\n\r\nNoncommercial personal use and research only. Commercial use prohibited.");
        if (!working) status.Text = complete ? TextFor("已完成。", "Completed.") : TextFor("准备就绪", "Ready");
        install.Text = complete ? TextFor("完成", "Finish") : SetupProgram.Uninstall ? TextFor("卸载", "Uninstall") : TextFor("安装", "Install");
        cancel.Text = TextFor("关闭", "Close"); license.Text = TextFor("许可协议", "License"); open.Text = TextFor("打开 Honor Control", "Open Honor Control");
    }

    private void ShowLicense()
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("License"))
        using (var reader = new StreamReader(stream))
        using (var dialog = new Form { Text = "License", Size = new Size(720, 600), StartPosition = FormStartPosition.CenterParent })
        {
            dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = reader.ReadToEnd(), Font = new Font("Segoe UI", 10) });
            dialog.ShowDialog(this);
        }
    }

    private async Task RunAsync()
    {
        working = true; install.Enabled = false; cancel.Enabled = false; language.Enabled = false;
        try
        {
            await Task.Run((Action)Perform);
            complete = true; working = false; progress.Value = 100; open.Visible = !SetupProgram.Uninstall;
            UpdateText();
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            status.Text = TextFor("未完成。请查看错误信息。", "Incomplete. See the error details.");
            if (!silent) MessageBox.Show(this, ex.Message, "Honor Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else { string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorControl", "Setup"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "setup-error.log"), ex.ToString()); }
        }
        finally { working = false; install.Enabled = true; cancel.Enabled = true; language.Enabled = true; }
    }

    private void Report(int value, string zh, string en)
    {
        BeginInvoke((Action)delegate { progress.Value = value; status.Text = TextFor(zh, en); });
    }

    private void Perform()
    {
        string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorControl", "Setup");
        Directory.CreateDirectory(logDirectory);
        if (SetupProgram.Uninstall)
        {
            Report(20, "正在停止并恢复设备控制…", "Stopping and restoring device control…");
            RunScript(Path.Combine(SetupProgram.InstallPath, "Uninstall.ps1"), "", logDirectory);
            Report(100, "卸载完成", "Uninstalled"); return;
        }
        string staging = Path.Combine(Path.GetTempPath(), "HonorControl-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Report(2, "正在校验安装包…", "Verifying the package…");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Package"))
            using (var expectedStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PackageHash"))
            using (var reader = new StreamReader(expectedStream))
            using (var sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(resource)).Replace("-", "");
                if (!hash.Equals(reader.ReadToEnd().Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package checksum mismatch.");
                resource.Position = 0;
                using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                {
                    int index = 0;
                    foreach (var entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid package path.");
                        if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                        Report(5 + (++index * 65 / archive.Entries.Count), "正在解压安装文件…", "Extracting installation files…");
                    }
                }
            }
            Report(75, "正在安装并注册登录启动…", "Installing and registering sign-in startup…");
            string languagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorControl", "language.txt");
            if (!File.Exists(languagePath)) File.WriteAllText(languagePath, english ? "en-US" : "zh-CN");
            RunScript(Path.Combine(staging, "Install.ps1"), "", logDirectory);
            Report(100, "安装完成", "Installed");
        }
        finally
        {
            // Only the exact staging directory generated by this invocation is removed.
            if (Path.GetFullPath(staging).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(staging, true); } catch { }
        }
    }

    private static void RunScript(string script, string arguments, string logDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + arguments)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(), RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(output, error);
            string log = output.Result + Environment.NewLine + error.Result;
            File.WriteAllText(Path.Combine(logDirectory, SetupProgram.Uninstall ? "uninstall.log" : "install.log"), log);
            if (process.ExitCode != 0) throw new InvalidOperationException(log.Length > 2000 ? log.Substring(log.Length - 2000) : log);
        }
    }

    private static void OpenApplication()
    {
        try { using (var signal = EventWaitHandle.OpenExisting("Local\\HonorControl.Show." + WindowsIdentity.GetCurrent().User.Value)) signal.Set(); } catch { }
    }
}
