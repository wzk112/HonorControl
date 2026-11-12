using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class ScenePowerPolicy
{
    private const string SchemeName = "Honor Control Scene EPP";
    private static readonly Guid[] Settings = [PowerPolicySnapshot.EnergyPreference, PowerPolicySnapshot.EnergyPreferenceClass1];
    private static readonly Lazy<Dictionary<(int Mode, bool Ac, int Scene, int Level), uint>> Strategy = new(ReadStrategy);
    private readonly string _journal = Path.Combine(AppPaths.UserState, "scene-power-restore.json");
    private Plan? _plan;
    private bool _yielded;
    private (bool Ac, uint Value)? _applied;
    private readonly SceneContext _context = new();

    private sealed record Plan(Guid Original, Guid Dedicated);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(nint root, in Guid source, out nint clone);
    [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(nint root, in Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, in Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteFriendlyName(nint root, in Guid scheme, nint group, nint setting, byte[] name, uint length);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(nint root, in Guid scheme, nint group, nint setting, byte[] name, ref uint length);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);

    public ScenePowerPolicy()
    {
        if (File.Exists(_journal)) _plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(_journal));
        Restore();
    }

    public string Update(int mode, string? gameApplication)
    {
        if (mode is not (0 or 1) || !GetSystemPowerStatus(out var power) || power.Ac > 1 || power.Saver != 0)
        {
            Restore();
            return mode == 3 ? L.T("HUNTER 由固件管理，场景 EPP 已暂停。") : L.T("当前模式或供电条件下暂停场景 EPP。");
        }

        var context = _context.Read();
        var (name, value) = Select(mode, power.Ac == 1, context.Path, gameApplication, context.Idle, context.Level);
        if (!Apply(power.Ac == 1, value)) return L.T("检测到 Windows 电源计划已切换，场景 EPP 已暂停。关闭后可重新启用。");
        return L.F($"{name} · 前台 CPU 负载{(context.Level == 1 ? L.T("低") : context.Level == 2 ? L.T("中") : L.T("高"))} · Windows EPP {value}%（两类核心已读回）");
    }

    internal static int LoadLevel(double cores, int previous) => SceneContext.LoadLevel(cores, previous);

    internal static (string Name, uint Value) Select(int mode, bool ac, string? foreground, string? gameApplication, bool idle, int level = 3)
    {
        // DarwinA SceneStrategy values, converted from the firmware range 0–255 to Windows 0–100.
        bool selected = !string.IsNullOrEmpty(gameApplication) && StringComparer.OrdinalIgnoreCase.Equals(foreground, gameApplication);
        string executable = Path.GetFileName(foreground) ?? "";
        int scene = selected ? 1 : SceneCatalog.Classify(executable);
        if (scene == -1 && idle) scene = 3;
        string label = scene switch
        {
            0 => L.T("会议"), 1 or 17 => selected ? L.T("指定高性能应用") : L.T("游戏"), 2 => L.T("办公"), 3 => L.T("空闲"),
            4 => L.T("视频"), 5 or 16 => L.T("测评"), 7 => L.T("社交"), 9 => L.T("浏览器"), 11 => L.T("相机与图像"),
            13 => L.T("专业创作"), 20 => L.T("计算与建模"), _ => L.T("默认")
        };
        if (mode == 0 && !ac && scene == 3) level = 1;
        uint raw = ResolveStrategy(mode, ac, scene, level);
        return (label, (raw * 100 + 127) / 255);
    }

    private static uint ResolveStrategy(int mode, bool ac, int scene, int level)
    {
        var strategies = Strategy.Value;
        if (strategies.TryGetValue((mode, ac, scene, level), out uint raw)) return raw;
        for (int candidate = 3; candidate >= 1; candidate--)
            if (strategies.TryGetValue((mode, ac, scene, candidate), out raw)) return raw;
        return strategies[(mode, ac, -1, 3)];
    }

    private static Dictionary<(int Mode, bool Ac, int Scene, int Level), uint> ReadStrategy()
    {
        using var stream = typeof(ScenePowerPolicy).Assembly.GetManifestResourceStream("DarwinAEpp.tsv")
            ?? throw new InvalidOperationException(L.T("本机 EPP 策略表缺失。"));
        using var reader = new StreamReader(stream);
        var result = new Dictionary<(int, bool, int, int), uint>();
        while (reader.ReadLine() is string line)
        {
            string[] fields = line.Split('\t');
            result.Add((int.Parse(fields[0]), fields[1] == "1", int.Parse(fields[2]), int.Parse(fields[3])), uint.Parse(fields[4]));
        }
        return result;
    }

    internal bool Apply(bool ac, uint value)
    {
        if (value > 100) throw new ArgumentOutOfRangeException(nameof(value));
        if (_yielded) return false;
        Guid active = PowerPolicySnapshot.ActiveScheme() ?? throw new InvalidOperationException(L.T("无法读取当前电源计划。"));
        if (_plan is { } plan && active != plan.Dedicated)
        {
            Restore();
            _yielded = true;
            return false;
        }

        try
        {
            _plan ??= CreatePlan(active);
            Guid dedicated = _plan.Dedicated;
            if (_applied == (ac, value) && Settings.All(setting => PowerPolicySnapshot.ReadIndex(dedicated, setting, ac) == value)) return true;
            foreach (Guid setting in Settings)
                Check(ac ? PowerWriteACValueIndex(0, dedicated, PowerPolicySnapshot.Processor, setting, value)
                    : PowerWriteDCValueIndex(0, dedicated, PowerPolicySnapshot.Processor, setting, value));
            if (PowerPolicySnapshot.ActiveScheme() != active)
            {
                Restore();
                _yielded = true;
                return false;
            }
            Check(PowerSetActiveScheme(0, dedicated));
            if (PowerPolicySnapshot.ActiveScheme() != dedicated || Settings.Any(setting => PowerPolicySnapshot.ReadIndex(dedicated, setting, ac) != value))
                throw new InvalidOperationException(L.T("Windows 电源计划或 EPP 读回与请求不符。"));
            _applied = (ac, value);
            return true;
        }
        catch { Restore(); throw; }
    }

    private Plan CreatePlan(Guid original)
    {
        Check(PowerDuplicateScheme(0, original, out nint pointer));
        Guid clone;
        try { clone = Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
        try
        {
            byte[] name = Encoding.Unicode.GetBytes(SchemeName + '\0');
            Check(PowerWriteFriendlyName(0, clone, 0, 0, name, (uint)name.Length));
            var plan = new Plan(original, clone);
            Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
            File.WriteAllText(_journal + ".tmp", JsonSerializer.Serialize(plan));
            File.Move(_journal + ".tmp", _journal, true);
            return plan;
        }
        catch { PowerDeleteScheme(0, clone); throw; }
    }

    public void Reset() { Restore(); _yielded = false; }

    public void Restore()
    {
        if (_plan is not { } plan) return;
        var name = new byte[256];
        uint length = (uint)name.Length;
        if (PowerReadFriendlyName(0, plan.Dedicated, 0, 0, name, ref length) == 0 &&
            Encoding.Unicode.GetString(name, 0, (int)length).TrimEnd('\0') == SchemeName && plan.Original != plan.Dedicated)
        {
            Guid active = PowerPolicySnapshot.ActiveScheme() ?? throw new InvalidOperationException(L.T("无法确认当前电源计划。"));
            if (active == plan.Dedicated)
            {
                Check(PowerSetActiveScheme(0, plan.Original));
                if (PowerPolicySnapshot.ActiveScheme() != plan.Original) throw new InvalidOperationException(L.T("未能恢复原电源计划。"));
            }
            Check(PowerDeleteScheme(0, plan.Dedicated));
        }
        File.Delete(_journal);
        _plan = null;
        _applied = null;
    }

    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
}
