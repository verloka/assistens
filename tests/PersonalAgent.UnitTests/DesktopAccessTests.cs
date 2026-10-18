using PersonalAgent.Core.Computer;

namespace PersonalAgent.UnitTests;

public sealed class DesktopAccessTests
{
    [Fact]
    public async Task RevocationClosesGateSynchronouslyButReleaseWaitsForActiveSection()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        var atomic = coordinator.EnterAtomic(lease);
        var pause = coordinator.RevokeAsync(lease);
        Assert.False(pause.IsCompleted);
        Assert.False(coordinator.IsCurrent(lease));
        Assert.Throws<DesktopAccessException>(() => coordinator.EnterAtomic(lease));
        var release = coordinator.ReleaseAsync(lease);
        Assert.False(release.IsCompleted);
        await Assert.ThrowsAsync<DesktopAccessException>(() => coordinator.AcquireAsync(Guid.NewGuid()));
        atomic.Dispose(); atomic.Dispose(); // Idempotent atomic disposal cannot corrupt the active counter.
        await pause;
        Assert.True(await release);
        var next = await coordinator.AcquireAsync(Guid.NewGuid());
        Assert.True(next.Generation > lease.Generation);
        Assert.False(coordinator.IsCurrent(lease));
    }

    [Fact]
    public async Task CancelingReleaseWaitKeepsClosedOwnershipUntilExplicitRelease()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        using var atomic = coordinator.EnterAtomic(lease);
        using var cts = new CancellationTokenSource();
        var release = coordinator.ReleaseAsync(lease, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
        Assert.False(coordinator.IsCurrent(lease));
        Assert.Equal(lease.RunId, coordinator.Snapshot.OwnerRunId);
        await Assert.ThrowsAsync<DesktopAccessException>(() => coordinator.AcquireAsync(Guid.NewGuid()));
        atomic.Dispose();
        Assert.True(await coordinator.ReleaseAsync(lease));
    }

    [Fact]
    public async Task AdmissionDefaultsDeniedAndCanceledOrInvalidRequestsNeverCreateGrant()
    {
        var denied = new DesktopAccessCoordinator();
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => denied.AcquireAsync(Guid.NewGuid()));
        Assert.Equal("DesktopAccessDenied", error.Code);
        Assert.Equal(new DesktopAccessSnapshot(null, 0), denied.Snapshot);
        var coordinator = new DesktopAccessCoordinator(_ => true);
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.AcquireAsync(Guid.Empty));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AcquireAsync(Guid.NewGuid(), cts.Token));
        Assert.Equal(new DesktopAccessSnapshot(null, 0), coordinator.Snapshot);
    }

    [Fact]
    public async Task OnlyOneOwnerAndSameOwnerAcquisitionIsIdempotent()
    {
        IDesktopAccessCoordinator coordinator = new DesktopAccessCoordinator(_ => true);
        var owner = Guid.NewGuid();
        var lease = await coordinator.AcquireAsync(owner);
        Assert.Same(lease, await coordinator.AcquireAsync(owner));
        var error = await Assert.ThrowsAsync<DesktopAccessException>(() => coordinator.AcquireAsync(Guid.NewGuid()));
        Assert.Equal("DesktopBusy", error.Code);
        Assert.True(coordinator.IsCurrent(lease));
        Assert.Equal(new DesktopAccessSnapshot(owner, 1) { State = DesktopAccessState.Granted }, coordinator.Snapshot);
    }

    [Fact]
    public async Task ReleaseIsGrantBoundAndStaleReleaseCannotDropNewSameRunGrant()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var owner = Guid.NewGuid();
        var old = await coordinator.AcquireAsync(owner);
        Assert.True(await coordinator.ReleaseAsync(old));
        Assert.False(coordinator.IsCurrent(old));
        var current = await coordinator.AcquireAsync(owner);
        Assert.True(current.Generation > old.Generation);
        Assert.False(await coordinator.ReleaseAsync(old));
        Assert.True(coordinator.IsCurrent(current));
        Assert.False(coordinator.IsCurrent(old));
        Assert.Equal(current.Generation, coordinator.Snapshot.Generation);
    }

    [Fact]
    public async Task MatchingIdsAndGenerationFromDifferentCoordinatorCannotForgeGrant()
    {
        var first = new DesktopAccessCoordinator(_ => true);
        var second = new DesktopAccessCoordinator(_ => true);
        var owner = Guid.NewGuid();
        var actual = await first.AcquireAsync(owner);
        var foreign = await second.AcquireAsync(owner);
        Assert.Equal(actual.Generation, foreign.Generation);
        Assert.False(first.IsCurrent(foreign));
        Assert.False(await first.ReleaseAsync(foreign));
        Assert.True(first.IsCurrent(actual));
    }

    [Fact]
    public async Task ConcurrentDifferentRunsProduceExactlyOneCurrentGrant()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            try { return await coordinator.AcquireAsync(Guid.NewGuid()); }
            catch (DesktopAccessException error) when (error.Code == "DesktopBusy") { return null; }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results.OfType<DesktopLease>());
        Assert.True(coordinator.IsCurrent(winner));
        Assert.Equal(1, coordinator.Snapshot.Generation);
        Assert.Equal(winner.RunId, coordinator.Snapshot.OwnerRunId);
    }

    [Fact]
    public async Task RepeatedOwnershipTurnoverNeverRevalidatesOldTokens()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var previous = new List<DesktopLease>();
        for (var turn = 1; turn <= 50; turn++)
        {
            var lease = await coordinator.AcquireAsync(Guid.NewGuid());
            Assert.Equal(turn, lease.Generation);
            Assert.All(previous, old => Assert.False(coordinator.IsCurrent(old)));
            Assert.True(await coordinator.ReleaseAsync(lease));
            previous.Add(lease);
        }
        Assert.Null(coordinator.Snapshot.OwnerRunId);
        Assert.Equal(50, coordinator.Snapshot.Generation);
    }
}
