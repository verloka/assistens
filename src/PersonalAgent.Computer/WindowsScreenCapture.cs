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
        if (options.Area != ScreenshotArea.PrimaryMonitor) throw new ScreenCaptureException("CaptureAreaUnsupported", "Only primary monitor capture is implemented.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CaptureCore(cancellationToken), cancellationToken);
    }

    private ScreenCapture CaptureCore(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var permission = authorize();
            if (!permission.Allowed || permission.Generation < 0) throw Denied();
            if (!native.IsDesktopAvailable()) throw Unavailable();
            var monitor = native.GetPrimaryMonitor();
            monitor.Validate();
            if (!monitor.IsPrimary) throw new ScreenCaptureException("CaptureFailed", "Primary monitor geometry was not available.");
            var bitmap = native.CaptureBitmap(monitor.Bounds);
            var capturedAt = DateTimeOffset.UtcNow;
            if (bitmap.PixelWidth != monitor.Bounds.Width || bitmap.PixelHeight != monitor.Bounds.Height)
                throw Stale();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            ct.ThrowIfCancellationRequested();
            if (native.GetPrimaryMonitor() != monitor) throw Stale();
            if (!native.IsDesktopAvailable()) throw Unavailable();
            var current = authorize();
            if (!current.Allowed) throw Denied();
            if (current.Generation != permission.Generation) throw Stale();
            var result = new ScreenCapture
            {
                ImageBytes = stream.ToArray(), ContentType = "image/png", ImageWidth = bitmap.PixelWidth, ImageHeight = bitmap.PixelHeight,
                SourceBounds = monitor.Bounds, Monitors = [monitor], TimestampUtc = capturedAt, DesktopGeneration = permission.Generation
            };
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ScreenCaptureException) { throw; }
        catch (Exception exception) { throw new ScreenCaptureException("CaptureFailed", "Windows screen capture failed.", exception); }
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
    ScreenMonitor GetPrimaryMonitor();
    BitmapSource CaptureBitmap(ScreenBounds bounds);
}
