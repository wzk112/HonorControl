using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HonorControl;

internal static class DisplayRefreshRate
{
    internal sealed record Snapshot(string Device, string Identity, uint Width, uint Height, uint Bits, uint Orientation, uint Flags, double Hertz, int[] Rates);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields; public int X, Y; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
        public ushort LogPixels; public uint Bits, Width, Height, DisplayFlags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, nint pathBuffer, ref uint modes, nint modeBuffer, nint topology);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsExW(string device, int index, ref DevMode mode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, ref DevMode mode, nint hwnd, uint flags, nint parameter);

    private static DevMode Empty() => new() { Device = "", Form = "", Size = (ushort)Marshal.SizeOf<DevMode>() };
    private static (string Device, string Identity, double Hertz) InternalDisplay()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int error = GetDisplayConfigBufferSizes(2, out uint paths, out uint modes);
            if (error != 0) throw new Win32Exception(error);
            if (paths is 0 or > 64 || modes > 256) throw new InvalidOperationException(L.T("显示器配置不在支持范围内。"));
            nint pathBuffer = Marshal.AllocHGlobal(checked((int)paths * 72));
            nint modeBuffer = Marshal.AllocHGlobal(Math.Max(64, checked((int)modes * 64)));
            try
            {
                error = QueryDisplayConfig(2, ref paths, pathBuffer, ref modes, modeBuffer, 0);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception(error);
                var values = new List<(string Source, bool Internal, string Identity, double Hertz)>();
                for (int i = 0; i < paths; i++)
                {
                    nint path = pathBuffer + i * 72;
                    uint technology = unchecked((uint)Marshal.ReadInt32(path, 36));
                    var source = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = Marshal.PtrToStructure<Luid>(path), Id = unchecked((uint)Marshal.ReadInt32(path, 8)), Name = "" };
                    error = DisplayConfigGetDeviceInfo(ref source);
                    if (error != 0) throw new Win32Exception(error);
                    uint denominator = unchecked((uint)Marshal.ReadInt32(path, 52));
                    double hz = denominator == 0 ? 0 : unchecked((uint)Marshal.ReadInt32(path, 48)) / (double)denominator;
                    string identity = $"{Marshal.ReadInt64(path, 20):X16}:{Marshal.ReadInt32(path, 28):X8}";
                    values.Add((source.Name, technology is 6 or 11 or 13 or 0x80000000, identity, hz));
                }
                var internalPaths = values.Where(v => v.Internal).ToArray();
                if (internalPaths.Length != 1) throw new InvalidOperationException(L.T("刷新率切换需要一块启用中的内置屏幕。"));
                var selected = internalPaths[0];
                if (values.Count(v => v.Source == selected.Source) != 1) throw new InvalidOperationException(L.T("镜像投影时无法单独切换内屏刷新率，请先使用扩展显示或仅内屏。"));
                return (selected.Source, selected.Identity, selected.Hertz);
            }
            finally { Marshal.FreeHGlobal(pathBuffer); Marshal.FreeHGlobal(modeBuffer); }
        }
        throw new InvalidOperationException(L.T("显示器配置正在变化，请稍后重试。"));
    }

    public static Snapshot Read()
    {
        var display = InternalDisplay();
        var current = Empty();
        if (!EnumDisplaySettingsExW(display.Device, -1, ref current, 0)) throw new InvalidOperationException(L.T("无法读取内屏当前显示模式。"));
        var rates = new SortedSet<int>();
        for (int index = 0; index < 4096; index++)
        {
            var mode = Empty();
            if (!EnumDisplaySettingsExW(display.Device, index, ref mode, 0)) break;
            if (mode.Width == current.Width && mode.Height == current.Height && mode.Bits == current.Bits && mode.Orientation == current.Orientation && (mode.DisplayFlags & 2) == (current.DisplayFlags & 2) && mode.Frequency is > 1 and <= 1000)
                rates.Add((int)mode.Frequency);
        }
        if (rates.Count == 0) throw new InvalidOperationException(L.T("驱动没有提供当前分辨率支持的刷新率。"));
        return new(display.Device, display.Identity, current.Width, current.Height, current.Bits, current.Orientation, current.DisplayFlags, display.Hertz > 1 ? display.Hertz : current.Frequency, rates.ToArray());
    }
    internal static int NextRate(double current, int[] rates)
    {
        if (rates.Length == 0) throw new InvalidOperationException(L.T("没有可用刷新率。"));
        return rates.Order().FirstOrDefault(rate => rate > current + .6, rates.Min());
    }
    public static Snapshot Cycle() { var before = Read(); return Set(before, NextRate(before.Hertz, before.Rates)); }
    public static Snapshot Set(Snapshot before, int hertz)
    {
        if (!before.Rates.Contains(hertz)) throw new ArgumentException(L.T("刷新率不在内屏支持列表中。"));
        var live = Read();
        if (!SameDisplay(before, live) || live.Orientation != before.Orientation || live.Bits != before.Bits)
            throw new InvalidOperationException(L.T("显示布局或分辨率已变化，请重新读取刷新率。"));
        if (Math.Abs(live.Hertz - hertz) < .6) return live;
        var requested = Empty();
        if (!EnumDisplaySettingsExW(before.Device, -1, ref requested, 0)) throw new InvalidOperationException(L.T("无法读取切换前的显示模式。"));
        uint original = requested.Frequency;
        requested.Fields = 0x400000; requested.Frequency = (uint)hertz;
        Check(ChangeDisplaySettingsExW(before.Device, ref requested, 0, 2, 0));
        try
        {
            Check(ChangeDisplaySettingsExW(before.Device, ref requested, 0, 1, 0));
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(200);
                var after = Read();
                if (!SameDisplay(before, after)) throw new InvalidOperationException(L.T("切换后显示布局发生变化。"));
                if (Math.Abs(after.Hertz - hertz) < .6) return after;
            }
            throw new InvalidOperationException(L.T("刷新率读回与请求不符。"));
        }
        catch
        {
            try
            {
                var now = Read(); var mode = Empty();
                if (SameDisplay(before, now) && EnumDisplaySettingsExW(before.Device, -1, ref mode, 0) && (mode.Frequency == hertz || mode.Frequency == original))
                { mode.Fields = 0x400000; mode.Frequency = original; ChangeDisplaySettingsExW(before.Device, ref mode, 0, 1, 0); }
            }
            catch { }
            throw;
        }
    }
    private static bool SameDisplay(Snapshot a, Snapshot b) => a.Device == b.Device && a.Identity == b.Identity && a.Width == b.Width && a.Height == b.Height;
    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException(result == 1 ? L.T("该显示模式需要重启，本次未切换。") : L.F($"Windows 拒绝刷新率切换（{result}）。"));
    }
}
