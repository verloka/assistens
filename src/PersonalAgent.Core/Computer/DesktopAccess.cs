namespace PersonalAgent.Core.Computer;

// Object identity is part of the grant: copying RunId/Generation cannot forge or revive access.
public sealed class DesktopLease
{
    internal DesktopLease(Guid runId, long generation) { RunId = runId; Generation = generation; }
    public Guid RunId { get; }
    public long Generation { get; }
}

public sealed record DesktopAccessSnapshot(Guid? OwnerRunId, long Generation);
public interface IDesktopAccessCoordinator
{
    DesktopAccessSnapshot Snapshot { get; }
    Task<DesktopLease> AcquireAsync(Guid runId, CancellationToken ct = default);
    bool IsCurrent(DesktopLease lease);
    IDisposable EnterAtomic(DesktopLease lease);
    Task RevokeAsync(DesktopLease lease);
    Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default);
}

// Local resource ownership only. Trusted admission defaults denied; no Win32/UI, idle policy or input.
// Callers explicitly release/revoke grants; task lifecycle, idle and resume policy stay outside.
public sealed class DesktopAccessCoordinator(Func<Guid, bool>? admit = null) : IDesktopAccessCoordinator
{
    private readonly Func<Guid, bool> admit = admit ?? (_ => false);
    private readonly object sync = new();
    private DesktopLease? current;
    private long generation;
    private bool revoked;
    private int active;
    private TaskCompletionSource? drained;

    public DesktopAccessSnapshot Snapshot { get { lock (sync) return new(current?.RunId, generation); } }

    public Task<DesktopLease> AcquireAsync(Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A desktop owner requires a run ID.", nameof(runId));
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (!admit(runId)) throw new DesktopAccessException("DesktopAccessDenied");
            if (current is not null)
            {
                if (current.RunId != runId) throw new DesktopAccessException("DesktopBusy");
                if (revoked) throw new DesktopAccessException("DesktopPaused");
                return Task.FromResult(current);
            }
            var next = new DesktopLease(runId, checked(generation + 1));
            generation = next.Generation;
            current = next;
            revoked = false;
            return Task.FromResult(next);
        }
    }

    public bool IsCurrent(DesktopLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync) return ReferenceEquals(current, lease) && !revoked;
    }

    public IDisposable EnterAtomic(DesktopLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync)
        {
            if (!ReferenceEquals(current, lease) || revoked) throw new DesktopAccessException("StaleDesktopContext");
            if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new AtomicSection(this);
        }
    }

    // Gate closes before returning the Task; callers need not await a model/store/UI to stop new work.
    // Ownership remains reserved until explicit Release, including while paused and after quiescence.
    public Task RevokeAsync(DesktopLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync)
        {
            if (!ReferenceEquals(current, lease)) return Task.CompletedTask;
            revoked = true;
            return active == 0 ? Task.CompletedTask : drained!.Task;
        }
    }

    public async Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Task wait;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, lease)) return false;
            revoked = true;
            wait = active == 0 ? Task.CompletedTask : drained!.Task;
        }
        await wait.WaitAsync(ct).ConfigureAwait(false);
        lock (sync)
        {
            if (!ReferenceEquals(current, lease)) return false;
            current = null;
            return true;
        }
    }

    private void ExitAtomic()
    {
        lock (sync) { if (--active == 0) drained!.TrySetResult(); }
    }
    private sealed class AtomicSection(DesktopAccessCoordinator owner) : IDisposable
    {
        private DesktopAccessCoordinator? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ExitAtomic();
    }
}

public sealed class DesktopAccessException(string code) : InvalidOperationException($"Desktop access stopped: {code}.")
{
    public string Code { get; } = code;
}
