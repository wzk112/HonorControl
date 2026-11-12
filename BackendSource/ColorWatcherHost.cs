using System;
using System.IO;
internal static class ColorWatcherHost {
    [STAThread] static int Main(){
        try{return ColorWatcher.Run();}
        catch(Exception ex){
            try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState");Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,"color-watcher.log"),DateTime.UtcNow.ToString("o")+" Watcher stopped with error: "+ex+Environment.NewLine);}catch{}
            return 1;
        }
    }
}
