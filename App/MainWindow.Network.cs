using System.Text.Json;
using HonorControl.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace HonorControl;

public sealed partial class MainWindow
{
    private static readonly string[] NetworkScenes = ["auto", "game", "meeting", "media", "download"];
    private string[] _priorityApps = [];
    private bool _loadingNetwork;
    private CancellationTokenSource? _download;

    private static string SceneLabel(string scene) => scene switch
    {
        "game" => L.T("游戏优先"), "meeting" => L.T("通话与会议优先"), "media" => L.T("视频与直播优先"),
        "download" => L.T("下载优先"), _ => L.T("等待识别应用")
    };

    private void UpdateNetwork(JsonElement state, bool loadSettings = false)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        bool enabled = IsTrue(state, "Enabled");
        NetworkSceneText.Text = enabled ? SceneLabel(state.Text("Scene")) : L.T("场景调度已关闭");
        string error = state.Text("Error", "");
        NetworkPolicyText.Text = error.Length > 0 ? L.T("规则应用失败：") + error : enabled
            ? L.F($"已读回 {Math.Max(0, state.Number("VerifiedPolicies"))} 条规则")
            : L.T("没有启用场景优先规则。");
        var priority = Items(state, "PriorityApps").Select(app => app.Text("Name")).ToArray();
        PriorityAppText.Text = priority.Length == 0 ? L.T("暂无正在运行的优先应用。") : L.T("已识别：") + string.Join("、", priority);
        var adapters = Items(state, "Adapters").ToArray();
        NetworkAdapterText.Text = adapters.Length == 0 ? L.T("未检测到活动网卡。") : string.Join("  ·  ", adapters.Select(adapter => $"{adapter.Text("Name")}  {Number(adapter, "LinkMbps"):0.#} Mbps"));
        NetworkRateText.Text = $"↓ {Rate(adapters.Sum(x => Number(x, "DownloadBytesPerSecond")))}   ↑ {Rate(adapters.Sum(x => Number(x, "UploadBytesPerSecond")))}";
        var wifiNote = state.Text("WifiNote", "");
        WifiAutomaticStatus.Text = wifiNote;
        if (!loadSettings || !state.TryGetProperty("Config", out var config)) return;
        _loadingNetwork = true;
        try
        {
            NetworkEnableToggle.IsOn = IsTrue(config, "Enabled");
            NetworkScenePicker.SelectedIndex = Math.Max(0, Array.IndexOf(NetworkScenes, config.Text("Scene")));
            LimitBackgroundCheck.IsChecked = IsTrue(config, "LimitBackgroundUploads");
            SmartWifiCheck.IsChecked = IsTrue(config, "SmartWifi");
            RecoverWifiCheck.IsChecked = IsTrue(config, "RecoverWifi");
            BackgroundUploadBox.Value = Number(config, "BackgroundUploadMbps");
            _priorityApps = Items(config, "CustomPriorityApps").Select(x => x.GetString()).OfType<string>().ToArray();
            CustomPriorityText.Text = _priorityApps.Length == 0 ? L.T("可添加未被自动识别的应用。") : L.T("额外优先：") + string.Join("、", _priorityApps.Select(Path.GetFileName));
        }
        finally { _loadingNetwork = false; }
    }

    private async Task ApplyNetworkAsync(string[]? priority = null)
    {
        if (!_ready || _loadingNetwork || _busy || _closing) return;
        var config = new NetworkConfig(NetworkEnableToggle.IsOn, NetworkScenes[Math.Max(0, NetworkScenePicker.SelectedIndex)],
            LimitBackgroundCheck.IsChecked == true, BackgroundUploadBox.Value, SmartWifiCheck.IsChecked == true, priority ?? _priorityApps, RecoverWifiCheck.IsChecked == true);
        await RunAsync(async () =>
        {
            var state = await _backend.ConfigureNetworkAsync(config);
            UpdateNetwork(state, true);
            Directory.CreateDirectory(AppPaths.UserState);
            File.WriteAllText(Path.Combine(AppPaths.UserState, "network-settings.json"), Wire.Encode(config));
            string error = state.Text("Error", "");
            if (error.Length != 0) throw new InvalidOperationException(error);
        }, L.T("场景设置已保存。"));
    }

    private async void NetworkConfig_Changed(object sender, RoutedEventArgs e) => await ApplyNetworkAsync();
    private async void NetworkScene_Changed(object sender, SelectionChangedEventArgs e) => await ApplyNetworkAsync();
    private async void ApplyNetwork_Click(object sender, RoutedEventArgs e) => await ApplyNetworkAsync();
    private async void ClearPriorityApps_Click(object sender, RoutedEventArgs e) => await ApplyNetworkAsync([]);
    private async void AddPriorityApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await ApplyNetworkAsync(_priorityApps.Append(file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) { Show(Friendly(ex), InfoBarSeverity.Error); }
    }

    private async void ScanWifi_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        WifiStatusText.Text = L.T("正在扫描附近网络…");
        try { UpdateWifi(await _backend.ScanWifiAsync()); }
        catch (Exception ex) { WifiStatusText.Text = Friendly(ex); throw; }
    }, null);

    private async void ConnectWifi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WifiRow row } || !row.CanConnect) return;
        await RunAsync(async () => UpdateWifi(await _backend.ConnectWifiAsync(row.InterfaceId, row.Profile)), L.T("WLAN 已连接。"));
    }

    private void UpdateWifi(JsonElement list)
    {
        var rows = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(item =>
        {
            bool connected = IsTrue(item, "Connected"), saved = IsTrue(item, "Saved");
            string name = item.Text("Ssid", "");
            return new WifiRow(name.Length == 0 ? L.T("隐藏网络") : name, item.Text("InterfaceId"), item.Text("Profile"),
                L.F($"信号 {item.Number("Signal")}% · {(connected ? L.T("已连接") : saved ? L.T("已保存") : L.T("未保存"))}"),
                connected ? L.T("已连接") : L.T("连接"), saved && !connected);
        }).ToArray() : [];
        WifiList.ItemsSource = rows;
        WifiStatusText.Text = rows.Length == 0 ? L.T("未发现附近 WLAN。") : L.F($"发现 {rows.Length} 项，可连接已保存的 WLAN。");
    }

    private void UpdateDownloadChannels()
    {
        int count = DownloadService.AvailableChannels;
        MultiNetworkCheck.IsEnabled = count >= 2;
        if (count < 2) MultiNetworkCheck.IsChecked = false;
        DownloadChannelsText.Text = count >= 2 ? L.F($"检测到 {count} 个网络通道。") : L.T("当前可使用单网络多连接下载。");
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null || !StartDownloadButton.IsEnabled) return;
        if (!Uri.TryCreate(DownloadUrlBox.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
        {
            DownloadStatusText.Text = L.T("请输入有效的 HTTP 或 HTTPS 下载链接。");
            return;
        }
        StartDownloadButton.IsEnabled = false;
        try
        {
            string name = string.Concat(Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath)).Split(Path.GetInvalidFileNameChars())).Trim('.', ' ');
            var picker = new FileSavePicker { SuggestedFileName = name.Length == 0 ? "download" : name };
            string extension = Path.GetExtension(name);
            picker.FileTypeChoices.Add(L.T("下载文件"), [extension.Length > 1 && extension.Length <= 16 ? extension : ".bin"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var cancellation = new CancellationTokenSource();
            _download = cancellation;
            CancelDownloadButton.IsEnabled = true;
            DownloadProgressBar.IsIndeterminate = true;
            DownloadStatusText.Text = L.T("正在连接服务器…");
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_download != cancellation) return;
                DownloadProgressBar.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) DownloadProgressBar.Value = 100d * p.Completed / p.Total;
                DownloadStatusText.Text = L.F($"{p.Completed / 1048576d:0.0} MiB · {Rate(p.BytesPerSecond)} · {p.Connections} 条连接");
            });
            var result = await DownloadService.DownloadAsync(url, file.Path, MultiNetworkCheck.IsChecked == true, progress, cancellation.Token);
            DownloadProgressBar.Value = 100;
            DownloadStatusText.Text = L.F($"已完成 · {result.Bytes / 1048576d:0.0} MiB · {result.Connections} 条连接");
        }
        catch (OperationCanceledException) { DownloadStatusText.Text = L.T("下载已取消。"); }
        catch (Exception ex) { DownloadStatusText.Text = Friendly(ex); }
        finally
        {
            _download = null;
            DownloadProgressBar.IsIndeterminate = false;
            StartDownloadButton.IsEnabled = true;
            CancelDownloadButton.IsEnabled = false;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _download?.Cancel();
    private static IEnumerable<JsonElement> Items(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];
    private static bool IsTrue(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double result) ? result : 0;
    private static string Rate(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:0.00} MiB/s" : $"{bytes / 1024:0.0} KiB/s";
    public sealed record WifiRow(string Ssid, string InterfaceId, string Profile, string Description, string ButtonText, bool CanConnect);
}
