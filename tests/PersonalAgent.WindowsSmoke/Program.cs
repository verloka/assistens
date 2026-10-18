using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PersonalAgent.WindowsSmoke;

// Attended diagnostic only. Never registered with the agent or launched by tests.
internal static class Program
{
    [STAThread]
    public static void Main() => new Application().Run(new SmokeWindow());
}

internal sealed class SmokeWindow : Window
{
    private readonly TextBox target = new() { IsReadOnly = true, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Image preview = new() { Height = 170, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button run = new() { Content = "Capture this window and type one x in the test field", Margin = new Thickness(0, 8, 0, 8) };
    private nint handle;
    private bool registered;
    private bool hotkeyObserved;

    public SmokeWindow()
    {
        Title = "PA-008 attended Windows diagnostic";
        Width = 680;
        Height = 560;
        ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Click the button yourself while this window is foreground. Release all keys. " +
                   "Only this window is captured, in memory. Then press Ctrl+Shift+F10 yourself to test the hotkey. " +
                   "Close this window to unregister it. No model, clipboard, focus activation or background input.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(run);
        panel.Children.Add(target);
        panel.Children.Add(report);
        panel.Children.Add(preview);
        Content = panel;
        run.Click += RunSmoke;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(OnMessage);
        };
        Closed += (_, _) =>
        {
            if (registered) Native.UnregisterHotKey(handle, 1);
            HwndSource.FromHwnd(handle)?.RemoveHook(OnMessage);
        };
    }

    private void RunSmoke(object sender, RoutedEventArgs args)
    {
        try
        {
            RequireForeground();
            if (Enumerable.Range(1, 254).Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0))
                throw new InvalidOperationException("Release all keys/buttons before running; nothing injected.");
            if (!registered)
            {
                registered = Native.RegisterHotKey(handle, 1, 0x4000 | 0x2 | 0x4, 0x79); // Ctrl+Shift+F10, no-repeat
                if (!registered) throw new Win32Exception(Marshal.GetLastWin32Error(), "Hotkey unavailable; nothing injected.");
            }
            preview.Source = Native.CaptureWindow(handle);
            Write($"Capture OK; {((BitmapSource)preview.Source).PixelWidth}x{((BitmapSource)preview.Source).PixelHeight}; " +
                  $"DPI={Native.GetDpiForWindow(handle)}; monitors={Native.GetSystemMetrics(80)}; " +
                  $"virtual origin=({Native.GetSystemMetrics(76)},{Native.GetSystemMetrics(77)}).");
            target.IsReadOnly = false;
            target.Clear();
            target.Focus();
            RequireForeground();
            if (!target.IsKeyboardFocused) throw new InvalidOperationException("Test field has no focus; nothing injected.");
            // Exactly one short atomic Unicode pair, with no modifiers or focus-changing keys.
            var pair = new[] { Native.Key('x', false), Native.Key('x', true) };
            var inserted = Native.SendInput(2, pair, Marshal.SizeOf<Native.Input>());
            if (inserted != 2)
            {
                // Never retry the character. Complete only our own key-up after a partial insertion.
                if (inserted == 1) Native.SendInput(1, [Native.Key('x', true)], Marshal.SizeOf<Native.Input>());
                throw new InvalidOperationException($"SendInput inserted {inserted}/2 events; no retry. UIPI may block input.");
            }
            run.IsEnabled = false;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                target.IsReadOnly = true;
                Write(target.Text == "x" ? "SendInput observed OK: one x in test field." : "SendInput observation FAILED; do not claim passed.");
                Write(hotkeyObserved ? "Hotkey already observed." : "Now press Ctrl+Shift+F10 manually, then close the window.");
            }));
        }
        catch (Exception exception)
        {
            target.IsReadOnly = true;
            Write($"BLOCKED: {exception.Message}");
        }
    }

    private void RequireForeground()
    {
        if (!IsActive || Native.GetForegroundWindow() != handle)
            throw new InvalidOperationException("Window is not foreground; nothing injected. Activate it yourself.");
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 1)
        {
            hotkeyObserved = true;
            Write("WM_HOTKEY observed OK. No input generated by the hotkey handler.");
            handled = true;
        }
        return 0;
    }

    private void Write(string text) { report.AppendText(text + Environment.NewLine); report.ScrollToEnd(); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { internal ushort VirtualKey, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }

    internal static Input Key(char value, bool up) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = value, Flags = 0x4u | (up ? 0x2u : 0) } }
    };

    internal static BitmapSource CaptureWindow(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == 0 || bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
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

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
