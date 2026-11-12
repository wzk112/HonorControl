using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class PerformanceController
{
    private readonly Func<string, Task<JsonElement>> _command;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _config = Path.Combine(RuntimeIdentity.UserState, "auto-performance.txt");
    private readonly string _journal = Path.Combine(RuntimeIdentity.UserState, "auto-performance-restore.json");
    private bool _enabled;
    private SavedMode? _saved;
    private int _pending = -1;
    private long _pendingSince, _changed;
    private readonly GameSessionTracker _gameSession = new();
    private object _snapshot = new { Enabled = false, Note = "自动性能模式已关闭。", Error = "" };

    private sealed record SavedMode(int Original, int Fan, int Applied);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);

    public PerformanceController(Func<string, Task<JsonElement>> command)
    {
        _command = command;
        _enabled = File.Exists(_config) && File.ReadAllText(_config).Trim() == "1";
        if (File.Exists(_journal)) _saved = JsonSerializer.Deserialize<SavedMode>(File.ReadAllText(_journal));
    }
    public object Snapshot => _snapshot;
    public async Task<object> ConfigureAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (!enabled) await RestoreAsync();
            SaveEnabled(enabled);
            _pending = -1;
            await UpdateAsync();
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonElement> ApplyManualAsync(string command)
    {
        await _gate.WaitAsync();
        try
        {
            var result = await _command(command);
            SaveEnabled(false);
            ClearJournal();
            _snapshot = new { Enabled = false, Note = "已使用手动性能模式，自动切换已关闭。", Error = "" };
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task RunAsync(CancellationToken token)
    {
        bool recovering = true;
        while (!token.IsCancellationRequested)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (recovering) { await RestoreAsync(); recovering = false; }
                await UpdateAsync();
            }
            catch (Exception ex) { _snapshot = new { Enabled = _enabled, Note = "等待下一次检测。", Error = ex.Message }; }
            finally { _gate.Release(); }
            await Task.Delay(5000, token);
        }
    }

    private async Task UpdateAsync()
    {
        if (!_enabled || File.Exists(RuntimeIdentity.PauseFile))
        {
            await RestoreAsync();
            _pending = -1;
            _snapshot = new { Enabled = _enabled, Note = _enabled ? "会话已暂停。" : "自动性能模式已关闭。", Error = "" };
            return;
        }
        var state = await _command("firmware-status");
        int current = state.GetProperty("ModeCode").GetInt32();
        if (current is not (0 or 1 or 3)) throw new InvalidOperationException("当前固件性能模式未识别。");
        if (_saved is { } saved && saved.Applied != current)
        {
            ClearJournal();
            SaveEnabled(false);
            _snapshot = new { Enabled = false, Note = "检测到外部性能模式切换，已采用新设置。", Error = "" };
            return;
        }
        if (!GetSystemPowerStatus(out var power) || power.Ac > 1) throw new InvalidOperationException("无法确认供电状态。");
        int scene = Scene();
        bool ac = power.Ac == 1 && power.Percent is >= 20 and <= 100;
        int target = SelectMode(scene, ac, state.GetProperty("FanPresetLevelsAvailable").GetInt32() >= 3, power.Saver != 0);
        long now = Stopwatch.GetTimestamp();
        if (target != _pending) { _pending = target; _pendingSince = now; }
        bool powerLost = !ac && current != 0;
        if (current != target && (powerLost || Stopwatch.GetElapsedTime(_pendingSince, now).TotalSeconds >= 8 &&
                (_changed == 0 || Stopwatch.GetElapsedTime(_changed, now).TotalSeconds >= 30)))
        {
            _saved ??= new(current, state.GetProperty("FanPreset").GetInt32(), current);
            var original = _saved;
            SaveJournal(_saved with { Applied = target });
            try
            {
                var applied = await _command(ModeCommand(target));
                if (applied.GetProperty("Status").GetProperty("ModeCode").GetInt32() != target)
                    throw new InvalidOperationException("自动性能模式读回不一致。");
                current = target;
                _changed = now;
            }
            catch { SaveJournal(original); throw; }
        }
        string constraint = scene is 1 or 5 or 13 or 16 or 17 or 20 ?
            power.Ac != 1 ? "电池供电，自动调度目标为智能模式。" :
            power.Percent < 20 ? "电量低于 20%，自动调度目标为智能模式。" :
            power.Percent > 100 ? "无法确认电量，自动调度目标为智能模式。" :
            power.Saver != 0 ? "节电模式已开启，自动调度目标为智能模式。" :
            scene is 1 or 17 && target != 3 ? "HUNTER 需要至少 135 W 的供电，当前自动目标为高能。" : "" : "";
        string source = _gameSession.Application.Length > 0 ? "游戏 · " + _gameSession.Application : "前台应用";
        _snapshot = new
        {
            Enabled = true, Note = $"自动调度 · {ModeName(current)}，目标 {ModeName(target)}。{source}",
            CurrentMode = current, TargetMode = target, SceneSource = source, Pending = current != target,
            Error = "", ConstraintReason = constraint
        };
    }

    internal static int SelectMode(int scene, bool ac, bool hunterSupported, bool batterySaver) =>
        !ac || batterySaver ? 0 : scene is 1 or 17 ? hunterSupported ? 3 : 1 : scene is 5 or 13 or 16 or 20 ? 1 : 0;

    private int Scene()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint id);
        string foreground = "";
        if (id != 0)
        {
            try { using var process = Process.GetProcessById((int)id); foreground = process.ProcessName + ".exe"; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var running = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { running.Add(process.ProcessName + ".exe"); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return _gameSession.Update(SceneCatalog.Classify(foreground), foreground, running, DateTimeOffset.UtcNow);
    }
    private static string ModeCommand(int mode) => mode switch { 0 => "firmware-smart", 1 => "firmware-performance", 3 => "firmware-hunter", _ => throw new InvalidOperationException("Unknown firmware mode.") };
    private static string ModeName(int mode) => mode switch { 0 => "智能", 1 => "高能", 3 => "HUNTER", _ => "未知" };
    private void SaveEnabled(bool enabled)
    {
        Directory.CreateDirectory(RuntimeIdentity.UserState);
        File.WriteAllText(_config, enabled ? "1" : "0");
        _enabled = enabled;
    }
    private void SaveJournal(SavedMode value)
    {
        Directory.CreateDirectory(RuntimeIdentity.UserState);
        File.WriteAllText(_journal + ".tmp", JsonSerializer.Serialize(value));
        File.Move(_journal + ".tmp", _journal, true);
        _saved = value;
    }
    private void ClearJournal() { File.Delete(_journal); _saved = null; }
    private async Task RestoreAsync()
    {
        if (_saved is not { } saved) return;
        var status = await _command("firmware-status");
        if (status.GetProperty("ModeCode").GetInt32() == saved.Applied)
        {
            int target = saved.Original;
            if (target != 0 && (!GetSystemPowerStatus(out var power) || power.Ac != 1 || power.Percent is < 20 or > 100)) target = 0;
            if (target == 3 && status.GetProperty("FanPresetLevelsAvailable").GetInt32() < 3) target = 0;
            var restored = await _command(ModeCommand(target));
            int available = restored.GetProperty("Status").GetProperty("FanPresetLevelsAvailable").GetInt32();
            if (target == 3 && saved.Fan is >= 1 and <= 5) await _command("fan-" + Math.Min(saved.Fan, available));
        }
        ClearJournal();
    }
}
