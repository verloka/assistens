using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;

namespace PersonalAgent.IntegrationTests;

public sealed class TakeControlPersistenceTests
{
    [Fact]
    public async Task HotkeyServiceClosesResourceBeforeWaitingForDurableRunPause()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new BlockingRepository(new SqliteRunRepository(database));
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Local takeover", new TaskOrigin("desktop")));
        var lease = await desktop.AcquireAsync(run.Id);
        using var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        service.TakeControl(); // No native key registration; tests the actual production callback.
        try
        {
            await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(desktop.IsCurrent(lease));
            Assert.Null(runs.GetRun(run.Id).PauseScope); // Store has not committed/published this revision yet.
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
        }
        finally { repository.Release.TrySetResult(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var item in runs.WatchRunAsync(run.Id, timeout.Token))
            if (runs.GetRun(run.Id).PauseScope == PauseScope.EntireRun) break;
        Assert.Equal(PauseScope.EntireRun, (await repository.GetAsync(run.Id))!.Run.PauseScope);
        Assert.False(desktop.IsCurrent(lease));
        Assert.False(service.IsRegistered);
    }

    private sealed class BlockingRepository(IRunRepository inner) : IRunRepository
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken = default) => inner.CreateAsync(run, conversation, request, events, cancellationToken);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken cancellationToken = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, cancellationToken);
        public Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public async Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents, AgentRunResult? result = null, CancellationToken cancellationToken = default)
        {
            if (run.PauseScope is not null) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await inner.UpdateAsync(run, expectedVersion, newEvents, result, cancellationToken);
        }
    }
}
