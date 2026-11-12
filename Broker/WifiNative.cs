using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace HonorControl.Network;
public sealed record WifiCandidate(string InterfaceId, string Profile, string Ssid, int Signal, bool Connected, bool Saved, bool Secured);
internal sealed record WifiSnapshot(WifiCandidate[] Networks, HashSet<string> Disconnected);
public static class WifiNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct InterfaceInfo { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public uint State; }
    [StructLayout(LayoutKind.Sequential)] private struct Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Available
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
        public Ssid Ssid; public uint BssType, BssCount;
        [MarshalAs(UnmanagedType.Bool)] public bool Connectable;
        public uint Reason, PhyCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] PhyTypes;
        [MarshalAs(UnmanagedType.Bool)] public bool MorePhy;
        public uint Signal;
        [MarshalAs(UnmanagedType.Bool)] public bool Security;
        public uint Authentication, Cipher, Flags, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ConnectParameters { public uint Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile; public nint Ssid, Bssids; public uint BssType, Flags; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(nint handle, ref Guid id, uint flags, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(nint handle, ref Guid id, nint ssid, nint ie, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(nint handle, ref Guid id, ref ConnectParameters parameters, nint reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    private static void Check(uint result) { if (result == 5) throw new InvalidOperationException("Windows 未授予 WLAN 信息访问权限。请在 Windows 的 WLAN 面板选网；本应用不会修改隐私设置。"); if (result != 0) throw new Win32Exception((int)result); }
    public static WifiCandidate[] Read(bool requestScan = false) => ReadSnapshot(requestScan).Networks;
    internal static WifiSnapshot ReadSnapshot(bool requestScan = false)
    {
        Check(WlanOpenHandle(2, 0, out _, out nint handle));
        try
        {
            Check(WlanEnumInterfaces(handle, 0, out nint interfaces));
            try
            {
                int count = Marshal.ReadInt32(interfaces); if (count < 0 || count > 16) throw new InvalidDataException("Invalid WLAN interface count.");
                var found = new List<WifiCandidate>();
                var disconnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<InterfaceInfo>(interfaces + 8 + i * Marshal.SizeOf<InterfaceInfo>()); var id = item.Id;
                    if (item.State == 4) disconnected.Add(id.ToString());
                    if (requestScan) Check(WlanScan(handle, ref id, 0, 0, 0));
                    Check(WlanGetAvailableNetworkList(handle, ref id, 0, 0, out nint networks));
                    try
                    {
                        int n = Marshal.ReadInt32(networks); if (n < 0 || n > 1024) throw new InvalidDataException("Invalid WLAN network count.");
                        for (int j = 0; j < n; j++)
                        {
                            var wifi = Marshal.PtrToStructure<Available>(networks + 8 + j * Marshal.SizeOf<Available>());
                            if (wifi.Ssid.Length > 32 || (!wifi.Connectable && (wifi.Flags & 1) == 0)) continue;
                            string name = Encoding.UTF8.GetString(wifi.Ssid.Bytes, 0, (int)wifi.Ssid.Length);
                            found.Add(new(id.ToString(), wifi.Profile, name, (int)Math.Clamp(wifi.Signal, 0, 100), (wifi.Flags & 1) != 0, !string.IsNullOrEmpty(wifi.Profile), wifi.Security));
                        }
                    }
                    finally { WlanFreeMemory(networks); }
                }
                return new(found.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Signal).ToArray(), disconnected);
            }
            finally { WlanFreeMemory(interfaces); }
        }
        finally { WlanCloseHandle(handle, 0); }
    }
    public static Task ConnectAsync(string interfaceId, string profile) => ConnectAsync(interfaceId, profile, false, null);

    internal static Task ConnectAutomaticAsync(WifiCandidate target, WifiCandidate? previous) =>
        ConnectAsync(target.InterfaceId, target.Profile, true, previous);

    private static async Task ConnectAsync(string interfaceId, string profile, bool automatic, WifiCandidate? previous)
    {
        var scan = ReadSnapshot();
        var candidate = scan.Networks.FirstOrDefault(x => x.InterfaceId == interfaceId && x.Profile == profile && x.Saved) ?? throw new ArgumentException("只能连接当前可见且已经保存凭据的 WLAN。");
        if (candidate.Connected) return;
        if (automatic)
        {
            var current = scan.Networks.FirstOrDefault(network => network.Connected);
            bool unchanged = previous is null ? current is null && scan.Disconnected.Contains(interfaceId) :
                current is not null && current.InterfaceId == previous.InterfaceId && current.Profile == previous.Profile && current.Signal < 35 && candidate.Signal >= current.Signal + 20;
            if (!unchanged) throw new InvalidOperationException("WLAN 状态已变化，等待下一次检测。");
        }
        Check(WlanOpenHandle(2, 0, out _, out nint handle));
        try { var id = Guid.Parse(candidate.InterfaceId); var args = new ConnectParameters { Mode = 0, Profile = candidate.Profile, BssType = 3 }; Check(WlanConnect(handle, ref id, ref args, 0)); }
        finally { WlanCloseHandle(handle, 0); }
        for (int i = 0; i < 12; i++) { await Task.Delay(1000); if (Read().Any(x => x.InterfaceId == candidate.InterfaceId && x.Profile == profile && x.Connected)) return; }
        throw new TimeoutException("WLAN 连接尚未读回确认，请检查 Windows 网络状态。");
    }
}
