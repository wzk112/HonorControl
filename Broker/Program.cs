using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using HonorControl.Shared;
using HonorControl.Network;

bool resident = args.Length == 1 && args[0] == "--resident";
if (!resident && (args.Length != 2 || args[0] != "--pipe" || !System.Text.RegularExpressions.Regex.IsMatch(args[1], @"^HonorControl\.[a-fA-F0-9]{32}$"))) return;
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return;
if (resident && !string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HonorControl"), StringComparison.OrdinalIgnoreCase)) return;
using var singleton = new Mutex(false, "Local\\HonorControl.Agent." + RuntimeIdentity.Sid);
if (resident) { try { if (!singleton.WaitOne(0)) return; } catch (AbandonedMutexException) { } }
HonorControl.Diagnostics.DiagnosticLog.Clean(RuntimeIdentity.UserState);
var backend = Path.Combine(AppContext.BaseDirectory, "Backend");
string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
var allowed = new HashSet<string>(StringComparer.Ordinal) { "firmware-status", "firmware-mode", "firmware-policy", "firmware-smart", "firmware-performance", "firmware-hunter", "fan-1", "fan-2", "fan-3", "fan-4", "fan-5", "charge-status", "charge-home", "charge-office", "charge-travel", "keyboard-status", "keyboard-icons", "keyboard-fkeys", "keyboard-restore-preference" };
allowed.UnionWith(["touchpad-status", "mic-light-muted", "mic-light-active"]);
using var lifetime = new CancellationTokenSource();
if (!resident) lifetime.CancelAfter(TimeSpan.FromHours(12));
var network = new NetworkEngine();
var audio = new HonorControl.AudioController();
var gpu = new HonorControl.GpuController(command => RunAsync(Path.Combine(backend, "HonorStandalone.exe"), [command]));
var performance = new HonorControl.PerformanceController(command => RunAsync(Path.Combine(backend, "HonorStandalone.exe"), [command]));
var dtt = new HonorControl.DttController(command => RunAsync(Path.Combine(backend, "HonorStandalone.exe"), [command]));
var boost = new HonorControl.GpuBoostController(command => RunAsync(Path.Combine(backend, "HonorStandalone.exe"), [command]));
if (resident) _ = network.RunAsync(lifetime.Token);
if (resident) _ = gpu.RunAsync(lifetime.Token);
if (resident) _ = performance.RunAsync(lifetime.Token);
if (resident) _ = dtt.RunAsync(lifetime.Token);
if (resident) _ = boost.RunAsync(lifetime.Token);
if (resident) _ = WatchUiAsync(lifetime.Token);
var userSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user identity.");
var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(userSid);
security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
try
{
    do
    {
        var pipe = NamedPipeServerStreamAcl.Create(resident ? RuntimeIdentity.PipeName : args[1], PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        using var initial = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (!resident) initial.CancelAfter(TimeSpan.FromSeconds(45));
        try { await pipe.WaitForConnectionAsync(initial.Token); }
        catch { pipe.Dispose(); throw; }
        if (resident) _ = HandleAsync(pipe); else await HandleAsync(pipe);
    } while (resident && !lifetime.IsCancellationRequested);
}
catch (OperationCanceledException) { }
catch (Exception ex) { Log(ex); if (resident) Environment.ExitCode = 1; }

async Task HandleAsync(NamedPipeServerStream pipe)
{
    using (pipe)
    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
    try
    {
        while (pipe.IsConnected && !lifetime.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(lifetime.Token);
            if (line is null || line.Length > 32768) break;
            ControlResponse response;
            try
            {
                var request = Wire.Decode<ControlRequest>(line);
                if (request.Action == "exit") { await writer.WriteLineAsync(Wire.Encode(new ControlResponse(true))); break; }
                if (request.Action == "hello") response = new(true, JsonSerializer.SerializeToElement(new { Administrator = true, Resident = resident, Version = version }));
                else if (request.Action == "install-profiles") response = new(true, await RunAsync(Path.Combine(backend, "HonorStandalone.exe"), ["install-profiles"]));
                else if (request.Action == "network-status") response = new(true, JsonSerializer.SerializeToElement(network.Snapshot));
                else if (request.Action == "audio-status") response = new(true, JsonSerializer.SerializeToElement(await Task.Run(audio.Read)));
                else if (request.Action == "audio-apply" && request.Command is not null) response = new(true, JsonSerializer.SerializeToElement(await Task.Run(() => audio.Apply(request.Command))));
                else if (request.Action == "gpu-status") response = new(true, JsonSerializer.SerializeToElement(await gpu.ReadAsync()));
                else if (request.Action == "gpu-configure" && request.Command is not null) response = new(true, JsonSerializer.SerializeToElement(await gpu.ConfigureAsync(request.Command)));
                else if (request.Action == "gpu-force-integrated") response = new(true, JsonSerializer.SerializeToElement(await gpu.ForceIntegratedAsync()));
                else if (request.Action == "performance-status") response = new(true, JsonSerializer.SerializeToElement(performance.Snapshot));
                else if (request.Action == "dtt-status") response = new(true, JsonSerializer.SerializeToElement(dtt.Snapshot));
                else if (request.Action == "boost-status") response = new(true, JsonSerializer.SerializeToElement(boost.Snapshot));
                else if (request.Action == "performance-configure" && request.Command is "on" or "off") response = new(true, JsonSerializer.SerializeToElement(await performance.ConfigureAsync(request.Command == "on")));
                else if (request.Action == "network-configure" && request.Network is not null) response = new(true, JsonSerializer.SerializeToElement(await network.ConfigureAsync(request.Network)));
                else if (request.Action == "wifi-scan") { await Task.Run(() => WifiNative.Read(true)); await Task.Delay(2000); response = new(true, JsonSerializer.SerializeToElement(await Task.Run(() => WifiNative.Read()))); }
                else if (request.Action == "wifi-connect" && request.Command is not null && request.InterfaceId is not null) { await WifiNative.ConnectAsync(request.InterfaceId, request.Command); response = new(true, JsonSerializer.SerializeToElement(WifiNative.Read())); }
                else if (request.Action == "firmware" && request.Command is { } command && allowed.Contains(command))
                {
                    response = new(true, command is "firmware-smart" or "firmware-performance" or "firmware-hunter" ?
                        await performance.ApplyManualAsync(command) : await RunAsync(Path.Combine(backend, "HonorStandalone.exe"), [command]));
                }
                else throw new InvalidOperationException("不支持的控制操作。");
            }
            catch (Exception ex) { response = new(false, Error: ex.Message); }
            await writer.WriteLineAsync(Wire.Encode(response));
        }
    }
    catch (IOException) { }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Log(ex); }
}

async Task WatchUiAsync(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(15000, token);
            if (File.Exists(RuntimeIdentity.PauseFile)) continue;
            bool found = Process.GetProcessesByName("HonorControl").Any(p => { try { return string.Equals(p.MainModule?.FileName, Path.Combine(AppContext.BaseDirectory, "HonorControl.exe"), StringComparison.OrdinalIgnoreCase); } catch { return false; } });
            if (!found)
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("/Run"); start.ArgumentList.Add("/TN"); start.ArgumentList.Add(RuntimeIdentity.UiTask);
                using var process = Process.Start(start);
                if (process is not null) await process.WaitForExitAsync(token);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Log(ex); }
    }
}
void Log(Exception ex)
{
    try { Directory.CreateDirectory(RuntimeIdentity.UserState); HonorControl.Diagnostics.DiagnosticLog.Append(Path.Combine(RuntimeIdentity.UserState, "broker-errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { }
}

async Task<JsonElement> RunAsync(string executable, IEnumerable<string> parameters)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = backend, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
    foreach (var item in parameters) start.ArgumentList.Add(item);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动控制程序。");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { try { process.Kill(true); } catch { } throw; }
    string text = (await output).Trim().TrimStart('\uFEFF');
    string error = (await errors).Trim();
    if (process.ExitCode != 0)
    {
        try { var failure = JsonDocument.Parse(error.Length > 0 ? error : text).RootElement; if (failure.TryGetProperty("Error", out var message)) throw new InvalidOperationException(message.GetString()); } catch (JsonException) { }
        throw new InvalidOperationException(error.Length > 0 ? error : text);
    }
    return text.Length == 0 ? JsonSerializer.SerializeToElement(Array.Empty<object>()) : JsonDocument.Parse(text).RootElement.Clone();
}
