using PersonalAgent.Computer;
using System.Runtime.InteropServices;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerInputSenderTests
{
    private static NativeInput[] Pair() => [NativeInput.Unicode('x', false), NativeInput.Unicode('x', true)];

    [Fact]
    public void NativeLayoutMatchesWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeInput>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Data)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<NativeKeyboardInput>());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<NativeMouseInput>());
    }

    [Fact]
    public async Task MissingAuthorizationNeverTouchesDesktopOrInput()
    {
        var native = new InputProbe();
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(null, native).SendAsync(Pair(), 1));
        Assert.Equal("InputDenied", error.Code);
        Assert.Equal(0, native.DesktopChecks);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData(false, 1, "InputDenied")]
    [InlineData(true, 2, "StaleDesktopContext")]
    public async Task QueuedOrStaleAuthorizationCannotSend(bool allowed, long actual, string code)
    {
        var native = new InputProbe();
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (allowed, actual), native).SendAsync(Pair(), 1));
        Assert.Equal(code, error.Code);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData("locked", "DesktopUnavailable")]
    [InlineData("revoked", "InputDenied")]
    [InlineData("generation", "StaleDesktopContext")]
    public async Task DesktopAndLastMomentContextChangesRejectBeforeNativeSend(string change, string code)
    {
        var allowed = true;
        long generation = 1;
        var native = new InputProbe { Available = change != "locked" };
        native.OnCheck = () => { if (change == "revoked") allowed = false; if (change == "generation") generation++; };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (allowed, generation), native).SendAsync(Pair(), 1));
        Assert.Equal(code, error.Code);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task PreCancellationAndMalformedOrUnbalancedInputDoNotReachNative()
    {
        var native = new InputProbe();
        var sender = new WindowsInputSender(() => (true, 1), native);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Pair(), 1, cts.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync([NativeInput.Key(17, false)], 1));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync([NativeInput.Key(17, true)], 1));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync([new() { Type = 2 }], 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sender.SendAsync([], 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sender.SendAsync(Enumerable.Repeat(Pair(), 33).SelectMany(x => x).ToArray(), 1));
        Assert.Equal(0, native.DesktopChecks);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task CancellationDuringAtomicNativeCallFinishesPairWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var native = new InputProbe { OnSend = batch => { cts.Cancel(); return new((uint)batch.Length, 0); } };
        await new WindowsInputSender(() => (true, 1), native).SendAsync(Pair(), 1, cts.Token);
        Assert.Single(native.Batches);
        Assert.Equal(2, native.Batches[0].Length);
    }

    [Theory]
    [InlineData(0, 0, "InputFailed", 1)]
    [InlineData(0, 5, "InputFailed", 1)]
    [InlineData(1, 0, "InputOutcomeUnknown", 2)]
    public async Task FailedNativeInputHasStructuredOutcomeAndOnlyReleasesInsertedPress(uint inserted, int win32, string code, int calls)
    {
        var native = new InputProbe();
        native.OnSend = batch => native.Batches.Count == 1 ? new(inserted, win32) : new((uint)batch.Length, 0);
        var sender = new WindowsInputSender(() => (true, 1), native);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => sender.SendAsync(Pair(), 1));
        Assert.Equal(code, error.Code);
        Assert.Equal(inserted, error.Inserted);
        Assert.Equal(win32, error.NativeError);
        Assert.True(error.CleanupSucceeded);
        Assert.Equal(calls, native.Batches.Count);
        if (inserted == 1) Assert.Equal(Pair()[1], Assert.Single(native.Batches[1]));
        error = await Assert.ThrowsAsync<ComputerInputException>(() => sender.SendAsync(Pair(), 1));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(calls, native.Batches.Count);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("cleanup-fails")]
    [InlineData("lock-after-partial")]
    public async Task UnknownOutcomeOrCleanupFailureNeverResendsDown(string failure)
    {
        var native = new InputProbe();
        native.OnSend = batch =>
        {
            if (native.Batches.Count == 1)
            {
                if (failure == "exception") throw new InvalidOperationException("synthetic native failure");
                if (failure == "lock-after-partial") native.Available = false;
                return new(1, 0);
            }
            return failure == "cleanup-fails" ? new(0, 5) : new((uint)batch.Length, 0);
        };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (true, 1), native).SendAsync(Pair(), 1));
        Assert.Equal("InputOutcomeUnknown", error.Code);
        Assert.Equal(failure == "exception", error.CleanupSucceeded);
        if (failure == "exception") Assert.Null(error.Inserted);
        Assert.All(native.Batches.Skip(1).SelectMany(x => x), input => Assert.Equal(6u, input.Data.Keyboard.Flags));
        Assert.Equal(failure == "lock-after-partial" ? 1 : 2, native.Batches.Count);
    }

    [Fact]
    public async Task PartialMousePressIsReleasedWithoutRepeatingMoveOrPress()
    {
        var native = new InputProbe();
        native.OnSend = batch => native.Batches.Count == 1 ? new(2, 0) : new((uint)batch.Length, 0);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (true, 1), native).SendAsync(
            [NativeInput.Mouse(0xc001, 0, 0), NativeInput.Mouse(2), NativeInput.Mouse(4)], 1));
        Assert.True(error.CleanupSucceeded);
        Assert.Equal(NativeInput.Mouse(4), Assert.Single(native.Batches[1]));
    }

    [Fact]
    public async Task FailingPolicyCannotEscapeAsSuccessOrSendInput()
    {
        var native = new InputProbe();
        var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsInputSender(() => throw new InvalidOperationException("policy unavailable"), native).SendAsync(Pair(), 1));
        Assert.Equal("InputPreflightFailed", error.Code);
        Assert.Equal(0u, error.Inserted);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task SeparateSendersSerializeAndRevalidateAfterWaiting()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new InputProbe { OnSend = batch =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return new((uint)batch.Length, 0);
        } };
        var second = new InputProbe();
        var allowed = true;
        var firstSend = new WindowsInputSender(() => (true, 1), first).SendAsync(Pair(), 1);
        Task? secondSend = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            secondSend = new WindowsInputSender(() => (allowed, 1), second).SendAsync(Pair(), 1);
            Assert.False(secondSend.IsCompleted);
            Assert.Equal(0, second.DesktopChecks);
            allowed = false;
        }
        finally { release.Set(); }
        await firstSend;
        Assert.NotNull(secondSend);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => secondSend);
        Assert.Equal("InputDenied", error.Code);
        Assert.Empty(second.Batches);
    }

    private sealed class InputProbe : IWindowsInputNative
    {
        public bool Available = true;
        public int DesktopChecks;
        public readonly List<NativeInput[]> Batches = [];
        public Action? OnCheck;
        public Func<NativeInput[], InputInsertion>? OnSend;
        public bool IsDesktopAvailable() { DesktopChecks++; OnCheck?.Invoke(); return Available; }
        public InputInsertion Send(NativeInput[] inputs)
        { Batches.Add(inputs.ToArray()); return OnSend?.Invoke(inputs) ?? new((uint)inputs.Length, 0); }
    }
}
