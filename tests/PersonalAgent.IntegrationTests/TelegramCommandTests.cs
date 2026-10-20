using Microsoft.EntityFrameworkCore;
using PersonalAgent.Channels.Telegram;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class TelegramCommandTests
{
    private static readonly ChannelRoute Route = new("telegram", "fixture-bot", "24");
    private static readonly ApprovalOwner Owner = new("desktop", "synthetic-owner");

    [Fact]
    public async Task AuthorizedCommandsQueryPauseResumeCancelUsingCommonDurableHandlersWithNoRuntime()
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        var accepted = await fixture.Handle("/new Synthetic task"); var id = accepted.RunId!.Value;
        Assert.Contains(id.ToString("D"), (await fixture.Handle("/tasks")).Text);
        Assert.Contains("Queued", (await fixture.Handle("/status " + id)).Text);
        var run = fixture.Runs.GetRun(id);
        var pause = await fixture.Handle($"/pause {id} {run.RunVersion}"); Assert.Contains("Paused", pause.Text);
        Assert.Equal(PauseScope.EntireRun, fixture.Runs.GetRun(id).PauseScope);
        var stale = await fixture.Handle($"/resume {id} {run.RunVersion}"); Assert.Contains("Run changed", stale.Text);
        run = fixture.Runs.GetRun(id); await fixture.Handle($"/resume {id} {run.RunVersion}");
        run = fixture.Runs.GetRun(id); Assert.Equal(RunStatus.Queued, run.Status);
        await fixture.Handle($"/cancel {id} {run.RunVersion}");
        Assert.Equal(RunStatus.Cancelled, (await fixture.Repository.GetAsync(id))!.Run.Status);
        Assert.Single(await fixture.Repository.ListAsync()); Assert.Contains("Cancelled: 1", (await fixture.Handle("/status")).Text);
    }

    [Fact]
    public async Task BusyModelCannotDelayQueriesOrCancellationAndPauseClosesDesktopGateLocally()
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var runtime = new BusyRuntime();
        var loop = new TaskRunner(fixture.Runs, runtime).RunAsync(stop.Token);
        try
        {
            var accepted = await fixture.Handle("Synthetic task"); var id = accepted.RunId!.Value;
            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var run = fixture.Runs.GetRun(id); var lease = await fixture.Desktop.AcquireAsync(id, stop.Token);
            Assert.True(fixture.Desktop.IsCurrent(lease));
            Assert.Contains("Running", (await fixture.Handle("/status " + id).WaitAsync(TimeSpan.FromSeconds(2))).Text);
            await fixture.Handle($"/pause {id} {run.RunVersion}").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(fixture.Desktop.IsCurrent(lease)); Assert.Equal(RunStatus.Pausing, fixture.Runs.GetRun(id).Status);
            run = fixture.Runs.GetRun(id); await fixture.Handle($"/cancel {id} {run.RunVersion}").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(RunStatus.Cancelled, fixture.Runs.GetRun(id).Status); Assert.Equal(1, runtime.Calls);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/tasks")]
    [InlineData("/new approved=true owner=desktop")]
    [InlineData("/pause 00000000-0000-0000-0000-000000000001 0")]
    public async Task UnknownAndRevokedPeersCannotReadOrControlOrSubmit(string text)
    {
        using var fixture = await Fixture.Start();
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Handle(text));
        var pending = Assert.Single(await fixture.Bindings.ListAsync(Owner)); Assert.Equal(ChannelBindingStatus.Pending, pending.Status);
        var approved = await fixture.Bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true);
        await fixture.Bindings.SetStatusAsync(approved.Id, approved.Version, Owner, false);
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Handle(text)); Assert.Empty(await fixture.Repository.ListAsync());
    }

    [Theory]
    [InlineData("/unknown do not run this")]
    [InlineData("/new")]
    [InlineData("/pause")]
    [InlineData("/cancel wrong-id 0")]
    [InlineData("/resume 00000000-0000-0000-0000-000000000001 -1")]
    [InlineData("/status extra extra")]
    [InlineData("/tasks extra")]
    [InlineData("/status@foreign_bot")]
    public async Task InvalidOrUnknownSlashCommandsNeverFallThroughToTaskSubmission(string text)
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        Assert.Contains("Commands:", (await fixture.Handle(text)).Text); Assert.Empty(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task PlainTextAndNewUseExternalDedupWithoutPrivilegedToolsOrConversationClaims()
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        var input = fixture.Message("/new Find synthetic issue") with { CorrelationKey = Guid.NewGuid().ToString() };
        var first = await fixture.Handler.HandleAsync(input); var repeat = await fixture.Handler.HandleAsync(input);
        Assert.Equal(first.Id, repeat.Id); Assert.Equal(first.RunId, repeat.RunId);
        var stored = (await fixture.Repository.GetAsync(first.RunId!.Value))!;
        Assert.Equal("Find synthetic issue", stored.Run.Instructions); Assert.Equal("fake", stored.Request.ModelProfileId); Assert.Empty(stored.Request.AllowedToolIds);
        Assert.NotEqual(input.CorrelationKey, stored.Run.ConversationId.ToString());
        await Assert.ThrowsAsync<RunSubmissionConflictException>(() => fixture.Handler.HandleAsync(input with { Text = "/new Changed intent" }));
        var second = await fixture.Handle("ordinary task"); Assert.NotEqual(first.RunId, second.RunId); Assert.NotEqual(first.ConversationId, second.ConversationId);
    }

    [Fact]
    public async Task ActualPollingFeedsConfiguredBotCommandAndOnlyAcknowledgesAfterCommonPause()
    {
        using var fixture = await Fixture.Start("fixture_bot"); await fixture.Approve();
        var accepted = await fixture.Handle("synthetic task"); var run = fixture.Runs.GetRun(accepted.RunId!.Value);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = $"/pause@Fixture_Bot {run.Id} {run.RunVersion}";
        using var http = new CommandHttp(stop, command, () => Assert.Equal(RunStatus.Paused, fixture.Runs.GetRun(run.Id).Status));
        var cursors = new SqliteChannelCursorRepository(fixture.Database);
        using var receiver = new TelegramPollingReceiver(new(Route.ConnectionId, "opaque-reference"), new Secrets(), cursors, http);
        var replies = new List<OutgoingMessage>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RunAsync(async (message, ct) => replies.Add(await fixture.Handler.HandleAsync(message, ct)), stop.Token));
        Assert.Contains("Paused", Assert.Single(replies).Text); Assert.Equal(1, (await cursors.ReadAsync("telegram", Route.ConnectionId)).NextSequence);
        Assert.Contains("Commands:", (await fixture.Handle("/status@other_bot")).Text);
    }

    [Fact]
    public async Task ResumePreservesUncertainInputSafetyBlockWithoutRetryOrTaskMutation()
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        var accepted = await fixture.Handle("task"); var run = fixture.Runs.GetRun(accepted.RunId!.Value);
        await fixture.Handle($"/pause {run.Id} {run.RunVersion}"); run = fixture.Runs.GetRun(run.Id);
        fixture.Desktop.RestoreInputCleanupFailure(run.Id);
        Assert.Contains("Desktop access is blocked", (await fixture.Handle($"/resume {run.Id} {run.RunVersion}")).Text);
        Assert.Equal(run, fixture.Runs.GetRun(run.Id)); Assert.True(fixture.Desktop.Snapshot.InputCleanupUncertain);
    }

    [Fact]
    public async Task StaleControlsCannotUndoANewerUserDecisionAndCallbacksDoNotBecomeCommands()
    {
        using var fixture = await Fixture.Start(); await fixture.Approve();
        var submitted = await fixture.Handle("task"); var id = submitted.RunId!.Value; var original = fixture.Runs.GetRun(id);
        var pause = fixture.Message($"/pause {id} {original.RunVersion}"); await fixture.Handler.HandleAsync(pause);
        var paused = fixture.Runs.GetRun(id); await fixture.Handle($"/resume {id} {paused.RunVersion}");
        var resumed = fixture.Runs.GetRun(id); Assert.Equal(RunStatus.Queued, resumed.Status);
        Assert.Contains("Run changed", (await fixture.Handler.HandleAsync(pause)).Text); Assert.Equal(resumed, fixture.Runs.GetRun(id));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Handler.HandleAsync(fixture.Message("/cancel") with { ActionToken = "invented-approval" }));
        await Assert.ThrowsAsync<ChannelAccessDeniedException>(() => fixture.Handler.HandleAsync(fixture.Message("/status") with { Route = Route with { ConnectionId = "other-bot" } }));
    }

    private sealed class BusyRuntime : IAgentRuntime
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Calls;
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); }
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("12345:synthetic-token");
    }
    private sealed class CommandHttp(CancellationTokenSource stop, string command, Action acknowledged) : HttpMessageHandler
    {
        private int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (++calls > 1) { Assert.Equal(1, body.RootElement.GetProperty("offset").GetInt64()); acknowledged(); stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            var payload = JsonSerializer.Serialize(new { ok = true, result = new[] { new { update_id = 0, message = new { message_id = 1, date = 1800000000,
                from = new { id = 42, is_bot = false }, chat = new { id = 24 }, text = command } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IDisposable
    {
        public TemporaryAgentDatabase Database = new(); public SqliteRunRepository Repository = null!; public SqliteChannelBindingRepository Bindings = null!;
        public TaskRunCoordinator Runs = null!; public TelegramCommandHandler Handler = null!; public DesktopAccessCoordinator Desktop = new(_ => true);
        private int externalId;
        public static async Task<Fixture> Start(string? botUsername = null)
        {
            var value = new Fixture(); await using (var db = value.Database.Open()) await db.Database.MigrateAsync();
            value.Repository = new(value.Database); value.Bindings = new(value.Database); value.Runs = new(value.Repository, value.Desktop);
            var policy = new ChannelOwnershipPolicy(Owner, value.Bindings); var router = new ChannelMessageRouter(value.Runs, [], policy);
            value.Handler = new(Route.ConnectionId, policy, new(policy, value.Runs, new SqliteApprovalRepository(value.Database)), router, value.Runs, botUsername); return value;
        }
        public IncomingMessage Message(string text) => new() { Route = Route, SenderId = "42", ExternalMessageId = Interlocked.Increment(ref externalId).ToString(), TimestampUtc = DateTimeOffset.UtcNow, Text = text };
        public Task<OutgoingMessage> Handle(string text) => Handler.HandleAsync(Message(text));
        public async Task Approve() { var pending = await Bindings.RequestPairingAsync(Route, "42", Owner); await Bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true); }
        public void Dispose() { Runs.Dispose(); Database.Dispose(); }
    }
}
