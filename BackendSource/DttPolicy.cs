using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

internal static class DttPolicy
{
    internal sealed class Values
    {
        public int Mode;
        public int Pl1;
        public int Epo;
        public int Fan;
    }
    internal sealed class Journal
    {
        public Values Original;
        public Values Applied;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac,Flags,Percent,Saver;public uint Lifetime,FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus value);
    private static readonly string State=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HonorControl","DeviceState");
    private static readonly string FilePath=Path.Combine(State,"dtt-restore.json");
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();

    private static Values Read(HonorFirmware.Session session)
    {
        return new Values {Mode=session.Mode(),Pl1=session.Call(3,14,5)[1],Epo=session.Call(3,14,8)[1],Fan=session.FanSelector()};
    }
    public static object Execute(HonorFirmware.Session session,string command)
    {
        session.CheckPlatform();
        if(command=="dtt-status")return Read(session);
        if(command=="dtt-restore"){Restore(session);return Read(session);}
        // The PL1/EPO/fan table was verified on the 125H configuration only.
        // Mode, fan-preset, Fn and charging controls use their separate platform guards.
        using(var processors=new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
        using(var values=processors.Get())
            if(!values.Cast<ManagementObject>().Any(item=>Convert.ToString(item["Name"]).Contains("125H")))
                throw new NotSupportedException("Scene DTT writes are verified for Core Ultra 5 125H only. Other CPU configurations remain unchanged.");
        int[] rule;
        if(command=="dtt-probe45")rule=new[]{-1,session.Mode(),1,-1,3,94,1,160,36,45};
        else
        {
            int index;
            if(!command.StartsWith("dtt-apply-")||!Int32.TryParse(command.Substring(10),out index)||index<0||index>255)
                throw new ArgumentException("Unknown DTT policy.");
            rule=File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"DraPolicies.tsv"))
                .Where(line=>line.Length>0).Select(line=>line.Split('\t').Select(Int32.Parse).ToArray()).Single(row=>row[0]==index);
        }
        if(rule.Length!=10||rule[5]<0||rule[5]>99||rule[6]<1||rule[6]>32||rule[7]<160||rule[7]>173||rule[9]<9||rule[9]>65)
            throw new InvalidDataException("Invalid DRA-XX DTT policy.");
        var current=Read(session);
        PowerStatus power;
        if(current.Mode!=rule[1]||(current.Mode!=0&&current.Mode!=1)||!GetSystemPowerStatus(out power)||power.Ac>1||power.Ac!=rule[2])
            throw new InvalidOperationException("The firmware mode or power source changed before the DTT update.");
        if(Process.GetProcessesByName("HnPerformanceCenter").Length+Process.GetProcessesByName("HnPerfPowerNexus").Length>0)
            throw new InvalidOperationException("Original HONOR performance hosts are running.");
        if(session.Call(3,14,3)[1]==1||session.Call(3,14,4)[1]==1||session.Call(3,14,6)[1]!=0)
            throw new InvalidOperationException("A firmware override is controlling the platform power policy.");
        if(!Valid(current))throw new InvalidOperationException("The current firmware policy cannot be restored by this build.");
        var desired=new Values{Mode=current.Mode,Pl1=rule[5],Epo=rule[6],Fan=rule[7]};
        Journal journal=File.Exists(FilePath)?Json.Deserialize<Journal>(File.ReadAllText(FilePath)):new Journal{Original=current,Applied=current};
        if(journal==null||!Valid(journal.Original)||!Valid(journal.Applied))throw new InvalidDataException("Invalid DTT restore record.");
        if(journal.Applied.Mode!=current.Mode){Restore(session);current=Read(session);journal=new Journal{Original=current,Applied=current};}
        if(current.Pl1!=journal.Applied.Pl1||current.Epo!=journal.Applied.Epo||current.Fan!=journal.Applied.Fan)
        {
            // EC _QF8 clears ODV5/ODV8 asynchronously after mode/power events.
            // Preserve that new baseline and reacquire only with the firmware override checks above satisfied.
            if(current.Pl1==0||current.Epo==0){Restore(session);current=Read(session);journal=new Journal{Original=current,Applied=current};}
            else throw new InvalidOperationException("Another controller changed the DTT policy.");
        }
        journal.Applied=desired;
        Save(journal);
        try
        {
            bool verified=false;
            for(int attempt=0;attempt<3;attempt++)
            {
                Write(session,current,desired,true);
                System.Threading.Thread.Sleep(150);
                current=Read(session);
                if(current.Pl1==desired.Pl1&&current.Epo==desired.Epo&&current.Fan==desired.Fan){verified=true;break;}
                if(current.Mode!=desired.Mode)break;
            }
            if(!verified)throw new InvalidOperationException("Combined DTT power and fan readback did not match.");
            if(session.Mode()!=desired.Mode)throw new InvalidOperationException("The firmware mode changed during the DTT update.");
            return new {Verified=true,RequestedPl1Watts=rule[9],Status=Read(session)};
        }
        catch
        {
            Restore(session);
            throw;
        }
    }
    private static bool Valid(Values value)
    {
        return value!=null&&(value.Mode==0||value.Mode==1)&&value.Pl1>=0&&value.Pl1<=99&&value.Epo>=0&&value.Epo<=32&&value.Fan>=160&&value.Fan<=173;
    }
    private static void SetOdv(HonorFirmware.Session session,byte index,int value)
    {
        session.Call(3,15,index,(byte)value);
        if(session.Call(3,14,index)[1]!=value)throw new InvalidOperationException("DTT policy readback did not match.");
    }
    private static void SetFan(HonorFirmware.Session session,int value)
    {
        session.Call(7,8,(byte)value);
        if(session.FanSelector()!=value)throw new InvalidOperationException("Automatic fan table readback did not match.");
    }
    private static void Write(HonorFirmware.Session session,Values current,Values target,bool fan)
    {
        if(current.Pl1!=target.Pl1)SetOdv(session,5,target.Pl1);
        if(current.Epo!=target.Epo)SetOdv(session,8,target.Epo);
        if(fan&&current.Fan!=target.Fan)SetFan(session,target.Fan);
    }
    internal static void Restore(HonorFirmware.Session session)
    {
        if(!File.Exists(FilePath))return;
        var journal=Json.Deserialize<Journal>(File.ReadAllText(FilePath));
        if(journal==null||!Valid(journal.Original)||!Valid(journal.Applied))throw new InvalidDataException("Invalid DTT restore record.");
        Values current=Read(session),original=journal.Original,applied=journal.Applied;
        if(current.Pl1==applied.Pl1&&current.Pl1!=original.Pl1)SetOdv(session,5,original.Pl1);
        if(current.Epo==applied.Epo&&current.Epo!=original.Epo)SetOdv(session,8,original.Epo);
        if((current.Mode==0||current.Mode==1)&&current.Fan==applied.Fan&&current.Fan!=original.Fan)SetFan(session,original.Fan);
        File.Delete(FilePath);
    }
    private static void Save(Journal value)
    {
        Directory.CreateDirectory(State);
        string temp=FilePath+".tmp";
        File.WriteAllText(temp,Json.Serialize(value));
        if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);
    }
}
