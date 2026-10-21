using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class DesktopEnvironmentPauseTests
{
    [Theory]
    [InlineData(DesktopEnvironmentChange.Session)] [InlineData(DesktopEnvironmentChange.Suspend)]
    [InlineData(DesktopEnvironmentChange.Resume)] [InlineData(DesktopEnvironmentChange.Display)]
    public async Task LifecycleChangesRevokeBeforeDrainAndRecoveryNeedsExplicitResumeWithFreshObservation(DesktopEnvironmentChange change)
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop); using var stop = new CancellationTokenSource();
        var runtime = new HeldRuntime(runs); var loop = new TaskRunner(runs, runtime).RunAsync(stop.Token);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var events = new Events(); await using var service = new DesktopEnvironmentPauseService(desktop, takeover,
            NullLogger<DesktopEnvironmentPauseService>.Instance, events.Subscribe);
        DesktopLease? freshLease = null;
        try
        {
            Assert.All(await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.StartAsync())), Assert.True);
            Assert.Equal(1, events.Subscriptions); Assert.Null(events.Context);
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic lifecycle run", new("test")));
            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var lease = await desktop.AcquireAsync(run.Id);
            var native = new Native(); var controller = new WindowsComputerController(desktop, lease, native, native, native.GetMonitors);
            var oldFrame = await controller.ScreenshotAsync(new()); controller.ConfirmObservation(oldFrame);
            await controller.TypeAsync("baseline"); Assert.Equal(1, native.InputCalls);
            using (var atomic = desktop.EnterAtomic(lease))
            {
                native.Available = false; events.Emit(change);
                Assert.False(desktop.IsCurrent(lease)); var version = desktop.Snapshot.PauseVersion;
                for (var i = 0; i < 100; i++) events.Emit(change);
                Assert.Equal(version, desktop.Snapshot.PauseVersion); Assert.False(takeover.Completion.IsCompleted);
                await Assert.ThrowsAsync<ComputerInputException>(() => controller.TypeAsync("queued")); Assert.Equal(1, native.InputCalls);
            }
            await takeover.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(takeover.Handoff.Confirmed);
            runtime.ReachBoundary.TrySetResult(); await Until(() => runs.GetRun(run.Id).Status == RunStatus.Paused);
            var stored = (await repository.GetAsync(run.Id))!;
            Assert.Equal(nameof(PauseReason.DesktopEnvironmentChanged), stored.Run.Reason);
            Assert.Equal("DesktopEnvironmentChanged", Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed).Payload!.Value.GetProperty("Reason").GetString());
            native.Available = true; native.Monitor = new("changed", new(40, 10, 8, 4), 144, 144, true);
            events.Emit(DesktopEnvironmentChange.Resume); events.Emit(DesktopEnvironmentChange.Session);
            Assert.Equal(RunStatus.Paused, runs.GetRun(run.Id).Status); Assert.Equal(1, native.InputCalls); Assert.Equal(1, native.Captures);
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
            var paused = runs.GetRun(run.Id); await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            Assert.Equal("StaleDesktopContext", (await Assert.ThrowsAsync<ComputerInputException>(() => controller.TypeAsync("old"))).Code);
            freshLease = await desktop.AcquireAsync(run.Id); Assert.True(freshLease.Generation > lease.Generation);
            var fresh = new WindowsComputerController(desktop, freshLease, native, native, native.GetMonitors);
            Assert.Equal("FreshScreenshotRequired", (await Assert.ThrowsAsync<ComputerInputException>(() => fresh.TypeAsync("unobserved"))).Code);
            var frame = await fresh.ScreenshotAsync(new()); Assert.Equal(native.Monitor.Bounds, frame.SourceBounds);
            Assert.Throws<ComputerInputException>(() => fresh.ConfirmObservation(oldFrame));
            await Assert.ThrowsAsync<ComputerInputException>(() => fresh.TypeAsync("unevaluated")); Assert.Equal(1, native.InputCalls);
            fresh.ConfirmObservation(frame); await fresh.TypeAsync("new context"); Assert.Equal(2, native.InputCalls);
        }
        finally { if (freshLease is not null) await desktop.ReleaseAsync(freshLease); stop.Cancel(); runtime.ReachBoundary.TrySetResult(); await loop; }
    }

    [Fact]
    public async Task NoOwnerLifecycleChangesDoNotPauseMcpOnlyRunOrFreezeNormalFutureAdmission()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var events = new Events(); await using var service = new DesktopEnvironmentPauseService(desktop, takeover, NullLogger<DesktopEnvironmentPauseService>.Instance, events.Subscribe);
        Assert.True(await service.StartAsync()); var run = await runs.SubmitAsync(new(Guid.NewGuid(), "MCP-only", new("test")));
        events.Emit(DesktopEnvironmentChange.Suspend); events.Emit(DesktopEnvironmentChange.Resume);
        Assert.Equal(run.RunVersion, runs.GetRun(run.Id).RunVersion); Assert.Null(runs.GetRun(run.Id).PauseScope);
        Assert.False(takeover.Handoff.Confirmed); Assert.Equal(DesktopAccessState.Free, desktop.Snapshot.State);
        var lease = await desktop.AcquireAsync(run.Id); Assert.True(desktop.IsCurrent(lease)); await desktop.ReleaseAsync(lease);
        await service.DisposeAsync(); Assert.Equal(1, events.Disposals);
    }

    [Fact]
    public async Task ShutdownDuringSubscriptionDisposesRegistrationAndIgnoresLateCallbacks()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var events = new Events(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var service = new DesktopEnvironmentPauseService(desktop, takeover, NullLogger<DesktopEnvironmentPauseService>.Instance, callback =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return events.Subscribe(callback); });
        var starting = service.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = service.DisposeAsync().AsTask(); release.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await starting); Assert.False(service.IsMonitoring); Assert.Equal(1, events.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync());
        desktop.ResumeRun(Guid.NewGuid()); events.Emit(DesktopEnvironmentChange.Display);
        Assert.Equal(0, service.ObservedChanges); Assert.Equal(DesktopAccessState.Free, desktop.Snapshot.State);
    }

    [Fact]
    public async Task SetupFailureBlocksGuiAndHeadlessHostDoesNotSubscribe()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        await using var service = new DesktopEnvironmentPauseService(desktop, takeover, NullLogger<DesktopEnvironmentPauseService>.Instance,
            _ => throw new InvalidOperationException("private environment details"));
        service.RequireAvailable(); Assert.False(await service.StartAsync());
        Assert.Equal("DesktopEnvironmentObservationUnavailable", Assert.Throws<DesktopAccessException>(service.RequireAvailable).Code);
        await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
        using var host = AppHost.CreateBuilder([]).Build(); await host.StartAsync();
        var headless = host.Services.GetRequiredService<DesktopEnvironmentPauseService>(); Assert.False(headless.IsMonitoring); headless.RequireAvailable();
        await host.StopAsync();
    }

    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, stop.Token); }
    private sealed class HeldRuntime(TaskRunCoordinator runs) : IAgentRuntime
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReachBoundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Entered.TrySetResult(); await ReachBoundary.Task.WaitAsync(ct); await runs.WaitAtBoundaryAsync(request.RunId, ct); await Task.Delay(Timeout.Infinite, ct); return new() { Status = AgentResultStatus.Completed, Summary = "Explicit test continuation" }; }
    }
    private sealed class Events : IDisposable
    {
        public int Subscriptions, Disposals; public SynchronizationContext? Context; private Action<DesktopEnvironmentChange>? changed;
        public IDisposable Subscribe(Action<DesktopEnvironmentChange> callback) { Interlocked.Increment(ref Subscriptions); Context = SynchronizationContext.Current; changed = callback; return this; }
        public void Emit(DesktopEnvironmentChange change) => changed?.Invoke(change);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed class Native : IWindowsCaptureNative, IWindowsKeyboardNative
    {
        public bool Available = true; public int InputCalls, Captures;
        public ScreenMonitor Monitor = new("initial", new(0, 0, 4, 4), 96, 96, true);
        public bool IsDesktopAvailable() => Available; public bool IsKeyDown(ushort key) => false;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => [Monitor];
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        { Captures++; var bitmap = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, new byte[bounds.Width * bounds.Height * 4], bounds.Width * 4); bitmap.Freeze(); return bitmap; }
        public InputInsertion Send(NativeInput[] inputs) { InputCalls++; return new((uint)inputs.Length, 0); }
    }
}
