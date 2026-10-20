using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

public sealed class TelegramPollingTests
{
    private const string Token = "12345:synthetic-secret-never-log";
    private static readonly ChannelRoute Route = new("telegram", "fixture-bot", "-100000000001");
    private static readonly ApprovalOwner Owner = new("desktop", "synthetic-owner");

    [Fact]
    public async Task DuplicatesAcrossBatchesAndReceiverRestartCreateOneDurableRun()
    {
        await using var fixture = await Fixture.Start(); await fixture.Approve();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new Script(stop, Batch(Message(42), Message(42)), Batch(Message(42)));
        using var receiver = fixture.Receiver(http); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Router.RunInboundAsync(receiver, stop.Token));
        Assert.Equal(new long[] { 0, 43, 43 }, http.Offsets); Assert.Equal(new ChannelCursor(43, 1), await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId));
        var run = Assert.Single(await fixture.Runs.ListAsync());
        Assert.Equal(Core.Domain.RunStatus.Completed, (await fixture.Coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var again = new Script(restart, Batch(Message(42)));
        using var resumed = fixture.Receiver(again); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Router.RunInboundAsync(resumed, restart.Token));
        Assert.All(again.Offsets, value => Assert.Equal(43, value)); Assert.Equal(run.Id, Assert.Single(await fixture.Runs.ListAsync()).Id);
    }

    [Fact]
    public async Task CrashAfterDurableSubmissionBeforeOffsetWriteReplaysTheSameRun()
    {
        await using var fixture = await Fixture.Start(); await fixture.Approve();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var http = new Script(stop, Batch(Message(42), Message(43)));
        using var receiver = fixture.Receiver(http, new FailedAdvance(fixture.Cursors));
        await Assert.ThrowsAsync<IOException>(() => fixture.Router.RunInboundAsync(receiver, stop.Token));
        Assert.Equal(new ChannelCursor(), await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId));
        var before = Assert.Single(await fixture.Runs.ListAsync()); Assert.Single(http.Offsets);
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var again = new Script(retry, Batch(Message(42)));
        using var resumed = fixture.Receiver(again); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Router.RunInboundAsync(resumed, retry.Token));
        Assert.Equal(before.Id, Assert.Single(await fixture.Runs.ListAsync()).Id); Assert.Equal(43, (await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
    }

    [Fact]
    public async Task CallbackFailureOrCancellationNeverAcknowledgesThatUpdateOrProcessesLaterOnes()
    {
        await using var fixture = await Fixture.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var http = new Script(stop, Batch(Message(10), Message(11), Message(12)));
        using var receiver = fixture.Receiver(http); var seen = new List<string>();
        await Assert.ThrowsAsync<IOException>(() => receiver.RunAsync((message, ct) =>
        {
            seen.Add(message.ExternalMessageId); if (message.ExternalMessageId == "11") throw new IOException("Synthetic acceptance failure"); return Task.CompletedTask;
        }, stop.Token));
        Assert.Equal(new[] { "10", "11" }, seen); Assert.Equal(11, (await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var again = new Script(cancel, Batch(Message(11), Message(12)));
        using var resumed = fixture.Receiver(again);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumed.RunAsync((_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancel.Token));
        Assert.Equal(11, (await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
    }

    [Fact]
    public async Task UnknownPeerIsRecordedPendingAndDiscardedWithoutTaskOrBodyRetention()
    {
        await using var fixture = await Fixture.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var http = new Script(stop, Batch(Message(7)));
        using var receiver = fixture.Receiver(http); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Router.RunInboundAsync(receiver, stop.Token));
        Assert.Empty(await fixture.Runs.ListAsync()); Assert.Equal(8, (await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
        var pending = Assert.Single(await fixture.Bindings.ListAsync(Owner)); Assert.Equal(ChannelBindingStatus.Pending, pending.Status);
        await using var db = fixture.Database.Open(); Assert.DoesNotContain("synthetic task", JsonSerializer.Serialize(await db.ChannelBindings.ToArrayAsync()));
    }

    [Fact]
    public async Task TransportMappingUsesCallbackActorAndSkipsUnsupportedBotAndServiceUpdates()
    {
        await using var fixture = await Fixture.Start();
        var callback = """{"update_id":9,"callback_query":{"id":"query-id","from":{"id":9000000001,"is_bot":false},"message":{"chat":{"id":-100000000001},"from":{"id":12345,"is_bot":true},"date":1800000000},"data":"opaque-action"}}""";
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var http = new Script(stop,
            Batch("""{"update_id":6,"edited_message":{}}""", Message(7, bot: true), """{"update_id":8,"message":{"photo":[]}}""", callback));
        using var receiver = fixture.Receiver(http); var seen = new List<IncomingMessage>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RunAsync((message, _) => { seen.Add(message); return Task.CompletedTask; }, stop.Token));
        var actual = Assert.Single(seen); Assert.Equal("9000000001", actual.SenderId); Assert.Equal(Route, actual.Route);
        Assert.Equal("opaque-action", actual.ActionToken); Assert.Equal("query-id", actual.CorrelationKey); Assert.Equal("9", actual.ExternalMessageId);
        Assert.Equal(10, (await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
    }

    [Theory]
    [InlineData(401, "Unauthorized", 1)]
    [InlineData(409, "PollingConflict", 1)]
    [InlineData(503, "ServiceUnavailable", 3)]
    [InlineData(429, "RateLimited", 3)]
    public async Task ProviderFailuresHaveBoundedReadRetriesAndSanitizedDiagnostics(int status, string code, int attempts)
    {
        await using var fixture = await Fixture.Start(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new Script(stop, Enumerable.Repeat(JsonSerializer.Serialize(new { ok = false, error_code = status, description = Token, parameters = new { retry_after = 0 } }), attempts).ToArray()) { Status = (HttpStatusCode)status };
        using var receiver = fixture.Receiver(http);
        var error = await Assert.ThrowsAsync<TelegramPollingException>(() => receiver.RunAsync((_, _) => throw new Exception("Must not accept"), stop.Token));
        Assert.Equal(code, error.Code); Assert.DoesNotContain(Token, error.ToString()); Assert.Equal(attempts, http.Offsets.Count);
        Assert.Equal(new ChannelCursor(), await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId));
    }

    [Fact]
    public async Task TransportExceptionContainingCredentialIsRedactedAndCancellationStopsPendingRequest()
    {
        await using var fixture = await Fixture.Start(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new Script(stop) { Failure = new HttpRequestException("https://api.telegram.org/bot" + Token) };
        using var receiver = fixture.Receiver(http, attempts: 1);
        var error = await Assert.ThrowsAsync<TelegramPollingException>(() => receiver.RunAsync((_, _) => Task.CompletedTask, stop.Token));
        Assert.Equal("TransportUnavailable", error.Code); Assert.DoesNotContain(Token, error.ToString());
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var pending = new Script(cancel) { Hold = true };
        using var waiting = fixture.Receiver(pending); var polling = waiting.RunAsync((_, _) => Task.CompletedTask, cancel.Token);
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
    }

    [Fact]
    public async Task SuccessfulPollAfterTransientFailureUsesUnchangedOffset()
    {
        await using var fixture = await Fixture.Start(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new Script(stop, "{\"ok\":false,\"error_code\":503}", Batch(Message(42)));
        using var receiver = fixture.Receiver(http); var seen = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RunAsync((message, _) => { seen.Add(message.ExternalMessageId); return Task.CompletedTask; }, stop.Token));
        Assert.Equal(new long[] { 0, 0, 43 }, http.Offsets); Assert.Equal("42", Assert.Single(seen));
    }

    [Theory]
    [InlineData("{\"ok\":true,\"result\":[{\"update_id\":\"42\"}]}", "InvalidUpdateId")]
    [InlineData("{\"ok\":true,\"result\":{}}", "InvalidResponse")]
    [InlineData("not-json", "InvalidResponse")]
    [InlineData("{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":3600}}", "RateLimited")]
    public async Task InvalidOrLongRateLimitedResponseStopsWithoutAdvancingOrAccepting(string response, string code)
    {
        await using var fixture = await Fixture.Start(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new Script(stop, response); using var receiver = fixture.Receiver(http);
        var error = await Assert.ThrowsAsync<TelegramPollingException>(() => receiver.RunAsync((_, _) => throw new Exception("Must not accept"), stop.Token));
        Assert.Equal(code, error.Code); Assert.Single(http.Offsets); Assert.Equal(new ChannelCursor(), await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId));
    }

    [Fact]
    public async Task CursorIsConnectionScopedMonotonicRevisionCheckedAndRejectsUnknownSchema()
    {
        await using var fixture = await Fixture.Start(); var cursor = await fixture.Cursors.ReadAsync("telegram", Route.ConnectionId);
        var next = await fixture.Cursors.AdvanceAsync("telegram", Route.ConnectionId, cursor, 42);
        await Assert.ThrowsAsync<ChannelCursorConflictException>(() => fixture.Cursors.AdvanceAsync("telegram", Route.ConnectionId, cursor, 43));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Cursors.AdvanceAsync("telegram", Route.ConnectionId, next, 41));
        Assert.Equal(new ChannelCursor(), await fixture.Cursors.ReadAsync("telegram", "other-bot"));
        Assert.Equal(next, await new SqliteChannelCursorRepository(fixture.Database).ReadAsync("telegram", Route.ConnectionId));
        await using var db = fixture.Database.Open(); await db.ChannelCursors.ExecuteUpdateAsync(set => set.SetProperty(row => row.PayloadSchemaVersion, 99));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Cursors.ReadAsync("telegram", Route.ConnectionId));
    }

    private static string Message(long id, bool bot = false) => JsonSerializer.Serialize(new { update_id = id, message = new { message_id = 27, date = 1800000000, from = new { id = 9000000001L, is_bot = bot }, chat = new { id = -100000000001L }, text = "synthetic task" } });
    private static string Batch(params string[] updates) => "{\"ok\":true,\"result\":[" + string.Join(',', updates) + "]}";
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) { Assert.Equal("opaque-reference", reference); return ValueTask.FromResult<string?>(Token); }
    }
    private sealed class FailedAdvance(IChannelCursorRepository inner) : IChannelCursorRepository
    {
        public Task<ChannelCursor> ReadAsync(string channelId, string connectionId, CancellationToken ct) => inner.ReadAsync(channelId, connectionId, ct);
        public Task<ChannelCursor> AdvanceAsync(string channelId, string connectionId, ChannelCursor expected, long nextSequence, CancellationToken ct) => throw new IOException("Synthetic cursor failure");
    }
    private sealed class Script(CancellationTokenSource stop, params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responses);
        public List<long> Offsets = []; public HttpStatusCode Status = HttpStatusCode.OK; public Exception? Failure; public bool Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("/answerCallbackQuery"))
            {
                using var answer = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("query-id", answer.RootElement.GetProperty("callback_query_id").GetString());
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
            }
            Assert.EndsWith("/getUpdates", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Offsets.Add(body.RootElement.GetProperty("offset").GetInt64());
            Assert.Equal(30, body.RootElement.GetProperty("timeout").GetInt32()); Assert.Equal(100, body.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(new[] { "message", "callback_query" }, body.RootElement.GetProperty("allowed_updates").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain(Token, body.RootElement.ToString()); Entered.TrySetResult();
            if (Failure is not null) throw Failure;
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (responses.Count == 0) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            return new(Status) { Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new(); public IHost Host = null!;
        public IChannelCursorRepository Cursors => Host.Services.GetRequiredService<IChannelCursorRepository>();
        public IChannelBindingRepository Bindings => Host.Services.GetRequiredService<IChannelBindingRepository>();
        public IRunRepository Runs => Host.Services.GetRequiredService<IRunRepository>();
        public TaskRunCoordinator Coordinator => Host.Services.GetRequiredService<TaskRunCoordinator>();
        public ChannelMessageRouter Router => Host.Services.GetRequiredService<ChannelMessageRouter>();
        public static async Task<Fixture> Start()
        {
            var value = new Fixture(); value.Host = AppHost.CreateBuilder([], value.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
                services.AddSingleton<IChannelAccessPolicy>(provider => new ChannelOwnershipPolicy(Owner, provider.GetRequiredService<IChannelBindingRepository>()))).Build();
            await value.Host.StartAsync(); return value;
        }
        public async Task Approve()
        { var pending = await Bindings.RequestPairingAsync(Route, "9000000001", Owner); await Bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true); }
        public TelegramPollingReceiver Receiver(HttpMessageHandler http, IChannelCursorRepository? cursor = null, int attempts = 3) => new(new(Route.ConnectionId, "opaque-reference", MaxPollAttempts: attempts), new Secrets(), cursor ?? Cursors, http);
        public async ValueTask DisposeAsync() { await Host.StopAsync(); Host.Dispose(); Database.Dispose(); }
    }
}
