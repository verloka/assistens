using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PersonalAgent.WindowsSmoke;

// No network, capture, hotkey registration or input on construction. START is the owner's explicit gesture.
internal sealed class LiveOpenAiWindow : Window
{
    private readonly Button start = new() { Content = "START live OpenAI read-only test (API)", Padding = new(12, 8, 12, 8) };
    private readonly Button resume = new() { Content = "RESUME with a new title and fresh capture", IsEnabled = false, Padding = new(12, 8, 12, 8) };
    private readonly Button cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly TextBlock status = new() { Text = "Waiting for START. No API or desktop access yet.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.OpenAI.Acceptance", Guid.NewGuid().ToString("N"));
    private TaskRunCoordinator? runs;
    private DesktopAccessCoordinator? desktop;
    private TakeControlHotkeyService? takeover;
    private SqliteRunRepository? repository;
    private Task? runner, initialization;
    private Guid? currentRun;
    private TaskCompletionSource paused = Signal();
    private string expectedTitle = "";
    private nint handle;
    private bool consent, closing, closed, canResume, reported;
    private int modelCalls, nativeInputs;
    private int captures;
    private long firstGeneration, lastGeneration;
    private DateTimeOffset resumeAt, lastCaptureAt;
    private bool pauseVerified;

    public LiveOpenAiWindow()
    {
        NewTitle(); Width = 930; Height = 640; MinWidth = 800; MinHeight = 600; ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "PA-059: live model -> native computer_screenshot -> result -> SQLite history", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Margin = new(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap,
            Text = "START uses the saved openai-live profile and its protected key. It permits bounded paid API requests and native captures of this visible test window only. " +
            "Other desktop pixels are masked before upload. No mouse/keyboard input, clipboard, external writes or screenshot files. " +
            "Keep this whole window visible on one monitor. Close other Personal Agent windows using Ctrl+Shift+F10 first. " +
            "After CAPTURED appears, press and release Ctrl+Shift+F10. Wait for PAUSE OK, then click RESUME. " +
            "The title changes on Resume; the model must read it from a fresh capture. Cancel/close stops the run. Synthetic history is retained in a temporary DB for review." });
        panel.Children.Add(start); panel.Children.Add(resume); panel.Children.Add(cancel); panel.Children.Add(status); panel.Children.Add(report);
        Content = panel;
        SourceInitialized += (_, _) => handle = new WindowInteropHelper(this).Handle;
        start.Click += async (_, _) => await StartAsync();
        resume.Click += async (_, _) => await ResumeAsync();
        cancel.Click += async (_, _) =>
        {
            if (currentRun is not { } id || runs is null) return;
            try { await runs.CancelAsync(new(Guid.NewGuid(), id, runs.GetRun(id).RunVersion)); }
            catch (Exception) { Write("PA059 CANCEL needs review; no operation retried."); }
        };
        refresh.Tick += (_, _) => Refresh(); refresh.Start(); Closing += CloseAsync;
    }

    private void NewTitle() { expectedTitle = "PA059 WINDOW " + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(); Title = expectedTitle; }
    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var db = new SqliteAgentDbContextFactory(Path.Combine(directory, "history.db"));
        await using (var context = db.CreateDbContext()) await context.Database.MigrateAsync(stop.Token);
        repository = new(db); desktop = new(_ => consent && Native.GetForegroundWindow() == handle);
        runs = new(repository, desktop); takeover = new(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        if (!takeover.Start()) throw new InvalidOperationException("Close another Personal Agent/diagnostic using Ctrl+Shift+F10, then restart this fixture.");
        runner = new TaskRunner(runs, new Runtime(ExecuteAsync)).RunAsync(stop.Token);
    }
    private async Task StartAsync()
    {
        start.IsEnabled = false;
        try
        {
            RequireForeground(); if (!Released()) throw new InvalidOperationException("Release keys/buttons before START.");
            initialization ??= InitializeAsync(); await initialization;
            if (closing) return;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent");
            var profiles = await new SqliteModelProfileRepository(new SqliteAgentDbContextFactory(Path.Combine(root, "history.db"))).ListAsync(stop.Token);
            var profile = profiles.SingleOrDefault(value => value.Id == "openai-live" && value.ProviderId == "openai")
                ?? throw new InvalidOperationException("Save an openai-live OpenAI profile in Models first.");
            consent = true; reported = canResume = pauseVerified = false; paused = Signal(); captures = modelCalls = nativeInputs = 0;
            NewTitle();
            var run = await runs!.SubmitAsync(new(Guid.NewGuid(),
                "Call computer_screenshot to read the exact OS title bar of the authorized test window. Return that exact title text as your final answer. " +
                "The screenshot tool may wait for an attended pause and Resume; use the fresh returned image, never guess the title. No input or other tool is allowed.",
                new("attended-openai-diagnostic"), ModelProfileId: profile.Id, AllowedToolIds: ["computer_screenshot"],
                Limits: new() { MaxSteps = 3, MaxTokens = 12_000, MaxDuration = TimeSpan.FromMinutes(3) }, Profile: profile));
            currentRun = run.Id;
            Write("PA059 START; saved provider/model selected; capture is restricted to this own window; no input. Wait for CAPTURED, then press Ctrl+Shift+F10.");
        }
        catch (Exception error) { consent = false; Write("PA059 BLOCKED: " + (error is InvalidOperationException ? error.Message : "Setup unavailable; no action retried.")); }
    }
    private async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
    {
        var capture = new OwnWindowCapture(this);
        var input = new NoInput(this);
        await using var session = new ComputerToolSession(request.RunId, desktop!, grant =>
            new WindowsComputerController(desktop!, grant, capture, input, capture.GetMonitors, _ => false));
        var source = session.Tools.Single(tool => tool.Descriptor.Id == "computer_screenshot");
        var tool = new AttendedScreenshot(this, source, capture);
        var registry = new ToolRegistry([tool], runs!.WaitAtBoundaryAsync, (descriptor, context, _) =>
            ValueTask.FromResult(consent && context.RunId == request.RunId && descriptor.Id == "computer_screenshot" && Native.GetForegroundWindow() == handle));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent", "secrets");
        var provider = new CountedProvider(new OpenAiModelProvider(new WindowsDpapiSecretStore(root)), this);
        var runtime = new MafAgentRuntime(provider, request.Profile!, registry, runs.WaitAtBoundaryAsync, session.ConfirmModelObservation);
        var result = await runtime.ExecuteAsync(request, progress, ct);
        var title = await Dispatcher.InvokeAsync(() => expectedTitle);
        if (result.Status == AgentResultStatus.Completed && (!pauseVerified || !result.Summary.Contains(title, StringComparison.Ordinal)
            || captures < 2 || lastGeneration <= firstGeneration || lastCaptureAt < resumeAt || nativeInputs != 0))
            return result with { Status = AgentResultStatus.Blocked, ErrorCode = "LiveTitleNotConfirmed", Summary = "The live title/fresh-capture/takeover acceptance was not confirmed; no retry performed." };
        return result;
    }
    private async Task ResumeAsync()
    {
        if (!canResume || currentRun is not { } id || runs is null) return;
        try
        {
            RequireForeground(); if (!Released()) throw new InvalidOperationException();
            canResume = false; resume.IsEnabled = false; NewTitle();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(100, stop.Token);
            resumeAt = DateTimeOffset.UtcNow;
            await runs.ResumeAsync(new(Guid.NewGuid(), id, runs.GetRun(id).RunVersion));
            Write("PA059 RESUME; title changed; pending image discarded; fresh native capture required.");
        }
        catch (Exception) { Write("PA059 RESUME BLOCKED; no API or capture retried."); }
    }
    private async void Refresh()
    {
        if (closing || runs is null || currentRun is not { } id) return;
        var run = runs.GetRun(id);
        cancel.IsEnabled = !TaskRunStateMachine.IsTerminal(run.Status);
        resume.IsEnabled = canResume && takeover!.Handoff.Confirmed;
        if (AttendedPause.Acknowledged(run, takeover!.Handoff.Confirmed)) paused.TrySetResult();
        if (!reported && run.Status is RunStatus.Blocked or RunStatus.Failed or RunStatus.Cancelled)
        {
            reported = true; consent = false;
            try
            {
                var failed = await repository!.GetAsync(id);
                Write($"PA059 {run.Status}; code={failed?.Result?.ErrorCode ?? "Unavailable"}; no success or retry claimed. Close/restart for another explicit attempt.");
            }
            catch (Exception) { Write("PA059 result could not be loaded; no success claimed."); }
            return;
        }
        if (reported || run.Status != RunStatus.Completed) return;
        reported = true; consent = false;
        try
        {
            // New repository/context rereads the durable run, not the coordinator's cached snapshot.
            var stored = await new SqliteRunRepository(new SqliteAgentDbContextFactory(Path.Combine(directory, "history.db"))).GetAsync(id);
            var history = stored?.Result?.Summary.Contains(expectedTitle, StringComparison.Ordinal) == true && stored.Run.Status == RunStatus.Completed
                && stored.Events.Any(value => value.EventType == RunEventType.DesktopPauseConfirmed);
            Write($"PA059 LIVE {(history && pauseVerified ? "OK" : "FAILED")}; titleMatched={history}; modelCalls={modelCalls}; captures={captures}; " +
                $"generation={firstGeneration}->{lastGeneration}; hotkeyPause={pauseVerified}; freshAfterResume={lastCaptureAt >= resumeAt}; durableHistory={history}; nativeInput={nativeInputs}.");
            Write("Synthetic history DB: " + Path.Combine(directory, "history.db"));
        }
        catch (Exception) { Write("PA059 HISTORY FAILED; no success claimed."); }
    }
    private void Write(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Write(text))); return; }
        status.Text = text; report.AppendText(text + Environment.NewLine); report.ScrollToEnd();
    }
    private void RequireForeground() { if (!IsActive || Native.GetForegroundWindow() != handle) throw new InvalidOperationException("Activate and keep this test window visible."); }
    private static bool Released() => new[] { 1, 2, 4, 16, 17, 18 }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async void CloseAsync(object? sender, CancelEventArgs args)
    {
        if (closed) return;
        args.Cancel = true; if (closing) return; closing = true; consent = false; refresh.Stop(); stop.Cancel(); takeover?.Dispose();
        try
        {
            if (initialization is not null) { try { await initialization; } catch { } }
            takeover?.Dispose();
            if (runner is not null) await runner;
            runs?.Dispose();
        }
        catch (Exception) { }
        finally { stop.Dispose(); closed = true; Close(); Application.Current.Shutdown(); }
    }

    private sealed class AttendedScreenshot(LiveOpenAiWindow window, IAgentTool inner, OwnWindowCapture capture) : IAgentTool
    {
        private bool held;
        public ToolDescriptor Descriptor { get; } = inner.Descriptor with
        {
            Description = "Capture only this authorized test window using native pixels; other monitor pixels are masked. May wait for attended pause and Resume. No input.",
            JsonSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false })
        };
        public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
        {
            var selected = new ToolArguments(JsonSerializer.SerializeToElement(new { area = "Monitor", monitorId = capture.OwnMonitor().Id }));
            var result = await inner.ExecuteAsync(selected, context, ct);
            if (held || result.Status != ToolResultStatus.Success) return result;
            held = true; window.firstGeneration = window.desktop!.Snapshot.Generation;
            window.Write("PA059 CAPTURED. Press and release Ctrl+Shift+F10 now. No image is sent to the model until explicit Resume/fresh capture.");
            await window.paused.Task.WaitAsync(ct);
            await window.takeover!.Completion.WaitAsync(ct);
            var beforeCalls = window.modelCalls; var beforeCaptures = window.captures;
            await Task.Delay(350, ct);
            window.pauseVerified = window.takeover.Handoff.Confirmed && beforeCalls == window.modelCalls && beforeCaptures == window.captures && window.nativeInputs == 0;
            if (!window.pauseVerified) return new() { Status = ToolResultStatus.Blocked, ErrorCode = "LivePauseUnconfirmed", Text = "Attended pause was not confirmed." };
            await AttendedPause.WaitForResumeAsync(window.runs!, context.RunId, () => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                window.canResume = true;
                window.Write("PA059 PAUSE OK; API/capture counts stable, nativeInput=0. Click RESUME for a new title and fresh image.");
            })), ct);
            selected = new(JsonSerializer.SerializeToElement(new { area = "Monitor", monitorId = capture.OwnMonitor().Id }));
            return await inner.ExecuteAsync(selected, context, ct); // Fresh read only; the held pre-pause image is discarded.
        }
    }
    private sealed class OwnWindowCapture(LiveOpenAiWindow window) : IWindowsCaptureNative
    {
        private readonly WindowsCaptureNative native = new();
        public bool IsDesktopAvailable() => window.consent && Native.GetForegroundWindow() == window.handle && native.IsDesktopAvailable();
        public IReadOnlyList<ScreenMonitor> GetMonitors() => native.GetMonitors();
        private ScreenBounds Bounds()
        {
            if (!IsDesktopAvailable() || Native.DwmGetWindowAttribute(window.handle, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0)
                throw new ScreenCaptureException("DesktopUnavailable", "Keep this whole test window visible.");
            return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        public ScreenMonitor OwnMonitor()
        {
            var bounds = Bounds();
            return GetMonitors().FirstOrDefault(monitor => monitor.Bounds.Contains(new(bounds.X, bounds.Y))
                && monitor.Bounds.Contains(new(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1)))
                ?? throw new ScreenCaptureException("DesktopUnavailable", "Keep this whole test window on one monitor.");
        }
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
            => window.Dispatcher.Invoke(() => CaptureOwnPixels(bounds, monitors));
        private BitmapSource CaptureOwnPixels(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        {
            var own = Bounds();
            if (!bounds.Contains(new(own.X, own.Y)) || !bounds.Contains(new(own.X + own.Width - 1, own.Y + own.Height - 1)))
                throw new ScreenCaptureException("DesktopUnavailable", "The own-window capture scope changed.");
            // Shared production GDI helper black-fills the monitor canvas and copies ONLY this own rectangle.
            // No other monitor/window pixels are read and then redacted afterwards.
            var masked = GdiScreenCapture.Capture(bounds, [own]);
            if (!IsDesktopAvailable()) throw new ScreenCaptureException("DesktopUnavailable", "The test window lost foreground.");
            Interlocked.Increment(ref window.captures);
            window.lastGeneration = window.desktop!.Snapshot.Generation; window.lastCaptureAt = DateTimeOffset.UtcNow;
            return masked;
        }
    }
    private sealed class NoInput(LiveOpenAiWindow window) : IWindowsKeyboardNative
    {
        public bool IsDesktopAvailable() => false;
        public bool IsKeyDown(ushort key) => false;
        public InputInsertion Send(NativeInput[] inputs) { Interlocked.Increment(ref window.nativeInputs); return new(0, 5); }
    }
    private sealed class CountedProvider(IModelProvider inner, LiveOpenAiWindow window) : IModelProvider
    {
        public string ProviderId => inner.ProviderId;
        public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        { var opened = await inner.OpenAsync(profile, ct); return new(new CountedClient(opened.Client, window), profile); }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct) => inner.TestConnectionAsync(profile, ct);
    }
    private sealed class CountedClient(IChatClient inner, LiveOpenAiWindow window) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref window.modelCalls);
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) yield return update;
        }
    }
    private sealed class Runtime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    { public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, progress, ct); }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint handle, int attribute, out Rect rect, int size);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    }
}
