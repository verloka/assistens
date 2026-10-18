using PersonalAgent.Computer;
using System.Collections.Concurrent;
using System.Text;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerTypeTests
{
    [Fact]
    public async Task UnicodeAndSpecialTextReachesNativeAsBalancedPacketPairsWithoutModifiers()
    {
        const string text = "Привіт 世界 café e\u0301 🙂 <> & \" ' \\ + =end";
        var native = new TypeProbe();
        await new WindowsKeyboardInput(() => (true, 1), native).TypeAsync(text, 1);
        Assert.Equal(text, Decode(native.Batches));
        foreach (var batch in native.Batches)
        {
            Assert.InRange(batch.Length, 2, 32);
            for (var i = 0; i < batch.Length; i += 2)
            {
                Assert.Equal(1u, batch[i].Type);
                Assert.Equal(0, batch[i].Data.Keyboard.VirtualKey);
                Assert.Equal(4u, batch[i].Data.Keyboard.Flags);
                Assert.Equal(6u, batch[i + 1].Data.Keyboard.Flags);
                Assert.Equal(batch[i].Data.Keyboard.Scan, batch[i + 1].Data.Keyboard.Scan);
            }
        }
    }

    [Theory]
    [InlineData("a\tb", 9)]
    [InlineData("a\nb", 13)]
    public async Task TabAndEnterUseIsolatedKeyBatchesBeforeContinuingText(string text, ushort key)
    {
        var native = new TypeProbe();
        await new WindowsKeyboardInput(() => (true, 1), native).TypeAsync(text, 1);
        Assert.Equal(3, native.Batches.Count);
        Assert.Equal("a", Decode([native.Batches[0]]));
        var control = native.Batches[1];
        Assert.Equal(2, control.Length);
        Assert.Equal(key, control[0].Data.Keyboard.VirtualKey);
        Assert.Equal(key, control[1].Data.Keyboard.VirtualKey);
        Assert.Equal(0u, control[0].Data.Keyboard.Flags);
        Assert.Equal(2u, control[1].Data.Keyboard.Flags);
        Assert.Equal("b", Decode([native.Batches[2]]));
    }

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public async Task TargetPolicyIsRecheckedAfterControlKeyBeforeAnyFollowingText(string text)
    {
        var native = new TypeProbe();
        var allowed = true;
        native.AfterSend = () => { if (native.Batches.Count == 2) allowed = false; };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsKeyboardInput(() => (allowed, 1), native).TypeAsync(text, 1));
        Assert.Equal("InputDenied", error.Code);
        Assert.Equal(2, native.Batches.Count);
    }

    [Theory]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    public async Task MultilineNormalizesLineEndingsAndUsesBalancedEnter(string text, string expected)
    {
        var native = new TypeProbe();
        await new WindowsKeyboardInput(() => (true, 1), native).TypeAsync(text, 1);
        Assert.Equal(expected, Decode(native.Batches));
        var enter = native.Batches.SelectMany(x => x).Where(x => x.Data.Keyboard.VirtualKey == 13).ToArray();
        Assert.Equal(2, enter.Length);
        Assert.Equal(0u, enter[0].Data.Keyboard.Flags);
        Assert.Equal(2u, enter[1].Data.Keyboard.Flags);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public async Task SurrogatePairCannotBeSplitAcrossAtomicBatches(int prefixLength)
    {
        var text = new string('a', prefixLength) + "🙂Z";
        var native = new TypeProbe();
        await new WindowsKeyboardInput(() => (true, 1), native).TypeAsync(text, 1);
        Assert.Equal(text, Decode(native.Batches));
        foreach (var batch in native.Batches)
        {
            var piece = Decode([batch]);
            Assert.True(new UTF8Encoding(false, true).GetByteCount(piece) > 0);
            Assert.False(char.IsHighSurrogate(piece[^1]));
            Assert.False(char.IsLowSurrogate(piece[0]));
        }
    }

    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("pair")]
    [InlineData("nul")]
    [InlineData("escape")]
    [InlineData("oversize")]
    public async Task InvalidPayloadIsRejectedBeforeAnyInputIncludingValidPrefix(string issue)
    {
        var text = issue switch
        {
            "high" => "valid prefix\ud800",
            "low" => "valid prefix\udc00",
            "pair" => "valid prefix\ud800X",
            "nul" => "valid prefix\0",
            "escape" => "valid prefix\u001b",
            _ => new string('x', 4097)
        };
        var native = new TypeProbe();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new WindowsKeyboardInput(() => (true, 1), native).TypeAsync(text, 1));
        Assert.Equal(0, native.Checks);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task MaximumLengthIsBoundedAndEmptyInputHasNoEffect()
    {
        var native = new TypeProbe();
        var driver = new WindowsKeyboardInput(() => (true, 1), native);
        await driver.TypeAsync("", 1);
        Assert.Empty(native.Batches);
        var text = new string('x', 4096);
        await driver.TypeAsync(text, 1);
        Assert.Equal(256, native.Batches.Count);
        Assert.All(native.Batches, batch => Assert.Equal(32, batch.Length));
        Assert.Equal(text, Decode(native.Batches));
    }

    [Fact]
    public async Task MissingAuthorizationPreventsAllNativeAccess()
    {
        var native = new TypeProbe();
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsKeyboardInput(null, native).TypeAsync("test", 1));
        Assert.Equal("InputDenied", error.Code);
        Assert.Equal(0, native.Checks);
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task PreCancellationHasNoEffectAndCancellationAfterFirstChunkPreservesOnlyItsCompleteText()
    {
        using var cts = new CancellationTokenSource();
        var native = new TypeProbe { AfterSend = () => cts.Cancel() };
        var driver = new WindowsKeyboardInput(() => (true, 1), native);
        var text = new string('x', 15) + "🙂not sent";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.TypeAsync(text, 1, cts.Token));
        Assert.Single(native.Batches);
        Assert.Equal(new string('x', 15), Decode(native.Batches));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.TypeAsync("next", 1, cts.Token));
        Assert.Single(native.Batches);
    }

    [Theory]
    [InlineData("revoke", "InputDenied")]
    [InlineData("generation", "StaleDesktopContext")]
    [InlineData("lock", "DesktopUnavailable")]
    public async Task GateAndSessionAreRecheckedBetweenChunks(string change, string code)
    {
        var allowed = true;
        long generation = 1;
        var native = new TypeProbe();
        native.AfterSend = () => { if (change == "revoke") allowed = false; if (change == "generation") generation++; if (change == "lock") native.Available = false; };
        var error = await Assert.ThrowsAsync<ComputerInputException>(() =>
            new WindowsKeyboardInput(() => (allowed, generation), native).TypeAsync(new string('a', 40), 1));
        Assert.Equal(code, error.Code);
        Assert.Single(native.Batches);
        Assert.Equal(new string('a', 16), Decode(native.Batches));
    }

    [Fact]
    public async Task PartialChunkOnlyReleasesHeldKeyAndCannotRetryOrSendFollowingChunks()
    {
        var native = new TypeProbe { Partial = true };
        var driver = new WindowsKeyboardInput(() => (true, 1), native);
        var error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.TypeAsync(new string('a', 40), 1));
        Assert.Equal("InputOutcomeUnknown", error.Code);
        Assert.True(error.CleanupSucceeded);
        Assert.Equal(2, native.Batches.Count);
        Assert.Equal(6u, Assert.Single(native.Batches[1]).Data.Keyboard.Flags);
        error = await Assert.ThrowsAsync<ComputerInputException>(() => driver.TypeAsync("other", 1));
        Assert.Equal("InputStateUnknown", error.Code);
        Assert.Equal(2, native.Batches.Count);
    }

    [Fact]
    public async Task OtherInputCannotInterleaveBetweenTextChunks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var first = new TypeProbe { AfterSend = () =>
        {
            order.Enqueue("text"); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        } };
        var second = new TypeProbe { AfterSend = () => order.Enqueue("other") };
        var typing = new WindowsKeyboardInput(() => (true, 1), first).TypeAsync(new string('a', 40), 1);
        Task? other = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            other = new WindowsInputSender(() => (true, 1), second).SendAsync([NativeInput.Key(65, false), NativeInput.Key(65, true)], 1);
            Assert.False(other.IsCompleted);
        }
        finally { release.Set(); }
        await typing;
        Assert.NotNull(other);
        await other;
        Assert.Equal(new[] { "text", "text", "text", "other" }, order);
    }

    private static string Decode(IEnumerable<NativeInput[]> batches) => new(batches.SelectMany(batch => batch)
        .Where(input => (input.Data.Keyboard.Flags & 2) == 0)
        .Select(input => input.Data.Keyboard.VirtualKey == 13 ? '\n' : (char)input.Data.Keyboard.Scan).ToArray());

    private sealed class TypeProbe : IWindowsInputNative
    {
        public bool Available = true, Partial;
        public int Checks;
        public List<NativeInput[]> Batches = [];
        public Action? AfterSend;
        public bool IsDesktopAvailable() { Checks++; return Available; }
        public InputInsertion Send(NativeInput[] inputs)
        {
            Batches.Add(inputs.ToArray()); AfterSend?.Invoke();
            return new(Partial && Batches.Count == 1 ? 1u : (uint)inputs.Length, 0);
        }
    }
}
