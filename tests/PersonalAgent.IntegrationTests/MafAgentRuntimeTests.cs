using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using System.Runtime.CompilerServices;

namespace PersonalAgent.IntegrationTests;

public sealed class MafAgentRuntimeTests
{
    [Fact]
    public async Task StreamsOnlyPublicAssistantOutputAndMapsConfirmedFinalUsage()
    {
        using var client = new StreamClient((messages, options, ct) => Updates(
            new(ChatRole.Assistant, [new TextReasoningContent("hidden reasoning token")]) { MessageId = "a" },
            new(ChatRole.Tool, "private tool payload") { MessageId = "tool" },
            new(ChatRole.Assistant, "Hello ") { MessageId = "answer" },
            new(ChatRole.Assistant, "world") { MessageId = "answer", FinishReason = ChatFinishReason.Stop,
                Contents = [new TextContent("world"), new UsageContent(new() { InputTokenCount = 12, OutputTokenCount = 3 })] }));
        var events = new Events();
        IAgentRuntime runtime = Runtime(client);
        var result = await runtime.ExecuteAsync(Request() with { Context = new("prior untrusted summary") }, events, CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Equal("Hello world", result.Summary);
        Assert.Equal(new AgentUsage(12, 3), result.Usage);
        Assert.Equal(1, client.Calls); Assert.Equal(1, client.Disposals);
        Assert.DoesNotContain(events.Items, e => e.Message.Contains("hidden reasoning") || e.Message.Contains("private tool"));
        Assert.Contains(events.Items, e => e.Kind == AgentEventKind.Progress && e.Message == "Hello ");
        Assert.Equal(AgentEventKind.Started, events.Items[0].Kind); Assert.Equal(AgentEventKind.Completed, events.Items[^1].Kind);
        Assert.All(client.Messages, message => Assert.Equal(ChatRole.User, message.Role));
        Assert.Contains(client.Messages, message => message.Text.Contains("prior untrusted summary"));
        Assert.Empty(client.Options!.Tools!); Assert.Equal(ChatToolMode.None, client.Options.ToolMode);
    }

    [Theory]
    [InlineData("length")] [InlineData("filter")] [InlineData("missing")] [InlineData("empty")]
    public async Task PartialFilteredUnconfirmedOrEmptyResponseCannotBecomeCompleted(string mode)
    {
        var finish = mode switch { "length" => ChatFinishReason.Length, "filter" => ChatFinishReason.ContentFilter, "missing" => (ChatFinishReason?)null, _ => ChatFinishReason.Stop };
        using var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, mode == "empty" ? "" : "partial") { FinishReason = finish }));
        var result = await Runtime(client).ExecuteAsync(Request(), new Events(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.NotNull(result.ErrorCode);
        Assert.Equal(1, client.Calls); Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task MidStreamExceptionIsSanitizedAndNeverRetriedOrReportedAsSuccess()
    {
        using var client = new StreamClient((_, _, ct) => Failing(ct));
        var result = await Runtime(client).ExecuteAsync(Request(), new Events(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Failed, result.Status); Assert.Equal("ModelExecutionFailed", result.ErrorCode);
        Assert.DoesNotContain("secret-key", result.Summary); Assert.NotEmpty(result.Warnings);
        Assert.Equal(1, client.Calls); Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task FactoryFailureIsSanitizedWithoutRetry()
    {
        var calls = 0;
        var runtime = new MafAgentRuntime((_, _) => { calls++; throw new InvalidOperationException("secret factory token"); }, (_, _) => Task.CompletedTask);
        var result = await runtime.ExecuteAsync(Request(), new Events(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Failed, result.Status); Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", result.Summary); Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task DurationLimitCancelsInFlightStreamWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new StreamClient((_, _, ct) => Waiting(entered, ct));
        var request = Request() with { Limits = new() { MaxDuration = TimeSpan.FromMilliseconds(300) } };
        var result = await Runtime(client).ExecuteAsync(request, new Events(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentResultStatus.Failed, result.Status); Assert.Equal("RunTimedOut", result.ErrorCode);
        Assert.InRange(client.Calls, 0, 1); Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task CancellationAfterLastChunkCannotPublishCompleted()
    {
        using var stop = new CancellationTokenSource();
        using var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, "late final") { FinishReason = ChatFinishReason.Stop }));
        var events = new Events(e => { if (e.Kind == AgentEventKind.Progress) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runtime(client).ExecuteAsync(Request(), events, stop.Token));
        Assert.DoesNotContain(events.Items, e => e.Kind == AgentEventKind.Completed);
        Assert.Equal(1, client.Calls); Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task UnconfiguredAllowedToolsBlockBeforeClientCreationAndUnsolicitedCallsNeverInvokeAnything()
    {
        var factories = 0;
        var runtime = new MafAgentRuntime((_, _) => { factories++; throw new InvalidOperationException("must not create"); }, (_, _) => Task.CompletedTask);
        var rejected = await runtime.ExecuteAsync(Request() with { AllowedToolIds = ["unconfigured_write"] }, new Events(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, rejected.Status); Assert.Equal(0, factories);
        using var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("untrusted-call", "unconfigured_write", new Dictionary<string, object?>())]) { FinishReason = ChatFinishReason.ToolCalls }));
        var response = await Runtime(client).ExecuteAsync(Request(), new Events(), CancellationToken.None);
        Assert.Equal("ToolRegistryUnavailable", response.ErrorCode); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task PreCancelledRequestCannotCreateClientAndCancellationDuringStreamDisposesIt()
    {
        using var pre = new CancellationTokenSource(); pre.Cancel(); var factories = 0;
        var runtime = new MafAgentRuntime((_, _) => { factories++; throw new InvalidOperationException(); }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(Request(), new Events(), pre.Token));
        Assert.Equal(0, factories);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new StreamClient((_, _, ct) => Waiting(entered, ct));
        var executing = Runtime(client).ExecuteAsync(Request(), new Events(), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        Assert.Equal(1, client.Calls); Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task PauseBoundaryStopsPublishingNextUpdateUntilExplicitResume()
    {
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = false;
        using var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, "first") { MessageId = "a" },
            new(ChatRole.Assistant, "second") { MessageId = "a", FinishReason = ChatFinishReason.Stop }));
        var events = new Events(e => { if (e.Kind == AgentEventKind.Progress) pause = true; });
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), async (_, ct) =>
        { if (pause) { paused.TrySetResult(); await resume.Task.WaitAsync(ct); } });
        var executing = runtime.ExecuteAsync(Request(), events, CancellationToken.None);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(executing.IsCompleted); Assert.DoesNotContain(events.Items, e => e.Message == "second");
        resume.SetResult(); Assert.Equal(AgentResultStatus.Completed, (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RealHostUsesAdapterThroughExistingRuntimeContractAndDurablyStoresItsResult()
    {
        using var database = new Fakes.TemporaryAgentDatabase();
        var created = new List<StreamClient>();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => new MafAgentRuntime((request, ct) =>
            {
                var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, "MAF local provider result") { FinishReason = ChatFinishReason.Stop }));
                created.Add(client); return ValueTask.FromResult<IChatClient>(client);
            }, provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync))).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Scripted provider", new("test"), ModelProfileId: "test-maf"));
            var final = await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunStatus.Completed, final.Status); Assert.Equal("MAF local provider result", final.Summary);
            var stored = await host.Services.GetRequiredService<PersonalAgent.Core.Persistence.IRunRepository>().GetAsync(run.Id);
            Assert.Equal(final.Summary, stored!.Result!.Summary);
            Assert.IsType<MafAgentRuntime>(host.Services.GetRequiredService<IAgentRuntime>());
            Assert.Single(created); Assert.Equal(1, created[0].Disposals);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task SeparateExecutionsDoNotReuseSdkSessionHistoryOrClient()
    {
        var clients = new List<StreamClient>();
        var runtime = new MafAgentRuntime((_, _) =>
        {
            var client = new StreamClient((_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop }));
            clients.Add(client); return ValueTask.FromResult<IChatClient>(client);
        }, (_, _) => Task.CompletedTask);
        var first = Request();
        await runtime.ExecuteAsync(first with { Instructions = "first private request" }, new Events(), CancellationToken.None);
        await runtime.ExecuteAsync(first with { RunId = Guid.NewGuid(), Instructions = "second request" }, new Events(), CancellationToken.None);
        Assert.Equal(2, clients.Count); Assert.All(clients, client => Assert.Equal(1, client.Disposals));
        Assert.DoesNotContain(clients[1].Messages, message => message.Text.Contains("first private request"));
    }

    private static MafAgentRuntime Runtime(IChatClient client) => new((_, _) => ValueTask.FromResult(client), (_, _) => Task.CompletedTask);
    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Public request", ModelProfileId = "local-scripted-maf" };
    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(params ChatResponseUpdate[] updates)
    { foreach (var update in updates) { await Task.Yield(); yield return update; } }
    private static async IAsyncEnumerable<ChatResponseUpdate> Failing([EnumeratorCancellation] CancellationToken ct)
    { yield return new(ChatRole.Assistant, "partial"); await Task.Yield(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("secret-key provider detail"); }
    private static async IAsyncEnumerable<ChatResponseUpdate> Waiting(TaskCompletionSource entered, [EnumeratorCancellation] CancellationToken ct)
    { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); yield break; }
    private sealed class Events(Action<AgentEvent>? onEvent = null) : IProgress<AgentEvent>
    {
        public List<AgentEvent> Items { get; } = [];
        public void Report(AgentEvent value) { Items.Add(value); onEvent?.Invoke(value); }
    }
    private sealed class StreamClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream) : IChatClient
    {
        public int Calls, Disposals;
        public IReadOnlyList<ChatMessage> Messages = [];
        public ChatOptions? Options;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming only.");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Messages = messages.ToArray(); Options = options; return stream(Messages, options, cancellationToken); }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("stream-local-test") : serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { Disposals++; }
    }
}
