using PersonalAgent.Core.Computer;

namespace PersonalAgent.UnitTests;

public sealed class CoordinatesTests
{
    [Theory]
    [InlineData(96, 0, 0)]
    [InlineData(120, 0, 0)]
    [InlineData(144, 0, 0)]
    [InlineData(96, -1920, -1080)]
    [InlineData(120, -1920, -1080)]
    [InlineData(144, -1920, -1080)]
    public void ResizedPixelsMapToPhysicalDesktopWithoutApplyingDpiTwice(double dpi, int x, int y)
    {
        var capture = Frame(new(x, y, 1920, 1080), 960, 540, dpi);
        Assert.Equal(new ScreenPoint(x + 201, y + 101), ScreenshotCoordinates.ToDesktop(capture, new(100, 50), 7));
        Assert.Equal(new ScreenPoint(x + 1, y + 1), ScreenshotCoordinates.ToDesktop(capture, new(0, 0), 7));
        Assert.Equal(new ScreenPoint(x + 1919, y + 1079), ScreenshotCoordinates.ToDesktop(capture, new(959, 539), 7));
        foreach (var point in new ScreenPoint[] { new(0, 0), new(100, 50), new(959, 539) })
            Assert.Equal(point, ScreenshotCoordinates.ToImage(capture, ScreenshotCoordinates.ToDesktop(capture, point, 7), 7));
    }

    [Fact]
    public void UnscaledPixelsMapExactlyIncludingNegativeOriginAndLastPixel()
    {
        var capture = Frame(new(-50, -30, 100, 60), 100, 60);
        Assert.Equal(new ScreenPoint(-50, -30), ScreenshotCoordinates.ToDesktop(capture, new(0, 0), 7));
        Assert.Equal(new ScreenPoint(49, 29), ScreenshotCoordinates.ToDesktop(capture, new(99, 59), 7));
        Assert.Equal(new ScreenPoint(99, 59), ScreenshotCoordinates.ToImage(capture, new(49, 29), 7));
    }

    [Fact]
    public void NonIntegralResizeAndUpscaleUsePixelCentersWithBoundedRoundTripError()
    {
        var reduced = Frame(new(-13, -9, 13, 9), 5, 4);
        for (var x = 0; x < 5; x++)
            for (var y = 0; y < 4; y++)
                Assert.Equal(new ScreenPoint(x, y), ScreenshotCoordinates.ToImage(reduced, ScreenshotCoordinates.ToDesktop(reduced, new(x, y), 7), 7));
        var enlarged = Frame(new(-3, -2, 3, 2), 9, 6);
        Assert.Equal(new ScreenPoint(-3, -2), ScreenshotCoordinates.ToDesktop(enlarged, new(0, 0), 7));
        Assert.Equal(new ScreenPoint(-1, -1), ScreenshotCoordinates.ToDesktop(enlarged, new(8, 5), 7));
        Assert.Equal(new ScreenPoint(1, 1), ScreenshotCoordinates.ToImage(enlarged, new(-3, -2), 7));
    }

    [Fact]
    public void MixedDpiDesktopRetainsGlobalPhysicalOriginAndRejectsGaps()
    {
        var capture = Frame(new(-300, -100, 500, 300), 250, 150) with
        {
            Monitors = [new("left", new(-300, -100, 200, 200), 144, 144, false), new("primary", new(0, 0, 200, 200), 96, 96, true)]
        };
        Assert.Equal(new ScreenPoint(-299, -99), ScreenshotCoordinates.ToDesktop(capture, new(0, 0), 7));
        Assert.Equal(new ScreenPoint(1, 1), ScreenshotCoordinates.ToDesktop(capture, new(150, 50), 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToDesktop(capture, new(125, 75), 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToImage(capture, new(-50, 0), 7));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(10, 0)]
    [InlineData(0, 5)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ImageCoordinatesOutsideHalfOpenBoundsAreRejected(int x, int y)
    {
        var capture = Frame(new(20, 30, 20, 10), 10, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToDesktop(capture, new(x, y), 7));
    }

    [Fact]
    public void StaleGenerationAndPhysicalCoordinatesOutsideCaptureAreRejected()
    {
        var capture = Frame(new(-10, -10, 10, 10), 5, 5);
        Assert.Throws<InvalidOperationException>(() => ScreenshotCoordinates.ToDesktop(capture, new(0, 0), 8));
        Assert.Throws<InvalidOperationException>(() => ScreenshotCoordinates.ToImage(capture, new(-1, -1), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToDesktop(capture, new(0, 0), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToImage(capture, new(0, -1), 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCoordinates.ToImage(capture, new(-11, -1), 7));
    }

    [Fact]
    public void ExtremeRepresentableGeometryUsesIntegerArithmeticWithoutOverflow()
    {
        var capture = Frame(new(int.MinValue, int.MaxValue, int.MaxValue, 1), int.MaxValue, 1);
        Assert.Equal(new ScreenPoint(int.MinValue, int.MaxValue), ScreenshotCoordinates.ToDesktop(capture, new(0, 0), 7));
        Assert.Equal(new ScreenPoint(-2, int.MaxValue), ScreenshotCoordinates.ToDesktop(capture, new(int.MaxValue - 1, 0), 7));
        Assert.Equal(new ScreenPoint(int.MaxValue - 1, 0), ScreenshotCoordinates.ToImage(capture, new(-2, int.MaxValue), 7));
    }

    private static ScreenCapture Frame(ScreenBounds bounds, int width, int height, double dpi = 96) => new()
    {
        // Geometry-only tests: encoded bytes are intentionally not decoded here.
        ImageBytes = [1], ContentType = "image/png", ImageWidth = width, ImageHeight = height,
        SourceBounds = bounds, Monitors = [new("display", bounds, dpi, dpi, true)],
        DesktopGeneration = 7, TimestampUtc = DateTimeOffset.UtcNow
    };
}
