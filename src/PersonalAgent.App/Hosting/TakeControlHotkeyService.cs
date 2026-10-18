using Microsoft.Extensions.Logging;
using PersonalAgent.Computer;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.App;

// Explicitly started by real WPF App startup; headless integration hosts do not register global keys.
public sealed class TakeControlHotkeyService(IDesktopAccessCoordinator desktop, TaskRunCoordinator runs,
    ILogger<TakeControlHotkeyService> logger) : IDisposable
{
    private WindowsTakeControlHotkey? hotkey;
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;
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
    public void TakeControl()
    {
        if (disposed) return;
        var owner = desktop.TakeControl(); // Synchronous local gate first, no model/store/UI wait.
        if (owner is { } runId) _ = PauseOwnerAsync(runId);
    }
    private async Task PauseOwnerAsync(Guid runId)
    {
        try
        {
            var run = runs.GetRun(runId);
            if (!TaskRunStateMachine.IsTerminal(run.Status))
                await runs.PauseAsync(new(Guid.NewGuid(), runId, run.RunVersion, PauseScope.EntireRun), stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("Desktop access was revoked; run pause could not be persisted. Manual review required."); }
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
