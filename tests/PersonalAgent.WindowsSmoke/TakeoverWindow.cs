using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PersonalAgent.WindowsSmoke;

// Attended only: launch creates controls, not input/capture/hotkey registration.
internal sealed class TakeoverWindow : Window
{
    private readonly ComboBox mode = new() { ItemsSource = new[] { "20 clicks", "Unicode text" }, SelectedIndex = 0 };
    private readonly Button start = new() { Content = "START attended test", Padding = new(10, 6, 10, 6) };
    private readonly Button move = new() { Content = "MOVE TARGET while paused", IsEnabled = false, Padding = new(10, 6, 10, 6) };
    private readonly Button resume = new() { Content = "RESUME with fresh screenshot", IsEnabled = false, Padding = new(10, 6, 10, 6) };
    private readonly Button cancel = new() { Content = "Cancel", IsEnabled = false, Padding = new(10, 6, 10, 6) };
    private readonly TextBlock status = new() { Text = "Select a mode, press START, then Ctrl+Shift+F10 after input begins.", TextWrapping = TextWrapping.Wrap };
    private readonly Border target = new() { Width = 190, Height = 80, HorizontalAlignment = HorizontalAlignment.Left,
        Background = Brushes.LightSkyBlue, Child = new TextBlock { Text = "OWN TEST TARGET", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    private readonly TextBox text = new() { Height = 95, IsReadOnly = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox report = new() { Height = 180, IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.Takeover", Guid.NewGuid().ToString("N"));
    private TaskRunCoordinator? runs;
    private DesktopAccessCoordinator? desktop;
    private TakeControlHotkeyService? takeControl;
    private UserActivityPauseService? activity;
    private readonly bool activityPause;
    private SqliteRunRepository? repository;
    private Task? runnerLoop, initialization;
    private Guid? currentRun;
    private string currentMode = "";
    private bool consent, closing, closed;
    private nint handle;
    private int clickEvents;
    private DateTimeOffset resumeRequested;
    private ScreenPoint expectedMouse;
    private bool mouseOperation;

    public TakeoverWindow(bool activityPause = false)
    {
        this.activityPause = activityPause;
        Title = activityPause ? "PersonalAgent attended activity pause (PA-111)" : "PersonalAgent attended takeover (PA-048)";
        if (activityPause) status.Text = "After START keep hands off until input begins, then move the mouse inside this window to pause. No hotkey needed.";
        Width = 800; Height = 680; MinWidth = 760; MinHeight = 650; ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Real input only in this test window. Keep it visible and active. No screenshot files/uploads or LLM.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(mode);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        foreach (var button in new[] { start, move, resume, cancel }) buttons.Children.Add(button);
        panel.Children.Add(buttons); panel.Children.Add(status);
        panel.Children.Add(new Grid { Height = 110, Children = { target } });
        panel.Children.Add(text); panel.Children.Add(report); Content = panel;
        SourceInitialized += (_, _) => handle = new WindowInteropHelper(this).Handle;
        target.PreviewMouseDown += (_, e) => { e.Handled = true; };
        target.PreviewMouseUp += (_, e) => { if (e.ChangedButton == System.Windows.Input.MouseButton.Left) clickEvents++; e.Handled = true; };
        start.Click += async (_, _) => await StartAsync();
        move.Click += (_, _) => { target.HorizontalAlignment = target.HorizontalAlignment == HorizontalAlignment.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left; UpdateLayout(); };
        resume.Click += async (_, _) => await ResumeAsync();
        cancel.Click += async (_, _) =>
        {
            if (runs is null || currentRun is not { } id) return;
            try { var run = runs.GetRun(id); await runs.CancelAsync(new(Guid.NewGuid(), id, run.RunVersion)); }
            catch (Exception) { Write("PA048 CANCEL needs review; no input retried."); }
        };
        refresh.Tick += (_, _) => Refresh(); refresh.Start(); Closing += CloseAsync;
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var factory = new SqliteAgentDbContextFactory(Path.Combine(directory, "history.db"));
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(stop.Token);
        if (closing) return;
        repository = new(factory);
        desktop = new(_ => consent && Native.GetForegroundWindow() == handle, beforeAcquire: (_, _) =>
        { activity?.RequireAvailable(); return Task.CompletedTask; });
        runs = new(repository, desktop);
        takeControl = new(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        if (!takeControl.Start()) throw new InvalidOperationException("Ctrl+Shift+F10 is unavailable. Close another diagnostic/App using it first.");
        if (activityPause)
        {
            activity = new(desktop, takeControl, NullLogger<UserActivityPauseService>.Instance);
            if (!await activity.StartAsync(stop.Token)) throw new InvalidOperationException("Input observation is unavailable; no GUI test started.");
        }
        runnerLoop = new TaskRunner(runs, new Runtime(ExecuteAsync)).RunAsync(stop.Token);
    }

    private async Task StartAsync()
    {
        start.IsEnabled = false; mode.IsEnabled = false;
        try
        {
            RequireForeground();
            if (!Released()) throw new InvalidOperationException("Release keys/buttons before START.");
            initialization ??= InitializeAsync(); await initialization;
            if (closing) return;
            if (currentRun is { } old && !TaskRunStateMachine.IsTerminal(runs!.GetRun(old).Status)) return;
            consent = true; clickEvents = 0; text.Text = ""; text.IsReadOnly = false;
            currentMode = (string)mode.SelectedItem;
            var run = await runs!.SubmitAsync(new(Guid.NewGuid(), $"PA048 {currentMode}", new("attended-diagnostic")));
            currentRun = run.Id;
            Write($"PA048 START {currentMode}. Wait for input, then {(activityPause ? "move the mouse inside this window (no hotkey)" : "press and release Ctrl+Shift+F10")}. Keep this window active. Synthetic checkpoint DB is temporary; screenshots stay in memory.");
        }
        catch (Exception error) { Write($"PA048 BLOCKED: {error.Message}"); start.IsEnabled = !closing; mode.IsEnabled = !closing; }
    }

    private async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
    {
        DesktopLease? lease = null;
        var input = new GuardedInput(this);
        var capture = new GuardedCapture(this);
        WindowsComputerController? controller = null;
        ScreenCapture? frame = null;
        var sample = string.Concat(Enumerable.Repeat("Тест 🙂 你好 ", 250));
        try
        {
            for (var second = 3; second > 0; second--)
            { await Dispatcher.InvokeAsync(() => status.Text = $"Input starts in {second}s. Then {(activityPause ? "move the mouse to pause; no hotkey" : "press Ctrl+Shift+F10")}."); await Task.Delay(1000, ct); }
            lease = await desktop!.AcquireAsync(request.RunId, ct);
            controller = new(desktop, lease, capture, input, capture.GetMonitors, chord => chord == "Ctrl+A");
            frame = await controller.ScreenshotAsync(new(ScreenshotArea.VirtualDesktop), ct);
            controller.ConfirmObservation(frame); // Explicit attended test chooses only its own known target.
            try
            {
                if (currentMode == "20 clicks")
                {
                    for (var step = 0; step < 20; step++)
                    {
                        var point = await PrepareMouseAsync();
                        await controller.ClickAsync(point, MouseButton.Left, ct);
                        progress.Report(new() { RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.Progress, Message = $"Own click {step + 1}/20" });
                        await Task.Delay(300, ct);
                    }
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => { RequireForeground(); mouseOperation = false; text.Focus(); });
                    await controller.TypeAsync(sample, ct);
                }
                Write("PA048 NO PAUSE: sequence finished. Repeat START and press Ctrl+Shift+F10 sooner.");
                return new() { Status = AgentResultStatus.Completed, Summary = "Attended sequence finished without takeover; acceptance not proven." };
            }
            catch (Exception error) when (error is ComputerInputException or ScreenCaptureException)
            {
                // A held Ctrl/Shift can reject a chunk just before F10 produces WM_HOTKEY.
                // Only wait for that local gate; never retry the rejected input or invoke a model.
                for (var wait = 0; wait < 20 && desktop.IsCurrent(lease); wait++) await Task.Delay(25, ct);
                if (desktop.IsCurrent(lease)) throw;
            }

            await takeControl!.Completion.WaitAsync(ct);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var before = input.Calls;
            await Task.Delay(350, ct);
            for (var wait = 0; wait < 20 && !Released(); wait++) await Task.Delay(100, ct);
            var actual = await Dispatcher.InvokeAsync(() => text.Text);
            var validUnicode = true;
            try { new UnicodeEncoding(false, false, true).GetByteCount(actual); }
            catch (EncoderFallbackException) { validUnicode = false; }
            var stored = (await repository!.GetAsync(request.RunId, ct))!;
            var stoppedPartway = currentMode == "20 clicks" ? clickEvents is > 0 and < 20 && clickEvents == before :
                actual.Length > 0 && actual.Length < sample.Length && actual.Length == input.UnicodeUnits && sample.StartsWith(actual, StringComparison.Ordinal);
            var pauseOk = stoppedPartway && validUnicode && Released() && before == input.Calls && takeControl.Handoff.Confirmed &&
                stored.Events.Any(e => e.EventType == RunEventType.DesktopPauseConfirmed);
            var autoReason = stored.Events.Any(e => e.Message == nameof(PauseReason.AutoPause));
            if (activityPause) pauseOk &= autoReason && activity!.IgnoredOwnEvents > 0;
            Write($"PA048 PAUSE {(pauseOk ? "OK" : "FAILED")}; mode={currentMode}; nativeCalls={before}->{input.Calls}; clickEvents={clickEvents}; chars={actual.Length}/{sample.Length}; modifiersReleased={Released()}; validUnicode={validUnicode}; checkpoint={takeControl.Handoff.Confirmed}; AutoPause={autoReason}; ownEvents={activity?.IgnoredOwnEvents ?? 0}. MOVE TARGET, then RESUME and keep hands off.");
            await runs!.WaitAtBoundaryAsync(request.RunId, new RunStepAccess(true, false, false), ct);

            // Resume discards the unfinished macro. Only deliberate fresh-target click/chord follows.
            var nativeBefore = input.Calls;
            var staleDenied = false;
            var safeProbePoint = await PrepareMouseAsync();
            try { await controller.ClickAsync(safeProbePoint, MouseButton.Left, ct); }
            catch (ComputerInputException error) when (error.Code == "StaleDesktopContext") { staleDenied = input.Calls == nativeBefore; }
            var oldGeneration = lease.Generation;
            lease = await desktop.AcquireAsync(request.RunId, ct);
            var fresh = new WindowsComputerController(desktop, lease, capture, input, capture.GetMonitors, chord => chord == "Ctrl+A");
            var foreignDenied = false;
            try { fresh.ConfirmObservation(frame); }
            catch (ComputerInputException) { foreignDenied = true; }
            var capturesBefore = capture.Calls;
            var freshFrame = await fresh.ScreenshotAsync(new(ScreenshotArea.VirtualDesktop), ct);
            var pointAfter = await PrepareMouseAsync(); fresh.ConfirmObservation(freshFrame);
            var clicksBefore = clickEvents;
            await fresh.ClickAsync(pointAfter, MouseButton.Left, ct);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Dispatcher.InvokeAsync(() => { RequireForeground(); mouseOperation = false; text.IsReadOnly = true; if (text.Text.Length == 0) text.Text = "Fresh context chord"; text.Select(0, 0); text.Focus(); });
            await fresh.PressKeyAsync(new("Ctrl+A"), ct);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var selected = await Dispatcher.InvokeAsync(() => text.SelectionLength == text.Text.Length && text.Text.Length > 0);
            var ok = pauseOk && staleDenied && foreignDenied && freshFrame.DesktopGeneration > oldGeneration &&
                freshFrame.TimestampUtc >= resumeRequested && capture.Calls == capturesBefore + 1 && clickEvents == clicksBefore + 1 && selected && Released();
            Write($"PA048 RESUME {(ok ? "OK" : "FAILED")}; mode={currentMode}; oldDenied={staleDenied}; foreignFrameDenied={foreignDenied}; generation={oldGeneration}->{lease.Generation}; freshCapture={capture.Calls == capturesBefore + 1}; freshClick={clickEvents == clicksBefore + 1}; CtrlA={selected}; modifiersReleased={Released()}; old sequence discarded; no screenshot files/uploads/LLM.");
            return new() { Status = ok ? AgentResultStatus.Completed : AgentResultStatus.Blocked, Summary = ok ? "Attended native takeover/resume passed." : "Attended takeover/resume needs review." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Write($"PA048 BLOCKED: {error.GetType().Name}; no input retried.");
            return new() { Status = AgentResultStatus.Blocked, Summary = "Attended test blocked; no retry." };
        }
        finally { if (lease is not null) await desktop!.ReleaseAsync(lease); }
    }

    private async Task<ScreenPoint> PrepareMouseAsync() => await Dispatcher.InvokeAsync(() =>
    {
        RequireForeground(); UpdateLayout(); mouseOperation = true;
        var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        expectedMouse = new((int)point.X, (int)point.Y); return expectedMouse;
    });
    private bool InputTargetValid() => Dispatcher.Invoke(() =>
    {
        if (closing || !consent || Native.GetForegroundWindow() != handle) return false;
        if (!mouseOperation) return text.IsKeyboardFocused;
        var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        return expectedMouse == new ScreenPoint((int)point.X, (int)point.Y) && Native.WindowFromPoint(new(expectedMouse.X, expectedMouse.Y)) == handle;
    });
    private void RequireForeground()
    { if (!IsActive || Native.GetForegroundWindow() != handle) throw new InvalidOperationException("Activate and keep this test window visible first."); }
    private static bool Released() => new[] { 1, 2, 4, 16, 17, 18 }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0);
    private async Task ResumeAsync()
    {
        if (currentRun is not { } id || runs is null) return;
        try
        {
            RequireForeground(); if (!Released()) throw new InvalidOperationException("Release keys/buttons first.");
            resumeRequested = DateTimeOffset.UtcNow;
            var run = runs.GetRun(id); await runs.ResumeAsync(new(Guid.NewGuid(), id, run.RunVersion));
        }
        catch (Exception error) { Write($"PA048 RESUME BLOCKED: {error.GetType().Name}; no input retried."); }
    }
    private void Refresh()
    {
        if (currentRun is not { } id || runs is null || closing) return;
        var run = runs.GetRun(id);
        var paused = run.Status is RunStatus.Paused or RunStatus.PausedForUser;
        resume.IsEnabled = move.IsEnabled = paused && takeControl!.Handoff.Confirmed;
        cancel.IsEnabled = !TaskRunStateMachine.IsTerminal(run.Status);
        start.IsEnabled = mode.IsEnabled = TaskRunStateMachine.IsTerminal(run.Status);
        if (paused) status.Text = "Paused. Move the target (or move this window), then click RESUME. No old sequence will be replayed.";
    }
    private void Write(string value)
    { if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Write(value))); return; } report.AppendText((activityPause ? value.Replace("PA048", "PA111", StringComparison.Ordinal) : value) + Environment.NewLine); report.ScrollToEnd(); }
    private async void CloseAsync(object? sender, CancelEventArgs args)
    {
        if (closed) return;
        args.Cancel = true; if (closing) return; closing = true; consent = false; refresh.Stop();
        takeControl?.Dispose(); stop.Cancel();
        try
        {
            if (activity is not null) await activity.DisposeAsync();
            if (initialization is not null) { try { await initialization; } catch { } }
            takeControl?.Dispose();
            if (runnerLoop is not null) await runnerLoop;
            runs?.Dispose(); stop.Dispose();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalAgent.Takeover")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception) { /* No input retry or broad filesystem cleanup on shutdown failure. */ }
        finally { closed = true; Close(); Application.Current.Shutdown(); }
    }

    private sealed class Runtime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    { public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken) => execute(request, progress, cancellationToken); }
    private sealed class GuardedCapture(TakeoverWindow window) : IWindowsCaptureNative
    {
        private readonly WindowsCaptureNative native = new();
        public int Calls;
        public bool IsDesktopAvailable() => native.IsDesktopAvailable() && Native.GetForegroundWindow() == window.handle;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => native.GetMonitors();
        public System.Windows.Media.Imaging.BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        { Calls++; return native.CaptureBitmap(bounds, monitors); }
    }
    private sealed class GuardedInput(TakeoverWindow window) : IWindowsKeyboardNative
    {
        private readonly WindowsInputNative native = new();
        public int Calls, UnicodeUnits;
        public bool IsDesktopAvailable() => native.IsDesktopAvailable() && window.InputTargetValid();
        public bool IsKeyDown(ushort key) => native.IsKeyDown(key);
        public InputInsertion Send(NativeInput[] inputs)
        {
            Thread.Sleep(80); // Fixture pacing only: keep Unicode chunks observable for human hotkey interruption.
            if (!window.InputTargetValid()) return new(0, 5);
            if (inputs.Any(input => input.Type == 1 && (input.Data.Keyboard.Flags & 4) != 0) &&
                new[] { 16, 17, 18 }.Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0)) return new(0, 0);
            Interlocked.Increment(ref Calls);
            var result = native.Send(inputs);
            if (result.Count == inputs.Length)
                Interlocked.Add(ref UnicodeUnits, inputs.Count(input => input.Type == 1 && (input.Data.Keyboard.Flags & 6) == 4));
            return result;
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public readonly record struct PhysicalPoint(int X, int Y);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(PhysicalPoint point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    }
}
