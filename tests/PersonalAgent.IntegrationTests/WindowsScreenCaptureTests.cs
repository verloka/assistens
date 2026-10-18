using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class WindowsScreenCaptureTests
{
    [Fact]
    public async Task CaptureDeniesWithoutCallerPolicyAndNeverTouchesNativeDesktop()
    {
        var native = new CaptureProbe();
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(null, native).CaptureAsync(new()));
        Assert.Equal("CaptureDenied", error.Code);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public async Task CaptureRequiresUnlockedDesktopAndDoesNotRetryNativeFailure()
    {
        var unavailable = new CaptureProbe { Available = false };
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (true, 1), unavailable).CaptureAsync(new()));
        Assert.Equal("DesktopUnavailable", error.Code);
        Assert.Equal(0, unavailable.Captures);
        var failure = new CaptureProbe { OnCapture = () => throw new Win32Exception(5) };
        error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (true, 1), failure).CaptureAsync(new()));
        Assert.Equal("CaptureFailed", error.Code);
        Assert.Equal(1, failure.Captures);
    }

    [Fact]
    public async Task CaptureEncodesActualPngAndPreservesPhysicalGeometryAndDpiWithoutScreenAccess()
    {
        var frame = await new WindowsScreenCapture(() => (true, 7), new CaptureProbe()).CaptureAsync(new());
        frame.Validate();
        Assert.Equal("image/png", frame.ContentType);
        Assert.Equal(7, frame.DesktopGeneration);
        Assert.Equal(new ScreenBounds(0, 0, 2, 2), frame.SourceBounds);
        Assert.Equal(120, frame.Monitors[0].DpiX);
        using var stream = new MemoryStream(frame.ImageBytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(2, decoded.PixelWidth);
        Assert.Equal(2, decoded.PixelHeight);
        var rgba = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[16];
        rgba.CopyPixels(pixels, 8, 0);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels[..4]);
    }

    [Theory]
    [InlineData("revoke", "CaptureDenied")]
    [InlineData("generation", "StaleDesktopContext")]
    [InlineData("lock", "DesktopUnavailable")]
    [InlineData("geometry", "StaleDesktopContext")]
    public async Task CaptureDiscardsFrameIfContextChangesDuringCapture(string change, string expected)
    {
        var allowed = true;
        long generation = 1;
        var native = new CaptureProbe();
        native.OnCapture = () =>
        {
            if (change == "revoke") allowed = false;
            if (change == "generation") generation++;
            if (change == "lock") native.Available = false;
            if (change == "geometry") native.Monitor = native.Monitor with { DpiX = 144, DpiY = 144 };
        };
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (allowed, generation), native).CaptureAsync(new()));
        Assert.Equal(expected, error.Code);
        Assert.Equal(1, native.Captures);
    }

    [Fact]
    public async Task CaptureHonorsPreCancellationAndRejectsInvalidSelectionBeforeNativeCalls()
    {
        var native = new CaptureProbe();
        var capture = new WindowsScreenCapture(() => (true, 1), native);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(new(), cancel.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => capture.CaptureAsync(new(ScreenshotArea.Monitor)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => capture.CaptureAsync(new((ScreenshotArea)999)));
        Assert.Equal(0, native.Calls);
    }

    [Theory]
    [InlineData(ScreenshotArea.VirtualDesktop, null, -3, -1, 5, 3, 2)]
    [InlineData(ScreenshotArea.PrimaryMonitor, null, 0, 0, 2, 2, 1)]
    [InlineData(ScreenshotArea.Monitor, "left", -3, -1, 2, 2, 1)]
    public async Task CaptureSelectsPhysicalBoundsAndPreservesMixedDpiMetadata(
        ScreenshotArea area, string? id, int x, int y, int width, int height, int monitorCount)
    {
        var native = TwoDisplays();
        var frame = await new WindowsScreenCapture(() => (true, 9), native).CaptureAsync(new(area, id));
        var expectedBounds = new ScreenBounds(x, y, width, height);
        Assert.Equal(expectedBounds, frame.SourceBounds);
        Assert.Equal(expectedBounds, native.RequestedBounds);
        Assert.Equal(monitorCount, frame.Monitors.Count);
        Assert.Equal(frame.Monitors, native.RequestedMonitors);
        foreach (var monitor in frame.Monitors)
            Assert.Equal(monitor.Id == "left" ? 144 : 120, monitor.DpiX);
        using var stream = new MemoryStream(frame.ImageBytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(width, decoded.PixelWidth);
        Assert.Equal(height, decoded.PixelHeight);
        Assert.Equal(1, native.Captures);
    }

    [Fact]
    public async Task CaptureDoesNotTreatEnumerationOrderAsDisplayChange()
    {
        var native = TwoDisplays();
        native.OnCapture = () => Array.Reverse(native.Monitors);
        var frame = await new WindowsScreenCapture(() => (true, 1), native).CaptureAsync(new(ScreenshotArea.VirtualDesktop));
        Assert.Equal(2, frame.Monitors.Count);
        Assert.Equal(new ScreenBounds(-3, -1, 5, 3), frame.SourceBounds);
    }

    [Theory]
    [InlineData("unplug")]
    [InlineData("move")]
    [InlineData("dpi")]
    [InlineData("primary")]
    [InlineData("plug")]
    public async Task CaptureDiscardsFrameOnSecondaryTopologyChangeEvenForPrimaryCapture(string change)
    {
        var native = TwoDisplays();
        native.OnCapture = () =>
        {
            switch (change)
            {
                case "unplug": native.Monitors = [native.Monitor]; break;
                case "move": native.Monitors[1] = native.Monitors[1] with { Bounds = new(-4, -1, 2, 2) }; break;
                case "dpi": native.Monitors[1] = native.Monitors[1] with { DpiX = 96, DpiY = 96 }; break;
                case "primary": native.Monitors = native.Monitors.Select(monitor => monitor with { IsPrimary = !monitor.IsPrimary }).ToArray(); break;
                case "plug": native.Monitors = [.. native.Monitors, new("third", new(2, 0, 2, 2), 96, 96, false)]; break;
            }
        };
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (true, 1), native).CaptureAsync(new()));
        Assert.Equal("StaleDesktopContext", error.Code);
        Assert.Equal(1, native.Captures);
    }

    [Fact]
    public async Task MissingSelectedMonitorIsRejectedWithoutFallbackOrPixelCapture()
    {
        var native = TwoDisplays();
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (true, 1), native).CaptureAsync(new(ScreenshotArea.Monitor, "removed")));
        Assert.Equal("MonitorUnavailable", error.Code);
        Assert.Equal(0, native.Captures);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("no-primary")]
    [InlineData("two-primary")]
    [InlineData("overflow")]
    public async Task InconsistentTopologyIsRejectedBeforeAllocatingBitmap(string issue)
    {
        var native = TwoDisplays();
        native.Monitors = issue switch
        {
            "empty" => [],
            "duplicate" => [native.Monitor, native.Monitor],
            "no-primary" => [native.Monitor with { IsPrimary = false }],
            "two-primary" => [native.Monitor, native.Monitors[1] with { IsPrimary = true }],
            "overflow" => [native.Monitor, new("distant", new(int.MinValue, 0, 2, 2), 96, 96, false)],
            _ => throw new ArgumentOutOfRangeException(nameof(issue))
        };
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(() => (true, 1), native).CaptureAsync(new(ScreenshotArea.VirtualDesktop)));
        Assert.Equal("CaptureFailed", error.Code);
        Assert.Equal(0, native.Captures);
    }

    [Fact]
    public async Task VirtualCaptureStillRequiresPolicyAndHonorsCancellationDuringNativeCopy()
    {
        var native = TwoDisplays();
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(null, native).CaptureAsync(new(ScreenshotArea.VirtualDesktop)));
        Assert.Equal("CaptureDenied", error.Code);
        Assert.Equal(0, native.Calls);
        using var cancel = new CancellationTokenSource();
        native.OnCapture = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsScreenCapture(() => (true, 1), native).CaptureAsync(new(ScreenshotArea.VirtualDesktop), cancel.Token));
        Assert.Equal(1, native.Captures);
    }

    private static CaptureProbe TwoDisplays() => new()
    {
        Monitors = [new("primary", new(0, 0, 2, 2), 120, 120, true), new("left", new(-3, -1, 2, 2), 144, 144, false)]
    };

    [Theory]
    [InlineData("revoke", "CaptureDenied")]
    [InlineData("generation", "StaleDesktopContext")]
    [InlineData("lock", "DesktopUnavailable")]
    [InlineData("cancel", null)]
    public async Task CaptureRechecksContextAfterTopologyEnumerationBeforeReadingPixels(string change, string? code)
    {
        var native = TwoDisplays();
        var allowed = true;
        long generation = 1;
        using var cancel = new CancellationTokenSource();
        native.OnTopologyRead = () =>
        {
            if (change == "revoke") allowed = false;
            if (change == "generation") generation++;
            if (change == "lock") native.Available = false;
            if (change == "cancel") cancel.Cancel();
        };
        var capture = new WindowsScreenCapture(() => (allowed, generation), native);
        if (code is null)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(new(ScreenshotArea.VirtualDesktop), cancel.Token));
        else
            Assert.Equal(code, (await Assert.ThrowsAsync<ScreenCaptureException>(() => capture.CaptureAsync(new(ScreenshotArea.VirtualDesktop), cancel.Token))).Code);
        Assert.Equal(0, native.Captures);
    }

    private sealed class CaptureProbe : IWindowsCaptureNative
    {
        public bool Available = true;
        public int Calls, Captures;
        public Action? OnCapture;
        public Action? OnTopologyRead;
        public ScreenMonitor[] Monitors = [new("primary", new(0, 0, 2, 2), 120, 120, true)];
        public ScreenMonitor Monitor { get => Monitors[0]; set => Monitors[0] = value; }
        public ScreenBounds? RequestedBounds;
        public IReadOnlyList<ScreenMonitor>? RequestedMonitors;
        public bool IsDesktopAvailable() { Calls++; return Available; }
        public IReadOnlyList<ScreenMonitor> GetMonitors() { Calls++; OnTopologyRead?.Invoke(); return Monitors; }
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        {
            RequestedBounds = bounds; RequestedMonitors = monitors;
            Calls++; Captures++; OnCapture?.Invoke();
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
                new byte[] { 10, 20, 30, 255 }.CopyTo(pixels, offset);
            var bitmap = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, pixels, bounds.Width * 4);
            bitmap.Freeze(); return bitmap;
        }
    }
}
