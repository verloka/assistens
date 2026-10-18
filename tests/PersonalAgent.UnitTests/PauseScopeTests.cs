using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.UnitTests;

public sealed class PauseScopeTests
{
    [Fact]
    public async Task GuiOnlyAllowsIndependentPermittedReadAndBlocksDesktopWritesAndDependentReads()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(Submit());
        var lease = await desktop.AcquireAsync(run.Id);
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        Assert.Equal(RunStatus.Queued, paused.Status);
        Assert.False(desktop.IsCurrent(lease));
        Assert.Equal(DesktopAccessState.PausedForUser, desktop.Snapshot.State);
        await runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(false, true, true)); // Approved independent MCP-read metadata.
        var gui = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(true, true, false));
        var write = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(false, false, true));
        var dependent = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(false, true, false));
        Assert.False(gui.IsCompleted); Assert.False(write.IsCompleted); Assert.False(dependent.IsCompleted);
        await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(run.Id));
        await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        await Task.WhenAll(gui, write, dependent).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(desktop.IsCurrent(lease));
        var fresh = await desktop.AcquireAsync(run.Id);
        Assert.True(fresh.Generation > lease.Generation);
    }

    [Fact]
    public async Task EntireRunBlocksEveryNewStepUntilResume()
    {
        using var runs = new TaskRunCoordinator();
        var run = await runs.SubmitAsync(Submit());
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.EntireRun));
        var computation = runs.WaitAtBoundaryAsync(run.Id, CancellationToken.None);
        var read = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(false, true, true));
        var gui = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(true, false, false));
        Assert.False(computation.IsCompleted); Assert.False(read.IsCompleted); Assert.False(gui.IsCompleted);
        await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        await Task.WhenAll(computation, read, gui).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ResourceStaysPausingWhileAtomicInputExistsAndResumeCannotReopenOldLease()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(Submit());
        var lease = await desktop.AcquireAsync(run.Id);
        var atomic = desktop.EnterAtomic(lease);
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        Assert.Equal(DesktopAccessState.Pausing, desktop.Snapshot.State);
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion)));
        Assert.Equal("DesktopPausing", error.Code);
        atomic.Dispose();
        Assert.Equal(DesktopAccessState.PausedForUser, desktop.Snapshot.State);
        await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        Assert.Equal(DesktopAccessState.Free, desktop.Snapshot.State);
        Assert.False(desktop.IsCurrent(lease));
    }

    [Fact]
    public async Task CancelWakesGuiPausedBoundaryAndCannotResumeOrKeepLease()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(Submit());
        var lease = await desktop.AcquireAsync(run.Id);
        var paused = await runs.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        var gui = runs.WaitAtBoundaryAsync(run.Id, new RunStepAccess(true, true, false));
        var canceled = await runs.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gui);
        Assert.Equal(RunStatus.Cancelled, canceled.Status);
        Assert.False(desktop.IsCurrent(lease));
        Assert.Equal(DesktopAccessState.Free, desktop.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.ResumeAsync(new(Guid.NewGuid(), run.Id, canceled.RunVersion)));
    }

    [Fact]
    public async Task SkipIsDistinctTerminalOutcomeAndDoesNotPauseOtherRun()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var active = await runs.SubmitAsync(Submit());
        var lease = await desktop.AcquireAsync(active.Id);
        var occurrence = await runs.SubmitAsync(Submit() with { Origin = new("schedule") });
        var skip = new SkipRunCommand(Guid.NewGuid(), occurrence.Id, occurrence.RunVersion, "Skipped by user before start");
        var skipped = await runs.SkipAsync(skip);
        Assert.Equal(RunStatus.Skipped, skipped.Status);
        Assert.Null(skipped.StartedAtUtc);
        Assert.Same(skipped, await runs.SkipAsync(skip));
        Assert.True(desktop.IsCurrent(lease));
        Assert.Equal(active.RunVersion, runs.GetRun(active.Id).RunVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.ResumeAsync(new(Guid.NewGuid(), skipped.Id, skipped.RunVersion)));
    }

    private static SubmitTaskCommand Submit() => new(Guid.NewGuid(), "Local scope test", new("desktop"));
}
