using System.Management;

namespace HonorControl;

internal sealed class FirmwareKeys(Action<uint> arrived) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint,long> _last = [];
    public string Status { get; private set; } = L.T("正在连接固件 Fn 事件…");
    internal static bool IsHandled(uint code) => code is 0x283 or 0x287 or 0x28B or 0x290 or 0x291 or 0x2A0 or 0x2A1 or 0x2A3 or 0x2A6 or 0x2A7 or 0x2A8;
    public void Start() { _ = RunAsync(_stop.Token); }
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var watcher = new ManagementEventWatcher(@"root\wmi", "SELECT * FROM OemWMIEvent");
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Stopped += (_, _) => stopped.TrySetResult();
            watcher.EventArrived += (_, e) =>
            {
                try
                {
                    if (!string.Equals(Convert.ToString(e.NewEvent["InstanceName"]), @"ACPI\PNP0C14\HWMI_0", StringComparison.OrdinalIgnoreCase)) return;
                    uint code = Convert.ToUInt32(e.NewEvent["Force"]);
                    if (!IsHandled(code)) return;
                    long now = Environment.TickCount64;
                    lock (_last)
                    {
                        if (code is 0x287 or 0x2A7 && _last.TryGetValue(code, out long before) && now - before < 300) { _last[code] = now; return; }
                        _last[code] = now;
                    }
                    arrived(code);
                }
                catch { }
            };
            try
            {
                await Task.Run(watcher.Start, token);
                Status = L.T("Fn 固件事件已连接 · 关闭窗口后继续生效");
                await stopped.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status = L.T("Fn 事件连接等待重试：") + ex.Message; }
            finally { try { watcher.Stop(); } catch { } }
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { break; }
        }
    }
    public void Dispose() { _stop.Cancel(); }
}
