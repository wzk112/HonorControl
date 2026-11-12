using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class HonorFirmware {
    [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte Ac,Flags,Percent,Reserved;public uint Life,FullLife; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
    static readonly byte[] FanSelectors={0xAA,0xAB,0xAC,0xAD,0xA0};
    static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    public static bool Handles(string command){return command.StartsWith("firmware-")||command.StartsWith("fan-")||command.StartsWith("charge-")||command.StartsWith("gpu-")||command.StartsWith("dtt-")||command.StartsWith("keyboard-")||command=="touchpad-status"||command=="mic-light-muted"||command=="mic-light-active";}
    internal sealed class Session : IDisposable {
        ManagementObject instance; Mutex mutex;bool locked;
        public Session() {
            mutex=new Mutex(false,@"Local\HonorStandalone.Firmware");
            try{locked=mutex.WaitOne(5000);}catch(AbandonedMutexException){locked=true;}
            if(!locked){mutex.Dispose();throw new TimeoutException("Another hardware operation is in progress.");}
            try {
                var scope=new ManagementScope(@"\\.\root\wmi");scope.Options.EnablePrivileges=true;scope.Connect();
                using(var query=new ManagementObjectSearcher(scope,new ObjectQuery("SELECT * FROM OemWMIMethod")))using(var items=query.Get()){
                    var matches=items.Cast<ManagementObject>().Where(x=>Convert.ToString(x["InstanceName"])==@"ACPI\PNP0C14\HWMI_0").ToArray();
                    if(matches.Length!=1)throw new InvalidOperationException("Exactly one HWMI_0 is required.");
                    instance=new ManagementObject(matches[0].Path);
                }
            }catch{Dispose();throw;}
        }
        public byte[] Call(byte main,byte sub,byte index,byte parameter=0) {
            var command=new byte[64];command[0]=main;command[1]=sub;command[2]=index;command[3]=parameter;
            using(var input=instance.GetMethodParameters("OemWMIfun")){
                input["u8Input"]=command;
                using(var reply=instance.InvokeMethod("OemWMIfun",input,null)){
                    var data=reply["u8Output"] as byte[];
                    if(data==null||data.Length<2||data[0]!=0)throw new InvalidOperationException("Firmware rejected "+BitConverter.ToString(command,0,3));
                    return data;
                }
            }
        }
        public int Mode(){return Call(4,14,0)[1];}
        public int FanSelector(){return Call(7,13,0)[1];}
        private const int IconKeysFirst = 1, StandardFKeysFirst = 2;
        public object KeyboardSnapshot(){CheckPlatform();int value=Call(4,6,0)[1];if(value!=IconKeysFirst&&value!=StandardFKeysFirst)throw new InvalidOperationException("Unknown Fn keyboard state.");return new{FnStateCode=value,StandardFKeys=value==StandardFKeysFirst};}
        public object KeyboardMode(string command){
            CheckPlatform();
            string preference=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState","keyboard-mode.txt");
            int value=command=="keyboard-fkeys"?StandardFKeysFirst:command=="keyboard-icons"?IconKeysFirst:0;
            if(command=="keyboard-restore-preference"){
                if(!File.Exists(preference))return KeyboardSnapshot();
                // The saved value is the physical firmware mode, including settings from older versions.
                if(!Int32.TryParse(File.ReadAllText(preference).Trim(),out value)||(value!=1&&value!=2))throw new InvalidDataException("Invalid keyboard mode preference.");
            }
            if(value!=1&&value!=2)throw new ArgumentException("Unknown Fn keyboard mode.");
            int original=Call(4,6,0)[1];if(original!=1&&original!=2)throw new InvalidOperationException("Unknown original Fn keyboard state.");
            if(command=="keyboard-restore-preference"&&value==original)return KeyboardSnapshot();
            try{
                Call(4,7,(byte)value);
                for(int attempt=0;attempt<10;attempt++){
                    Thread.Sleep(80);
                    if(Call(4,6,0)[1]==value){
                        if(command!="keyboard-restore-preference"){Directory.CreateDirectory(Path.GetDirectoryName(preference));File.WriteAllText(preference,value.ToString());}
                        return KeyboardSnapshot();
                    }
                }
                throw new InvalidOperationException("Fn keyboard mode readback did not match.");
            }catch{Call(4,7,(byte)original);throw;}
        }
        public object GpuSnapshot(){
            CheckPlatform();
            int state=Call(6,42,0)[1];
            int support=Call(6,43,0)[1];
            string[] outputs=WindowsColor.OutputAdapters();
            return new {StateCode=state,HotPlugSupported=support==3,ActiveOutputAdapters=outputs};
        }
        public object SetGpu(bool enabled,bool force=false){
            CheckPlatform();
            if(!enabled)NvidiaBoost.Restore();
            if(Call(6,43,0)[1]!=3)throw new InvalidOperationException("GPU hot-plug is unsupported on this configuration.");
            int state=Call(6,42,0)[1];
            if(enabled&&state==0){
                Call(6,41,0xAA,1);
                for(int attempt=0;attempt<20;attempt++){Thread.Sleep(250);state=Call(6,42,0)[1];if(state==1||state==4)return GpuSnapshot();}
                throw new InvalidOperationException("The discrete GPU has not completed its power-on transition.");
            }
            if(!enabled&&state!=0){
                if(Mode()==3)throw new InvalidOperationException("Select Smart or Performance before integrated-only GPU mode.");
                WindowsColor.RequireIntelOutput();
                if(state!=1&&!(force&&state==4))throw new InvalidOperationException("The discrete GPU is busy or changing state. Waiting for applications to release it.");
                Call(6,41,0xAA,force?(byte)2:(byte)0);
                for(int attempt=0;attempt<20;attempt++){
                    Thread.Sleep(250);state=Call(6,42,0)[1];
                    if(state==0)return GpuSnapshot();
                    if(state==4&&!force)break;
                }
                Call(6,41,0xAA,1);
                throw new InvalidOperationException("The discrete GPU could not be turned off. Dual-GPU operation has been requested again.");
            }
            if(state==2||state==3)throw new InvalidOperationException("The discrete GPU is changing state.");
            return GpuSnapshot();
        }
        public object PolicySnapshot(){
            CheckPlatform();
            var odv=new int[9];
            for(byte index=0;index<odv.Length;index++)odv[index]=Call(3,14,index)[1];
            var charge=Call(3,22,0);
            return new {ModeCode=Mode(),DttOdv=odv,ChargeMode=charge[1],ChargeActivityCode=charge[2],ChargeStartPercent=charge[3],ChargeStopPercent=charge[4]};
        }
        public object ChargeSnapshot(){
            CheckPlatform();
            var data=Call(3,17,0);
            if(data.Length<3)throw new InvalidDataException("Short charge threshold reply.");
            return new {ChargeStartPercent=data[1],ChargeStopPercent=data[2]};
        }
        public object SetCharge(byte start,byte stop){
            CheckPlatform();
            var before=Call(3,17,0);
            if(before.Length<3||before[1]>before[2]||before[2]>100)throw new InvalidOperationException("Unexpected original charging thresholds.");
            if(before[1]!=start||before[2]!=stop){
                try { WriteCharge(start,stop); }
                catch { WriteCharge(before[1],before[2]); throw; }
            }
            return ChargeSnapshot();
        }
        private void WriteCharge(byte start,byte stop){
            Call(3,16,start,stop);
            for(int attempt=0;attempt<8;attempt++){
                Thread.Sleep(100);
                var readback=Call(3,17,0);
                if(readback.Length>=3&&readback[1]==start&&readback[2]==stop)return;
            }
            throw new InvalidOperationException("Charging thresholds did not match the request.");
        }
        public int Word(byte main,byte sub,byte index,int offset){var data=Call(main,sub,index);if(data.Length<offset+2)throw new InvalidDataException("Short firmware reply.");return data[offset]|(data[offset+1]<<8);}
        public object Snapshot(){
            int mode=Mode();var f0=Call(2,8,0);var f1=Call(2,8,1);var temp=Call(2,2,0);
            int jack=Word(6,60,0,1),v0=Word(2,9,0,2),v1=Word(2,9,1,2),a0=Word(2,9,16,2),a1=Word(2,9,17,2);
            double watts=v0>0?v0*(double)a0/1000000.0:v1*(double)a1/1000000.0;
            PowerStatus power;
            bool fanPower=GetSystemPowerStatus(out power)&&power.Ac==1&&power.Percent!=255&&power.Percent>=20;
            int fanLevels=!fanPower?0:jack==1||watts>=199?5:watts>=135?3:0;
            int selector=FanSelector();
            int fanPreset=mode==3?Array.IndexOf(FanSelectors,(byte)selector)+1:0;
            return new {Backend="Windows ACPI WMI",ModeCode=mode,Mode=ModeName(mode),CpuCelsius=temp[1]==0?(int)temp[2]:-(int)temp[2],Fan0ActualRpm=f0[1]+256*f0[2],Fan0TargetRpm=f0[3]+256*f0[4],Fan1ActualRpm=f1[1]+256*f1[2],Fan1TargetRpm=f1[3]+256*f1[4],BarrelJackPresent=jack==1,PdAdapterWatts=watts,FanPresetLevelsAvailable=fanLevels,FanPreset=fanPreset,FanSelector=selector,HonorPerformanceHosts=Process.GetProcessesByName("HnPerformanceCenter").Length+Process.GetProcessesByName("HnPerfPowerNexus").Length};
        }
        public void CheckPlatform(){
            using(var q=new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))using(var all=q.Get())if(!all.Cast<ManagementObject>().Any(x=>Convert.ToString(x["Model"])=="DRA-XX"))throw new InvalidOperationException("Writes are limited to the verified DRA-XX platform.");
            using(var q=new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))using(var all=q.Get())if(!all.Cast<ManagementObject>().Any(x=>Convert.ToString(x["SMBIOSBIOSVersion"])=="1.13"))throw new InvalidOperationException("This build's firmware writes were verified on BIOS 1.13 only.");
        }
        public bool Barrel(){return Word(6,60,0,1)==1;}
        public double PdWatts(){int v0=Word(2,9,0,2);return v0>0?v0*(double)Word(2,9,16,2)/1000000.0:Word(2,9,1,2)*(double)Word(2,9,17,2)/1000000.0;}
        public void CheckPower(bool hunter){
            PowerStatus status;if(!GetSystemPowerStatus(out status)||status.Ac!=1||status.Percent==255||status.Percent<20)throw new InvalidOperationException("AC power and at least 20% battery are required.");
            if(hunter&&!Barrel()&&PdWatts()<135)throw new InvalidOperationException("HUNTER requires the supported barrel supply or >=135W PD adapter.");
        }
        public void SetMode(int target){
            if(target!=0&&target!=1&&target!=3)throw new ArgumentException("Unknown performance mode.");
            CheckPlatform();if(target!=0)CheckPower(target==3);
            int before=Mode();
            if(target!=before)DttPolicy.Restore(this);
            if(target==3)SetGpu(true);
            Call(4,15,(byte)target);
            for(int attempt=0;attempt<10;attempt++){
                Thread.Sleep(250);
                if(Mode()!=target)continue;
                if(target==3&&before!=3){
                    string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState","last-fan-level.txt");
                    int preference;
                    if(!File.Exists(path)||!Int32.TryParse(File.ReadAllText(path).Trim(),out preference)||preference<1||preference>5)preference=5;
                    if(!Barrel()&&PdWatts()<199)preference=Math.Min(preference,3);
                    Call(7,8,FanSelectors[preference-1]);
                    if(FanSelector()!=FanSelectors[preference-1])throw new InvalidOperationException("HUNTER fan preference was not restored.");
                }
                return;
            }
            throw new InvalidOperationException("Firmware mode did not match the request.");
        }
        public void Dispose(){if(instance!=null){instance.Dispose();instance=null;}if(mutex!=null){if(locked)mutex.ReleaseMutex();mutex.Dispose();mutex=null;locked=false;}}
    }
    static string ModeName(int mode){return mode==0?"Smart":mode==1?"Performance":mode==3?"HUNTER":"Unknown";}
    public static object Execute(string command){
        using(var session=new Session()){
            if(command.StartsWith("dtt-"))return DttPolicy.Execute(session,command);
            if(command.StartsWith("gpu-clock-"))return NvidiaBoost.Execute(session,command);
            if(command=="firmware-status")return session.Snapshot();
            if(command=="touchpad-status"){
                session.CheckPlatform();
                int value=session.Call(2,15,0)[1];
                if(value!=0&&value!=1)throw new InvalidOperationException("Unknown touchpad status.");
                return new{TouchpadCode=value};
            }
            if(command=="mic-light-muted"||command=="mic-light-active"){
                session.CheckPlatform();
                bool muted=command=="mic-light-muted";
                session.Call(4,11,muted?(byte)1:(byte)0);
                return new{Accepted=true};
            }
            if(command=="keyboard-status")return session.KeyboardSnapshot();
            if(command.StartsWith("keyboard-"))return session.KeyboardMode(command);
            if(command=="firmware-mode")return new {ModeCode=session.Mode()};
            if(command=="firmware-policy")return session.PolicySnapshot();
            if(command=="charge-status")return session.ChargeSnapshot();
            if(command=="charge-home")return session.SetCharge(40,70);
            if(command=="charge-office")return session.SetCharge(70,90);
            if(command=="charge-travel")return session.SetCharge(95,100);
            if(command=="gpu-status")return session.GpuSnapshot();
            if(command=="gpu-dual")return session.SetGpu(true);
            if(command=="gpu-integrated")return session.SetGpu(false);
            if(command=="gpu-force-integrated")return session.SetGpu(false,true);
            if(command.StartsWith("fan-")){
                int level;if(!Int32.TryParse(command.Substring(4),out level)||level<1||level>5)throw new ArgumentException("Fan level must be 1..5.");
                session.CheckPlatform();session.CheckPower(true);if(session.Mode()!=3)throw new InvalidOperationException("Select HUNTER mode before a manual fan preset.");
                if(level>3&&!session.Barrel()&&session.PdWatts()<199)throw new InvalidOperationException("Fan presets 4 and 5 require the supported 200W supply.");
                var reply=session.Call(7,8,FanSelectors[level-1]);
                if(reply[1]!=FanSelectors[level-1]||session.FanSelector()!=FanSelectors[level-1])throw new InvalidOperationException("Fan-selector readback did not match.");
                Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState"));File.WriteAllText(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState"),"last-fan-level.txt"),level.ToString());
                Thread.Sleep(500);
                return new {RequestedFanPreset=level,FirmwareSelectorReadbackVerified=true,RpmTargetChangeVerified=false,Note="Firmware varies fan RPM with load; preset acknowledgement is separate from RPM response.",Status=session.Snapshot()};
            }
            int target=command=="firmware-smart"?0:command=="firmware-performance"?1:command=="firmware-hunter"?3:-1;
            if(target<0)throw new ArgumentException("Unknown firmware command.");
            int before=session.Mode();
            try{session.SetMode(target);Thread.Sleep(500);if(session.Mode()!=target)throw new InvalidOperationException("Mode was overwritten after transition.");return new {RequestedMode=ModeName(target),Verified=true,PreviousMode=ModeName(before),Status=session.Snapshot()};}
            catch{if(before==0||before==1||before==3)session.SetMode(before);throw;}
        }
    }
    public static int Menu(){
        while(true){
            Console.WriteLine("\nIndependent HONOR firmware control - DRA-XX / BIOS 1.13");
            Console.WriteLine("1 Smart    2 Performance    3 HUNTER    4 Read sensors");
            Console.WriteLine("5 Fan preset 1..5    Q Exit");
            string choice=(Console.ReadLine()??"q").Trim().ToLowerInvariant();if(choice=="q")return 0;
            string command=choice=="1"?"firmware-smart":choice=="2"?"firmware-performance":choice=="3"?"firmware-hunter":choice=="4"?"firmware-status":null;
            if(choice=="5"){Console.Write("Fan preset (1..5): ");command="fan-"+(Console.ReadLine()??"").Trim();}
            if(command==null)continue;
            try{Console.WriteLine(Json.Serialize(Execute(command)));}catch(Exception ex){Console.WriteLine(Json.Serialize(new{Error=ex.Message}));}
        }
    }
}
