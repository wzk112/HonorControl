using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal sealed class SceneScheduler
{
    private const uint ExecutionSpeed = 1;
    private const int ProcessPowerThrottling = 4;
    private readonly string _state = Path.Combine(AppPaths.UserState, "scene-scheduler.json");
    private readonly string _journal = Path.Combine(AppPaths.UserState, "scene-restore.json");
    private Settings _settings = new(false, null, null);
    private List<Change> _changes = [];
    private readonly GameAffinity _affinity = new();

    public sealed record Settings(bool Enabled, string? ForegroundApp, string? BackgroundApp, bool AutoForeground = false);
    private sealed record Change(int Id, long Start, string Path, bool Eco, uint Control, uint State);

    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottle { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(nint process, int information, ref PowerThrottle value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int information, ref PowerThrottle value, uint size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte Ac, BatteryFlag, BatteryPercent, StatusFlag; public uint Lifetime, FullLifetime; }

    public SceneScheduler()
    {
        if (File.Exists(_state)) _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_state)) ?? _settings;
        if (File.Exists(_journal)) _changes = JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(_journal)) ?? [];
        RestoreAll();
    }

    public Settings Configuration => _settings;
    public void Configure(Settings settings)
    {
        if (settings.ForegroundApp is not null && StringComparer.OrdinalIgnoreCase.Equals(settings.ForegroundApp, settings.BackgroundApp))
            throw new ArgumentException(L.T("前台和后台规则需要选择不同的应用。"));
        Save(_state, settings);
        _settings = settings;
    }

    public string Update()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        bool ac = GetSystemPowerStatus(out var power) && power.Ac == 1;
        return Update(foreground, ac);
    }

    internal string Update(uint foreground, bool ac)
    {
        bool active = _settings.Enabled;
        int affinity = _affinity.Update(foreground, active && ac && _settings.AutoForeground);
        string? automatic = active && ac && _settings.AutoForeground ? DetectForeground(foreground) : null;
        foreach (var change in _changes.ToArray())
        {
            bool desired = active && (change.Eco
                ? foreground != change.Id && Same(_settings.BackgroundApp, change.Path)
                : ac && foreground == change.Id && (Same(_settings.ForegroundApp, change.Path) || Same(automatic, change.Path)));
            if (!desired || !Alive(change)) Restore(change);
        }
        if (!active) return L.T("调度已关闭");
        if (ac) Apply(automatic ?? _settings.ForegroundApp, foreground, eco: false);
        Apply(_settings.BackgroundApp, foreground, eco: true);
        string foregroundLabel = ac ? L.T("等待匹配的前台应用") : L.T("电池供电，前台策略暂停");
        if (_changes.Any(c => !c.Eco)) foregroundLabel = L.T("前台 HighQoS 已读回");
        return foregroundLabel + (_changes.Any(c => c.Eco) ? L.T(" · 后台 EcoQoS 已读回") : "") + (affinity > 0 ? L.T(" · 游戏核心分配已读回") : "");
    }

    private static string? DetectForeground(uint foreground)
    {
        if (foreground == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)foreground);
            if (!SceneCatalog.IsForegroundPriority(process.ProcessName + ".exe")) return null;
            string? path = process.MainModule?.FileName;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
            return path is null || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? null : path;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private void Apply(string? path, uint foreground, bool eco)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        using (process)
        {
            if (process.SessionId != Process.GetCurrentProcess().SessionId || (process.Id == foreground) == eco || _changes.Any(c => c.Id == process.Id)) continue;
            if (!Same(process.MainModule?.FileName, path)) continue;
            var original = Read(process.Handle);
            uint wanted = eco ? ExecutionSpeed : 0;
            if (original.ControlMask == ExecutionSpeed && original.StateMask == wanted) continue;
            var change = new Change(process.Id, process.StartTime.ToFileTimeUtc(), path, eco, original.ControlMask, original.StateMask);
            _changes.Add(change);
            Save(_journal, _changes);
            Set(process.Handle, ExecutionSpeed, wanted);
        }
    }

    public void RestoreAll()
    {
        _affinity.RestoreAll();
        foreach (var change in _changes.ToArray()) Restore(change);
    }

    private void Restore(Change change)
    {
        if (Alive(change))
        {
            using var process = Process.GetProcessById(change.Id);
            var current = Read(process.Handle);
            uint applied = change.Eco ? ExecutionSpeed : 0;
            if (current.ControlMask == ExecutionSpeed && current.StateMask == applied)
                Set(process.Handle, change.Control, change.State);
        }
        _changes.Remove(change);
        Save(_journal, _changes);
    }

    private static bool Same(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
    private static bool Alive(Change change)
    {
        try
        {
            using var process = Process.GetProcessById(change.Id);
            return process.StartTime.ToFileTimeUtc() == change.Start && Same(process.MainModule?.FileName, change.Path);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static PowerThrottle Read(nint process)
    {
        var state = new PowerThrottle { Version = 1 };
        if (!GetProcessInformation(process, ProcessPowerThrottling, ref state, 12))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return state;
    }

    private static void Set(nint process, uint control, uint state)
    {
        var value = new PowerThrottle { Version = 1, ControlMask = control, StateMask = state };
        if (!SetProcessInformation(process, ProcessPowerThrottling, ref value, 12))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var readback = Read(process);
        if (readback.ControlMask != control || readback.StateMask != state)
            throw new InvalidOperationException(L.T("进程 QoS 读回与请求不符。"));
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
    }
}
