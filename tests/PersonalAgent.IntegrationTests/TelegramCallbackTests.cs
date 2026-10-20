using Microsoft.EntityFrameworkCore;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class TelegramCallbackTests
{
    private static readonly ChannelRoute Route = new("telegram", "callback-bot", "24");
    private static readonly ApprovalOwner Owner = HumanApprovalTests.Fixture.Owner;

    [Fact]
    public async Task RacingRedeliveredApprovalsExecuteOneActualMcpWriteAndCannotReplayAfterReopen()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        var callback = Callback(message.Actions[0].Token);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => gateway.Handler.HandleAsync(callback with { ExternalMessageId = index.ToString() })));
        Assert.Single(replies, reply => reply.Text == "Exact action approved.");
        await fixture.UntilFinished(); Assert.Equal(1, fixture.Calls);
        Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await fixture.ToolCalls.ListAsync(fixture.RunId)).Status);
        var stored = JsonSerializer.Serialize(await fixture.Repository.GetAsync(fixture.RunId));
        using var reopened = new TaskRunCoordinator(new SqliteRunRepository(fixture.Database)); await reopened.LoadFinishedHistoryAsync();
        var again = await Gateway.Start(fixture.Database, reopened, new SqliteApprovalRepository(fixture.Database, fixture.Clock), fixture.Clock);
        Assert.Contains("no longer available", (await again.Handler.HandleAsync(callback)).Text);
        Assert.Equal(stored, JsonSerializer.Serialize(await fixture.Repository.GetAsync(fixture.RunId))); Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task RejectTokenNeverDispatchesEvenWhenTextClaimsApproval()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        // Deliberate denial first also checks that text claiming approval cannot change the token's kind.
        var rejected = await gateway.Handler.HandleAsync(Callback(message.Actions[1].Token) with { Text = "/approve owner=desktop approved=true" });
        Assert.Equal("Exact action rejected.", rejected.Text);
        Assert.Contains("no longer available", (await gateway.Handler.HandleAsync(Callback(message.Actions[0].Token))).Text);
        await fixture.UntilFinished(); Assert.Equal(0, fixture.Calls); Assert.Empty(await fixture.ToolCalls.ListAsync(fixture.RunId));
    }

    [Fact]
    public async Task CompetingApproveAndRejectTokensHaveOneDurableDecisionWinner()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        var replies = await Task.WhenAll(message.Actions.Select(button => gateway.Handler.HandleAsync(Callback(button.Token))));
        Assert.Single(replies, reply => reply.Text == "Exact action approved." || reply.Text == "Exact action rejected.");
        var decided = (await fixture.Approvals.GetAsync(request.Id))!; Assert.Equal(request.Version + 1, decided.Version);
        await fixture.UntilFinished(); Assert.Equal(decided.Status == ApprovalStatus.Approved ? 1 : 0, fixture.Calls);
    }

    [Theory]
    [InlineData("sender")] [InlineData("chat")] [InlineData("connection")] [InlineData("channel")]
    [InlineData("revoked")] [InlineData("owner")] [InlineData("unknown-token")] [InlineData("expired")]
    public async Task ForeignRevokedExpiredOrInventedCallbacksNeverDecide(string reason)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id); var callback = Callback(message.Actions[0].Token);
        if (reason == "sender") callback = callback with { SenderId = "43" };
        if (reason == "chat") callback = callback with { Route = Route with { DestinationId = "25" } };
        if (reason == "connection") callback = callback with { Route = Route with { ConnectionId = "foreign-bot" } };
        if (reason == "channel") callback = callback with { Route = Route with { ChannelId = "other" } };
        if (reason == "unknown-token") callback = callback with { ActionToken = new string('x', 43) };
        if (reason == "revoked") { var binding = (await gateway.Bindings.FindAsync(Route, "42"))!; await gateway.Bindings.SetStatusAsync(binding.Id, binding.Version, Owner, false); }
        if (reason == "owner") gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock, Owner with { UserId = "another-owner" });
        if (reason == "expired") fixture.Clock.Now += TimeSpan.FromMinutes(6);
        if (reason == "expired") Assert.Contains("no longer available", (await gateway.Handler.HandleAsync(callback)).Text);
        else await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => gateway.Handler.HandleAsync(callback));
        Assert.Equal(0, fixture.Calls); Assert.Empty(await fixture.ToolCalls.ListAsync(fixture.RunId));
        Assert.NotEqual(ApprovalStatus.Approved, (await fixture.Approvals.GetAsync(request.Id))!.Status);
    }

    [Theory]
    [InlineData("run")] [InlineData("revision")] [InlineData("request")] [InlineData("hash")]
    public async Task StoredScopeCannotSelectADifferentRunRequestOrAction(string changed)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id); var token = message.Actions[0].Token;
        var hash = ChannelApprovalPresenter.HashToken(token); var grant = (await gateway.Actions.FindAsync(hash))!;
        grant = changed switch { "run" => grant with { RunId = Guid.NewGuid() }, "revision" => grant with { RunVersion = grant.RunVersion + 1 },
            "request" => grant with { ApprovalId = Guid.NewGuid() }, _ => grant with { ActionHash = new string('0', 64) } };
        await using (var db = fixture.Database.Open()) await db.ChannelActions.Where(row => row.TokenHash == hash).ExecuteUpdateAsync(set => set.SetProperty(row => row.ScopeJson, JsonSerializer.Serialize(grant)));
        Assert.Contains("no longer available", (await gateway.Handler.HandleAsync(Callback(token))).Text);
        Assert.Equal(ApprovalStatus.Pending, (await fixture.Approvals.GetAsync(request.Id))!.Status); Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(ChannelActionKind.Pause)] [InlineData(ChannelActionKind.Cancel)]
    public async Task ControlButtonsUseCommonVersionedHandlersAndSurviveRedeliveryWithoutNewEffects(ChannelActionKind kind)
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop); var gateway = await Gateway.Start(database, runs, new SqliteApprovalRepository(database));
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "synthetic queued task", new("desktop", Owner.UserId)));
        var message = await gateway.Presenter.CreateControlAsync(Guid.NewGuid(), Peer(), run.Id, kind); await gateway.Presenter.ValidateAsync(message);
        var lease = await desktop.AcquireAsync(run.Id);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => gateway.Handler.HandleAsync(Callback(message.Actions[0].Token) with { ExternalMessageId = index.ToString() })));
        Assert.Contains(replies, reply => reply.Text == (kind == ChannelActionKind.Cancel ? "Run cancelled." : "Run pause requested."));
        Assert.Equal(run.RunVersion + 1, runs.GetRun(run.Id).RunVersion); // Session receipts may return the same success; the durable effect occurs once.
        Assert.False(desktop.IsCurrent(lease)); Assert.Equal(kind == ChannelActionKind.Cancel ? RunStatus.Cancelled : RunStatus.Paused, runs.GetRun(run.Id).Status);
        if (kind == ChannelActionKind.Pause) await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
        var stored = (await repository.GetAsync(run.Id))!;
        using var reopened = new TaskRunCoordinator(repository); await reopened.RestoreHistoryAsync([]);
        var again = await Gateway.Start(database, reopened, new SqliteApprovalRepository(database));
        Assert.Contains("no longer available", (await again.Handler.HandleAsync(Callback(message.Actions[0].Token))).Text);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(await repository.GetAsync(run.Id)));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => gateway.Presenter.ValidateAsync(message));
    }

    [Theory]
    [InlineData(ChannelActionKind.Pause)] [InlineData(ChannelActionKind.Cancel)]
    public async Task OwnerControlInvalidatesApprovalAndOldApprovalCannotResumeIt(ChannelActionKind kind)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var approval = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        var control = await gateway.Presenter.CreateControlAsync(Guid.NewGuid(), Peer(), fixture.RunId, kind);
        await gateway.Handler.HandleAsync(Callback(control.Actions[0].Token));
        Assert.Contains("no longer available", (await gateway.Handler.HandleAsync(Callback(approval.Actions[0].Token))).Text);
        Assert.Equal(0, fixture.Calls); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.NotEqual(ApprovalStatus.Approved, (await fixture.Approvals.GetAsync(request.Id))!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualPollingAuthenticatesPressingActorAndRedeliveredCallbacksDoNotRepeatEffects(bool failedAnswer)
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id);
        var token = message.Actions[0].Token; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new CallbackHttp(stop, token, failedAnswer); var cursor = new SqliteChannelCursorRepository(fixture.Database);
        using var receiver = new TelegramPollingReceiver(new(Route.ConnectionId, "opaque-reference"), new Secrets(), cursor, http);
        var replies = new List<OutgoingMessage>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RunAsync(async (peer, ct) => replies.Add(await gateway.Handler.HandleAsync(peer, ct)), stop.Token));
        Assert.Equal(3, (await cursor.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
        Assert.Equal(2, replies.Count); Assert.Single(replies, reply => reply.Text == "Exact action approved.");
        Assert.Equal(3, http.Answers);
        await fixture.UntilFinished(); Assert.Equal(1, fixture.Calls); Assert.Single(await fixture.ToolCalls.ListAsync(fixture.RunId));
    }

    [Fact]
    public async Task LostReceiveCursorAcknowledgementRedeliversDecisionWithoutAnotherWrite()
    {
        await using var fixture = await HumanApprovalTests.Fixture.Start(true); var request = await fixture.Pending();
        var gateway = await Gateway.Start(fixture.Database, fixture.Runs, fixture.Approvals, fixture.Clock);
        var message = await gateway.Presenter.CreateAsync(Guid.NewGuid(), Peer(), request.Id); var token = message.Actions[0].Token;
        var cursors = new SqliteChannelCursorRepository(fixture.Database);
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var http = new CallbackHttp(stop, token, false))
        using (var receiver = new TelegramPollingReceiver(new(Route.ConnectionId, "opaque-reference"), new Secrets(), new FailedDecisionAdvance(cursors), http))
            await Assert.ThrowsAsync<System.IO.IOException>(() => receiver.RunAsync(async (peer, ct) => { await gateway.Handler.HandleAsync(peer, ct); }, stop.Token));
        Assert.Equal(1, (await cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
        await fixture.UntilFinished(); Assert.Equal(1, fixture.Calls);
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var again = new CallbackHttp(retry, token, true, firstAnswer: 1);
        using var resumed = new TelegramPollingReceiver(new(Route.ConnectionId, "opaque-reference"), new Secrets(), cursors, again);
        var replies = new List<OutgoingMessage>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumed.RunAsync(async (peer, ct) => replies.Add(await gateway.Handler.HandleAsync(peer, ct)), retry.Token));
        Assert.All(replies, reply => Assert.Contains("no longer available", reply.Text));
        Assert.Equal(3, (await cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence); Assert.Equal(1, fixture.Calls);
    }

    private sealed class FailedDecisionAdvance(IChannelCursorRepository inner) : IChannelCursorRepository
    {
        public Task<ChannelCursor> ReadAsync(string channelId, string connectionId, CancellationToken ct) => inner.ReadAsync(channelId, connectionId, ct);
        public Task<ChannelCursor> AdvanceAsync(string channelId, string connectionId, ChannelCursor expected, long nextSequence, CancellationToken ct) =>
            nextSequence == 2 ? throw new System.IO.IOException("Synthetic lost cursor acknowledgement") : inner.AdvanceAsync(channelId, connectionId, expected, nextSequence, ct);
    }

    private static IncomingMessage Peer() => new() { Route = Route, SenderId = "42", ExternalMessageId = "1", TimestampUtc = DateTimeOffset.UtcNow, Text = "review" };
    private static IncomingMessage Callback(string token) => Peer() with { Text = "", ActionToken = token };
    private sealed record Gateway(SqliteChannelBindingRepository Bindings, SqliteChannelActionRepository Actions, ChannelApprovalPresenter Presenter, TelegramCommandHandler Handler)
    {
        public static async Task<Gateway> Start(TemporaryAgentDatabase database, TaskRunCoordinator runs, IApprovalRepository approvals, TimeProvider? clock = null, ApprovalOwner? owner = null)
        {
            var bindings = new SqliteChannelBindingRepository(database, clock);
            if (await bindings.FindAsync(Route, "42") is null) { var pending = await bindings.RequestPairingAsync(Route, "42", Owner); await bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true); }
            var policy = new ChannelOwnershipPolicy(owner ?? Owner, bindings); var actions = new SqliteChannelActionRepository(database);
            var commands = new ChannelOwnerCommands(policy, runs, approvals);
            return new(bindings, actions, new(policy, approvals, actions, clock, runs), new(Route.ConnectionId, policy, commands, new(runs, [], policy), runs,
                actions: new ChannelActionHandler(policy, actions, approvals, commands, runs, clock)));
        }
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => ValueTask.FromResult<string?>("12345:synthetic-token");
    }
    private sealed class CallbackHttp(CancellationTokenSource stop, string token, bool failedAnswer, int firstAnswer = 0) : HttpMessageHandler
    {
        private int calls; public int Answers;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/answerCallbackQuery"))
            {
                using var answer = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("callback-" + (firstAnswer + Answers++), answer.RootElement.GetProperty("callback_query_id").GetString()); Assert.Single(answer.RootElement.EnumerateObject());
                if (failedAnswer) throw new HttpRequestException("Synthetic secret-bearing transport error");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
            }
            if (++calls > 1) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            await request.Content!.ReadAsStringAsync(ct);
            var updates = Enumerable.Range(0, 3).Select(index => new { update_id = index, callback_query = new { id = "callback-" + index,
                from = new { id = index == 0 ? 999 : 42, is_bot = false }, data = token,
                message = new { message_id = 10, date = 1800000000, from = new { id = 12345, is_bot = true }, chat = new { id = 24 }, text = "/cancel fake" } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result = updates }), Encoding.UTF8, "application/json") };
        }
    }
}
