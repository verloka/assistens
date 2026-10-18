using PersonalAgent.Core.Computer;
using System.IO;
using System.Windows.Media.Imaging;

namespace PersonalAgent.Computer;

// The trusted caller supplies authorization/generation; tool arguments cannot grant access.
// No caller policy configured means denied. This class is not registered as an agent tool yet.
public sealed class WindowsScreenCapture
{
    private readonly Func<(bool Allowed, long Generation)> authorize;
    private readonly IWindowsCaptureNative native;

    public WindowsScreenCapture(Func<(bool Allowed, long Generation)>? authorize = null)
        : this(authorize, new WindowsCaptureNative()) { }
    internal WindowsScreenCapture(Func<(bool Allowed, long Generation)>? authorize, IWindowsCaptureNative native)
    {
        this.authorize = authorize ?? (() => (false, 0));
        this.native = native;
    }

    public Task<ScreenCapture> CaptureAsync(ScreenshotOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CaptureCore(options, cancellationToken), cancellationToken);
    }

    private ScreenCapture CaptureCore(ScreenshotOptions options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var permission = authorize();
            if (!permission.Allowed || permission.Generation < 0) throw Denied();
            if (!native.IsDesktopAvailable()) throw Unavailable();
            var topology = ReadTopology();
            var monitors = options.Area switch
            {
                ScreenshotArea.VirtualDesktop => topology,
                ScreenshotArea.PrimaryMonitor => topology.Where(monitor => monitor.IsPrimary).ToArray(),
                ScreenshotArea.Monitor => topology.Where(monitor => monitor.Id == options.MonitorId).ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            };
            if (monitors.Length == 0) throw new ScreenCaptureException("MonitorUnavailable", "The requested monitor is no longer available.");
            var bounds = GetBounds(monitors);
            // Enumerating displays/DPI can take time; revalidate before reading any pixels.
            ct.ThrowIfCancellationRequested();
            var beforeCopy = authorize();
            if (!beforeCopy.Allowed) throw Denied();
            if (beforeCopy.Generation != permission.Generation) throw Stale();
            if (!native.IsDesktopAvailable()) throw Unavailable();
            var bitmap = native.CaptureBitmap(bounds, monitors);
            var capturedAt = DateTimeOffset.UtcNow;
            if (bitmap.PixelWidth != bounds.Width || bitmap.PixelHeight != bounds.Height)
                throw Stale();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            ct.ThrowIfCancellationRequested();
            if (!ReadTopology().SequenceEqual(topology)) throw Stale();
            if (!native.IsDesktopAvailable()) throw Unavailable();
            var current = authorize();
            if (!current.Allowed) throw Denied();
            if (current.Generation != permission.Generation) throw Stale();
            var result = new ScreenCapture
            {
                ImageBytes = stream.ToArray(), ContentType = "image/png", ImageWidth = bitmap.PixelWidth, ImageHeight = bitmap.PixelHeight,
                SourceBounds = bounds, Monitors = monitors, TimestampUtc = capturedAt, DesktopGeneration = permission.Generation
            };
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ScreenCaptureException) { throw; }
        catch (Exception exception) { throw new ScreenCaptureException("CaptureFailed", "Windows screen capture failed.", exception); }
    }
    private ScreenMonitor[] ReadTopology()
    {
        // Enumeration order is not identity; any actual topology/DPI change invalidates a frame.
        var monitors = native.GetMonitors().OrderBy(monitor => monitor.Id, StringComparer.Ordinal).ToArray();
        foreach (var monitor in monitors) monitor.Validate();
        if (monitors.Count(monitor => monitor.IsPrimary) != 1 || monitors.Select(monitor => monitor.Id).Distinct(StringComparer.Ordinal).Count() != monitors.Length)
            throw new ScreenCaptureException("CaptureFailed", "A consistent display topology was not available.");
        return monitors;
    }

    private static ScreenBounds GetBounds(ScreenMonitor[] monitors)
    {
        var left = monitors.Min(monitor => monitor.Bounds.X);
        var top = monitors.Min(monitor => monitor.Bounds.Y);
        var right = monitors.Max(monitor => (long)monitor.Bounds.X + monitor.Bounds.Width);
        var bottom = monitors.Max(monitor => (long)monitor.Bounds.Y + monitor.Bounds.Height);
        var bounds = new ScreenBounds(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
        bounds.Validate();
        return bounds;
    }

    private static ScreenCaptureException Denied() => new("CaptureDenied", "Screen capture is not authorized.");
    private static ScreenCaptureException Unavailable() => new("DesktopUnavailable", "An active unlocked default Windows desktop is required.");
    private static ScreenCaptureException Stale() => new("StaleDesktopContext", "Capture authorization or screen geometry changed; observe again.");
}

public sealed class ScreenCaptureException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal interface IWindowsCaptureNative
{
    bool IsDesktopAvailable();
    IReadOnlyList<ScreenMonitor> GetMonitors();
    BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors);
}
