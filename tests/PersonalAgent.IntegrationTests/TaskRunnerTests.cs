using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.IntegrationTests;

public sealed class TaskRunnerTests
{
    [Fact]
    public async Task TaskRunnerDuplicateSubmissionsExecuteExactlyOnceAndKeepOrderedHistory()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var counter = new CountingRuntime(new FakeAgentRuntime(waitAtBoundary: coordinator.WaitAtBoundaryAsync));
        var loop = new TaskRunner(coordinator, counter).RunAsync(stop.Token);
        try
        {
            var command = Submit();
            var submissions = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => coordinator.SubmitAsync(command))));
            var id = Assert.Single(submissions.Select(run => run.Id).Distinct());
            var final = await coordinator.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Completed, final.Status);
            Assert.Equal(1, counter.Calls);
            Assert.Contains("fake", final.Summary);
            var events = coordinator.GetEvents(id);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(n => (long)n), events.Select(e => e.Sequence));
            Assert.Single(events, e => e.EventType == RunEventType.RunStarted);
            Assert.Single(events, e => e.EventType == RunEventType.RunFinished);
            Assert.NotNull(coordinator.GetResult(id));
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task TaskRunnerCancelDuringExecutionWinsOverLateSuccessAndDropsLateProgress()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var runtime = new DelegateRuntime(async (request, progress, _) =>
        {
            entered.SetResult();
            await release.Task;
            progress.Report(new AgentEvent { RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.Completed, Message = "late" });
            return Success();
        });
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        var run = await coordinator.SubmitAsync(Submit());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = coordinator.GetRun(run.Id);
            await coordinator.CancelAsync(new(Guid.NewGuid(), run.Id, current.RunVersion));
            release.SetResult();
            var final = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Cancelled, final.Status);
        }
        finally { release.TrySetResult(); stop.Cancel(); await loop; }
        Assert.DoesNotContain(coordinator.GetEvents(run.Id), e => e.Message == "late");
    }

    [Fact]
    public async Task TaskRunnerTimeoutFailsAndNextTaskStillRuns()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var runtime = new DelegateRuntime(async (request, _, ct) =>
        {
            if (request.Instructions == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success();
        });
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            var timed = await coordinator.SubmitAsync(Submit() with { Instructions = "wait", Limits = new RunLimits { MaxDuration = TimeSpan.FromMilliseconds(50) } });
            var next = await coordinator.SubmitAsync(Submit());
            var failed = await coordinator.WaitForTerminalAsync(timed.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Failed, failed.Status);
            Assert.Equal("RunTimedOut", failed.ErrorCode);
            Assert.Equal(RunStatus.Completed, (await coordinator.WaitForTerminalAsync(next.Id).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskRunnerAcknowledgesPauseAtBoundaryThenResumesOrCancelsSameAttempt(bool cancelPaused)
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var inStep = Signal();
        var finishStep = Signal();
        var reachedBoundary = Signal();
        var secondStep = false;
        var runtime = new DelegateRuntime(async (request, _, ct) =>
        {
            inStep.SetResult();
            await finishStep.Task.WaitAsync(ct);
            var waiting = coordinator.WaitAtBoundaryAsync(request.RunId, ct);
            reachedBoundary.SetResult();
            await waiting;
            secondStep = true;
            return Success();
        });
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        var run = await coordinator.SubmitAsync(Submit());
        try
        {
            await inStep.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = coordinator.GetRun(run.Id);
            var pausing = await coordinator.PauseAsync(new(Guid.NewGuid(), run.Id, current.RunVersion));
            Assert.Equal(RunStatus.Pausing, pausing.Status);
            finishStep.SetResult();
            await reachedBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paused = coordinator.GetRun(run.Id);
            Assert.Equal(RunStatus.Paused, paused.Status);
            Assert.False(secondStep);
            if (cancelPaused) await coordinator.CancelAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            else await coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            var final = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancelPaused ? RunStatus.Cancelled : RunStatus.Completed, final.Status);
            Assert.Equal(run.Id, final.Id);
        }
        finally { finishStep.TrySetResult(); stop.Cancel(); await loop; }
        Assert.Equal(!cancelPaused, secondStep);
    }

    [Fact]
    public async Task TaskRunnerDoesNotInvokeRuntimeForCancelledQueuedItem()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var run = await coordinator.SubmitAsync(Submit());
        await coordinator.CancelAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
        var count = new CountingRuntime(new FakeAgentRuntime());
        var loop = new TaskRunner(coordinator, count).RunAsync(stop.Token);
        // A sentinel task proves that the earlier queue item was consumed.
        try
        {
            var sentinel = await coordinator.SubmitAsync(Submit());
            await coordinator.WaitForTerminalAsync(sentinel.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, count.Calls);
            Assert.Equal(RunStatus.Cancelled, coordinator.GetRun(run.Id).Status);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task TaskRunnerShutdownInterruptsActiveAttemptAndCannotRestartIt()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var runtime = new DelegateRuntime(async (_, _, ct) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Success(); });
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        var run = await coordinator.SubmitAsync(Submit());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        var interrupted = coordinator.GetRun(run.Id);
        Assert.Equal(RunStatus.Interrupted, interrupted.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ResumeAsync(new(Guid.NewGuid(), run.Id, interrupted.RunVersion)));
    }

    [Fact]
    public async Task TaskRunnerRejectsSecondConsumerAndSanitizesRuntimeFailures()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var runtime = new DelegateRuntime((_, _, _) => throw new Exception("provider-secret"));
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new TaskRunner(coordinator, runtime).RunAsync(stop.Token));
            var run = await coordinator.SubmitAsync(Submit());
            var failed = await coordinator.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Failed, failed.Status);
            Assert.Equal("AgentExecutionFailed", failed.ErrorCode);
            Assert.DoesNotContain("secret", failed.Summary);
            Assert.DoesNotContain(coordinator.GetEvents(run.Id), e => e.Message.Contains("secret"));
        }
        finally { stop.Cancel(); await loop; }
    }

    private sealed class CountingRuntime(IAgentRuntime inner) : IAgentRuntime
    {
        public int Calls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.ExecuteAsync(request, progress, ct);
        }
    }

    [Fact]
    public async Task TaskRunnerStreamPreservesToolMetadataAndMatchesRecordedHistory()
    {
        using var coordinator = new TaskRunCoordinator();
        using var stop = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var runtime = new DelegateRuntime((request, progress, _) =>
        {
            progress.Report(new AgentEvent
            {
                RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.ToolCompleted,
                Message = "Synthetic tool metadata only", ToolId = "test.read", ToolCallId = callId
            });
            return Task.FromResult(Success());
        });
        var run = await coordinator.SubmitAsync(Submit());
        var observed = Collect();
        var loop = new TaskRunner(coordinator, runtime).RunAsync(stop.Token);
        try
        {
            var values = await observed.WaitAsync(TimeSpan.FromSeconds(5));
            var toolEvent = Assert.Single(values, value => value.EventType == RunEventType.ToolExecuted);
            Assert.Equal(callId, toolEvent.ToolCallId);
            Assert.Equal("test.read", toolEvent.ToolId);
            Assert.Equal(values, coordinator.GetEvents(run.Id));
        }
        finally { stop.Cancel(); await loop; }

        async Task<List<RunEvent>> Collect()
        {
            var values = new List<RunEvent>();
            await foreach (var value in coordinator.WatchRunAsync(run.Id, stop.Token)) values.Add(value);
            return values;
        }
    }
    private sealed class DelegateRuntime(Func<AgentRequest, IProgress<AgentEvent>, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, progress, ct);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AgentRunResult Success() => new() { Status = AgentResultStatus.Completed, Summary = "Local diagnostic succeeded" };
    private static SubmitTaskCommand Submit() => new(Guid.NewGuid(), "Local diagnostic", new TaskOrigin("desktop"));
}
