using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class HybridMcpGuiTests
{
    [Theory]
    [InlineData(ScheduledDesktopPolicy.StartWhenIdle)]
    [InlineData(ScheduledDesktopPolicy.StartAfterCountdown)]
    [InlineData(ScheduledDesktopPolicy.AskEveryTime)]
    public Task ScheduledMcpReadPreservesContextWhileFirstGuiWaitsForActualWarningIdleAndLease(ScheduledDesktopPolicy policy) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(true, scheduledPolicy: policy);
        await fixture.DispatchScheduled();
        await Until(() => fixture.Gate.GetWarnings().Count == 1 && fixture.Dashboard.Status == "WaitingForDesktop");
        var warning = fixture.Gate.GetWarnings().Single();
        Assert.Equal(1, fixture.McpCalls); Assert.Equal(2, fixture.Client!.CallCount);
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Equal(0, fixture.ControllerCreations); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        var model = new ScheduledDesktopWarningViewModel(fixture.Gate, warning.Id); var window = new ScheduledDesktopWarningWindow(model);
        try
        {
            await Bind(window); model.Presented(); fixture.Clock.Advance(TimeSpan.FromSeconds(14)); await Task.Delay(350);
            Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            if (policy != ScheduledDesktopPolicy.StartAfterCountdown)
            {
                await Task.Delay(350); Assert.Equal(0, fixture.Native.Captures); Assert.Equal(1, fixture.McpCalls);
                fixture.Idle = true;
                if (policy == ScheduledDesktopPolicy.AskEveryTime)
                {
                    await Task.Delay(350); Assert.Equal(0, fixture.Native.Captures);
                    model.Refresh(); model.StartCommand.Execute(null); await model.StartCommand.ExecutionTask!;
                }
            }
            await fixture.Runs.WaitForTerminalAsync(fixture.RunId).WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => fixture.Dashboard.Status == "Completed");
            Assert.Equal(1, fixture.McpCalls); Assert.Equal(4, fixture.Client.CallCount); Assert.Equal(1, fixture.ControllerCreations);
            Assert.Equal(2, fixture.Native.Captures); Assert.Single(fixture.Native.Inputs); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
            var stored = (await fixture.Repository.GetAsync(fixture.RunId))!;
            Assert.Equal("schedule", stored.Run.Origin.Source); Assert.NotNull(stored.Run.TaskDefinitionId);
            Assert.Equal("https://jira.fixture.invalid/browse/TEST-42", Assert.Single(stored.Result!.Sources).Url.AbsoluteUri);
            Assert.Equal("Completed", ((TextBlock)fixture.Window.FindName("RunStatusText")).Text);
            var requested = stored.Events.Where(value => value.EventType == RunEventType.ToolRequested).ToArray();
            var executed = stored.Events.Where(value => value.EventType == RunEventType.ToolExecuted).ToArray();
            Assert.Equal(3, requested.Length); Assert.Equal(requested.Select(value => value.ToolCallId), executed.Select(value => value.ToolCallId));
            Assert.Contains(stored.Events, value => value.Message.Contains("Waiting for scheduled desktop warning"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ScheduledLateGuiSkipOrCancelDoesNotReplayMcpReadOrCreateNativeEffects(bool cancel) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(true, scheduledPolicy: ScheduledDesktopPolicy.StartWhenIdle);
        await fixture.DispatchScheduled(); await Until(() => fixture.Gate.GetWarnings().Count == 1 && fixture.Dashboard.Status == "WaitingForDesktop");
        var warning = fixture.Gate.GetWarnings().Single(); fixture.Gate.Present(warning.Id);
        if (cancel) { fixture.Dashboard.CancelCommand.Execute(null); await fixture.Dashboard.CancelCommand.ExecutionTask!; }
        else Assert.True(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.SkipOnce));
        var run = await fixture.Runs.WaitForTerminalAsync(fixture.RunId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(cancel ? RunStatus.Cancelled : RunStatus.Skipped, run.Status);
        Assert.Equal(1, fixture.McpCalls); Assert.Equal(2, fixture.Client!.CallCount); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
        Assert.Equal(0, fixture.ControllerCreations); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow));
    });

    [Fact]
    public Task ScheduledModelCannotClickDirectlyAfterMcpWithoutAFreshEvaluatedFrame() => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(true, scheduledPolicy: ScheduledDesktopPolicy.StartWhenIdle, directClick: true);
        await fixture.DispatchScheduled(); await Until(() => fixture.Dashboard.Status == "Blocked");
        var stored = (await fixture.Repository.GetAsync(fixture.RunId))!;
        Assert.Equal("StaleDesktopContext", stored.Result!.ErrorCode); Assert.Equal(1, fixture.McpCalls); Assert.Equal(2, fixture.Client!.CallCount);
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Equal(0, fixture.ControllerCreations);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId); Assert.Empty(fixture.Gate.GetWarnings());
        Assert.Equal("https://jira.fixture.invalid/browse/TEST-42", Assert.Single(stored.Result.Sources).Url.AbsoluteUri);
        fixture.Dashboard.CancelCommand.Execute(null); await fixture.Dashboard.CancelCommand.ExecutionTask!;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OneRunUsesActualMcpAndGuiFixtureWithSourcesHistoryAndLateDesktopConsent(bool mcpFirst) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(mcpFirst);
        fixture.Dashboard.SubmitCommand.Execute(null);
        if (mcpFirst)
        {
            await fixture.GuiRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => fixture.Dashboard.Status == "Paused");
            Assert.Equal(1, fixture.McpCalls); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
            Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
            Assert.Contains(fixture.Dashboard.Events, value => value.Message.Contains("GUI requires explicit admission"));
            var responsive = false; await fixture.Window.Dispatcher.InvokeAsync(() => responsive = true); Assert.True(responsive);
            fixture.AllowDesktop = fixture.Idle = true;
            fixture.Dashboard.ResumeCommand.Execute(null); await fixture.Dashboard.ResumeCommand.ExecutionTask!;
        }
        await fixture.Dashboard.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10)); await Pump();
        Assert.Equal("Completed", ((TextBlock)fixture.Window.FindName("RunStatusText")).Text);
        Assert.Contains("TEST-42", ((TextBlock)fixture.Window.FindName("RunSummaryText")).Text);
        Assert.Equal(1, fixture.McpCalls); Assert.Equal(2, fixture.Native.Captures); Assert.Single(fixture.Native.Inputs);
        Assert.Equal(4, fixture.Client!.CallCount); Assert.Equal(1, fixture.ControllerCreations);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        var stored = await fixture.Repository.GetAsync(fixture.RunId);
        Assert.Equal(RunStatus.Completed, stored!.Run.Status);
        Assert.Single(await fixture.Repository.ListAsync());
        Assert.Equal("https://jira.fixture.invalid/browse/TEST-42", Assert.Single(stored.Result!.Sources).Url.AbsoluteUri);
        Assert.DoesNotContain(stored.Result.Sources, value => value.Url.Host == "forged.invalid");
        Assert.Equal(stored.Result.Sources, fixture.Dashboard.Sources);
        var requested = stored.Events.Where(value => value.EventType == RunEventType.ToolRequested).ToArray();
        var executed = stored.Events.Where(value => value.EventType == RunEventType.ToolExecuted).ToArray();
        Assert.Equal(3, requested.Length); Assert.Equal(requested.Select(value => value.ToolCallId), executed.Select(value => value.ToolCallId));
        Assert.All(stored.Events, value => Assert.Equal(fixture.RunId, value.RunId));
        var reopened = await new Persistence.SqliteRunRepository(new Persistence.SqliteAgentDbContextFactory(fixture.Database.DatabasePath)).GetAsync(fixture.RunId);
        Assert.Equal(stored.Result.Summary, reopened!.Result!.Summary); Assert.Equal(stored.Result.Sources, reopened.Result.Sources);
        if (mcpFirst) Assert.Contains(stored.Events, value => value.EventType == RunEventType.RunResumed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task McpErrorOrTimeoutAfterGuiKeepsDashboardAndDurableBlockedOutcomeConsistentWithoutReplay(bool timeout) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(false, timeout ? "timeout" : "error");
        fixture.Dashboard.SubmitCommand.Execute(null);
        await Until(() => fixture.Dashboard.Status == "Blocked");
        var stored = await fixture.Repository.GetAsync(fixture.RunId);
        Assert.Equal(RunStatus.Blocked, stored!.Run.Status); Assert.Equal(AgentResultStatus.Blocked, stored.Result!.Status);
        Assert.Equal(timeout ? "McpRequestTimedOut" : "McpToolError", stored.Result.ErrorCode);
        Assert.Equal(stored.Result.ErrorCode, fixture.Dashboard.Result!.ErrorCode);
        Assert.Equal("Blocked", ((TextBlock)fixture.Window.FindName("RunStatusText")).Text);
        Assert.Equal(1, fixture.McpCalls); Assert.Equal(3, fixture.Client!.CallCount); Assert.Equal(2, fixture.Native.Captures);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId); Assert.DoesNotContain("private-jira-error", JsonSerializer.Serialize(stored.Result));
        fixture.Dashboard.CancelCommand.Execute(null); await fixture.Dashboard.CancelCommand.ExecutionTask!;
        await fixture.Dashboard.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Cancelled", fixture.Dashboard.Status); Assert.Equal(1, fixture.McpCalls);
    });

    [Fact]
    public Task CancelWhileLateGuiConsentIsPendingNeverAcquiresDesktopOrRepeatsCompletedMcpRead() => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(true);
        fixture.Dashboard.SubmitCommand.Execute(null);
        await fixture.GuiRequested.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Until(() => fixture.Dashboard.Status == "Paused");
        fixture.Dashboard.CancelCommand.Execute(null); await fixture.Dashboard.CancelCommand.ExecutionTask!;
        await fixture.Dashboard.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Cancelled", fixture.Dashboard.Status); Assert.Equal(1, fixture.McpCalls);
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Equal(0, fixture.ControllerCreations);
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.Equal(RunStatus.Cancelled, (await fixture.Repository.GetAsync(fixture.RunId))!.Run.Status);
    });

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly TemporaryAgentDatabase Database = new();
        public readonly TaskCompletionSource GuiRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly GuiNative Native = new(Dispatcher.CurrentDispatcher);
        public DesktopAccessCoordinator Desktop = null!;
        public bool AllowDesktop;
        public volatile bool Idle;
        public Guid RunId;
        public int McpCalls, ControllerCreations;
        public ScriptedChatClient? Client;
        public MainWindow Window = null!;
        public DashboardViewModel Dashboard = null!;
        public IRunRepository Repository => host.Services.GetRequiredService<IRunRepository>();
        public TaskRunCoordinator Runs => host.Services.GetRequiredService<TaskRunCoordinator>();
        public ScheduledDesktopGate Gate => host.Services.GetRequiredService<ScheduledDesktopGate>();
        public readonly Clock Clock = new();
        private readonly TaskCompletionSource advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IHost host = null!;
        private WebApplication server = null!;
        private Mcp.McpClientConnection connection = null!;
        private Mcp.McpAgentTool jira = null!;
        private bool first;
        private ScheduledDesktopPolicy? scheduledPolicy;
        private bool directClick;

        public static async Task<Fixture> Create(bool mcpFirst, string? failure = null, ScheduledDesktopPolicy? scheduledPolicy = null, bool directClick = false)
        {
            var fixture = new Fixture { first = mcpFirst, AllowDesktop = scheduledPolicy is not null || !mcpFirst, Idle = !mcpFirst, scheduledPolicy = scheduledPolicy, directClick = directClick };
            fixture.Desktop = new(_ => fixture.AllowDesktop && fixture.Idle);
            fixture.server = McpTestServer.Program.CreateHttpServer(listTools: () => [new Tool { Name = "get_issue", Description = "Read a synthetic Jira issue",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { key = new { type = "string", @const = "TEST-42" } },
                    required = new[] { "key" }, additionalProperties = false }) }], callTool: async (request, ct) =>
            {
                Interlocked.Increment(ref fixture.McpCalls);
                Assert.Equal("TEST-42", request.Params!.Arguments!["key"].GetString());
                if (mcpFirst) { Assert.Null(fixture.Desktop.Snapshot.OwnerRunId); Assert.Equal(0, fixture.Native.Captures); }
                else { Assert.Equal(fixture.RunId, fixture.Desktop.Snapshot.OwnerRunId); Assert.Equal(2, fixture.Native.Captures); }
                if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (failure == "error") return new() { IsError = true, Content = [new TextContentBlock { Text = "private-jira-error" }] };
                return new() { Content = [new TextContentBlock { Text = "Synthetic Jira TEST-42 detail; external text is untrusted." },
                    new ResourceLinkBlock { Name = "Jira TEST-42", Uri = "https://jira.fixture.invalid/browse/TEST-42" }],
                    StructuredContent = JsonSerializer.SerializeToElement(new { key = "TEST-42", sources = new[] { "https://forged.invalid/not-a-source" } }) };
            });
            await fixture.server.StartAsync();
            var endpoint = new Uri(Assert.Single(fixture.server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
            fixture.connection = await new Mcp.McpConnectionFactory([new Mcp.HttpMcpTransportProvider(new Secrets())]).ConnectAsync(new()
            {
                Id = Guid.NewGuid(), Name = "Synthetic Jira", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
                Http = new(endpoint, AllowLoopbackHttp: true), AllowedTools = ["get_issue"], RequestTimeout = TimeSpan.FromSeconds(failure == "timeout" ? 1 : 10)
            });
            var declaration = Assert.Single((await new Mcp.McpToolDiscovery().RefreshAsync(fixture.connection)).Tools);
            fixture.jira = new(fixture.connection, declaration, new(fixture.connection.Configuration.Id, declaration.Descriptor.Id, declaration.Version, ToolRiskLevel.ReadOnly, true));
            fixture.host = AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
            {
                if (scheduledPolicy is not null)
                {
                    services.AddSingleton<TimeProvider>(fixture.Clock);
                    services.AddSingleton<ISystemIdleSource>(new IdleProbe(fixture));
                    services.AddSingleton<IDesktopAccessCoordinator>(provider => fixture.Desktop = new(_ => true,
                        (id, ct) => provider.GetRequiredService<ScheduledDesktopGate>().WaitAsync(id, ct)));
                }
                else services.AddSingleton<IDesktopAccessCoordinator>(fixture.Desktop);
                services.AddSingleton<ITaskCommands>(provider => new Commands(provider.GetRequiredService<TaskRunCoordinator>(), fixture));
                services.AddSingleton<IAgentRuntime>(provider => new Runtime((request, progress, ct) => fixture.ExecuteAsync(
                    provider.GetRequiredService<TaskRunCoordinator>(), request, progress, ct)));
            }).Build();
            await fixture.host.StartAsync();
            fixture.Window = fixture.host.Services.GetRequiredService<MainWindow>(); await Bind(fixture.Window);
            fixture.Dashboard = fixture.host.Services.GetRequiredService<DashboardViewModel>();
            fixture.Dashboard.Instructions = "Find the synthetic Teams reference and read Jira details";
            return fixture;
        }
        public async Task DispatchScheduled()
        {
            var now = Clock.GetUtcNow(); var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Scheduled hybrid fixture", Instructions = Dashboard.Instructions,
                ModelProfileId = "scripted-hybrid", CreatedAtUtc = now, UpdatedAtUtc = now };
            await host.Services.GetRequiredService<IScheduleRepository>().SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id,
                Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC", DesktopPolicy = scheduledPolicy!.Value, CreatedAtUtc = now, UpdatedAtUtc = now }));
            Clock.Advance(TimeSpan.FromMinutes(10)); await host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync();
            var run = Assert.Single(Runs.GetRuns()); RunId = run.Id; Dashboard.SelectedHistory = run;
            advanced.TrySetResult(); // Let the controlled model begin after the dispatcher committed the next occurrence.
        }
        private async Task<AgentRunResult> ExecuteAsync(TaskRunCoordinator runs, AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            RunId = request.RunId;
            if (scheduledPolicy is not null) await advanced.Task.WaitAsync(ct);
            await using var computer = new ComputerToolSession(request.RunId, Desktop, lease =>
            {
                Interlocked.Increment(ref ControllerCreations);
                return new WindowsComputerController(Desktop, lease, Native, Native, Native.GetMonitors);
            });
            var tools = computer.Tools.Where(tool => tool.Descriptor.Id is "computer_screenshot" or "computer_click").Append(jira).ToArray();
            var registry = new ToolRegistry(tools, async (id, access, token) =>
            {
                if (access.RequiresDesktop && !AllowDesktop)
                {
                    progress.Report(new() { RunId = id, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.Warning,
                        Message = "Synthetic warning: GUI requires explicit admission and idle desktop; no GUI operation has started." });
                    await runs.PauseAsync(new(Guid.NewGuid(), id, runs.GetRun(id).RunVersion), token);
                    var boundary = runs.WaitAtBoundaryAsync(id, access, token);
                    while (runs.GetRun(id).Status != RunStatus.Paused && !boundary.IsCompleted) await Task.Delay(10, token);
                    GuiRequested.TrySetResult();
                    await boundary;
                }
                await runs.WaitAtBoundaryAsync(id, access, token);
            }, (_, _, _) => ValueTask.FromResult(true)); // Trusted fixture owner permits only these synthetic targets.
            string? observation = null;
            var steps = new List<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>>();
            if (first) steps.Add((_, _) => Call("jira", jira.Descriptor.Id, new { key = "TEST-42" }));
            steps.Add((messages, _) =>
            {
                if (first) { Assert.Equal(1, McpCalls); Assert.Equal(0, Native.Captures); Assert.Null(Desktop.Snapshot.OwnerRunId); }
                if (directClick) return Call("unobserved-click", "computer_click", new { observationId = "00000000000000000000000000000000", imageX = 0, imageY = 0, button = "left" });
                return Call("screen", "computer_screenshot", new { });
            });
            steps.Add((messages, _) =>
            {
                Assert.Equal(request.RunId, Desktop.Snapshot.OwnerRunId); Assert.Equal(1, Native.Captures);
                if (scheduledPolicy is not null) Assert.Contains(messages.SelectMany(value => value.Contents).OfType<FunctionResultContent>(), value => value.CallId == "jira" && ((string)value.Result!).Contains("TEST-42"));
                observation = Observation(messages); Assert.Contains(messages.SelectMany(value => value.Contents), value => value is DataContent);
                return Call("click", "computer_click", new { observationId = observation, imageX = 0, imageY = 0, button = "left" });
            });
            if (!first) steps.Add((messages, _) =>
            {
                Assert.NotEqual(observation, Observation(messages)); Assert.Equal(2, Native.Captures);
                Assert.True(Native.TeamsSelected);
                return Call("jira", jira.Descriptor.Id, new { key = "TEST-42" });
            });
            steps.Add((messages, options) =>
            {
                Assert.Equal(AgentPromptPolicy.Instructions, options!.Instructions);
                Assert.Equal(2, Native.Captures); Assert.Equal(1, McpCalls);
                Assert.Equal(first ? "click" : "jira", messages.SelectMany(value => value.Contents).OfType<FunctionResultContent>().Last().CallId);
                return new(new ChatMessage(ChatRole.Assistant, "Teams mentions TEST-42; Jira details read with a source.")) { FinishReason = ChatFinishReason.Stop };
            });
            Client = new(steps.ToArray());
            return await new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(Client), runs.WaitAtBoundaryAsync, registry,
                computer.ConfirmModelObservation).ExecuteAsync(request, progress, ct);
        }
        public async ValueTask DisposeAsync()
        {
            Window?.Close(); await host.StopAsync(); host.Dispose(); await connection.DisposeAsync(); await server.StopAsync(); await server.DisposeAsync(); Database.Dispose();
        }
        private sealed class Commands(TaskRunCoordinator runs, Fixture fixture) : ITaskCommands
        {
            public Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken ct) => runs.SubmitAsync(command with
            { ModelProfileId = "scripted-hybrid", Profile = null, AllowedToolIds = ["computer_screenshot", "computer_click", fixture.jira.Descriptor.Id] }, ct);
            public Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken ct) => runs.CancelAsync(command, ct);
            public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken ct) => runs.PauseAsync(command, ct);
            public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken ct) => runs.ResumeAsync(command, ct);
            public Task<TaskRun> SkipAsync(SkipRunCommand command, CancellationToken ct) => runs.SkipAsync(command, ct);
        }
        private sealed class IdleProbe(Fixture fixture) : ISystemIdleSource
        {
            public SystemIdleSnapshot Read() => new(true, fixture.Idle ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks = DateTimeOffset.Parse("2026-10-20T08:00:00Z").UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
    private sealed class GuiNative(Dispatcher dispatcher) : IWindowsCaptureNative, IWindowsKeyboardNative
    {
        public int Captures; public bool TeamsSelected; public List<NativeInput[]> Inputs = [];
        public bool IsDesktopAvailable() => true;
        public bool IsKeyDown(ushort key) => false;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => [new("synthetic-gui", new(-480, 100, 480, 160), 96, 96, true)];
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        {
            Interlocked.Increment(ref Captures);
            return dispatcher.Invoke(() =>
            {
                var visual = new DrawingVisual();
                using (var draw = visual.RenderOpen())
                {
                    draw.DrawRectangle(Brushes.White, null, new(0, 0, 480, 160));
                    draw.DrawText(new FormattedText(TeamsSelected ? "Synthetic Teams: please check TEST-42" : "Synthetic Teams tab — click to open",
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1), new(12, 40));
                }
                var bitmap = new RenderTargetBitmap(480, 160, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
            });
        }
        public InputInsertion Send(NativeInput[] inputs) { Inputs.Add(inputs.ToArray()); TeamsSelected = true; return new((uint)inputs.Length, 0); }
    }
    private static ChatResponse Call(string id, string name, object arguments) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(id, name, JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone()))]))
    { FinishReason = ChatFinishReason.ToolCalls };
    private static string Observation(IReadOnlyList<ChatMessage> messages)
    {
        var result = messages.SelectMany(value => value.Contents).OfType<FunctionResultContent>().Last();
        using var body = JsonDocument.Parse((string)result.Result!); var data = body.RootElement.GetProperty("data");
        if (data.TryGetProperty("observation", out var observation)) data = observation;
        return data.GetProperty("observationId").GetString()!;
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Runtime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    { public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, progress, ct); }
    private static async Task Bind(Window window) { window.Measure(new Size(1000, 1000)); window.Arrange(new Rect(0, 0, 1000, 1000)); window.UpdateLayout(); await Pump(); }
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> condition) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, stop.Token); await Pump(); }
    private static Task OnWpfThread(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            })); Dispatcher.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
