using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;

internal static class WindowsColor {
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low;public int High; }
    sealed class Identity { public Luid Adapter,TargetAdapter;public uint Source,Target; }
    [StructLayout(LayoutKind.Sequential)] struct AdvancedColor { public int Type;public uint Size;public Luid Adapter;public uint Id,Flags,Encoding,BitsPerChannel; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct AdapterName { public int Type;public uint Size;public Luid Adapter;public uint Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path; }
    [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint pathCount,IntPtr paths,ref uint modeCount,IntPtr modes,IntPtr topology);
    [DllImport("user32.dll")]static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);
    [DllImport("user32.dll")]static extern int DisplayConfigGetDeviceInfo(ref AdapterName info);
    [DllImport("mscms.dll")]static extern int ColorProfileGetDisplayDefault(uint scope,Luid adapter,uint source,uint type,uint subtype,out IntPtr profile);
    [DllImport("mscms.dll",CharSet=CharSet.Unicode)]static extern int ColorProfileSetDisplayDefaultAssociation(uint scope,string profile,uint type,uint subtype,Luid adapter,uint source);
    [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool InstallColorProfileW(string machine,string profilePath);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr pointer);
    public static string[] OutputAdapters(){
        for(int attempt=0;attempt<3;attempt++){
            uint pc,mc;int result=GetDisplayConfigBufferSizes(2,out pc,out mc);
            if(result!=0)throw new InvalidOperationException("Display configuration query failed: "+result);
            if(pc>32||mc>128)throw new InvalidOperationException("Unexpected display configuration size.");
            IntPtr paths=Marshal.AllocHGlobal(checked((int)pc*72)),modes=Marshal.AllocHGlobal(checked((int)mc*64));
            try{
                result=QueryDisplayConfig(2,ref pc,paths,ref mc,modes,IntPtr.Zero);
                if(result==122)continue;
                if(result!=0)throw new InvalidOperationException("Display configuration read failed: "+result);
                var names=new List<string>();
                for(int index=0;index<pc;index++){
                    IntPtr path=IntPtr.Add(paths,index*72);
                    var info=new AdapterName{Type=4,Size=(uint)Marshal.SizeOf(typeof(AdapterName)),Adapter=new Luid{Low=(uint)Marshal.ReadInt32(path,20),High=Marshal.ReadInt32(path,24)}};
                    result=DisplayConfigGetDeviceInfo(ref info);
                    if(result!=0||String.IsNullOrEmpty(info.Path))throw new InvalidOperationException("Cannot identify the active display adapter.");
                    if(!names.Contains(info.Path))names.Add(info.Path);
                }
                return names.ToArray();
            }finally{Marshal.FreeHGlobal(paths);Marshal.FreeHGlobal(modes);}
        }
        throw new InvalidOperationException("Display topology is changing.");
    }
    public static void RequireIntelOutput(){
        string[] adapters=OutputAdapters();
        if(adapters.Length==0)throw new InvalidOperationException("Cannot verify an active Intel display output.");
        foreach(string adapter in adapters)if(adapter.IndexOf("VEN_8086",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("An active display uses another GPU. Select Optimus before turning off the discrete GPU.");
    }
    static Identity GetIdentity(){
        uint pc,mc;int result=GetDisplayConfigBufferSizes(2,out pc,out mc);if(result!=0)throw new InvalidOperationException("Display configuration query failed: "+result);
        IntPtr paths=Marshal.AllocHGlobal(checked((int)pc*72)),modes=Marshal.AllocHGlobal(checked((int)mc*64));
        try {
            result=QueryDisplayConfig(2,ref pc,paths,ref mc,modes,IntPtr.Zero);
            if(result!=0)throw new InvalidOperationException("Display configuration read failed: "+result);
            if(pc!=1||(uint)Marshal.ReadInt32(paths,36)!=0x80000000)throw new InvalidOperationException("ICC sync currently requires the single internal display configuration verified on this laptop.");
            return new Identity{
                Adapter=new Luid{Low=(uint)Marshal.ReadInt32(paths),High=Marshal.ReadInt32(paths,4)},Source=(uint)Marshal.ReadInt32(paths,8),
                TargetAdapter=new Luid{Low=(uint)Marshal.ReadInt32(paths,20),High=Marshal.ReadInt32(paths,24)},Target=(uint)Marshal.ReadInt32(paths,28)
            };
        }finally{Marshal.FreeHGlobal(paths);Marshal.FreeHGlobal(modes);}
    }
    public static void RequireSdr(){
        var target=GetIdentity();
        var info=new AdvancedColor{Type=9,Size=(uint)Marshal.SizeOf(typeof(AdvancedColor)),Adapter=target.TargetAdapter,Id=target.Target};
        int result=DisplayConfigGetDeviceInfo(ref info);
        if(result!=0)throw new InvalidOperationException("Cannot verify SDR output: "+result);
        if((info.Flags&2)!=0)throw new InvalidOperationException("Display effects require SDR output. Turn off HDR or Windows automatic color management before applying.");
    }
    public static string GetProfile(){
        Identity id=GetIdentity();IntPtr name;int hr=ColorProfileGetDisplayDefault(1,id.Adapter,id.Source,0,7,out name);
        if(hr<0)Marshal.ThrowExceptionForHR(hr);
        try{if(name==IntPtr.Zero)throw new InvalidOperationException("Windows returned no display profile.");return Marshal.PtrToStringUni(name);}finally{if(name!=IntPtr.Zero)LocalFree(name);}
    }
    public static void SetProfile(string profile){
        if(String.IsNullOrWhiteSpace(profile)||Path.GetFileName(profile)!=profile)throw new InvalidDataException("Invalid installed profile name.");
        string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"spool\drivers\color",profile);
        if(!File.Exists(installed))throw new FileNotFoundException("Install the bundled ICC profiles before applying hardware and ICC together.",installed);
        Identity id=GetIdentity();int hr=ColorProfileSetDisplayDefaultAssociation(1,profile,0,7,id.Adapter,id.Source);
        if(hr<0)Marshal.ThrowExceptionForHR(hr);
        if(!String.Equals(GetProfile(),profile,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("ICC association readback mismatch.");
    }
    public static object InstallProfiles(){
        foreach(string name in new[]{"HonorsRGB.icc","HonorP3.icc","HonorNative.icc"}){
            string target=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"spool\drivers\color",name);
            if(File.Exists(target))continue;
            if(!InstallColorProfileW(null,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name)))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return new{Installed=true,Profiles=new[]{"HonorsRGB.icc","HonorP3.icc","HonorNative.icc"}};
    }
}
