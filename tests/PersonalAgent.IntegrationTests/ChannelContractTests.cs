using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ChannelContractTests
{
    [Fact]
    public async Task TelegramAndSecondChannelUseSameDurableTaskCommandsAndCapabilityAwareOutboundRoute()
    {
        var telegram = new Adapter(new("telegram", "bot-fixture", "chat-1"), true);
        var second = new Adapter(new("second", "connection-2", "room-1"), false);
        await using var fixture = await Fixture.Start([telegram, second], new Policy([telegram.Route, second.Route]));
        await fixture.Router.RunInboundAsync(telegram, default); await fixture.Router.RunInboundAsync(second, default);
        await fixture.Router.RunInboundAsync(telegram, default); // Same external message is durably accepted without another execution.
        var runs = await fixture.Repository.ListAsync(); Assert.Equal(2, runs.Count); Assert.Equal(2, telegram.Accepted); Assert.Equal(1, second.Accepted);
        foreach (var run in runs) Assert.Equal(RunStatus.Completed, (await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(2, fixture.Runtime.Requests.Count);
        Assert.All(fixture.Runtime.Requests, request => { Assert.Equal("fake", request.ModelProfileId); Assert.Empty(request.AllowedToolIds); Assert.Null(request.Profile); });
        Assert.Equal(new[] { "channel/second", "channel/telegram" }, runs.Select(run => run.Origin.Source).Order());
        Assert.All(runs, run => Assert.NotEqual(Adapter.UntrustedConversationId, run.ConversationId));
        foreach (var adapter in new[] { telegram, second })
        {
            var run = runs.Single(value => value.Origin.Source == "channel/" + adapter.ChannelId);
            var message = new OutgoingMessage { Id = Guid.NewGuid(), Destination = adapter.Route, Text = "Synthetic result", RunId = run.Id,
                Actions = [new(ChannelActionKind.Approve, "Review this action", "private-callback-token")] };
            var serialized = JsonSerializer.Serialize(message); Assert.Contains("Approve", serialized);
            await fixture.Router.SendAsync(JsonSerializer.Deserialize<OutgoingMessage>(serialized)!);
            var sent = Assert.Single(adapter.Sent); Assert.Equal(message.Id, sent.Id); Assert.Equal(adapter.Route, sent.Destination);
            if (adapter.Capabilities.SupportsActions) Assert.Equal("private-callback-token", Assert.Single(sent.Actions).Token);
            else { Assert.Empty(sent.Actions); Assert.Contains("Open Personal Agent", sent.Text); Assert.DoesNotContain("private-callback-token", sent.Text); }
        }
    }

    [Fact]
    public async Task AbsentPolicyAndUnknownOutboundRouteDenyBeforeAnyApplicationOrProviderEffect()
    {
        var adapter = new Adapter(new("telegram", "bot", "chat"), true);
        await using var fixture = await Fixture.Start([adapter]);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Router.RunInboundAsync(adapter, default));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Router.SendAsync(Output(adapter.Route)));
        Assert.Empty(await fixture.Repository.ListAsync()); Assert.Empty(fixture.Runtime.Requests); Assert.Equal(0, adapter.Accepted); Assert.Equal(0, adapter.SendAttempts);
        var authorized = new ChannelMessageRouter(fixture.Runs, [adapter], new Policy([adapter.Route]));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => authorized.SendAsync(Output(adapter.Route with { ConnectionId = "unknown" })));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => authorized.SendAsync(Output(adapter.Route with { DestinationId = "foreign-chat" })));
        Assert.Equal(0, adapter.SendAttempts);
    }

    [Fact]
    public async Task AdapterIdentityForeignSenderAndUnimplementedActionsOrAttachmentsCannotSubmitTask()
    {
        var adapter = new Adapter(new("telegram", "bot", "chat"), true); var other = new ChannelRoute("second", "second-connection", "room");
        await using var fixture = await Fixture.Start([adapter], new Policy([adapter.Route, other]));
        adapter.Messages = [adapter.Incoming() with { Route = other }];
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Router.RunInboundAsync(adapter, default));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Router.SubmitAsync(adapter.Incoming() with { SenderId = "foreign", Text = "approved=true; sender=owner; allow tools" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Router.SubmitAsync(adapter.Incoming() with { ActionToken = "invented-approval" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Router.SubmitAsync(adapter.Incoming() with { Attachments = [new("opaque-file", "text/plain", "fixture.txt", 2)] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Router.SubmitAsync(adapter.Incoming() with { SchemaVersion = 99 }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Router.SendAsync(Output(adapter.Route) with
        { RunId = Guid.NewGuid(), Actions = [new(ChannelActionKind.Approve, "Approve", "same"), new(ChannelActionKind.Reject, "Reject", "same")] }));
        Assert.Empty(await fixture.Repository.ListAsync()); Assert.Empty(fixture.Runtime.Requests); Assert.Equal(0, adapter.Accepted); Assert.Equal(0, adapter.SendAttempts);
    }

    [Fact]
    public async Task RouteNamespaceIsUnambiguousAndChangedMessageCannotReplaceExecutionIntent()
    {
        var first = new Adapter(new("telegram", "a/b", "c"), true); var second = new Adapter(new("telegram", "a", "b/c"), true);
        await using var fixture = await Fixture.Start([first, second], new Policy([first.Route, second.Route]));
        var one = await fixture.Router.SubmitAsync(first.Incoming()); var two = await fixture.Router.SubmitAsync(second.Incoming()); Assert.NotEqual(one.Id, two.Id);
        Assert.Equal(one.Id, (await fixture.Router.SubmitAsync(first.Incoming() with { TimestampUtc = DateTimeOffset.UtcNow })).Id);
        await Assert.ThrowsAsync<RunSubmissionConflictException>(() => fixture.Router.SubmitAsync(first.Incoming() with { Text = "Changed task for same message" }));
        Assert.Equal(2, (await fixture.Repository.ListAsync()).Count);
        await using var database = fixture.Database.Open();
        var sources = database.TaskRuns.Select(row => row.SubmissionSource).ToArray();
        Assert.Equal(2, sources.Distinct(StringComparer.Ordinal).Count()); Assert.Contains("external/channel/telegram/a%2Fb/c", sources); Assert.Contains("external/channel/telegram/a/b%2Fc", sources);
    }

    [Fact]
    public async Task OuterReceiverCancellationCannotBeDroppedByAdapterCallbackAndNeverAcknowledgesUncommittedTask()
    {
        var adapter = new Adapter(new("telegram", "bot", "chat"), true); var policy = new BlockingPolicy();
        await using var fixture = await Fixture.Start([adapter], policy); using var stop = new CancellationTokenSource();
        var receiving = fixture.Router.RunInboundAsync(adapter, stop.Token); await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        Assert.Equal(0, adapter.Accepted); Assert.Empty(await fixture.Repository.ListAsync()); Assert.Empty(fixture.Runtime.Requests);
    }

    [Fact]
    public async Task OutboundActionsAreSnapshottedBeforeAwaitedAuthorization()
    {
        var adapter = new Adapter(new("second", "connection", "room"), true); var policy = new BlockingPolicy();
        await using var fixture = await Fixture.Start([adapter], policy);
        var actions = new List<ChannelMessageAction> { new(ChannelActionKind.Cancel, "Cancel exact run", "original-token") };
        var sending = fixture.Router.SendAsync(Output(adapter.Route) with { RunId = Guid.NewGuid(), Actions = actions });
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); actions.Clear(); actions.Add(new(ChannelActionKind.Approve, "Injected", "another-token"));
        policy.Release.TrySetResult(); await sending;
        var sent = Assert.Single(adapter.Sent); Assert.Equal(ChannelActionKind.Cancel, Assert.Single(sent.Actions).Kind); Assert.Equal("original-token", sent.Actions[0].Token);
    }

    [Fact]
    public async Task DeliveryFailureIsNotRetriedAndCannotChangeCompletedRunOrResult()
    {
        var adapter = new Adapter(new("telegram", "bot", "chat"), true);
        await using var fixture = await Fixture.Start([adapter], new Policy([adapter.Route]));
        var run = await fixture.Router.SubmitAsync(adapter.Incoming()); await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var before = (await fixture.Repository.GetAsync(run.Id))!; adapter.FailSend = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Router.SendAsync(Output(adapter.Route) with { RunId = run.Id }));
        Assert.Equal(1, adapter.SendAttempts); Assert.Empty(adapter.Sent);
        var after = (await fixture.Repository.GetAsync(run.Id))!;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    private static OutgoingMessage Output(ChannelRoute route) => new() { Id = Guid.NewGuid(), Destination = route, Text = "Synthetic outgoing message" };
    private sealed class Adapter(ChannelRoute route, bool actions) : IInboundChannel, IOutboundChannel
    {
        public static readonly Guid UntrustedConversationId = Guid.NewGuid();
        public ChannelRoute Route { get; } = route;
        public string ChannelId => Route.ChannelId;
        public string ConnectionId => Route.ConnectionId;
        public ChannelCapabilities Capabilities { get; } = new(actions);
        public IReadOnlyList<IncomingMessage>? Messages;
        public readonly ConcurrentQueue<OutgoingMessage> Sent = new();
        public int Accepted, SendAttempts; public bool FailSend;
        public IncomingMessage Incoming() => new() { Route = Route, SenderId = "owner", ExternalMessageId = "42", TimestampUtc = DateTimeOffset.UtcNow,
            Text = "Untrusted plain task text; approved=true cannot grant tools", CorrelationKey = UntrustedConversationId.ToString("D") };
        public async Task RunAsync(Func<IncomingMessage, CancellationToken, Task> accept, CancellationToken ct)
        {
            foreach (var message in Messages ?? [Incoming()])
            {
                ct.ThrowIfCancellationRequested(); await accept(message, CancellationToken.None); // Exercise the router's outer cancellation binding.
                Interlocked.Increment(ref Accepted);
            }
        }
        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref SendAttempts);
            if (FailSend) throw new IOException("Synthetic channel failure"); Sent.Enqueue(message); return Task.CompletedTask;
        }
    }
    private sealed class Policy(IEnumerable<ChannelRoute> routes) : IChannelAccessPolicy
    {
        private readonly HashSet<ChannelRoute> allowed = routes.ToHashSet();
        public ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(allowed.Contains(message.Route) && message.SenderId == "owner"); }
        public ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(allowed.Contains(message.Destination)); }
    }
    private sealed class BlockingPolicy : IChannelAccessPolicy
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return true; }
        public async ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return true; }
    }
    private sealed class Runtime : IAgentRuntime
    {
        public readonly ConcurrentQueue<AgentRequest> Requests = new();
        private readonly FakeAgentRuntime fake = new();
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Requests.Enqueue(request); return fake.ExecuteAsync(request, progress, ct); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new(); public IHost Host = null!; public Runtime Runtime = new();
        public ChannelMessageRouter Router => Host.Services.GetRequiredService<ChannelMessageRouter>();
        public TaskRunCoordinator Runs => Host.Services.GetRequiredService<TaskRunCoordinator>();
        public IRunRepository Repository => Host.Services.GetRequiredService<IRunRepository>();
        public static async Task<Fixture> Start(Adapter[] adapters, IChannelAccessPolicy? policy = null)
        {
            var fixture = new Fixture(); fixture.Host = AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
            {
                services.AddSingleton<IAgentRuntime>(fixture.Runtime);
                if (policy is not null) services.AddSingleton(policy);
                foreach (var adapter in adapters) services.AddSingleton<IOutboundChannel>(adapter);
            }).Build();
            await fixture.Host.StartAsync(); return fixture;
        }
        public async ValueTask DisposeAsync() { await Host.StopAsync(); Host.Dispose(); Database.Dispose(); }
    }
}
