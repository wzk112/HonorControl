using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HonorControl.Shared;
namespace HonorControl.Network;
internal sealed class NetworkEngine
{
    private readonly SemaphoreSlim _gate = new(1);
    private NetworkConfig _config = new();
    private object _snapshot = new { Enabled = false, Scene = "idle", Apps = Array.Empty<object>(), Adapters = Array.Empty<object>(), Error = "", VerifiedPolicies = 0 };
    private string _applied = "", _error = "", _wifiNote = "";
    private bool _everSynchronized;
    private readonly Dictionary<string, (long Rx, long Tx, long Time)> _counters = [];
    private long _lastWifi;
    private readonly WifiRecovery _wifi = new();
    private static readonly HashSet<string> Games = new(["League of Legends", "YuanShen", "GenshinImpact", "BH3", "csgo", "cs2", "crossfire", "CF", "StarRail", "ZenlessZoneZero", "VALORANT-Win64-Shipping", "r5apex", "FortniteClient-Win64-Shipping", "Overwatch", "RainbowSix", "RainbowSix_Vulkan", "GTA5", "bf6", "DeltaForceClient-Win64-Shipping", "Client-Win64-Shipping"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Meetings = new(["WeChat", "Weixin", "QQ", "DingTalk", "WXWork", "wemeetapp", "Feishu", "Zoom", "ms-teams", "Teams", "Welink"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Media = new(["QQLive", "YOUKU", "QyClient", "QyPlayer", "mgtv", "HuyaClient", "DouyuLive", "哔哩哔哩", "obs64", "bililive"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Downloaders = new(["Thunder", "DownloadSDKServer", "steam", "steamwebhelper", "steamservice", "aDrive", "BaiduNetdisk", "BaiduNetdiskHost", "aria2c", "qbittorrent", "fdm", "IDMan", "HnAppStore"], StringComparer.OrdinalIgnoreCase);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    private string ConfigPath => Path.Combine(RuntimeIdentity.UserState, "network-settings.json");
    public NetworkEngine() { try { if (File.Exists(ConfigPath)) _config = Validate(JsonSerializer.Deserialize<NetworkConfig>(File.ReadAllText(ConfigPath), Wire.Options) ?? new()); } catch { } }
    public object Snapshot => _snapshot;
    public async Task<object> ConfigureAsync(NetworkConfig value)
    {
        var config = Validate(value);
        await _gate.WaitAsync();
        try
        {
            if (_config.Enabled != config.Enabled || _config.RecoverWifi != config.RecoverWifi || _config.SmartWifi != config.SmartWifi) _lastWifi = 0;
            _config = config;
            _applied = "";
            await UpdateCoreAsync();
            return _snapshot;
        }
        finally { _gate.Release(); }
    }
    private static NetworkConfig Validate(NetworkConfig config)
    {
        if (config.Scene is not ("auto" or "game" or "meeting" or "media" or "download")) throw new ArgumentException("未知网络场景。");
        if (!double.IsFinite(config.BackgroundUploadMbps) || config.BackgroundUploadMbps < .1 || config.BackgroundUploadMbps > 100000) throw new ArgumentException("后台上传上限超出范围。");
        var paths = (config.CustomPriorityApps ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 16 || paths.Any(x => x.Length > 1024 || !Path.IsPathFullyQualified(x) || x.Length < 4 || x[1] != ':' || x[2] != '\\' || !x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(x))) throw new ArgumentException("请选择本地应用的 EXE，最多 16 项。");
        return config with { CustomPriorityApps = paths };
    }
    public async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await _gate.WaitAsync(cancellation);
            try { await UpdateCoreAsync(); } catch (Exception ex) { _error = ex.Message; _applied = ""; }
            finally { _gate.Release(); }
            await Task.Delay(3000, cancellation);
        }
    }
    private sealed record RunningApp(string Name, string Path, string Scene, int Pid);
    private sealed record Policy(string Path, int Dscp, double UploadMbps);
    private async Task UpdateCoreAsync()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        var apps = new List<RunningApp>();
        foreach (var process in Process.GetProcesses())
        using (process)
        try
        {
            string name = process.ProcessName;
            string category = Games.Contains(name) ? "game" : Meetings.Contains(name) ? "meeting" : Media.Contains(name) ? "media" : Downloaders.Contains(name) ? "download" :
                SceneCatalog.Classify(name + ".exe") switch { 1 or 17 => "game", 0 => "meeting", 4 => "media", _ => "" };
            if (category.Length == 0 && !(_config.CustomPriorityApps?.Any(x => Path.GetFileNameWithoutExtension(x).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false)) continue;
            string? path = process.MainModule?.FileName; if (path is null) continue;
            if (_config.CustomPriorityApps?.Contains(path, StringComparer.OrdinalIgnoreCase) == true) category = "custom";
            if (category.Length > 0) apps.Add(new(name, path, category, process.Id));
        }
        catch { }
        string scene = _config.Scene;
        if (scene == "auto")
        {
            string? focusScene = apps.FirstOrDefault(x => x.Pid == foreground)?.Scene;
            scene = focusScene is "game" or "meeting" or "media" or "download" ? focusScene : apps.Any(x => x.Scene == "game") ? "game" : apps.Any(x => x.Scene == "meeting") ? "meeting" : apps.Any(x => x.Scene == "media") ? "media" : apps.Any(x => x.Scene == "download") ? "download" : "idle";
        }
        bool enabled = _config.Enabled && !File.Exists(RuntimeIdentity.PauseFile);
        var priority = apps.Where(x => x.Scene == scene || x.Scene == "custom").DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        var policies = new List<Policy>();
        if (enabled)
        {
            policies.AddRange(priority.Select(x => new Policy(x.Path, scene == "media" || scene == "download" ? 34 : 46, 0)));
            if (_config.LimitBackgroundUploads && priority.Length > 0 && scene != "download")
                policies.AddRange(apps.Where(x => x.Scene == "download" && !priority.Any(y => y.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase))).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(24).Select(x => new Policy(x.Path, 0, _config.BackgroundUploadMbps)));
        }
        string signature = Wire.Encode(policies.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray());
        if (!_everSynchronized || signature != _applied)
        {
            try { await ApplyPoliciesAsync(signature); _applied = signature; _everSynchronized = true; _error = ""; }
            catch (Exception ex) { _error = ex.Message; }
        }
        if (!enabled || (!_config.SmartWifi && !_config.RecoverWifi)) { _wifi.Reset(); _wifiNote = ""; }
        else if (Stopwatch.GetTimestamp() - _lastWifi > 8 * Stopwatch.Frequency)
        {
            _lastWifi = Stopwatch.GetTimestamp();
            try
            {
                var scan = WifiNative.ReadSnapshot();
                var now = DateTimeOffset.UtcNow;
                bool protectedScene = scene is "game" or "meeting" || apps.Any(x => x.Scene is "game" or "meeting");
                var current = scan.Networks.FirstOrDefault(x => x.Connected);
                bool disconnected = current is null;
                var target = _wifi.Evaluate(scan, now, _config.RecoverWifi, _config.SmartWifi, protectedScene, disconnected && HasOtherUplink());
                if (target is not null)
                {
                    _wifi.MarkAttempt(now);
                    await WifiNative.ConnectAutomaticAsync(target, current);
                    _wifiNote = disconnected ? "已重新连接原 WLAN。" : "弱网切换已读回。";
                }
                else if (!_wifiNote.StartsWith("已重新连接", StringComparison.Ordinal) && !_wifiNote.StartsWith("弱网切换已", StringComparison.Ordinal))
                    _wifiNote = protectedScene && !disconnected ? "游戏或会议期间保持当前 WLAN。" : "正在监测 WLAN 连接。";
            }
            catch (Exception ex) { _wifiNote = ex.Message; }
        }
        _snapshot = new { Enabled = enabled, Config = _config, Scene = scene, PriorityApps = priority.Select(x => new { x.Name, x.Path }).ToArray(), BackgroundApps = policies.Where(x => x.UploadMbps > 0).Select(x => new { Name = Path.GetFileName(x.Path), x.UploadMbps }).ToArray(), VerifiedPolicies = _error.Length == 0 ? policies.Count : 0, Error = _error, WifiNote = _wifiNote, Adapters = ReadAdapters() };
    }
    private static bool HasOtherUplink() => NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
        adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not
            (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
        adapter.GetIPProperties().GatewayAddresses.Any(gateway => !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any)));
    private object[] ReadAdapters()
    {
        var result = new List<object>(); long now = Stopwatch.GetTimestamp();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        try
        {
            var stats = adapter.GetIPStatistics(); double rx = 0, tx = 0;
            if (_counters.TryGetValue(adapter.Id, out var old)) { double seconds = (now - old.Time) / (double)Stopwatch.Frequency; if (seconds > 0) { rx = Math.Max(0, (stats.BytesReceived - old.Rx) / seconds); tx = Math.Max(0, (stats.BytesSent - old.Tx) / seconds); } }
            _counters[adapter.Id] = (stats.BytesReceived, stats.BytesSent, now);
            result.Add(new { adapter.Name, adapter.Description, Wifi = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, LinkMbps = adapter.Speed / 1000000d, DownloadBytesPerSecond = rx, UploadBytesPerSecond = tx });
        }
        catch { }
        return result.ToArray();
    }
    private async Task ApplyPoliciesAsync(string json)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "NetworkPolicy.ps1"), "-Payload", Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法应用场景网络规则。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(true); } catch { } throw; }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error.Trim() : output.Trim());
        using var document = JsonDocument.Parse(output); if (!document.RootElement.GetProperty("Verified").GetBoolean()) throw new IOException("网络规则读回失败。");
    }
}
