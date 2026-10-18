using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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

namespace PersonalAgent.IntegrationTests;

public sealed class SafePauseTests
{
    [Fact]
    public async Task HandoffWaitsForOwnedModifierCleanupAndDurableCheckpointWithNoQueuedInput()
    {
        using var fixture = await Fixture.CreateAsync();
        using var native = new NativeProbe();
        var keyboard = new WindowsKeyboardInput(fixture.Desktop, fixture.Lease, native, _ => true);
        var input = keyboard.PressKeyAsync(new("Ctrl+A"), fixture.Lease.Generation);
        try
        {
            await native.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains((ushort)17, native.Held);
            fixture.Model.TakeControlCommand.Execute(null);
            var completion = fixture.Service.Completion;
            Assert.False(fixture.Desktop.IsCurrent(fixture.Lease));
            Assert.False(completion.IsCompleted); Assert.False(fixture.Service.Handoff.Confirmed);
            Assert.Equal("Pausing", fixture.Model.Status);
            Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
            Assert.False(fixture.Model.ResumeCommand.CanExecute(null));
            var repeated = fixture.Service.TakeControlAsync();
            Assert.Same(completion, repeated); // One pending local handoff, no duplicate checkpoint.
            var queued = keyboard.TypeAsync("must not type", fixture.Lease.Generation);
            native.ReleaseCleanup.Set();
            var error = await Assert.ThrowsAsync<ComputerInputException>(() => input);
            Assert.True(error.CleanupSucceeded);
            await Assert.ThrowsAsync<ComputerInputException>(() => queued);
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Model.TakeControlCommand.ExecutionTask!;
            fixture.Model.Refresh();
            Assert.Empty(native.Held); Assert.Equal(2, native.Calls);
            Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
            Assert.Equal(fixture.Run.Id, fixture.Desktop.Snapshot.HumanControlOwnerRunId);
            Assert.True(fixture.Service.Handoff.Confirmed);
            Assert.Contains("Компьютер твой", fixture.Model.Message);
            var stored = (await fixture.Repository.GetAsync(fixture.Run.Id))!;
            var checkpoint = Assert.Single(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
            Assert.True(checkpoint.Payload!.Value.GetProperty("InputReleased").GetBoolean());
            Assert.Equal(fixture.Lease.Generation, checkpoint.Payload.Value.GetProperty("Generation").GetInt64());
            Assert.Equal(Enumerable.Range(1, stored.Events.Count).Select(n => (long)n), stored.Events.Select(e => e.Sequence));
            await fixture.Runs.ConfirmDesktopPauseAsync(fixture.Run.Id);
            Assert.Single(fixture.Runs.GetEvents(fixture.Run.Id), e => e.EventType == RunEventType.DesktopPauseConfirmed);
        }
        finally { native.ReleaseCleanup.Set(); }
    }

    [Fact]
    public async Task ReleasedLeaseAloneCannotConfirmHandoffWhileSqliteCheckpointIsPending()
    {
        using var fixture = await Fixture.CreateAsync(hold: true);
        var completion = fixture.Service.TakeControlAsync();
        try
        {
            await fixture.Repository.ConfirmationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
            Assert.False(fixture.Service.Handoff.Confirmed);
            fixture.Model.Refresh();
            Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
            Assert.False(fixture.Model.ResumeCommand.CanExecute(null));
            Assert.DoesNotContain((await fixture.Repository.GetAsync(fixture.Run.Id))!.Events,
                e => e.EventType == RunEventType.DesktopPauseConfirmed);
        }
        finally { fixture.Repository.ReleaseConfirmation.TrySetResult(); }
        await completion.WaitAsync(TimeSpan.FromSeconds(5)); fixture.Model.Refresh();
        Assert.True(fixture.Service.Handoff.Confirmed);
        Assert.Contains("Компьютер твой", fixture.Model.Message);
        Assert.Contains((await fixture.Repository.GetAsync(fixture.Run.Id))!.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
    }

    [Fact]
    public async Task CheckpointStorageFailureNeverClaimsHandoffOrReopensDesktop()
    {
        using var fixture = await Fixture.CreateAsync(fail: true);
        await fixture.Service.TakeControlAsync(); fixture.Model.Refresh();
        Assert.False(fixture.Service.Handoff.Confirmed); Assert.Equal("Error", fixture.Model.Status);
        Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
        Assert.False(fixture.Model.ResumeCommand.CanExecute(null));
        Assert.Equal(1, fixture.Repository.ConfirmationWrites);
        Assert.DoesNotContain((await fixture.Repository.GetAsync(fixture.Run.Id))!.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
        await Assert.ThrowsAsync<DesktopAccessException>(() => fixture.Desktop.AcquireAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrLockedCleanupIsLatchedAndDurablyBlockedInsteadOfConfirmed(bool locked)
    {
        using var fixture = await Fixture.CreateAsync();
        using var native = new NativeProbe { FailCleanup = !locked, LockCleanup = locked };
        var keyboard = new WindowsKeyboardInput(fixture.Desktop, fixture.Lease, native, _ => true);
        var input = keyboard.PressKeyAsync(new("Ctrl+A"), fixture.Lease.Generation);
        try
        {
            await native.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completion = fixture.Service.TakeControlAsync();
            native.ReleaseCleanup.Set();
            var error = await Assert.ThrowsAsync<ComputerInputException>(() => input);
            Assert.False(error.CleanupSucceeded);
            await completion.WaitAsync(TimeSpan.FromSeconds(5)); fixture.Model.Refresh();
            Assert.NotEmpty(native.Held); Assert.True(fixture.Desktop.Snapshot.InputCleanupUncertain);
            Assert.False(fixture.Service.Handoff.Confirmed); Assert.Equal("Error", fixture.Model.Status);
            Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
            Assert.False(fixture.Model.ResumeCommand.CanExecute(null));
            Assert.Throws<DesktopAccessException>(() => fixture.Desktop.ResumeRun(fixture.Run.Id));
            var rejected = await Assert.ThrowsAsync<DesktopAccessException>(() => fixture.Desktop.AcquireAsync(Guid.NewGuid()));
            Assert.Equal("InputCleanupUncertain", rejected.Code);
            var stored = (await fixture.Repository.GetAsync(fixture.Run.Id))!;
            Assert.Equal(RunStatus.Blocked, stored.Run.Status); Assert.Equal("InputCleanupUncertain", stored.Run.ErrorCode);
            Assert.DoesNotContain(stored.Events, e => e.EventType == RunEventType.DesktopPauseConfirmed);
            Assert.Contains("manual review", stored.Run.Reason);
            Assert.Empty(await new RunRecovery(fixture.Repository).RecoverAsync());
            var restoredDesktop = new DesktopAccessCoordinator(_ => true);
            using var restored = new TaskRunCoordinator(fixture.Repository, restoredDesktop);
            await restored.RestoreHistoryAsync([]);
            Assert.True(restoredDesktop.Snapshot.InputCleanupUncertain);
            Assert.Equal("InputCleanupUncertain", restored.GetRun(fixture.Run.Id).ErrorCode);
            await Assert.ThrowsAsync<DesktopAccessException>(() => restoredDesktop.AcquireAsync(Guid.NewGuid()));
        }
        finally { native.ReleaseCleanup.Set(); }
    }

    [Fact]
    public async Task ConfirmationWithoutPausedRunCannotRevokeOrReleaseActiveGrant()
    {
        using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runs.ConfirmDesktopPauseAsync(fixture.Run.Id));
        Assert.True(fixture.Desktop.IsCurrent(fixture.Lease));
        Assert.DoesNotContain(fixture.Runs.GetEvents(fixture.Run.Id), e => e.EventType == RunEventType.DesktopPauseConfirmed);
    }

    [Fact]
    public async Task CancellationDuringFailedCleanupRetainsUncertaintyAcrossDatabaseReopen()
    {
        using var fixture = await Fixture.CreateAsync();
        using var native = new NativeProbe { FailCleanup = true };
        var input = new WindowsKeyboardInput(fixture.Desktop, fixture.Lease, native, _ => true)
            .PressKeyAsync(new("Ctrl+A"), fixture.Lease.Generation);
        try
        {
            await native.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = fixture.Runs.CancelAsync(new(Guid.NewGuid(), fixture.Run.Id, fixture.Run.RunVersion));
            Assert.False(cancel.IsCompleted);
            native.ReleaseCleanup.Set();
            await Assert.ThrowsAsync<ComputerInputException>(() => input);
            var cancelled = await cancel.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Cancelled, cancelled.Status); Assert.Equal("InputCleanupUncertain", cancelled.ErrorCode);
            var restoredDesktop = new DesktopAccessCoordinator(_ => true);
            using var restored = new TaskRunCoordinator(fixture.Repository, restoredDesktop);
            await restored.RestoreHistoryAsync([]);
            Assert.True(restoredDesktop.Snapshot.InputCleanupUncertain);
            Assert.Equal("InputCleanupUncertain", restored.GetRun(cancelled.Id).ErrorCode);
            await Assert.ThrowsAsync<DesktopAccessException>(() => restoredDesktop.AcquireAsync(Guid.NewGuid()));
            await fixture.Service.TakeControlAsync(); fixture.Model.Refresh();
            Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
        }
        finally { native.ReleaseCleanup.Set(); }
    }

    [Fact]
    public async Task CheckpointForOldGenerationDoesNotSubstituteForNewPause()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Service.TakeControlAsync();
        fixture.Desktop.ResumeRun(fixture.Run.Id);
        var fresh = await fixture.Desktop.AcquireAsync(fixture.Run.Id);
        await fixture.Service.TakeControlAsync();
        var checkpoints = (await fixture.Repository.GetAsync(fixture.Run.Id))!.Events
            .Where(e => e.EventType == RunEventType.DesktopPauseConfirmed).ToArray();
        Assert.Equal(2, checkpoints.Length);
        Assert.Equal(fresh.Generation, checkpoints[^1].Payload!.Value.GetProperty("Generation").GetInt64());
        Assert.True(fresh.Generation > fixture.Lease.Generation);
    }

    [Fact]
    public async Task ResumeThroughAnotherUiCannotLeaveHumanHandoffClaimVisible()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Service.TakeControlAsync(); fixture.Model.Refresh();
        Assert.True(fixture.Model.IsControlConfirmed);
        var paused = fixture.Runs.GetRun(fixture.Run.Id);
        await fixture.Runs.ResumeAsync(new(Guid.NewGuid(), paused.Id, paused.RunVersion));
        await fixture.Desktop.AcquireAsync(paused.Id);
        fixture.Model.Refresh();
        Assert.False(fixture.Model.IsControlConfirmed);
        Assert.DoesNotContain("Компьютер твой", fixture.Model.Message);
    }

    private sealed class NativeProbe : IWindowsKeyboardNative, IDisposable
    {
        public readonly TaskCompletionSource CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ReleaseCleanup = new();
        public HashSet<ushort> Held = [];
        public bool FailCleanup, LockCleanup;
        public int Calls;
        public bool IsKeyDown(ushort key) => false;
        public bool IsDesktopAvailable()
        {
            if (LockCleanup && Calls == 1)
            {
                CleanupEntered.TrySetResult();
                if (!ReleaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return false;
            }
            return true;
        }
        public InputInsertion Send(NativeInput[] batch)
        {
            Calls++;
            if (Calls == 1) { Held.Add(batch[0].Data.Keyboard.VirtualKey); return new(1, 0); }
            CleanupEntered.TrySetResult();
            if (!ReleaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (FailCleanup) return new(0, 5);
            foreach (var input in batch) { Assert.True((input.Data.Keyboard.Flags & 2) != 0); Held.Remove(input.Data.Keyboard.VirtualKey); }
            return new((uint)batch.Length, 0);
        }
        public void Dispose() => ReleaseCleanup.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        public required TemporaryAgentDatabase Database;
        public required RepositoryProbe Repository;
        public required DesktopAccessCoordinator Desktop;
        public required TaskRunCoordinator Runs;
        public required TaskRun Run;
        public required DesktopLease Lease;
        public required TakeControlHotkeyService Service;
        public required ControlPanelViewModel Model;
        public required IHost Host;
        public static async Task<Fixture> CreateAsync(bool hold = false, bool fail = false)
        {
            var database = new TemporaryAgentDatabase();
            await using (var db = database.Open()) await db.Database.MigrateAsync();
            var repository = new RepositoryProbe(new SqliteRunRepository(database)) { Hold = hold, Fail = fail };
            var desktop = new DesktopAccessCoordinator(_ => true);
            var runs = new TaskRunCoordinator(repository, desktop);
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Safe pause fixture", new("test")));
            var lease = await desktop.AcquireAsync(run.Id);
            var service = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().Build();
            return new()
            {
                Database = database, Repository = repository, Desktop = desktop, Runs = runs, Run = run,
                Lease = lease, Service = service, Host = host,
                Model = new(runs, runs, desktop, service, (IHostApplicationLifetime)host.Services.GetService(typeof(IHostApplicationLifetime))!)
            };
        }
        public void Dispose() { Repository.ReleaseConfirmation.TrySetResult(); Service.Dispose(); Runs.Dispose(); Host.Dispose(); Database.Dispose(); }
    }

    private sealed class RepositoryProbe(IRunRepository inner) : IRunRepository
    {
        public bool Hold, Fail;
        public int ConfirmationWrites;
        public readonly TaskCompletionSource ConfirmationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken = default) => inner.CreateAsync(run, conversation, request, events, cancellationToken);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken cancellationToken = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, cancellationToken);
        public Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public async Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents, AgentRunResult? result = null, CancellationToken cancellationToken = default)
        {
            if (newEvents.Any(e => e.EventType == RunEventType.DesktopPauseConfirmed))
            {
                ConfirmationWrites++; ConfirmationEntered.TrySetResult();
                if (Hold) await ReleaseConfirmation.Task.WaitAsync(cancellationToken);
                if (Fail) throw new System.IO.IOException("synthetic checkpoint failure");
            }
            await inner.UpdateAsync(run, expectedVersion, newEvents, result, cancellationToken);
        }
    }
}
