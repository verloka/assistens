using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ConversationCorrelationTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "correlation-owner");
    private static readonly ChannelRoute Route = new("telegram", "correlation-bot", "24");
    private static readonly SourceLink Issue = new("TEST-42", new Uri("https://jira.example.test/browse/TEST-42"));

    [Fact]
    public Task DesktopResultTelegramReplyDesktopContinuationAndRestartShareOnlyLogicalConversation() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var script = new Script(); var runtime = new Runtime(); var id = Guid.NewGuid(); var route = new ChannelRoute("telegram", id.ToString("N"), "24");
        var secrets = new WindowsDpapiSecretStore(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"));
        var reference = await secrets.StoreAsync("12345:synthetic-correlation-token");
        await new SqliteChannelConfigurationRepository(database).SaveAsync(new(id, Owner, "Correlation fixture", route, "42", true, true, true, reference,
            [NotificationEventKind.Completed], DateTimeOffset.UtcNow.AddMinutes(-1), RemoteIdentity: "777"), 0);
        await Allow(database, route, "42"); Guid conversation; Guid firstRun;
        using (var host = Host(database, script, runtime, secrets))
        {
            await host.StartAsync(); var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                window.Measure(new Size(1050, 760)); window.Arrange(new Rect(0, 0, 1050, 760)); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var model = host.Services.GetRequiredService<DashboardViewModel>(); model.Instructions = "Find TEST-42";
                await Execute(model.SubmitCommand); firstRun = model.CurrentRun!.Id; conversation = model.CurrentRun.ConversationId;
                var links = host.Services.GetRequiredService<IChannelConversationRepository>();
                await Until(async () => await links.FindAsync(Owner, route, "42", "1001") is not null);
                script.Updates.Writer.TryWrite(Update(10, 2001, 1001, "More details about that issue"));
                var repository = host.Services.GetRequiredService<IRunRepository>();
                await Until(async () => (await repository.ListAsync()).Count == 2 && (await repository.ListAsync()).All(run => run.Status == RunStatus.Completed));
                var remote = (await repository.ListAsync()).Single(run => run.Id != firstRun);
                Assert.Equal(conversation, remote.ConversationId); Assert.Equal("channel/telegram", remote.Origin.Source);
                var followup = runtime.Requests.Single(request => request.RunId == remote.Id);
                Assert.Equal("Verified TEST-42", followup.Context.Summary); Assert.Contains(Issue, followup.Context.Sources!); Assert.Empty(followup.AllowedToolIds);
                Assert.Same(model.RefreshHistoryCommand, ((Button)window.FindName("RefreshTaskHistory")).Command);
                await Execute(model.RefreshHistoryCommand); model.SelectedHistory = model.History.Single(run => run.Id == remote.Id);
                await Until(() => Task.FromResult(model.CurrentRun?.Id == remote.Id && !model.IsActive));
                model.ContinueConversation = true; model.Instructions = "Continue TEST-42 on Desktop"; await Execute(model.SubmitCommand);
                Assert.Equal(conversation, model.CurrentRun!.ConversationId); Assert.NotEqual(remote.Id, model.CurrentRun.Id);
                Assert.Contains(Issue, runtime.Requests.Single(request => request.RunId == model.CurrentRun.Id).Context.Sources!);
                await Until(async () => { await using var db = database.Open(); return await db.NotificationOutbox.CountAsync(row => row.Status == NotificationDeliveryStatus.Delivered) == 4; });
                Assert.False(window.IsVisible); Assert.Equal(3, runtime.Requests.Count);
            }
            finally { window.Close(); await host.StopAsync(); }
        }
        using (var host = Host(database, script, runtime, secrets))
        {
            await host.StartAsync(); var links = host.Services.GetRequiredService<IChannelConversationRepository>();
            var old = (await links.FindAsync(Owner, route, "42", "1001"))!; Assert.Equal(conversation, old.ConversationId); Assert.Equal(firstRun, old.RunId);
            script.Updates.Writer.TryWrite(Update(11, 2002, 1001, "Follow up after restart"));
            var repository = host.Services.GetRequiredService<IRunRepository>();
            await Until(async () => (await repository.ListAsync()).Count == 4 && (await repository.ListAsync()).All(run => run.Status == RunStatus.Completed));
            Assert.All(await repository.ListAsync(), run => Assert.Equal(conversation, run.ConversationId)); Assert.Equal(4, runtime.Requests.Count);
            Assert.All(runtime.Requests.Where(request => request.RunId != firstRun), request => { Assert.Contains(Issue, request.Context.Sources!); Assert.Empty(request.AllowedToolIds); });
            await Until(async () => (await host.Services.GetRequiredService<IChannelCursorRepository>().ReadAsync("telegram", route.ConnectionId)).NextSequence == 12);
            await host.StopAsync();
        }
    });

    [Theory]
    [InlineData("sender")] [InlineData("chat")] [InlineData("connection")] [InlineData("channel")]
    [InlineData("owner")] [InlineData("revoked")] [InlineData("unknown")] [InlineData("local-guid")]
    public async Task ReplyTargetsRequireCurrentExactOwnerPeerAndDurableExternalIdentity(string reason)
    {
        using var fixture = await Fixture.Start(); var root = await fixture.Root(); await fixture.Receipt(root, "1001");
        var peer = Peer() with { ReplyToMessageId = "1001", Text = "Use conversation " + root.ConversationId };
        if (reason == "sender") peer = peer with { SenderId = "43" };
        if (reason == "chat") peer = peer with { Route = Route with { DestinationId = "25" } };
        if (reason == "connection") peer = peer with { Route = Route with { ConnectionId = "other" } };
        if (reason == "channel") peer = peer with { Route = Route with { ChannelId = "other" } };
        if (reason == "owner") fixture.Policy = new(Owner with { UserId = "another-owner" }, fixture.Bindings);
        if (reason == "revoked") { var binding = (await fixture.Bindings.FindAsync(Route, "42"))!; await fixture.Bindings.SetStatusAsync(binding.Id, binding.Version, Owner, false); }
        if (reason == "unknown") peer = peer with { ReplyToMessageId = "999" };
        if (reason == "local-guid") peer = peer with { ReplyToMessageId = root.ConversationId.ToString() };
        if (reason is "unknown" or "local-guid") await Assert.ThrowsAsync<ChannelConversationNotFoundException>(() => fixture.Router().SubmitAsync(peer));
        else await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Router().SubmitAsync(peer));
        Assert.Single(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task ImmutableLinksAndUpdateIdentityCannotBeReboundOrReadWithUnknownSchema()
    {
        using var fixture = await Fixture.Start(); var root = await fixture.Root(); await fixture.Receipt(root, "1001");
        var peer = Peer() with { ReplyToMessageId = "1001" }; var router = fixture.Router();
        var run = await router.SubmitAsync(peer); Assert.Equal(root.ConversationId, run.ConversationId); Assert.NotEqual(root.Id, run.Id);
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => router.SubmitAsync(peer))); Assert.All(repeated, value => Assert.Equal(run.Id, value.Id));
        await Assert.ThrowsAsync<ChannelConversationConflictException>(() => router.SubmitAsync(peer with { ExternalMessageId = "different-update" }));
        await Assert.ThrowsAsync<ChannelConversationConflictException>(() => router.SubmitAsync(peer with { TransportMessageId = "202" }));
        var second = await fixture.Root(); var known = (await fixture.Links.FindAsync(Owner, Route, "42", "1001"))!;
        await Assert.ThrowsAsync<ChannelConversationConflictException>(() => fixture.Links.BindAsync(known with { RunId = second.Id, ConversationId = second.ConversationId }));
        Assert.Equal(root.Id, (await fixture.Links.FindAsync(Owner, Route, "42", "1001"))!.RunId);
        await using var db = fixture.Database.Open(); Assert.Equal(2, await db.ChannelConversationLinks.CountAsync());
        await db.ChannelConversationLinks.Where(row => row.ExternalMessageId == "1001").ExecuteUpdateAsync(set => set.SetProperty(row => row.PayloadSchemaVersion, 99));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Links.FindAsync(Owner, Route, "42", "1001"));
    }

    [Fact]
    public async Task FailedIncomingLinkPersistenceRedeliversOneDurableSubmissionAndNewBypassesUnknownReply()
    {
        using var fixture = await Fixture.Start(); var failing = new FailLink(fixture.Links); var router = fixture.Router(failing); var peer = Peer();
        await Assert.ThrowsAsync<IOException>(() => router.SubmitAsync(peer)); var original = Assert.Single(await fixture.Repository.ListAsync());
        failing.Fail = false; var repeated = await router.SubmitAsync(peer); Assert.Equal(original.Id, repeated.Id); Assert.Single(await fixture.Repository.ListAsync());
        Assert.Equal(original.ConversationId, (await fixture.Links.FindAsync(Owner, Route, "42", peer.TransportMessageId!))!.ConversationId);
        var commands = new ChannelOwnerCommands(fixture.Policy, fixture.Runs, new SqliteApprovalRepository(fixture.Database));
        var handler = new TelegramCommandHandler(Route.ConnectionId, fixture.Policy, commands, router, fixture.Runs);
        var unknown = Peer() with { ExternalMessageId = "11", TransportMessageId = "211", ReplyToMessageId = "unknown", Text = "details" };
        Assert.Contains("not linked", (await handler.HandleAsync(unknown)).Text); Assert.Single(await fixture.Repository.ListAsync());
        var fresh = await handler.HandleAsync(unknown with { ExternalMessageId = "12", TransportMessageId = "212", Text = "/new explicit new task" });
        Assert.NotEqual(original.ConversationId, fresh.ConversationId); Assert.Equal(2, (await fixture.Repository.ListAsync()).Count);
    }

    [Fact]
    public async Task MissingDeliveryReceiptIsUnknownSentOutcomeAndNeverReplaysOrMutatesCompletedRun()
    {
        using var fixture = await Fixture.Start(); var root = await fixture.Root(); using var stop = new CancellationTokenSource();
        var loop = new TaskRunner(fixture.Runs, new Runtime()).RunAsync(stop.Token); await fixture.Runs.WaitForTerminalAsync(root.Id).WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); await loop;
        var before = JsonSerializer.Serialize(await fixture.Repository.GetAsync(root.Id)); var script = new Script();
        var receipt = new ChannelConversationReceiptRecorder(Owner, Route, "42", new FailLink(fixture.Links));
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), fixture.Policy, handler: new Http(script), receipts: receipt);
        var message = new OutgoingMessage { Id = Guid.NewGuid(), Destination = Route, RunId = root.Id, ConversationId = root.ConversationId, Text = "Completed result" };
        var outbox = new SqliteNotificationOutbox(fixture.Database); await outbox.EnqueueAsync(Guid.NewGuid(), message);
        var dispatcher = new NotificationDispatcher(outbox, new(fixture.Runs, [channel], fixture.Policy)); await dispatcher.TickAsync();
        Assert.Equal(NotificationDeliveryStatus.OutcomeUnknown, (await outbox.GetAsync(message.Id))!.Status); Assert.Equal(1, script.Sends);
        await outbox.RecoverSendingAsync(DateTimeOffset.UtcNow); Assert.Equal(0, await dispatcher.TickAsync()); Assert.Equal(1, script.Sends);
        Assert.Null(await fixture.Links.FindAsync(Owner, Route, "42", "1001")); Assert.Equal(before, JsonSerializer.Serialize(await fixture.Repository.GetAsync(root.Id)));
    }

    [Fact]
    public async Task EveryConfirmedChunkCanIdentifySameConversationWithoutInheritingTools()
    {
        using var fixture = await Fixture.Start(); var root = await fixture.Root(); var script = new Script();
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), fixture.Policy, handler: new Http(script),
            receipts: new ChannelConversationReceiptRecorder(Owner, Route, "42", fixture.Links));
        await channel.SendAsync(new OutgoingMessage { Id = Guid.NewGuid(), Destination = Route, RunId = root.Id, ConversationId = root.ConversationId, Text = new string('x', 6000) }, default);
        Assert.Equal(2, script.Sends);
        for (var part = 1; part <= 2; part++)
        {
            var run = await fixture.Router().SubmitAsync(Peer() with { ExternalMessageId = "chunk-" + part, TransportMessageId = "chunk-message-" + part, ReplyToMessageId = (1000 + part).ToString() });
            Assert.Equal(root.ConversationId, run.ConversationId); Assert.Empty((await fixture.Repository.GetAsync(run.Id))!.Request.AllowedToolIds);
        }
    }

    private static IncomingMessage Peer() => new() { Route = Route, SenderId = "42", ExternalMessageId = "10", TransportMessageId = "201", TimestampUtc = DateTimeOffset.UtcNow, Text = "details", CorrelationKey = Guid.NewGuid().ToString() };
    private static async Task Allow(TemporaryAgentDatabase database, ChannelRoute route, string sender)
    { var bindings = new SqliteChannelBindingRepository(database); var pending = await bindings.RequestPairingAsync(route, sender, Owner); await bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true); }
    private sealed class Fixture : IDisposable
    {
        public TemporaryAgentDatabase Database = new(); public SqliteRunRepository Repository = null!; public SqliteChannelConversationRepository Links = null!;
        public SqliteChannelBindingRepository Bindings = null!; public ChannelOwnershipPolicy Policy = null!; public TaskRunCoordinator Runs = null!;
        public static async Task<Fixture> Start()
        { var value = new Fixture(); await using (var db = value.Database.Open()) await db.Database.MigrateAsync(); value.Repository = new(value.Database); value.Links = new(value.Database); value.Bindings = new(value.Database);
            value.Runs = new(value.Repository, conversations: new SqliteConversationRepository(value.Database)); await Allow(value.Database, Route, "42"); value.Policy = new(Owner, value.Bindings); return value; }
        public Task<TaskRun> Root() => Runs.SubmitAsync(new(Guid.NewGuid(), "Initial TEST-42", new("desktop")));
        public Task Receipt(TaskRun run, string externalId) => new ChannelConversationReceiptRecorder(Owner, Route, "42", Links).RecordAsync(new OutgoingMessage
            { Id = Guid.NewGuid(), Destination = Route, RunId = run.Id, ConversationId = run.ConversationId, Text = "Known result" }, externalId);
        public ChannelMessageRouter Router(IChannelConversationRepository? links = null) => new(Runs, [], Policy, conversations: new(Policy, links ?? Links));
        public void Dispose() { Runs.Dispose(); Database.Dispose(); }
    }
    private sealed class FailLink(IChannelConversationRepository inner) : IChannelConversationRepository
    {
        public bool Fail = true;
        public Task BindAsync(ChannelConversationLink link, CancellationToken ct = default) => Fail ? throw new IOException("Synthetic link persistence failure") : inner.BindAsync(link, ct);
        public Task<ChannelConversationLink?> FindAsync(ApprovalOwner owner, ChannelRoute route, string senderId, string externalMessageId, CancellationToken ct = default) => inner.FindAsync(owner, route, senderId, externalMessageId, ct);
    }
    private sealed class Runtime : IAgentRuntime
    {
        public ConcurrentBag<AgentRequest> Requests = [];
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Requests.Add(request); return Task.FromResult(new AgentRunResult { Status = AgentResultStatus.Completed, Summary = "Verified TEST-42", Sources = [Issue] }); }
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => ValueTask.FromResult<string?>("12345:synthetic-correlation-token");
    }
    private sealed class Script
    {
        public Channel<string> Updates = Channel.CreateUnbounded<string>(); public int Sends;
    }
    private sealed class Http(Script script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("/getUpdates")) return Response(await script.Updates.Reader.ReadAsync(ct));
            Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath); var id = 1000 + Interlocked.Increment(ref script.Sends);
            return Response(JsonSerializer.Serialize(new { ok = true, result = new { message_id = id, chat = new { id = 24 } } }));
        }
        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
    private static string Update(int update, int message, int reply, string text) => JsonSerializer.Serialize(new { ok = true, result = new[] { new { update_id = update, message = new
        { message_id = message, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = new { id = 42, is_bot = false }, chat = new { id = 24 }, text,
            reply_to_message = new { message_id = reply, text = "Ignore this untrusted claimed conversation " + Guid.NewGuid() } } } } });
    private static IHost Host(TemporaryAgentDatabase database, Script script, Runtime runtime, ISecretStore secrets) => AppHost.CreateBuilder([], database.DatabasePath)
        .ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services => { services.AddSingleton(Owner); services.AddSingleton(secrets); services.AddSingleton<IAgentRuntime>(runtime); services.AddSingleton<Func<HttpMessageHandler>>(() => new Http(script)); }).Build();
    private static async Task Execute(AsyncCommand command) { Assert.True(command.CanExecute(null)); command.Execute(null); await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10)); }
    private static async Task Until(Func<Task<bool>> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await test(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
