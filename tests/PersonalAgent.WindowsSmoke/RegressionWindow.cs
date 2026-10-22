using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Mcp;
using PersonalAgent.Persistence;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PersonalAgent.WindowsSmoke;

// Explicitly armed native acceptance only. No registration with the task runtime/UI.
// Production tray, warning, idle, gate, MAF and MCP; own-window capture only, no injected input.
internal sealed class RegressionWindow : Window
{
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Tray + window switching", "Scheduled busy keyboard", "MCP then GUI" }, SelectedIndex = 0 };
    private readonly Button start = new() { Content = "START selected PA-115 check", Margin = new(0, 8, 0, 8) };
    private readonly Button check = new() { Content = "CHECK paused / busy", IsEnabled = false, Margin = new(0, 8, 0, 8) };
    private readonly Button resume = new() { Content = "RESUME / ALLOW own-window capture", IsEnabled = false };
    private readonly TextBox typing = new() { AcceptsReturn = true, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, Height = 245, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Lifetime lifetime = new();
    private readonly TaskCompletionSource allowResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.Regression", Guid.NewGuid().ToString("N"));
    private readonly WindowsSystemIdleSource idle = new();
    private readonly Action shutdown;
    private TaskRunCoordinator? runs;
    private DesktopAccessCoordinator? desktop;
    private TakeControlHotkeyService? takeover;
    private SqliteRunRepository? repository;
    private SqliteScheduleRepository? schedules;
    private ScheduleDispatcher? scheduler;
    private ScheduledDesktopGate? gate;
    private ControlPanelViewModel? controls;
    private WindowsTrayIcon? tray;
    private ScheduledDesktopWarningWindow? warningWindow;
    private ScheduledDesktopWarningViewModel? warningModel;
    private Guid? warningId, runId;
    private Task? starting, checking, runner, tickingTask;
    private WebApplication? server;
    private McpClientConnection? connection;
    private McpAgentTool? mcp;
    private nint handle;
    private int selected, captures, mcpCalls, warnings, busySamples, typedCharacters;
    private int capturesAtPause;
    private bool ticking, closing, closed, checkedPause, decisionTaken;
    private volatile bool armed, paused, nativeViolation;
    private long firstGeneration;
    private string busyReport = "";
    private string executionStage = "not-started";
    private DesktopLease? acquiredLease;

    internal bool HasResources => runs is not null || tray is not null || server is not null;
    internal bool IsArmed => armed;
    internal bool IsStopping => lifetime.ApplicationStopping.IsCancellationRequested;

    public RegressionWindow(Action? shutdown = null)
    {
        this.shutdown = shutdown ?? (() => Application.Current?.Shutdown());
        Title = "PersonalAgent PA-115 native regression"; Width = 880; Height = 650; ShowActivated = false;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "Select one check, START, then follow the report. No API keys, paid model, corporate service, injected input, clipboard or screenshot files. " +
            "The local scripted model can request one read-only own-window screenshot after permission. Type only synthetic text here; it is never logged. " +
            "One check per launch; close after the result. Do not use tray Resume: use this window's explicit RESUME button." });
        foreach (var child in new UIElement[] { mode, start, typing, check, resume, report }) panel.Children.Add(child);
        Content = panel;
        SourceInitialized += (_, _) => handle = new WindowInteropHelper(this).Handle;
        typing.TextChanged += (_, _) => { if (armed) typedCharacters++; };
        start.Click += (_, _) => starting = StartAsync();
        check.Click += (_, _) => checking = CheckAsync();
        resume.Click += (_, _) => checking = ResumeAsync();
        timer.Tick += (_, _) => { if (!ticking) tickingTask = TickAsync(); };
        Closing += CloseAsync;
    }

    private async Task StartAsync()
    {
        start.IsEnabled = mode.IsEnabled = false;
        try
        {
            RequireForeground(); selected = mode.SelectedIndex;
            Directory.CreateDirectory(directory);
            var factory = new SqliteAgentDbContextFactory(Path.Combine(directory, "synthetic.db"));
            await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(lifetime.ApplicationStopping);
            repository = new(factory); schedules = new(factory);
            desktop = new(_ => armed, (id, ct) => gate!.WaitAsync(id, ct));
            runs = new(repository, desktop); gate = new(runs, schedules, systemIdle: idle);
            takeover = new(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
            if (!takeover.Start()) throw new InvalidOperationException("HotkeyUnavailable");
            controls = new(runs, runs, desktop, takeover, lifetime);
            tray = new(); tray.Start(controls, () => { Show(); Activate(); }, () => { Show(); Activate(); }, Close);
            if (selected == 2) await ConnectMcpAsync();
            if (closing) return;
            armed = true; runner = new TaskRunner(runs, new Runtime(ExecuteAsync)).RunAsync(lifetime.ApplicationStopping);
            if (selected == 0)
            {
                var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic PA115 tray hold", new("attended-regression")));
                runId = run.Id;
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "PA115 synthetic native check", Instructions = "Read only the authorized synthetic fixture.",
                    ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
                await schedules.SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, TimeZoneId = "UTC",
                    Recurrence = new() { Mode = ScheduleMode.Once, OnceAtUtc = now.AddSeconds(selected == 1 ? 15 : 2) },
                    DesktopPolicy = selected == 1 ? ScheduledDesktopPolicy.StartWhenIdle : ScheduledDesktopPolicy.AskEveryTime,
                    CreatedAtUtc = now, UpdatedAtUtc = now }));
                scheduler = new(schedules, runs);
                Write(selected == 1 ? "PA115 SCHEDULE ARMED; due in15seconds. Keep typing synthetic text here until CHECK is enabled (about30seconds). Do not press Start now in the warning."
                    : "PA115 MCP ARMED; local read then GUI request. Type synthetic text here until CHECK is enabled; GUI must wait for explicit permission.");
                typing.Focus();
            }
            timer.Start();
        }
        catch (Exception error) { Write($"PA115 BLOCKED; stage=start; type={error.GetType().Name}; close this fixture, no retry."); }
    }

    private async Task ConnectMcpAsync()
    {
        server = McpTestServer.Program.CreateHttpServer(listTools: () => [new Tool { Name = "read_synthetic", Description = "Read synthetic local data only",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }) }], callTool: (_, _) =>
        {
            Interlocked.Increment(ref mcpCalls);
            if (desktop!.Snapshot.OwnerRunId is not null || captures != 0) nativeViolation = true;
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Synthetic local result; untrusted data." }] });
        });
        await server.StartAsync(lifetime.ApplicationStopping);
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        connection = await new McpConnectionFactory([new HttpMcpTransportProvider(new NoSecrets())]).ConnectAsync(new()
        {
            Id = Guid.NewGuid(), Name = "PA115 loopback read", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
            Http = new(new Uri(address + "/mcp"), AllowLoopbackHttp: true), AllowedTools = ["read_synthetic"]
        }, lifetime.ApplicationStopping);
        var declaration = (await new McpToolDiscovery().RefreshAsync(connection, lifetime.ApplicationStopping)).Tools.Single();
        mcp = new(connection, declaration, new(connection.Configuration.Id, declaration.Descriptor.Id, declaration.Version, ToolRiskLevel.ReadOnly, true));
    }

    private async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
    {
        try { return await ExecuteCoreAsync(request, progress, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error switch { ScreenCaptureException value => value.Code, DesktopAccessException value => value.Code, _ => "DiagnosticExecutionFailed" };
            Write($"PA115 BLOCKED; stage={executionStage}; type={error.GetType().Name}; code={code}; ownForeground={Native.GetForegroundWindow() == handle}; desktopState={desktop?.Snapshot.State}; no retry.");
            return new() { Status = AgentResultStatus.Blocked, ErrorCode = code, Summary = "Native diagnostic stopped; review the fixed stage/code report before retrying." };
        }
        finally { if (acquiredLease is { } lease) await desktop!.ReleaseAsync(lease); }
    }

    private async Task<AgentRunResult> ExecuteCoreAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
    {
        runId = request.RunId;
        if (selected == 0)
        {
            executionStage = "tray-initial-acquire";
            var lease = await desktop!.AcquireAsync(request.RunId, ct); firstGeneration = lease.Generation;
            executionStage = "tray-initial-capture";
            var controller = Controller(lease); await controller.ScreenshotAsync(new(), ct);
            Write("PA115 TRAY ARMED; use the Personal Agent tray menu: Pause & Take Control. This run owns the desktop but sends no input. Then switch to a harmless window, return and CHECK.");
            while (runs!.GetRun(request.RunId).PauseScope is null) await Task.Delay(50, ct);
            await takeover!.Completion.WaitAsync(ct);
            await AttendedPause.WaitForResumeAsync(runs, request.RunId, () => { capturesAtPause = captures; paused = true; }, ct);
            // Even a tray Resume cannot trigger capture without the fixture's own explicit button.
            await allowResume.Task.WaitAsync(ct);
            executionStage = "tray-resume-acquire";
            var next = await desktop.AcquireAsync(request.RunId, ct);
            bool staleDenied;
            executionStage = "tray-stale-capture-denial";
            try { await controller.ScreenshotAsync(new(), ct); staleDenied = false; }
            catch (ScreenCaptureException) { staleDenied = true; }
            executionStage = "tray-fresh-capture";
            await Controller(next).ScreenshotAsync(new(), ct);
            var ok = checkedPause && staleDenied && next.Generation > firstGeneration && captures == capturesAtPause + 1;
            Write($"PA115 TRAY RESUME {(ok ? "OK" : "FAILED")}; staleDenied={staleDenied}; generation={firstGeneration}->{next.Generation}; freshCapture={captures == capturesAtPause + 1}; nativeInput=0; otherWindowObservationRequired=True.");
            return new() { Status = AgentResultStatus.Completed, Summary = "Synthetic native tray check ended." };
        }
        executionStage = "scheduled-maf-tools";
        await using var computer = new ComputerToolSession(request.RunId, desktop!, Controller);
        var tools = computer.Tools.Where(tool => tool.Descriptor.Id == "computer_screenshot").ToList();
        if (mcp is not null) tools.Add(mcp);
        var registry = new ToolRegistry(tools, runs!.WaitAtBoundaryAsync, (_, _, _) => ValueTask.FromResult(true));
        var steps = new List<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>>();
        if (mcp is not null) steps.Add((_, _) => Call(mcp.Descriptor.Id));
        steps.Add((_, _) => Call("computer_screenshot"));
        steps.Add((_, _) => new(new ChatMessage(ChatRole.Assistant, "Synthetic own-window capture complete.")) { FinishReason = ChatFinishReason.Stop });
        using var client = new ScriptedChatClient(steps.ToArray());
        var result = await new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), runs.WaitAtBoundaryAsync,
            registry, computer.ConfirmModelObservation).ExecuteAsync(request with { AllowedToolIds = tools.Select(tool => tool.Descriptor.Id).ToArray() }, progress, ct);
        return result;
    }

    private WindowsComputerController Controller(DesktopLease lease)
    {
        acquiredLease = lease;
        return new(desktop!, lease, new OwnCapture(this), new NoInput(), () => new WindowsCaptureNative().GetMonitors());
    }
    private static ChatResponse Call(string tool) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls };

    private async Task TickAsync()
    {
        if (ticking || closing || runs is null) return;
        ticking = true;
        try
        {
            if (scheduler is not null) await scheduler.TickAsync(lifetime.ApplicationStopping);
            controls!.Refresh(); tray!.Refresh(controls.Status);
            if (selected == 0) { check.IsEnabled = paused && !decisionTaken; return; }
            var warning = gate!.GetWarnings().SingleOrDefault();
            if (warning is not null)
            {
                if (warningId != warning.Id)
                {
                    warningWindow?.Close(); warningId = warning.Id; warnings++;
                    warningModel = new(gate, warning.Id); warningWindow = new(warningModel) { Owner = this };
                    warningWindow.Show();
                }
                warningModel!.Refresh();
                var input = idle.Read();
                if (input.DesktopAvailable && input.InputIdleTime is { TotalSeconds: < 2 }) busySamples++;
                if (desktop!.Snapshot.OwnerRunId is not null || captures != 0) nativeViolation = true;
                check.IsEnabled = !decisionTaken && warning.Presented && warning.SecondsRemaining == 0 && typedCharacters >= 10;
            }
            else if (warningWindow is not null) { warningWindow.Close(); warningWindow = null; }
        }
        catch (Exception error) { timer.Stop(); Write($"PA115 BLOCKED; stage=monitor; type={error.GetType().Name}; close this fixture."); }
        finally { ticking = false; }
    }

    private async Task CheckAsync()
    {
        decisionTaken = true; check.IsEnabled = false;
        try
        {
            RequireForeground();
            if (runId is not { } id) throw new InvalidOperationException("RunMissing");
            if (selected == 0)
            {
                await takeover!.Completion;
                var before = captures; await Task.Delay(750, lifetime.ApplicationStopping);
                var stored = (await repository!.GetAsync(id))!;
                controls!.Refresh();
                checkedPause = paused && controls!.IsControlConfirmed && captures == before && captures == capturesAtPause &&
                    desktop!.Snapshot.OwnerRunId is null && stored.Events.Any(e => e.EventType == RunEventType.DesktopPauseConfirmed);
                Write($"PA115 TRAY PAUSE {(checkedPause ? "OK" : "FAILED")}; checkpoint={takeover.Handoff.Confirmed}; controlConfirmed={controls!.IsControlConfirmed}; captures={before}->{captures}; nativeInput=0; manuallyConfirmWindowSwitch=True.");
                resume.IsEnabled = checkedPause; return;
            }
            var warning = gate!.GetWarnings().Single();
            var ok = warning.Presented && warning.SecondsRemaining == 0 && warnings == 1 && typedCharacters >= 10 && busySamples >= 4 &&
                !nativeViolation && captures == 0 && desktop!.Snapshot.OwnerRunId is null && (selected == 1 || mcpCalls == 1);
            busyReport = $"warnings={warnings}; typedEvents={typedCharacters}; busySamples={busySamples}; captures={captures}; nativeInput=0; ownerAbsent={desktop!.Snapshot.OwnerRunId is null}; mcpCalls={mcpCalls}";
            Write($"PA115 {(selected == 1 ? "SCHEDULE" : "MCP")} WAIT {(ok ? "OK" : "FAILED")}; {busyReport}; scriptedModel=True; manuallyConfirmNoFocusTheft=True.");
            if (!ok) return;
            if (selected == 1)
            {
                if (!gate.Decide(warning.Id, ScheduledDesktopDecision.SkipOnce)) throw new InvalidOperationException("SkipRejected");
                while (!TaskRunStateMachine.IsTerminal(runs!.GetRun(id).Status)) await Task.Delay(50, lifetime.ApplicationStopping);
                var stored = (await repository!.GetAsync(id))!;
                var skipped = stored.Run.Status == RunStatus.Skipped && captures == 0;
                Write($"PA115 SCHEDULE {(skipped ? "OK" : "FAILED")}; durableStatus={stored.Run.Status}; {busyReport}; noGui=True; oneWarning={warnings == 1}.");
            }
            else resume.IsEnabled = true;
        }
        catch (Exception error) { Write($"PA115 BLOCKED; stage=check; type={error.GetType().Name}; no retry."); }
    }

    private async Task ResumeAsync()
    {
        resume.IsEnabled = false;
        try
        {
            RequireForeground(); var id = runId!.Value;
            if (selected == 0)
            {
                var run = runs!.GetRun(id);
                if (run.PauseScope is not null) await runs.ResumeAsync(new(Guid.NewGuid(), id, run.RunVersion));
                allowResume.TrySetResult();
            }
            else
            {
                var warning = gate!.GetWarnings().Single();
                if (!gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)) throw new InvalidOperationException("AdmissionRejected");
            }
            var terminal = await runs!.WaitForTerminalAsync(id, lifetime.ApplicationStopping).WaitAsync(TimeSpan.FromSeconds(10), lifetime.ApplicationStopping);
            if (terminal.Status != RunStatus.Completed)
            {
                Write($"PA115 {(selected == 0 ? "TRAY" : "MCP")} RESUME BLOCKED; durableStatus={terminal.Status}; code={terminal.ErrorCode ?? "NoCompletedResult"}; stage={executionStage}; no retry.");
                return;
            }
            if (selected == 2)
            {
                var stored = (await repository!.GetAsync(id))!;
                var ok = stored.Run.Status == RunStatus.Completed && captures == 1 && mcpCalls == 1 && !nativeViolation;
                Write($"PA115 MCP {(ok ? "OK" : "FAILED")}; durableStatus={stored.Run.Status}; captures={captures}; mcpCalls={mcpCalls}; nativeInput=0; explicitAdmission=True; noReadReplay={mcpCalls == 1}; scriptedModel=True.");
            }
        }
        catch (Exception error) { Write($"PA115 BLOCKED; stage=resume; type={error.GetType().Name}; no retry."); }
    }

    private void RequireForeground() { if (Native.GetForegroundWindow() != handle) throw new InvalidOperationException("OwnWindowNotForeground"); }
    private void Write(string value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Write(value))); return; }
        report.AppendText(value + Environment.NewLine); report.ScrollToEnd();
    }
    private async void CloseAsync(object? sender, CancelEventArgs args)
    {
        if (closed) return; args.Cancel = true; if (closing) return;
        closing = true; armed = false; timer.Stop(); lifetime.StopApplication();
        start.IsEnabled = check.IsEnabled = resume.IsEnabled = false;
        try
        {
            await Task.WhenAll(starting ?? Task.CompletedTask, checking ?? Task.CompletedTask, runner ?? Task.CompletedTask, tickingTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(12));
            warningWindow?.Close(); tray?.Dispose(); takeover?.Dispose(); runs?.Dispose();
            if (connection is not null) await connection.DisposeAsync();
            if (server is not null) { await server.StopAsync(); await server.DisposeAsync(); }
        }
        catch (Exception) { /* Preserve scoped synthetic data on incomplete cleanup; never retry effects. */ }
        finally
        {
            warningWindow?.Close(); tray?.Dispose(); takeover?.Dispose(); closed = true;
            // Unarmed cleanup can complete synchronously inside Closing; defer the final Close.
            _ = Dispatcher.BeginInvoke(new Action(() => { Close(); shutdown(); }));
        }
    }

    private sealed class OwnCapture(RegressionWindow window) : IWindowsCaptureNative
    {
        private readonly WindowsCaptureNative native = new();
        public bool IsDesktopAvailable() => window.armed && Native.GetForegroundWindow() == window.handle && native.IsDesktopAvailable();
        public IReadOnlyList<ScreenMonitor> GetMonitors() => native.GetMonitors();
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors) => window.Dispatcher.Invoke(() =>
        {
            window.RequireForeground();
            if (Native.DwmGetWindowAttribute(window.handle, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0)
                throw new ScreenCaptureException("OwnWindowBoundsUnavailable", "The authorized test window bounds are unavailable.");
            var own = new ScreenBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (!bounds.Contains(new(own.X, own.Y)) || !bounds.Contains(new(own.X + own.Width - 1, own.Y + own.Height - 1)))
                throw new ScreenCaptureException("KeepWholeWindowOnPrimaryMonitor", "Keep the complete authorized test window on the primary monitor.");
            var bitmap = GdiScreenCapture.Capture(bounds, [own]);
            if (!IsDesktopAvailable()) throw new ScreenCaptureException("OwnWindowLostForeground", "The authorized test window lost foreground.");
            Interlocked.Increment(ref window.captures); return bitmap;
        });
    }
    private sealed class NoInput : IWindowsKeyboardNative
    {
        public bool IsDesktopAvailable() => false;
        public bool IsKeyDown(ushort key) => false;
        public InputInsertion Send(NativeInput[] inputs) => throw new InvalidOperationException("NativeInputForbidden");
    }
    private sealed class NoSecrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stop.Token;
        public CancellationToken ApplicationStopped => stop.Token;
        public void StopApplication() => stop.Cancel();
    }
    private sealed class Runtime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    { public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, progress, ct); }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint handle, int attribute, out Rect rect, int size);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    }
}
