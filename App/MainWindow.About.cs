using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HonorControl;

public sealed partial class MainWindow
{
    internal const string Version = "0.14.5";
    private bool _guideOpen;
    private static string WelcomeRecord => Path.Combine(AppPaths.UserState, "welcome-version.txt");

    private void InitializeAbout()
    {
        AboutVersion.Text = "Honor Control " + Version;
        FooterVersion.Text = "Honor Control " + Version;
        Navigation.OpenPaneLength = L.English ? 250 : 202;
        AboutDataText.Text = L.T("日志与恢复记录保存在本机 LocalAppData\\HonorControl。") + Environment.NewLine +
            L.T("运行日志每份最多 2 MiB，每类最多 3 份，超过 14 天未更新的日志自动清理。偏好、恢复记录与校准备份保留。");
        LanguagePicker.SelectedIndex = L.English ? 1 : 0;
    }

    private async Task ShowStartupGuideAsync(bool force = false, bool changes = false)
    {
        if (!_ready || _closing || _guideOpen || Root.XamlRoot is null) return;
        bool first = !File.Exists(WelcomeRecord);
        if (!force && !first && File.ReadAllText(WelcomeRecord).Trim() == Version) return;
        _guideOpen = true;
        try
        {
            string migration = await Task.Run(ReadMigrationStatus);
            var tabs = new Pivot();
            tabs.Items.Add(GuideTab(L.T("开始使用"), new[]
            {
                (L.T("1 · 安装与驱动"), L.T("双击 Setup 安装程序，授权一次 UAC。安装到 Program Files，登录后启动托盘和设备控制。升级保留偏好与本机校准数据。保留 Intel、NVIDIA、Realtek、Fortemedia 与 Histen 等硬件驱动。")),
                (L.T("2 · 避免重复控制"), L.T("卸载荣耀电脑管家及独立 OSD、HUNTERCAMP 等软件后台。不要卸载音频 APO、芯片组或 ACPI 驱动。详细步骤见安装指南。")),
                (L.T("3 · 按需开启自动控制"), L.T("自动性能、进程调度与 OS Turbo 分别控制模式、进程 QoS 和场景功耗。首次安装默认关闭，升级保留设置。HUNTER 档位仅在 HUNTER 模式及供电满足条件时可调。")),
                (L.T("性能模式与档位"), L.T("在「性能与散热」直接选择智能、高能或 HUNTER。高能要求插电且电量至少 20%；HUNTER 要求圆口或至少 135 W PD。HUNTER 下可选风扇预设，200 W 电源开放 4、5 档。界面显示固件读回档位，转速随负载变化。")),
                (L.T("自动性能如何工作"), L.T("开启「自动性能模式」后，识别到游戏进程就选择供电允许的最高模式。切到桌面不降档；进程消失后保留 45 秒，再等待场景稳定。创作场景选高能，其他选智能。手动切换成功后自动开关关闭，受限切换保留原设置并提示原因。")),
                (L.T("进程调度与 OS Turbo"), L.T("「进程调度」控制进程 HighQoS、EcoQoS 和少量游戏核心分配；可选择前后台程序，或开启自动识别。「OS Turbo 场景调度」控制 EPP、DTT、风扇表与显卡频偏。HUNTER 下 EPP 暂停，功耗和风扇交给固件。三个开关可分别使用。")),
                (L.T("色彩与刷新率"), L.T("在「显示与色彩」选择原生、sRGB 或 P3，同时更新硬件 LUT 和 ICC。仅改 ICC 不会改变全屏输出色域。安装器可自动导入匹配的本机原厂校准，当前已验证内屏 SDR。刷新率下拉框直接选目标值；Fn+R 则循环支持的刷新率。")),
                (L.T("音效与麦克风"), L.T("在「音频」分别选择外放、耳机音效与麦克风拾音。空间音频依赖 Histen，单人、多人、环境音依赖 Fortemedia 服务和阵列驱动。拾音模式不等于麦克风静音；静音功能键和指示灯单独工作。")),
                (L.T("显卡与充电"), L.T("自动显卡策略插电保留双显卡，电池供电时等独显空闲 30 秒再尝试关闭，占用期间等待。强制集显可能中断独显任务。家用、办公、差旅分别设置充电起止阈值；达到停止阈值后不会强制放电。")),
                (L.T("网络功能"), L.T("场景网络规则只影响出站优先标记和已识别下载器的后台上传，效果取决于网络设备。WLAN 只连接已有凭据的网络，游戏与会议期间暂停弱网切换。不提供浏览器下载接管或手机协同。")),
                (L.T("常驻与快捷键"), L.T("关闭窗口后继续托盘常驻。Fn+P 切换固件模式，Fn+R 切换内屏刷新率；F1–F12 默认行为在设置页选择，备用组合键可自定义。亮度、音量不显示本应用浮窗。右键托盘退出可暂停本次登录会话。")),
                (L.T("恢复与记录"), L.T("关闭对应自动控制后，按恢复记录还原本应用修改的设置。「恢复首次备份」还原首次保存的硬件色彩和 ICC。日志保存在 LocalAppData\\HonorControl，设置页可打开。升级保留设置；关于页可重看教程和更新记录。"))
            }));
            tabs.Items.Add(GuideTab(L.T("官方组件迁移"), new[]
            {
                (L.T("本机检测"), migration),
                (L.T("可以卸载的应用"), L.T("荣耀电脑管家 → 管家与更新后台；OSD → 原厂功能键浮窗；HUNTERCAMP → 游戏控制后台；Magic 视界 → 动态界面服务；荣耀 YOYO 助理 → AI 助理与推理后台。可先在 Windows「已安装的应用」按这些名称卸载。")),
                (L.T("对应的软件服务名"), L.T("MBAMainService：电脑管家主服务\nGuardService：管家守护\nHnUpdateService：荣耀软件更新\nOSDServer：原厂 OSD\nHunterCampService：HUNTERCAMP\nMagicAnimationService：Magic 视界\nHnPCAIService、HnPCInferEngine：AI 与推理\nQAXDefender.SDK：仅清理安装路径位于 HONOR\\PCManager\\qspublic 下的捆绑组件。")),
                (L.T("必须保留的硬件组件"), L.T("保留 Fortemedia 的 FMAPOService、Realtek 音频服务、Histen / Honor 音频 APO、Awinic、Intel Smart Sound、Intel Dynamic Tuning、Intel / NVIDIA 显卡、ACPI、HID 与摄像头驱动。不要只按厂商名删除驱动。ProgramData\\HonorEffect 是音频校准数据，应保留。")),
                (L.T("残留怎么处理"), L.T("优先使用应用卸载入口。Clean-HonorSoftware.ps1 默认只列清理计划，核对后再以管理员运行并加 -Apply。脚本记录软件目录、注册项、服务、自启动和应用包的处理结果，不删除硬件驱动。关于页的安装指南有完整命令与恢复步骤。"))
            }));
            tabs.Items.Add(GuideTab(L.T("更新记录"), new[]
            {
                (Version, L.T("安装时自动检查并导入受支持内屏的本机原厂 ICC 和 LUT。安装完成页会显示校准结果。")),
                (L.T("游戏与自动性能"), L.T("补充 CS2、英雄联盟、PUBG、原神和星铁等主进程。游戏运行时保持性能模式，排除启动器和辅助进程误触发。游戏进程消失后保留 45 秒缓冲。")),
                (L.T("音频与提示"), L.T("增加 Histen 外放、耳机空间音频及单人、多人、环境拾音模式。浮窗按功能使用图标，性能切换受限时显示原因。"))
            }));
            tabs.Items.Add(GuideTab(L.T("支持与须知"), new[]
            {
                (L.T("已验证设备"), L.T("实机验证为 DRA-XX / BIOS 1.13 / 125H + RTX 4060。同型号其他配置按功能判断，未承诺整机完整兼容；相同 CPU、显卡的其他型号不适用。")),
                (L.T("同型号不同配置"), L.T("固件控制按 DRA-XX 与 BIOS 1.13 检查，仍需其他配置实测。DTT 场景写入限 125H；NVIDIA 频偏限 RTX 4060 Laptop GPU；部分游戏核心规则含 125H 与 155H。色彩依赖对应物理面板，音频依赖实际驱动与端点。")),
                (L.T("功能范围"), L.T("性能与风扇、充电保护、Fn、刷新率、SDR 色彩、音频及场景调度。色彩需要本机校准文件；空间音频和拾音需要对应驱动与校准数据。")),
                (L.T("已知限制"), L.T("未覆盖原厂核显锁频、后台 CPU 硬配额和游戏配置文件修改。电池策略、多网络叠加、HDR 与外接多屏未完成全面验证。手机协同不在当前范围。")),
                (L.T("许可与记录"), L.T("源码仅供非商业个人研究使用。设备操作与恢复记录保存在本机，不上传日志。关于页可查看源码、许可、安装指南和更新记录。"))
            }));
            tabs.SelectedIndex = changes || (!first && !force) ? 2 : 0;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme,
                Title = L.T("Honor Control · 使用说明"),
                Content = tabs,
                CloseButtonText = L.T("知道了"),
                DefaultButton = ContentDialogButton.Close
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            await dialog.ShowAsync();
            Directory.CreateDirectory(AppPaths.UserState);
            File.WriteAllText(WelcomeRecord, Version);
        }
        catch (Exception ex) { Log(Friendly(ex)); }
        finally { _guideOpen = false; }
    }

    private static string ReadMigrationStatus()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MBAMainService", "GuardService", "HnUpdateService", "OSDServer", "HunterCampService", "MagicAnimationService", "HnPCAIService", "HnPCInferEngine", "QAXDefender.SDK" };
            using var query = new System.Management.ManagementObjectSearcher("SELECT Name,PathName FROM Win32_Service");
            using var result = query.Get();
            var found = result.Cast<System.Management.ManagementObject>().Where(item =>
                names.Contains(Convert.ToString(item["Name"]) ?? "") &&
                ((Convert.ToString(item["PathName"]) ?? "").Contains("\\HONOR\\", StringComparison.OrdinalIgnoreCase) ||
                 (Convert.ToString(item["PathName"]) ?? "").Contains("\\RPC\\OSD\\", StringComparison.OrdinalIgnoreCase)))
                .Select(item => Convert.ToString(item["Name"])).ToArray();
            return found.Length == 0 ? L.T("上述荣耀软件服务均未检测到。下方仍列出具体名称，便于迁移其他设备时核对。")
                : L.T("检测到的软件服务：") + string.Join(", ", found);
        }
        catch { return L.T("无法读取服务清单，请按下方名称在 Windows 服务和已安装的应用中核对。"); }
    }

    private static PivotItem GuideTab(string title, IEnumerable<(string Title, string Text)> sections)
    {
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(0, 12, 16, 12), MaxWidth = 600 };
        foreach (var section in sections)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(new TextBlock { Text = L.T(section.Title), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            block.Children.Add(new TextBlock { Text = L.T(section.Text), Style = (Style)Application.Current.Resources["CaptionLabel"], FontSize = 14, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(block);
        }
        return new PivotItem { Header = L.T(title), Content = new ScrollViewer { Content = panel, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    private async void Guide_Click(object sender, RoutedEventArgs e) => await ShowStartupGuideAsync(force: true);
    private async void Changes_Click(object sender, RoutedEventArgs e) => await ShowStartupGuideAsync(force: true, changes: true);
    private void Repository_Click(object sender, RoutedEventArgs e) => OpenDocument("https://github.com/wzk112/HonorControl");
    private void License_Click(object sender, RoutedEventArgs e) => OpenDocument("https://github.com/wzk112/HonorControl/blob/main/LICENSE");
    private void InstallGuide_Click(object sender, RoutedEventArgs e) => OpenDocument("https://github.com/wzk112/HonorControl/blob/main/INSTALL." + (L.English ? "en-US" : "zh-CN") + ".md");

    private void OpenDocument(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex) { Show(Friendly(ex), InfoBarSeverity.Error); }
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        string choice = LanguagePicker.SelectedIndex == 1 ? "en-US" : "zh-CN";
        Directory.CreateDirectory(AppPaths.UserState);
        File.WriteAllText(Path.Combine(AppPaths.UserState, "language.txt"), choice);
        RestartLanguageButton.Visibility = choice == L.Language ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestartLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _guideOpen) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--restart" } });
            _quitRequested = true;
            Close();
            Application.Current.Exit();
        }
        catch (Exception ex) { Show(Friendly(ex), InfoBarSeverity.Error); }
    }
}
