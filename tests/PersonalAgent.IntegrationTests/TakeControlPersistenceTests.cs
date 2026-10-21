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
    public async Task ActivityCompletionCannotReturnPreviousTaskAfterGateClosesBeforeHandoffPublication()
    {
        using var desktop = new HeldTakeover(); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Handoff publication", new("test")));
        var lease = await desktop.AcquireAsync(run.Id);
        using var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var callback = Task.Run(() => service.AutoPauseForUserActivity());
        await desktop.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(desktop.IsCurrent(lease));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () => { reading.TrySetResult(); await service.Completion; return service.Handoff; });
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(completion, await Task.WhenAny(completion, Task.Delay(100)));
        }
        finally { desktop.Release.Set(); }
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await completion.WaitAsync(TimeSpan.FromSeconds(5))).Confirmed);
        Assert.Equal(nameof(PauseReason.AutoPause), runs.GetRun(run.Id).Reason);
    }

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
        await service.Completion.WaitAsync(timeout.Token); // Final safe-pause checkpoint also uses the database.
        Assert.Equal(PauseScope.EntireRun, (await repository.GetAsync(run.Id))!.Run.PauseScope);
        Assert.False(desktop.IsCurrent(lease));
        Assert.False(service.IsRegistered);
    }

    private sealed class HeldTakeover : IDesktopAccessCoordinator, IDisposable
    {
        private readonly DesktopAccessCoordinator inner = new(_ => true);
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new();
        public DesktopAccessSnapshot Snapshot => inner.Snapshot;
        public Task<DesktopLease> AcquireAsync(Guid id, CancellationToken ct = default) => inner.AcquireAsync(id, ct);
        public Task<DesktopLease> WaitAcquireAsync(Guid id, CancellationToken ct = default) => inner.WaitAcquireAsync(id, ct);
        public bool IsCurrent(DesktopLease lease) => inner.IsCurrent(lease);
        public IDisposable EnterAtomic(DesktopLease lease) => inner.EnterAtomic(lease);
        public Task RevokeAsync(DesktopLease lease) => inner.RevokeAsync(lease);
        public Task PauseRunAsync(Guid id) => inner.PauseRunAsync(id);
        public void ResumeRun(Guid id) => inner.ResumeRun(id);
        public long ValidateResumeRun(Guid id) => inner.ValidateResumeRun(id);
        public bool TryResumeRun(Guid id, long version) => inner.TryResumeRun(id, version);
        public Task CancelRunAsync(Guid id) => inner.CancelRunAsync(id);
        public Guid? TakeControl() => inner.TakeControl();
        public Guid? TakeControlIfOwned()
        {
            var id = inner.TakeControlIfOwned(); Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return id;
        }
        public void ReportInputCleanupFailure(DesktopLease lease) => inner.ReportInputCleanupFailure(lease);
        public void RestoreInputCleanupFailure(Guid id) => inner.RestoreInputCleanupFailure(id);
        public Task<DesktopAccessSnapshot> ReleasePausedRunAsync(Guid id, CancellationToken ct = default) => inner.ReleasePausedRunAsync(id, ct);
        public Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default) => inner.ReleaseAsync(lease, ct);
        public void Dispose() => Release.Dispose();
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
