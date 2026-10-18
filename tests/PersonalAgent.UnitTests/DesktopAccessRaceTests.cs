using PersonalAgent.Core.Computer;

namespace PersonalAgent.UnitTests;

public sealed class DesktopAccessRaceTests
{
    [Fact]
    public async Task TakeoverBetweenAdmissionAndGrantCannotBeLost()
    {
        using var admissionReturned = new ManualResetEventSlim();
        var entered = Signal(); var waitingRun = Guid.NewGuid();
        var access = new DesktopAccessCoordinator(id =>
        {
            if (id == waitingRun)
            { entered.TrySetResult(); Assert.True(admissionReturned.Wait(TimeSpan.FromSeconds(5))); }
            return true;
        });
        var old = await access.AcquireAsync(Guid.NewGuid());
        using var stop = new CancellationTokenSource();
        // Preserve the outer Task: completion proves WaitAcquire reached its first await/grant.
        var launched = Task.Factory.StartNew(() => access.WaitAcquireAsync(waitingRun, stop.Token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            access.TakeControl(); await access.ReleasePausedRunAsync(old.RunId);
            admissionReturned.Set();
            var pending = await launched.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); Assert.Equal(old.Generation, access.Snapshot.Generation);
            Assert.Equal(DesktopAccessState.PausedForUser, access.Snapshot.State);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { admissionReturned.Set(); stop.Cancel(); }
    }

    [Fact]
    public async Task CancellationWhileAdmissionIsOnAnotherThreadCannotCreateGrantOrLeaveQueueHead()
    {
        using var releaseAdmission = new ManualResetEventSlim();
        var entered = Signal(); var waitingRun = Guid.NewGuid();
        var access = new DesktopAccessCoordinator(id =>
        {
            if (id == waitingRun)
            { entered.TrySetResult(); Assert.True(releaseAdmission.Wait(TimeSpan.FromSeconds(5))); }
            return true;
        });
        using var stop = new CancellationTokenSource();
        var launched = Task.Factory.StartNew(() => access.WaitAcquireAsync(waitingRun, stop.Token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel(); releaseAdmission.Set();
            var cancelled = await launched.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(0, access.Snapshot.Generation);
            Assert.True(access.IsCurrent(await access.AcquireAsync(Guid.NewGuid())));
        }
        finally { releaseAdmission.Set(); stop.Cancel(); }
    }

    [Fact]
    public async Task RepeatedPauseCannotReuseEarlierResumeVersionOrReopenOldGrant()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var old = await access.AcquireAsync(Guid.NewGuid());
        access.TakeControl(); await access.ReleasePausedRunAsync(old.RunId);
        var earlier = access.ValidateResumeRun(old.RunId);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => access.TakeControl())));
        Assert.False(access.TryResumeRun(old.RunId, earlier)); Assert.False(access.IsCurrent(old));
        Assert.Equal(DesktopAccessState.PausedForUser, access.Snapshot.State);
        var current = access.ValidateResumeRun(old.RunId);
        Assert.True(current > earlier); Assert.True(access.TryResumeRun(old.RunId, current));
        var fresh = await access.AcquireAsync(old.RunId);
        Assert.True(fresh.Generation > old.Generation); Assert.False(access.IsCurrent(old));
    }

    [Fact]
    public async Task TwoReleaseWaitersCannotReleaseReplacementOwnerAfterAtomicDrain()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var old = await access.AcquireAsync(Guid.NewGuid());
        using var atomic = access.EnterAtomic(old);
        var firstRelease = access.ReleaseAsync(old); var secondRelease = access.ReleaseAsync(old);
        var queued = access.WaitAcquireAsync(Guid.NewGuid());
        Assert.False(firstRelease.IsCompleted); Assert.False(secondRelease.IsCompleted); Assert.False(queued.IsCompleted);
        atomic.Dispose();
        var outcomes = await Task.WhenAll(firstRelease, secondRelease).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, outcomes.Count(done => done));
        var replacement = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(access.IsCurrent(replacement)); Assert.False(await access.ReleaseAsync(old));
        Assert.True(access.IsCurrent(replacement));
    }

    [Fact]
    public async Task ConcurrentSameRunWaitersShareOneOpaqueGrantWithoutGenerationInflation()
    {
        var access = new DesktopAccessCoordinator(_ => true); var owner = Guid.NewGuid();
        var start = Signal();
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        { await start.Task; return await access.WaitAcquireAsync(owner); })).ToArray();
        start.SetResult();
        var grants = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));
        var single = Assert.Single(grants.Distinct());
        Assert.Equal(1, single.Generation); Assert.True(access.IsCurrent(single));
        Assert.All(grants, grant => Assert.Same(single, grant));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
