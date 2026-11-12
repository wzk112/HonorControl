using System.Runtime.InteropServices;

namespace HonorControl;

internal static class MicrophoneMute
{
    private const int Capture = 1, Communications = 2, Multimedia = 1;
    private const int NotFound = unchecked((int)0x80070490);
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static bool Read() => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
        return muted;
    });

    public static bool Toggle() => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool before));
        Marshal.ThrowExceptionForHR(endpoint.SetMute(!before, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool after));
        if (after == before) throw new InvalidOperationException(L.T("麦克风静音状态未改变。"));
        return after;
    });

    private static bool WithEndpoint(Func<IAudioEndpointVolume, bool> action)
    {
        var type = Type.GetTypeFromCLSID(EnumeratorClass, throwOnError: true)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        IMMDevice? device = null;
        object? activated = null;
        try
        {
            int result = enumerator.GetDefaultAudioEndpoint(Capture, Communications, out device);
            if (result == NotFound) result = enumerator.GetDefaultAudioEndpoint(Capture, Multimedia, out device);
            if (result == NotFound) throw new InvalidOperationException(L.T("没有可用的默认麦克风。"));
            Marshal.ThrowExceptionForHR(result);
            var volumeId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device!.Activate(ref volumeId, 23, IntPtr.Zero, out activated));
            return action((IAudioEndpointVolume)activated);
        }
        finally
        {
            if (activated is not null) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float volume, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float volume);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float volume, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
