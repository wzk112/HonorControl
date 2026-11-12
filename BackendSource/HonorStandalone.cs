// Windows x64 proof of concept. Uses the installed Intel driver and inbox WMI.
// No HONOR executable, service, DLL, registry entry, or custom driver is loaded.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Management;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Collections.Generic;

internal static class HonorStandalone {
    const string IntelDll = @"C:\Windows\System32\ControlLib.dll";
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlInit(IntPtr args, out IntPtr api);
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlClose(IntPtr api);
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, IntPtr devices);
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlEnumerateDisplayOutputs(IntPtr device, ref uint count, IntPtr displays);
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlPixelTransformationGetConfig(IntPtr display, IntPtr config);
    [DllImport(IntelDll, CallingConvention=CallingConvention.Cdecl)] static extern uint ctlPixelTransformationSetConfig(IntPtr display, IntPtr config);
    delegate uint Enumerator(IntPtr handle, ref uint count, IntPtr result);
    sealed class ColorOperationLock : IDisposable {
        System.Threading.Mutex mutex;bool held;
        public ColorOperationLock(){mutex=new System.Threading.Mutex(false,@"Local\HonorStandalone.ColorPipeline");try{held=mutex.WaitOne(10000);}catch(System.Threading.AbandonedMutexException){held=true;}if(!held){mutex.Dispose();throw new TimeoutException("Another color operation is in progress.");}}
        public void Dispose(){if(held){mutex.ReleaseMutex();held=false;}mutex.Dispose();}
    }
    static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly string DeviceState=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState");
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(uint result,string operation) { if(result!=0) throw new InvalidOperationException(operation+" failed: 0x"+result.ToString("X8")); }
    static IntPtr Alloc(int size) { IntPtr p=Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size],0,p,size); return p; }
    static int Int(IntPtr p,int offset) { return Marshal.ReadInt32(p,offset); }
    static void Put(IntPtr p,int offset,int value) { Marshal.WriteInt32(p,offset,value); }
    static string Hash(double[] values) { byte[] bytes=new byte[values.Length*8]; Buffer.BlockCopy(values,0,bytes,0,bytes.Length); using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
    static double[] Load(string path) {
        byte[] bytes=File.ReadAllBytes(path);
        if(bytes.Length!=17*17*17*3*8) throw new InvalidDataException("Unexpected calibration size.");
        var data=new double[bytes.Length/8]; Buffer.BlockCopy(bytes,0,data,0,bytes.Length);
        if(data.Any(x=>Double.IsNaN(x)||Double.IsInfinity(x)||x<0||x>1)) throw new InvalidDataException("Invalid LUT value.");
        return data;
    }
    static void Save(string path,double[] values) { var bytes=new byte[values.Length*8]; Buffer.BlockCopy(values,0,bytes,0,bytes.Length); File.WriteAllBytes(path,bytes); }
    static List<IntPtr> Enumerate(Enumerator call,IntPtr handle) {
        uint count=0; Check(call(handle,ref count,IntPtr.Zero),"Enumerate count");
        if(count>128) throw new InvalidDataException("Invalid adapter/display count.");
        var results=new List<IntPtr>(); if(count==0)return results;
        IntPtr buffer=Alloc(checked((int)count*8));
        try { Check(call(handle,ref count,buffer),"Enumerate handles"); for(int i=0;i<count;i++)results.Add(Marshal.ReadIntPtr(buffer,i*8)); }
        finally { Marshal.FreeHGlobal(buffer); }
        return results;
    }
    sealed class Display : IDisposable {
        public IntPtr Handle,Block; public int Samples;
        public Display(IntPtr handle,IntPtr block) { Handle=handle; Block=block; Samples=Int(block,24); if(Samples!=17)throw new InvalidDataException("Only verified 17-point calibration is supported."); }
        public double[] Read() {
            IntPtr request=Alloc(272), data=Alloc(Samples*Samples*Samples*3*8);
            try {
                Put(request,0,272); Put(request,8,1); Put(request,256,1); Marshal.WriteIntPtr(request,264,Block);
                Put(Block,0,144); Put(Block,16,24); Marshal.WriteIntPtr(Block,32,data);
                uint result=ctlPixelTransformationGetConfig(Handle,request);
                // This driver returns INVALID_DATA after the OEM Native action restores
                // the 3D-LUT block to default. Preserve this state instead of inventing an identity LUT.
                if(result==0x4800000f)return null;
                Check(result,"Read current 3D LUT");
                var values=new double[Samples*Samples*Samples*3]; Marshal.Copy(data,values,0,values.Length); return values;
            } finally { Marshal.WriteIntPtr(Block,32,IntPtr.Zero); Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(request); }
        }
        public void Apply(double[] values) {
            if(values.Length!=Samples*Samples*Samples*3)throw new InvalidDataException("LUT does not match display.");
            IntPtr request=Alloc(32), data=Alloc(values.Length*8);
            try {
                Marshal.Copy(values,0,data,values.Length); Put(Block,0,144); Put(Block,16,24); Marshal.WriteIntPtr(Block,32,data);
                Put(request,0,32); Put(request,8,2); Put(request,16,1); Marshal.WriteIntPtr(request,24,Block);
                Check(ctlPixelTransformationSetConfig(Handle,request),"Set 3D LUT");
            } finally { Marshal.WriteIntPtr(Block,32,IntPtr.Zero); Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(request); }
        }
        public void RestoreNative() {
            IntPtr request=Alloc(32);
            try {Put(request,0,32);Put(request,8,1);Put(request,16,1);Marshal.WriteIntPtr(request,24,Block);Check(ctlPixelTransformationSetConfig(Handle,request),"Restore native 3D-LUT block");}
            finally{Marshal.FreeHGlobal(request);}
        }
        public void Dispose() { if(Block!=IntPtr.Zero){Marshal.FreeHGlobal(Block);Block=IntPtr.Zero;} }
    }
    static Display FindDisplay(IntPtr api) {
        var found=new List<Display>();
        try {
            foreach(var device in Enumerate(ctlEnumerateDevices,api)) foreach(var display in Enumerate(ctlEnumerateDisplayOutputs,device)) {
                IntPtr request=Alloc(272), blocks=IntPtr.Zero;
                try {
                    Put(request,0,272);
                    uint result=ctlPixelTransformationGetConfig(display,request);
                    if(result!=0)continue;
                    int count=Int(request,256); if(count==0)continue;
                    if(count<0||count>64)throw new InvalidDataException("Invalid pipeline block count.");
                    blocks=Alloc(count*144); for(int i=0;i<count;i++)Put(blocks,i*144,144);
                    Marshal.WriteIntPtr(request,264,blocks); Check(ctlPixelTransformationGetConfig(display,request),"Read pipeline capability");
                    for(int i=0;i<count;i++)if(Int(blocks,i*144+12)==2) {
                        IntPtr block=Alloc(144); var bytes=new byte[144]; Marshal.Copy(IntPtr.Add(blocks,i*144),bytes,0,144); Marshal.Copy(bytes,0,block,144);
                        found.Add(new Display(display,block)); break;
                    }
                } finally { if(blocks!=IntPtr.Zero)Marshal.FreeHGlobal(blocks);Marshal.FreeHGlobal(request); }
            }
            if(found.Count!=1)throw new InvalidOperationException("Exactly one active Intel 3D-LUT display is required.");
            return found[0];
        } catch { foreach(var d in found)d.Dispose(); throw; }
    }
    static string BaseMode(double[] current, int samples, string profile, DisplayEffects effects, out double error, out bool effectsVerified) {
        string closest=current==null?"Native":"Custom";error=Double.MaxValue;effectsVerified=false;
        if(effects.Active && current!=null){
            string baseMode=profile.Equals("HonorP3.icc",StringComparison.OrdinalIgnoreCase)?"P3":profile.Equals("HonorsRGB.icc",StringComparison.OrdinalIgnoreCase)?"sRGB":profile.Equals("HonorNative.icc",StringComparison.OrdinalIgnoreCase)?"Native":null;
            if(baseMode!=null && (baseMode=="Native" || File.Exists(Path.Combine(Root,baseMode+".lut")))){
                var calibration=baseMode=="Native"?null:Load(Path.Combine(Root,baseMode+".lut"));
                for(int hour=0;hour<(effects.Style=="reading"?24:6);hour+=6){
                    var expected=effects.Apply(calibration,samples,hour);
                    if(MaxDifference(current,expected)<=.002){error=0;effectsVerified=true;return baseMode;}
                }
            }
        }
        foreach(string mode in current==null?new string[0]:new[]{"sRGB","P3"}) {
            if(!File.Exists(Path.Combine(Root,mode+".lut")))continue;
            double[] expected=Load(Path.Combine(Root,mode+".lut")); double mse=0;
            for(int i=0;i<current.Length;i++)mse+=(current[i]-expected[i])*(current[i]-expected[i]);
            double rmse=Math.Sqrt(mse/current.Length);if(rmse<error){error=rmse;closest=mode;}
        }
        return current==null?"Native":error<.002?closest:"Custom";
    }
    static double MaxDifference(double[] current,double[] expected) {
        if(current==null || expected==null)return current==expected?0:Double.PositiveInfinity;
        double max=0;
        for(int i=0;i<current.Length;i++)max=Math.Max(max,Math.Abs(current[i]-expected[i]));
        return max;
    }
    static object Status(Display display) {
        var effects=DisplayEffects.Read(DeviceState);
        double[] current=display.Read();
        string profile=WindowsColor.GetProfile();
        double error;bool verified;
        string mode=BaseMode(current,display.Samples,profile,effects,out error,out verified);
        return new {Backend="Intel IGCL (installed graphics driver)",LutSamplesPerChannel=display.Samples,ClosestFactoryMode=mode,WindowsIccProfile=profile,Custom3DLutActive=current!=null,FactoryDifferenceRmse=current==null?(double?)null:error,LutSha256=current==null?null:Hash(current),EffectStyle=effects.Style,Temperature=effects.Temperature,EffectsVerified=verified,HonorModulesLoaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Count(m=>m.FileName.IndexOf(@"\Program Files\HONOR\",StringComparison.OrdinalIgnoreCase)>=0)};
    }
    static object FirmwareStatus() {
        var scope=new ManagementScope(@"\\.\root\wmi");scope.Options.EnablePrivileges=true;scope.Connect();
        using(var search=new ManagementObjectSearcher(scope,new ObjectQuery("SELECT * FROM OemWMIMethod"))) using(var all=search.Get()) {
            var instances=all.Cast<ManagementObject>().Where(x=>(Convert.ToString(x["InstanceName"])??"").EndsWith("HWMI_0",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(instances.Length!=1)throw new InvalidOperationException("Expected exactly one HWMI_0.");
            using(var input=instances[0].GetMethodParameters("OemWMIfun")) {
                byte[] command=new byte[64];command[0]=4;command[1]=14;input["u8Input"]=command;
                using(var reply=instances[0].InvokeMethod("OemWMIfun",input,null)) {
                    byte[] data=(byte[])reply["u8Output"];if(data.Length<2||data[0]!=0)throw new InvalidOperationException("Firmware read rejected.");
                    return new {Backend="Windows ACPI WMI",ModeCode=data[1],Mode=data[1]==0?"Smart":data[1]==1?"Performance":data[1]==3?"HUNTER":"Unknown",ReadOnly=true};
                }
            }
        }
    }
    public static int Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        string command=args.Length==0?"status":args[0].ToLowerInvariant();
        try {
            if(command=="admin-menu")return AdminMenu();
            if(command=="color-watch")return ColorWatcher.Run();
            if(command=="color-watch-start"){Console.WriteLine(Json.Serialize(ColorWatcher.Start()));return 0;}
            if(command=="color-watch-stop"){Console.WriteLine(Json.Serialize(ColorWatcher.Signal(true)));return 0;}
            if(command=="color-watch-reapply"){Console.WriteLine(Json.Serialize(ColorWatcher.Signal(false)));return 0;}
            if(command=="color-watch-settings"){Console.WriteLine(Json.Serialize(ColorWatcher.Signal(false,true)));return 0;}
            if(command=="color-startup-enable"){Console.WriteLine(Json.Serialize(ColorWatcher.SetStartup(true)));return 0;}
            if(command=="color-startup-disable"){Console.WriteLine(Json.Serialize(ColorWatcher.SetStartup(false)));return 0;}
            if(command=="firmware-menu")return HonorFirmware.Menu();
            if(HonorFirmware.Handles(command)){Console.WriteLine(Json.Serialize(HonorFirmware.Execute(command)));return 0;}
            if(command=="install-profiles"){Console.WriteLine(Json.Serialize(WindowsColor.InstallProfiles()));return 0;}
            bool changeEffects=command=="display-effect";
            if(!new[]{"status","native","srgb","p3","restore","display-effect","color-reapply"}.Contains(command))throw new ArgumentException("Unknown color command.");
            using(var colorGuard=new ColorOperationLock()){
            if(!Environment.Is64BitProcess)throw new InvalidOperationException("Run the x64 build.");
            IntPtr init=Alloc(36),api=IntPtr.Zero;
            try {
                Put(init,0,36);Put(init,8,0x10000);Check(ctlInit(init,out api),"Initialize Intel driver");
                using(var display=FindDisplay(api)) {
                    if(command=="status"){Console.WriteLine(Json.Serialize(Status(display)));return 0;}
                    double[] before=display.Read();string beforeProfile=WindowsColor.GetProfile();
                    string state=DeviceState;
                    Directory.CreateDirectory(state);
                    var previousEffects=DisplayEffects.Read(state);
                    var effects=previousEffects;
                    bool reapply=command=="color-reapply";
                    if(reapply){
                        command=File.ReadAllText(Path.Combine(state,"last-color-mode.txt")).Trim();
                        if(command!="native"&&command!="p3"&&command!="srgb"&&command!="restore")throw new InvalidDataException("Invalid saved color mode.");
                    }
                    if(changeEffects){
                        int temperature;
                        if(args.Length!=3||!Int32.TryParse(args[2],out temperature))throw new ArgumentException("Expected an effect and temperature.");
                        effects=new DisplayEffects(args[1],temperature);
                        double error;bool verified;
                        command=BaseMode(before,display.Samples,beforeProfile,previousEffects,out error,out verified).ToLowerInvariant();
                        if(command=="custom")throw new InvalidOperationException("Select a factory color mode before applying display effects.");
                    }
                    if(command=="restore")effects=new DisplayEffects("off",0);
                    if(effects.Active)WindowsColor.RequireSdr();
                    string backup=Path.Combine(state,"before-first-change.lut"),nativeBackup=Path.Combine(state,"before-first-change.native");
                    if(!File.Exists(backup)&&!File.Exists(nativeBackup)){if(before==null)File.WriteAllText(nativeBackup,"Native");else Save(backup,before);}
                    string profileBackup=Path.Combine(state,"before-first-change-icc.txt");if(!File.Exists(profileBackup))File.WriteAllText(profileBackup,beforeProfile);
                    string target=command=="restore"?backup:Path.Combine(Root,command=="srgb"?"sRGB.lut":"P3.lut");
                    bool native=command=="native"||(command=="restore"&&File.Exists(nativeBackup));
                    double[] wanted=effects.Apply(native?null:Load(target),display.Samples,DateTime.Now.Hour);
                    string wantedProfile=command=="restore"?File.ReadAllText(profileBackup).Trim():command=="native"?"HonorNative.icc":command=="srgb"?"HonorsRGB.icc":"HonorP3.icc";
                    try {
                        if(!String.Equals(beforeProfile,wantedProfile,StringComparison.OrdinalIgnoreCase))WindowsColor.SetProfile(wantedProfile);
                        if(wanted==null)display.RestoreNative();else display.Apply(wanted);
                        double maxError=MaxDifference(display.Read(),wanted);
                        // This driver quantizes 12-bit factory data into its 10-bit LUT.
                        // Two output steps cover the measured conversion error (max 0.001221).
                        if(maxError>2.0/1024.0+0.00001)throw new InvalidOperationException("LUT readback mismatch; maximum error="+maxError);
                        if(!reapply)File.WriteAllText(Path.Combine(state,"last-color-mode.txt"),command);
                        if(changeEffects||(!reapply&&command=="restore"))effects.Save(state);
                        Console.WriteLine(Json.Serialize(new {Command=command,Verified=true,MaximumQuantizationDifference=maxError,Status=Status(display)}));
                    } catch {WindowsColor.SetProfile(beforeProfile);if(before==null)display.RestoreNative();else display.Apply(before);var restored=display.Read();if((restored==null)!=(before==null)||(before!=null&&Hash(restored)!=Hash(before)))throw new InvalidOperationException("Restoration could not be confirmed; use saved LUT backup.");throw;}
                }
            } finally {if(api!=IntPtr.Zero)ctlClose(api);Marshal.FreeHGlobal(init);}
            return 0;
            }
        } catch(Exception ex) {Console.Error.WriteLine(Json.Serialize(new {Error=ex.Message}));return 1;}
    }
    static int AdminMenu(){
        while(true){
            Console.WriteLine("\nHONOR independent control - Administrator session");
            Console.WriteLine("1 Performance and fan controls   2 Network QoS   3 Install bundled ICC profiles   Q Exit");
            string choice=(Console.ReadLine()??"q").Trim().ToLowerInvariant();if(choice=="q")return 0;
            try{
                if(choice=="1")HonorFirmware.Menu();
                else if(choice=="2"){
                    string script=Path.Combine(Root,"Network-Control.ps1");
                    var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\" -Action Menu"){UseShellExecute=false,WorkingDirectory=Root};
                    using(var process=Process.Start(start))process.WaitForExit();
                }else if(choice=="3")Console.WriteLine(Json.Serialize(WindowsColor.InstallProfiles()));
            }catch(Exception ex){Console.WriteLine(Json.Serialize(new{Error=ex.Message}));}
        }
    }
}
