namespace PersonalAgent.Core.Computer;

// Object identity is part of the grant: copying RunId/Generation cannot forge or revive access.
public sealed class DesktopLease
{
    internal DesktopLease(Guid runId, long generation) { RunId = runId; Generation = generation; }
    public Guid RunId { get; }
    public long Generation { get; }
}

public enum DesktopAccessState { Free, Granted, Pausing, PausedForUser }
public sealed record DesktopAccessSnapshot(Guid? OwnerRunId, long Generation)
{
    public DesktopAccessState State { get; init; }
    public bool InputCleanupUncertain { get; init; }
    public Guid? HumanControlOwnerRunId { get; init; }
    public long PauseVersion { get; init; }
}
public interface IDesktopAccessCoordinator
{
    DesktopAccessSnapshot Snapshot { get; }
    Task<DesktopLease> AcquireAsync(Guid runId, CancellationToken ct = default);
    Task<DesktopLease> WaitAcquireAsync(Guid runId, CancellationToken ct = default);
    bool IsCurrent(DesktopLease lease);
    IDisposable EnterAtomic(DesktopLease lease);
    Task RevokeAsync(DesktopLease lease);
    Task PauseRunAsync(Guid runId);
    void ResumeRun(Guid runId);
    long ValidateResumeRun(Guid runId);
    bool TryResumeRun(Guid runId, long expectedPauseVersion);
    Task CancelRunAsync(Guid runId);
    Guid? TakeControl();
    Guid? TakeControlIfOwned();
    void ReportInputCleanupFailure(DesktopLease lease);
    void RestoreInputCleanupFailure(Guid runId);
    Task<DesktopAccessSnapshot> ReleasePausedRunAsync(Guid runId, CancellationToken ct = default);
    Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default);
}

// Local resource ownership only. Trusted admission defaults denied; no Win32/UI, idle policy or input.
// Callers explicitly release/revoke grants; task lifecycle, idle and resume policy stay outside.
public sealed class DesktopAccessCoordinator(Func<Guid, bool>? admit = null,
    Func<Guid, CancellationToken, Task>? beforeAcquire = null) : IDesktopAccessCoordinator
{
    private readonly Func<Guid, bool> admit = admit ?? (_ => false);
    private readonly object sync = new();
    private DesktopLease? current;
    private long generation;
    private bool revoked;
    private int active;
    private TaskCompletionSource? drained;
    private readonly HashSet<Guid> pausedRuns = [];
    private bool userControl;
    private Guid? humanControlOwner;
    private bool inputCleanupUncertain;
    private long pauseVersion;
    private readonly LinkedList<Waiter> waiters = [];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DesktopAccessSnapshot Snapshot
    {
        get { lock (sync) return new(current?.RunId, generation) { State = current is null ?
            (userControl ? DesktopAccessState.PausedForUser : DesktopAccessState.Free) :
            !revoked ? DesktopAccessState.Granted : active > 0 ? DesktopAccessState.Pausing : DesktopAccessState.PausedForUser,
            InputCleanupUncertain = inputCleanupUncertain, HumanControlOwnerRunId = humanControlOwner, PauseVersion = pauseVersion }; }
    }

    public async Task<DesktopLease> AcquireAsync(Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A desktop owner requires a run ID.", nameof(runId));
        ct.ThrowIfCancellationRequested();
        // Admission may query task state. Never call external policy while holding the resource lock.
        var allowed = admit(runId);
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
            if (userControl || pausedRuns.Contains(runId)) throw new DesktopAccessException("DesktopPaused");
            if (!allowed) throw new DesktopAccessException("DesktopAccessDenied");
            if (current is not null)
            {
                if (current.RunId != runId) throw new DesktopAccessException("DesktopBusy");
                if (revoked) throw new DesktopAccessException("DesktopPaused");
                return current;
            }
            if (waiters.Count > 0) throw new DesktopAccessException("DesktopBusy");
        }
        var version = Snapshot.PauseVersion;
        if (beforeAcquire is not null) await beforeAcquire(runId, ct).ConfigureAwait(false);
        allowed = admit(runId);
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
            if (userControl || pausedRuns.Contains(runId) || pauseVersion != version) throw new DesktopAccessException("DesktopPaused");
            if (!allowed) throw new DesktopAccessException("DesktopAccessDenied");
            if (current is not null || waiters.Count > 0) throw new DesktopAccessException("DesktopBusy");
            return Grant(runId);
        }
    }

    public async Task<DesktopLease> WaitAcquireAsync(Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A desktop owner requires a run ID.", nameof(runId));
        ct.ThrowIfCancellationRequested();
        LinkedListNode<Waiter> node;
        lock (sync) node = waiters.AddLast(new Waiter(runId));
        try
        {
            while (true)
            {
                // Policy may query task state: keep it outside the resource lock, including after wakeup.
                var allowed = admit(runId);
                Task? wait = null;
                long version;
                lock (sync)
                {
                    ct.ThrowIfCancellationRequested();
                    if (node.Value.Cancelled) throw new OperationCanceledException("Desktop wait cancelled.", new CancellationToken(true));
                    if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
                    if (!allowed) throw new DesktopAccessException("DesktopAccessDenied");
                    if (!userControl && !pausedRuns.Contains(runId))
                    {
                        if (current?.RunId == runId && !revoked) return current;
                        if (current is not null || !ReferenceEquals(waiters.First, node)) wait = changed.Task;
                    }
                    else wait = changed.Task;
                    version = pauseVersion;
                }
                if (wait is not null) { await wait.WaitAsync(ct).ConfigureAwait(false); continue; }
                if (beforeAcquire is not null) await beforeAcquire(runId, ct).ConfigureAwait(false);
                allowed = admit(runId);
                lock (sync)
                {
                    ct.ThrowIfCancellationRequested();
                    if (node.Value.Cancelled) throw new OperationCanceledException("Desktop wait cancelled.", new CancellationToken(true));
                    if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
                    if (!allowed) throw new DesktopAccessException("DesktopAccessDenied");
                    if (pauseVersion == version && !userControl && !pausedRuns.Contains(runId)
                        && current is null && ReferenceEquals(waiters.First, node)) return Grant(runId);
                }
            }
        }
        finally { lock (sync) { waiters.Remove(node); Pulse(); } }
    }

    private DesktopLease Grant(Guid runId)
    {
        current = new(runId, checked(++generation)); revoked = false;
        return current;
    }
    private void Pulse()
    {
        var previous = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
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
            Pulse();
            return active == 0 ? Task.CompletedTask : drained!.Task;
        }
    }

    public Task PauseRunAsync(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID required.", nameof(runId));
        lock (sync)
        {
            if (pausedRuns.Add(runId)) pauseVersion++;
            if (userControl && humanControlOwner is null) humanControlOwner = runId;
            if (current?.RunId != runId) return Task.CompletedTask;
            return RevokeAsync(current);
        }
    }

    public void ResumeRun(Guid runId)
    {
        lock (sync)
        {
            ValidateResumeRun(runId);
            if (current?.RunId == runId && revoked)
            {
                if (active > 0) throw new DesktopAccessException("DesktopPausing");
                current = null; // Never reopen the old lease; next acquire gets a new generation.
            }
            pausedRuns.Remove(runId);
            userControl = false;
            humanControlOwner = null;
            Pulse();
        }
    }

    public long ValidateResumeRun(Guid runId)
    {
        lock (sync)
        {
            if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
            if (userControl && humanControlOwner is { } owner && owner != runId) throw new DesktopAccessException("DesktopPaused");
            if (userControl && current is not null && current.RunId != runId) throw new DesktopAccessException("DesktopPaused");
            if (current?.RunId == runId && active > 0) throw new DesktopAccessException("DesktopPausing");
            return pauseVersion;
        }
    }

    public bool TryResumeRun(Guid runId, long expectedPauseVersion)
    {
        lock (sync)
        {
            if (pauseVersion != expectedPauseVersion) return false;
            try { ResumeRun(runId); return true; }
            catch (DesktopAccessException) { return false; }
        }
    }

    // Human takeover is global: even a run with no current lease cannot acquire behind the user's back.
    // Activity observation is different: ordinary input must not pause unrelated MCP-only work.
    public Guid? TakeControlIfOwned()
    {
        lock (sync) return current is null || revoked ? null : TakeControl();
    }

    public Guid? TakeControl()
    {
        lock (sync)
        {
            userControl = true;
            pauseVersion++;
            Pulse();
            if (current is null) return null;
            var runId = current.RunId;
            humanControlOwner = runId;
            _ = PauseRunAsync(runId);
            return runId;
        }
    }

    // The native adapter reports failed release before disposing its atomic section.
    // Never clear this uncertainty through Resume or a replacement lease.
    public void ReportInputCleanupFailure(DesktopLease lease)
    {
        lock (sync)
        {
            if (!ReferenceEquals(current, lease)) return;
            inputCleanupUncertain = true; revoked = true; userControl = true;
            pauseVersion++;
            humanControlOwner = lease.RunId; pausedRuns.Add(lease.RunId);
            Pulse();
        }
    }

    public void RestoreInputCleanupFailure(Guid runId)
    {
        lock (sync)
        {
            inputCleanupUncertain = true; userControl = true;
            pauseVersion++;
            humanControlOwner ??= runId; pausedRuns.Add(runId);
            if (current is not null) revoked = true;
            Pulse();
        }
    }

    public async Task<DesktopAccessSnapshot> ReleasePausedRunAsync(Guid runId, CancellationToken ct = default)
    {
        Task wait;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (!pausedRuns.Contains(runId)) throw new DesktopAccessException("DesktopNotPaused");
            if (current is not null && current.RunId != runId) throw new DesktopAccessException("DesktopBusy");
            wait = active == 0 ? Task.CompletedTask : drained!.Task;
        }
        await wait.WaitAsync(ct).ConfigureAwait(false);
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (inputCleanupUncertain) throw new DesktopAccessException("InputCleanupUncertain");
            if (!pausedRuns.Contains(runId)) throw new DesktopAccessException("DesktopNotPaused");
            if (current is not null)
            {
                if (current.RunId != runId) throw new DesktopAccessException("DesktopBusy");
                if (!revoked || active > 0) throw new DesktopAccessException("DesktopPausing");
                current = null;
                Pulse();
            }
            // Resource ownership is released; the human's logical pause owner remains reserved.
            return Snapshot;
        }
    }

    public Task CancelRunAsync(Guid runId)
    {
        lock (sync)
        {
            pausedRuns.Remove(runId);
            foreach (var waiter in waiters) if (waiter.RunId == runId) waiter.Cancelled = true;
            Pulse();
            return current?.RunId == runId ? ReleaseAsync(current) : Task.CompletedTask;
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
            Pulse();
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
    private sealed class Waiter(Guid runId)
    {
        public Guid RunId { get; } = runId;
        public bool Cancelled;
    }
}

public sealed class DesktopAccessException(string code) : InvalidOperationException($"Desktop access stopped: {code}.")
{
    public string Code { get; } = code;
}
