using System.Runtime.InteropServices;
namespace HonorControl;
internal sealed class TrayIcon : IDisposable
{
    private readonly nint _hwnd, _icon;
    private readonly SubclassProc _callback;
    private readonly Action<string> _action;
    private const uint Message = 0x8000 + 83;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public nint Hwnd; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string file, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string message);
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    public TrayIcon(nint hwnd, Action<string> action)
    {
        _hwnd = hwnd; _action = action; _callback = WindowProc;
        _icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "HonorControl.ico"), 1, 32, 32, 0x10);
        if (_icon == 0 || !SetWindowSubclass(hwnd, _callback, 83, 0)) throw new InvalidOperationException(L.T("无法创建托盘图标。"));
        Add();
    }
    private void Add() { var data = Data(); Shell_NotifyIconW(0, ref data); }
    private NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Hwnd = _hwnd, Id = 1, Flags = 1 | 2 | 4, Callback = Message, Icon = _icon, Tip = L.T("Honor Control · 性能与色彩常驻"), Info = "", InfoTitle = "" };
    private nint WindowProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (msg == _taskbarCreated) Add();
        if (msg == Message)
        {
            uint mouse = (uint)lparam.ToInt64() & 0xffff;
            if (mouse == 0x203) _action("show");
            else if (mouse == 0x205) ShowMenu();
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wparam, lparam);
    }
    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0, 1, L.T("打开 Honor Control")); AppendMenuW(menu, 0x800, 0, null);
            AppendMenuW(menu, 0, 10, L.T("智能模式")); AppendMenuW(menu, 0, 11, L.T("高能模式")); AppendMenuW(menu, 0, 13, L.T("HUNTER 模式"));
            AppendMenuW(menu, 0, 14, L.T("循环内屏刷新率"));
            AppendMenuW(menu, 0x800, 0, null); AppendMenuW(menu, 0, 20, L.T("原生色彩")); AppendMenuW(menu, 0, 21, L.T("sRGB 色彩")); AppendMenuW(menu, 0, 22, L.T("P3 色彩"));
            AppendMenuW(menu, 0x800, 0, null); AppendMenuW(menu, 0, 99, L.T("退出（本次会话停止常驻）"));
            GetCursorPos(out var point); SetForegroundWindow(_hwnd);
            uint choice = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _hwnd, 0);
            string? command = choice switch { 1 => "show", 10 => "firmware-smart", 11 => "firmware-performance", 13 => "firmware-hunter", 14 => "cycle-refresh", 20 => "native", 21 => "srgb", 22 => "p3", 99 => "quit", _ => null };
            if (command is not null) _action(command);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose() { var data = Data(); Shell_NotifyIconW(2, ref data); RemoveWindowSubclass(_hwnd, _callback, 83); DestroyIcon(_icon); }
}
