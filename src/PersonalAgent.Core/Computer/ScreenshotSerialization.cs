namespace PersonalAgent.Core.Computer;

public enum ScreenshotImageFormat { Png, Jpeg }

public sealed record ScreenshotImageOptions
{
    public ScreenshotImageFormat Format { get; init; } = ScreenshotImageFormat.Png;
    public int MaxWidth { get; init; } = 4096;
    public int MaxHeight { get; init; } = 4096;
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxInputBytes { get; init; } = 32 * 1024 * 1024;
    public long MaxInputPixels { get; init; } = 32_000_000;
    // Preserve small UI text by default. Oversize frames fail unless resizing is explicitly enabled.
    public bool ResizeToFit { get; init; }
    public int JpegQuality { get; init; } = 90;

    public void Validate()
    {
        if (!Enum.IsDefined(Format)) throw new ArgumentOutOfRangeException(nameof(Format));
        if (MaxWidth <= 0 || MaxHeight <= 0 || MaxBytes <= 0 || MaxInputBytes <= 0 || MaxInputPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), "Image limits must be positive.");
        if (JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
    }
}

public interface IScreenshotSerializer
{
    // Produces new encoded bytes/dimensions; original physical geometry/time/generation stay unchanged.
    Task<ScreenCapture> SerializeAsync(ScreenCapture capture, ScreenshotImageOptions options, CancellationToken cancellationToken = default);
}

public sealed class ScreenshotSerializationException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
