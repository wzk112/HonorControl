using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

internal static class NvidiaBoost
{
    // NVML public clock-offset API, values in MHz. Only the two DRA-XX OEM presets are exposed.
    [StructLayout(LayoutKind.Sequential)] private struct Offset { public uint Version,Type,Pstate; public int Value,Min,Max; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint index,out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StringQuery(IntPtr device,StringBuilder text,uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Clock(IntPtr device,ref Offset value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Temperature(IntPtr device,uint sensor,out uint value);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr library,string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);
    private static readonly string FilePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState","gpu-clock-restore.json");
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    internal sealed class Journal { public string Uuid; public int OriginalGraphics,OriginalMemory,AppliedGraphics,AppliedMemory; }

    private sealed class Device : IDisposable
    {
        private IntPtr library,device;
        private bool initialized;
        private Clock read,write;
        public string Uuid,Name;
        public uint Celsius;
        private Delegate Get(string name,Type type)
        {
            IntPtr proc=GetProcAddress(library,name);
            if(proc==IntPtr.Zero)throw new NotSupportedException("The installed NVIDIA driver lacks "+name);
            return Marshal.GetDelegateForFunctionPointer(proc,type);
        }
        public Device()
        {
            library=LoadLibraryEx(Path.Combine(Environment.SystemDirectory,"nvml.dll"),IntPtr.Zero,0x1100);
            if(library==IntPtr.Zero)throw new InvalidOperationException("NVIDIA NVML is unavailable.");
            try
            {
                Check(((Init)Get("nvmlInit_v2",typeof(Init)))(),"initialize");initialized=true;
                Check(((Handle)Get("nvmlDeviceGetHandleByIndex_v2",typeof(Handle)))(0,out device),"select GPU");
                var name=new StringBuilder(128);Check(((StringQuery)Get("nvmlDeviceGetName",typeof(StringQuery)))(device,name,128),"read GPU name");Name=name.ToString();
                if(Name!="NVIDIA GeForce RTX 4060 Laptop GPU")throw new NotSupportedException("Clock presets are verified for the installed RTX 4060 Laptop GPU only.");
                var uuid=new StringBuilder(128);Check(((StringQuery)Get("nvmlDeviceGetUUID",typeof(StringQuery)))(device,uuid,128),"read GPU identity");Uuid=uuid.ToString();
                Check(((Temperature)Get("nvmlDeviceGetTemperature",typeof(Temperature)))(device,0,out Celsius),"read GPU temperature");
                read=(Clock)Get("nvmlDeviceGetClockOffsets",typeof(Clock));
                write=(Clock)Get("nvmlDeviceSetClockOffsets",typeof(Clock));
            }
            catch { Dispose();throw; }
        }
        public Offset Read(uint type)
        {
            var value=new Offset{Version=(uint)Marshal.SizeOf(typeof(Offset))|(1u<<24),Type=type,Pstate=0};
            Check(read(device,ref value),"read clock offset");return value;
        }
        public void Set(uint type,int value)
        {
            var info=Read(type);
            if(value<info.Min||value>info.Max)throw new InvalidOperationException("The requested clock offset is outside the driver limits.");
            if(value==info.Value)return;
            info.Value=value;Check(write(device,ref info),"set clock offset");
            if(Read(type).Value!=value)throw new InvalidOperationException("NVIDIA clock-offset readback did not match.");
        }
        public void Dispose()
        {
            if(library!=IntPtr.Zero){if(initialized)((Init)Get("nvmlShutdown",typeof(Init)))();FreeLibrary(library);library=IntPtr.Zero;initialized=false;}
        }
    }
    private static void Check(int code,string action){if(code!=0)throw new InvalidOperationException("NVIDIA could not "+action+" (NVML "+code+").");}
    private static object Snapshot(Device gpu)
    {
        var graphics=gpu.Read(0);var memory=gpu.Read(2);
        return new {GraphicsOffsetMhz=graphics.Value,MemoryOffsetMhz=memory.Value,GpuCelsius=gpu.Celsius,Owned=File.Exists(FilePath)};
    }
    public static object Execute(HonorFirmware.Session firmware,string command)
    {
        firmware.CheckPlatform();
        if(command=="gpu-clock-restore"){Restore();return new{Restored=true};}
        if(firmware.Call(6,42,0)[1]==0)throw new InvalidOperationException("The discrete GPU is off.");
        using(var gpu=new Device())
        {
            if(command=="gpu-clock-status")return Snapshot(gpu);
            int desired=command=="gpu-clock-base"?50:command=="gpu-clock-game"?200:-1;
            if(desired<0)throw new ArgumentException("Unknown GPU clock preset.");
            firmware.CheckPower(false);
            if(System.Diagnostics.Process.GetProcessesByName("HnPerformanceCenter").Length+System.Diagnostics.Process.GetProcessesByName("HnPerfPowerNexus").Length!=0)
                throw new InvalidOperationException("Original HONOR performance hosts are running.");
            if(gpu.Celsius>=80)throw new InvalidOperationException("GPU clock boost is paused at 80 C or above.");
            int graphics=gpu.Read(0).Value,memory=gpu.Read(2).Value;
            Journal journal=File.Exists(FilePath)?Json.Deserialize<Journal>(File.ReadAllText(FilePath)):new Journal{Uuid=gpu.Uuid,OriginalGraphics=graphics,OriginalMemory=memory,AppliedGraphics=graphics,AppliedMemory=memory};
            Validate(journal);
            if(journal.Uuid!=gpu.Uuid||graphics!=journal.AppliedGraphics||memory!=journal.AppliedMemory)
                throw new InvalidOperationException("Another controller changed the GPU clock offsets.");
            journal.AppliedGraphics=desired;journal.AppliedMemory=100;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath+".tmp",Json.Serialize(journal));
            if(File.Exists(FilePath))File.Replace(FilePath+".tmp",FilePath,null);else File.Move(FilePath+".tmp",FilePath);
            try { gpu.Set(0,desired);gpu.Set(2,100);return Snapshot(gpu); }
            catch { Restore();throw; }
        }
    }
    private static void Validate(Journal journal)
    {
        if(journal==null||String.IsNullOrEmpty(journal.Uuid)||journal.OriginalGraphics< -1000||journal.OriginalGraphics>1000||journal.OriginalMemory< -2000||journal.OriginalMemory>6000||journal.AppliedGraphics<0||journal.AppliedGraphics>200||journal.AppliedMemory<0||journal.AppliedMemory>100)
            throw new InvalidDataException("Invalid GPU clock restore record.");
    }
    public static void Restore()
    {
        if(!File.Exists(FilePath))return;
        var journal=Json.Deserialize<Journal>(File.ReadAllText(FilePath));Validate(journal);
        using(var gpu=new Device())
        {
            if(journal.Uuid!=gpu.Uuid)throw new InvalidOperationException("GPU identity differs from the restore record.");
            if(gpu.Read(0).Value==journal.AppliedGraphics)gpu.Set(0,journal.OriginalGraphics);
            if(gpu.Read(2).Value==journal.AppliedMemory)gpu.Set(2,journal.OriginalMemory);
        }
        File.Delete(FilePath);
    }
}
