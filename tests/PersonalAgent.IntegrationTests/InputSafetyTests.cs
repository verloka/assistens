using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.IntegrationTests;

public sealed class InputSafetyTests
{
    [Theory]
    [InlineData("click", false)] [InlineData("click", true)]
    [InlineData("type", false)] [InlineData("type", true)]
    [InlineData("chord", false)] [InlineData("chord", true)]
    public async Task FailureReleasesOwnInputAndFaultsLaterOperationOnSameDriver(string operation, bool unknown)
    {
        var native = new SafetyProbe { Prefix = operation == "click" ? 2 : 1, ThrowFirst = unknown };
        var frame = ScreenshotFixture.Create();
        var mouse = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        var keyboard = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        var error = await Assert.ThrowsAsync<ComputerInputException>(async () =>
        {
            if (operation == "click") await mouse.ClickAsync(frame, new(-3, -2), MouseButton.Left);
            else if (operation == "type") await keyboard.TypeAsync(new string('a', 40), 1);
            else await keyboard.PressKeyAsync(new("Ctrl+Alt+Shift+A"), 1);
        });
        Assert.Equal("InputOutcomeUnknown", error.Code);
        Assert.True(error.CleanupSucceeded);
        Assert.Empty(native.Held);
        Assert.All(native.Batches.Skip(1).SelectMany(batch => batch), input => Assert.True(IsRelease(input)));
        var count = native.Batches.Count;
        // Cross-method calls share a fault latch inside each driver, not just the failed operation.
        error = await Assert.ThrowsAsync<ComputerInputException>(async () =>
        {
            if (operation == "click") await mouse.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1);
            else await keyboard.TypeAsync("must not start", 1);
        });
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(count, native.Batches.Count);
        Assert.Empty(native.Held);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task QueuedOperationCannotStartAfterPriorAtomicCallFails(bool mouseInput)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new SafetyProbe { Prefix = mouseInput ? 2 : 1, BeforeFirstReturns = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        } };
        var frame = ScreenshotFixture.Create();
        var mouse = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        var keyboard = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        var first = mouseInput ? mouse.ClickAsync(frame, new(-3, -2), MouseButton.Right) : keyboard.PressKeyAsync(new("Ctrl+A"), 1);
        Task? queued = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            queued = mouseInput ? mouse.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 1) : keyboard.TypeAsync("queued", 1);
            Assert.False(queued.IsCompleted);
        }
        finally { release.Set(); }
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => first);
        Assert.True(error.CleanupSucceeded);
        Assert.NotNull(queued);
        error = await Assert.ThrowsAsync<ComputerInputException>(() => queued);
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(2, native.Batches.Count); // Only initial partial batch and release cleanup.
        Assert.Empty(native.Held);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CleanupFailureOrLockedDesktopIsExplicitAndStillForbidsLaterInput(bool locked)
    {
        var native = new SafetyProbe { Prefix = 1, FailCleanup = !locked };
        native.BeforeFirstReturns = () => { if (locked) native.Available = false; };
        var keyboard = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => keyboard.PressKeyAsync(new("Ctrl+A"), 1));
        Assert.False(error.CleanupSucceeded);
        Assert.NotEmpty(native.Held); // Never falsely promise release if Windows refuses cleanup.
        var count = native.Batches.Count;
        error = await Assert.ThrowsAsync<ComputerInputException>(() => keyboard.TypeAsync("forbidden", 1));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(count, native.Batches.Count);
    }

    private static bool IsRelease(NativeInput input) => input.Type == 1 ? (input.Data.Keyboard.Flags & 2) != 0 : input.Data.Mouse.Flags is 4 or 16 or 64;
    private static string Identity(NativeInput input) => input.Type == 1 ?
        $"key:{input.Data.Keyboard.VirtualKey}:{input.Data.Keyboard.Scan}:{input.Data.Keyboard.Flags & ~2u}" :
        $"mouse:{(IsRelease(input) ? input.Data.Mouse.Flags >> 1 : input.Data.Mouse.Flags)}";
    private sealed class SafetyProbe : IWindowsKeyboardNative
    {
        public int Prefix;
        public bool ThrowFirst, FailCleanup, Available = true;
        public Action? BeforeFirstReturns;
        public HashSet<string> Held = [];
        public List<NativeInput[]> Batches = [];
        public bool IsDesktopAvailable() => Available;
        public bool IsKeyDown(ushort key) => false; // No physical user input in this scripted fixture.
        public InputInsertion Send(NativeInput[] inputs)
        {
            Batches.Add(inputs.ToArray());
            var count = Batches.Count == 1 ? Prefix : FailCleanup ? 0 : inputs.Length;
            foreach (var input in inputs.Take(count))
            {
                if (input.Type == 0 && input.Data.Mouse.Flags is not (2 or 4 or 8 or 16 or 32 or 64)) continue;
                if (IsRelease(input)) Held.Remove(Identity(input));
                else Held.Add(Identity(input));
            }
            if (Batches.Count == 1)
            {
                BeforeFirstReturns?.Invoke();
                if (ThrowFirst) throw new InvalidOperationException("synthetic unknown outcome");
            }
            return new((uint)count, 0);
        }
    }
}
