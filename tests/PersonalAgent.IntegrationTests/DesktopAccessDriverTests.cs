using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class DesktopAccessDriverTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevokeBetweenPreflightAndNativeEntryCannotSlipThrough(bool capture)
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        var race = new RevokeAtEntryAccess(coordinator);
        if (capture)
        {
            var native = new CaptureProbe();
            var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => new WindowsScreenCapture(race, lease, native).CaptureAsync(new()));
            Assert.Equal("StaleDesktopContext", error.Code);
            Assert.Equal(0, native.Captures);
        }
        else
        {
            var native = new InputProbe();
            var frame = ScreenshotFixture.Create(lease.Generation);
            var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
                new WindowsMouseInput(race, lease, native, () => frame.Monitors).ClickAsync(frame, new(-3, -2), MouseButton.Left));
            Assert.Equal("StaleDesktopContext", error.Code);
            Assert.Empty(native.Batches);
        }
    }

    [Theory]
    [InlineData("click")] [InlineData("scroll")] [InlineData("type")] [InlineData("key")]
    public async Task RevokedGrantCannotExecuteAnyInputPath(string operation)
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        var native = new InputProbe();
        var frame = ScreenshotFixture.Create(lease.Generation);
        var mouse = new WindowsMouseInput(coordinator, lease, native, () => frame.Monitors);
        var keyboard = new WindowsKeyboardInput(coordinator, lease, native, _ => true);
        await coordinator.RevokeAsync(lease);
        await Assert.ThrowsAsync<ComputerInputException>(async () =>
        {
            if (operation == "click") await mouse.ClickAsync(frame, new(-3, -2), MouseButton.Left);
            else if (operation == "scroll") await mouse.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1);
            else if (operation == "type") await keyboard.TypeAsync("forbidden", lease.Generation);
            else await keyboard.PressKeyAsync(new("Ctrl+A"), lease.Generation);
        });
        Assert.Empty(native.Batches);
        Assert.Equal(0, native.DesktopChecks);
    }

    [Fact]
    public async Task PauseDuringAtomicPartialInputWaitsUntilReleaseCleanupFinishes()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        Task? pause = null;
        var native = new InputProbe();
        native.OnSend = inputs =>
        {
            if (native.Batches.Count == 1)
            {
                pause = coordinator.RevokeAsync(lease);
                Assert.False(pause.IsCompleted);
                Assert.False(coordinator.IsCurrent(lease));
                return new(1, 0);
            }
            Assert.NotNull(pause);
            Assert.False(pause.IsCompleted);
            Assert.Equal(2u, Assert.Single(inputs).Data.Keyboard.Flags); // Own Ctrl release only.
            return new((uint)inputs.Length, 0);
        };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsKeyboardInput(coordinator, lease, native, _ => true).PressKeyAsync(new("Ctrl+A"), lease.Generation));
        Assert.True(error.CleanupSucceeded);
        Assert.NotNull(pause);
        await pause.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await coordinator.ReleaseAsync(lease));
        Assert.Equal(2, native.Batches.Count);
    }

    [Fact]
    public async Task PausingAfterFirstTextChunkBlocksAllLaterChunks()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        Task? pause = null;
        var native = new InputProbe();
        native.OnSend = inputs => { pause = coordinator.RevokeAsync(lease); return new((uint)inputs.Length, 0); };
        await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsKeyboardInput(coordinator, lease, native).TypeAsync(new string('x', 40), lease.Generation));
        Assert.Single(native.Batches);
        Assert.NotNull(pause);
        await pause;
        Assert.Equal(32, native.Batches[0].Length);
    }

    [Fact]
    public async Task CaptureRechecksRevocationAndDoesNotReturnPausedFrame()
    {
        var coordinator = new DesktopAccessCoordinator(_ => true);
        var lease = await coordinator.AcquireAsync(Guid.NewGuid());
        var native = new CaptureProbe();
        Task? pause = null;
        native.OnCapture = () => { pause = coordinator.RevokeAsync(lease); Assert.False(pause.IsCompleted); };
        var driver = new WindowsScreenCapture(coordinator, lease, native);
        var error = await Assert.ThrowsAsync<ScreenCaptureException>(() => driver.CaptureAsync(new()));
        Assert.Equal("CaptureDenied", error.Code);
        Assert.NotNull(pause);
        await pause;
        Assert.Equal(1, native.Captures);
        await Assert.ThrowsAsync<ScreenCaptureException>(() => driver.CaptureAsync(new()));
        Assert.Equal(1, native.Captures);
    }

    private sealed class InputProbe : IWindowsKeyboardNative
    {
        public int DesktopChecks;
        public List<NativeInput[]> Batches = [];
        public Func<NativeInput[], InputInsertion>? OnSend;
        public bool IsKeyDown(ushort key) => false;
        public bool IsDesktopAvailable() { DesktopChecks++; return true; }
        public InputInsertion Send(NativeInput[] inputs)
        { Batches.Add(inputs.ToArray()); return OnSend?.Invoke(inputs) ?? new((uint)inputs.Length, 0); }
    }
    private sealed class RevokeAtEntryAccess(IDesktopAccessCoordinator inner) : IDesktopAccessCoordinator
    {
        public DesktopAccessSnapshot Snapshot => inner.Snapshot;
        public Task<DesktopLease> AcquireAsync(Guid runId, CancellationToken ct = default) => inner.AcquireAsync(runId, ct);
        public bool IsCurrent(DesktopLease lease) => inner.IsCurrent(lease);
        public IDisposable EnterAtomic(DesktopLease lease) { _ = inner.RevokeAsync(lease); return inner.EnterAtomic(lease); }
        public Task RevokeAsync(DesktopLease lease) => inner.RevokeAsync(lease);
        public Task PauseRunAsync(Guid runId) => inner.PauseRunAsync(runId);
        public void ResumeRun(Guid runId) => inner.ResumeRun(runId);
        public Task CancelRunAsync(Guid runId) => inner.CancelRunAsync(runId);
        public Guid? TakeControl() => inner.TakeControl();
        public Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default) => inner.ReleaseAsync(lease, ct);
    }
    private sealed class CaptureProbe : IWindowsCaptureNative
    {
        public Action? OnCapture;
        public int Captures;
        public bool IsDesktopAvailable() => true;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => [new("primary", new(0, 0, 4, 3), 96, 96, true)];
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        {
            Captures++; OnCapture?.Invoke();
            return BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 16);
        }
    }
}
