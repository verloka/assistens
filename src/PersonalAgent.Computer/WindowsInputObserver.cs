using PersonalAgent.Core.Computer;
using System.Windows.Threading;

namespace PersonalAgent.Computer;

public enum ObservedInputDevice { Keyboard, Mouse }
// Deliberately excludes key codes, text, pointer positions and window/application identities.
public readonly record struct ObservedInputActivity(ObservedInputDevice Device, bool Injected, bool LowerIntegrityInjected, uint Tick, nuint ExtraInfo);
public sealed record InputObservationStatus(bool Running, long Events, string? ErrorCode);

// Explicitly started prototype. No App registration, input injection, capture, polling or event backlog.
// The callback runs on a dedicated native message thread; consumers must do bounded synchronous work only.
public sealed class WindowsInputObserver : IAsyncDisposable
{
    private readonly IWindowsActivityHooks hooks;
    private readonly ISystemIdleSource idle;
    private readonly Action<ObservedInputActivity> observe;
    private readonly object sync = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? dispatcher;
    private bool started, stopping, running;
    private long events;
    private string? error;

    public WindowsInputObserver(Action<ObservedInputActivity> observe) : this(observe, new WindowsActivityHooks(), new WindowsSystemIdleSource()) { }
    internal WindowsInputObserver(Action<ObservedInputActivity> observe, IWindowsActivityHooks hooks, ISystemIdleSource idle)
    { ArgumentNullException.ThrowIfNull(observe); this.observe = observe; this.hooks = hooks; this.idle = idle; }
    public InputObservationStatus Status { get { lock (sync) return new(running && !stopping, Interlocked.Read(ref events), error); } }
    public Task Completion => finished.Task;
    public SystemIdleSnapshot ReadIdle() => idle.Read(); // Existing session-aware GetLastInputInfo probe, on demand only.

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (!started)
            {
                started = true;
                var thread = new Thread(Run) { IsBackground = true, Name = "PersonalAgent.InputObservation" };
                thread.SetApartmentState(ApartmentState.STA); thread.Start();
            }
        }
        try { return await ready.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await DisposeAsync().ConfigureAwait(false); throw; }
    }
    private void Run()
    {
        try
        {
            var loop = Dispatcher.CurrentDispatcher;
            lock (sync) { dispatcher = loop; if (stopping) return; }
            using var registration = hooks.Install(OnActivity);
            lock (sync)
            {
                if (stopping) return;
                running = true; ready.TrySetResult(true);
            }
            Dispatcher.Run(); // Blocks on messages while idle; no periodic timer or queued activity history.
        }
        catch (Exception) { lock (sync) error ??= "InputObservationUnavailable"; }
        finally
        {
            lock (sync) { running = false; stopping = true; dispatcher = null; }
            ready.TrySetResult(false); finished.TrySetResult();
        }
    }
    private void OnActivity(ObservedInputActivity activity)
    {
        lock (sync) if (stopping) return;
        Interlocked.Increment(ref events);
        try { observe(activity); }
        catch (Exception)
        {
            lock (sync) error = "InputObservationCallbackFailed";
            RequestStop(); // Never unwind a hook callback or silently continue with a broken consumer.
        }
    }
    private void RequestStop()
    {
        lock (sync)
        {
            stopping = true;
            if (!started) { ready.TrySetResult(false); finished.TrySetResult(); }
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }
    public ValueTask DisposeAsync() { RequestStop(); return new(finished.Task); }
}

internal interface IWindowsActivityHooks
{
    IDisposable Install(Action<ObservedInputActivity> observe);
}
