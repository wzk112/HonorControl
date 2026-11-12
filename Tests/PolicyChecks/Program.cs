using System.Text.Json;
using HonorControl;
using HonorControl.Shared;

int mode = 3, gpuState = 1, fan = 5;
var operations = new List<string>();
Task<JsonElement> Hardware(string command)
{
    operations.Add(command);
    if (command == "gpu-dual") gpuState = 1;
    if (command == "gpu-integrated") gpuState = 0;
    if (command.StartsWith("fan-", StringComparison.Ordinal)) fan = int.Parse(command[4..]);
    mode = command switch { "firmware-smart" => 0, "firmware-performance" => 1, "firmware-hunter" => 3, _ => mode };
    object status = new { ModeCode = mode, FanPreset = fan, FanPresetLevelsAvailable = 5 };
    return Task.FromResult(JsonSerializer.SerializeToElement(command switch
    {
        "gpu-status" or "gpu-dual" or "gpu-integrated" => (object)new { StateCode = gpuState, HotPlugSupported = true, ActiveOutputAdapters = new[] { "PCI#VEN_8086" } },
        "firmware-mode" => new { ModeCode = mode },
        "firmware-status" => status,
        _ => new { Status = status }
    }));
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

Directory.CreateDirectory(RuntimeIdentity.UserState);
if (args is ["manual-performance"])
{
    string config = Path.Combine(RuntimeIdentity.UserState, "auto-performance.txt");
    string journal = Path.Combine(RuntimeIdentity.UserState, "auto-performance-restore.json");
    const string baseline = "{\"Original\":3,\"Fan\":5,\"Applied\":1}";
    File.WriteAllText(config, "1");
    File.WriteAllText(journal, baseline);
    bool reject = true;
    var controller = new PerformanceController(command => reject ? throw new InvalidOperationException("Power condition rejected.") : Hardware(command));
    bool failed = false;
    try { await controller.ApplyManualAsync("firmware-hunter"); } catch (InvalidOperationException) { failed = true; }
    Require(failed && File.ReadAllText(config) == "1" && File.ReadAllText(journal) == baseline, "A rejected switch disabled automatic scheduling or lost its restore record.");
    reject = false;
    await controller.ApplyManualAsync("firmware-hunter");
    Require(mode == 3 && File.ReadAllText(config) == "0" && !File.Exists(journal), "A successful manual switch did not release automatic ownership.");
    var session = new GameSessionTracker();
    var now = DateTimeOffset.UtcNow;
    Require(session.Update(17, "overwatch.exe", ["overwatch.exe"], now) == 17, "The foreground game was not recognized.");
    Require(session.Update(-1, "HonorControl.exe", ["overwatch.exe"], now.AddMinutes(2)) == 17, "Returning to Honor Control lost the running game.");
    Require(session.Update(-1, "explorer.exe", [], now.AddMinutes(2).AddSeconds(20)) == 1, "The game launch transition lost its grace period.");
    Require(session.Update(-1, "explorer.exe", [], now.AddMinutes(3)) == -1, "An exited game kept its mode indefinitely.");
    Require(session.Update(-1, "explorer.exe", ["gtavlauncher.exe"], now.AddMinutes(5)) == -1, "A background launcher incorrectly kept the game mode.");
    Require(session.Update(1, "eadesktop.exe", ["eadesktop.exe"], now.AddMinutes(6)) == -1, "The EA client incorrectly entered game mode.");
    foreach (string executable in new[] { "cs2.exe", "League of Legends.exe", "TslGame.exe", "YuanShen.exe", "StarRail.exe" })
        Require(SceneCatalog.Classify(executable) == 1, "A common game main process is missing.");
    Console.WriteLine("Manual rejection preserves automatic scheduling. Successful switching releases ownership.");
    return;
}
var gpu = new GpuController(Hardware);
bool rejected = false;
try { await gpu.ConfigureAsync("integrated"); } catch (InvalidOperationException) { rejected = true; }
Require(rejected && gpuState == 1, "HUNTER integrated-only guard failed.");
mode = 0;
await gpu.ConfigureAsync("integrated");
Require(gpuState == 1, "GPU was disabled before the idle grace period.");
await Task.Delay(31000);
await gpu.ReadAsync();
Require(gpuState == 0 && File.Exists(Path.Combine(RuntimeIdentity.UserState, "gpu-restore.flag")), "GPU idle transition was not journaled.");
await gpu.ConfigureAsync("disabled");
Require(gpuState == 1 && !File.Exists(Path.Combine(RuntimeIdentity.UserState, "gpu-restore.flag")), "GPU ownership restoration failed.");

Require(PerformanceController.SelectMode(1, true, true, false) == 3, "Game HUNTER selection failed.");
Require(PerformanceController.SelectMode(1, true, false, false) == 1, "PD fallback selection failed.");
Require(PerformanceController.SelectMode(13, true, true, false) == 1, "Creative mode selection failed.");
Require(PerformanceController.SelectMode(1, false, true, false) == 0, "Battery mode selection failed.");
Require(PerformanceController.SelectMode(17, true, true, true) == 0, "Battery saver selection failed.");

mode = 1;
File.WriteAllText(Path.Combine(RuntimeIdentity.UserState, "auto-performance-restore.json"), "{\"Original\":3,\"Fan\":5,\"Applied\":1}");
var performance = new PerformanceController(Hardware);
using var stop = new CancellationTokenSource(300);
try { await performance.RunAsync(stop.Token); } catch (OperationCanceledException) { }
Require(mode == 3 && fan == 5 && !File.Exists(Path.Combine(RuntimeIdentity.UserState, "auto-performance-restore.json")), "Interrupted automatic performance recovery failed.");
await performance.ApplyManualAsync("firmware-smart");
Console.WriteLine(JsonSerializer.Serialize(new { Verified = true, GpuIdleDelay = true, GpuRestored = true, HunterGuard = true, PerformanceSelection = true, PerformanceRestored = true, Operations = operations }));

namespace HonorControl.Shared
{
    internal static class RuntimeIdentity
    {
        public static string UserState => Path.Combine(AppContext.BaseDirectory, "state");
        public static string PauseFile => Path.Combine(UserState, "pause.flag");
    }
}
