using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HonorControl;

internal enum OverlayIcon
{
    Performance, Color, RefreshRate, Keyboard, Touchpad, TouchpadOff, Microphone, MicrophoneMuted, Warning, Speaker, Headphones
}

internal sealed class OverlayWindow
{
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromMilliseconds(2400) };
    private nint _window;

    [StructLayout(LayoutKind.Sequential)] private record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private record struct Size(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Depth;
        public uint Compression, ImageSize; public int XResolution, YResolution; public uint Colors, Important;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint screen, ref Point position, ref Size size, nint source, ref Point origin, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);

    public OverlayWindow() => _dismiss.Tick += (_, _) => { _dismiss.Stop(); ShowWindow(_window, 0); };

    public void Present(string heading, string detail, OverlayIcon icon)
    {
        try
        {
            _dismiss.Stop();
            bool warning = icon == OverlayIcon.Warning;
            _dismiss.Interval = TimeSpan.FromMilliseconds(warning ? 4200 : 2400);
            if (_window == 0)
            {
                // TOPMOST | TRANSPARENT | TOOLWINDOW | LAYERED | NOACTIVATE. An unowned popup also works from the tray.
                _window = CreateWindowExW(0x080800A8, "STATIC", L.T("Honor Control · 切换提示"), 0x80000000, 0, 0, 0, 0, 0, 0, 0, 0);
                if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            nint monitor = MonitorFromWindow(GetForegroundWindow(), 2);
            var screen = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref screen)) throw new Win32Exception(Marshal.GetLastWin32Error());
            float scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
            var size = new Size((int)Math.Round((warning ? 420 : 352) * scale), (int)Math.Round((warning ? 112 : 92) * scale));
            var position = new Point(screen.Work.Left + (screen.Work.Right - screen.Work.Left - size.Width) / 2, screen.Work.Bottom - size.Height - (int)(48 * scale));
            DrawAndPresent(heading, detail, icon, scale, position, size);
            _dismiss.Start();
        }
        catch (Exception ex)
        {
            if (_window != 0) ShowWindow(_window, 0);
            try { HonorControl.Diagnostics.DiagnosticLog.Append(Path.Combine(AppPaths.UserState, "errors.log"), $"{DateTimeOffset.Now:o} Overlay: {ex}{Environment.NewLine}"); } catch { }
        }
    }

    private void DrawAndPresent(string heading, string detail, OverlayIcon icon, float scale, Point position, Size size)
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint dib = 0, previous = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, Depth = 32 };
            dib = CreateDIBSection(dc, ref info, 0, out nint pixels, 0, 0);
            if (dib == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, dib);
            using (var bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(scale, scale);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                bool warning = icon == OverlayIcon.Warning;
                FillRound(graphics, 2, 2, warning ? 416 : 348, warning ? 108 : 88, 18, Color.FromArgb(250, 28, 40, 57));
                FillRound(graphics, 18, 20, 52, 52, 13, Color.FromArgb(41, 78, 112));
                DrawIcon(graphics, icon);
                using var label = new Font("Microsoft YaHei UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
                using var title = new Font("Microsoft YaHei UI", 23, FontStyle.Bold, GraphicsUnit.Pixel);
                using var muted = new SolidBrush(Color.FromArgb(187, 202, 221));
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                if (warning)
                {
                    using var reason = new Font("Microsoft YaHei UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var wrap = new StringFormat { Trimming = StringTrimming.EllipsisWord };
                    format.Alignment = StringAlignment.Center;
                    wrap.Alignment = StringAlignment.Center;
                    wrap.LineAlignment = StringAlignment.Center;
                    graphics.DrawString(heading, label, muted, new RectangleF(86, 18, 314, 22), format);
                    graphics.DrawString(detail, reason, Brushes.White, new RectangleF(86, 42, 314, 56), wrap);
                }
                else
                {
                    graphics.DrawString(detail, label, muted, new RectangleF(86, 20, 248, 22), format);
                    graphics.DrawString(heading, title, Brushes.White, new RectangleF(86, 42, 248, 36), format);
                }
                graphics.Flush();
            }
            var origin = new Point(0, 0);
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_window, 0, ref position, ref size, dc, ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetWindowPos(_window, -1, position.X, position.Y, size.Width, size.Height, 0x50)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            if (dib != 0) DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    private static void DrawIcon(Graphics graphics, OverlayIcon icon)
    {
        var transform = graphics.Save();
        graphics.TranslateTransform(28, 30);
        using var pen = new Pen(Color.FromArgb(200, 228, 255), 2.2f)
        {
            LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
        };
        void Line(float x1, float y1, float x2, float y2) => graphics.DrawLine(pen, x1, y1, x2, y2);
        void Dot(float x, float y) => graphics.FillEllipse(Brushes.AliceBlue, x, y, 2.6f, 2.6f);
        void Box(float x, float y, float width, float height, float radius)
        {
            using var outline = RoundedRectangle(x, y, width, height, radius);
            graphics.DrawPath(pen, outline);
        }

        switch (icon)
        {
            case OverlayIcon.Color:
                graphics.DrawEllipse(pen, 4, 4, 24, 24);
                graphics.DrawArc(pen, 11, 11, 10, 10, 30, 250);
                Dot(8, 14); Dot(14, 7); Dot(21, 12);
                break;
            case OverlayIcon.RefreshRate:
                Box(3, 5, 26, 18, 3);
                graphics.DrawArc(pen, 11, 9, 10, 10, 5, 265);
                graphics.DrawLines(pen, [new PointF(16, 8), new PointF(21, 9), new PointF(20, 13)]);
                Line(11, 27, 21, 27);
                break;
            case OverlayIcon.Keyboard:
                Box(2, 8, 28, 17, 3);
                Dot(7, 12); Dot(13, 12); Dot(19, 12); Dot(24, 12);
                Line(10, 20, 22, 20);
                break;
            case OverlayIcon.Touchpad:
            case OverlayIcon.TouchpadOff:
                Box(3, 6, 26, 20, 4);
                Line(4, 21, 28, 21); Line(16, 21, 16, 26);
                break;
            case OverlayIcon.Microphone:
            case OverlayIcon.MicrophoneMuted:
                Box(12, 3, 8, 15, 4);
                graphics.DrawArc(pen, 8, 9, 16, 14, 0, 180);
                Line(16, 23, 16, 28); Line(12, 28, 20, 28);
                break;
            case OverlayIcon.Speaker:
                graphics.DrawPolygon(pen, [new PointF(4, 12), new PointF(10, 12), new PointF(16, 6), new PointF(16, 26), new PointF(10, 20), new PointF(4, 20)]);
                graphics.DrawArc(pen, 13, 9, 10, 14, -55, 110);
                graphics.DrawArc(pen, 12, 4, 17, 24, -50, 100);
                break;
            case OverlayIcon.Headphones:
                graphics.DrawArc(pen, 4, 4, 24, 25, 180, 180);
                Box(4, 16, 6, 11, 2); Box(22, 16, 6, 11, 2);
                break;
            case OverlayIcon.Warning:
                graphics.DrawPolygon(pen, [new PointF(16, 4), new PointF(29, 27), new PointF(3, 27)]);
                Line(16, 12, 16, 19); Dot(14.7f, 22);
                break;
            case OverlayIcon.Performance:
                graphics.DrawPolygon(pen, [new PointF(18, 2), new PointF(6, 17), new PointF(15, 17), new PointF(13, 30), new PointF(26, 13), new PointF(17, 13)]);
                break;
        }
        if (icon is OverlayIcon.TouchpadOff or OverlayIcon.MicrophoneMuted)
        {
            using var gap = new Pen(Color.FromArgb(41, 78, 112), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(gap, 5, 4, 27, 28);
            Line(5, 4, 27, 28);
        }
        graphics.Restore(transform);
    }

    private static void FillRound(Graphics graphics, float x, float y, float width, float height, float radius, Color color)
    {
        using var path = RoundedRectangle(x, y, width, height, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float diameter = 2 * radius;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void DismissAndClose()
    {
        _dismiss.Stop();
        if (_window != 0) DestroyWindow(_window);
        _window = 0;
    }
}
