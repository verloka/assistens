using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Diagnostics;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Channels;
using PersonalAgent.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class RunDiagnosticsTests
{
    private const string Private = "synthetic_private_payload_118";

    [Fact]
    public async Task BlockedModelResultRetainsKnownUsageInMetricsWithoutPretendingTheRunFinished()
    {
        using var metrics = new Metrics();
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(builder => builder.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(new FakeAgentRuntime(result: new()
            { Status = AgentResultStatus.Blocked, Summary = Private, ErrorCode = "ToolPermissionDenied", Usage = new(10, 5, 0.25m, "USD") }))).Build();
        await host.StartAsync(); var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), Private, new("desktop")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runs.GetResult(run.Id) is null) await Task.Delay(1, timeout.Token);
        var diagnostics = host.Services.GetRequiredService<RunDiagnosticsService>(); await diagnostics.ObserveAsync(); await diagnostics.ObserveAsync();
        Assert.Equal(RunStatus.Blocked, runs.GetRun(run.Id).Status); Assert.Null(runs.GetRun(run.Id).EndedAtUtc);
        Assert.Equal(10, metrics.Total("personalagent.tokens.input")); Assert.Equal(5, metrics.Total("personalagent.tokens.output"));
        Assert.Equal(0.25, metrics.Total("personalagent.cost.estimate")); Assert.Equal(0, metrics.Count(AgentOperation.Model));
        Assert.Contains(metrics.Measurements, item => item.Name == "personalagent.operations" && item.Tags == "operation=Run;outcome=Blocked");
        await host.StopAsync();
    }

    [Fact]
    public async Task PersistedUnknownDeliveryShowsMetadataAndManualReviewAfterRestartWithoutReplay()
    {
        using var database = new TemporaryAgentDatabase(); var run = await HistoryRetentionTests.SeedAsync(database);
        var stored = (await new SqliteRunRepository(database).GetAsync(run.Id))!;
        var outbox = new SqliteNotificationOutbox(database);
        var message = new OutgoingMessage { Id = Guid.NewGuid(), RunId = run.Id, ConversationId = run.ConversationId,
            Destination = new("fixture", "fixture", "peer"), Text = Private };
        await outbox.EnqueueAsync(stored.Events.Last().Id, message);
        var claimed = (await outbox.ClaimNextAsync(DateTimeOffset.UtcNow))!;
        await outbox.FinishAsync(message.Id, claimed.Version, NotificationDeliveryStatus.OutcomeUnknown, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "DeliveryOutcomeUnknown");
        for (var index = 0; index < 2; index++)
        {
            using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
            await host.StartAsync();
            var diagnostics = host.Services.GetRequiredService<RunDiagnosticsService>(); await diagnostics.ObserveAsync();
            var row = Assert.Single(diagnostics.DeliveryProblems);
            Assert.Equal(run.Id, row.RunId); Assert.Equal(message.Id, row.DeliveryId);
            Assert.Equal(NotificationDeliveryStatus.OutcomeUnknown, row.Status); Assert.Equal(1, row.Attempts);
            Assert.Contains("Do not retry", row.NextStep); Assert.DoesNotContain(Private, JsonSerializer.Serialize(row));
            Assert.Equal(1, (await outbox.GetAsync(message.Id))!.Attempts);
            await host.StopAsync();
        }
    }

    [Fact]
    public Task ActualDiagnosticsXamlKeepsPrivateFailureTextOutAndPauseMetricsOffTheControlThread() => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var metrics = new Metrics();
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(builder => builder.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(new FakeAgentRuntime(result: new()
            { Status = AgentResultStatus.Failed, Summary = Private, ErrorCode = "ToolOutcomeUnknown" }))).Build();
        await host.StartAsync();
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), Private, new("desktop")));
        await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var main = host.Services.GetRequiredService<MainWindow>();
        var model = host.Services.GetRequiredService<RunDiagnosticsViewModel>(); var window = new RunDiagnosticsWindow(model);
        try
        {
            model.Refresh();
            window.Measure(new System.Windows.Size(1050, 650)); window.Arrange(new System.Windows.Rect(0, 0, 1050, 650)); window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.NotNull(main.FindName("RunDiagnosticsButton")); Assert.False(window.ShowActivated);
            var grid = (System.Windows.Controls.DataGrid)window.FindName("DiagnosticsRuns");
            Assert.True(grid.IsReadOnly); Assert.Equal(run.Id, Assert.IsType<RunDiagnostic>(Assert.Single(grid.Items.Cast<object>())).RunId);
            var next = ((System.Windows.Controls.TextBlock)window.FindName("DiagnosticNextStep")).Text;
            Assert.Contains("Do not replay", next); Assert.DoesNotContain(Private, next);
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(model.Runs));
            Assert.Equal(RunDiagnosticCode.OutcomeUnknown, model.Selected!.Code);
            var before = runs.GetSnapshot(run.Id);
            model.Refresh(); model.Refresh(); var after = runs.GetSnapshot(run.Id);
            Assert.Equal(before.Run, after.Run); Assert.Equal(before.Events, after.Events); Assert.Equal(before.Result, after.Result);
            Assert.Equal(RunStatus.Failed, runs.GetRun(run.Id).Status);
            var controlThread = Environment.CurrentManagedThreadId;
            var takeover = host.Services.GetRequiredService<TakeControlHotkeyService>().TakeControlAsync();
            Assert.Equal(DesktopAccessState.PausedForUser, host.Services.GetRequiredService<IDesktopAccessCoordinator>().Snapshot.State);
            await takeover;
            var measured = metrics.Measurements.Where(m => m.Name == "personalagent.operations" && m.Tags.Contains("operation=PauseGateClose;")).ToArray();
            Assert.Single(measured); Assert.NotEqual(controlThread, measured[0].ThreadId);
        }
        finally { window.Close(); main.Close(); await host.StopAsync(); }
    });

    [Fact]
    public async Task ResumedRunFailureIsCountedOnceWithUnknownUsageRatherThanInventedZero()
    {
        using var metrics = new Metrics();
        PauseRuntime? runtime = null;
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(builder => builder.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => runtime = new PauseRuntime(provider.GetRequiredService<TaskRunCoordinator>()))).Build();
        await host.StartAsync(); var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), Private, new("desktop")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runtime!.Entered.Task.WaitAsync(timeout.Token);
        await runs.PauseAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
        runtime.Release.TrySetResult();
        while (runs.GetRun(run.Id).Status != RunStatus.Paused) await Task.Delay(1, timeout.Token);
        await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
        await runs.WaitForTerminalAsync(run.Id).WaitAsync(timeout.Token);
        var diagnostics = host.Services.GetRequiredService<RunDiagnosticsService>(); await diagnostics.ObserveAsync(); await diagnostics.ObserveAsync();
        Assert.Equal(1, metrics.Count(AgentOperation.Resume)); Assert.Equal(1, metrics.Count(AgentOperation.FailureAfterResume));
        Assert.Equal("Usage and cost: unknown", Assert.Single(diagnostics.Snapshot()).Usage);
        Assert.Equal(0, metrics.Total("personalagent.tokens.input"));
        await host.StopAsync();
    }

    [Fact]
    public async Task ActualHostedModelToolRunEmitsCorrelatedPrivateSafeDiagnosticsAndMetricsWithoutRecountAfterRestart()
    {
        using var metrics = new Metrics(); using var logs = new PrivateLogs();
        using var database = new TemporaryAgentDatabase();
        var tool = new ReadTool();
        using var client = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("fixture-call", tool.Descriptor.Id, new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls },
            (_, _) => new(new ChatMessage(ChatRole.Assistant, Private)) { FinishReason = ChatFinishReason.Stop });
        Guid id;
        using (var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders().AddProvider(logs))
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider =>
            {
                var runs = provider.GetRequiredService<TaskRunCoordinator>();
                var registry = new ToolRegistry([tool], runs.WaitAtBoundaryAsync, (_, _, _) => ValueTask.FromResult(true));
                return new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), runs.WaitAtBoundaryAsync, registry);
            })).Build())
        {
            await host.StartAsync();
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), Private, new("desktop"), AllowedToolIds: [tool.Descriptor.Id])); id = run.Id;
            Assert.Equal(RunStatus.Completed, (await runs.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            var diagnostics = host.Services.GetRequiredService<RunDiagnosticsService>(); await diagnostics.ObserveAsync();
            var logCount = logs.Lines.Count; await diagnostics.ObserveAsync(); Assert.Equal(logCount, logs.Lines.Count);
            var row = Assert.Single(diagnostics.Snapshot());
            Assert.Equal(1, row.ToolRequests); Assert.Equal(1, row.ToolCompletions); Assert.NotNull(row.Duration);
            Assert.Contains("Input: 2; output: 2", row.Usage); Assert.Contains("cost: unknown", row.Usage);
            Assert.Null(row.DesktopLeaseGeneration);
            Assert.Equal(1, tool.Calls); Assert.Equal(2, client.CallCount);
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(row));
            Assert.Contains(logs.Lines, line => line.Contains("RunId=" + id));
            Assert.Contains(logs.Lines, line => line.Contains("ToolCallId=") && line.Contains("ToolId=" + RunDiagnosticsService.Identifier(tool.Descriptor.Id)));
            Assert.DoesNotContain(logs.Lines, line => line.Contains(Private));
            Assert.Equal(2, metrics.Count(AgentOperation.Model)); Assert.Equal(1, metrics.Count(AgentOperation.Tool)); Assert.Equal(1, metrics.Count(AgentOperation.Mcp));
            Assert.Equal(1, metrics.Count(AgentOperation.Run)); Assert.Equal(2, metrics.Total("personalagent.tokens.input"));
            Assert.Equal(2, metrics.Total("personalagent.tokens.output")); Assert.Equal(0, metrics.Total("personalagent.cost.estimate"));
            Assert.DoesNotContain(metrics.Measurements, measurement => measurement.Tags.Contains(Private) || measurement.Tags.Contains(id.ToString()));
            Assert.Contains(metrics.Measurements, measurement => measurement.Name == "personalagent.duration" && measurement.Tags.Contains("Model"));
            await host.StopAsync();
        }
        using var restarted = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
        await restarted.StartAsync(); await restarted.Services.GetRequiredService<RunDiagnosticsService>().ObserveAsync();
        Assert.Equal(1, metrics.Count(AgentOperation.Run)); Assert.Equal(2, metrics.Count(AgentOperation.Model)); Assert.Equal(1, tool.Calls);
        Assert.Equal(id, Assert.Single(restarted.Services.GetRequiredService<RunDiagnosticsService>().Snapshot()).RunId);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task ThrowingMetricListenerCannotFailAnActualModelOrToolOperation()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) => { if (instrument.Meter.Name == AgentTelemetry.MeterName) observer.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException(Private)); listener.Start();
        var tool = new ReadTool();
        var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
        using var client = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("fixture-call", tool.Descriptor.Id, new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls },
            (_, _) => new(new ChatMessage(ChatRole.Assistant, "Completed")) { FinishReason = ChatFinishReason.Stop });
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, registry);
        var result = await runtime.ExecuteAsync(new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = Private,
            ModelProfileId = "fake", AllowedToolIds = [tool.Descriptor.Id] }, new NoProgress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Equal(1, tool.Calls); Assert.Equal(2, client.CallCount);
    }

    [Theory]
    [InlineData(RunStatus.Failed, "ToolOutcomeUnknown", RunDiagnosticCode.OutcomeUnknown)]
    [InlineData(RunStatus.Failed, "PostActionObservationFailed", RunDiagnosticCode.OutcomeUnknown)]
    [InlineData(RunStatus.Cancelled, "InputCleanupUncertain", RunDiagnosticCode.InputCleanupUncertain)]
    [InlineData(RunStatus.Failed, "ProviderAuthenticationFailed", RunDiagnosticCode.ModelConfiguration)]
    [InlineData(RunStatus.Blocked, "ToolPermissionDenied", RunDiagnosticCode.PermissionDenied)]
    [InlineData(RunStatus.Failed, "TokenLimitExceeded", RunDiagnosticCode.LimitExceeded)]
    [InlineData(RunStatus.Skipped, "DeferredExpired", RunDiagnosticCode.DeferredExpired)]
    [InlineData(RunStatus.Interrupted, null, RunDiagnosticCode.Interrupted)]
    [InlineData(RunStatus.WaitingForApproval, null, RunDiagnosticCode.WaitingForApproval)]
    [InlineData(RunStatus.WaitingForDesktop, null, RunDiagnosticCode.WaitingForDesktop)]
    [InlineData(RunStatus.PausedForUser, "AutoPause", RunDiagnosticCode.UserActivity)]
    [InlineData(RunStatus.PausedForUser, "DesktopEnvironmentChanged", RunDiagnosticCode.DesktopChanged)]
    [InlineData(RunStatus.Failed, Private, RunDiagnosticCode.Failure)]
    public void DiagnosticClassificationUsesFixedSafeLabelsAndManualReviewAdvice(RunStatus status, string? reason, RunDiagnosticCode expected)
    {
        var run = new TaskRun { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = Private, Summary = Private, ModelProfileId = Private,
            Origin = new(Private), CreatedAtUtc = DateTimeOffset.UtcNow, Status = status, ErrorCode = reason, Reason = reason,
            PauseScope = status == RunStatus.PausedForUser ? PauseScope.EntireRun : null };
        var row = RunDiagnosticsService.Describe(run, [new() { Id = Guid.NewGuid(), RunId = run.Id, Sequence = 1, EventType = RunEventType.Progress,
            TimestampUtc = DateTimeOffset.UtcNow, Message = Private, ToolId = Private, Payload = JsonSerializer.SerializeToElement(new { body = Private }) }], new(null, 100), null);
        Assert.Equal(expected, row.Code); Assert.DoesNotContain(Private, JsonSerializer.Serialize(row));
        Assert.Contains("unknown", row.Usage); Assert.Null(row.Duration); Assert.Null(row.DesktopLeaseGeneration);
        if (expected is RunDiagnosticCode.OutcomeUnknown or RunDiagnosticCode.Interrupted) Assert.Contains("Do not replay", row.NextStep);
        if (expected == RunDiagnosticCode.InputCleanupUncertain) Assert.Contains("Resume cannot clear", row.NextStep);
    }

    [Fact]
    public void FormatterKeepsOnlyTypedStatusCodesAndHashesArbitraryIdentifiers()
    {
        var fields = new Dictionary<string, object?> { ["ToolId"] = Private, ["ProviderId"] = Private, ["Status"] = RunStatus.Failed,
            ["ErrorCode"] = RunDiagnosticCode.OutcomeUnknown, ["DesktopLeaseGeneration"] = 42L, ["ToolCallId"] = Private };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Warning, "PersonalAgent.App", new(2100), fields,
            new Exception(Private), (_, _) => Private);
        using var output = new System.IO.StringWriter(); new PrivacyConsoleFormatter().Write(entry, null, output);
        Assert.DoesNotContain(Private, output.ToString()); Assert.Contains("Status=Failed", output.ToString());
        Assert.Contains("ErrorCode=OutcomeUnknown", output.ToString()); Assert.Contains("DesktopLeaseGeneration=42", output.ToString());
        fields["Status"] = Private; fields["ErrorCode"] = Private;
        using var unknown = new System.IO.StringWriter(); new PrivacyConsoleFormatter().Write(entry, null, unknown);
        Assert.DoesNotContain("Status=", unknown.ToString()); Assert.DoesNotContain("ErrorCode=", unknown.ToString());
    }

    private sealed class ReadTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(Private, Private, JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            new(ToolSourceKind.Mcp, "fixture"), ToolRiskLevel.ReadOnly, IsReadOnly: true, IndependentOfDesktop: true);
        public int Calls;
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new ToolResult { Status = ToolResultStatus.Success, Text = Private }); }
    }
    private sealed class NoProgress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class PauseRuntime(TaskRunCoordinator runs) : IAgentRuntime
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct); await runs.WaitAtBoundaryAsync(request.RunId, ct);
            return new() { Status = AgentResultStatus.Failed, Summary = Private, ErrorCode = Private };
        }
    }

    private sealed record Measurement(string Name, double Value, string Tags, int ThreadId);
    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener listener = new();
        public readonly ConcurrentBag<Measurement> Measurements = [];
        public Metrics()
        {
            listener.InstrumentPublished = (instrument, observer) => { if (instrument.Meter.Name == AgentTelemetry.MeterName) observer.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measurements.Add(new(instrument.Name, value, Tags(tags), Environment.CurrentManagedThreadId)));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Measurements.Add(new(instrument.Name, value, Tags(tags), Environment.CurrentManagedThreadId)));
            listener.Start();
        }
        private static string Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags) => string.Join(";", tags.ToArray().Select(pair => pair.Key + "=" + pair.Value));
        public double Count(AgentOperation operation) => Measurements.Where(item => item.Name == "personalagent.operations" && item.Tags.Contains("operation=" + operation + ";")).Sum(item => item.Value);
        public double Total(string name) => Measurements.Where(item => item.Name == name).Sum(item => item.Value);
        public void Dispose() => listener.Dispose();
    }
    private sealed class PrivateLogs : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<string> Lines = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry<TState>(level, "PersonalAgent.App", id, state, exception, formatter);
            using var output = new System.IO.StringWriter(); new PrivacyConsoleFormatter().Write(entry, null, output); Lines.Enqueue(output.ToString());
        }
        public void Dispose() { }
    }
}
