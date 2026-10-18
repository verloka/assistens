using PersonalAgent.Core.Computer;

namespace PersonalAgent.Computer;

// One controller per opaque grant. Resume creates another controller, never replaces this lease.
// Not registered as an agent tool or automatically authorized in DI.
public sealed class WindowsComputerController : IObservedComputerController
{
    private readonly IDesktopAccessCoordinator access;
    private readonly DesktopLease lease;
    private readonly WindowsScreenCapture capture;
    private readonly WindowsMouseInput mouse;
    private readonly WindowsKeyboardInput keyboard;
    private readonly object sync = new();
    private ScreenCapture? returnedFrame, inputFrame;
    private bool confirmed;
    private long observationVersion;

    public WindowsComputerController(IDesktopAccessCoordinator access, DesktopLease lease, Func<string, bool>? allowChord = null)
        : this(access, lease, new WindowsCaptureNative(), new WindowsInputNative(),
            () => new WindowsCaptureNative().GetMonitors(), allowChord) { }

    internal WindowsComputerController(IDesktopAccessCoordinator access, DesktopLease lease,
        IWindowsCaptureNative captureNative, IWindowsKeyboardNative inputNative,
        Func<IReadOnlyList<ScreenMonitor>> readMonitors, Func<string, bool>? allowChord = null)
    {
        this.access = access; this.lease = lease;
        capture = new(access, lease, captureNative);
        mouse = new(access, lease, inputNative, readMonitors);
        keyboard = new(access, lease, inputNative, allowChord);
    }

    public async Task<ScreenCapture> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct = default)
    {
        long version;
        lock (sync) { confirmed = false; returnedFrame = inputFrame = null; version = ++observationVersion; }
        var frame = await capture.CaptureAsync(options, ct).ConfigureAwait(false);
        lock (sync)
        {
            if (!access.IsCurrent(lease) || version != observationVersion) throw Error("StaleDesktopContext");
            returnedFrame = frame;
            inputFrame = frame with { ImageBytes = frame.ImageBytes.ToArray(), Monitors = frame.Monitors.ToArray() };
            return frame;
        }
    }

    public void ConfirmObservation(ScreenCapture observation)
    {
        lock (sync)
        {
            if (!access.IsCurrent(lease)) throw Error("StaleDesktopContext");
            if (returnedFrame is null || !ReferenceEquals(returnedFrame, observation)) throw Error("FreshScreenshotRequired");
            confirmed = true;
        }
    }

    public Task ClickAsync(ScreenPoint point, MouseButton button, CancellationToken ct = default) =>
        mouse.ClickAsync(Observation(), point, button, ct);
    public Task ScrollAsync(ScreenPoint point, ScrollDirection direction, int amount, CancellationToken ct = default) =>
        mouse.ScrollAsync(Observation(), point, direction, amount, ct);
    public Task TypeAsync(string text, CancellationToken ct = default)
    { Observation(); return keyboard.TypeAsync(text, lease.Generation, ct); }
    public Task PressKeyAsync(KeyCombination keys, CancellationToken ct = default)
    { Observation(); return keyboard.PressKeyAsync(keys, lease.Generation, ct); }

    private ScreenCapture Observation()
    {
        lock (sync)
        {
            if (!access.IsCurrent(lease)) throw Error("StaleDesktopContext");
            if (inputFrame is null) throw Error("FreshScreenshotRequired");
            if (!confirmed) throw Error("ScreenReevaluationRequired");
            return inputFrame;
        }
    }
    private static ComputerInputException Error(string code) => new(code, 0, 0, 0, true);
}
