using System.Security.Principal;
namespace HonorControl.Shared;
public static class RuntimeIdentity
{
    public static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
    public static string PipeName => "HonorControl.Resident." + Sid;
    public static string UserState => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorControl");
    public static string DeviceState => Path.Combine(UserState, "DeviceState");
    public static string PauseFile => Path.Combine(UserState, "pause-resident.flag");
    public const string AgentTask = "HonorControl.Agent";
    public const string UiTask = "HonorControl.UI";
}
