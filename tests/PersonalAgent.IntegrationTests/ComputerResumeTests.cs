using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerResumeTests
{
    [Theory]
    [InlineData("click")] [InlineData("scroll")] [InlineData("type")] [InlineData("key")]
    public async Task EveryInputNeedsCurrentScreenshotAndTrustedReevaluation(string operation)
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        var lease = await desktop.AcquireAsync(Guid.NewGuid());
        var native = new NativeProbe();
        var controller = Controller(desktop, lease, native);
        var missing = await Assert.ThrowsAsync<ComputerInputException>(() => Input(controller, operation, 0));
        Assert.Equal("FreshScreenshotRequired", missing.Code); Assert.Equal(0, native.Captures);
        var frame = await controller.ScreenshotAsync(new());
        var unconfirmed = await Assert.ThrowsAsync<ComputerInputException>(() => Input(controller, operation, 0));
        Assert.Equal("ScreenReevaluationRequired", unconfirmed.Code); Assert.Empty(native.Inputs);
        Assert.Throws<ComputerInputException>(() => controller.ConfirmObservation(frame with { }));
        controller.ConfirmObservation(frame);
        await Input(controller, operation, 0);
        Assert.NotEmpty(native.Inputs); Assert.Equal(1, native.Captures);
    }

    [Fact]
    public async Task ChangedWindowsAfterResumeCannotUseOldControllerFrameOrCoordinates()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        var oldLease = await desktop.AcquireAsync(Guid.NewGuid());
        var native = new NativeProbe();
        var old = Controller(desktop, oldLease, native);
        var oldFrame = await old.ScreenshotAsync(new()); old.ConfirmObservation(oldFrame);
        await old.ClickAsync(new(0, 0), MouseButton.Left);
        var calls = native.Inputs.Count;
        desktop.TakeControl(); await desktop.ReleasePausedRunAsync(oldLease.RunId);
        desktop.ResumeRun(oldLease.RunId);
        var freshLease = await desktop.AcquireAsync(oldLease.RunId);
        var fresh = Controller(desktop, freshLease, native);
        native.Origin = 10; // User moved/changed the target display while paused.
        await Assert.ThrowsAsync<ComputerInputException>(async () => await old.ClickAsync(new(0, 0), MouseButton.Left));
        Assert.Throws<ComputerInputException>(() => fresh.ConfirmObservation(oldFrame));
        await Assert.ThrowsAsync<ComputerInputException>(async () => await fresh.ClickAsync(new(0, 0), MouseButton.Left));
        Assert.Equal(calls, native.Inputs.Count); Assert.Equal(1, native.Captures);
        var newFrame = await fresh.ScreenshotAsync(new());
        Assert.Equal(freshLease.Generation, newFrame.DesktopGeneration); Assert.Equal(10, newFrame.SourceBounds.X);
        fresh.ConfirmObservation(newFrame);
        await Assert.ThrowsAsync<ComputerInputException>(async () => await fresh.ClickAsync(new(0, 0), MouseButton.Left));
        Assert.Equal(calls, native.Inputs.Count);
        await fresh.ClickAsync(new(10, 0), MouseButton.Left);
        Assert.Equal(calls + 1, native.Inputs.Count); Assert.Equal(2, native.Captures);
        Assert.True(freshLease.Generation > oldLease.Generation);
    }

    [Fact]
    public async Task FailedRefreshInvalidatesPreviousConfirmationWithoutFallbackOrRetry()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        var lease = await desktop.AcquireAsync(Guid.NewGuid());
        var native = new NativeProbe(); var controller = Controller(desktop, lease, native);
        var old = await controller.ScreenshotAsync(new()); controller.ConfirmObservation(old);
        native.FailCapture = true;
        await Assert.ThrowsAsync<ScreenCaptureException>(() => controller.ScreenshotAsync(new()));
        Assert.Throws<ComputerInputException>(() => controller.ConfirmObservation(old));
        await Assert.ThrowsAsync<ComputerInputException>(async () => await controller.TypeAsync("must not start"));
        Assert.Empty(native.Inputs); Assert.Equal(2, native.Captures);
    }

    [Fact]
    public async Task ReturnedImageMetadataCannotMutatePrivateInputGeometry()
    {
        var desktop = new DesktopAccessCoordinator(_ => true);
        var lease = await desktop.AcquireAsync(Guid.NewGuid());
        var native = new NativeProbe(); var controller = Controller(desktop, lease, native);
        var frame = await controller.ScreenshotAsync(new());
        ((ScreenMonitor[])frame.Monitors)[0] = new("forged", new(1000, 1000, 10, 10), 192, 192, true);
        Array.Clear(frame.ImageBytes);
        controller.ConfirmObservation(frame);
        await controller.ClickAsync(new(0, 0), MouseButton.Left);
        Assert.Single(native.Inputs);
    }

    private static WindowsComputerController Controller(IDesktopAccessCoordinator desktop, DesktopLease lease, NativeProbe native) =>
        new(desktop, lease, native, native, native.GetMonitors, _ => true);
    private static async Task Input(IComputerController controller, string operation, int x)
    {
        if (operation == "click") await controller.ClickAsync(new(x, 0), MouseButton.Left);
        else if (operation == "scroll") await controller.ScrollAsync(new(x, 0), ScrollDirection.Down, 1);
        else if (operation == "type") await controller.TypeAsync("a");
        else await controller.PressKeyAsync(new("Ctrl+A"));
    }
    private sealed class NativeProbe : IWindowsCaptureNative, IWindowsKeyboardNative
    {
        public int Origin, Captures;
        public bool FailCapture;
        public readonly List<NativeInput[]> Inputs = [];
        public bool IsDesktopAvailable() => true;
        public bool IsKeyDown(ushort key) => false;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => [new("primary", new(Origin, 0, 4, 3), 96, 96, true)];
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        {
            Captures++;
            if (FailCapture) throw new InvalidOperationException("synthetic capture failure");
            return BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[48], 16);
        }
        public InputInsertion Send(NativeInput[] inputs) { Inputs.Add(inputs.ToArray()); return new((uint)inputs.Length, 0); }
    }
}
