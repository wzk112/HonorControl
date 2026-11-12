using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HonorControl;

internal sealed record HotkeyChord(uint Modifiers, uint Key)
{
    public void Validate()
    {
        if ((Modifiers & ~15u) != 0 || (Modifiers & 11u) == 0 || Key < 0x20 || Key > 0xFE || Key is 0x5B or 0x5C or 0x5D or 0x7B || Key is >= 0xA0 and <= 0xA5)
            throw new ArgumentException(L.T("快捷键需要包含 Ctrl、Alt 或 Win，以及一个普通按键；F12 为系统保留键。"));
        if ((Modifiers & 3) == 3 && Key == 0x2E) throw new ArgumentException(L.T("Ctrl + Alt + Delete 为系统保留快捷键。"));
    }
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(Key switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (Key - 0x6F),
            0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
            0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2D => "Insert", 0x2E => "Delete",
            >= 0x60 and <= 0x69 => "Num " + (Key - 0x60),
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => "VK " + Key.ToString("X2")
        });
        return string.Join(" + ", parts);
    }
}

internal sealed record HotkeySettings(bool Enabled, HotkeyChord Performance, HotkeyChord RefreshRate)
{
    public static HotkeySettings Default => new(false, new(3, 0x50), new(3, 0x52));
    public void Validate()
    {
        if (Performance is null || RefreshRate is null) throw new ArgumentException(L.T("快捷键配置不完整。"));
        Performance.Validate(); RefreshRate.Validate();
        if (Enabled && Performance == RefreshRate) throw new ArgumentException(L.T("性能与刷新率需要使用不同的快捷键。"));
    }
}

internal sealed class GlobalHotkeys : IDisposable
{
    private const int PerformanceId = 0x4841, RefreshId = 0x4842;
    private readonly nint _hwnd;
    private readonly SubclassProc _callback;
    private readonly Action<string> _action;
    private readonly Func<int, HotkeyChord, bool> _register;
    private readonly Action<int> _unregister;
    private readonly HashSet<int> _registered = [];
    private bool _suspended, _disposed;
    public HotkeySettings Configuration { get; private set; } = HotkeySettings.Default with { Enabled = false };
    public string Status { get; private set; } = L.T("备用组合键已关闭。");
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam);

    public GlobalHotkeys(nint hwnd, Action<string> action) : this(hwnd, action, (id, chord) => RegisterHotKey(hwnd, id, chord.Modifiers | 0x4000, chord.Key), id => UnregisterHotKey(hwnd, id)) { }
    internal GlobalHotkeys(nint hwnd, Action<string> action, Func<int, HotkeyChord, bool> register, Action<int> unregister)
    {
        _hwnd = hwnd; _action = action; _register = register; _unregister = unregister; _callback = WindowProc;
        if (hwnd != 0 && !SetWindowSubclass(hwnd, _callback, 84, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Configure(HotkeySettings settings, bool strict = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        settings.Validate();
        var previous = Configuration;
        Release();
        Configuration = settings;
        var errors = Register();
        if (strict && errors.Count != 0)
        {
            Release(); Configuration = previous; Register();
            throw new InvalidOperationException(string.Join("；", errors) + L.T("。原快捷键已保留。"));
        }
    }
    private List<string> Register()
    {
        var errors = new List<string>();
        if (_suspended) { Status = L.T("正在录入，已临时暂停全局快捷键。"); return errors; }
        if (!Configuration.Enabled) { Status = L.T("备用组合键已关闭。"); return errors; }
        foreach (var (id, chord, name) in new[] { (PerformanceId, Configuration.Performance, L.T("性能")), (RefreshId, Configuration.RefreshRate, L.T("刷新率")) })
        {
            if (_register(id, chord)) _registered.Add(id);
            else errors.Add(L.F($"{name}快捷键 {chord} 无法注册，可能已被其他应用或系统占用"));
        }
        Status = errors.Count == 0 ? L.F($"已启用 · 性能 {Configuration.Performance} · 刷新率 {Configuration.RefreshRate}") : string.Join("；", errors);
        return errors;
    }
    public void Suspend() { if (_disposed) return; _suspended = true; Release(); Status = L.T("正在录入，已临时暂停全局快捷键。"); }
    public void Resume() { if (_disposed || !_suspended) return; _suspended = false; Register(); }
    private void Release() { foreach (int id in _registered) _unregister(id); _registered.Clear(); }
    private nint WindowProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint data)
    {
        int keyId = unchecked((int)wparam);
        if (msg == 0x0312 && !_suspended && _registered.Contains(keyId))
        {
            var chord = keyId == PerformanceId ? Configuration.Performance : Configuration.RefreshRate;
            ulong payload = unchecked((ulong)lparam.ToInt64());
            if ((payload & 15) == chord.Modifiers && ((payload >> 16) & 0xffff) == chord.Key)
                _action(keyId == PerformanceId ? "cycle-performance" : "cycle-refresh");
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wparam, lparam);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Release(); if (_hwnd != 0) RemoveWindowSubclass(_hwnd, _callback, 84); _disposed = true;
    }
}

internal static class PerformanceCycle
{
    public static string? UnavailableReason(int target, bool ac, int battery, bool barrel, double pdWatts)
    {
        if (target == 0) return null;
        if (!ac) return L.T("请先连接外接电源。");
        if (battery > 100) return L.T("无法确认电池电量，请稍后重试。");
        if (battery < 20) return L.T("电池电量需达到 20%。");
        return target == 3 && !barrel && pdWatts < 135 ? L.T("HUNTER 需要圆口电源或至少 135 W 的 PD 电源。") : null;
    }

    public static int Next(int current, bool ac, int battery, bool barrel, double pdWatts)
    {
        bool high = ac && battery is >= 20 and <= 100;
        int[] modes = !high ? [0] : barrel || pdWatts >= 135 ? [0, 1, 3] : [0, 1];
        int index = Array.IndexOf(modes, current);
        return modes[(index + 1) % modes.Length];
    }
}
