using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HonorControl;

public sealed partial class MainWindow
{
    private bool _readingAudio;
    private JsonElement _audioSnapshot;

    private async Task RefreshAudioAsync() => UpdateAudio(await _backend.AudioAsync());

    private async void ReadAudio_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAudioAsync, null);

    private void UpdateAudio(JsonElement state)
    {
        _audioSnapshot = state;
        _readingAudio = true;
        try
        {
            int speaker = state.Number("Speaker");
            SpeakerEffect.IsEnabled = speaker >= 0;
            SpeakerEffect.SelectedIndex = speaker switch { 8 or 11 => 0, 5 => 1, 4 => 2, 1 => 3, 3 => 4, 2 => 5, _ => -1 };
            SpeakerEffectStatus.Text = SpeakerEffect.SelectedIndex >= 0 ? "" : speaker < 0 ?
                L.T("未检测到 Histen 外放组件。") : L.F($"当前驱动模式 · {speaker}");

            int headphones = state.Number("Headphones");
            HeadphoneEffect.IsEnabled = headphones >= 0;
            HeadphoneEffect.SelectedIndex = headphones switch { 5 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => -1 };
            HeadphoneEffectStatus.Text = HeadphoneEffect.SelectedIndex >= 0 ? "" : headphones < 0 ?
                L.T("未检测到 Histen 耳机组件。") : L.F($"当前驱动模式 · {headphones}");

            int microphone = state.Number("Microphone");
            bool available = state.TryGetProperty("MicrophoneAvailable", out var capability) && capability.GetBoolean();
            MicrophoneEffect.IsEnabled = available;
            MicrophoneEffect.SelectedIndex = microphone switch { 2 => 0, 3 => 1, 19 => 2, _ => -1 };
            MicrophoneEffectStatus.Text = !available ? state.Text("MicStatus", L.T("无法读取拾音模式。")) :
                MicrophoneEffect.SelectedIndex < 0 ? L.F($"当前驱动模式 · {microphone}") : "";
        }
        finally { _readingAudio = false; }
    }

    private async void AudioEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _readingAudio || _closing) return;
        if (_busy)
        {
            if (_audioSnapshot.ValueKind == JsonValueKind.Object) UpdateAudio(_audioSnapshot);
            return;
        }
        if (sender is not ComboBox picker ||
            picker.SelectedItem is not ComboBoxItem { Tag: string command, Content: string label }) return;
        var icon = picker == SpeakerEffect ? OverlayIcon.Speaker : picker == HeadphoneEffect ? OverlayIcon.Headphones : OverlayIcon.Microphone;
        await RunAsync(async () =>
        {
            try { UpdateAudio(await _backend.AudioAsync(command)); }
            catch
            {
                try { await RefreshAudioAsync(); }
                catch { }
                throw;
            }
            _overlay.Present(label, picker.Header?.ToString() ?? L.T("音频"), icon);
        }, L.T("切换成功"));
    }
}
