using System.Runtime.InteropServices;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class DttController
{
    private readonly Func<string, Task<JsonElement>> _command;
    private readonly SceneContext _context = new();
    private readonly Rule[] _rules;
    private int _last = -1;
    private bool _recovering = true;
    private string Preference => Path.Combine(RuntimeIdentity.UserState, "scene-power-enabled.txt");
    private string Journal => Path.Combine(RuntimeIdentity.UserState, "DeviceState", "dtt-restore.json");
    private object _snapshot = new { Enabled = false, Note = "等待场景调度。", Error = "" };
    private sealed record Rule(int Id, int Mode, bool Ac, int Scene, int Level, int Pl1, int Epo, int Fan, int Epp, int Watts);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus power);

    public DttController(Func<string, Task<JsonElement>> command)
    {
        _command = command;
        _rules = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Backend", "DraPolicies.tsv")).Where(line => line.Length > 0)
            .Select(line => line.Split('\t').Select(int.Parse).ToArray())
            .Select(row => new Rule(row[0], row[1], row[2] == 1, row[3], row[4], row[5], row[6], row[7], row[8], row[9])).ToArray();
    }
    public object Snapshot => _snapshot;
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await UpdateAsync(); }
            catch (Exception ex)
            {
                _last = -1;
                _snapshot = new { Enabled = true, Note = "场景功耗与风扇等待重试。", Error = ex.Message };
            }
            await Task.Delay(5000, token);
        }
    }

    private async Task UpdateAsync()
    {
        if (_recovering) { await RestoreAsync(); _recovering = false; }
        bool enabled = File.Exists(Preference) && File.ReadAllText(Preference).Trim() == "1";
        if (!enabled || File.Exists(RuntimeIdentity.PauseFile))
        {
            await RestoreAsync();
            _snapshot = new { Enabled = enabled, Note = "场景功耗与风扇已暂停。", Error = "" };
            return;
        }
        var firmware = await _command("firmware-status");
        int mode = firmware.GetProperty("ModeCode").GetInt32();
        bool originalRunning = firmware.GetProperty("HonorPerformanceHosts").GetInt32() != 0;
        if (mode is not (0 or 1) || originalRunning)
        {
            await RestoreAsync();
            _snapshot = new { Enabled = true, Note = mode == 3 ? "HUNTER 使用固件功耗与所选风扇预设。" : "等待原厂性能后台退出后接管场景功耗与风扇。", Error = "" };
            return;
        }
        if (!GetSystemPowerStatus(out var power) || power.Ac > 1) throw new InvalidOperationException("无法确认供电状态。");
        var context = _context.Read();
        bool ac = power.Ac == 1;
        int level = context.Scene == 3 && !ac ? 1 : context.Level;
        Rule rule = _rules.FirstOrDefault(row => row.Mode == mode && row.Ac == ac && row.Scene == context.Scene && row.Level == level)
            ?? _rules.Where(row => row.Mode == mode && row.Ac == ac && row.Scene == context.Scene).OrderByDescending(row => row.Level).FirstOrDefault()
            ?? _rules.Single(row => row.Mode == mode && row.Ac == ac && row.Scene == -1 && row.Level == 3);
        if (_last != rule.Id || !File.Exists(Journal))
        {
            var result = await _command("dtt-apply-" + rule.Id);
            if (!result.GetProperty("Verified").GetBoolean()) throw new InvalidOperationException("场景功耗策略读回未确认。");
            _last = rule.Id;
        }
        var actual = await _command("dtt-status");
        if (actual.GetProperty("Pl1").GetInt32() != rule.Pl1 || actual.GetProperty("Epo").GetInt32() != rule.Epo || actual.GetProperty("Fan").GetInt32() != rule.Fan)
            throw new InvalidOperationException("固件策略已被其他控制器更改，等待重新确认控制权。");
        _snapshot = new { Enabled = true, Note = $"DTT 已读回 · PL1 请求 {rule.Watts} W · EPO {rule.Epo} · 风扇表 {rule.Fan:X2}", Error = "" };
    }

    private async Task RestoreAsync()
    {
        _last = -1;
        if (File.Exists(Journal)) await _command("dtt-restore");
    }
}
