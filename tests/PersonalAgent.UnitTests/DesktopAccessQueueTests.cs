using PersonalAgent.Core.Computer;

namespace PersonalAgent.UnitTests;

public sealed class DesktopAccessQueueTests
{
    [Fact]
    public async Task WaitingRunsAcquireInOrderOnlyAfterActualOwnerRelease()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        var secondId = Guid.NewGuid(); var thirdId = Guid.NewGuid();
        var second = access.WaitAcquireAsync(secondId); var third = access.WaitAcquireAsync(thirdId);
        Assert.False(second.IsCompleted); Assert.False(third.IsCompleted);
        Assert.Equal(owner.RunId, access.Snapshot.OwnerRunId);
        await access.ReleaseAsync(owner);
        var grantedSecond = await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(secondId, grantedSecond.RunId); Assert.False(third.IsCompleted);
        await Assert.ThrowsAsync<DesktopAccessException>(() => access.AcquireAsync(Guid.NewGuid()));
        await access.ReleaseAsync(grantedSecond);
        var grantedThird = await third.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(thirdId, grantedThird.RunId); Assert.True(grantedThird.Generation > grantedSecond.Generation);
    }

    [Fact]
    public async Task ReleasedHumanPauseDoesNotGrantAnotherRunUntilExplicitOwnerResume()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var first = await access.AcquireAsync(Guid.NewGuid());
        using var stop = new CancellationTokenSource();
        var second = access.WaitAcquireAsync(Guid.NewGuid(), stop.Token);
        access.TakeControl(); await access.ReleasePausedRunAsync(first.RunId);
        Assert.False(second.IsCompleted); Assert.Null(access.Snapshot.OwnerRunId);
        Assert.Throws<DesktopAccessException>(() => access.ResumeRun(Guid.NewGuid()));
        Assert.False(second.IsCompleted);
        access.ResumeRun(first.RunId);
        var next = await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(access.IsCurrent(next)); Assert.False(access.IsCurrent(first));
    }

    [Fact]
    public async Task TokenCancellationRemovesQueueHeadWithoutChangingOwnerOrBlockingNextRun()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        using var stop = new CancellationTokenSource();
        var cancelled = access.WaitAcquireAsync(Guid.NewGuid(), stop.Token);
        var next = access.WaitAcquireAsync(Guid.NewGuid());
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(access.IsCurrent(owner)); Assert.False(next.IsCompleted);
        await access.ReleaseAsync(owner);
        Assert.True(access.IsCurrent(await next.WaitAsync(TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public async Task RunCancellationRemovesAllItsWaitersAndKeepsOtherRunsIntact()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        var cancelledId = Guid.NewGuid();
        var first = access.WaitAcquireAsync(cancelledId); var duplicate = access.WaitAcquireAsync(cancelledId);
        var other = access.WaitAcquireAsync(Guid.NewGuid());
        await access.CancelRunAsync(cancelledId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
        Assert.True(access.IsCurrent(owner));
        await access.ReleaseAsync(owner);
        Assert.True(access.IsCurrent(await other.WaitAsync(TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public async Task AdmissionIsRecheckedAfterWaitingAndCannotBeInheritedFromEarlierApproval()
    {
        var allowed = true; var access = new DesktopAccessCoordinator(_ => allowed);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        var pending = access.WaitAcquireAsync(Guid.NewGuid());
        allowed = false; await access.ReleaseAsync(owner);
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => pending);
        Assert.Equal("DesktopAccessDenied", error.Code); Assert.Null(access.Snapshot.OwnerRunId);
        allowed = true;
        Assert.True(access.IsCurrent(await access.AcquireAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task CancelledOwnerDoesNotAutomaticallyLiftHumanPauseForQueuedRun()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        using var stop = new CancellationTokenSource();
        var pending = access.WaitAcquireAsync(Guid.NewGuid(), stop.Token);
        access.TakeControl(); await access.CancelRunAsync(owner.RunId);
        Assert.False(pending.IsCompleted); Assert.Equal(DesktopAccessState.PausedForUser, access.Snapshot.State);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task UncertainInputFailsWaitersWithoutNewGrantAndDefaultPolicyDeniesQueue()
    {
        var denied = await Assert.ThrowsAsync<DesktopAccessException>(() => new DesktopAccessCoordinator().WaitAcquireAsync(Guid.NewGuid()));
        Assert.Equal("DesktopAccessDenied", denied.Code);
        var access = new DesktopAccessCoordinator(_ => true);
        var owner = await access.AcquireAsync(Guid.NewGuid());
        var pending = access.WaitAcquireAsync(Guid.NewGuid());
        access.ReportInputCleanupFailure(owner);
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => pending);
        Assert.Equal("InputCleanupUncertain", error.Code); Assert.Equal(owner.RunId, access.Snapshot.OwnerRunId);
    }
}
