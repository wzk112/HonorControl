using Microsoft.UI.Xaml;
using HonorControl.Shared;

namespace HonorControl;
public partial class App : Application
{
    private Window? _window;
    private Mutex? _instance;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _quit;
    private RegisteredWaitHandle? _quitWait;
    public App()
    {
        L.Initialize();
        HonorControl.Diagnostics.DiagnosticLog.Clean(AppPaths.UserState);
        HonorControl.Diagnostics.DiagnosticLog.Clean(AppPaths.DeviceState);
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { Directory.CreateDirectory(AppPaths.UserState); HonorControl.Diagnostics.DiagnosticLog.Append(Path.Combine(AppPaths.UserState, "errors.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine); } catch { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(AppPaths.UserState);
        if (Environment.GetCommandLineArgs().Contains("--exit"))
        {
            try { using var signal = EventWaitHandle.OpenExisting("Local\\HonorControl.Quit." + RuntimeIdentity.Sid); signal.Set(); } catch { }
            Exit(); return;
        }
        _instance = new Mutex(false, "Local\\HonorControl.UI." + RuntimeIdentity.Sid);
        bool restarting = Environment.GetCommandLineArgs().Contains("--restart");
        bool owned; try { owned = _instance.WaitOne(restarting ? 10000 : 0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            try { using var signal = EventWaitHandle.OpenExisting("Local\\HonorControl.Show." + RuntimeIdentity.Sid); signal.Set(); } catch { }
            Exit(); return;
        }
        try { File.Delete(RuntimeIdentity.PauseFile); } catch { }
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HonorControl.Show." + RuntimeIdentity.Sid);
        var main = new MainWindow(); _window = main;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => main.DispatcherQueue.TryEnqueue(main.ShowMainWindow), null, Timeout.Infinite, false);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HonorControl.Quit." + RuntimeIdentity.Sid);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => main.DispatcherQueue.TryEnqueue(main.RequestExit), null, Timeout.Infinite, false);
        if (AppPaths.BackgroundLaunch) main.AppWindow.Show(false);
        else _window.Activate();
    }
}
