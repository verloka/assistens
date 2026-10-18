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
    public async Task CaptureHonorsPreCancellationAndRejectsUnimplementedAreaBeforeNativeCalls()
    {
        var native = new CaptureProbe();
        var capture = new WindowsScreenCapture(() => (true, 1), native);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(new(), cancel.Token));
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => capture.CaptureAsync(new(ScreenshotArea.VirtualDesktop)));
        Assert.Equal("CaptureAreaUnsupported", error.Code);
        Assert.Equal(0, native.Calls);
    }

    private sealed class CaptureProbe : IWindowsCaptureNative
    {
        public bool Available = true;
        public int Calls, Captures;
        public Action? OnCapture;
        public ScreenMonitor Monitor = new("primary", new(0, 0, 2, 2), 120, 120, true);
        public bool IsDesktopAvailable() { Calls++; return Available; }
        public ScreenMonitor GetPrimaryMonitor() { Calls++; return Monitor; }
        public BitmapSource CaptureBitmap(ScreenBounds bounds)
        {
            Calls++; Captures++; OnCapture?.Invoke();
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255 }, 8);
            bitmap.Freeze(); return bitmap;
        }
    }
}
