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
    Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default);
}

// Local resource ownership only. Trusted admission defaults denied; no Win32/UI, idle policy or input.
// Callers explicitly release/revoke grants; pause/atomic boundaries are layered onto this coordinator.
public sealed class DesktopAccessCoordinator(Func<Guid, bool>? admit = null) : IDesktopAccessCoordinator
{
    private readonly Func<Guid, bool> admit = admit ?? (_ => false);
    private readonly object sync = new();
    private DesktopLease? current;
    private long generation;

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
                return Task.FromResult(current);
            }
            var next = new DesktopLease(runId, checked(generation + 1));
            generation = next.Generation;
            current = next;
            return Task.FromResult(next);
        }
    }

    public bool IsCurrent(DesktopLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync) return ReferenceEquals(current, lease);
    }

    public Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, lease)) return Task.FromResult(false);
            current = null;
            return Task.FromResult(true);
        }
    }
}

public sealed class DesktopAccessException(string code) : InvalidOperationException($"Desktop access stopped: {code}.")
{
    public string Code { get; } = code;
}
