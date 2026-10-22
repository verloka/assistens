using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.IntegrationTests;

public sealed class TakeoverStressTests
{
    public static IEnumerable<object[]> InputCases => from operation in new[] { "click", "type", "chord" }
        from outcome in new[] { "success", "partial", "throw" } from cancel in new[] { false, true } select new object[] { operation, outcome, cancel };

    [Theory]
    [MemberData(nameof(InputCases))]
    public async Task RepeatedTakeoverKeepsQueuedInputStaleReleasesModifiersAndIsolatesOverlappingRuns(string operation, string outcome, bool cancel)
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop);
        using var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic repeated takeover", new("stress-test")));
            var lease = await desktop.AcquireAsync(run.Id); using var native = new InputProbe(operation, outcome);
            var keyboard = new WindowsKeyboardInput(desktop, lease, native, _ => true);
            var frame = ScreenshotFixture.Create(lease.Generation); var mouse = new WindowsMouseInput(desktop, lease, native, () => frame.Monitors);
            using var inputStop = new CancellationTokenSource(); using var waitStop = new CancellationTokenSource();
            var first = operation switch {
                "click" => mouse.ClickAsync(frame, new(-3, -2), MouseButton.Left, inputStop.Token),
                "type" => keyboard.TypeAsync(string.Concat(Enumerable.Repeat("Тест 🙂 ", 100)), lease.Generation, inputStop.Token),
                _ => keyboard.PressKeyAsync(new("Ctrl+Alt+Shift+A"), lease.Generation, inputStop.Token)
            };
            Task[] queued = []; Task handoff = Task.CompletedTask; Task<TaskRun>? cancellation = null;
            try
            {
                await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotEmpty(native.Held);
                queued = Enumerable.Range(0, 12).Select(_ => keyboard.TypeAsync("queued", lease.Generation)).ToArray();
                var nextId = Guid.NewGuid(); var waiting = desktop.WaitAcquireAsync(nextId, waitStop.Token);
                if (cancel)
                {
                    cancellation = runs.CancelAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
                    handoff = service.TakeControlAsync(run.Id); inputStop.Cancel();
                }
                else handoff = iteration % 2 == 0 ? service.AutoPauseForUserActivity() : service.PauseForDesktopEnvironmentChange();
                Assert.False(desktop.IsCurrent(lease)); Assert.False(waiting.IsCompleted);
                native.Release.Set(); await ObserveInput(first);
                await Task.WhenAll(queued.Select(async task => await Assert.ThrowsAsync<ComputerInputException>(() => task)));
                if (cancellation is not null) Assert.Equal(RunStatus.Cancelled, (await cancellation).Status);
                await handoff.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(native.Held); Assert.False(desktop.Snapshot.InputCleanupUncertain);
                Assert.Equal(outcome == "success" ? 1 : 2, native.Calls); Assert.False(waiting.IsCompleted);
                Assert.Equal(DesktopAccessState.PausedForUser, desktop.Snapshot.State);
                if (!cancel)
                {
                    Assert.True(service.Handoff.Confirmed);
                    var stored = (await repository.GetAsync(run.Id))!;
                    Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
                    await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
                }
                else desktop.ResumeRun(run.Id); // Explicit test-owner consent for the OTHER run, never resuming a cancelled task.
                var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(next.Generation > lease.Generation);
                var count = native.Calls;
                var oldDriver = new WindowsKeyboardInput(desktop, lease, native);
                await Assert.ThrowsAsync<ComputerInputException>(() => oldDriver.TypeAsync("stale", lease.Generation));
                Assert.Equal(count, native.Calls); Assert.True(desktop.IsCurrent(next));
                var freshDriver = new WindowsKeyboardInput(desktop, next, native, _ => true);
                await freshDriver.PressKeyAsync(new("Ctrl+A"), next.Generation); Assert.Empty(native.Held);
                Assert.True(await desktop.ReleaseAsync(next)); Assert.False(await desktop.ReleaseAsync(lease));
                if (!cancel) await runs.CancelAsync(new(Guid.NewGuid(), run.Id, runs.GetRun(run.Id).RunVersion));
            }
            finally
            {
                native.Release.Set(); waitStop.Cancel(); await ObserveInput(first);
                foreach (var task in queued) await ObserveInput(task);
                if (cancellation is not null) await cancellation;
                await handoff;
            }
        }
    }

    private static async Task ObserveInput(Task task)
    { try { await task; } catch (ComputerInputException error) { if (error.Requested > 0) Assert.True(error.CleanupSucceeded, error.Code); } catch (OperationCanceledException) { } }

    private sealed class InputProbe(string operation, string outcome) : IWindowsKeyboardNative, IDisposable
    {
        public int Calls; public HashSet<string> Held = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public ManualResetEventSlim Release = new();
        public bool IsDesktopAvailable() => true; public bool IsKeyDown(ushort key) => false;
        public InputInsertion Send(NativeInput[] inputs)
        {
            if (++Calls != 1) { Apply(inputs); return new((uint)inputs.Length, 0); }
            var prefix = operation == "click" ? 2 : 1; Apply(inputs.Take(prefix)); Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (outcome == "throw") throw new InvalidOperationException("Synthetic unknown native outcome");
            if (outcome == "partial") return new((uint)prefix, 0);
            Apply(inputs.Skip(prefix)); return new((uint)inputs.Length, 0);
        }
        private void Apply(IEnumerable<NativeInput> inputs)
        {
            foreach (var input in inputs)
            {
                if (input.Type == 0 && input.Data.Mouse.Flags is not (2 or 4 or 8 or 16 or 32 or 64)) continue;
                var up = input.Type == 1 ? (input.Data.Keyboard.Flags & 2) != 0 : input.Data.Mouse.Flags is 4 or 16 or 64;
                var key = input.Type == 1 ? $"key:{input.Data.Keyboard.VirtualKey}:{input.Data.Keyboard.Scan}:{input.Data.Keyboard.Flags & ~2u}" : $"mouse:{(up ? input.Data.Mouse.Flags >> 1 : input.Data.Mouse.Flags)}";
                if (up) Held.Remove(key); else Held.Add(key);
            }
        }
        public void Dispose() => Release.Dispose();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LocalTakeoverFromOldGenerationCannotOverrideLaterConsentEvenIfTheSameRunIsPausedAgain(bool pauseAgain)
    {
        var desktop = new DesktopAccessCoordinator(_ => true); using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Generation-bound local takeover", new("test")));
        var old = await desktop.AcquireAsync(run.Id); desktop.TakeControl(); var version = desktop.Snapshot.PauseVersion;
        desktop.ResumeRun(run.Id); var fresh = await desktop.AcquireAsync(run.Id);
        if (pauseAgain) desktop.TakeControl();
        var rejected = await Assert.ThrowsAsync<DesktopAccessException>(() => runs.PauseForLocalTakeoverAsync(run.Id, version, old.Generation, PauseReason.AutoPause));
        Assert.Equal("ActivityTakeoverSuperseded", rejected.Code); Assert.Null(runs.GetRun(run.Id).PauseScope);
        if (pauseAgain)
        {
            await runs.PauseForLocalTakeoverAsync(run.Id, desktop.Snapshot.PauseVersion, fresh.Generation, PauseReason.AutoPause);
            Assert.Equal(nameof(PauseReason.AutoPause), runs.GetRun(run.Id).Reason);
        }
        else Assert.True(desktop.IsCurrent(fresh));
        await desktop.ReleaseAsync(fresh);
    }

    [Fact]
    public async Task RepeatedTakeoverWhileActivityPauseWaitsForStorageStillConfirmsSameGeneration()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new Repository(new SqliteRunRepository(database)); var desktop = new DesktopAccessCoordinator(_ => true);
        using var runs = new TaskRunCoordinator(repository, desktop);
        using var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var owner = await runs.SubmitAsync(new(Guid.NewGuid(), "Activity takeover fixture", new("test")));
        var other = await runs.SubmitAsync(new(Guid.NewGuid(), "Unrelated write fixture", new("test")));
        var lease = await desktop.AcquireAsync(owner.Id); repository.HoldNext = 1;
        var writing = runs.PauseAsync(new(Guid.NewGuid(), other.Id, other.RunVersion));
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = service.AutoPauseForUserActivity();
        try
        {
            for (var i = 0; i < 100; i++) Assert.Same(pause, service.TakeControlAsync(owner.Id));
            Assert.False(desktop.IsCurrent(lease)); repository.Release.TrySetResult();
            await writing; await pause.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Handoff.Confirmed);
            var stored = (await repository.GetAsync(owner.Id))!;
            Assert.Equal(nameof(PauseReason.AutoPause), stored.Run.Reason);
            Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
        }
        finally { repository.Release.TrySetResult(); await writing; await pause; }
    }

    [Fact]
    public async Task NewOwnerTakeoverCannotBeLostBehindPreviousGenerationHandoff()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new Repository(new SqliteRunRepository(database)); using var desktop = new SnapshotBarrier(repository);
        using var runs = new TaskRunCoordinator(repository, desktop);
        using var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var firstRun = await runs.SubmitAsync(new(Guid.NewGuid(), "First handoff", new("test")));
        var nextRun = await runs.SubmitAsync(new(Guid.NewGuid(), "Next owner", new("test")));
        var old = await desktop.AcquireAsync(firstRun.Id); var first = service.AutoPauseForUserActivity();
        Task? next = null;
        try
        {
            await desktop.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // Confirmation committed; old worker has not published/completed.
            var paused = runs.GetRun(firstRun.Id); await runs.ResumeAsync(new(Guid.NewGuid(), firstRun.Id, paused.RunVersion));
            var current = await desktop.AcquireAsync(nextRun.Id); Assert.True(current.Generation > old.Generation);
            next = service.AutoPauseForUserActivity(); Assert.False(desktop.IsCurrent(current));
            Assert.NotSame(first, next); Assert.Equal(nextRun.Id, service.Handoff.RunId); Assert.False(service.Handoff.Confirmed);
            desktop.Release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5)); await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Handoff.Confirmed); Assert.Equal(nextRun.Id, service.Handoff.RunId); Assert.Equal(current.Generation, service.Handoff.Generation);
            var stored = (await repository.GetAsync(nextRun.Id))!;
            Assert.Equal(nameof(PauseReason.AutoPause), stored.Run.Reason);
            Assert.Equal(current.Generation, Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed).Payload!.Value.GetProperty("Generation").GetInt64());
        }
        finally { desktop.Release.Set(); await first; if (next is not null) await next; }
    }

    private sealed class Repository(IRunRepository inner) : IRunRepository
    {
        public int HoldNext; public volatile bool ConfirmationCommitted;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken ct = default) => inner.CreateAsync(run, conversation, request, events, ct);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken ct = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, ct);
        public Task<StoredRun?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async Task UpdateAsync(TaskRun run, long version, IReadOnlyList<RunEvent> events, AgentRunResult? result = null, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref HoldNext, 0) == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await inner.UpdateAsync(run, version, events, result, ct);
            if (events.Any(e => e.EventType == RunEventType.DesktopPauseConfirmed)) ConfirmationCommitted = true;
        }
    }
    private sealed class SnapshotBarrier(Repository repository) : IDesktopAccessCoordinator, IDisposable
    {
        private readonly DesktopAccessCoordinator inner = new(_ => true); private int held;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public ManualResetEventSlim Release = new();
        public DesktopAccessSnapshot Snapshot
        {
            get
            {
                var snapshot = inner.Snapshot;
                if (repository.ConfirmationCommitted && Interlocked.CompareExchange(ref held, 1, 0) == 0)
                { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                return snapshot;
            }
        }
        public Task<DesktopLease> AcquireAsync(Guid id, CancellationToken ct = default) => inner.AcquireAsync(id, ct);
        public Task<DesktopLease> WaitAcquireAsync(Guid id, CancellationToken ct = default) => inner.WaitAcquireAsync(id, ct);
        public bool IsCurrent(DesktopLease lease) => inner.IsCurrent(lease);
        public IDisposable EnterAtomic(DesktopLease lease) => inner.EnterAtomic(lease);
        public Task RevokeAsync(DesktopLease lease) => inner.RevokeAsync(lease);
        public Task PauseRunAsync(Guid id) => inner.PauseRunAsync(id);
        public void ResumeRun(Guid id) => inner.ResumeRun(id);
        public long ValidateResumeRun(Guid id) => inner.ValidateResumeRun(id);
        public bool TryResumeRun(Guid id, long version) => inner.TryResumeRun(id, version);
        public Task CancelRunAsync(Guid id) => inner.CancelRunAsync(id);
        public Guid? TakeControl() => inner.TakeControl();
        public Guid? TakeControlIfOwned() => inner.TakeControlIfOwned();
        public void ReportInputCleanupFailure(DesktopLease lease) => inner.ReportInputCleanupFailure(lease);
        public void RestoreInputCleanupFailure(Guid id) => inner.RestoreInputCleanupFailure(id);
        public Task<DesktopAccessSnapshot> ReleasePausedRunAsync(Guid id, CancellationToken ct = default) => inner.ReleasePausedRunAsync(id, ct);
        public Task<bool> ReleaseAsync(DesktopLease lease, CancellationToken ct = default) => inner.ReleaseAsync(lease, ct);
        public void Dispose() => Release.Dispose();
    }
}
