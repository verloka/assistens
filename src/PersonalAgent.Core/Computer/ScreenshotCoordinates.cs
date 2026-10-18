namespace PersonalAgent.Core.Computer;

// Both coordinate spaces use pixel indices. A pixel maps through its center;
// physical bounds already account for DPI, so no additional DPI multiplier applies.
public static class ScreenshotCoordinates
{
    public static ScreenPoint ToDesktop(ScreenCapture capture, ScreenPoint imagePixel, long currentGeneration)
    {
        ValidateContext(capture, currentGeneration);
        if (imagePixel.X < 0 || imagePixel.Y < 0 || imagePixel.X >= capture.ImageWidth || imagePixel.Y >= capture.ImageHeight)
            throw new ArgumentOutOfRangeException(nameof(imagePixel), "Point is outside the encoded image.");
        var bounds = capture.SourceBounds;
        var point = new ScreenPoint(
            checked((int)((long)bounds.X + MapPixel(imagePixel.X, capture.ImageWidth, bounds.Width))),
            checked((int)((long)bounds.Y + MapPixel(imagePixel.Y, capture.ImageHeight, bounds.Height))));
        ValidateDesktopPoint(capture, point);
        return point;
    }

    public static ScreenPoint ToImage(ScreenCapture capture, ScreenPoint desktopPixel, long currentGeneration)
    {
        ValidateContext(capture, currentGeneration);
        ValidateDesktopPoint(capture, desktopPixel);
        var bounds = capture.SourceBounds;
        return new(
            MapPixel(checked((int)((long)desktopPixel.X - bounds.X)), bounds.Width, capture.ImageWidth),
            MapPixel(checked((int)((long)desktopPixel.Y - bounds.Y)), bounds.Height, capture.ImageHeight));
    }

    private static int MapPixel(int value, int fromLength, int toLength) =>
        checked((int)(((2L * value + 1) * toLength) / (2L * fromLength)));

    private static void ValidateContext(ScreenCapture capture, long currentGeneration)
    {
        capture.Validate();
        if (currentGeneration < 0) throw new ArgumentOutOfRangeException(nameof(currentGeneration));
        if (capture.DesktopGeneration != currentGeneration)
            throw new InvalidOperationException("Screenshot belongs to a stale desktop generation; capture again.");
    }

    private static void ValidateDesktopPoint(ScreenCapture capture, ScreenPoint point)
    {
        if (!capture.SourceBounds.Contains(point) || !capture.Monitors.Any(monitor => monitor.Bounds.Contains(point)))
            throw new ArgumentOutOfRangeException(nameof(point), "Point is outside captured displays or in a gap between them.");
    }
}
