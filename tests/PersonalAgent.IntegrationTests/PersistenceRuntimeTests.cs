using Microsoft.EntityFrameworkCore;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceRuntimeTests
{
    [Theory]
    [InlineData(AgentResultStatus.Completed, RunStatus.Completed)]
    [InlineData(AgentResultStatus.Failed, RunStatus.Failed)]
    public async Task PersistenceRuntimeReopensCompleteHistoryResultMetadataAndNeutralRequest(AgentResultStatus resultStatus, RunStatus expected)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        IRunRepository repository = new SqliteRunRepository(database);
        Guid id;
        IReadOnlyList<RunEvent> originalEvents;
        var output = new AgentRunResult
        {
            Status = resultStatus, Summary = "Durable diagnostic result", ErrorCode = resultStatus == AgentResultStatus.Failed ? "ScriptedFailure" : null,
            Sources = [new SourceLink("Source", new Uri("https://example.com/test"))],
            Artifacts = [new AgentArtifact("artifact-1", "text/plain", "Diagnostic metadata only")],
            Usage = new AgentUsage(0, 0), Warnings = ["Local fake only"]
        };
        using (var coordinator = new TaskRunCoordinator(repository))
        using (var stop = new CancellationTokenSource())
        {
            var runtime = new FakeAgentRuntime([new("First recorded step"), new("Second recorded step")], async (runId, ct) =>
            {
                var durable = (await repository.GetAsync(runId, ct))!;
                Assert.Equal(RunStatus.Running, durable.Run.Status);
                await coordinator.WaitAtBoundaryAsync(runId, ct);
            }, output);
            var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
            try
            {
                var submitted = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Persist this local task", new TaskOrigin("desktop"), AllowedToolIds: ["test.read"]));
                id = submitted.Id;
                var final = await coordinator.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(expected, final.Status);
                originalEvents = coordinator.GetEvents(id);
                var durable = (await repository.GetAsync(id))!;
                Assert.Equal(JsonSerializer.Serialize(originalEvents), JsonSerializer.Serialize(durable.Events));
                Assert.Equal(final.RunVersion, durable.Run.RunVersion);
            }
            finally { stop.Cancel(); await loop; }
        }
        using var reopened = new TaskRunCoordinator(new SqliteRunRepository(database));
        await reopened.LoadFinishedHistoryAsync();
        Assert.Equal(id, Assert.Single(reopened.GetRuns()).Id);
        Assert.Equal(expected, reopened.GetRun(id).Status);
        Assert.Equal(JsonSerializer.Serialize(originalEvents), JsonSerializer.Serialize(reopened.GetEvents(id)));
        var restored = reopened.GetResult(id)!;
        Assert.Equal(output.Summary, restored.Summary);
        Assert.Equal(output.ErrorCode, restored.ErrorCode);
        Assert.Equal(output.Sources, restored.Sources);
        Assert.Equal(output.Artifacts, restored.Artifacts);
        Assert.Equal(output.Usage, restored.Usage);
        Assert.Equal(output.Warnings, restored.Warnings);
        var stored = (await repository.GetAsync(id))!;
        Assert.Equal(["test.read"], stored.Request.AllowedToolIds);
        Assert.Equal("fake", stored.Request.ModelProfileId);
        Assert.NotNull(stored.Run.StartedAtUtc);
        Assert.NotNull(stored.Run.EndedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistenceRuntimeNeverStartsAgentWithoutCommittedCreationAndStart(bool failCreation)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new FailingRepository(new SqliteRunRepository(database), failCreation);
        using var coordinator = new TaskRunCoordinator(repository);
        using var stop = new CancellationTokenSource();
        var runtime = new CountingRuntime();
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            var command = new SubmitTaskCommand(Guid.NewGuid(), "Do not execute after save failure", new TaskOrigin("desktop"));
            if (failCreation)
            {
                await Assert.ThrowsAsync<RunPersistenceException>(() => coordinator.SubmitAsync(command));
                Assert.Empty(coordinator.GetRuns());
                Assert.Empty(await repository.ListAsync());
            }
            else
            {
                var run = await coordinator.SubmitAsync(command);
                var failed = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(RunStatus.Failed, failed.Status);
                Assert.Equal("HistorySaveFailed", failed.ErrorCode);
                Assert.DoesNotContain(coordinator.GetEvents(run.Id), value => value.EventType == RunEventType.RunStarted);
                Assert.Equal(RunStatus.Failed, (await repository.GetAsync(run.Id))!.Run.Status);
            }
            Assert.Equal(0, runtime.Calls);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task PersistenceRuntimeFailedFinalSaveCannotPublishCompletedOutcome()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new FailingRepository(new SqliteRunRepository(database), false, failCompletion: true);
        using var coordinator = new TaskRunCoordinator(repository);
        using var stop = new CancellationTokenSource();
        var loop = new TaskRunner(coordinator, new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync)).RunAsync(stop.Token);
        try
        {
            var run = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Final save failure", new TaskOrigin("desktop")));
            var final = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunStatus.Failed, final.Status);
            Assert.Equal("HistorySaveFailed", final.ErrorCode);
            Assert.Equal(RunStatus.Failed, (await repository.GetAsync(run.Id))!.Run.Status);
            Assert.Equal(AgentResultStatus.Failed, coordinator.GetResult(run.Id)!.Status);
            Assert.Single(coordinator.GetEvents(run.Id), value => value.EventType == RunEventType.RunFinished);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceRuntimePauseResumeOrCancelKeepsDurableHistoryConsistent(bool cancelPaused)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        IRunRepository repository = new SqliteRunRepository(database);
        using var coordinator = new TaskRunCoordinator(repository);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaries = 0;
        var runtime = new FakeAgentRuntime([new("One"), new("Two")], async (id, ct) =>
        {
            if (Interlocked.Increment(ref boundaries) == 3) { entered.SetResult(); await release.Task.WaitAsync(ct); }
            await coordinator.WaitAtBoundaryAsync(id, ct);
        });
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        var run = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Durable pause", new TaskOrigin("desktop")));
        var observation = Observe();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = coordinator.GetRun(run.Id);
            await coordinator.PauseAsync(new(Guid.NewGuid(), run.Id, current.RunVersion));
            release.SetResult();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paused = coordinator.GetRun(run.Id);
            Assert.Equal(RunStatus.Paused, (await repository.GetAsync(run.Id))!.Run.Status);
            if (cancelPaused) await coordinator.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            else await coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            var final = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            await observation.WaitAsync(TimeSpan.FromSeconds(5));
            var durable = (await repository.GetAsync(run.Id))!;
            Assert.Equal(cancelPaused ? RunStatus.Cancelled : RunStatus.Completed, durable.Run.Status);
            Assert.Equal(final.RunVersion, durable.Run.RunVersion);
            Assert.Equal(JsonSerializer.Serialize(coordinator.GetEvents(run.Id)), JsonSerializer.Serialize(durable.Events));
            if (cancelPaused) Assert.Null(durable.Result);
        }
        finally { release.TrySetResult(); stop.Cancel(); await loop; }

        async Task Observe()
        {
            await foreach (var value in coordinator.WatchRunAsync(run.Id, stop.Token))
                if (value.EventType == RunEventType.RunPaused) acknowledged.TrySetResult();
        }
    }

    private sealed class CountingRuntime : IAgentRuntime
    {
        public int Calls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new AgentRunResult { Status = AgentResultStatus.Completed, Summary = "Unexpected" }); }
    }
    private sealed class FailingRepository(IRunRepository inner, bool failCreation, bool failCompletion = false) : IRunRepository
    {
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken ct = default) =>
            failCreation ? throw new IOException("Injected storage failure") : inner.CreateAsync(run, conversation, request, events, ct);
        public Task UpdateAsync(TaskRun run, long version, IReadOnlyList<RunEvent> events, AgentRunResult? result = null, CancellationToken ct = default) =>
            (failCompletion ? run.Status == RunStatus.Completed : run.Status == RunStatus.Running)
                ? throw new IOException("Injected storage failure") : inner.UpdateAsync(run, version, events, result, ct);
        public Task<StoredRun?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken ct = default) =>
            failCreation ? throw new IOException("Injected storage failure") : inner.GetOrCreateAsync(run, conversation, request, events, identity, ct);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
    }
}
