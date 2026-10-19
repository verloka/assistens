using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

// Exercises the pinned MAF pipeline, application dispatch and real SQLite, without network or desktop input.
public sealed class AgentContractTests
{
    [Fact]
    public async Task DeterministicToolSequencePersistsCorrelatedProgressAndFinalResult()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var run = await fixture.SubmitAsync();
        var final = await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunStatus.Completed, final.Status);
        var stored = await fixture.Repository.GetAsync(run.Id);
        Assert.Equal("Read first and second", stored!.Result!.Summary);
        Assert.Equal(new AgentUsage(3, 3), stored.Result.Usage);
        Assert.Equal(new[] { "first", "second" }, fixture.Tool.Reads);
        Assert.Equal(3, fixture.Client.CallCount);
        var requested = stored.Events.Where(e => e.EventType == RunEventType.ToolRequested).ToArray();
        var completed = stored.Events.Where(e => e.EventType == RunEventType.ToolExecuted).ToArray();
        Assert.Equal(2, requested.Length);
        Assert.Equal(requested.Select(e => e.ToolCallId), completed.Select(e => e.ToolCallId));
        Assert.All(completed, e => Assert.Equal("local_read", e.ToolId));
        Assert.Single(stored.Events, e => e.EventType == RunEventType.RunFinished);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("extra")]
    public async Task MalformedSecondCallBlocksWithoutEffectRetryOrFabricatedSuccess(string invalid)
    {
        object arguments = invalid switch
        {
            "missing" => new { },
            "type" => new { item = 42 },
            _ => new { item = "second", unexpected = true }
        };
        await using var fixture = new Fixture(arguments);
        await fixture.StartAsync();
        var run = await fixture.SubmitAsync();
        await WaitForStatusAsync(fixture.Runs, run.Id, RunStatus.Blocked);
        var stored = await fixture.Repository.GetAsync(run.Id);
        Assert.Equal("InvalidToolArguments", stored!.Result!.ErrorCode);
        Assert.Equal(AgentResultStatus.Blocked, stored.Result.Status);
        Assert.Equal(new[] { "first" }, fixture.Tool.Reads);
        Assert.Equal(2, fixture.Client.CallCount);
        Assert.DoesNotContain(stored.Events, e => e.Message == "Model execution completed.");
        Assert.Single(stored.Events, e => e.EventType == RunEventType.ToolExecuted);
    }

    [Fact]
    public async Task TimeoutDuringSecondToolCancelsItAndPersistsFailureWithoutNextModelCall()
    {
        await using var fixture = new Fixture();
        var entered = Signal();
        var cancelled = Signal();
        fixture.Tool.BeforeRead = async (item, ct) =>
        {
            if (item != "second") return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (ct.IsCancellationRequested) cancelled.TrySetResult(); }
        };
        await fixture.StartAsync();
        var run = await fixture.SubmitAsync(new() { MaxDuration = TimeSpan.FromSeconds(2) });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var final = await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunStatus.Failed, final.Status);
        Assert.Equal("RunTimedOut", final.ErrorCode);
        Assert.Equal(new[] { "first" }, fixture.Tool.Reads);
        Assert.Equal(2, fixture.Client.CallCount);
        Assert.Equal("RunTimedOut", (await fixture.Repository.GetAsync(run.Id))!.Result!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseBeforeSecondToolRequiresExplicitResumeOrCancelsWithoutContinuing(bool cancel)
    {
        await using var fixture = new Fixture();
        fixture.HoldSecondBoundary = true;
        await fixture.StartAsync();
        var run = await fixture.SubmitAsync();
        await fixture.SecondBoundary.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pausing = await fixture.Runs.PauseAsync(new(Guid.NewGuid(), run.Id, fixture.Runs.GetRun(run.Id).RunVersion));
        Assert.Equal(RunStatus.Pausing, pausing.Status);
        fixture.ReleaseBoundary.TrySetResult();
        await WaitForStatusAsync(fixture.Runs, run.Id, RunStatus.Paused);
        Assert.Equal(new[] { "first" }, fixture.Tool.Reads);
        Assert.Equal(2, fixture.Client.CallCount);
        Assert.Equal(RunStatus.Paused, (await fixture.Repository.GetAsync(run.Id))!.Run.Status);
        var paused = fixture.Runs.GetRun(run.Id);
        if (cancel) await fixture.Runs.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        else await fixture.Runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        var final = await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(cancel ? RunStatus.Cancelled : RunStatus.Completed, final.Status);
        Assert.Equal(cancel ? new[] { "first" } : new[] { "first", "second" }, fixture.Tool.Reads);
        Assert.Equal(cancel ? 2 : 3, fixture.Client.CallCount);
        Assert.Equal(final.Status, (await fixture.Repository.GetAsync(run.Id))!.Run.Status);
        Assert.Single(fixture.Runs.GetEvents(run.Id), e => e.EventType == RunEventType.RunStarted);
    }

    private static async Task WaitForStatusAsync(TaskRunCoordinator runs, Guid runId, RunStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in runs.WatchRunAsync(runId, timeout.Token))
            if (runs.GetRun(runId).Status == status) return;
        throw new InvalidOperationException($"Run ended without reaching {status}.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ChatResponse Call(string id, object arguments) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(id, "local_read", JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone()))])) { FinishReason = ChatFinishReason.ToolCalls };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryAgentDatabase database = new();
        private readonly IHost host;
        public ReadTool Tool { get; } = new();
        public ScriptedChatClient Client { get; }
        public TaskRunCoordinator Runs => host.Services.GetRequiredService<TaskRunCoordinator>();
        public IRunRepository Repository => host.Services.GetRequiredService<IRunRepository>();
        public bool HoldSecondBoundary;
        public TaskCompletionSource SecondBoundary { get; } = Signal();
        public TaskCompletionSource ReleaseBoundary { get; } = Signal();

        public Fixture(object? secondArguments = null)
        {
            Client = new((_, options) =>
            {
                Assert.Equal("local_read", Assert.Single(options!.Tools!).Name);
                Assert.All(options.Tools!, tool => Assert.False(tool is AIFunction));
                return Call("read-first", new { item = "first" });
            }, (messages, _) =>
            {
                Assert.Equal("read-first", Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()).CallId);
                return Call("read-second", secondArguments ?? new { item = "second" });
            }, (messages, _) =>
            {
                Assert.Equal(new[] { "read-first", "read-second" }, messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId));
                return new(new ChatMessage(ChatRole.Assistant, "Read first and second")) { FinishReason = ChatFinishReason.Stop };
            });
            host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
                .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider =>
                {
                    var runs = provider.GetRequiredService<TaskRunCoordinator>();
                    var boundaries = 0;
                    var registry = new ToolRegistry([Tool], async (id, access, ct) =>
                    {
                        if (Interlocked.Increment(ref boundaries) == 2 && HoldSecondBoundary)
                        {
                            SecondBoundary.TrySetResult();
                            await ReleaseBoundary.Task.WaitAsync(ct);
                        }
                        await runs.WaitAtBoundaryAsync(id, access, ct);
                    });
                    return new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(Client), runs.WaitAtBoundaryAsync, registry);
                })).Build();
        }
        public Task StartAsync() => host.StartAsync();
        public Task<TaskRun> SubmitAsync(RunLimits? limits = null) => Runs.SubmitAsync(new(Guid.NewGuid(), "Read synthetic records",
            new("contract-test"), ModelProfileId: "scripted", AllowedToolIds: ["local_read"], Limits: limits));
        public async ValueTask DisposeAsync()
        {
            ReleaseBoundary.TrySetResult();
            await host.StopAsync();
            host.Dispose();
            database.Dispose();
        }
    }

    private sealed class ReadTool : IAgentTool
    {
        public List<string> Reads { get; } = [];
        public Func<string, CancellationToken, Task>? BeforeRead;
        public ToolDescriptor Descriptor { get; } = new("local_read", "Read an in-memory test record",
            JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "item" }, additionalProperties = false,
                properties = new { item = new { type = "string" } } }),
            new(ToolSourceKind.Local, "contract-test"), ToolRiskLevel.ReadOnly, IsReadOnly: true, IndependentOfDesktop: true);
        public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
        {
            var item = arguments.Value.GetProperty("item").GetString()!;
            if (BeforeRead is not null) await BeforeRead(item, ct);
            ct.ThrowIfCancellationRequested();
            Reads.Add(item);
            return new() { Status = ToolResultStatus.Success, Text = "Observed " + item };
        }
    }
}
