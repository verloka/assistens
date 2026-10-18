namespace PersonalAgent.Core.Computer;

// Physical desktop pixels, never WPF device-independent units or model image pixels.
public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct ScreenBounds(int X, int Y, int Width, int Height)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || (long)X + Width - 1 > int.MaxValue || (long)Y + Height - 1 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Width), "Bounds require positive dimensions and representable physical pixels.");
    }
    // Half-open bounds: the last valid pixel is (X + Width - 1, Y + Height - 1).
    public bool Contains(ScreenPoint point) => Width > 0 && Height > 0 && point.X >= X && point.Y >= Y &&
        (long)point.X < (long)X + Width && (long)point.Y < (long)Y + Height;
}

public sealed record ScreenMonitor(string Id, ScreenBounds Bounds, double DpiX, double DpiY, bool IsPrimary)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        Bounds.Validate();
        if (!double.IsFinite(DpiX) || !double.IsFinite(DpiY) || DpiX <= 0 || DpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(DpiX));
    }
}

public enum ScreenshotArea { PrimaryMonitor, VirtualDesktop, Monitor }
public sealed record ScreenshotOptions(ScreenshotArea Area = ScreenshotArea.PrimaryMonitor, string? MonitorId = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Area)) throw new ArgumentOutOfRangeException(nameof(Area));
        if (Area == ScreenshotArea.Monitor) ArgumentException.ThrowIfNullOrWhiteSpace(MonitorId);
        else if (MonitorId is not null) throw new ArgumentException("A monitor ID is valid only for a selected monitor capture.");
    }
}

public sealed record ScreenCapture
{
    public required byte[] ImageBytes { get; init; }
    public required string ContentType { get; init; }
    public required int ImageWidth { get; init; }
    public required int ImageHeight { get; init; }
    // Original physical capture area remains unchanged if the encoded image is resized.
    public required ScreenBounds SourceBounds { get; init; }
    // Per-monitor DPI avoids pretending mixed-DPI desktops have one global scale.
    public required IReadOnlyList<ScreenMonitor> Monitors { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required long DesktopGeneration { get; init; }

    public void Validate()
    {
        SourceBounds.Validate();
        if (ImageBytes.Length == 0 || ImageWidth <= 0 || ImageHeight <= 0) throw new ArgumentException("Capture requires encoded image data and positive image dimensions.");
        if (ContentType is not ("image/png" or "image/jpeg")) throw new ArgumentException("Unsupported screenshot content type.");
        if (DesktopGeneration < 0 || TimestampUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Capture requires a valid generation and UTC timestamp.");
        if (Monitors.Count == 0) throw new ArgumentException("Capture requires monitor geometry/DPI metadata.");
        foreach (var monitor in Monitors) monitor.Validate();
    }
}
