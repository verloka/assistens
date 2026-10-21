using Microsoft.Extensions.Logging;
using PersonalAgent.Computer;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.App;

public sealed record DesktopHandoff(Guid? RunId, long Generation, bool Confirmed, string? Error);

// Explicitly started by real WPF App startup; headless integration hosts do not register global keys.
public sealed class TakeControlHotkeyService(IDesktopAccessCoordinator desktop, TaskRunCoordinator runs,
    ILogger<TakeControlHotkeyService> logger) : IDisposable
{
    private WindowsTakeControlHotkey? hotkey;
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;
    private readonly object sync = new();
    private Task pending = Task.CompletedTask;
    private DesktopHandoff handoff = new(null, 0, false, null);
    public Task Completion { get { lock (sync) return pending; } }
    public DesktopHandoff Handoff => Volatile.Read(ref handoff);
    public bool IsRegistered => hotkey is not null;
    public bool Start(TakeControlHotkeyGesture? gesture = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hotkey is not null) return true;
        try { hotkey = new WindowsTakeControlHotkey(TakeControl, gesture); return true; }
        catch (TakeControlHotkeyException error)
        {
            desktop.TakeControl(); // Registration failure must not permit later GUI acquisition.
            logger.LogWarning("Take Control shortcut registration failed: {NativeError}; GUI remains blocked.", error.NativeError);
            return false;
        }
    }
    public void TakeControl() => _ = TakeControlAsync();
    public Task TakeControlAsync(Guid? targetRunId = null)
    {
        if (disposed) return Task.CompletedTask;
        var owner = desktop.TakeControl(); // Synchronous local gate first, no model/store/UI wait.
        lock (sync)
        {
            if (!pending.IsCompleted) return pending;
            var runId = owner ?? desktop.Snapshot.HumanControlOwnerRunId ?? targetRunId ??
                runs.GetRuns().FirstOrDefault(run => !TaskRunStateMachine.IsTerminal(run.Status) && run.Status != RunStatus.Blocked)?.Id;
            return BeginHandoff(runId);
        }
    }
    // Called from a low-level input callback: only local gate/metadata work here, never storage or UI waits.
    public Task AutoPauseForUserActivity()
    {
        if (disposed) return Task.CompletedTask;
        var runId = desktop.TakeControlIfOwned();
        return runId is null ? Task.CompletedTask : BeginHandoff(runId, PauseReason.AutoPause);
    }
    private Task BeginHandoff(Guid? runId, PauseReason? reason = null)
    {
        lock (sync)
        {
            if (!pending.IsCompleted) return pending;
            Volatile.Write(ref handoff, new(runId, desktop.Snapshot.Generation, false, null));
            // Never hold this shared callback lock across even a synchronously completing store operation.
            return pending = Task.Run(() => PauseOwnerAsync(runId, reason));
        }
    }
    private async Task PauseOwnerAsync(Guid? runId, PauseReason? reason = null)
    {
        try
        {
            if (runId is { } id)
            {
                var run = runs.GetRun(id);
                if (TaskRunStateMachine.IsTerminal(run.Status)) throw new InvalidOperationException("Run is terminal.");
                if (run.PauseScope is null)
                    await runs.PauseAsync(new(Guid.NewGuid(), id, run.RunVersion, PauseScope.EntireRun, reason), stopping.Token).ConfigureAwait(false);
                await runs.ConfirmDesktopPauseAsync(id, stopping.Token).ConfigureAwait(false);
            }
            var snapshot = desktop.Snapshot;
            if (snapshot.InputCleanupUncertain || snapshot.OwnerRunId is not null || snapshot.State != DesktopAccessState.PausedForUser)
                throw new DesktopAccessException("DesktopPauseUnconfirmed");
            Volatile.Write(ref handoff, new(runId, snapshot.Generation, true, null));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception)
        {
            Volatile.Write(ref handoff, new(runId, desktop.Snapshot.Generation, false, "PauseUnconfirmed"));
            if (runId is { } id && desktop.Snapshot.InputCleanupUncertain)
            {
                try { await runs.BlockUncertainInputAsync(id, stopping.Token).ConfigureAwait(false); }
                catch (Exception) { logger.LogWarning("Uncertain input checkpoint could not be saved; desktop remains blocked."); }
            }
            logger.LogWarning("Desktop access was revoked; safe pause could not be confirmed. Manual review required.");
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        desktop.TakeControl();
        stopping.Cancel();
        hotkey?.Dispose(); hotkey = null;
        stopping.Dispose();
    }
}
