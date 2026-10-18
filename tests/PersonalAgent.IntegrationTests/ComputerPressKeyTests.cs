using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerPressKeyTests
{
    [Fact]
    public async Task ChordCanonicalizesAndReleasesModifiersInReverseOrder()
    {
        var native = new KeyProbe();
        string? permitted = null;
        await new WindowsKeyboardInput(() => (true, 1), native, chord => { permitted = chord; return true; })
            .PressKeyAsync(new(" shift + ctrl + alt + a "), 1);
        Assert.Equal("Ctrl+Alt+Shift+A", permitted);
        var batch = Assert.Single(native.Batches);
        Assert.Equal(new ushort[] { 17, 18, 16, 65, 65, 16, 18, 17 }, batch.Select(input => input.Data.Keyboard.VirtualKey));
        Assert.Equal(new uint[] { 0, 0, 0, 0, 2, 2, 2, 2 }, batch.Select(input => input.Data.Keyboard.Flags));
        Assert.Empty(native.Held);
    }

    [Theory]
    [InlineData("Ctrl+L", 76, false)]
    [InlineData("Alt+Tab", 9, false)]
    [InlineData("Escape", 27, false)]
    [InlineData("Shift+Right", 39, true)]
    [InlineData("F12", 123, false)]
    public async Task SupportedNamesProduceCorrectMainKeyAndExtendedFlag(string chord, ushort key, bool extended)
    {
        var native = new KeyProbe();
        await new WindowsKeyboardInput(() => (true, 1), native, _ => true).PressKeyAsync(new(chord), 1);
        var main = Assert.Single(native.Batches).Where(input => input.Data.Keyboard.VirtualKey == key).ToArray();
        Assert.Equal(extended ? 1u : 0, main[0].Data.Keyboard.Flags);
        Assert.Equal(extended ? 3u : 2u, main[1].Data.Keyboard.Flags);
        Assert.Empty(native.Held);
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    [InlineData(4u)] [InlineData(5u)] [InlineData(6u)] [InlineData(7u)]
    public async Task EveryPartialPrefixCleansOwnHeldKeysWithoutRetry(uint inserted)
    {
        var native = new KeyProbe { FirstInserted = inserted };
        var driver = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.PressKeyAsync(new("Ctrl+Alt+Shift+A"), 1));
        Assert.Equal(inserted, error.Inserted);
        Assert.True(error.CleanupSucceeded);
        Assert.Empty(native.Held);
        Assert.All(native.Batches.Skip(1).SelectMany(batch => batch), input => Assert.Equal(2u, input.Data.Keyboard.Flags & 2));
        var count = native.Batches.Count;
        error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.PressKeyAsync(new("Ctrl+A"), 1));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(count, native.Batches.Count);
    }

    [Fact]
    public async Task UnknownNativeExceptionReleasesModifiersAndMainKeyWithoutReplayingPresses()
    {
        var native = new KeyProbe { FirstInserted = 3, ThrowAfterFirst = true };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsKeyboardInput(() => (true, 1), native, _ => true).PressKeyAsync(new("Ctrl+Alt+Shift+A"), 1));
        Assert.Equal("InputOutcomeUnknown", error.Code);
        Assert.Null(error.Inserted);
        Assert.True(error.CleanupSucceeded);
        Assert.Empty(native.Held);
        Assert.All(native.Batches[1], input => Assert.Equal(2u, input.Data.Keyboard.Flags & 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringAtomicSendStillReleasesAllModifiers(bool partial)
    {
        using var cts = new CancellationTokenSource();
        var native = new KeyProbe { FirstInserted = partial ? 3u : null, AfterFirst = () => cts.Cancel() };
        var driver = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        if (partial)
        {
            var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.PressKeyAsync(new("Ctrl+Alt+Shift+A"), 1, cts.Token));
            Assert.True(error.CleanupSucceeded);
        }
        else await driver.PressKeyAsync(new("Ctrl+Alt+Shift+A"), 1, cts.Token);
        Assert.Empty(native.Held);
    }

    [Fact]
    public async Task DefaultChordPolicyAndExplicitDenialPreventSend()
    {
        var native = new KeyProbe();
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsKeyboardInput(() => (true, 1), native).PressKeyAsync(new("Alt+F4"), 1));
        Assert.Equal("KeyChordDenied", error.Code);
        error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsKeyboardInput(() => (true, 1), native, chord => chord == "Ctrl+A").PressKeyAsync(new("Ctrl+Alt+Delete"), 1));
        Assert.Equal("KeyChordDenied", error.Code);
        Assert.Empty(native.Batches);
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+A")] [InlineData("Win+R")] [InlineData("Ctrl+")]
    [InlineData("Ctrl+Unknown")] [InlineData("Ctrl")] [InlineData("A+Ctrl")]
    public async Task InvalidOrUnsupportedChordRejectsBeforeNative(string chord)
    {
        var native = new KeyProbe();
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsKeyboardInput(() => (true, 1), native, _ => true).PressKeyAsync(new(chord), 1));
        Assert.Empty(native.Batches);
        Assert.Equal(0, native.Checks);
    }

    [Theory]
    [InlineData((ushort)16)] [InlineData((ushort)17)] [InlineData((ushort)18)] [InlineData((ushort)65)]
    public async Task AlreadyHeldUserKeyIsNeverReleased(ushort key)
    {
        var native = new KeyProbe(); native.ExternalHeld.Add(key);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsKeyboardInput(() => (true, 1), native, _ => true).PressKeyAsync(new("Ctrl+A"), 1));
        Assert.Equal("UserInputActive", error.Code);
        Assert.Empty(native.Batches);
        Assert.Contains(key, native.ExternalHeld);
    }

    [Fact]
    public async Task PreCancellationAndStaleGenerationSendNothing()
    {
        var native = new KeyProbe();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var driver = new WindowsKeyboardInput(() => (true, 2), native, _ => true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.PressKeyAsync(new("Ctrl+A"), 1, cts.Token));
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.PressKeyAsync(new("Ctrl+A"), 1));
        Assert.Equal("StaleDesktopContext", error.Code);
        Assert.Empty(native.Batches);
    }

    private sealed class KeyProbe : IWindowsKeyboardNative
    {
        public uint? FirstInserted;
        public bool ThrowAfterFirst;
        public int Checks;
        public Action? AfterFirst;
        public HashSet<ushort> Held = [], ExternalHeld = [];
        public List<NativeInput[]> Batches = [];
        public bool IsDesktopAvailable() { Checks++; return true; }
        public bool IsKeyDown(ushort key) => Held.Contains(key) || ExternalHeld.Contains(key);
        public InputInsertion Send(NativeInput[] inputs)
        {
            Batches.Add(inputs.ToArray());
            var count = Batches.Count == 1 ? FirstInserted ?? (uint)inputs.Length : (uint)inputs.Length;
            foreach (var input in inputs.Take((int)count))
            {
                if ((input.Data.Keyboard.Flags & 2) != 0) Held.Remove(input.Data.Keyboard.VirtualKey);
                else Held.Add(input.Data.Keyboard.VirtualKey);
            }
            if (Batches.Count == 1)
            {
                AfterFirst?.Invoke();
                if (ThrowAfterFirst) throw new InvalidOperationException("synthetic native exception");
            }
            return new(count, 0);
        }
    }
}
