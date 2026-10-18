using Microsoft.EntityFrameworkCore;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceIdempotencyTests
{
    [Theory]
    [InlineData("telegram/connection-1", "update-42")]
    [InlineData("schedule/task-1", "2026-10-18T12:00:00Z")]
    public async Task PersistenceExternalKeySurvivesCoordinatorRestartAndRejectsChangedIntent(string source, string key)
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        var command = Submit() with { ExternalKey = new RunTriggerKey(source, key) };
        Guid id;
        using (var first = new TaskRunCoordinator(new SqliteRunRepository(database))) id = (await first.SubmitAsync(command)).Id;
        using (var second = new TaskRunCoordinator(new SqliteRunRepository(database)))
            Assert.Equal(id, (await second.SubmitAsync(command with { CommandId = Guid.NewGuid() })).Id);
        using (var third = new TaskRunCoordinator(new SqliteRunRepository(database)))
            await Assert.ThrowsAsync<RunSubmissionConflictException>(() => third.SubmitAsync(command with { CommandId = Guid.NewGuid(), Instructions = "Another operation" }));
        await using var db = database.Open();
        Assert.Equal(1, await db.TaskRuns.CountAsync());
        Assert.Equal(1, await db.Conversations.CountAsync());
        Assert.Equal(2, await db.RunEvents.CountAsync());
        Assert.Equal(id, (await db.TaskRuns.SingleAsync()).Id);
    }

    [Fact]
    public async Task PersistenceCommandIdWithoutExternalKeyIsStableAcrossRestart()
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        var command = Submit();
        Guid id;
        using (var first = new TaskRunCoordinator(new SqliteRunRepository(database))) id = (await first.SubmitAsync(command)).Id;
        using var reopened = new TaskRunCoordinator(new SqliteRunRepository(database));
        Assert.Equal(id, (await reopened.SubmitAsync(command)).Id);
        await using var db = database.Open();
        Assert.Equal(1, await db.TaskRuns.CountAsync());
    }

    [Fact]
    public async Task PersistenceCompetingExternalKeyCreatesOneRunConversationAndInitialHistory()
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        var prototype = Submit() with { ExternalKey = new RunTriggerKey("schedule/task-1", "occurrence-1") };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database));
            return (await coordinator.SubmitAsync(prototype with { CommandId = Guid.NewGuid() })).Id;
        })).ToArray();
        start.SetResult();
        Assert.Single((await Task.WhenAll(attempts)).Distinct());
        await using var db = database.Open();
        Assert.Equal(1, await db.TaskRuns.CountAsync());
        Assert.Equal(1, await db.Conversations.CountAsync());
        Assert.Equal(2, await db.RunEvents.CountAsync());
    }

    [Fact]
    public async Task PersistenceExternalRedeliveryExecutesOnceAndCannotReplayCompletedAttemptAfterRestart()
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        var command = Submit() with { ExternalKey = new RunTriggerKey("telegram/connection-1", "update-9") };
        Guid id;
        IReadOnlyList<RunEvent> history;
        using (var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database)))
        using (var stop = new CancellationTokenSource())
        {
            var runtime = new CountingRuntime(new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync));
            var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
            try
            {
                var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => coordinator.SubmitAsync(command with { CommandId = Guid.NewGuid() })));
                id = Assert.Single(results.Select(run => run.Id).Distinct());
                await coordinator.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, runtime.Calls);
                history = coordinator.GetEvents(id);
            }
            finally { stop.Cancel(); await loop; }
        }
        using var reopened = new TaskRunCoordinator(new SqliteRunRepository(database));
        await reopened.LoadFinishedHistoryAsync();
        using var stopping = new CancellationTokenSource();
        var probe = new CountingRuntime(new FakeAgentRuntime(waitAtBoundary: reopened.WaitAtBoundaryAsync));
        var worker = new TaskRunner(reopened, probe).RunAsync(stopping.Token);
        try
        {
            var duplicate = await reopened.SubmitAsync(command with { CommandId = Guid.NewGuid() });
            Assert.Equal(id, duplicate.Id);
            Assert.Equal(RunStatus.Completed, duplicate.Status);
            var sentinel = await reopened.SubmitAsync(Submit());
            await reopened.WaitForTerminalAsync(sentinel.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, probe.Calls); // Only sentinel, never the redelivered completed run.
            Assert.Equal(history, reopened.GetEvents(id));
        }
        finally { stopping.Cancel(); await worker; }
    }

    [Fact]
    public async Task PersistenceExternalKeyNamespacesKeepUnrelatedConnectionsSeparate()
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database));
        var first = await coordinator.SubmitAsync(Submit() with { ExternalKey = new RunTriggerKey("telegram/bot-a", "42") });
        var second = await coordinator.SubmitAsync(Submit() with { ExternalKey = new RunTriggerKey("telegram/bot-b", "42") });
        Assert.NotEqual(first.Id, second.Id);
        await using var db = database.Open();
        Assert.Equal(2, await db.TaskRuns.CountAsync());
    }

    [Fact]
    public async Task PersistenceLostCreationAcknowledgementRedeliveryStartsOriginalLocalRunOnce()
    {
        using var database = new TemporaryAgentDatabase();
        await Migrate(database);
        using var coordinator = new TaskRunCoordinator(new LostAcknowledgementRepository(new SqliteRunRepository(database)));
        using var stop = new CancellationTokenSource();
        var runtime = new CountingRuntime(new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync));
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            var command = Submit() with { ExternalKey = new RunTriggerKey("telegram/connection-1", "update-unknown-ack") };
            await Assert.ThrowsAsync<RunPersistenceException>(() => coordinator.SubmitAsync(command));
            Assert.Empty(coordinator.GetRuns());
            var original = Assert.Single(await new SqliteRunRepository(database).ListAsync());
            Assert.Equal(RunStatus.Queued, original.Status);
            var accepted = await coordinator.SubmitAsync(command);
            Assert.Equal(original.Id, accepted.Id);
            Assert.Equal(RunStatus.Completed, (await coordinator.WaitForTerminalAsync(accepted.Id).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.Equal(1, runtime.Calls);
            await using var db = database.Open();
            Assert.Equal(1, await db.TaskRuns.CountAsync());
            Assert.Equal(1, await db.Conversations.CountAsync());
        }
        finally { stop.Cancel(); await loop; }
    }

    private sealed class LostAcknowledgementRepository(IRunRepository inner) : IRunRepository
    {
        private int attempts;
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken ct = default) => inner.CreateAsync(run, conversation, request, events, ct);
        public async Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken ct = default)
        {
            var stored = await inner.GetOrCreateAsync(run, conversation, request, events, identity, ct);
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Creation committed but acknowledgement was lost.");
            return stored;
        }
        public Task UpdateAsync(TaskRun run, long version, IReadOnlyList<RunEvent> events, AgentRunResult? result = null, CancellationToken ct = default) => inner.UpdateAsync(run, version, events, result, ct);
        public Task<StoredRun?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
    }

    private sealed class CountingRuntime(IAgentRuntime inner) : IAgentRuntime
    {
        public int Calls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return inner.ExecuteAsync(request, progress, ct); }
    }
    private static SubmitTaskCommand Submit() => new(Guid.NewGuid(), "Local idempotency task", new TaskOrigin("test"));
    private static async Task Migrate(TemporaryAgentDatabase database) { await using var db = database.Open(); await db.Database.MigrateAsync(); }
}
