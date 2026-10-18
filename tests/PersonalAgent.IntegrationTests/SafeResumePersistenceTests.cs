using Microsoft.EntityFrameworkCore;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;

namespace PersonalAgent.IntegrationTests;

public sealed class SafeResumePersistenceTests
{
    [Fact]
    public async Task AlreadyRunningResumeCannotClearNewHumanControl()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAgentRuntime(waitAtBoundary: async (_, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        var loop = new TaskRunner(runs, runtime).RunAsync(stop.Token);
        try
        {
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "No implicit permission", new("test")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var lease = await desktop.AcquireAsync(run.Id);
            desktop.TakeControl();
            var running = runs.GetRun(run.Id);
            await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, running.RunVersion));
            Assert.False(desktop.IsCurrent(lease));
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(run.Id));
        }
        finally { stop.Cancel(); await loop; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DesktopCannotReopenBeforeSuccessfulResumeCheckpoint(bool fail)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new ResumeRepository(new SqliteRunRepository(database)) { Fail = fail };
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Resume fixture", new("test")));
        var old = await desktop.AcquireAsync(run.Id);
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        var resume = runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        try
        {
            await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(desktop.IsCurrent(old));
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(run.Id));
            Assert.Equal(PauseScope.GuiOnly, runs.GetRun(run.Id).PauseScope);
            Assert.Equal(PauseScope.GuiOnly, (await repository.GetAsync(run.Id))!.Run.PauseScope);
        }
        finally { repository.Release.TrySetResult(); }
        if (fail)
        {
            await Assert.ThrowsAsync<RunPersistenceException>(() => resume);
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(run.Id));
            Assert.Equal(PauseScope.GuiOnly, runs.GetRun(run.Id).PauseScope);
        }
        else
        {
            await resume;
            var fresh = await desktop.AcquireAsync(run.Id);
            Assert.True(fresh.Generation > old.Generation); Assert.False(desktop.IsCurrent(old));
            Assert.Null((await repository.GetAsync(run.Id))!.Run.PauseScope);
        }
    }

    [Fact]
    public async Task NewTakeoverDuringResumeWriteWinsAndDoesNotWakeOldGuiStep()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new ResumeRepository(new SqliteRunRepository(database));
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Takeover race", new("test")));
        await desktop.AcquireAsync(run.Id);
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        using var waitStop = new CancellationTokenSource();
        var gui = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(true, false, false), waitStop.Token);
        var resume = runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        try
        {
            await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            desktop.TakeControl();
        }
        finally { repository.Release.TrySetResult(); }
        var stillPaused = await resume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseScope.EntireRun, stillPaused.PauseScope); Assert.Equal(RunStatus.Paused, stillPaused.Status);
        Assert.False(gui.IsCompleted);
        await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(run.Id));
        Assert.Equal(RunStatus.Paused, (await repository.GetAsync(run.Id))!.Run.Status);
        waitStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gui);
    }

    private sealed class ResumeRepository(IRunRepository inner) : IRunRepository
    {
        public bool Fail;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken = default) => inner.CreateAsync(run, conversation, request, events, cancellationToken);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken cancellationToken = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, cancellationToken);
        public Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public async Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents, AgentRunResult? result = null, CancellationToken cancellationToken = default)
        {
            if (newEvents.Any(e => e.EventType == RunEventType.RunResumed))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
                if (Fail) throw new System.IO.IOException("synthetic resume failure");
            }
            await inner.UpdateAsync(run, expectedVersion, newEvents, result, cancellationToken);
        }
    }
}
