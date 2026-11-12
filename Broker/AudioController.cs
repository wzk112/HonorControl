using Microsoft.Win32;

namespace HonorControl;

internal sealed class AudioController
{
    private const string SpeakerKey = @"SOFTWARE\HonorAPO";
    private const string HeadphoneKey = @"SOFTWARE\HonorHpAPO";
    private const string ValueName = "HonorAlgoMode";
    private const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    private const string ForteEffect = "{70306ef0-7851-11dc-8314-0800200c9a66}";
    private readonly object _gate = new();

    public object Read()
    {
        lock (_gate) return ReadState();
    }

    public object Apply(string command)
    {
        lock (_gate)
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (!string.Equals(bios?.GetValue("SystemProductName") as string, "DRA-XX", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("音效写入仅在已验证的 DRA-XX 机型上可用。");
            switch (command)
            {
                case "speaker-spatial": SetMode(SpeakerKey, 11); break;
                case "speaker-music": SetMode(SpeakerKey, 1); break;
                case "speaker-game": SetMode(SpeakerKey, 2); break;
                case "speaker-movie": SetMode(SpeakerKey, 3); break;
                case "speaker-vocal": SetMode(SpeakerKey, 4); break;
                case "speaker-standard": SetMode(SpeakerKey, 5); break;
                case "headphones-spatial": SetMode(HeadphoneKey, 1); break;
                case "headphones-over-ear": SetMode(HeadphoneKey, 2); break;
                case "headphones-earbuds": SetMode(HeadphoneKey, 3); break;
                case "headphones-in-ear": SetMode(HeadphoneKey, 4); break;
                case "headphones-stereo": SetMode(HeadphoneKey, 5); break;
                case "mic-single": MicMode(2); break;
                case "mic-group": MicMode(3); break;
                case "mic-ambient": MicMode(19); break;
                default: throw new ArgumentException("未知的音频设置。");
            }
            return ReadState();
        }
    }

    private static object ReadState()
    {
        int? mic = null;
        string micStatus = "";
        try { mic = MicMode(); }
        catch (Exception ex) { micStatus = ex.Message; }
        return new { Speaker = ReadMode(SpeakerKey), Headphones = ReadMode(HeadphoneKey), Microphone = mic, MicrophoneAvailable = mic.HasValue, MicStatus = micStatus };
    }

    private static int? ReadMode(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue("ParameterPath") is string parameters && Directory.Exists(parameters) &&
            key.GetValue(ValueName) is int mode ? mode : null;
    }

    private static void SetMode(string path, int mode)
    {
        if (ReadMode(path) is not int before)
            throw new InvalidOperationException("找不到已安装的 HONOR Histen 音频组件或校准文件。");
        if (before == mode) return;
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true) ??
            throw new InvalidOperationException("无法打开 HONOR Histen 设置。");
        key.SetValue(ValueName, mode, RegistryValueKind.DWord);
        if (ReadMode(path) != mode)
            throw new InvalidOperationException("HONOR Histen 音效设置读回不一致。");
    }

    private static string InternalMicrophone()
    {
        using var captures = Registry.LocalMachine.OpenSubKey(CaptureKey);
        if (captures is null) throw new InvalidOperationException("找不到 Windows 录音端点。");
        var candidates = captures.GetSubKeyNames().Where(id =>
        {
            if (!Guid.TryParse(id, out _)) return false;
            using var device = captures.OpenSubKey(id);
            if (device?.GetValue("DeviceState") is not 1) return false;
            using var effect = device.OpenSubKey(@"FxProperties\" + ForteEffect);
            return effect is not null;
        }).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("未找到唯一的 Fortemedia 内置麦克风阵列。");
        return candidates[0];
    }

    private static int MicMode(int? requested = null)
    {
        using var microphone = new FortemediaClient(InternalMicrophone());
        return requested is int mode ? microphone.Apply(mode) : microphone.Mode();
    }
}
