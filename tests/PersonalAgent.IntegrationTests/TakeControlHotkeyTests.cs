using PersonalAgent.Computer;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class TakeControlHotkeyTests
{
    [Fact]
    public Task RegistersNoRepeatAndDispatchesOnlyMatchingSignatureThenUnregistersOnce() => OnWpfThread(() =>
    {
        var native = new HotkeyProbe();
        var calls = 0;
        var hotkey = new WindowsTakeControlHotkey(() => calls++, new(), native);
        Assert.Equal(0x4006u, native.Modifiers);
        Assert.Equal(0x79u, native.Key);
        var handled = false;
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId + 1, new(0x790006), ref handled);
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId, new(0x780006), ref handled);
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId, new(0x790002), ref handled);
        Assert.False(handled); Assert.Equal(0, calls);
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId, new(0x790006), ref handled);
        Assert.True(handled); Assert.Equal(1, calls);
        hotkey.Dispose(); hotkey.Dispose();
        handled = false;
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId, new(0x790006), ref handled);
        Assert.False(handled); Assert.Equal(1, calls); Assert.Equal(1, native.Unregisters);
    });

    [Fact]
    public Task RegistrationConflictKeepsNativeErrorAndDoesNotPretendRegistered() => OnWpfThread(() =>
    {
        var native = new HotkeyProbe { RegisterOk = false, LastError = 1409 };
        var error = Assert.Throws<TakeControlHotkeyException>(() => new WindowsTakeControlHotkey(() => { }, new(), native));
        Assert.Equal(1409, error.NativeError);
        Assert.Equal(0, native.Unregisters);
    });

    [Fact]
    public Task CallbackExceptionCannotEscapeWindowProcedureAndFailedUnregisterRemainsVisible() => OnWpfThread(() =>
    {
        var native = new HotkeyProbe { UnregisterOk = false, LastError = 5 };
        var hotkey = new WindowsTakeControlHotkey(() => throw new InvalidOperationException("synthetic"), new(), native);
        var handled = false;
        hotkey.ProcessMessage(0x312, WindowsTakeControlHotkey.HotkeyId, new(0x790006), ref handled);
        Assert.True(handled);
        Assert.IsType<InvalidOperationException>(hotkey.CallbackError);
        hotkey.Dispose();
        Assert.Equal(5, hotkey.UnregisterError);
    });

    [Theory]
    [InlineData(0u, 0x79u)] [InlineData(8u, 0x79u)] [InlineData(6u, 0x7bu)]
    public void UnsupportedConfigurationCannotRegister(uint modifiers, uint key) =>
        Assert.Throws<ArgumentException>(() => new TakeControlHotkeyGesture(modifiers, key).Validate());

    private sealed class HotkeyProbe : IHotkeyNative
    {
        public bool RegisterOk = true, UnregisterOk = true;
        public int LastError { get; set; }
        public uint Modifiers, Key;
        public int Unregisters;
        public bool Register(nint window, int id, uint modifiers, uint key) { Modifiers = modifiers; Key = key; return RegisterOk; }
        public bool Unregister(nint window, int id) { Unregisters++; return UnregisterOk; }
    }
    private static Task OnWpfThread(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { test(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
