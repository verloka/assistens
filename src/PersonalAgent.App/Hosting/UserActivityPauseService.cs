using Microsoft.Extensions.Logging;
using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.App;

// Real App startup (or an attended fixture) opts in; headless hosts do not install hooks.
public sealed class UserActivityPauseService(IDesktopAccessCoordinator desktop, TakeControlHotkeyService takeover,
    ILogger<UserActivityPauseService> logger, Func<Action<ObservedInputActivity>, WindowsInputObserver>? observerFactory = null) : IAsyncDisposable, IDisposable
{
    private WindowsInputObserver? observer;
    private Task watch = Task.CompletedTask;
    private volatile bool required, stopping;
    private long ownEvents;
    private long injectedEvents, markerWidthMismatches, unflaggedOwnEvents, lowerOwnEvents;
    public long IgnoredOwnEvents => Interlocked.Read(ref ownEvents);
    public bool IsMonitoring => observer?.Status.Running == true;
    public string ObservationDiagnostic
    {
        get
        {
            var status = observer?.Status;
            return $"observerRunning={status?.Running == true}; observedEvents={status?.Events ?? 0}; observerError={status?.ErrorCode ?? "none"}; "
                + $"injectedEvents={Interlocked.Read(ref injectedEvents)}; markerWidthMismatch={Interlocked.Read(ref markerWidthMismatches)}; "
                + $"ownUnflagged={Interlocked.Read(ref unflaggedOwnEvents)}; ownLower={Interlocked.Read(ref lowerOwnEvents)}";
        }
    }
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(stopping, this);
        if (observer is not null) return IsMonitoring;
        required = true;
        observer = observerFactory?.Invoke(OnActivity) ?? new WindowsInputObserver(OnActivity);
        try
        {
            var started = await observer.StartAsync(ct).ConfigureAwait(false);
            if (!started) desktop.TakeControl();
            watch = WatchAsync(observer);
            return started;
        }
        catch { desktop.TakeControl(); throw; }
    }
    public void RequireAvailable()
    {
        if (required && !IsMonitoring) throw new DesktopAccessException("InputObservationUnavailable");
    }
    private void OnActivity(ObservedInputActivity activity)
    {
        if (stopping) return;
        if (activity.Injected) Interlocked.Increment(ref injectedEvents);
        var marker = InputActivityAttribution.InspectMarker(activity);
        if (marker.Exact)
        {
            if (!activity.Injected) Interlocked.Increment(ref unflaggedOwnEvents);
            if (activity.LowerIntegrityInjected) Interlocked.Increment(ref lowerOwnEvents);
        }
        else if (activity.Injected && marker.Lower32)
            Interlocked.Increment(ref markerWidthMismatches);
        if (!InputActivityAttribution.RequiresTakeover(activity)) { Interlocked.Increment(ref ownEvents); return; }
        _ = takeover.AutoPauseForUserActivity(); // Closes only the current GUI owner's gate before returning.
    }
    private async Task WatchAsync(WindowsInputObserver active)
    {
        await active.Completion.ConfigureAwait(false);
        if (stopping) return;
        _ = takeover.AutoPauseForUserActivity();
        desktop.TakeControl(); // Failure also prevents a future unobserved acquisition.
        logger.LogWarning("Input observation stopped; desktop automation remains blocked. Restart Personal Agent to restore observation.");
    }
    public async ValueTask DisposeAsync()
    {
        stopping = true;
        if (required) desktop.TakeControl();
        if (observer is not null) await observer.DisposeAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
