using Microsoft.EntityFrameworkCore;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Diagnostics;
using System.IO;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceRecoveryTests
{
    [Fact]
    public async Task PersistenceRecoveryAfterActualProcessKillDoesNotRepeatUnknownWriteOrInvokeRecoveredRuntime()
    {
        using var database = new TemporaryAgentDatabase();
        var effectPath = database.DatabasePath + ".effect";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var child = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { typeof(RecoveryTestProcess.Program).Assembly.Location, database.DatabasePath, effectPath },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        })!;
        var errors = child.StandardError.ReadToEndAsync();
        Guid id;
        try
        {
            string? line;
            do { line = await child.StandardOutput.ReadLineAsync(timeout.Token); }
            while (line is not null && !line.StartsWith("RECOVERY_READY="));
            Assert.NotNull(line);
            id = Guid.Parse(line[15..]);
            Assert.Equal(["effect"], await File.ReadAllLinesAsync(effectPath, timeout.Token));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            var diagnostics = await errors;
            Assert.True(string.IsNullOrEmpty(diagnostics), diagnostics);
        }
        // Process termination releases ownership automatically; this is the same lock used by startup.
        using var ownership = new FileStream(database.DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        IRunRepository repository = new SqliteRunRepository(database);
        Assert.Equal(RunStatus.Running, (await repository.GetAsync(id))!.Run.Status);
        var safeQueue = await new RunRecovery(repository).RecoverAsync(timeout.Token);
        Assert.Empty(safeQueue);
        var recovered = (await repository.GetAsync(id))!;
        Assert.Equal(RunStatus.Interrupted, recovered.Run.Status);
        Assert.Equal("RunInterrupted", recovered.Run.ErrorCode);
        Assert.Contains("Verify external effects", recovered.Run.Summary);
        Assert.Single(recovered.Events, value => value.EventType == RunEventType.ToolRequested);
        Assert.DoesNotContain(recovered.Events, value => value.EventType == RunEventType.ToolExecuted);
        Assert.Empty(await new RunRecovery(repository).RecoverAsync(timeout.Token));
        Assert.Equal(recovered.Events.Count, (await repository.GetAsync(id))!.Events.Count);

        using var coordinator = new TaskRunCoordinator(repository);
        await coordinator.RestoreHistoryAsync(safeQueue, timeout.Token);
        using var stop = new CancellationTokenSource();
        var probe = new RecoveryProbeRuntime(id, new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync));
        var loop = new TaskRunner(coordinator, probe).RunAsync(stop.Token);
        try
        {
            var sentinel = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Fresh local task", new TaskOrigin("test")), timeout.Token);
            await coordinator.WaitForTerminalAsync(sentinel.Id, timeout.Token);
            Assert.Equal(0, probe.RecoveredCalls);
            Assert.Equal(["effect"], await File.ReadAllLinesAsync(effectPath, timeout.Token));
            Assert.Equal(RunStatus.Interrupted, coordinator.GetRun(id).Status);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task PersistenceRecoveryRequeuesOnlyKnownSafeLocalTasksOnceAndBlocksUnknownTools()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        IRunRepository repository = new SqliteRunRepository(database);
        Guid safeId, unsafeId;
        using (var beforeCrash = new TaskRunCoordinator(repository))
        {
            safeId = (await beforeCrash.SubmitAsync(new(Guid.NewGuid(), "Unstarted local task", new TaskOrigin("test")))).Id;
            unsafeId = (await beforeCrash.SubmitAsync(new(Guid.NewGuid(), "Unknown write tool", new TaskOrigin("test"), AllowedToolIds: ["new.write"]))).Id;
        }
        var queued = await new RunRecovery(repository).RecoverAsync();
        Assert.Equal([safeId], queued);
        Assert.Equal(RunStatus.Blocked, (await repository.GetAsync(unsafeId))!.Run.Status);
        var blockedVersion = (await repository.GetAsync(unsafeId))!.Run.RunVersion;
        await new RunRecovery(repository).RecoverAsync();
        Assert.Equal(blockedVersion, (await repository.GetAsync(unsafeId))!.Run.RunVersion);
        using var coordinator = new TaskRunCoordinator(repository);
        await coordinator.RestoreHistoryAsync(queued);
        await coordinator.RestoreHistoryAsync(queued);
        using var stop = new CancellationTokenSource();
        var probe = new RecoveryProbeRuntime(safeId, new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync));
        var loop = new TaskRunner(coordinator, probe).RunAsync(stop.Token);
        try
        {
            Assert.Equal(RunStatus.Completed, (await coordinator.WaitForTerminalAsync(safeId).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            var sentinel = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Sentinel", new TaskOrigin("test")));
            await coordinator.WaitForTerminalAsync(sentinel.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, probe.RecoveredCalls);
            Assert.Equal(RunStatus.Blocked, coordinator.GetRun(unsafeId).Status);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.WaitingForDesktop)]
    [InlineData(RunStatus.WaitingForUser)]
    [InlineData(RunStatus.WaitingForApproval)]
    [InlineData(RunStatus.Pausing)]
    [InlineData(RunStatus.Paused)]
    [InlineData(RunStatus.PausedForUser)]
    [InlineData(RunStatus.Blocked)]
    public async Task PersistenceRecoveryInterruptsEveryStartedNonterminalStateWithoutRequeue(RunStatus interruptedState)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        IRunRepository repository = new SqliteRunRepository(database);
        TaskRun run;
        using (var coordinator = new TaskRunCoordinator(repository))
            run = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Interrupted state fixture", new TaskOrigin("test"), AllowedToolIds: ["fixture.write"]));
        var sequence = 2L;
        await MoveTo(RunStatus.Running);
        if (interruptedState is RunStatus.Paused or RunStatus.PausedForUser) await MoveTo(RunStatus.Pausing);
        if (interruptedState != RunStatus.Running) await MoveTo(interruptedState);
        Assert.Empty(await new RunRecovery(repository).RecoverAsync());
        var recovered = (await repository.GetAsync(run.Id))!;
        Assert.Equal(RunStatus.Interrupted, recovered.Run.Status);
        Assert.Equal("RunInterrupted", recovered.Run.ErrorCode);
        Assert.Null(recovered.Run.PauseScope);
        Assert.NotNull(recovered.Run.EndedAtUtc);
        Assert.Equal(sequence + 1, recovered.Events[^1].Sequence);
        Assert.False(recovered.Events[^1].Payload!.Value.GetProperty("automaticReplay").GetBoolean());

        async Task MoveTo(RunStatus status)
        {
            var next = TaskRunStateMachine.Transition(run, status, run.RunVersion, DateTimeOffset.UtcNow, "Fixture transition");
            await repository.UpdateAsync(next, run.RunVersion, [new RunEvent
            {
                Id = Guid.NewGuid(), RunId = run.Id, Sequence = ++sequence, EventType = RunEventType.StatusChanged,
                TimestampUtc = DateTimeOffset.UtcNow, Message = "Fixture transition"
            }]);
            run = next;
        }
    }

    private sealed class RecoveryProbeRuntime(Guid recoveredId, IAgentRuntime inner) : IAgentRuntime
    {
        public int RecoveredCalls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { if (request.RunId == recoveredId) Interlocked.Increment(ref RecoveredCalls); return inner.ExecuteAsync(request, progress, ct); }
    }
}
