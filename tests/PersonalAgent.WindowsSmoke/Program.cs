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
    private readonly Button clickTest = new() { Content = "CLICK selected button in TEST TARGET (moves pointer once)", Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox clickButton = new() { ItemsSource = Enum.GetValues<MouseButton>(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Border clickTarget = new() { Height = 48, Background = System.Windows.Media.Brushes.LightSkyBlue,
        Child = new TextBlock { Text = "TEST TARGET: no external effect", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    private readonly List<(System.Windows.Input.MouseButton Button, bool Up)> observedClicks = [];
    private nint handle;
    private bool registered;
    private bool hotkeyObserved;
    private int primaryConsent;
    private long primaryGeneration;
    private CancellationTokenSource? primaryCancellation;
    private CancellationTokenSource? clickCancellation;
    private bool typingDiagnostic;

    public SmokeWindow()
    {
        Title = "PersonalAgent attended Windows diagnostic (PA-008/025/026/032)";
        Width = 680;
        Height = 660;
        ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "The first button tests this window's crop, one x and the hotkey (PA-008); release all keys first. " +
                   "PRIMARY, VIRTUAL and SECONDARY capture local PNG previews and type nothing (PA-025/026). " +
                   "Activate this window and press the desired button yourself. No files, uploads, clipboard or background input. " +
                   "CLICK moves the pointer once to TEST TARGET; choose Left/Right/Middle and do not move or switch apps during the check. " +
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
        panel.Children.Add(clickButton);
        panel.Children.Add(clickTest);
        panel.Children.Add(clickTarget);
        clickTest.Click += async (_, _) => await RunClickAsync();
        clickTarget.PreviewMouseDown += (_, args) => { observedClicks.Add((args.ChangedButton, false)); args.Handled = true; };
        clickTarget.PreviewMouseUp += (_, args) => { observedClicks.Add((args.ChangedButton, true)); args.Handled = true; };
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
            clickCancellation?.Cancel();
            if (registered) Native.UnregisterHotKey(handle, 1);
            HwndSource.FromHwnd(handle)?.RemoveHook(OnMessage);
        };
    }

    private async Task CaptureScreenAsync(ScreenshotArea area)
    {
        var label = area switch { ScreenshotArea.PrimaryMonitor => "PRIMARY", ScreenshotArea.VirtualDesktop => "VIRTUAL", _ => "MONITOR" };
        // A second click must not replace/revoke an in-flight capture's consent or token.
        if (primaryCancellation is not null || clickCancellation is not null || typingDiagnostic) { Write("Wait for the current diagnostic."); return; }
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

    private async Task RunClickAsync()
    {
        if (clickCancellation is not null || primaryCancellation is not null || typingDiagnostic) { Write("Wait for the current diagnostic."); return; }
        clickTest.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        clickCancellation = cancellation;
        try
        {
            RequireForeground();
            var selected = (MouseButton)clickButton.SelectedItem;
            var center = new Point(clickTarget.ActualWidth / 2, clickTarget.ActualHeight / 2);
            var physical = clickTarget.PointToScreen(center);
            var point = new ScreenPoint((int)Math.Round(physical.X), (int)Math.Round(physical.Y));
            bool Allowed() => IsActive && Native.GetForegroundWindow() == handle && clickTarget.IsVisible &&
                clickTarget.PointToScreen(center) == physical && Native.WindowFromPoint(new(point.X, point.Y)) == handle &&
                new[] { 1, 2, 4, 16, 17, 18, 91, 92 }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0);
            var generation = Interlocked.Increment(ref primaryGeneration);
            (bool, long) Authorize() => Dispatcher.Invoke(() => (Allowed(), generation));
            var frame = await new WindowsScreenCapture(Authorize).CaptureAsync(new(ScreenshotArea.VirtualDesktop), cancellation.Token);
            observedClicks.Clear();
            await new WindowsMouseInput(Authorize).ClickAsync(frame, point, selected, cancellation.Token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var expected = selected switch { MouseButton.Left => System.Windows.Input.MouseButton.Left,
                MouseButton.Right => System.Windows.Input.MouseButton.Right, _ => System.Windows.Input.MouseButton.Middle };
            var correct = observedClicks.SequenceEqual(new[] { (expected, false), (expected, true) });
            Write($"CLICK {selected} {(correct ? "OK" : "FAILED")}; physical={point}; generation={generation}; observed={observedClicks.Count}; no retry/files/uploads.");
        }
        catch (Exception exception) { Write($"CLICK BLOCKED: {exception.Message}"); }
        finally
        {
            clickCancellation = null;
            clickTest.IsEnabled = true;
        }
    }

    private async void RunSmoke(object sender, RoutedEventArgs args)
    {
        if (clickCancellation is not null || primaryCancellation is not null || typingDiagnostic) { Write("Wait for the current diagnostic."); return; }
        typingDiagnostic = true;
        run.IsEnabled = false;
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
            // The authorization callback uses Dispatcher-bound state; invoke the atomic sender off UI
            // with a trusted callback that marshals only the brief policy check back to this window.
            var inputSender = new WindowsInputSender(() => Dispatcher.Invoke(() =>
                (IsActive && Native.GetForegroundWindow() == handle && target.IsKeyboardFocused, 0L)));
            await inputSender.SendAsync([NativeInput.Unicode('x', false), NativeInput.Unicode('x', true)], 0);
            await Dispatcher.InvokeAsync(new Action(() =>
            {
                target.IsReadOnly = true;
                Write(target.Text == "x" ? "SendInput observed OK: one x in test field." : "SendInput observation FAILED; do not claim passed.");
                Write(hotkeyObserved ? "Hotkey already observed." : "Now press Ctrl+Shift+F10 manually, then close the window.");
            }), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception exception)
        {
            target.IsReadOnly = true;
            Write($"BLOCKED: {exception.Message}");
        }
        finally { typingDiagnostic = false; }
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
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }

    internal static BitmapSource CaptureWindow(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return GdiScreenCapture.Capture(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
}
