using System.Runtime.InteropServices;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class GpuBoostController(Func<string, Task<JsonElement>> command)
{
    private readonly SceneContext _context = new();
    private string Journal => Path.Combine(RuntimeIdentity.DeviceState, "gpu-clock-restore.json");
    private object _snapshot = new { Enabled = false, Note = "显卡频偏控制已暂停。", Error = "" };
    private bool _recovering = true;
    public object Snapshot => _snapshot;
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Life, FullLife; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus power);

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await UpdateAsync(); }
            catch (Exception ex)
            {
                try { await RestoreAsync(); } catch { }
                _snapshot = new { Enabled = true, Note = "显卡频偏控制等待重试。", Error = ex.Message };
            }
            await Task.Delay(10000, token);
        }
    }
    private async Task UpdateAsync()
    {
        if (_recovering) { await RestoreAsync(); _recovering = false; }
        string preference = Path.Combine(RuntimeIdentity.UserState, "scene-power-enabled.txt");
        bool enabled = File.Exists(preference) && File.ReadAllText(preference).Trim() == "1";
        if (!enabled || File.Exists(RuntimeIdentity.PauseFile) || !GetSystemPowerStatus(out var power) || power.Ac != 1 || power.Percent < 20 || power.Percent == 255 || power.Saver != 0)
        {
            await RestoreAsync();
            _snapshot = new { Enabled = enabled, Note = "显卡频偏控制已暂停，应用设置已恢复。", Error = "" };
            return;
        }
        var firmware = await command("firmware-status");
        if (firmware.GetProperty("HonorPerformanceHosts").GetInt32() != 0)
        {
            await RestoreAsync();
            _snapshot = new { Enabled = true, Note = "等待原厂性能后台退出后接管显卡频偏。", Error = "" };
            return;
        }
        int mode = firmware.GetProperty("ModeCode").GetInt32();
        var scene = _context.Read();
        bool game = scene.Scene is 1 or 17;
        if (mode != 3 && !game)
        {
            await RestoreAsync();
            _snapshot = new { Enabled = true, Note = "非游戏场景使用原显卡频偏。", Error = "" };
            return;
        }
        var gpu = await command("gpu-status");
        if (gpu.GetProperty("StateCode").GetInt32() is not (1 or 4))
        {
            _snapshot = new { Enabled = true, Note = "独显未就绪，显卡频偏控制暂停。", Error = "" };
            return;
        }
        var status = await command("gpu-clock-status");
        if (status.GetProperty("GpuCelsius").GetInt32() >= 80)
        {
            await RestoreAsync();
            _snapshot = new { Enabled = true, Note = "GPU 温度达到 80°C，已恢复原显卡频偏。", Error = "" };
            return;
        }
        var applied = await command(game ? "gpu-clock-game" : "gpu-clock-base");
        _snapshot = new { Enabled = true, Note = $"显卡频偏已读回 · 核心 +{applied.GetProperty("GraphicsOffsetMhz").GetInt32()} MHz / 显存 +{applied.GetProperty("MemoryOffsetMhz").GetInt32()} MHz", Error = "" };
    }
    private async Task RestoreAsync() { if (File.Exists(Journal)) await command("gpu-clock-restore"); }
}
