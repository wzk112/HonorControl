using System.Runtime.InteropServices;

namespace HonorControl;

internal static class PowerPolicySnapshot
{
    internal static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
    internal static readonly Guid EnergyPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    internal static readonly Guid EnergyPreferenceClass1 = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
    private static readonly Guid MaxProcessor = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, out uint value);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);

    public static string Read()
    {
        if (ActiveScheme() is not Guid scheme) return L.T("无法读取 Windows 电源计划。");
        return L.F($"Windows 电源计划 · {scheme:D}\n") +
            L.F($"CPU 能效偏好 EPP · 插电 {Value(scheme, EnergyPreference, true)} / 电池 {Value(scheme, EnergyPreference, false)}\n") +
            L.F($"最大处理器状态 · 插电 {Value(scheme, MaxProcessor, true)} / 电池 {Value(scheme, MaxProcessor, false)}");
    }

    internal static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(0, out nint pointer) != 0 || pointer == 0) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static string Value(Guid scheme, Guid setting, bool ac) => ReadIndex(scheme, setting, ac) is uint value ? $"{value}%" : "—";

    internal static uint? ReadIndex(Guid scheme, Guid setting, bool ac)
    {
        uint result = ac ? PowerReadACValueIndex(0, scheme, Processor, setting, out uint value)
            : PowerReadDCValueIndex(0, scheme, Processor, setting, out value);
        return result == 0 ? value : null;
    }
}
