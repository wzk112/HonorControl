using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class GpuController
{
    private readonly Func<string, Task<JsonElement>> _command;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _config = Path.Combine(RuntimeIdentity.UserState, "gpu-mode.txt");
    private readonly string _restore = Path.Combine(RuntimeIdentity.UserState, "gpu-restore.flag");
    private string _mode = "disabled";
    private long _idle;
    private object _snapshot = new { Mode = "disabled", StateCode = -1, Note = "等待读取显卡状态。", Error = "" };

    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus value);

    public GpuController(Func<string, Task<JsonElement>> command)
    {
        _command = command;
        if (File.Exists(_config))
        {
            string saved = File.ReadAllText(_config).Trim();
            if (Valid(saved)) _mode = saved;
        }
    }

    public object Snapshot => _snapshot;
    private static bool Valid(string mode) => mode is "disabled" or "auto" or "dual" or "integrated";

    public async Task<object> ForceIntegratedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            int before = (await _command("gpu-status")).GetProperty("StateCode").GetInt32();
            if (before is not (0 or 1 or 4)) throw new InvalidOperationException("显卡正在切换，请等待状态稳定。");
            Directory.CreateDirectory(RuntimeIdentity.UserState);
            if (before != 0) File.WriteAllText(_restore, "Restore dual-GPU operation after explicit integrated-only switching.");
            try
            {
                await _command("gpu-force-integrated");
                File.WriteAllText(_config, "integrated");
                _mode = "integrated";
                _idle = 0;
                await UpdateAsync(true);
                return _snapshot;
            }
            catch { await RestoreAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<object> ConfigureAsync(string mode)
    {
        if (!Valid(mode)) throw new ArgumentException("未知显卡策略。");
        await _gate.WaitAsync();
        try
        {
            if (mode == "integrated" && (await _command("firmware-mode")).GetProperty("ModeCode").GetInt32() == 3)
                throw new InvalidOperationException("请先切换至智能或高能模式，再选择集成显卡。");
            if (mode is "disabled" or "dual") await RestoreAsync();
            Directory.CreateDirectory(RuntimeIdentity.UserState);
            File.WriteAllText(_config + ".tmp", mode);
            File.Move(_config + ".tmp", _config, true);
            _mode = mode;
            _idle = 0;
            await UpdateAsync(true);
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> ReadAsync()
    {
        await _gate.WaitAsync();
        try { await UpdateAsync(true); return _snapshot; }
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
                await UpdateAsync(false);
            }
            catch (Exception ex) { _snapshot = new { Mode = _mode, StateCode = -1, Note = "等待下一次检测。", Error = ex.Message }; }
            finally { _gate.Release(); }
            await Task.Delay(10000, token);
        }
    }

    private async Task UpdateAsync(bool forceRead)
    {
        bool paused = File.Exists(RuntimeIdentity.PauseFile);
        if (paused || _mode == "disabled")
        {
            await RestoreAsync();
            if (!forceRead) return;
        }
        JsonElement state = await _command("gpu-status");
        int code = state.GetProperty("StateCode").GetInt32();
        string note = _mode == "disabled" || paused ? "自动显卡策略已关闭。" : "双显卡运行。";
        if (!paused && _mode != "disabled")
        {
            if (!state.GetProperty("HotPlugSupported").GetBoolean()) throw new InvalidOperationException("当前配置不支持固件显卡热切换。");
            if (!GetSystemPowerStatus(out var power) || power.Ac > 1) throw new InvalidOperationException("无法确认供电状态。");
            int mode = (await _command("firmware-mode")).GetProperty("ModeCode").GetInt32();
            bool dual = _mode == "dual" || mode == 3 || (_mode == "auto" && power.Ac == 1);
            if (dual)
            {
                _idle = 0;
                if (code == 0) { state = await _command("gpu-dual"); code = state.GetProperty("StateCode").GetInt32(); }
                if (code is 1 or 4) File.Delete(_restore);
                note = mode == 3 ? "HUNTER 使用双显卡。" : "双显卡运行。";
            }
            else if (code == 0)
            {
                _idle = 0;
                note = "独显已关闭，使用集成显卡。";
            }
            else if (code == 1 && AllIntel(state))
            {
                if (_idle == 0) _idle = Stopwatch.GetTimestamp();
                note = "独显空闲，等待 30 秒后切换。";
                if (Stopwatch.GetElapsedTime(_idle).TotalSeconds >= 30)
                {
                    Directory.CreateDirectory(RuntimeIdentity.UserState);
                    File.WriteAllText(_restore, "Restore dual-GPU operation on exit or interrupted session.");
                    state = await _command("gpu-integrated");
                    code = state.GetProperty("StateCode").GetInt32();
                    note = "独显已关闭，使用集成显卡。";
                }
            }
            else
            {
                _idle = 0;
                note = code == 4 ? "独显正在使用，等待应用释放。" : "等待显卡状态和显示输出满足切换条件。";
            }
        }
        _snapshot = new { Mode = _mode, StateCode = code, Note = note, Error = "", Status = state };
    }

    private static bool AllIntel(JsonElement state)
    {
        var paths = state.GetProperty("ActiveOutputAdapters").EnumerateArray().ToArray();
        return paths.Length > 0 && paths.All(path => path.GetString()?.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task RestoreAsync()
    {
        if (!File.Exists(_restore)) return;
        var state = await _command("gpu-dual");
        if (state.GetProperty("StateCode").GetInt32() is not (1 or 4)) throw new InvalidOperationException("等待独显恢复双显卡运行。");
        File.Delete(_restore);
        _idle = 0;
    }
}
