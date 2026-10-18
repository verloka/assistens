using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceRepositoryTests
{
    [Fact]
    public async Task PersistenceRepositoryCommitsTransitionAndHistoryAndRejectsRepeatedVersion()
    {
        using var database = new TemporaryAgentDatabase();
        var (repository, run) = await Seed(database);
        var running = TaskRunStateMachine.Transition(run, RunStatus.Running, run.RunVersion, DateTimeOffset.UtcNow);
        await repository.UpdateAsync(running, run.RunVersion, [Event(run.Id, 3, RunEventType.RunStarted)]);
        var restored = (await repository.GetAsync(run.Id))!;
        Assert.Equal(RunStatus.Running, restored.Run.Status);
        Assert.Equal(2, restored.Run.RunVersion);
        Assert.Equal([1L, 2L, 3L], restored.Events.Select(value => value.Sequence));
        await Assert.ThrowsAsync<RunVersionConflictException>(() => repository.UpdateAsync(running, 1, [Event(run.Id, 4, RunEventType.Progress)]));
        Assert.Equal(3, (await repository.GetAsync(run.Id))!.Events.Count);
    }

    [Fact]
    public async Task PersistenceRepositoryCompetingUpdatesProduceOneWinnerWithoutOrphanEvents()
    {
        using var database = new TemporaryAgentDatabase();
        var (repository, run) = await Seed(database);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var next = TaskRunStateMachine.Transition(run, index % 2 == 0 ? RunStatus.Cancelled : RunStatus.Running, 1, DateTimeOffset.UtcNow);
            try { await new SqliteRunRepository(database).UpdateAsync(next, 1, [Event(run.Id, 3, RunEventType.StatusChanged)]); return true; }
            catch (RunVersionConflictException) { return false; }
        })).ToArray();
        start.SetResult();
        Assert.Single(await Task.WhenAll(attempts), value => value);
        var stored = (await repository.GetAsync(run.Id))!;
        Assert.Equal(2, stored.Run.RunVersion);
        Assert.Equal(3, stored.Events.Count);
        Assert.Contains(stored.Run.Status, new[] { RunStatus.Cancelled, RunStatus.Running });
    }

    [Fact]
    public async Task PersistenceRepositoryEventInsertFailureRollsBackStateWrite()
    {
        using var database = new TemporaryAgentDatabase();
        var (repository, run) = await Seed(database);
        var previous = (await repository.GetAsync(run.Id))!;
        var next = TaskRunStateMachine.Transition(run, RunStatus.Running, 1, DateTimeOffset.UtcNow);
        // Valid sequence with a duplicate event primary key forces a real DB failure in SaveChanges.
        var collision = Event(run.Id, 3, RunEventType.RunStarted) with { Id = previous.Events[0].Id };
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.UpdateAsync(next, 1, [collision]));
        var restored = (await repository.GetAsync(run.Id))!;
        Assert.Equal(RunStatus.Queued, restored.Run.Status);
        Assert.Equal(1, restored.Run.RunVersion);
        Assert.Equal(2, restored.Events.Count);
    }

    [Fact]
    public async Task PersistenceRepositoryRefusesInputMutationTerminalRewriteAndUnknownPayloadVersion()
    {
        using var database = new TemporaryAgentDatabase();
        var (repository, run) = await Seed(database);
        var cancelled = TaskRunStateMachine.Transition(run, RunStatus.Cancelled, 1, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(cancelled with { Instructions = "Changed" }, 1, [Event(run.Id, 3, RunEventType.RunFinished)]));
        await repository.UpdateAsync(cancelled, 1, [Event(run.Id, 3, RunEventType.RunFinished)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateAsync(cancelled with { RunVersion = 3 }, 2, [Event(run.Id, 4, RunEventType.Progress)]));
        await using var db = database.Open();
        var row = await db.TaskRuns.SingleAsync();
        row.PayloadSchemaVersion = 2;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync(run.Id));
    }

    private static async Task<(IRunRepository Repository, TaskRun Run)> Seed(TemporaryAgentDatabase database)
    {
        await using var db = database.Open();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation { Id = Guid.NewGuid(), Title = "Test", CreatedAtUtc = now, UpdatedAtUtc = now };
        var run = new TaskRun
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Instructions = "Local task", ModelProfileId = "fake",
            Origin = new TaskOrigin("desktop"), CreatedAtUtc = now, Status = RunStatus.Queued, RunVersion = 1
        };
        var request = new AgentRequest { RunId = run.Id, ConversationId = run.ConversationId, Instructions = run.Instructions, ModelProfileId = run.ModelProfileId };
        IRunRepository repository = new SqliteRunRepository(database);
        await repository.CreateAsync(run, conversation, request, [Event(run.Id, 1, RunEventType.RunCreated), Event(run.Id, 2, RunEventType.StatusChanged)]);
        return (repository, run);
    }
    private static RunEvent Event(Guid id, long sequence, RunEventType type) => new()
    {
        Id = Guid.NewGuid(), RunId = id, Sequence = sequence, EventType = type, TimestampUtc = DateTimeOffset.UtcNow, Message = type.ToString()
    };
}
