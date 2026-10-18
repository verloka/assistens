using PersonalAgent.Core.Computer;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class ScreenContractTests
{
    [Fact]
    public void ScreenContractsPreserveNegativePhysicalOriginMixedDpiAndResizedImageGeometry()
    {
        var source = new ScreenBounds(-1920, -200, 3840, 1440);
        var capture = new ScreenCapture
        {
            ImageBytes = [1, 2, 3], ContentType = "image/png", ImageWidth = 1920, ImageHeight = 720,
            SourceBounds = source, DesktopGeneration = 7, TimestampUtc = DateTimeOffset.UtcNow,
            Monitors = [new("left", new(-1920, -200, 1920, 1080), 96, 96, false), new("primary", new(0, 0, 1920, 1240), 144, 144, true)]
        };
        // DTO validation checks the contract, not image decoding or physical display hardware.
        capture.Validate();
        var restored = JsonSerializer.Deserialize<ScreenCapture>(JsonSerializer.Serialize(capture))!;
        restored.Validate();
        Assert.Equal(source, restored.SourceBounds);
        Assert.Equal(1920, restored.ImageWidth);
        Assert.Equal(7, restored.DesktopGeneration);
        Assert.Equal(capture.Monitors, restored.Monitors);
        Assert.Equal(capture.ImageBytes, restored.ImageBytes);
        Assert.Equal(new ScreenPoint(-1919, -199), JsonSerializer.Deserialize<ScreenPoint>(JsonSerializer.Serialize(new ScreenPoint(-1919, -199))));
    }

    [Fact]
    public void ScreenBoundsUseHalfOpenPhysicalPixelsWithoutOverflow()
    {
        var bounds = new ScreenBounds(-1920, -1080, 1920, 1080);
        bounds.Validate();
        Assert.True(bounds.Contains(new(-1920, -1080)));
        Assert.True(bounds.Contains(new(-1, -1)));
        Assert.False(bounds.Contains(new(0, -1)));
        Assert.False(bounds.Contains(new(-1921, -1)));
        Assert.False(bounds.Contains(new(-1, 0)));
        new ScreenBounds(int.MaxValue, int.MaxValue, 1, 1).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenBounds(int.MaxValue, 0, 2, 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenBounds(0, 0, 0, 1).Validate());
    }

    [Fact]
    public void ScreenshotOptionsRequireUnambiguousSelectionAndRejectUnknownArea()
    {
        new ScreenshotOptions().Validate();
        new ScreenshotOptions(ScreenshotArea.VirtualDesktop).Validate();
        new ScreenshotOptions(ScreenshotArea.Monitor, "display-2").Validate();
        Assert.ThrowsAny<ArgumentException>(() => new ScreenshotOptions(ScreenshotArea.Monitor).Validate());
        Assert.Throws<ArgumentException>(() => new ScreenshotOptions(MonitorId: "display-2").Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenshotOptions((ScreenshotArea)999).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenMonitor("display", new(0, 0, 10, 10), double.NaN, 96, true).Validate());
    }
}
