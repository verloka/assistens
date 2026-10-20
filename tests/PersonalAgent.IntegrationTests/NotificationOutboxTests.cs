using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class NotificationOutboxTests
{
    private static readonly ChannelRoute Route = new("telegram", "fixture-bot", "42");

    [Fact]
    public async Task DnsOutageKeepsCompletedRunAndDeliversOnceAfterDurableRetryAndReopen()
    {
        using var fixture = await Fixture.Start(); using var http = new Http { Offline = true };
        using var channel = new TelegramOutboundChannel(Route.ConnectionId, "reference", new Secrets(), new Policy(), handler: http);
        var dispatcher = fixture.Dispatcher(channel); var message = fixture.Message(); var before = await fixture.RunJson();
        var queued = await fixture.Outbox.EnqueueAsync(fixture.EventId, message); Assert.Equal(0, queued.Attempts);
        Assert.Equal(1, await dispatcher.TickAsync()); var pending = (await fixture.Outbox.GetAsync(message.Id))!;
        Assert.Equal(NotificationDeliveryStatus.Pending, pending.Status); Assert.Equal(1, pending.Attempts); Assert.True(pending.NextAttemptAtUtc > fixture.Clock.Now);
        Assert.Equal(0, await dispatcher.TickAsync()); Assert.Equal(1, http.Attempts); Assert.Equal(0, http.Accepted);
        fixture.Clock.Now = pending.NextAttemptAtUtc; http.Offline = false;
        var reopened = new SqliteNotificationOutbox(fixture.Database, fixture.Clock);
        using (var host = AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        { services.AddSingleton<TimeProvider>(fixture.Clock); services.AddSingleton<IOutboundChannel>(channel); services.AddSingleton<IChannelAccessPolicy>(new Policy()); }).Build())
        {
            await host.StartAsync(); await Until(async () => (await reopened.GetAsync(message.Id))?.Status == NotificationDeliveryStatus.Delivered); await host.StopAsync();
        }
        var delivered = (await reopened.GetAsync(message.Id))!; Assert.Equal(NotificationDeliveryStatus.Delivered, delivered.Status); Assert.Equal(2, delivered.Attempts); Assert.NotNull(delivered.DeliveredAtUtc);
        Assert.Equal(0, await dispatcher.TickAsync()); Assert.Equal(1, http.Accepted); Assert.Equal(2, http.Attempts);
        Assert.Equal(delivered.Version, (await reopened.EnqueueAsync(fixture.EventId, message)).Version); Assert.Equal(before, await fixture.RunJson());
    }

    [Fact]
    public async Task CompetingDispatchersClaimOnePersistedAttemptAndCannotReplacePayload()
    {
        using var fixture = await Fixture.Start(); var channel = new Adapter(); var message = fixture.Message();
        var queued = (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Outbox.EnqueueAsync(fixture.EventId, message))))[0];
        await Assert.ThrowsAsync<NotificationConflictException>(() => fixture.Outbox.EnqueueAsync(fixture.EventId, message with { Text = "Changed result" }));
        await Assert.ThrowsAsync<NotificationConflictException>(() => fixture.Outbox.EnqueueAsync(Guid.NewGuid(), message));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Dispatcher(channel).TickAsync()));
        Assert.Equal(1, channel.Calls); var delivered = (await fixture.Outbox.GetAsync(message.Id))!; Assert.Equal(NotificationDeliveryStatus.Delivered, delivered.Status); Assert.Equal(1, delivered.Attempts);
        await using (var db = fixture.Database.Open()) Assert.Equal(1, await db.NotificationOutbox.CountAsync());
        await Assert.ThrowsAsync<NotificationConflictException>(() => fixture.Outbox.FinishAsync(message.Id, queued.Version, NotificationDeliveryStatus.Delivered, fixture.Clock.Now, fixture.Clock.Now, null));
    }

    [Fact]
    public async Task LostLocalAcknowledgementRecoversUnknownWithoutReplayAndHostedWorkerContinuesIndependentMessage()
    {
        using var fixture = await Fixture.Start(); var channel = new Adapter(); var original = fixture.Message(); var before = await fixture.RunJson();
        await fixture.Outbox.EnqueueAsync(fixture.EventId, original);
        await Assert.ThrowsAsync<IOException>(() => fixture.Dispatcher(channel, new FailedFinish(fixture.Outbox)).TickAsync());
        Assert.Equal(1, channel.Calls); Assert.Equal(NotificationDeliveryStatus.Sending, (await fixture.Outbox.GetAsync(original.Id))!.Status);
        var independent = fixture.Message() with { Text = "Independent notification" }; await fixture.Outbox.EnqueueAsync(Guid.NewGuid(), independent);
        for (var restart = 0; restart < 2; restart++)
        {
            using var host = AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
            { services.AddSingleton<IOutboundChannel>(channel); services.AddSingleton<IChannelAccessPolicy>(new Policy()); }).Build();
            await host.StartAsync();
            await Until(async () => (await fixture.Outbox.GetAsync(independent.Id))?.Status == NotificationDeliveryStatus.Delivered);
            Assert.Equal(NotificationDeliveryStatus.OutcomeUnknown, (await fixture.Outbox.GetAsync(original.Id))!.Status);
            await host.StopAsync();
        }
        Assert.Equal(2, channel.Calls); Assert.Equal(before, await fixture.RunJson());
    }

    [Fact]
    public async Task ExplicitRateLimitHonorsMinimumDelayAndStopsAtAttemptBudget()
    {
        using var fixture = await Fixture.Start(); var channel = new Adapter { Failure = new ChannelDeliveryException("SECRETproviderdescription", false, true, retryAfter: TimeSpan.FromSeconds(120)) };
        var message = fixture.Message(); await fixture.Outbox.EnqueueAsync(fixture.EventId, message);
        var dispatcher = fixture.Dispatcher(channel, options: new() { MaxAttempts = 2 }); await dispatcher.TickAsync();
        var first = (await fixture.Outbox.GetAsync(message.Id))!; Assert.Equal(fixture.Clock.Now.AddSeconds(120), first.NextAttemptAtUtc);
        fixture.Clock.Now = first.NextAttemptAtUtc.AddTicks(-1); Assert.Equal(0, await dispatcher.TickAsync());
        fixture.Clock.Now = first.NextAttemptAtUtc; await dispatcher.TickAsync();
        var failed = (await fixture.Outbox.GetAsync(message.Id))!; Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status); Assert.Equal("DeliveryAttemptsExhausted", failed.ErrorCode);
        Assert.Equal(2, channel.Calls); Assert.Equal(0, await dispatcher.TickAsync()); Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(failed));
    }

    [Theory]
    [InlineData("unknown", NotificationDeliveryStatus.OutcomeUnknown)]
    [InlineData("partial", NotificationDeliveryStatus.Failed)]
    [InlineData("rejected", NotificationDeliveryStatus.Failed)]
    [InlineData("untyped", NotificationDeliveryStatus.OutcomeUnknown)]
    public async Task UnknownPartialAndPermanentFailuresDoNotRetryOrChangeCompletedRun(string failure, NotificationDeliveryStatus expected)
    {
        using var fixture = await Fixture.Start(); var before = await fixture.RunJson(); var channel = new Adapter { Failure = failure switch
        {
            "unknown" => new ChannelDeliveryException("SECRET", true), "partial" => new ChannelDeliveryException("SECRET", false, true, acceptedParts: 1),
            "rejected" => new ChannelDeliveryException("SECRET", false), _ => new IOException("SECRETproviderbody")
        } };
        var message = fixture.Message(); await fixture.Outbox.EnqueueAsync(fixture.EventId, message); var dispatcher = fixture.Dispatcher(channel);
        await dispatcher.TickAsync(); Assert.Equal(expected, (await fixture.Outbox.GetAsync(message.Id))!.Status);
        fixture.Clock.Now += TimeSpan.FromDays(1); Assert.Equal(0, await dispatcher.TickAsync()); Assert.Equal(1, channel.Calls); Assert.Equal(before, await fixture.RunJson());
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(await fixture.Outbox.GetAsync(message.Id)));
    }

    [Fact]
    public async Task ExpiredAndUnauthorizedMessagesFailWithoutProviderAndOtherPendingMessagesContinue()
    {
        using var fixture = await Fixture.Start(); var channel = new Adapter(); var expired = fixture.Message(); await fixture.Outbox.EnqueueAsync(fixture.EventId, expired);
        fixture.Clock.Now += TimeSpan.FromDays(2); var fresh = fixture.Message(); await fixture.Outbox.EnqueueAsync(Guid.NewGuid(), fresh);
        var unauthorized = fixture.Message() with { Destination = Route with { DestinationId = "foreign" } }; await fixture.Outbox.EnqueueAsync(Guid.NewGuid(), unauthorized);
        Assert.Equal(3, await fixture.Dispatcher(channel).TickAsync()); Assert.Equal(1, channel.Calls);
        Assert.Equal("DeliveryExpired", (await fixture.Outbox.GetAsync(expired.Id))!.ErrorCode);
        Assert.Equal(NotificationDeliveryStatus.Delivered, (await fixture.Outbox.GetAsync(fresh.Id))!.Status);
        Assert.Equal("DeliveryNotAuthorizedOrInvalid", (await fixture.Outbox.GetAsync(unauthorized.Id))!.ErrorCode);
    }

    [Fact]
    public async Task CancellationDuringSendPersistsUnknownBeforeShutdownAndDoesNotRetry()
    {
        using var fixture = await Fixture.Start(); var channel = new Adapter { Hold = true }; using var stop = new CancellationTokenSource();
        var message = fixture.Message(); await fixture.Outbox.EnqueueAsync(fixture.EventId, message); var dispatcher = fixture.Dispatcher(channel);
        var dispatching = dispatcher.TickAsync(stop.Token); await channel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatching);
        Assert.Equal(NotificationDeliveryStatus.OutcomeUnknown, (await fixture.Outbox.GetAsync(message.Id))!.Status);
        Assert.Equal(0, await dispatcher.TickAsync()); Assert.Equal(1, channel.Calls);
    }

    private static async Task Until(Func<Task<bool>> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!await condition()) await Task.Delay(20, stop.Token); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Policy : IChannelAccessPolicy
    {
        public ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) => ValueTask.FromResult(true);
        public ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct) => ValueTask.FromResult(message.Destination == Route);
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("12345:synthetic-token");
    }
    private sealed class Http : HttpMessageHandler
    {
        public bool Offline; public int Attempts, Accepted;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++; if (Offline) throw new HttpRequestException(HttpRequestError.NameResolutionError, "Synthetic unavailable DNS"); Accepted++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1,\"chat\":{\"id\":42}}}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Adapter : IOutboundChannel
    {
        public string ChannelId => "telegram"; public string ConnectionId => Route.ConnectionId; public ChannelCapabilities Capabilities { get; } = new();
        public int Calls; public Exception? Failure; public bool Hold; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, ct); if (Failure is not null) throw Failure; }
    }
    private sealed class FailedFinish(INotificationOutbox inner) : INotificationOutbox
    {
        public Task<NotificationDelivery> EnqueueAsync(Guid eventId, OutgoingMessage message, CancellationToken ct) => inner.EnqueueAsync(eventId, message, ct);
        public Task<NotificationDelivery?> GetAsync(Guid messageId, CancellationToken ct) => inner.GetAsync(messageId, ct);
        public Task<NotificationDelivery?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct) => inner.ClaimNextAsync(now, ct);
        public Task<NotificationDelivery> FinishAsync(Guid messageId, long expectedVersion, NotificationDeliveryStatus status, DateTimeOffset now, DateTimeOffset nextAttemptAt, string? errorCode, CancellationToken ct) => throw new IOException("Synthetic lost local delivery acknowledgement");
        public Task RecoverSendingAsync(DateTimeOffset now, CancellationToken ct) => inner.RecoverSendingAsync(now, ct);
    }
    private sealed class Fixture : IDisposable
    {
        public TemporaryAgentDatabase Database = new(); public Clock Clock = new(); public SqliteNotificationOutbox Outbox = null!;
        public TaskRunCoordinator Runs = null!; public SqliteRunRepository Repository = null!; public Guid RunId, EventId;
        public static async Task<Fixture> Start()
        {
            var fixture = new Fixture(); await using (var db = fixture.Database.Open()) await db.Database.MigrateAsync();
            fixture.Repository = new(fixture.Database); fixture.Runs = new(fixture.Repository); fixture.Outbox = new(fixture.Database, fixture.Clock);
            var run = await fixture.Runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic notification source", new("desktop"))); fixture.RunId = run.Id;
            using var stop = new CancellationTokenSource(); var runner = new TaskRunner(fixture.Runs, new FakeAgentRuntime()).RunAsync(stop.Token);
            await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); await runner;
            fixture.EventId = fixture.Runs.GetEvents(run.Id).Last().Id; return fixture;
        }
        public OutgoingMessage Message() => TelegramOutboundChannel.RunMessage(Guid.NewGuid(), Route, Runs.GetRun(RunId));
        public async Task<string> RunJson() => JsonSerializer.Serialize(await Repository.GetAsync(RunId));
        public NotificationDispatcher Dispatcher(IOutboundChannel channel, INotificationOutbox? outbox = null, NotificationDeliveryOptions? options = null) => new(outbox ?? Outbox, new(Runs, [channel], new Policy()), Clock, options);
        public void Dispose() { Runs.Dispose(); Database.Dispose(); }
    }
}
