using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.UnitTests;

public sealed class TaskCommandsTests
{
    [Fact]
    public async Task CommandsDuplicateSubmitReturnsOneRunAndRejectsChangedPayload()
    {
        using var coordinator = new TaskRunCoordinator();
        var command = Submit();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => coordinator.SubmitAsync(command))));
        Assert.Single(submissions.Select(run => run.Id).Distinct());
        Assert.Equal(2, coordinator.GetEvents(submissions[0].Id).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SubmitAsync(command with { Instructions = "Different" }));
    }

    [Fact]
    public async Task CommandsRepeatedCancelStaysTerminalAndCannotBecomeResume()
    {
        using var coordinator = new TaskRunCoordinator();
        var run = await coordinator.SubmitAsync(Submit());
        var cancel = new CancelRunCommand(Guid.NewGuid(), run.Id, run.RunVersion);
        var cancelled = await coordinator.CancelAsync(cancel);
        Assert.Equal(cancelled, await coordinator.CancelAsync(cancel));
        Assert.Equal(RunStatus.Cancelled, coordinator.GetRun(run.Id).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, cancelled.RunVersion)));
        Assert.Equal(RunStatus.Cancelled, coordinator.GetRun(run.Id).Status);
    }

    [Fact]
    public async Task CommandsQueuedPauseWaitsForExplicitResumeAndCancellationWins()
    {
        using var coordinator = new TaskRunCoordinator();
        var run = await coordinator.SubmitAsync(Submit());
        var command = new PauseRunCommand(Guid.NewGuid(), run.Id, run.RunVersion);
        var paused = await coordinator.PauseAsync(command);
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal(paused, await coordinator.PauseAsync(command));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = coordinator.WaitAtBoundaryAsync(run.Id, timeout.Token);
        Assert.False(waiting.IsCompleted);
        var cancel = await coordinator.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<RunVersionConflictException>(() => coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion)));
        Assert.Equal(RunStatus.Cancelled, cancel.Status);
    }

    [Fact]
    public async Task CommandsResumeKeepsSameRunAndDoesNotDuplicateEvents()
    {
        using var coordinator = new TaskRunCoordinator();
        var run = await coordinator.SubmitAsync(Submit());
        var paused = await coordinator.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
        var command = new ResumeRunCommand(Guid.NewGuid(), run.Id, paused.RunVersion);
        var resumed = await coordinator.ResumeAsync(command);
        Assert.Equal(run.Id, resumed.Id);
        Assert.Equal(RunStatus.Queued, resumed.Status);
        var count = coordinator.GetEvents(run.Id).Count;
        await coordinator.ResumeAsync(command);
        Assert.Equal(count, coordinator.GetEvents(run.Id).Count);
    }

    [Fact]
    public async Task CommandsGuiOnlyPauseAllowsNonGuiBoundaryAndCannotApproveDesktopAccess()
    {
        using var coordinator = new TaskRunCoordinator();
        var run = await coordinator.SubmitAsync(Submit());
        var paused = await coordinator.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        Assert.Equal(RunStatus.Queued, paused.Status);
        Assert.Equal(PauseScope.GuiOnly, paused.PauseScope);
        await coordinator.WaitAtBoundaryAsync(run.Id, CancellationToken.None);
        var resumed = await coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
        Assert.Null(resumed.PauseScope);
        Assert.Equal(RunStatus.Queued, resumed.Status);
    }

    [Fact]
    public async Task CommandsRejectCrossCommandIdReuseAndPreCancelledSubmission()
    {
        using var coordinator = new TaskRunCoordinator();
        var submit = Submit();
        var run = await coordinator.SubmitAsync(submit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CancelAsync(new(submit.CommandId, run.Id, run.RunVersion)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SubmitAsync(Submit(), cancel.Token));
    }

    private static SubmitTaskCommand Submit() => new(Guid.NewGuid(), "Local diagnostic", new TaskOrigin("desktop"));
}
