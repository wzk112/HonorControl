using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HonorControl.Shared;

namespace HonorControl;

internal static class AppPaths
{
    public static string Backend => Path.Combine(AppContext.BaseDirectory, "Backend");
    public static string UserState => RuntimeIdentity.UserState;
    public static string DeviceState => RuntimeIdentity.DeviceState;
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.json"));
    public static bool BackgroundLaunch => Environment.GetCommandLineArgs().Contains("--background");
    public static string? ExistingPipeName
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--broker-pipe");
            return index >= 0 && index + 1 < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[index + 1], @"^HonorControl\.[a-fA-F0-9]{32}$") ? args[index + 1] : null;
        }
    }
}

internal sealed class BackendClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1);
    private readonly SemaphoreSlim _requestLock = new(1);
    private readonly SemaphoreSlim _colorLock = new(1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _existingPipeUsed;
    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (IsConnected) return;
            if (AppPaths.Installed)
            {
                _pipe = new NamedPipeClientStream(".", RuntimeIdentity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await _pipe.ConnectAsync(10000);
                _reader = new(_pipe, new UTF8Encoding(false), false, 4096, true);
                _writer = new(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await ExchangeAsync(new("hello"));
                return;
            }
            string name;
            if (!_existingPipeUsed && AppPaths.ExistingPipeName is string existing)
            {
                name = existing; _existingPipeUsed = true;
            }
            else
            {
                name = "HonorControl." + Guid.NewGuid().ToString("N");
                string broker = Path.Combine(AppContext.BaseDirectory, "HonorControl.Broker.exe");
                var start = new ProcessStartInfo(broker) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
                start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
                using var process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException(L.T("无法启动管理员控制会话。"));
            }
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(45000);
            _reader = new(_pipe, new UTF8Encoding(false), false, 4096, true);
            _writer = new(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await ExchangeAsync(new("hello"));
        }
        catch { await DisconnectAsync(); throw; }
        finally { _connectionLock.Release(); }
    }

    public async Task<JsonElement> HardwareAsync(string command)
    {
        await ConnectAsync();
        return await ExchangeAsync(new("firmware", command));
    }
    public async Task<JsonElement> InstallProfilesAsync()
    {
        await ConnectAsync();
        return await ExchangeAsync(new("install-profiles"));
    }
    public async Task<JsonElement> NetworkStatusAsync() { await ConnectAsync(); return await ExchangeAsync(new("network-status")); }
    public async Task<JsonElement> GpuStatusAsync() { await ConnectAsync(); return await ExchangeAsync(new("gpu-status")); }
    public async Task<JsonElement> AudioAsync(string? mode = null)
    {
        await ConnectAsync();
        return await ExchangeAsync(mode is null ? new("audio-status") : new("audio-apply", mode));
    }
    public async Task<JsonElement> ConfigureGpuAsync(string mode) { await ConnectAsync(); return await ExchangeAsync(new("gpu-configure", mode)); }
    public async Task<JsonElement> ForceIntegratedGpuAsync() { await ConnectAsync(); return await ExchangeAsync(new("gpu-force-integrated")); }
    public async Task<JsonElement> PerformanceStatusAsync() { await ConnectAsync(); return await ExchangeAsync(new("performance-status")); }
    public async Task<JsonElement> DttStatusAsync() { await ConnectAsync(); return await ExchangeAsync(new("dtt-status")); }
    public async Task<JsonElement> BoostStatusAsync() { await ConnectAsync(); return await ExchangeAsync(new("boost-status")); }
    public async Task<JsonElement> ConfigurePerformanceAsync(bool enabled) { await ConnectAsync(); return await ExchangeAsync(new("performance-configure", enabled ? "on" : "off")); }
    public async Task<JsonElement> ConfigureNetworkAsync(NetworkConfig value) { await ConnectAsync(); return await ExchangeAsync(new("network-configure", Network: value)); }
    public async Task<JsonElement> ScanWifiAsync() { await ConnectAsync(); return await ExchangeAsync(new("wifi-scan")); }
    public async Task<JsonElement> ConnectWifiAsync(string id, string profile) { await ConnectAsync(); return await ExchangeAsync(new("wifi-connect", profile, InterfaceId: id)); }
    private async Task<JsonElement> ExchangeAsync(ControlRequest request)
    {
        await _requestLock.WaitAsync();
        try
        {
            if (_writer is null || _reader is null) throw new InvalidOperationException(L.T("管理员会话尚未连接。"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await _writer.WriteLineAsync(Wire.Encode(request));
            string? line = await _reader.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException(L.T("管理员控制会话已结束，请重新启用硬件控制。"));
            var result = Wire.Decode<ControlResponse>(line);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? L.T("控制操作未完成。"));
            return result.Data?.Clone() ?? JsonSerializer.SerializeToElement(Array.Empty<object>());
        }
        catch (IOException) { await DisconnectAsync(); throw; }
        catch (OperationCanceledException) { await DisconnectAsync(); throw; }
        finally { _requestLock.Release(); }
    }
    public async Task<JsonElement> ColorAsync(string command, params string[] arguments)
    {
        var allowed = new[] { "status", "native", "srgb", "p3", "restore", "display-effect", "color-watch-start", "color-watch-stop", "color-watch-reapply", "color-reapply", "color-watch-settings", "color-startup-enable", "color-startup-disable" };
        if (!allowed.Contains(command)) throw new ArgumentException(L.T("不支持的色彩操作。"));
        await _colorLock.WaitAsync();
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppPaths.Backend, "HonorStandalone.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.Backend, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add(command);
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException(L.T("无法启动色彩控制程序。"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(true); } catch { } throw; }
            string text = (await stdout).Trim().TrimStart('\uFEFF');
            string error = (await stderr).Trim();
            if (process.ExitCode != 0)
            {
                try { var result = JsonDocument.Parse(error.Length > 0 ? error : text).RootElement; if (result.TryGetProperty("Error", out var message)) throw new InvalidOperationException(message.GetString()); } catch (JsonException) { }
                throw new InvalidOperationException(error.Length > 0 ? error : text);
            }
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        finally { _colorLock.Release(); }
    }
    private async Task DisconnectAsync()
    {
        // Dispose buffered writers before their underlying pipe; a disconnected
        // peer must not turn window shutdown into an unhandled UI exception.
        try { _writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        _writer = null;
        _reader?.Dispose(); _reader = null;
        if (_pipe is not null) { await _pipe.DisposeAsync(); _pipe = null; }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (IsConnected) await ExchangeAsync(new("exit")); } catch { }
        await DisconnectAsync();
    }
}

