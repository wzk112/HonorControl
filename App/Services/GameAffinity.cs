using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace HonorControl;

internal sealed class GameAffinity
{
    private readonly string _file = Path.Combine(AppPaths.UserState, "affinity-restore.json");
    private List<Change> _changes = [];
    private readonly string _cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "";
    private sealed record Change(int Id, long Start, string Path, ulong Original, ulong Applied);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessAffinityMask(nint process, out nuint mask, out nuint system);

    public GameAffinity()
    {
        if (File.Exists(_file)) _changes = JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(_file)) ?? [];
        RestoreAll();
    }

    public int Update(uint foreground, bool enabled)
    {
        foreach (var change in _changes.ToArray())
            if (!enabled || change.Id != foreground) Restore(change);
        if (!enabled || foreground == 0 || _changes.Any(change => change.Id == foreground)) return _changes.Count;
        try
        {
            using var process = Process.GetProcessById((int)foreground);
            ulong mask = Mask(_cpu, process.ProcessName);
            if (mask == 0 || process.SessionId != Process.GetCurrentProcess().SessionId) return _changes.Count;
            string? path = process.MainModule?.FileName;
            if (path is null) return _changes.Count;
            if (!GetProcessAffinityMask(process.Handle, out nuint original, out nuint system)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((mask & (ulong)system) != mask || mask == (ulong)original) return _changes.Count;
            var change = new Change(process.Id, process.StartTime.ToFileTimeUtc(), path, (ulong)original, mask);
            _changes.Add(change);
            Save();
            Set(process, mask);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            RestoreAll();
        }
        return _changes.Count;
    }

    internal static ulong Mask(string cpu, string process) => (cpu, process.ToLowerInvariant()) switch
    {
        (var name, "pes2021" or "fl_2024") when name.Contains("155H", StringComparison.Ordinal) => 1047555,
        (var name, "acodyssey") when name.Contains("155H", StringComparison.Ordinal) => 4194303,
        (var name, "pes2021" or "fl_2024") when name.Contains("125H", StringComparison.Ordinal) => 64515,
        (var name, "acodyssey") when name.Contains("125H", StringComparison.Ordinal) => 262143,
        _ => 0
    };

    public void RestoreAll() { foreach (var change in _changes.ToArray()) Restore(change); }

    private void Restore(Change change)
    {
        try
        {
            using var process = Process.GetProcessById(change.Id);
            if (process.StartTime.ToFileTimeUtc() == change.Start && StringComparer.OrdinalIgnoreCase.Equals(process.MainModule?.FileName, change.Path) &&
                ReadMask(process) == change.Applied) Set(process, change.Original);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        _changes.Remove(change);
        Save();
    }
    private static void Set(Process process, ulong mask)
    {
        process.ProcessorAffinity = (nint)mask;
        if (ReadMask(process) != mask) throw new InvalidOperationException("CPU affinity readback did not match.");
    }
    private static ulong ReadMask(Process process)
    {
        if (!GetProcessAffinityMask(process.Handle, out nuint mask, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (ulong)mask;
    }
    private void Save()
    {
        Directory.CreateDirectory(AppPaths.UserState);
        File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_changes));
        File.Move(_file + ".tmp", _file, true);
    }
}
