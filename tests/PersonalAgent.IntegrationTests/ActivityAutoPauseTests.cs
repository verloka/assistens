using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

public sealed class ActivityAutoPauseTests
{
    [Theory]
    [InlineData("physical")] [InlineData("foreign")] [InlineData("lower")]
    public async Task ActivityAutoPauseClosesGateBeforeDelayedStorageAndWaitsForAtomicInputCleanup(string origin)
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new DelayedPause(new SqliteRunRepository(database)); var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop); using var stop = new CancellationTokenSource();
        var runtime = new HeldRuntime(runs); var loop = new TaskRunner(runs, runtime).RunAsync(stop.Token);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var hooks = new Hooks(); await using var activity = new UserActivityPauseService(desktop, takeover, NullLogger<UserActivityPauseService>.Instance,
            callback => new WindowsInputObserver(callback, hooks, new Idle()));
        Assert.True(await activity.StartAsync());
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Active GUI activity fixture", new("test")));
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var lease = await desktop.AcquireAsync(run.Id);
        using var input = new Input(hooks); var keyboard = new WindowsKeyboardInput(desktop, lease, input);
        var typing = keyboard.TypeAsync(new string('a', 100), lease.Generation);
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(desktop.IsCurrent(lease)); Assert.True(activity.IgnoredOwnEvents > 0);
            var user = new ObservedInputActivity(ObservedInputDevice.Mouse, origin != "physical", origin == "lower", 42,
                origin == "lower" ? InputActivityAttribution.Marker : 0);
            hooks.Emit(user); // Synchronous callback must return while both native input and SQLite remain held.
            Assert.False(desktop.IsCurrent(lease)); Assert.Equal(DesktopAccessState.Pausing, desktop.Snapshot.State);
            await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(takeover.Completion.IsCompleted);
            for (var i = 0; i < 1000; i++) hooks.Emit(user); Assert.Equal(1, repository.PauseWrites);
            input.Release.Set(); await Assert.ThrowsAsync<ComputerInputException>(() => typing); Assert.Equal(1, input.Calls);
            Assert.False(takeover.Handoff.Confirmed); // Released native input alone cannot replace the durable checkpoint.
            repository.Release.TrySetResult(); await takeover.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(takeover.Handoff.Confirmed); Assert.Null(desktop.Snapshot.OwnerRunId);
            runtime.Release.TrySetResult();
            await Until(() => runs.GetRun(run.Id).Status == RunStatus.Paused);
            var paused = runs.GetRun(run.Id); Assert.Equal(nameof(PauseReason.AutoPause), paused.Reason); Assert.Equal(PauseScope.EntireRun, paused.PauseScope);
            var stored = (await repository.GetAsync(run.Id))!; Assert.Equal(nameof(PauseReason.AutoPause), stored.Run.Reason);
            Assert.Contains(stored.Events, e => e.Message == nameof(PauseReason.AutoPause));
            Assert.Equal("UserActivity", Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed).Payload!.Value.GetProperty("Reason").GetString());
            var version = paused.RunVersion; await Task.Delay(30); Assert.Equal(version, runs.GetRun(run.Id).RunVersion); Assert.Equal(1, input.Calls);
            await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
            using var uiHost = Host.CreateDefaultBuilder().Build();
            var model = new ControlPanelViewModel(runs, runs, desktop, takeover, (IHostApplicationLifetime)uiHost.Services.GetService(typeof(IHostApplicationLifetime))!);
            Assert.Contains("Agent paused", model.Message); Assert.True(model.IsControlConfirmed);
            await runs.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion)); // Deliberate owner cancellation, no automatic resume.
        }
        finally { input.Release.Set(); repository.Release.TrySetResult(); runtime.Release.TrySetResult(); stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task ActivityWithoutGuiOwnerDoesNotPauseMcpOnlyRunOrBlockFutureAcquisition()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var hooks = new Hooks(); await using var service = new UserActivityPauseService(desktop, takeover, NullLogger<UserActivityPauseService>.Instance,
            callback => new WindowsInputObserver(callback, hooks, new Idle()));
        Assert.True(await service.StartAsync()); var run = await runs.SubmitAsync(new(Guid.NewGuid(), "MCP-only", new("test")));
        var version = run.RunVersion; hooks.Emit(new(ObservedInputDevice.Keyboard, false, false, 0, 0));
        Assert.Equal(version, runs.GetRun(run.Id).RunVersion); Assert.Equal(DesktopAccessState.Free, desktop.Snapshot.State);
        var lease = await desktop.AcquireAsync(run.Id); Assert.True(desktop.IsCurrent(lease));
        await desktop.ReleaseAsync(lease);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ObserverSetupOrUnexpectedPumpExitBlocksAcquisitionWithoutClaimingHealthyMonitoring(bool afterStart)
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var hooks = new Hooks { Fail = !afterStart }; WindowsInputObserver? observer = null;
        await using var service = new UserActivityPauseService(desktop, takeover, NullLogger<UserActivityPauseService>.Instance,
            callback => observer = new WindowsInputObserver(callback, hooks, new Idle()));
        service.RequireAvailable(); Assert.Equal(afterStart, await service.StartAsync());
        if (afterStart) { hooks.Dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send); await observer!.Completion; await Until(() => desktop.Snapshot.State == DesktopAccessState.PausedForUser); }
        Assert.False(service.IsMonitoring); Assert.Equal("InputObservationUnavailable", Assert.Throws<DesktopAccessException>(service.RequireAvailable).Code);
        await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AutoPauseReasonIsAdditiveAndInvalidAutomaticScopeCannotChangeRunOrGate()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "legacy command", new("test"))); var lease = await desktop.AcquireAsync(run.Id);
        var command = new PauseRunCommand(Guid.NewGuid(), run.Id, run.RunVersion);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(command)); Assert.False(json.RootElement.TryGetProperty("Reason", out _));
        await Assert.ThrowsAsync<ArgumentException>(() => runs.PauseAsync(command with { Scope = PauseScope.GuiOnly, Reason = PauseReason.AutoPause }));
        await Assert.ThrowsAsync<ArgumentException>(() => runs.PauseAsync(command with { Reason = (PauseReason)99 }));
        Assert.Equal(run.RunVersion, runs.GetRun(run.Id).RunVersion); Assert.True(desktop.IsCurrent(lease)); await desktop.ReleaseAsync(lease);
    }

    [Fact]
    public async Task AutoPauseCheckpointFailureKeepsDesktopClosedWithoutFalseConfirmationOrRetry()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new DelayedPause(new SqliteRunRepository(database)) { Fail = true }; repository.Release.TrySetResult();
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(repository, desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Failed activity checkpoint", new("test"))); var lease = await desktop.AcquireAsync(run.Id);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var hooks = new Hooks(); await using var activity = new UserActivityPauseService(desktop, takeover, NullLogger<UserActivityPauseService>.Instance,
            callback => new WindowsInputObserver(callback, hooks, new Idle()));
        Assert.True(await activity.StartAsync()); hooks.Emit(new(ObservedInputDevice.Mouse, false, false, 0, 0));
        Assert.False(desktop.IsCurrent(lease)); await takeover.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(takeover.Handoff.Confirmed); Assert.Equal("PauseUnconfirmed", takeover.Handoff.Error);
        Assert.Equal(run.RunVersion, (await repository.GetAsync(run.Id))!.Run.RunVersion);
        for (var i = 0; i < 20; i++) hooks.Emit(new(ObservedInputDevice.Mouse, false, false, 0, 0));
        Assert.Equal(1, repository.PauseWrites); await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HeadlessAppCompositionDoesNotStartHooksAndSupportsNormalHostDisposal()
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders()).Build();
        await host.StartAsync();
        var activity = host.Services.GetRequiredService<UserActivityPauseService>();
        Assert.False(activity.IsMonitoring); activity.RequireAvailable();
        await host.StopAsync(); // The real WPF startup is the explicit observation entry point.
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class HeldRuntime(TaskRunCoordinator runs) : IAgentRuntime
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); await runs.WaitAtBoundaryAsync(request.RunId, ct); return new() { Status = AgentResultStatus.Completed, Summary = "Explicit continuation" }; }
    }
    private sealed class Idle : ISystemIdleSource { public SystemIdleSnapshot Read() => new(true, TimeSpan.Zero); }
    private sealed class Hooks : IWindowsActivityHooks
    {
        public bool Fail; public Dispatcher? Dispatcher; private Action<ObservedInputActivity>? observe;
        public IDisposable Install(Action<ObservedInputActivity> callback)
        { if (Fail) throw new InvalidOperationException(); Dispatcher = Dispatcher.CurrentDispatcher; observe = callback; return new Scope(); }
        public void Emit(ObservedInputActivity activity) => observe?.Invoke(activity);
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
    private sealed class Input(Hooks hooks) : IWindowsKeyboardNative, IDisposable
    {
        public int Calls; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public ManualResetEventSlim Release = new();
        public bool IsDesktopAvailable() => true; public bool IsKeyDown(ushort key) => false;
        public InputInsertion Send(NativeInput[] inputs)
        {
            Interlocked.Increment(ref Calls);
            foreach (var input in inputs) hooks.Emit(new(ObservedInputDevice.Keyboard, true, false, 0, input.Data.Keyboard.Extra));
            Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return new((uint)inputs.Length, 0);
        }
        public void Dispose() => Release.Dispose();
    }
    private sealed class DelayedPause(IRunRepository inner) : IRunRepository
    {
        public int PauseWrites; public bool Fail; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken ct = default) => inner.CreateAsync(run, conversation, request, events, ct);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken ct = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, ct);
        public Task<StoredRun?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async Task UpdateAsync(TaskRun run, long version, IReadOnlyList<RunEvent> events, AgentRunResult? result = null, CancellationToken ct = default)
        {
            if (run.Status is RunStatus.Pausing or RunStatus.Paused && run.Reason == nameof(PauseReason.AutoPause))
            { Interlocked.Increment(ref PauseWrites); Entered.TrySetResult(); await Release.Task.WaitAsync(ct); if (Fail) throw new System.IO.IOException("Synthetic checkpoint failure"); }
            await inner.UpdateAsync(run, version, events, result, ct);
        }
    }
}
