using PersonalAgent.Core.Computer;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PersonalAgent.Computer;

internal sealed class WindowsCaptureNative : IWindowsCaptureNative
{
    public bool IsDesktopAvailable()
    {
        using var process = Process.GetCurrentProcess();
        if (process.SessionId == 0) return false;
        nint session = 0;
        try
        {
            if (!WTSQuerySessionInformation(0, process.SessionId, 8, out session, out var bytes) || bytes < 4 || Marshal.ReadInt32(session) != 0) return false;
        }
        finally { if (session != 0) WTSFreeMemory(session); }
        var desktop = OpenInputDesktop(0, false, 0x1); // DESKTOP_READOBJECTS only, never switch/unlock.
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(desktop); }
    }

    public ScreenMonitor GetPrimaryMonitor()
    {
        using var dpi = new PhysicalDpiScope();
        var monitor = MonitorFromPoint(new Point { X = 0, Y = 0 }, 1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bounds = new ScreenBounds(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top);
        bounds.Validate();
        // Hidden, non-activating metadata window: GetDpiForWindow is the DPI-aware API.
        // GetDpiForMonitor is explicitly unsuitable for per-monitor-aware threads.
        var window = CreateWindowEx(0x08000000, "STATIC", "", 0x80000000,
            bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, 1, 1, 0, 0, 0, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var value = GetDpiForWindow(window);
            if (value == 0) throw new Win32Exception("Could not read monitor DPI.");
            return new ScreenMonitor(info.Device, bounds, value, value, (info.Flags & 1) != 0);
        }
        finally { DestroyWindow(window); }
    }
    public BitmapSource CaptureBitmap(ScreenBounds bounds) => GdiScreenCapture.Capture(bounds);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Bounds, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}

internal sealed class PhysicalDpiScope : IDisposable
{
    private readonly nint previous;
    public PhysicalDpiScope()
    {
        previous = SetThreadDpiAwarenessContext(-4); // PerMonitorV2, scoped to this native operation.
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (SetThreadDpiAwarenessContext(previous) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

// Promoted from PA-008's diagnostic: the same GDI copy/cleanup now serves the driver and attended fixture.
internal static class GdiScreenCapture
{
    public static BitmapSource Capture(ScreenBounds bounds)
    {
        bounds.Validate();
        using var dpi = new PhysicalDpiScope();
        var screen = GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (memory == 0 || bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020)) throw new Win32Exception(Marshal.GetLastWin32Error()); // SRCCOPY | CAPTUREBLT
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
