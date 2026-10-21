using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.App;

public enum DesktopEnvironmentChange { Session, Suspend, Resume, Display }

// Opt-in real WPF startup only. No polling, capture, input, session switching or power control.
public sealed class DesktopEnvironmentPauseService(IDesktopAccessCoordinator desktop, TakeControlHotkeyService takeover,
    ILogger<DesktopEnvironmentPauseService> logger, Func<Action<DesktopEnvironmentChange>, IDisposable>? subscribe = null) : IAsyncDisposable, IDisposable
{
    private Task<bool>? starting;
    private readonly object sync = new();
    private IDisposable? subscription;
    private volatile bool required, stopping, monitoring;
    private long changes;
    private readonly long[] counts = new long[4];
    private int lastChange = -1;
    public bool IsMonitoring => monitoring;
    public long ObservedChanges => Interlocked.Read(ref changes);
    public long ChangeCount(DesktopEnvironmentChange change) => Interlocked.Read(ref counts[(int)change]);
    public DesktopEnvironmentChange? LastChange
    { get { var value = Volatile.Read(ref lastChange); return value < 0 ? null : (DesktopEnvironmentChange)value; } }
    public Task<bool> StartAsync()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            required = true;
            // Do not let static SystemEvents subscriptions capture the UI SynchronizationContext.
            return starting ??= Task.Run(() =>
            {
                try { subscription = subscribe?.Invoke(OnChange) ?? new WindowsDesktopChanges(OnChange); monitoring = !stopping; return monitoring; }
                catch (Exception) { desktop.TakeControl(); logger.LogWarning("Desktop environment observation unavailable; GUI remains blocked."); return false; }
            });
        }
    }
    public void RequireAvailable()
    { if (required && !monitoring) throw new DesktopAccessException("DesktopEnvironmentObservationUnavailable"); }
    private void OnChange(DesktopEnvironmentChange change)
    {
        if (stopping) return;
        Interlocked.Increment(ref changes); Volatile.Write(ref lastChange, (int)change);
        Interlocked.Increment(ref counts[(int)change]);
        try { _ = takeover.PauseForDesktopEnvironmentChange(); }
        catch (Exception) { monitoring = false; desktop.TakeControl(); logger.LogWarning("Desktop environment pause failed; GUI remains blocked."); }
    }
    public async ValueTask DisposeAsync()
    {
        Task<bool>? started;
        lock (sync) { stopping = true; monitoring = false; started = starting; }
        if (required) desktop.TakeControl();
        if (started is not null) await started.ConfigureAwait(false);
        Interlocked.Exchange(ref subscription, null)?.Dispose();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class WindowsDesktopChanges : IDisposable
    {
        private readonly Action<DesktopEnvironmentChange> changed;
        public WindowsDesktopChanges(Action<DesktopEnvironmentChange> changed)
        {
            this.changed = changed;
            try
            {
                SystemEvents.SessionSwitch += Session;
                SystemEvents.PowerModeChanged += Power;
                SystemEvents.DisplaySettingsChanging += Display;
                SystemEvents.DisplaySettingsChanged += Display;
            }
            catch { Dispose(); throw; }
        }
        private void Session(object? sender, SessionSwitchEventArgs e) => changed(DesktopEnvironmentChange.Session);
        private void Power(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) changed(DesktopEnvironmentChange.Suspend);
            else if (e.Mode == PowerModes.Resume) changed(DesktopEnvironmentChange.Resume);
        }
        private void Display(object? sender, EventArgs e) => changed(DesktopEnvironmentChange.Display);
        public void Dispose()
        {
            SystemEvents.SessionSwitch -= Session; SystemEvents.PowerModeChanged -= Power;
            SystemEvents.DisplaySettingsChanging -= Display; SystemEvents.DisplaySettingsChanged -= Display;
        }
    }
}
