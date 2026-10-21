using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.IntegrationTests;

public sealed class InputAttributionTests
{
    [Theory]
    [InlineData(ObservedInputDevice.Keyboard)] [InlineData(ObservedInputDevice.Mouse)]
    public void OnlyCurrentProcessInjectedMarkerSuppressesTakeover(ObservedInputDevice device)
    {
        Assert.NotEqual((nuint)0, InputActivityAttribution.Marker);
        var own = new ObservedInputActivity(device, true, false, 42, InputActivityAttribution.Marker);
        Assert.Equal(InputActivityOrigin.OwnInjected, InputActivityAttribution.Classify(own)); Assert.False(InputActivityAttribution.RequiresTakeover(own));
        var physical = own with { Injected = false, ExtraInfo = 0 };
        Assert.Equal(InputActivityOrigin.PhysicalCandidate, InputActivityAttribution.Classify(physical)); Assert.True(InputActivityAttribution.RequiresTakeover(physical));
        foreach (var uncertain in new[] { own with { ExtraInfo = 0 }, own with { ExtraInfo = InputActivityAttribution.Marker ^ 1 },
            own with { Injected = false }, own with { LowerIntegrityInjected = true }, physical with { LowerIntegrityInjected = true }, own with { Device = (ObservedInputDevice)99 } })
        { Assert.Equal(InputActivityOrigin.Uncertain, InputActivityAttribution.Classify(uncertain)); Assert.True(InputActivityAttribution.RequiresTakeover(uncertain)); }
    }

    [Fact]
    public async Task ActualDriverPathsStampTextChordMouseWheelAndCleanupWithoutChangingInputPayloads()
    {
        var native = new Input(); var keyboard = new WindowsKeyboardInput(() => (true, 1), native, _ => true);
        await keyboard.TypeAsync("A😀\n\t", 1); await keyboard.PressKeyAsync(new("Ctrl+A"), 1);
        var frame = ScreenshotFixture.Create(); var mouse = new WindowsMouseInput(() => (true, 1), native, () => frame.Monitors);
        await mouse.ClickAsync(frame, new(-3, -2), MouseButton.Left); await mouse.ScrollAsync(frame, new(-3, -2), ScrollDirection.Down, 2);
        Assert.Contains(native.Batches.SelectMany(batch => batch), input => input.Type == 0 && input.Data.Mouse.Flags == 0x800);
        Assert.Contains(native.Batches.SelectMany(batch => batch), input => input.Type == 1 && input.Data.Keyboard.VirtualKey == 0x11);
        Assert.All(native.Batches.SelectMany(batch => batch), AssertOwn);
        foreach (var mode in new[] { "partial-key", "partial-mouse", "unknown" })
        {
            var failure = new Input { Failure = mode }; var original = mode == "partial-mouse" ?
                new[] { NativeInput.Mouse(0xc001), NativeInput.Mouse(2), NativeInput.Mouse(4) } : new[] { NativeInput.Key(17, false), NativeInput.Key(17, true) };
            var error = await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (true, 1), failure).SendAsync(original, 1));
            Assert.True(error.CleanupSucceeded); Assert.Equal(2, failure.Batches.Count);
            Assert.All(failure.Batches.SelectMany(batch => batch), AssertOwn);
            var release = Assert.Single(failure.Batches[1]); Assert.True(release.Type == 0 ? release.Data.Mouse.Flags == 4 : release.Data.Keyboard.Flags == 2);
            Assert.All(original, input => Assert.Equal((nuint)0, input.Type == 0 ? input.Data.Mouse.Extra : input.Data.Keyboard.Extra));
        }
    }

    [Fact]
    public async Task ForgedCallerMarkerStillFailsPreflightAndCannotBypassLeaseOrValidation()
    {
        var native = new Input(); var original = new[] { NativeInput.Key(65, false), NativeInput.Key(65, true) };
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsInputSender(() => (true, 1), native).SendAsync(InputActivityAttribution.Stamp(original), 1));
        await Assert.ThrowsAsync<ComputerInputException>(() => new WindowsInputSender(() => (false, 1), native).SendAsync(original, 1));
        Assert.Empty(native.Batches);
    }

    [Fact]
    public async Task OwnDriverEventsKeepLeaseButHumanAndForeignInjectionCloseItImmediately()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); var run = Guid.NewGuid(); var lease = await desktop.AcquireAsync(run);
        var native = new Input { OnActivity = activity => { if (InputActivityAttribution.RequiresTakeover(activity)) desktop.TakeControl(); } };
        var sender = new WindowsInputSender(desktop, lease, native);
        await sender.SendAsync([NativeInput.Key(65, false), NativeInput.Key(65, true)], lease.Generation);
        Assert.True(desktop.IsCurrent(lease));
        native.OnActivity(new(ObservedInputDevice.Mouse, false, false, 0, 0));
        Assert.False(desktop.IsCurrent(lease));
        await Assert.ThrowsAsync<ComputerInputException>(() => sender.SendAsync([NativeInput.Key(66, false), NativeInput.Key(66, true)], lease.Generation));
        Assert.Single(native.Batches); Assert.True(await desktop.ReleaseAsync(lease));
        desktop.ResumeRun(run); lease = await desktop.AcquireAsync(run);
        native.OnActivity(new(ObservedInputDevice.Keyboard, true, false, 0, 0)); Assert.False(desktop.IsCurrent(lease));
        await desktop.ReleaseAsync(lease);
    }

    private static void AssertOwn(NativeInput input)
    {
        var activity = input.Type == 1 ? WindowsActivityHooks.Keyboard(new() { Flags = 0x10, Extra = input.Data.Keyboard.Extra }) :
            WindowsActivityHooks.Mouse(new() { Flags = 1, Extra = input.Data.Mouse.Extra });
        Assert.Equal(InputActivityOrigin.OwnInjected, InputActivityAttribution.Classify(activity));
    }
    private sealed class Input : IWindowsKeyboardNative
    {
        public List<NativeInput[]> Batches = []; public string? Failure; public Action<ObservedInputActivity>? OnActivity;
        public bool IsDesktopAvailable() => true;
        public bool IsKeyDown(ushort key) => false;
        public InputInsertion Send(NativeInput[] inputs)
        {
            Batches.Add(inputs.ToArray());
            foreach (var input in inputs) OnActivity?.Invoke(new(input.Type == 1 ? ObservedInputDevice.Keyboard : ObservedInputDevice.Mouse, true, false, 0,
                input.Type == 1 ? input.Data.Keyboard.Extra : input.Data.Mouse.Extra));
            if (Batches.Count == 1 && Failure is { } failure)
            { if (failure == "unknown") throw new InvalidOperationException("Unknown native outcome"); return new(failure == "partial-mouse" ? 2u : 1u, 0); }
            return new((uint)inputs.Length, 0);
        }
    }
}
