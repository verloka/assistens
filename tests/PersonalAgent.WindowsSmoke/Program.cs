using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using System.IO;

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
    private int primaryConsent;
    private long primaryGeneration;
    private CancellationTokenSource? primaryCancellation;

    public SmokeWindow()
    {
        Title = "PersonalAgent attended Windows diagnostic (PA-008/025/026)";
        Width = 680;
        Height = 660;
        ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "The first button tests this window's crop, one x and the hotkey (PA-008); release all keys first. " +
                   "PRIMARY, VIRTUAL and SECONDARY capture local PNG previews and type nothing (PA-025/026). " +
                   "Activate this window and press the desired button yourself. No files, uploads, clipboard or background input. " +
                   "Ctrl+Shift+F10 applies only after the first button. Close the window when finished.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(run);
        var primary = new Button { Content = "Capture PRIMARY monitor (PNG preview only; no typing)", Margin = new Thickness(0, 0, 0, 8) };
        var virtualDesktop = new Button { Content = "Capture VIRTUAL desktop (all monitors; no typing)", Margin = new Thickness(0, 0, 0, 8) };
        var secondary = new Button { Content = "Capture SECONDARY monitor (first non-primary; no typing)", Margin = new Thickness(0, 0, 0, 8) };
        primary.Click += async (_, _) => await CaptureScreenAsync(ScreenshotArea.PrimaryMonitor);
        virtualDesktop.Click += async (_, _) => await CaptureScreenAsync(ScreenshotArea.VirtualDesktop);
        secondary.Click += async (_, _) => await CaptureScreenAsync(ScreenshotArea.Monitor);
        panel.Children.Add(primary);
        panel.Children.Add(virtualDesktop);
        panel.Children.Add(secondary);
        panel.Children.Add(new TextBlock { Text = "CAPTURE CHECK: this blue bar must appear on the display containing this window", Background = System.Windows.Media.Brushes.DodgerBlue, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(target);
        panel.Children.Add(report);
        panel.Children.Add(preview);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        run.Click += RunSmoke;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(OnMessage);
        };
        Closed += (_, _) =>
        {
            Volatile.Write(ref primaryConsent, 0);
            primaryCancellation?.Cancel();
            if (registered) Native.UnregisterHotKey(handle, 1);
            HwndSource.FromHwnd(handle)?.RemoveHook(OnMessage);
        };
    }

    private async Task CaptureScreenAsync(ScreenshotArea area)
    {
        var label = area switch { ScreenshotArea.PrimaryMonitor => "PRIMARY", ScreenshotArea.VirtualDesktop => "VIRTUAL", _ => "MONITOR" };
        // A second click must not replace/revoke an in-flight capture's consent or token.
        if (primaryCancellation is not null) { Write("Capture already in progress; wait for its result."); return; }
        try
        {
            RequireForeground();
            Interlocked.Increment(ref primaryGeneration);
            Volatile.Write(ref primaryConsent, 1);
            primaryCancellation = new CancellationTokenSource();
            string? monitorId = null;
            if (area == ScreenshotArea.Monitor)
            {
                var native = new WindowsCaptureNative();
                if (!native.IsDesktopAvailable()) throw new ScreenCaptureException("DesktopUnavailable", "An active Default desktop is required.");
                monitorId = native.GetMonitors().FirstOrDefault(monitor => !monitor.IsPrimary)?.Id
                    ?? throw new ScreenCaptureException("MonitorUnavailable", "Connect a second display in Extend mode first.");
            }
            var capture = new WindowsScreenCapture(() =>
                (Volatile.Read(ref primaryConsent) == 1 && Native.GetForegroundWindow() == handle, Interlocked.Read(ref primaryGeneration)));
            var frame = await capture.CaptureAsync(new(area, monitorId), primaryCancellation.Token);
            using var stream = new MemoryStream(frame.ImageBytes, writable: false);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze();
            preview.Source = image;
            Write($"{label} OK; PNG {frame.ImageWidth}x{frame.ImageHeight}; source={frame.SourceBounds}; " +
                  $"DPI={frame.Monitors[0].DpiX}; monitors={Native.GetSystemMetrics(80)}; generation={frame.DesktopGeneration}; bytes={frame.ImageBytes.Length}. Preview must match selected area. Nothing saved/uploaded.");
            foreach (var monitor in frame.Monitors)
                Write($"DISPLAY {monitor.Id}; bounds={monitor.Bounds}; DPI={monitor.DpiX}x{monitor.DpiY}; primary={monitor.IsPrimary}.");
            Write($"SYSTEM virtual=({Native.GetSystemMetrics(76)},{Native.GetSystemMetrics(77)},{Native.GetSystemMetrics(78)},{Native.GetSystemMetrics(79)}). Compare source bounds for VIRTUAL; verify preview yourself.");
        }
        catch (ScreenCaptureException exception) { Write($"{label} BLOCKED: {exception.Code}; {exception.Message}"); }
        catch (OperationCanceledException) { Write($"{label} cancelled."); }
        catch (Exception exception) { Write($"{label} failed: {exception.Message}"); }
        finally
        {
            Volatile.Write(ref primaryConsent, 0);
            primaryCancellation?.Dispose(); primaryCancellation = null;
        }
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
        return GdiScreenCapture.Capture(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
}
