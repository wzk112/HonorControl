using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System.Text.Json;

namespace HonorControl;

public sealed partial class MainWindow
{
    private SceneScheduler? _scheduler;
    private ScenePowerPolicy? _scenePower;
    private bool _syncingScheduler;
    private bool _scenePowerBusy;
    private bool _syncingAutomatic;
    private string _lastAutomaticConstraint = "";
    private static string ScenePowerPreference => Path.Combine(AppPaths.UserState, "scene-power-enabled.txt");

    private void UpdateDtt(JsonElement value)
    {
        string error = value.Text("Error", "");
        DttStatusText.Text = error.Length > 0 ? error : value.Text("Note", "");
    }

    private void UpdateBoost(JsonElement value)
    {
        string error = value.Text("Error", "");
        GpuBoostStatusText.Text = error.Length > 0 ? error : value.Text("Note", "");
    }

    private void UpdateAutomaticPerformance(JsonElement value)
    {
        _syncingAutomatic = true;
        try { AutoPerformanceToggle.IsOn = value.TryGetProperty("Enabled", out var enabled) && enabled.GetBoolean(); }
        finally { _syncingAutomatic = false; }
        string error = value.Text("Error", "");
        AutoPerformanceStatus.Text = error.Length == 0 ? value.Text("Note", "") : error;
        if (error.Length == 0 && AutoPerformanceToggle.IsOn && value.Number("TargetMode") is 0 or 1 or 3)
        {
            int target = value.Number("TargetMode");
            string wait = target != _mode ? L.T(" · 等待场景稳定后切换") : "";
            AutoPerformanceStatus.Text = L.F($"当前{ModeName(_mode)} · 目标{ModeName(target)} · {value.Text("SceneSource", L.T("前台应用"))}{wait}");
        }
        string constraint = error.Length > 0 ? Friendly(new InvalidOperationException(error)) : value.Text("ConstraintReason", "");
        if (constraint.Length > 0 && constraint != _lastAutomaticConstraint)
            _overlay.Present(L.T("自动性能调度受限"), constraint, OverlayIcon.Warning);
        _lastAutomaticConstraint = constraint;
    }

    private async void AutoPerformance_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingAutomatic || _busy) return;
        bool enabled = AutoPerformanceToggle.IsOn;
        await RunAsync(async () =>
        {
            try { UpdateAutomaticPerformance(await _backend.ConfigurePerformanceAsync(enabled)); }
            catch { UpdateAutomaticPerformance(await _backend.PerformanceStatusAsync()); throw; }
        }, L.T("自动性能设置已保存。"));
    }

    private void LoadScheduler()
    {
        try
        {
            _scheduler = new SceneScheduler();
            UpdateSchedulerControls();
            RefreshScheduler();
        }
        catch (Exception ex)
        {
            SchedulerStatus.Text = Friendly(ex);
            SchedulerControls.Visibility = Visibility.Collapsed;
            SchedulerToggle.IsEnabled = false;
        }
        try
        {
            _scenePower = new ScenePowerPolicy();
            ScenePowerToggle.IsOn = File.Exists(ScenePowerPreference) && File.ReadAllText(ScenePowerPreference).Trim() == "1";
        }
        catch (Exception ex) { ScenePowerStatus.Text = Friendly(ex); ScenePowerToggle.IsEnabled = false; }
    }

    private async Task RefreshScenePowerAsync(int? verifiedMode = null)
    {
        if (_scenePower is null || !ScenePowerToggle.IsOn || _scenePowerBusy || _busy || _closing) return;
        _scenePowerBusy = true;
        try
        {
            if (!_backend.IsConnected)
            {
                _scenePower.Restore();
                ScenePowerStatus.Text = L.T("等待后台读取当前固件模式。");
                return;
            }
            int mode = verifiedMode ?? (await _backend.HardwareAsync("firmware-mode")).Number("ModeCode");
            if (!_closing && !_busy && ScenePowerToggle.IsOn)
                ScenePowerStatus.Text = _scenePower.Update(mode, _scheduler?.Configuration.ForegroundApp);
        }
        catch (Exception ex)
        {
            try { _scenePower.Restore(); } catch (Exception restoreError) { ScenePowerStatus.Text = Friendly(restoreError); return; }
            ScenePowerStatus.Text = Friendly(ex);
        }
        finally { _scenePowerBusy = false; }
    }

    private async void ScenePower_Toggled(object sender, RoutedEventArgs e)
    {
        if (_scenePower is null || _syncingScheduler) return;
        try
        {
            _scenePower.Reset();
            Directory.CreateDirectory(AppPaths.UserState);
            File.WriteAllText(ScenePowerPreference, ScenePowerToggle.IsOn ? "1" : "0");
            if (ScenePowerToggle.IsOn) await RefreshScenePowerAsync();
            else ScenePowerStatus.Text = L.T("场景 EPP 已关闭。");
        }
        catch (Exception ex)
        {
            ScenePowerStatus.Text = Friendly(ex);
            _syncingScheduler = true;
            try { ScenePowerToggle.IsOn = File.Exists(ScenePowerPreference) && File.ReadAllText(ScenePowerPreference).Trim() == "1"; }
            finally { _syncingScheduler = false; }
        }
    }

    private void RefreshScheduler()
    {
        if (_scheduler is null) return;
        try { SchedulerStatus.Text = _scheduler.Update(); }
        catch (Exception ex) { SchedulerStatus.Text = Friendly(ex); }
    }

    private void UpdateSchedulerControls()
    {
        if (_scheduler is null) return;
        var settings = _scheduler.Configuration;
        _syncingScheduler = true;
        try { SchedulerToggle.IsOn = settings.Enabled; SchedulerAutoForeground.IsChecked = settings.AutoForeground; }
        finally { _syncingScheduler = false; }
        ForegroundAppText.Text = Path.GetFileName(settings.ForegroundApp) ?? L.T("未选择");
        BackgroundAppText.Text = Path.GetFileName(settings.BackgroundApp) ?? L.T("未选择");
    }

    private void Scheduler_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingScheduler || _scheduler is null) return;
        try { _scheduler.Configure(_scheduler.Configuration with { Enabled = SchedulerToggle.IsOn, AutoForeground = SchedulerAutoForeground.IsChecked == true }); RefreshScheduler(); }
        catch (Exception ex) { SchedulerStatus.Text = Friendly(ex); UpdateSchedulerControls(); }
    }

    private async void ChooseSchedulerApp_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduler is null || sender is not Button { Tag: string role }) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if (file.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(L.T("请选择普通应用程序。"));
            var settings = _scheduler.Configuration;
            _scheduler.Configure(role == "foreground" ? settings with { ForegroundApp = file.Path } : settings with { BackgroundApp = file.Path });
            UpdateSchedulerControls();
            RefreshScheduler();
        }
        catch (Exception ex) { SchedulerStatus.Text = Friendly(ex); }
    }

    private void ClearScheduler_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduler is null) return;
        try { _scheduler.Configure(new(false, null, null)); UpdateSchedulerControls(); RefreshScheduler(); }
        catch (Exception ex) { SchedulerStatus.Text = Friendly(ex); }
    }
}
