using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.E2ETests;

// Application fake-flow acceptance only; not the ten live Windows/business MVP E2Es.
public sealed class LocalFakeFlowTests
{
    [Theory]
    [InlineData("complete", RunStatus.Completed)]
    [InlineData("cancel", RunStatus.Cancelled)]
    [InlineData("fail", RunStatus.Failed)]
    public async Task SubmitProgressPauseDecisionAndFinalHistoryAreOneConsistentAttempt(string decision, RunStatus expected)
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var reached = Signal();
        var release = Signal();
        var paused = Signal();
        var boundaries = 0;
        var command = new SubmitTaskCommand(Guid.NewGuid(), "Local acceptance task", new TaskOrigin("test-channel", "owner", "update-1"));
        var submissions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => coordinator.SubmitAsync(command))));
        var id = Assert.Single(submissions.Select(run => run.Id).Distinct());
        var observed = ObserveAsync();
        var result = new AgentRunResult
        {
            Status = decision == "fail" ? AgentResultStatus.Failed : AgentResultStatus.Completed,
            Summary = decision == "fail" ? "Scripted local failure" : "Scripted local success",
            ErrorCode = decision == "fail" ? "ScriptedFailure" : null,
            Sources = [new SourceLink("Diagnostic source", new Uri("https://example.com/local-test"))],
            Usage = new AgentUsage(0, 0)
        };
        var runtime = new FakeAgentRuntime([new("Read local value"), new("Prepare answer")], async (runId, ct) =>
        {
            if (Interlocked.Increment(ref boundaries) == 3)
            {
                reached.SetResult();
                await release.Task.WaitAsync(ct);
            }
            await coordinator.WaitAtBoundaryAsync(runId, ct);
        }, result);
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = coordinator.GetRun(id);
            var pause = new PauseRunCommand(Guid.NewGuid(), id, running.RunVersion);
            await coordinator.PauseAsync(pause);
            await coordinator.PauseAsync(pause);
            release.SetResult();
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = coordinator.GetRun(id);
            Assert.Equal(RunStatus.Paused, snapshot.Status);
            Assert.DoesNotContain(coordinator.GetEvents(id), value => value.Message == "Prepare answer");
            if (decision == "cancel") await coordinator.CancelAsync(new(Guid.NewGuid(), id, snapshot.RunVersion));
            else await coordinator.ResumeAsync(new(Guid.NewGuid(), id, snapshot.RunVersion));

            var final = await coordinator.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            var history = await observed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, final.Status);
            Assert.Equal(id, (await coordinator.SubmitAsync(command)).Id);
            Assert.Equal(history, coordinator.GetEvents(id));
            Assert.Equal(Enumerable.Range(1, history.Count).Select(n => (long)n), history.Select(value => value.Sequence));
            Assert.Single(history, value => value.EventType == RunEventType.RunStarted);
            Assert.Single(history, value => value.EventType == RunEventType.RunFinished);
            Assert.NotNull(final.StartedAtUtc);
            Assert.NotNull(final.EndedAtUtc);
            if (decision == "cancel")
            {
                Assert.Null(coordinator.GetResult(id));
                Assert.DoesNotContain(history, value => value.Message == "Prepare answer");
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ResumeAsync(new(Guid.NewGuid(), id, final.RunVersion)));
            }
            else
            {
                Assert.Equal(result.Summary, final.Summary);
                Assert.Equal(result.ErrorCode, final.ErrorCode);
                Assert.Equal(result.Sources, final.Sources);
                Assert.Equal(result.Usage, coordinator.GetResult(id)!.Usage);
            }
        }
        finally { release.TrySetResult(); stop.Cancel(); await loop; }

        async Task<List<RunEvent>> ObserveAsync()
        {
            var history = new List<RunEvent>();
            await foreach (var value in coordinator.WatchRunAsync(id, stop.Token))
            {
                history.Add(value);
                if (value.EventType == RunEventType.RunPaused) paused.TrySetResult();
            }
            return history;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
