using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

internal static class ColorWatcher {
    const string StopEvent=@"Local\HonorStandalone.ColorWatch.Stop";
    const string ApplyEvent=@"Local\HonorStandalone.ColorWatch.Apply";
    const string SettingsEvent=@"Local\HonorStandalone.ColorWatch.Settings";
    const string MutexName=@"Local\HonorStandalone.ColorWatch.Instance";
    static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly string State=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState");
    static readonly string Preferences=Path.GetDirectoryName(State);
    static bool ColorEnabled(){string file=Path.Combine(Preferences,"color-watch-enabled.txt");return !File.Exists(file)||File.ReadAllText(file).Trim()!="0";}
    static bool RhythmEnabled(){string file=Path.Combine(Preferences,"brightness-rhythm-enabled.txt");return File.Exists(file)&&File.ReadAllText(file).Trim()=="1";}
    static void Log(string message){HonorControl.Diagnostics.DiagnosticLog.Append(Path.Combine(State,"color-watcher.log"),DateTime.UtcNow.ToString("o")+" "+message+Environment.NewLine);}
    static void ApplySelected(){
        try {
            string file=Path.Combine(State,"last-color-mode.txt");if(!File.Exists(file))return;
            string mode=File.ReadAllText(file).Trim().ToLowerInvariant();if(mode!="native"&&mode!="srgb"&&mode!="p3"&&mode!="restore")return;
            var start=new ProcessStartInfo(Path.Combine(Root,"HonorStandalone.exe"),"color-reapply"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Root,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(start)){if(!process.WaitForExit(15000)){process.Kill();Log("Color apply timed out.");return;}string output=process.StandardOutput.ReadToEnd();string error=process.StandardError.ReadToEnd();Log(process.ExitCode==0?"Verified reapply: "+mode:"Reapply failed: "+error.Trim());}
        }catch(Exception ex){Log(ex.Message);}
    }
    public static object Start(){
        if(!RhythmEnabled()&&!File.Exists(Path.Combine(State,"last-color-mode.txt")))throw new InvalidOperationException("Choose sRGB, P3 or Native before starting automatic color reapply.");
        Mutex existing;if(Mutex.TryOpenExisting(MutexName,out existing)){existing.Dispose();return new{Running=true,AlreadyRunning=true};}
        // Launch a GUI-subsystem watcher without inheriting the launcher's
        // captured console pipes. It must return while the watcher stays alive.
        var start=new ProcessStartInfo(Path.Combine(Root,"HonorColorWatcher.exe")){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Root};
        using(var process=Process.Start(start)){for(int i=0;i<20;i++){Thread.Sleep(100);if(Mutex.TryOpenExisting(MutexName,out existing)){existing.Dispose();return new{Running=true,AlreadyRunning=false};}if(process.HasExited)throw new InvalidOperationException("Color watcher exited during startup.");}}
        throw new InvalidOperationException("Could not confirm the color watcher started.");
    }
    public static object Signal(bool stop,bool settings=false){
        EventWaitHandle handle;if(!EventWaitHandle.TryOpenExisting(stop?StopEvent:settings?SettingsEvent:ApplyEvent,out handle))return new{Running=false,Signalled=false};
        using(handle)handle.Set();return new{Running=true,Signalled=true};
    }
    public static object SetStartup(bool enabled){
        using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){
            if(enabled)key.SetValue("HonorStandaloneColor","\""+Path.Combine(Root,"HonorColorWatcher.exe")+"\"",RegistryValueKind.String);
            else key.DeleteValue("HonorStandaloneColor",false);
        }
        return new{StartWithWindows=enabled,Scope="Current user"};
    }
    public static int Run(){
        bool created;using(var mutex=new Mutex(true,MutexName,out created)){
            if(!created)return 0;
            using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,StopEvent))using(var apply=new EventWaitHandle(false,EventResetMode.AutoReset,ApplyEvent))using(var settings=new EventWaitHandle(false,EventResetMode.AutoReset,SettingsEvent)){
                PowerModeChangedEventHandler power=(s,e)=>{if(e.Mode==PowerModes.Resume)apply.Set();};
                EventHandler display=(s,e)=>apply.Set();
                SystemEvents.PowerModeChanged+=power;SystemEvents.DisplaySettingsChanged+=display;
                BrightnessRhythm rhythm=null;
                try {
                    Log("Watcher started.");
                    if(ColorEnabled())ApplySelected();
                    int period=DateTime.Now.Hour/6;
                    string lastRhythmError=null;
                    var signals=new WaitHandle[]{stop,apply,settings};
                    while(true){
                        bool rhythmEnabled=RhythmEnabled();
                        try {
                            if(rhythm==null&&(rhythmEnabled||File.Exists(Path.Combine(State,"brightness-rhythm-journal.txt"))))rhythm=new BrightnessRhythm(State);
                            if(rhythm!=null)rhythm.Update(rhythmEnabled);
                            lastRhythmError=null;
                        }catch(Exception ex){if(ex.Message!=lastRhythmError){Log("Brightness rhythm: "+ex.Message);lastRhythmError=ex.Message;}}
                        int signal=WaitHandle.WaitAny(signals,rhythmEnabled?2000:30000);
                        if(signal==0)break;
                        int current=DateTime.Now.Hour/6;
                        if(signal==1){if(stop.WaitOne(1000))break;if(ColorEnabled())ApplySelected();}
                        else if(current!=period&&ColorEnabled()){
                            string effects=Path.Combine(State,"display-effects.txt");
                            if(File.Exists(effects)&&File.ReadAllText(effects).StartsWith("reading ",StringComparison.Ordinal))ApplySelected();
                        }
                        period=current;
                    }
                    Log("Watcher stopped.");
                }
                finally{
                    try{if(rhythm!=null)rhythm.Restore();}catch(Exception ex){Log("Brightness restore: "+ex.Message);}
                    SystemEvents.PowerModeChanged-=power;SystemEvents.DisplaySettingsChanged-=display;mutex.ReleaseMutex();
                }
            }
        }
        return 0;
    }
}
