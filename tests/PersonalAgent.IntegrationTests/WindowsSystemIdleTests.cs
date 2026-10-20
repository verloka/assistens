using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.IntegrationTests;

public sealed class WindowsSystemIdleTests
{
    [Theory]
    [InlineData(5_000UL, 4_990U, 10)]
    [InlineData(4_294_968_296UL, 4_294_966_296U, 2_000)]
    [InlineData(60_000UL, 0U, 60_000)]
    [InlineData(5_000UL, 6_000U, -1)]
    [InlineData(100UL, 4_294_967_290U, -1)]
    public void NativeTickWrapIsHandledButFutureOrAmbiguousTickCannotClaimIdle(ulong now, uint input, int expectedMs)
    {
        var native = new Native { Tick = now, Input = input }; var idle = new WindowsSystemIdleSource(native).Read();
        Assert.True(idle.DesktopAvailable);
        if (expectedMs < 0) Assert.Null(idle.InputIdleTime); else Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), idle.InputIdleTime);
    }

    [Fact]
    public void ChangedBackwardTimestampIsRecentActivityEvenWhenItsReportedIdleIsLarge()
    {
        var native = new Native { Tick = 100_000, Input = 99_000 }; var source = new WindowsSystemIdleSource(native);
        Assert.Equal(TimeSpan.FromSeconds(1), source.Read().InputIdleTime);
        native.Tick = 101_000; native.Input = 50_000; Assert.Equal(TimeSpan.Zero, source.Read().InputIdleTime);
        native.Tick = 111_000; Assert.Equal(TimeSpan.FromSeconds(10), source.Read().InputIdleTime);
        native.Tick = 112_000; native.Input = 111_999; Assert.Equal(TimeSpan.Zero, source.Read().InputIdleTime);
    }

    [Fact]
    public void LockedDesktopFailureAndExceptionFailClosedWithoutCaptureOrInput()
    {
        var native = new Native { Available = false }; var source = new WindowsSystemIdleSource(native);
        Assert.Equal(new(false, null), source.Read()); Assert.Equal(0, native.Reads);
        native.Available = true; native.Success = false;
        Assert.Equal(new(true, null), source.Read()); Assert.Equal(1, native.Reads);
        native.Throw = true; Assert.Equal(new(false, null), source.Read());
    }

    [Fact]
    public void ActualWin32IdleProbeReadsOnlySessionMetadata()
    {
        var native = new WindowsIdleNative();
        Assert.True(native.TryGetLastInputTick(out _)); Assert.True(native.GetTickCount64() > 0);
        var reading = new WindowsSystemIdleSource().Read();
        if (!reading.DesktopAvailable) Assert.Null(reading.InputIdleTime);
        else Assert.True(reading.InputIdleTime is null || reading.InputIdleTime >= TimeSpan.Zero);
        // No capture, windows, SendInput or focus operation is used by this probe.
    }

    private sealed class Native : IWindowsIdleNative
    {
        public bool Available = true, Success = true, Throw;
        public ulong Tick; public uint Input; public int Reads;
        public bool IsDesktopAvailable() => Throw ? throw new InvalidOperationException("Synthetic probe failure") : Available;
        public bool TryGetLastInputTick(out uint tick) { Reads++; tick = Input; return Success; }
        public ulong GetTickCount64() => Tick;
    }
}
