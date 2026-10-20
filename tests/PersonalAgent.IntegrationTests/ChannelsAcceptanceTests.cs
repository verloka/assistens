using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ChannelsAcceptanceTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "channels-acceptance-owner");
    private static readonly ChannelRoute DiscordRoute = new("discord", "fake-discord", "room-1");

    [Fact]
    public async Task ChannelsE2E09DiscordAdapterRegistrationRunsCommonTaskAndDeliversBothDestinationsAcrossRestart()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new HumanApprovalTests.TestClock(); var calls = new ConcurrentBag<AgentRequest>(); var discord = new DiscordAdapter(); var http = new HttpState { FailFirstSend = true };
        var configurations = new SqliteChannelConfigurationRepository(database); var id = Guid.NewGuid(); var telegram = new ChannelRoute("telegram", id.ToString("N"), "24");
        var reference = await new WindowsDpapiSecretStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.DatabasePath)!, "secrets")).StoreAsync("12345:synthetic-channels-token");
        await configurations.SaveAsync(new(id, Owner, "Scripted Telegram", telegram, "42", true, false, true, reference, [NotificationEventKind.Completed], clock.Now.AddMinutes(-1), RemoteIdentity: "777"), 0);
        await configurations.SaveAsync(new(Guid.NewGuid(), Owner, "Fake Discord", DiscordRoute, "discord-owner", true, true, true, null, [NotificationEventKind.Completed], clock.Now.AddMinutes(-1)), 0);
        await Allow(database, Owner, telegram, "42"); await Allow(database, Owner, DiscordRoute, "discord-owner");
        Guid runId; string stored;
        using (var host = Host(database, discord, http, calls, clock))
        {
            await host.StartAsync(); var router = host.Services.GetRequiredService<ChannelMessageRouter>();
            await router.RunInboundAsync(discord, default); // Fake transport supplies the same update twice.
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); runId = Assert.Single(runs.GetRuns()).Id;
            await runs.WaitForTerminalAsync(runId).WaitAsync(TimeSpan.FromSeconds(10));
            await Until(async () => { await using var db = database.Open(); return await db.NotificationOutbox.CountAsync() == 2 && await db.NotificationOutbox.AnyAsync(row => row.Status == NotificationDeliveryStatus.Pending && row.Attempts == 1) && discord.Sent.Count == 1; });
            Assert.Equal("channel/discord", runs.GetRun(runId).Origin.Source); Assert.NotEqual(DiscordAdapter.UntrustedConversation, runs.GetRun(runId).ConversationId.ToString());
            Assert.Single(calls); Assert.Empty(calls.Single().AllowedToolIds); Assert.Equal("fake", calls.Single().ModelProfileId);
            Assert.Equal(1, http.SendAttempts); Assert.Empty(http.Sent); Assert.StartsWith("Completed", discord.Sent.Single().Text);
            await using (var db = database.Open()) clock.Now = (await db.NotificationOutbox.SingleAsync(row => row.ChannelId == "telegram")).NextAttemptAtUtc;
            await host.Services.GetRequiredService<NotificationDispatcher>().TickAsync(); Assert.Single(http.Sent);
            stored = JsonSerializer.Serialize(await host.Services.GetRequiredService<IRunRepository>().GetAsync(runId)); await host.StopAsync();
        }
        using (var host = Host(database, discord, http, calls, clock))
        {
            await host.StartAsync(); await host.Services.GetRequiredService<ChannelMessageRouter>().RunInboundAsync(discord, default);
            await host.Services.GetRequiredService<RunNotificationPublisher>().TickAsync(); await host.Services.GetRequiredService<NotificationDispatcher>().TickAsync();
            Assert.Equal(runId, Assert.Single(await host.Services.GetRequiredService<IRunRepository>().ListAsync()).Id); Assert.Single(calls);
            Assert.Equal(stored, JsonSerializer.Serialize(await host.Services.GetRequiredService<IRunRepository>().GetAsync(runId)));
            Assert.Single(discord.Sent); Assert.Single(http.Sent); await host.StopAsync();
        }
        await using var final = database.Open(); var deliveries = await final.NotificationOutbox.ToArrayAsync();
        Assert.Equal(2, deliveries.Length); Assert.All(deliveries, item => Assert.Equal(NotificationDeliveryStatus.Delivered, item.Status));
        Assert.Single(deliveries.Select(item => item.EventId).Distinct()); Assert.Equal(4, discord.Accepted);
    }

    [Fact]
    public async Task ChannelsSecondAdapterMapsSameOwnerControlsAndDegradesUnsupportedButtonsWithoutAuthority()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var discord = new DiscordAdapter(); using var host = Host(database, discord, new(), [], new HumanApprovalTests.TestClock());
        await Allow(database, Owner, DiscordRoute, "discord-owner");
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); var router = host.Services.GetRequiredService<ChannelMessageRouter>();
        var controls = host.Services.GetRequiredService<ChannelOwnerCommands>(); var peer = discord.Peer();
        var run = await router.SubmitAsync(peer);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => discord.Pause(controls, peer with { SenderId = "foreign" }, new(Guid.NewGuid(), run.Id, run.RunVersion)));
        Assert.Equal(run, runs.GetRun(run.Id));
        var command = new PauseRunCommand(Guid.NewGuid(), run.Id, run.RunVersion);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => discord.Pause(controls, peer, command)));
        var paused = runs.GetRun(run.Id); Assert.Equal(RunStatus.Paused, paused.Status); Assert.Equal(run.RunVersion + 1, paused.RunVersion);
        await discord.Resume(controls, peer, new(Guid.NewGuid(), run.Id, paused.RunVersion)); var resumed = runs.GetRun(run.Id); Assert.Equal(RunStatus.Queued, resumed.Status);
        await discord.Pause(controls, peer, command); Assert.Equal(resumed, runs.GetRun(run.Id)); // Same receipt cannot undo later resume.
        await Assert.ThrowsAsync<RunVersionConflictException>(() => discord.Pause(controls, peer, command with { CommandId = Guid.NewGuid() }));
        await router.SendAsync(new OutgoingMessage { Id = Guid.NewGuid(), Destination = DiscordRoute, RunId = run.Id, Text = "Pending action requires local review",
            Actions = [new(ChannelActionKind.Approve, "Approve exact action", "private-approval-token")] });
        var sent = Assert.Single(discord.Sent); Assert.Empty(sent.Actions); Assert.DoesNotContain("private-approval-token", sent.Text); Assert.Contains("Open Personal Agent", sent.Text);
        await discord.Cancel(controls, peer, new(Guid.NewGuid(), run.Id, resumed.RunVersion)); Assert.Equal(RunStatus.Cancelled, runs.GetRun(run.Id).Status);
        Assert.Empty(await host.Services.GetRequiredService<IApprovalRepository>().ListPendingAsync(Owner));
    }

    [Fact]
    public async Task ChannelsTelegramHttpApprovalOutboxRetryAllowlistButtonsOffsetAndDuplicateDecisionComposeOnce()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var pending = await fixture.Pending();
        var route = new ChannelRoute("telegram", "matrix-bot", "24"); var owner = HumanApprovalTests.Fixture.Owner;
        await Allow(fixture.Database, owner, route, "42"); var policy = new ChannelOwnershipPolicy(owner, new SqliteChannelBindingRepository(fixture.Database));
        var actions = new SqliteChannelActionRepository(fixture.Database); var presenter = new ChannelApprovalPresenter(policy, fixture.Approvals, actions, fixture.Clock, fixture.Runs);
        var peer = new IncomingMessage { Route = route, SenderId = "42", ExternalMessageId = "review", TimestampUtc = fixture.Clock.Now, Text = "review" };
        var message = await presenter.CreateAsync(Guid.NewGuid(), peer, pending.Id); var state = new HttpState { FailFirstSend = true, BeforeForeignAnswer = () => Assert.Equal(0, fixture.Calls) };
        using var channel = new TelegramOutboundChannel(route.ConnectionId, "reference", new Secrets(), policy, presenter, new Http(state));
        var outbox = new SqliteNotificationOutbox(fixture.Database, fixture.Clock); await outbox.EnqueueAsync(pending.Id, message);
        var dispatcher = new NotificationDispatcher(outbox, new(fixture.Runs, [channel], policy), fixture.Clock);
        await dispatcher.TickAsync(); var retry = (await outbox.GetAsync(message.Id))!; Assert.Equal(NotificationDeliveryStatus.Pending, retry.Status); Assert.Equal(0, fixture.Calls);
        fixture.Clock.Now = retry.NextAttemptAtUtc; await dispatcher.TickAsync();
        var body = Assert.Single(state.Sent); var keyboard = body.GetProperty("reply_markup").GetProperty("inline_keyboard");
        state.CallbackToken = keyboard[0][0].GetProperty("callback_data").GetString(); Assert.Equal(message.Actions[0].Token, state.CallbackToken);
        Assert.Equal(2, keyboard.GetArrayLength()); Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(pending.Id))!.Status);
        var commands = new ChannelOwnerCommands(policy, fixture.Runs, fixture.Approvals);
        var handler = new TelegramCommandHandler(route.ConnectionId, policy, commands, new(fixture.Runs, [], policy), fixture.Runs,
            actions: new ChannelActionHandler(policy, actions, fixture.Approvals, commands, fixture.Runs, fixture.Clock));
        var cursors = new SqliteChannelCursorRepository(fixture.Database); var replies = new List<OutgoingMessage>();
        for (var pass = 0; pass < 2; pass++)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); state.Stop = stop;
            using var receiver = new TelegramPollingReceiver(new(route.ConnectionId, "reference"), new Secrets(), cursors, new Http(state));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RunAsync(async (input, ct) => replies.Add(await handler.HandleAsync(input, ct)), stop.Token));
        }
        Assert.Equal(new long[] { 0, 13, 13 }, state.Offsets); Assert.Equal(13, (await cursors.ReadAsync("telegram", route.ConnectionId)).NextSequence);
        Assert.Equal(3, state.Answers); Assert.Equal(2, replies.Count); Assert.Single(replies, reply => reply.Text == "Exact action approved.");
        await fixture.UntilFinished(); Assert.Equal(1, fixture.Calls); Assert.Single(await fixture.ToolCalls.ListAsync(fixture.RunId));
        Assert.Equal(NotificationDeliveryStatus.Delivered, (await outbox.GetAsync(message.Id))!.Status); Assert.Equal(2, state.SendAttempts);
    }

    private static async Task Allow(TemporaryAgentDatabase database, ApprovalOwner owner, ChannelRoute route, string sender)
    { var bindings = new SqliteChannelBindingRepository(database); var pending = await bindings.RequestPairingAsync(route, sender, owner); await bindings.SetStatusAsync(pending.Id, pending.Version, owner, true); }
    private static IHost Host(TemporaryAgentDatabase database, DiscordAdapter discord, HttpState http, ConcurrentBag<AgentRequest> calls, TimeProvider clock) =>
        AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton(Owner); services.AddSingleton(clock); services.AddSingleton<IOutboundChannel>(discord); services.AddSingleton<IInboundChannel>(discord);
            services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.DatabasePath)!, "secrets")));
            services.AddSingleton<Func<HttpMessageHandler>>(() => new Http(http));
            services.AddSingleton<IAgentRuntime>(provider => new CountingRuntime(provider.GetRequiredService<FakeAgentRuntime>(), calls));
        }).Build();
    private sealed class CountingRuntime(IAgentRuntime inner, ConcurrentBag<AgentRequest> calls) : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { calls.Add(request); return inner.ExecuteAsync(request, progress, ct); }
    }
    private sealed class DiscordAdapter : IInboundChannel, IOutboundChannel
    {
        public const string UntrustedConversation = "11111111-1111-1111-1111-111111111111";
        public string ChannelId => DiscordRoute.ChannelId; public string ConnectionId => DiscordRoute.ConnectionId; public ChannelCapabilities Capabilities { get; } = new(false);
        public ConcurrentBag<OutgoingMessage> Sent = []; public int Accepted;
        public IncomingMessage Peer() => new() { Route = DiscordRoute, SenderId = "discord-owner", ExternalMessageId = "7", TimestampUtc = DateTimeOffset.UtcNow,
            Text = "Synthetic Discord task", CorrelationKey = UntrustedConversation };
        public async Task RunAsync(Func<IncomingMessage, CancellationToken, Task> accept, CancellationToken ct)
        { for (var index = 0; index < 2; index++) { await accept(Peer(), ct); Interlocked.Increment(ref Accepted); } }
        public Task SendAsync(OutgoingMessage message, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Sent.Add(message.Snapshot()); return Task.CompletedTask; }
        public Task<TaskRun> Pause(ChannelOwnerCommands commands, IncomingMessage actor, PauseRunCommand command) => commands.PauseAsync(actor, command);
        public Task<TaskRun> Resume(ChannelOwnerCommands commands, IncomingMessage actor, ResumeRunCommand command) => commands.ResumeAsync(actor, command);
        public Task<TaskRun> Cancel(ChannelOwnerCommands commands, IncomingMessage actor, CancelRunCommand command) => commands.CancelAsync(actor, command);
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => ValueTask.FromResult<string?>("12345:synthetic-channels-token");
    }
    private sealed class HttpState
    {
        public bool FailFirstSend; public int SendAttempts, Answers; public string? CallbackToken; public CancellationTokenSource? Stop; public Action? BeforeForeignAnswer;
        public ConcurrentBag<JsonElement> Sent = []; public List<long> Offsets = [];
    }
    private sealed class Http(HttpState state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var body = document.RootElement;
            if (request.RequestUri.AbsolutePath.EndsWith("/sendMessage"))
            {
                var attempt = Interlocked.Increment(ref state.SendAttempts);
                if (attempt == 1 && state.FailFirstSend) throw new HttpRequestException(HttpRequestError.NameResolutionError, "Synthetic offline");
                Assert.Equal(24, body.GetProperty("chat_id").GetInt64()); state.Sent.Add(body.Clone());
                return Response("{\"ok\":true,\"result\":{\"message_id\":1,\"chat\":{\"id\":24}}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/answerCallbackQuery"))
            { if (state.Answers == 0) state.BeforeForeignAnswer?.Invoke(); state.Answers++; Assert.Single(body.EnumerateObject()); return Response("{\"ok\":true,\"result\":true}"); }
            Assert.EndsWith("/getUpdates", request.RequestUri.AbsolutePath); var offset = body.GetProperty("offset").GetInt64(); state.Offsets.Add(offset);
            if (offset >= 13) { state.Stop!.Cancel(); ct.ThrowIfCancellationRequested(); }
            var updates = Enumerable.Range(10, 3).Select(id => new { update_id = id, callback_query = new { id = "query-" + id,
                from = new { id = id == 10 ? 999 : 42, is_bot = false }, data = state.CallbackToken,
                message = new { message_id = 1, date = 1800000000, from = new { id = 777, is_bot = true }, chat = new { id = 24 } } } });
            return Response(JsonSerializer.Serialize(new { ok = true, result = updates }));
        }
        private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
    private static async Task Until(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
}
