using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using HonorControl.Shared;
using Microsoft.Win32;

namespace HonorControl;

public sealed partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private readonly ObservableCollection<string> _activity = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _ready, _busy, _refreshing, _syncingSwitches, _active = true, _closing;
    private readonly OverlayWindow _overlay = new();
    private TrayIcon? _tray;
    private readonly DispatcherTimer _residentTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _watcherDesired, _rhythmDesired, _residentBusy, _quitRequested;
    private int _mode = -1;
    private int _modeRevision;
    private int _currentFanLevel;
    private int _availableFanLevels;
    private string _color = "";

    public MainWindow()
    {
        InitializeComponent();
        InitializeAbout();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HonorControl.ico"));
        SystemBackdrop = new MicaBackdrop();
        Root.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        UpdateTitleBarTheme();
        AppWindow.Resize(new SizeInt32(1220, 860));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = true;
        ActivityList.ItemsSource = _activity;
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), TrayAction);
        AppWindow.Closing += (_, args) => { if (!_quitRequested) { args.Cancel = true; AppWindow.Hide(); } };
        _residentTimer.Tick += async (_, _) => await MaintainResidentAsync();
        Root.Loaded += async (_, _) =>
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int width = (int)Math.Min(1220 * scale, work.Width * .94);
            int height = (int)Math.Min(860 * scale, work.Height * .92);
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2));
            _ready = true;
            LoadScheduler();
            LoadHotkeys();
            StartFirmwareKeys();
            ResidentStatusText.Text = AppPaths.Installed ? L.T("已安装 · 登录自动启动 · 后台保活已启用") : L.T("便携运行 · 关闭窗口后保留托盘");
            _watcherDesired = AppPaths.Installed && (!File.Exists(Path.Combine(AppPaths.UserState, "color-watch-enabled.txt")) || File.ReadAllText(Path.Combine(AppPaths.UserState, "color-watch-enabled.txt")).Trim() != "0");
            _rhythmDesired = File.Exists(RhythmPreference) && File.ReadAllText(RhythmPreference).Trim() == "1";
            if (AppPaths.BackgroundLaunch) AppWindow.Hide();
            await RunAsync(async () =>
            {
                await RefreshColorAsync(); ReadColorPreferences();
                await ReadRefreshRateAsync();
                if (AppPaths.Installed || AppPaths.ExistingPipeName is not null)
                {
                    UpdateFirmware(await _backend.HardwareAsync("firmware-status"));
                    UpdateCharge(await _backend.HardwareAsync("charge-status"));
                    UpdateGpu(await _backend.GpuStatusAsync(), true);
                    UpdateAutomaticPerformance(await _backend.PerformanceStatusAsync());
                    UpdateDtt(await _backend.DttStatusAsync());
                    UpdateBoost(await _backend.BoostStatusAsync());
                    await ReadKeyboardModeAsync(restorePreference: true);
                }
            }, null);
            Log(L.T("应用已启动。色彩控制可直接使用。"));
            _timer.Start();
            if (AppPaths.Installed) { _residentTimer.Start(); await MaintainResidentAsync(); }
            if (!AppPaths.BackgroundLaunch) await ShowStartupGuideAsync();
        };
        Activated += async (_, e) => { _active = e.WindowActivationState != WindowActivationState.Deactivated; if (!_active) _hotkeys?.Resume(); else if (_capturingHotkey) _hotkeys?.Suspend(); if (_active && _ready && DisplayPage.Visibility == Visibility.Visible) await ReadRefreshRateAsync(); };
        _timer.Tick += async (_, _) =>
        {
            if (!_closing) RefreshScheduler();
            if (_busy || _refreshing || _closing) return;
            if (!_backend.IsConnected) { await RefreshScenePowerAsync(); return; }
            _refreshing = true;
            try
            {
                if (AppPaths.Installed || _active || ScenePowerToggle.IsOn)
                {
                    int previous = _mode;
                    int revision = _modeRevision;
                    bool performanceVisible = _active && PerformancePage.Visibility == Visibility.Visible;
                    var snapshot = await _backend.HardwareAsync(performanceVisible ? "firmware-status" : "firmware-mode");
                    if (_busy || _closing || revision != _modeRevision) return;
                    bool needsHunterPreset = !performanceVisible && previous != 3 && snapshot.Number("ModeCode") == 3;
                    if (needsHunterPreset)
                    {
                        snapshot = await _backend.HardwareAsync("firmware-status");
                        if (_busy || _closing || revision != _modeRevision) return;
                    }
                    if (performanceVisible || needsHunterPreset) UpdateFirmware(snapshot);
                    else UpdateMode(snapshot.Number("ModeCode"));
                    UpdateSelections();
                    if (previous is 0 or 1 or 3 && _mode is 0 or 1 or 3 && previous != _mode)
                    {
                        PresentModeOverlay(_mode);
                        Log(L.T("检测到固件性能模式切换为") + ModeName(_mode) + "。");
                    }
                    await RefreshScenePowerAsync(_mode);
                    if (performanceVisible) WindowsPowerPolicyText.Text = PowerPolicySnapshot.Read();
                    if (performanceVisible && !_busy) UpdateGpu(await _backend.GpuStatusAsync());
                    if (!_busy) UpdateAutomaticPerformance(await _backend.PerformanceStatusAsync());
                    if (performanceVisible && !_busy) UpdateDtt(await _backend.DttStatusAsync());
                    if (performanceVisible && !_busy) UpdateBoost(await _backend.BoostStatusAsync());
                }
                if (_active && !_busy && NetworkPage.Visibility == Visibility.Visible)
                {
                    try { UpdateNetwork(await _backend.NetworkStatusAsync()); UpdateDownloadChannels(); }
                    catch (Exception ex) { NetworkPolicyText.Text = Friendly(ex); }
                }
                if (_active && !_busy && AudioPage.Visibility == Visibility.Visible)
                    await RefreshAudioAsync();
            }
            catch (Exception ex)
            {
                await RefreshScenePowerAsync(-1);
                UpdateConnection();
                if (_active) Show(Friendly(ex), InfoBarSeverity.Warning);
            }
            finally { _refreshing = false; }
        };
        Closed += async (_, _) => { _closing = true; _timer.Stop(); _residentTimer.Stop(); _hotkeys?.Dispose(); _firmwareKeys?.Dispose(); try { _scenePower?.Restore(); } catch (Exception ex) { Log(Friendly(ex)); } try { _scheduler?.RestoreAll(); } catch (Exception ex) { Log(Friendly(ex)); } _tray?.Dispose(); _overlay.DismissAndClose(); await _backend.DisposeAsync(); };
    }

    private async Task RunAsync(Func<Task> operation, string? success, bool switchFeedback = false)
    {
        if (_busy || _closing) return;
        _busy = true; BusyRing.IsActive = true; BusyRing.Visibility = Visibility.Visible;
        try
        {
            await operation();
            if (success is not null) { Show(success, InfoBarSeverity.Success); Log(success); }
        }
        catch (Exception ex)
        {
            string message = Friendly(ex);
            Show(message, InfoBarSeverity.Error); Log(message);
            if (switchFeedback) _overlay.Present(L.T("性能模式未切换"), message, OverlayIcon.Warning);
        }
        finally
        {
            _busy = false; BusyRing.IsActive = false; BusyRing.Visibility = Visibility.Collapsed;
            UpdateConnection(); UpdateSelections();
        }
    }
    private void Show(string message, InfoBarSeverity severity)
    {
        Notice.Message = L.T(message); Notice.Severity = severity; Notice.IsOpen = true;
    }
    private void Log(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  ·  " + message;
        _activity.Insert(0, line); while (_activity.Count > 30) _activity.RemoveAt(_activity.Count - 1);
        HonorControl.Diagnostics.DiagnosticLog.Append(Path.Combine(AppPaths.UserState, "activity.log"), DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
    }
    private static string Friendly(Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }) return L.T("已取消管理员授权。色彩功能仍可使用。");
        string text = ex.Message;
        if (text.Contains("HUNTER requires", StringComparison.Ordinal)) return L.T("HUNTER 需要圆口电源或至少 135 W 的 PD 电源。");
        if (text.Contains("presets 4 and 5 require", StringComparison.Ordinal)) return L.T("HUNTER 第 4、5 档需要支持的 200 W 电源。");
        if (text.Contains("Display effects require SDR", StringComparison.Ordinal)) return L.T("显示效果仅支持 SDR。请先关闭 HDR 或 Windows 自动色彩管理。");
        if (text.Contains("AC power", StringComparison.OrdinalIgnoreCase)) return L.T("请连接电源，并确保电量至少为 20%。");
        if (text.Contains("single internal", StringComparison.OrdinalIgnoreCase)) return L.T("当前色彩功能只支持本机单内置屏布局，请先断开其他显示器。");
        if (text.Contains("BIOS 1.13", StringComparison.OrdinalIgnoreCase)) return L.T("当前固件版本不在已验证范围；已停止写入。");
        if (text.Contains("Choose sRGB", StringComparison.OrdinalIgnoreCase)) return L.T("请先选择一次原生、sRGB 或 P3，再开启自动色彩恢复。");
        if (text.Contains("Install the bundled ICC", StringComparison.OrdinalIgnoreCase)) return L.T("系统中的 ICC 文件缺失。请到设置页点击“安装 / 检查 ICC”。");
        return L.T(text);
    }
    private void UpdateConnection()
    {
        ConnectLabel.Text = _backend.IsConnected ? L.T("硬件控制已启用") : L.T("启用硬件控制");
        ConnectButton.IsEnabled = !_backend.IsConnected && !_busy;
    }
    private static string ModeName(int mode) => mode switch { 0 => L.T("智能"), 1 => L.T("高能"), 3 => "HUNTER", _ => L.T("等待连接") };
    private void UpdateMode(int mode)
    {
        _mode = mode;
        CurrentModeText.Text = ModeName(mode);
        ModeFootnote.Text = mode is 0 or 1 or 3 ? L.T("已从设备固件读取确认") : L.T("连接后读取实际模式");
    }
    private void UpdateFirmware(JsonElement value)
    {
        if (value.TryGetProperty("Status", out var status)) value = status;
        UpdateMode(value.Number("ModeCode"));
        _currentFanLevel = value.Number("FanPreset", 0);
        if (_mode == 3 && _currentFanLevel is >= 1 and <= 5)
            ModeFootnote.Text = L.F($"固件已读回 · 风扇预设第 {_currentFanLevel} 档");
        int temperature = value.Number("CpuCelsius");
        CpuTemperature.Text = temperature is >= 0 and <= 130 ? temperature + " °C" : "— °C";
        Fan0Rpm.Text = FormatRpm(value.Number("Fan0ActualRpm")); Fan1Rpm.Text = FormatRpm(value.Number("Fan1ActualRpm"));
        Fan0Target.Text = L.T("目标 ") + FormatRpm(value.Number("Fan0TargetRpm")) + " RPM";
        Fan1Target.Text = L.T("目标 ") + FormatRpm(value.Number("Fan1TargetRpm")) + " RPM";
        bool barrel = value.TryGetProperty("BarrelJackPresent", out var jack) && jack.GetBoolean();
        double pd = value.TryGetProperty("PdAdapterWatts", out var watts) ? watts.GetDouble() : 0;
        _availableFanLevels = value.Number("FanPresetLevelsAvailable", barrel || pd >= 199 ? 5 : pd >= 135 ? 3 : 0);
        PowerSourceText.Text = barrel ? L.T("圆口电源") : pd > 0 ? $"USB-C · {pd:0} W" : L.T("电池 / 未识别");
        PowerSourceHint.Text = barrel ? L.T("已检测到电源插口连接") : pd > 0 ? L.T("已读取 PD 电源信息") : L.T("高能与 HUNTER 需要外接电源");
        UpdateSelections();
    }
    private static string FormatRpm(int rpm) => rpm is >= 0 and <= 8000 ? rpm.ToString("N0") : "—";
    private void UpdateSelections()
    {
        SmartModeButton.IsChecked = _mode == 0; PerformanceModeButton.IsChecked = _mode == 1; HunterModeButton.IsChecked = _mode == 3;
        SrgbButton.IsChecked = _color.Equals("sRGB", StringComparison.OrdinalIgnoreCase); P3Button.IsChecked = _color.Equals("P3", StringComparison.OrdinalIgnoreCase);
        NativeButton.IsChecked = _color.Equals("Native", StringComparison.OrdinalIgnoreCase);
        SmartSelected.Visibility = _mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        PerformanceSelected.Visibility = _mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        HunterSelected.Visibility = _mode == 3 ? Visibility.Visible : Visibility.Collapsed;
        SrgbSelected.Visibility = SrgbButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        P3Selected.Visibility = P3Button.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NativeSelected.Visibility = NativeButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var fanButtons = new[] { Fan1Button, Fan2Button, Fan3Button, Fan4Button, Fan5Button };
        for (int i = 0; i < fanButtons.Length; i++)
        {
            fanButtons[i].IsEnabled = _mode == 3 && !_busy;
            fanButtons[i].Opacity = i < _availableFanLevels ? 1 : .55;
            bool selected = _mode == 3 && _currentFanLevel == i + 1;
            fanButtons[i].IsChecked = selected;
            fanButtons[i].Content = selected ? L.F($"✓ {i + 1} 档") : L.F($"{i + 1} 档");
        }
        FanAvailability.Text = _mode != 3 ? L.T("风扇预设仅在 HUNTER 模式下可调。") : _availableFanLevels switch
        {
            5 => L.T("当前电源支持 1–5 档。实际转速随温度和负载变化。"),
            3 => L.T("当前电源支持 1–3 档。第 4、5 档需要支持的 200 W 电源。"),
            _ => L.T("切换档位需要受支持的外接电源，并且电池电量至少为 20%。")
        };
        FanPresetHint.Text = _mode == 3 && _currentFanLevel is >= 1 and <= 5 ? L.F($"当前预设 · 第 {_currentFanLevel} 档，已从固件读取。") :
            _mode == 3 ? L.T("当前预设未识别。") : L.T("智能与高能模式按场景风扇表自动调速。");
        ForceIntegratedGpuButton.IsEnabled = _mode is 0 or 1 && !_busy;
    }
    private async Task RefreshColorAsync()
    {
        var status = await _backend.ColorAsync("status");
        _color = status.Text("ClosestFactoryMode");
        string profile = status.Text("WindowsIccProfile");
        DisplayModeSummary.Text = L.T("当前色彩 · ") + (_color == "Native" ? L.T("原生") : _color);
        IccProfileText.Text = "Windows ICC · " + profile;
        bool matches = _color == "P3" && profile.Equals("HonorP3.icc", StringComparison.OrdinalIgnoreCase) || _color == "sRGB" && profile.Equals("HonorsRGB.icc", StringComparison.OrdinalIgnoreCase) || _color == "Native" && profile.Equals("HonorNative.icc", StringComparison.OrdinalIgnoreCase);
        ColorVerificationText.Text = matches ? L.T("色域与 ICC 匹配") : L.T("色域与 ICC 不匹配，请重新选择色彩模式。");
        string effect = status.Text("EffectStyle", "off");
        DisplayEffectPicker.SelectedIndex = effect == "eye" ? 1 : effect == "reading" ? 2 : 0;
        TemperatureSlider.Value = status.Number("Temperature", 0);
        bool effectActive = effect != "off" || TemperatureSlider.Value != 0;
        DisplayEffectStatus.Text = !effectActive ? L.T("色彩效果已关闭") : IsTrue(status, "EffectsVerified") ? L.T("硬件 LUT 已读回") : L.T("设置尚未应用到硬件 LUT");
        UpdateSelections();
    }
    private async void ApplyDisplayEffect_Click(object sender, RoutedEventArgs e)
    {
        string effect = DisplayEffectPicker.SelectedIndex == 1 ? "eye" : DisplayEffectPicker.SelectedIndex == 2 ? "reading" : "off";
        string value = ((int)TemperatureSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RunAsync(async () =>
        {
            await _backend.ColorAsync("display-effect", effect, value);
            await RefreshColorAsync();
        }, L.T("显示效果已应用并读回。"));
    }
    private void ReadColorPreferences()
    {
        _syncingSwitches = true;
        try
        {
            ColorWatchToggle.IsOn = _watcherDesired;
            BrightnessRhythmToggle.IsOn = _rhythmDesired;
        }
        finally { _syncingSwitches = false; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _backend.ConnectAsync(); UpdateFirmware(await _backend.HardwareAsync("firmware-status"));
        UpdateCharge(await _backend.HardwareAsync("charge-status"));
        if (AudioPage.Visibility == Visibility.Visible) await RefreshAudioAsync();
    }, L.T("硬件控制已启用。本次窗口会话无需重复管理员授权。"));
    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { UpdateSelections(); return; }
        await ApplyModeAsync((string)((ToggleButton)sender).Tag);
    }
    private async Task ApplyModeAsync(string command)
    {
        await RunAsync(async () => { _modeRevision++; if (command == "firmware-hunter") _scenePower?.Restore(); UpdateFirmware(await _backend.HardwareAsync(command)); PresentModeOverlay(_mode); }, L.T("切换成功"), switchFeedback: true);
    }
    private async void Fan_Click(object sender, RoutedEventArgs e)
    {
        string command = (string)((ToggleButton)sender).Tag;
        await RunAsync(async () =>
        {
            if (!_backend.IsConnected) { await _backend.ConnectAsync(); UpdateFirmware(await _backend.HardwareAsync("firmware-status")); }
            if (_mode != 3) throw new InvalidOperationException(L.T("请先切换至 HUNTER 模式，再选择风扇预设。"));
            var result = await _backend.HardwareAsync(command); UpdateFirmware(result); PresentModeOverlay(_mode);
        }, L.T("切换成功"), switchFeedback: true);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => UpdateFirmware(await _backend.HardwareAsync("firmware-status")), L.T("传感器状态已刷新。"));
    private async void RefreshFirmwarePolicy_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var policy = await _backend.HardwareAsync("firmware-policy");
        string values = string.Join("  ·  ", policy.GetProperty("DttOdv").EnumerateArray().Select((item, index) => $"ODV{index} {item.GetInt32()}"));
        FirmwarePolicyText.Text = L.F($"DTT 固件策略值 · {values}\n充电阈值 · {policy.Number("ChargeStartPercent")}% 开始 / {policy.Number("ChargeStopPercent")}% 停止");
    }, null);
    private void UpdateCharge(JsonElement status)
    {
        int start = status.Number("ChargeStartPercent"), stop = status.Number("ChargeStopPercent");
        ChargeStatus.Text = L.F($"固件读数 · {start}% 开始充电 / {stop}% 停止充电");
        ChargeHome.IsChecked = (start, stop) == (40, 70);
        ChargeOffice.IsChecked = (start, stop) == (70, 90);
        ChargeTravel.IsChecked = (start, stop) == (95, 100);
    }
    private async void Charge_Click(object sender, RoutedEventArgs e)
    {
        string command = (string)((ToggleButton)sender).Tag;
        await RunAsync(async () =>
        {
            try { UpdateCharge(await _backend.HardwareAsync(command)); }
            catch { UpdateCharge(await _backend.HardwareAsync("charge-status")); throw; }
        }, L.T("充电阈值已从固件读回。"));
    }
    private async void ReadCharge_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => UpdateCharge(await _backend.HardwareAsync("charge-status")), null);
    private async void Color_Click(object sender, RoutedEventArgs e) => await ApplyColorAsync((string)((ToggleButton)sender).Tag);
    private async Task ApplyColorAsync(string command)
    {
        string label = command == "native" ? L.T("原生") : command == "srgb" ? "sRGB" : "P3";
        await RunAsync(async () => { await _backend.ColorAsync(command); await RefreshColorAsync(); _overlay.Present(label, L.T("色彩模式"), OverlayIcon.Color); }, L.T("切换成功"));
    }
    private async void RestoreColor_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await _backend.ColorAsync("restore"); await RefreshColorAsync(); }, L.T("已恢复首次备份的硬件色彩与 ICC。"));
    private async void RefreshColor_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await RefreshColorAsync(); ReadColorPreferences(); }, L.T("已重新读取硬件色彩与 ICC 状态。"));
    private async void InstallProfiles_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await _backend.InstallProfilesAsync(); await RefreshColorAsync(); }, L.T("三种色彩的 ICC 配置已安装。"));
    private async void ColorWatch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingSwitches || _busy) return;
        bool enabled = ColorWatchToggle.IsOn;
        await RunAsync(() => SetWatcherPreferenceAsync("color-watch-enabled.txt", enabled, false), null);
        ReadColorPreferences();
    }
    private static string RhythmPreference => Path.Combine(AppPaths.UserState, "brightness-rhythm-enabled.txt");
    private async void BrightnessRhythm_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingSwitches || _busy) return;
        await RunAsync(() => SetWatcherPreferenceAsync("brightness-rhythm-enabled.txt", BrightnessRhythmToggle.IsOn, true), null);
        ReadColorPreferences();
    }
    private async Task SetWatcherPreferenceAsync(string name, bool enabled, bool rhythm)
    {
        Directory.CreateDirectory(AppPaths.UserState);
        string path = Path.Combine(AppPaths.UserState, name);
        bool previous = rhythm ? _rhythmDesired : _watcherDesired;
        File.WriteAllText(path, enabled ? "1" : "0");
        try
        {
            bool keepRunning = enabled || (rhythm ? _watcherDesired : _rhythmDesired);
            await _backend.ColorAsync(keepRunning ? "color-watch-start" : "color-watch-stop");
            if (keepRunning) await _backend.ColorAsync(rhythm || !enabled ? "color-watch-settings" : "color-watch-reapply");
            if (rhythm) _rhythmDesired = enabled;
            else _watcherDesired = enabled;
        }
        catch
        {
            File.WriteAllText(path, previous ? "1" : "0");
            throw;
        }
    }
    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag?.ToString() ?? "performance";
        PerformancePage.Visibility = tag == "performance" ? Visibility.Visible : Visibility.Collapsed;
        DisplayPage.Visibility = tag == "display" ? Visibility.Visible : Visibility.Collapsed;
        AudioPage.Visibility = tag == "audio" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = tag == "network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = tag switch
        {
            "display" => L.T("显示与色彩"),
            "audio" => L.T("音频"),
            "network" => L.T("网络加速"),
            "settings" => L.T("设置与记录"),
            "about" => L.T("关于"),
            _ => L.T("性能与散热")
        };
        AnimatePage(tag switch
        {
            "display" => DisplayPage, "audio" => AudioPage, "network" => NetworkPage,
            "settings" => SettingsPage, "about" => AboutPage, _ => PerformancePage
        });
        if (tag == "display") await RunAsync(async () => { await RefreshColorAsync(); ReadColorPreferences(); await ReadRefreshRateAsync(); }, null);
        else if (tag == "audio" && (AppPaths.Installed || _backend.IsConnected)) await RunAsync(RefreshAudioAsync, null);
        else if (tag == "network") await RunAsync(async () => { UpdateNetwork(await _backend.NetworkStatusAsync(), true); UpdateDownloadChannels(); }, null);
    }
    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Root.RequestedTheme = ThemePicker.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
    }
    private void UpdateTitleBarTheme()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 130, 138, 150) : Windows.UI.Color.FromArgb(255, 100, 108, 120);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 55, 60, 69) : Windows.UI.Color.FromArgb(255, 223, 228, 236);
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.UserState); Process.Start(new ProcessStartInfo(AppPaths.UserState) { UseShellExecute = true });
    }
    private void OpenDisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        string uri = (string)((Button)sender).Tag;
        if (uri is "ms-settings:nightlight" or "ms-settings:display" or "ms-settings:display-advanced" or "ms-settings:network-wifi" or "ms-settings:sound")
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
    private void OpenNvidiaPanel_Click(object sender, RoutedEventArgs e)
    {
        const string panel = @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel";
        try { Process.Start(new ProcessStartInfo("explorer.exe", panel) { UseShellExecute = true }); }
        catch (Exception ex) { Show(Friendly(ex), InfoBarSeverity.Error); }
    }
    public void ShowMainWindow()
    {
        try { File.Delete(RuntimeIdentity.PauseFile); } catch { }
        AppWindow.Show(); Activate();
        if (_ready) _ = ShowStartupGuideAsync();
    }
    public void RequestExit() => TrayAction("quit");
    private async void TrayAction(string action)
    {
        if (action == "show") { ShowMainWindow(); return; }
        if (action == "quit")
        {
            _download?.Cancel();
            Directory.CreateDirectory(AppPaths.UserState); File.WriteAllText(RuntimeIdentity.PauseFile, "Paused until next login or manual launch.");
            _quitRequested = true; _closing = true; _residentTimer.Stop();
            try { await _backend.ColorAsync("color-watch-stop"); } catch { }
            Close(); Application.Current.Exit(); return;
        }
        if (action == "cycle-refresh") { HotkeyAction(action); return; }
        if (action.StartsWith("firmware-", StringComparison.Ordinal)) await ApplyModeAsync(action);
        else await ApplyColorAsync(action);
    }
    private async Task MaintainResidentAsync()
    {
        if (_residentBusy || _closing) return;
        _residentBusy = true;
        try
        {
            if (_watcherDesired || _rhythmDesired)
            {
                bool found = Process.GetProcessesByName("HonorColorWatcher").Any(p => { try { return p.MainModule?.FileName?.StartsWith(AppPaths.Backend, StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } });
                if (!found) await _backend.ColorAsync("color-watch-start");
            }
            if (!_backend.IsConnected) { await _backend.ConnectAsync(); UpdateConnection(); }
        }
        catch (Exception ex) { Log(L.T("后台恢复等待：") + Friendly(ex)); }
        finally { _residentBusy = false; }
    }
    private void PreviewOverlay_Click(object sender, RoutedEventArgs e) => _overlay.Present("HUNTER", L.T("预览 · 性能模式"), OverlayIcon.Performance);

}
