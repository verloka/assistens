using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerClickTests
{
    [Theory]
    [InlineData(MouseButton.Left, 2u)]
    [InlineData(MouseButton.Right, 8u)]
    [InlineData(MouseButton.Middle, 32u)]
    public async Task ClickMovesToExactPhysicalPixelThenBalancesRequestedButton(MouseButton button, uint down)
    {
        var frame = ScreenshotFixture.Create();
        var native = new ClickProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        await driver.ClickAsync(frame, new(-3, -2), button);
        var batch = Assert.Single(native.Batches);
        Assert.Equal(3, batch.Length);
        Assert.Equal(0xc001u, batch[0].Data.Mouse.Flags); // ABSOLUTE | VIRTUALDESK | MOVE
        Assert.Equal(-3, (int)((long)batch[0].Data.Mouse.X * 4 / 65536) - 4);
        Assert.Equal(-2, (int)((long)batch[0].Data.Mouse.Y * 3 / 65536) - 3);
        Assert.Equal(down, batch[1].Data.Mouse.Flags);
        Assert.Equal(down << 1, batch[2].Data.Mouse.Flags);
    }

    [Theory]
    [InlineData(-1920, -1080)]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(2559, 1439)]
    public async Task MixedDpiNegativeDesktopUsesPhysicalPixelCentersWithoutDoubleScaling(int x, int y)
    {
        ScreenMonitor[] monitors = [new("left", new(-1920, -1080, 1920, 1080), 144, 144, false),
            new("primary", new(0, 0, 2560, 1440), 120, 120, true)];
        var frame = ScreenshotFixture.Create() with { Monitors = monitors, SourceBounds = new(-1920, -1080, 4480, 2520) };
        var native = new ClickProbe();
        await new WindowsMouseInput(() => (true, 1), native, () => monitors).ClickAsync(frame, new(x, y), MouseButton.Left);
        var move = Assert.Single(native.Batches)[0].Data.Mouse;
        Assert.Equal(x, (int)((long)move.X * 4480 / 65536) - 1920);
        Assert.Equal(y, (int)((long)move.Y * 2520 / 65536) - 1080);
    }

    [Theory]
    [InlineData(-5, -2)]
    [InlineData(0, -2)]
    [InlineData(-3, -4)]
    [InlineData(-3, 0)]
    public async Task OutOfFrameCoordinatesAreRejectedBeforeNativeChecks(int x, int y)
    {
        var frame = ScreenshotFixture.Create();
        var native = new ClickProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => throw new InvalidOperationException());
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(x, y), MouseButton.Left));
        Assert.Equal("InvalidCoordinates", error.Code);
        Assert.Equal(0, native.Checks);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task MonitorGapAndInvalidButtonCannotSendInput()
    {
        var frame = ScreenshotFixture.Create() with { SourceBounds = new(-4, -3, 10, 3),
            Monitors = [new("one", new(-4, -3, 4, 3), 96, 96, true), new("two", new(4, -3, 2, 3), 96, 96, false)] };
        var native = new ClickProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(1, -2), MouseButton.Left));
        Assert.Equal("InvalidCoordinates", error.Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.ClickAsync(frame, new(-3, -2), (MouseButton)99));
        Assert.Equal(0, native.Checks);
    }

    [Theory]
    [InlineData("generation", "StaleDesktopContext")]
    [InlineData("dpi", "StaleDesktopContext")]
    [InlineData("removed", "DesktopUnavailable")]
    [InlineData("denied", "InputDenied")]
    [InlineData("locked", "DesktopUnavailable")]
    public async Task StaleOrUnauthorizedDesktopCannotStartClick(string change, string code)
    {
        var frame = ScreenshotFixture.Create();
        var native = new ClickProbe { Available = change != "locked" };
        var driver = new WindowsMouseInput(() => (change != "denied", change == "generation" ? 2 : 1), native,
            () => change == "removed" ? [] : change == "dpi" ? [frame.Monitors[0] with { DpiX = 96 }] : frame.Monitors);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(-3, -2), MouseButton.Left));
        Assert.Equal(code, error.Code);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task TopologyReadCannotHideDesktopBecomingUnavailable()
    {
        var frame = ScreenshotFixture.Create();
        var native = new ClickProbe();
        var driver = new WindowsMouseInput(() => (true, 1), native, () => { native.Available = false; return frame.Monitors; });
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(-3, -2), MouseButton.Left));
        Assert.Equal("DesktopUnavailable", error.Code);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData(65535, 1)]
    [InlineData(65535, 65534)]
    [InlineData(65536, 1)]
    [InlineData(65536, 65535)]
    public async Task LargestRepresentableDesktopsDoNotRoundIntoAdjacentPixel(int width, int x)
    {
        var monitor = new ScreenMonitor("wide", new(0, 0, width, 3), 96, 96, true);
        var frame = ScreenshotFixture.Create() with { SourceBounds = monitor.Bounds, Monitors = [monitor] };
        var native = new ClickProbe();
        await new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors).ClickAsync(frame, new(x, 1), MouseButton.Left);
        var move = Assert.Single(native.Batches)[0].Data.Mouse;
        Assert.Equal(x, (int)((long)move.X * width / 65536));
    }

    [Fact]
    public async Task PartialClickOnlyReleasesButtonAndBlocksFurtherCalls()
    {
        var frame = ScreenshotFixture.Create();
        var native = new ClickProbe { Partial = true };
        var driver = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(-3, -2), MouseButton.Left));
        Assert.Equal("InputOutcomeUnknown", error.Code);
        Assert.True(error.CleanupSucceeded);
        Assert.Equal(4u, Assert.Single(native.Batches[1]).Data.Mouse.Flags);
        error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.ClickAsync(frame, new(-3, -2), MouseButton.Left));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(2, native.Batches.Count);
    }

    private sealed class ClickProbe : IWindowsInputNative
    {
        public bool Available = true, Partial;
        public int Checks;
        public List<NativeInput[]> Batches = [];
        public bool IsDesktopAvailable() { Checks++; return Available; }
        public InputInsertion Send(NativeInput[] inputs)
        { Batches.Add(inputs.ToArray()); return new(Partial && Batches.Count == 1 ? 2u : (uint)inputs.Length, 0); }
    }
}
