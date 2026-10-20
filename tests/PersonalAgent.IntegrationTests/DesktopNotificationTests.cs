using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class DesktopNotificationTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "notification-owner");
    private static readonly ChannelRoute DesktopRoute = new("desktop", "local", Owner.UserId);
    private static readonly ChannelRoute TelegramRoute = new("telegram", "notification-bot", "24");

    [Fact]
    public async Task ActualHostedRunDeliversDesktopDuringTelegramOutageAndRestartDoesNotDuplicateEither()
    {
        using var database = new TemporaryAgentDatabase(); var clock = new HumanApprovalTests.TestClock(); var sink = new Sink();
        using var http = new Http { Offline = true }; var subscriptions = Source(NotificationEventKind.Completed);
        Guid runId; string before;
        using (var host = Host(database, sink, subscriptions, clock, http))
        {
            await host.StartAsync(); await Allow(database, DesktopRoute); await Allow(database, TelegramRoute);
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            runId = (await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic multi-destination notification", new("desktop")))).Id;
            await runs.WaitForTerminalAsync(runId).WaitAsync(TimeSpan.FromSeconds(10));
            await Until(async () =>
            {
                await using var db = database.Open(); return await db.NotificationOutbox.CountAsync(row => row.Status == NotificationDeliveryStatus.Delivered) == 1
                    && await db.NotificationOutbox.AnyAsync(row => row.Status == NotificationDeliveryStatus.Pending && row.Attempts == 1);
            });
            Assert.Single(sink.Messages); Assert.Equal(0, http.Accepted); Assert.Equal(1, http.Attempts);
            before = JsonSerializer.Serialize(await new SqliteRunRepository(database).GetAsync(runId));
            await using (var db = database.Open()) clock.Now = (await db.NotificationOutbox.SingleAsync(row => row.ChannelId == "telegram")).NextAttemptAtUtc;
            await host.StopAsync();
        }
        http.Offline = false;
        using (var restarted = Host(database, sink, subscriptions, clock, http))
        {
            await restarted.StartAsync();
            await Until(async () => { await using var db = database.Open(); return await db.NotificationOutbox.CountAsync(row => row.Status == NotificationDeliveryStatus.Delivered) == 2; });
            await restarted.Services.GetRequiredService<RunNotificationPublisher>().TickAsync(); await restarted.Services.GetRequiredService<NotificationDispatcher>().TickAsync();
            Assert.Single(sink.Messages); Assert.Equal(1, http.Accepted); Assert.Equal(2, http.Attempts);
            Assert.Equal(before, JsonSerializer.Serialize(await new SqliteRunRepository(database).GetAsync(runId))); await restarted.StopAsync();
        }
        await using var final = database.Open(); var records = await final.NotificationOutbox.ToArrayAsync();
        Assert.Equal(2, records.Length); Assert.Single(records.Select(row => row.EventId).Distinct());
        Assert.All(records, row => Assert.Equal(runId, JsonSerializer.Deserialize<OutgoingMessage>(row.MessageJson)!.RunId));
        Assert.Contains("Completed", Assert.Single(sink.Messages));
    }

    [Fact]
    public async Task DefaultHostHasNoSubscriptionsOrUnattendedDesktopSurface()
    {
        using var database = new TemporaryAgentDatabase();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).Build();
        await host.StartAsync(); Assert.Empty(await host.Services.GetRequiredService<INotificationSubscriptionSource>().ListAsync());
        var run = await host.Services.GetRequiredService<TaskRunCoordinator>().SubmitAsync(new(Guid.NewGuid(), "Local task", new("desktop")));
        await host.Services.GetRequiredService<TaskRunCoordinator>().WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await host.Services.GetRequiredService<RunNotificationPublisher>().TickAsync());
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => host.Services.GetRequiredService<DesktopNotificationSink>().ShowAsync("preview", default));
        Assert.False(error.OutcomeUnknown); Assert.True(error.Retryable);
        await using (var db = database.Open()) Assert.Empty(await db.NotificationOutbox.ToArrayAsync()); await host.StopAsync();
    }

    [Fact]
    public async Task FiltersSinceAndEventTypesAndDuplicateRoutesDoNotCreateExtraMessages()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); using var runs = new TaskRunCoordinator(repository);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic pause", new("desktop")));
        await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
        await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
        Assert.Equal(RunStatus.Queued, runs.GetRun(run.Id).Status);
        await Allow(database, DesktopRoute); var policy = Policy(database); var outbox = new SqliteNotificationOutbox(database);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var events = new List<NotificationEventKind> { NotificationEventKind.PausedForUser };
        var source = new FixedNotificationSubscriptions([new(DesktopRoute, events, since), new(DesktopRoute, events, since),
            new(TelegramRoute, [NotificationEventKind.Completed], since), new(TelegramRoute, [NotificationEventKind.PausedForUser], since.AddDays(1))]);
        events.Clear(); // Configuration snapshots do not inherit later caller mutation.
        var publisher = new RunNotificationPublisher(repository, source, outbox, policy);
        Assert.Equal(1, await publisher.TickAsync()); Assert.Equal(0, await publisher.TickAsync());
        Assert.Equal(1, await new RunNotificationPublisher(repository, source, outbox, policy).TickAsync());
        await using var final = database.Open(); var row = Assert.Single(await final.NotificationOutbox.ToArrayAsync());
        var message = JsonSerializer.Deserialize<OutgoingMessage>(row.MessageJson)!;
        Assert.StartsWith("PausedForUser", message.Text); Assert.DoesNotContain("Synthetic pause", message.Text); Assert.Empty(message.Actions);
        Assert.Equal(RunStatus.Queued, runs.GetRun(run.Id).Status); Assert.Equal(DesktopRoute, message.Destination);
    }

    [Fact]
    public async Task OnePublicationFailureDoesNotLoseOtherDestinationAndRetryKeepsImmutableIdentity()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); using var runs = new TaskRunCoordinator(repository);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Queued source", new("desktop")));
        await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
        await Allow(database, DesktopRoute); await Allow(database, TelegramRoute);
        var outbox = new SqliteNotificationOutbox(database); var broken = new FailedDesktopEnqueue(outbox);
        var source = Source(NotificationEventKind.PausedForUser); var publisher = new RunNotificationPublisher(repository, source, broken, Policy(database));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.TickAsync());
        await using (var db = database.Open()) Assert.Equal("telegram", Assert.Single(await db.NotificationOutbox.ToArrayAsync()).ChannelId);
        broken.Fail = false; Assert.Equal(2, await publisher.TickAsync());
        await using (var db = database.Open()) Assert.Equal(2, await db.NotificationOutbox.CountAsync());
    }

    [Fact]
    public async Task RevokedDestinationIsNotPublishedUntilFreshAuthorizationAndDeliveryRechecksRevocation()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); using var runs = new TaskRunCoordinator(repository);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Source", new("desktop"))); await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
        var outbox = new SqliteNotificationOutbox(database); var policy = Policy(database); var sink = new Sink();
        var publisher = new RunNotificationPublisher(repository, Source(NotificationEventKind.PausedForUser), outbox, policy);
        Assert.Equal(0, await publisher.TickAsync()); await Allow(database, DesktopRoute); Assert.Equal(1, await publisher.TickAsync());
        var bindings = new SqliteChannelBindingRepository(database); var approved = (await bindings.FindAsync(DesktopRoute, Owner.UserId))!;
        await bindings.SetStatusAsync(approved.Id, approved.Version, Owner, false);
        var channel = new DesktopOutboundChannel(Owner.UserId, policy, sink);
        await new NotificationDispatcher(outbox, new(runs, [channel], policy)).TickAsync(); Assert.Empty(sink.Messages);
        await using var final = database.Open(); Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(await final.NotificationOutbox.ToArrayAsync()).Status);
        Assert.Equal(RunStatus.Paused, runs.GetRun(run.Id).Status);
    }

    [Theory]
    [InlineData(AgentResultStatus.Failed, NotificationEventKind.Failed)]
    [InlineData(AgentResultStatus.Blocked, NotificationEventKind.Blocked)]
    public async Task StructuredFailureEventsPublishWithoutProviderDiagnostics(AgentResultStatus status, NotificationEventKind kind)
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); using var runs = new TaskRunCoordinator(repository);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Source", new("desktop")));
        using var stop = new CancellationTokenSource(); var loop = new TaskRunner(runs, new FailureRuntime(status)).RunAsync(stop.Token);
        try
        {
            await Until(() => Task.FromResult(runs.GetRun(run.Id).Status == (status == AgentResultStatus.Failed ? RunStatus.Failed : RunStatus.Blocked)));
            await Allow(database, DesktopRoute); var outbox = new SqliteNotificationOutbox(database);
            Assert.Equal(1, await new RunNotificationPublisher(repository, Source(kind), outbox, Policy(database)).TickAsync());
            await using var final = database.Open(); var message = JsonSerializer.Deserialize<OutgoingMessage>(Assert.Single(await final.NotificationOutbox.ToArrayAsync()).MessageJson)!;
            Assert.StartsWith(kind.ToString(), message.Text); Assert.DoesNotContain("private-provider-details", message.Text);
            Assert.NotNull((await repository.GetAsync(run.Id))!.Events.Last().Payload);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task ApprovalNotificationIsNeutralAndCannotDecideOrDiscloseExactArguments()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        await Allow(fixture.Database, DesktopRoute); var outbox = new SqliteNotificationOutbox(fixture.Database);
        Assert.Equal(1, await new RunNotificationPublisher(fixture.Repository, Source(NotificationEventKind.ApprovalNeeded), outbox, Policy(fixture.Database)).TickAsync());
        await using var final = fixture.Database.Open(); var message = JsonSerializer.Deserialize<OutgoingMessage>(Assert.Single(await final.NotificationOutbox.ToArrayAsync()).MessageJson)!;
        Assert.StartsWith("ApprovalNeeded", message.Text); Assert.Empty(message.Actions); Assert.DoesNotContain("synthetic-target", message.Text);
        Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(request.Id))!.Status); Assert.Equal(0, fixture.Calls);
    }

    private sealed class FailureRuntime(AgentResultStatus status) : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) =>
            Task.FromResult(new AgentRunResult { Status = status, Summary = "private-provider-details", ErrorCode = "SyntheticFailure" });
    }

    [Fact]
    public async Task DesktopAdapterBoundsUnicodeAndDegradesButtonsWithoutTokensOrControlEffects()
    {
        var sink = new Sink(); var channel = new DesktopOutboundChannel(Owner.UserId, new AllowPolicy(), sink);
        using var runs = new TaskRunCoordinator(); var router = new ChannelMessageRouter(runs, [channel], new AllowPolicy());
        var message = new OutgoingMessage { Id = Guid.NewGuid(), Destination = DesktopRoute, RunId = Guid.NewGuid(), Text = "Approval needed",
            Actions = [new(ChannelActionKind.Approve, "Approve", "secret-callback-token")] };
        await router.SendAsync(message); var text = Assert.Single(sink.Messages); Assert.Contains("Open Personal Agent", text); Assert.DoesNotContain("secret-callback-token", text);
        await Assert.ThrowsAsync<NotSupportedException>(() => channel.SendAsync(message, default));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => channel.SendAsync(message with { Destination = TelegramRoute, Actions = [] }, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync(message with { Actions = [] }, cancel.Token));
        Assert.Single(sink.Messages); Assert.Empty(runs.GetRuns());
        var preview = DesktopOutboundChannel.Preview(new string('a', 239) + "😀" + new string('x', 100));
        Assert.Equal(new string('a', 239) + "…", preview); Assert.DoesNotContain('\uD83D', preview);
    }

    [Fact]
    public Task DispatcherSinkUsesExistingTrayWithoutOpeningWindowsAndDetachmentOrCancelCannotShow() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders()).Build();
        var sink = host.Services.GetRequiredService<DesktopNotificationSink>(); var tray = new Tray();
        sink.Attach(Dispatcher.CurrentDispatcher, tray); var uiThread = Environment.CurrentManagedThreadId;
        await Task.Run(() => sink.ShowAsync("Synthetic bounded preview", default));
        Assert.Equal(uiThread, tray.ThreadId); Assert.Equal("Synthetic bounded preview", Assert.Single(tray.Messages));
        Assert.False(host.Services.GetRequiredService<MainWindow>().IsVisible); Assert.False(host.Services.GetRequiredService<ControlWindow>().IsVisible);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.ShowAsync("cancelled", cancel.Token));
        sink.Detach(); await Assert.ThrowsAsync<ChannelDeliveryException>(() => sink.ShowAsync("detached", default)); Assert.Single(tray.Messages);
    });

    private static FixedNotificationSubscriptions Source(NotificationEventKind kind) => new([new(DesktopRoute, [kind], DateTimeOffset.UtcNow.AddHours(-1)), new(TelegramRoute, [kind], DateTimeOffset.UtcNow.AddHours(-1))]);
    private static ChannelOwnershipPolicy Policy(TemporaryAgentDatabase database) => new(Owner, new SqliteChannelBindingRepository(database));
    private static async Task Allow(TemporaryAgentDatabase database, ChannelRoute route)
    { var bindings = new SqliteChannelBindingRepository(database); var pending = await bindings.RequestPairingAsync(route, route == DesktopRoute ? Owner.UserId : "42", Owner); await bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true); }
    private static IHost Host(TemporaryAgentDatabase database, Sink sink, INotificationSubscriptionSource subscriptions, TimeProvider clock, Http http) =>
        AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton(Owner); services.AddSingleton(clock); services.AddSingleton<IDesktopNotificationSink>(sink); services.AddSingleton(subscriptions);
            services.AddSingleton<IOutboundChannel>(provider => new TelegramOutboundChannel(TelegramRoute.ConnectionId, "reference", new Secrets(), provider.GetRequiredService<IChannelAccessPolicy>(), handler: http));
        }).Build();
    private sealed class Sink : IDesktopNotificationSink
    {
        private readonly object sync = new(); private readonly List<string> messages = [];
        public IReadOnlyList<string> Messages { get { lock (sync) return messages.ToArray(); } }
        public Task ShowAsync(string text, CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (sync) messages.Add(text); return Task.CompletedTask; }
    }
    private sealed class Tray : ITrayIcon
    {
        public List<string> Messages = []; public int ThreadId;
        public void Start(ControlPanelViewModel model, Action open, Action controls, Action exit) => throw new InvalidOperationException("Do not create native tray in tests.");
        public void Refresh(string status) { } public void Dispose() { }
        public void ShowNotification(string text) { ThreadId = Environment.CurrentManagedThreadId; Messages.Add(text); }
    }
    private sealed class AllowPolicy : IChannelAccessPolicy
    {
        public ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) => ValueTask.FromResult(true);
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => ValueTask.FromResult<string?>("12345:synthetic-token");
    }
    private sealed class Http : HttpMessageHandler
    {
        public bool Offline; public int Attempts, Accepted;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Attempts); if (Offline) throw new HttpRequestException(HttpRequestError.NameResolutionError, "Synthetic offline"); Interlocked.Increment(ref Accepted);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1,\"chat\":{\"id\":24}}}") }); }
    }
    private sealed class FailedDesktopEnqueue(INotificationOutbox inner) : INotificationOutbox
    {
        public bool Fail = true;
        public Task<NotificationDelivery> EnqueueAsync(Guid eventId, OutgoingMessage message, CancellationToken ct) => Fail && message.Destination.ChannelId == "desktop" ? throw new System.IO.IOException("Synthetic enqueue failure") : inner.EnqueueAsync(eventId, message, ct);
        public Task<NotificationDelivery?> GetAsync(Guid messageId, CancellationToken ct) => inner.GetAsync(messageId, ct);
        public Task<NotificationDelivery?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct) => inner.ClaimNextAsync(now, ct);
        public Task<NotificationDelivery> FinishAsync(Guid messageId, long expectedVersion, NotificationDeliveryStatus status, DateTimeOffset now, DateTimeOffset nextAttemptAt, string? errorCode, CancellationToken ct) => inner.FinishAsync(messageId, expectedVersion, status, now, nextAttemptAt, errorCode, ct);
        public Task RecoverSendingAsync(DateTimeOffset now, CancellationToken ct) => inner.RecoverSendingAsync(now, ct);
    }
    private static async Task Until(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await test(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
