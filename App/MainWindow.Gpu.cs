using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HonorControl;

public sealed partial class MainWindow
{
    private static readonly string[] GpuModes = ["disabled", "auto", "dual", "integrated"];
    private bool _syncingGpu;

    private void UpdateGpu(JsonElement value, bool loadConfig = false)
    {
        GpuStateText.Text = L.T("当前独显 · ") + (value.Number("StateCode") switch
        {
            0 => L.T("已关闭"), 1 => L.T("空闲"), 2 => L.T("正在关闭"), 3 => L.T("正在启动"), 4 => L.T("正在使用"), _ => L.T("等待读取")
        });
        string error = value.Text("Error", "");
        GpuPolicyStatus.Text = error.Length > 0 ? error : value.Text("Note", "");
        if (value.TryGetProperty("Status", out var status) && status.TryGetProperty("ActiveOutputAdapters", out var outputs))
        {
            var names = outputs.EnumerateArray().Select(path => path.GetString() ?? "").Select(path =>
                path.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? "Intel" :
                path.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" : L.T("其他显卡")).Distinct();
            GpuDisplayRoute.Text = L.T("显示输出 · ") + string.Join("、", names);
        }
        if (!loadConfig) return;
        _syncingGpu = true;
        try { GpuModePicker.SelectedIndex = Math.Max(0, Array.IndexOf(GpuModes, value.Text("Mode", "disabled"))); }
        finally { _syncingGpu = false; }
    }

    private async void GpuMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingGpu || _busy || GpuModePicker.SelectedIndex < 0) return;
        string mode = GpuModes[GpuModePicker.SelectedIndex];
        await RunAsync(async () =>
        {
            try { UpdateGpu(await _backend.ConfigureGpuAsync(mode), true); }
            catch { UpdateGpu(await _backend.GpuStatusAsync(), true); throw; }
        }, L.T("显卡策略已保存。"));
    }

    private async void ForceIntegratedGpu_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = L.T("强制切换为集成显卡"),
            Content = L.T("正在使用独显的游戏、渲染或录制任务可能中断。切换前请保存相关工作。"),
            PrimaryButtonText = L.T("强制切换"),
            CloseButtonText = L.T("取消"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
            await RunAsync(async () => UpdateGpu(await _backend.ForceIntegratedGpuAsync(), true), L.T("集成显卡状态已读回。"));
    }
}
