using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HonorControl.Shared;

internal sealed class SceneContext
{
    internal sealed record Sample(string? Path, int Scene, int Level, bool Idle);
    private (int Id, long Start, TimeSpan Cpu, long Time, int Level)? _previous;
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);

    public Sample Read()
    {
        var input = new LastInput { Size = 8 };
        bool idle = GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) >= 60_000;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint id);
        if (id == 0) { _previous = null; return new(null, idle ? 3 : -1, 2, idle); }
        try
        {
            using var process = Process.GetProcessById((int)id);
            string? path = process.MainModule?.FileName;
            long start = process.StartTime.ToFileTimeUtc();
            TimeSpan cpu = process.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            int level = 2;
            if (_previous is { } previous && previous.Id == process.Id && previous.Start == start)
            {
                double seconds = Stopwatch.GetElapsedTime(previous.Time, now).TotalSeconds;
                if (seconds is >= 1 and <= 20) level = LoadLevel((cpu - previous.Cpu).TotalSeconds / seconds, previous.Level);
            }
            _previous = (process.Id, start, cpu, now, level);
            int scene = SceneCatalog.Classify(process.ProcessName + ".exe");
            if (scene == -1 && idle) scene = 3;
            return new(path, scene, level, idle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _previous = null;
            return new(null, idle ? 3 : -1, 2, idle);
        }
    }
    internal static int LoadLevel(double cores, int previous) => cores >= (previous == 3 ? .55 : .75) ? 3 : cores <= (previous == 1 ? .25 : .15) ? 1 : 2;
}
