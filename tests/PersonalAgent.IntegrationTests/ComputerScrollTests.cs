using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerScrollTests
{
    [Theory]
    [InlineData(ScrollDirection.Up, 0x800u, 120)]
    [InlineData(ScrollDirection.Down, 0x800u, -120)]
    [InlineData(ScrollDirection.Right, 0x1000u, 120)]
    [InlineData(ScrollDirection.Left, 0x1000u, -120)]
    public async Task EachDirectionUsesSignedWheelDeltaAndPhysicalAnchor(ScrollDirection direction, uint flags, int delta)
    {
        var native = new ScrollProbe();
        var frame = ScreenshotFixture.Create();
        await new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors).ScrollAsync(frame, new(-3, -2), direction, 1);
        var batch = Assert.Single(native.Batches);
        Assert.Equal(2, batch.Length);
        Assert.Equal(0xc001u, batch[0].Data.Mouse.Flags);
        Assert.Equal(-3, (int)((long)batch[0].Data.Mouse.X * 4 / 65536) - 4);
        Assert.Equal(-2, (int)((long)batch[0].Data.Mouse.Y * 3 / 65536) - 3);
        Assert.Equal(flags, batch[1].Data.Mouse.Flags);
        Assert.Equal(delta, unchecked((int)batch[1].Data.Mouse.Data));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task AllowedAmountIsExactlyOneBoundedWheelEvent(int amount)
    {
        var native = new ScrollProbe();
        var frame = ScreenshotFixture.Create();
        await new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors).ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, amount);
        var batch = Assert.Single(native.Batches);
        Assert.Equal(2, batch.Length);
        Assert.Equal(-120 * amount, unchecked((int)batch[1].Data.Mouse.Data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public async Task InvalidAmountRejectsBeforeDesktopOrInput(int amount)
    {
        var native = new ScrollProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.ScrollAsync(ScreenshotFixture.Create(), new(-3, -2), ScrollDirection.Up, amount));
        Assert.Equal(0, native.Checks);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task InvalidDirectionAndOutsideAnchorCannotReachNative()
    {
        var native = new ScrollProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.ScrollAsync(ScreenshotFixture.Create(), new(-3, -2), (ScrollDirection)99, 1));
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ScrollAsync(ScreenshotFixture.Create(), new(0, -2), ScrollDirection.Up, 1));
        Assert.Equal("InvalidCoordinates", error.Code);
        Assert.Equal(0, native.Checks);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData("denied", "InputDenied")]
    [InlineData("locked", "DesktopUnavailable")]
    [InlineData("generation", "StaleDesktopContext")]
    [InlineData("dpi", "StaleDesktopContext")]
    [InlineData("revoke-after-geometry", "InputDenied")]
    public async Task ScrollSharesCurrentAuthorizationAndGeometryGate(string change, string code)
    {
        var allowed = change != "denied";
        var native = new ScrollProbe { Available = change != "locked" };
        var frame = ScreenshotFixture.Create();
        var driver = new WindowsMouseInput(() => (allowed, change == "generation" ? 2 : 1), native, () =>
        {
            if (change == "revoke-after-geometry") allowed = false;
            return change == "dpi" ? [frame.Monitors[0] with { DpiY = 96 }] : frame.Monitors;
        });
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1));
        Assert.Equal(code, error.Code);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task PartialMoveOrFailedScrollIsNeverReplayed(uint inserted)
    {
        var native = new ScrollProbe { Inserted = inserted };
        var frame = ScreenshotFixture.Create();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1));
        Assert.Equal(inserted, error.Inserted);
        Assert.True(error.CleanupSucceeded); // Wheel/move hold no key/button.
        error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Single(native.Batches);
    }

    [Fact]
    public async Task CancellationCannotStartScroll()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var native = new ScrollProbe();
        var frame = ScreenshotFixture.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors).ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1, cts.Token));
        Assert.Empty(native.Batches);
        Assert.Equal(0, native.Checks);
    }

    private sealed class ScrollProbe : IWindowsInputNative
    {
        public bool Available = true;
        public int Checks;
        public uint? Inserted;
        public List<NativeInput[]> Batches = [];
        public bool IsDesktopAvailable() { Checks++; return Available; }
        public InputInsertion Send(NativeInput[] inputs)
        { Batches.Add(inputs.ToArray()); return new(Inserted ?? (uint)inputs.Length, 0); }
    }
}
