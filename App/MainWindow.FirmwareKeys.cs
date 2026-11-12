using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace HonorControl;

public sealed partial class MainWindow
{
    private FirmwareKeys? _firmwareKeys;
    private bool _syncingFn, _fnReadBusy;
    private bool _micKeyBusy, _touchpadKeyBusy;
    private bool? _standardFKeys;
    private int _lastModeOverlay = -1;
    private int _lastFanOverlay;
    private long _lastModeOverlayTime;
    private void StartFirmwareKeys()
    {
        _firmwareKeys = new FirmwareKeys(code => { DispatcherQueue.TryEnqueue(() => FirmwareKeyEvent(code)); });
        _firmwareKeys.Start();
    }
    private void PresentModeOverlay(int mode)
    {
        long now = Environment.TickCount64;
        int fan = mode == 3 ? _currentFanLevel : 0;
        if (_lastModeOverlay == mode && _lastFanOverlay == fan && now - _lastModeOverlayTime < 1500) return;
        _lastModeOverlay = mode; _lastFanOverlay = fan; _lastModeOverlayTime = now;
        string title = ModeName(mode);
        if (mode == 3) title += fan is >= 1 and <= 5 ? L.F($" · 第 {fan} 档") : L.T(" · 档位未知");
        _overlay.Present(title, L.T("性能模式"), OverlayIcon.Performance);
    }
    private async void FirmwareKeyEvent(uint code)
    {
        if (!_ready || _closing) return;
        if (!FirmwareKeys.IsHandled(code)) return;
        Log(L.F($"Fn 固件事件：0x{code:X3}"));
        FnEventStatusText.Text = _firmwareKeys?.Status ?? "";
        if (code == 0x2A6)
        {
            string reason = GetSystemPowerStatus(out var power)
                ? PerformanceCycle.UnavailableReason(1, power.Ac == 1, power.Percent, false, 0) ?? L.T("当前供电条件只支持智能模式。")
                : L.T("无法确认供电状态，请稍后重试。");
            _overlay.Present(L.T("性能模式未切换"), reason, OverlayIcon.Warning);
            Show(reason, InfoBarSeverity.Warning);
            Log(reason);
            return;
        }
        if (code == 0x2A7) { HotkeyAction("cycle-refresh"); return; }
        if (code is 0x2A0 or 0x2A1 or 0x2A8)
        {
            // These are mode-change notifications. Fn+P has already changed the EC mode.
            // Reissuing a cycle here would skip modes or change it twice.
            if (_busy) return;
            await RunAsync(async () =>
            {
                await Task.Delay(180);
                _modeRevision++;
                var snapshot = await _backend.HardwareAsync("firmware-status");
                UpdateFirmware(snapshot);
                if (code == 0x2A8 && _mode == 0 && GetSystemPowerStatus(out var power))
                {
                    bool barrel = snapshot.TryGetProperty("BarrelJackPresent", out var jack) && jack.GetBoolean();
                    double pd = snapshot.TryGetProperty("PdAdapterWatts", out var watts) ? watts.GetDouble() : 0;
                    string? reason = PerformanceCycle.UnavailableReason(1, power.Ac == 1, power.Percent, barrel, pd);
                    if (reason is not null) throw new InvalidOperationException(reason);
                }
                PresentModeOverlay(_mode);
            }, L.T("切换成功"), switchFeedback: true);
            return;
        }
        if (code == 0x28B) { ShowMainWindow(); return; }
        if (code is 0x283 or 0x2A3) { await ShowTouchpadStateAsync(); return; }
        if (code == 0x287) { await ToggleMicrophoneAsync(); return; }
        if (code is 0x290 or 0x291) await ReadKeyboardModeAsync();
    }

    private async Task ShowTouchpadStateAsync()
    {
        if (_touchpadKeyBusy) return;
        _touchpadKeyBusy = true;
        try
        {
            await Task.Delay(120);
            var status = await _backend.HardwareAsync("touchpad-status");
            int code = status.Number("TouchpadCode");
            if (_closing || code is not (0 or 1)) return;
            string title = code == 1 ? L.T("触摸板已启用") : L.T("触摸板已禁用");
            _overlay.Present(title, L.T("触摸板"), code == 1 ? OverlayIcon.Touchpad : OverlayIcon.TouchpadOff);
            Log(title);
        }
        catch (Exception ex) { Log(L.T("读取触摸板状态失败：") + Friendly(ex)); }
        finally { _touchpadKeyBusy = false; }
    }

    private async Task ToggleMicrophoneAsync()
    {
        if (_micKeyBusy) return;
        _micKeyBusy = true;
        try
        {
            bool muted = await Task.Run(MicrophoneMute.Toggle);
            if (_closing) return;
            string title = muted ? L.T("麦克风已静音") : L.T("麦克风已开启");
            _overlay.Present(title, L.T("麦克风"), muted ? OverlayIcon.MicrophoneMuted : OverlayIcon.Microphone);
            Log(title);
            try { await _backend.HardwareAsync(muted ? "mic-light-muted" : "mic-light-active"); }
            catch (Exception ex) { Log(L.T("同步麦克风指示灯失败：") + Friendly(ex)); }
        }
        catch (Exception ex) { Log(L.T("切换麦克风失败：") + Friendly(ex)); }
        finally { _micKeyBusy = false; }
    }
    private async Task ReadKeyboardModeAsync(bool restorePreference = false)
    {
        if (_fnReadBusy || _closing) return;
        _fnReadBusy = true;
        try
        {
            var state = await _backend.HardwareAsync(restorePreference ? "keyboard-restore-preference" : "keyboard-status");
            UpdateKeyboardMode(state);
        }
        catch (Exception ex) { KeyboardFnStateText.Text = Friendly(ex); }
        finally { _fnReadBusy = false; }
        FnEventStatusText.Text = _firmwareKeys?.Status ?? L.T("正在连接 Fn 固件事件…");
    }
    private void UpdateKeyboardMode(JsonElement state)
    {
        if (!state.TryGetProperty("StandardFKeys", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        bool standard = value.GetBoolean();
        _standardFKeys = standard;
        _syncingFn = true;
        try { FnRowModePicker.SelectedIndex = standard ? 1 : 0; }
        finally { _syncingFn = false; }
        KeyboardFnStateText.Text = standard ? L.T("当前：标准 F1–F12 优先 · 按住 Fn 使用键帽图标功能。") : L.T("当前：键帽图标功能优先 · 按住 Fn 输入标准 F1–F12。");
    }
    private async void FnRowMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingFn || FnRowModePicker.SelectedIndex < 0) return;
        if (_busy || _fnReadBusy)
        {
            _syncingFn = true;
            try { FnRowModePicker.SelectedIndex = _standardFKeys switch { true => 1, false => 0, null => -1 }; }
            finally { _syncingFn = false; }
            return;
        }
        bool standard = FnRowModePicker.SelectedIndex == 1;
        await RunAsync(async () =>
        {
            try
            {
                UpdateKeyboardMode(await _backend.HardwareAsync(standard ? "keyboard-fkeys" : "keyboard-icons"));
                _overlay.Present(standard ? L.T("标准 F1–F12") : L.T("键帽图标功能"), L.T("Fn 默认行为"), OverlayIcon.Keyboard);
                Log(L.T("Fn 默认行为已保存。") + KeyboardFnStateText.Text);
            }
            catch { await ReadKeyboardModeAsync(); throw; }
        }, L.T("切换成功"));
    }
    private async void ReadKeyboardMode_Click(object sender, RoutedEventArgs e) => await ReadKeyboardModeAsync();
}
