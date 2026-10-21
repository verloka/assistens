using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

public sealed class InputObservationTests
{
    [Fact]
    public async Task ExplicitStartUsesOneMessageThreadAndIdleHasNoPollingOrActivityBacklog()
    {
        var native = new Hooks(); var idle = new Idle(); var calls = 0;
        await using var observer = new WindowsInputObserver(_ => calls++, native, idle);
        Assert.False(observer.Status.Running); Assert.Equal(0, native.Installs); Assert.Equal(0, idle.Reads);
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => observer.StartAsync())), Assert.True);
        Assert.Equal(1, native.Installs); Assert.NotEqual(Environment.CurrentManagedThreadId, native.Thread);
        using var process = Process.GetCurrentProcess();
        using var worker = process.Threads.Cast<ProcessThread>().Single(thread => thread.Id == native.NativeThread);
        var before = worker.TotalProcessorTime;
        await Task.Delay(250); // One bounded idle measurement; no native hooks or input used in automation.
        var cpu = worker.TotalProcessorTime - before;
        Assert.Equal(0, observer.Status.Events); Assert.Equal(0, calls); Assert.Equal(0, idle.Reads);
        Assert.True(cpu < TimeSpan.FromMilliseconds(100), "The observation thread should block on messages while idle.");
        Assert.Equal(new(true, TimeSpan.FromMinutes(2)), observer.ReadIdle()); Assert.Equal(1, idle.Reads);
        native.Emit(new(ObservedInputDevice.Mouse, false, false, 42, 0));
        Assert.Equal(1, observer.Status.Events); Assert.Equal(1, calls);
        await observer.DisposeAsync(); Assert.False(observer.Status.Running); Assert.Equal(1, native.Disposals);
        native.Emit(new(ObservedInputDevice.Keyboard, false, false, 43, 0)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BurstKeepsOnlyCountsAndForwardsMetadataOnTheHookThread()
    {
        var native = new Hooks(); var calls = 0;
        await using var observer = new WindowsInputObserver(activity =>
        { Assert.Equal(ObservedInputDevice.Keyboard, activity.Device); Assert.True(activity.Injected); Assert.Equal(native.Thread, Environment.CurrentManagedThreadId); calls++; }, native, new Idle());
        Assert.True(await observer.StartAsync());
        native.Dispatcher!.Invoke(() => { for (var i = 0; i < 100_000; i++) native.Emit(new(ObservedInputDevice.Keyboard, true, false, (uint)i, 19)); });
        Assert.Equal(100_000, calls); Assert.Equal(100_000, observer.Status.Events); Assert.Null(observer.Status.ErrorCode);
    }

    [Fact]
    public async Task CallbackFailureCannotEscapeAndStopsObservationWithFixedDiagnostic()
    {
        var native = new Hooks(); await using var observer = new WindowsInputObserver(_ => throw new InvalidOperationException("private body"), native, new Idle());
        Assert.True(await observer.StartAsync()); native.Emit(new(ObservedInputDevice.Mouse, false, false, 0, 0));
        await observer.DisposeAsync(); Assert.Equal("InputObservationCallbackFailed", observer.Status.ErrorCode); Assert.Equal(1, native.Disposals);
    }

    [Fact]
    public async Task RegistrationFailureIsUnavailableAndNeverClaimsActive()
    {
        var native = new Hooks { Fail = true }; await using var observer = new WindowsInputObserver(_ => { }, native, new Idle());
        Assert.False(await observer.StartAsync()); Assert.False(observer.Status.Running); Assert.Equal("InputObservationUnavailable", observer.Status.ErrorCode);
        await observer.DisposeAsync(); Assert.Equal(1, native.Installs);
    }

    [Fact]
    public async Task StopDuringRegistrationRollsBackBeforeStartCanReportSuccess()
    {
        var native = new Hooks { Hold = true }; await using var observer = new WindowsInputObserver(_ => { }, native, new Idle());
        var starting = observer.StartAsync(); await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = observer.DisposeAsync(); native.Release.Set();
        await stopping.AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(await starting); Assert.Equal(1, native.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.StartAsync());
    }

    [Fact]
    public async Task CancelledStartupAndNeverStartedDisposalInstallNoHooks()
    {
        var native = new Hooks(); await using var observer = new WindowsInputObserver(_ => { }, native, new Idle());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.StartAsync(stop.Token));
        await observer.DisposeAsync(); Assert.Equal(0, native.Installs);
    }

    [Theory]
    [InlineData(0U, false, false)] [InlineData(0x10U, true, false)] [InlineData(0x12U, true, true)]
    public void KeyboardFlagsPreserveOnlyAttributionMetadata(uint flags, bool injected, bool lower)
    {
        var activity = WindowsActivityHooks.Keyboard(new() { Key = 65, Scan = 31, Flags = flags, Time = 23, Extra = 456 });
        Assert.Equal(new(ObservedInputDevice.Keyboard, injected, lower, 23, 456), activity);
    }

    [Theory]
    [InlineData(0U, false, false)] [InlineData(1U, true, false)] [InlineData(3U, true, true)]
    public void MouseFlagsDoNotPublishPositionOrButtonData(uint flags, bool injected, bool lower)
    {
        var activity = WindowsActivityHooks.Mouse(new() { X = 987, Y = 654, Data = 120, Flags = flags, Time = 24, Extra = 789 });
        Assert.Equal(new(ObservedInputDevice.Mouse, injected, lower, 24, 789), activity);
        Assert.False(WindowsActivityHooks.IsInputMessage(true, 0x200)); Assert.False(WindowsActivityHooks.IsInputMessage(false, 0x100));
        Assert.True(WindowsActivityHooks.IsInputMessage(false, 0x20e)); Assert.True(WindowsActivityHooks.IsInputMessage(true, 0x105));
    }

    private sealed class Idle : ISystemIdleSource
    { public int Reads; public SystemIdleSnapshot Read() { Reads++; return new(true, TimeSpan.FromMinutes(2)); } }
    private sealed class Hooks : IWindowsActivityHooks
    {
        public int Installs, Disposals, Thread, NativeThread; public bool Fail, Hold; public Dispatcher? Dispatcher;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new(); private Action<ObservedInputActivity>? observe;
        public IDisposable Install(Action<ObservedInputActivity> callback)
        {
            Installs++; Thread = Environment.CurrentManagedThreadId; NativeThread = GetCurrentThreadId(); Dispatcher = Dispatcher.CurrentDispatcher; Entered.TrySetResult();
            if (Hold && !Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (Fail) throw new InvalidOperationException("private failure"); observe = callback; return new Scope(this);
        }
        public void Emit(ObservedInputActivity activity) => observe?.Invoke(activity);
        private sealed class Scope(Hooks owner) : IDisposable { public void Dispose() => Interlocked.Increment(ref owner.Disposals); }
        [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    }
}
