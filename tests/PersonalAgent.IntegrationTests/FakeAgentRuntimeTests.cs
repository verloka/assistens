using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.IntegrationTests;

public sealed class FakeAgentRuntimeTests
{
    [Fact]
    public async Task FakeAgentManualRequestProducesOrderedProgressAndFinalResultWithoutCredentials()
    {
        IAgentRuntime runtime = new FakeAgentRuntime([new("Step one"), new("Step two")]);
        var events = new List<AgentEvent>();
        var request = Request();
        var result = await runtime.ExecuteAsync(request, new InlineProgress(events.Add), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status);
        Assert.Equal([AgentEventKind.Started, AgentEventKind.Progress, AgentEventKind.Progress, AgentEventKind.Completed], events.Select(e => e.Kind));
        Assert.Equal(["Step one", "Step two"], events.Where(e => e.Kind == AgentEventKind.Progress).Select(e => e.Message));
        Assert.All(events, e => Assert.Equal(request.RunId, e.RunId));
        Assert.Equal(new AgentUsage(0, 0), result.Usage);
        Assert.Contains("fake", result.Summary);
    }

    [Fact]
    public async Task FakeAgentWaitsAtControlledPauseBoundaryAndCancellationPreventsNextStep()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryCalls = 0;
        var events = new List<AgentEvent>();
        var runtime = new FakeAgentRuntime([new("One"), new("Two")], async (_, ct) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) == 3)
            {
                reached.SetResult();
                await release.Task.WaitAsync(ct);
            }
        });
        using var cancel = new CancellationTokenSource();
        var execution = runtime.ExecuteAsync(Request(), new InlineProgress(events.Add), cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.Single(events, e => e.Kind == AgentEventKind.Progress);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        release.SetResult();
        Assert.Single(events, e => e.Kind == AgentEventKind.Progress);
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Completed);
    }

    [Fact]
    public async Task FakeAgentPauseReleaseContinuesTheSameRun()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAgentRuntime([new("One")], async (_, ct) => { reached.TrySetResult(); await release.Task.WaitAsync(ct); });
        var events = new List<AgentEvent>();
        var execution = runtime.ExecuteAsync(Request(), new InlineProgress(events.Add), CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(events);
        release.SetResult();
        Assert.Equal(AgentResultStatus.Completed, (await execution).Status);
        Assert.Single(events, e => e.Kind == AgentEventKind.Completed);
    }

    [Fact]
    public async Task FakeAgentPreCancelledRequestReportsNoWork()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FakeAgentRuntime().ExecuteAsync(Request(),
            new InlineProgress(_ => Assert.Fail("No work expected")), cancel.Token));
    }

    [Fact]
    public async Task FakeAgentEnforcesLimitsAndNeverPretendsToRunAnotherProfile()
    {
        var events = new List<AgentEvent>();
        var progress = new InlineProgress(events.Add);
        var runtime = new FakeAgentRuntime([new("One"), new("Two")]);
        var limited = await runtime.ExecuteAsync(Request() with { Limits = new RunLimits { MaxSteps = 1 } }, progress, CancellationToken.None);
        Assert.Equal("StepLimitExceeded", limited.ErrorCode);
        var blocked = await runtime.ExecuteAsync(Request() with { ModelProfileId = "real-provider" }, progress, CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, blocked.Status);
        Assert.Equal("FakeProfileRequired", blocked.ErrorCode);
        Assert.Empty(events);

        var waiting = new FakeAgentRuntime(waitAtBoundary: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        var timedOut = await waiting.ExecuteAsync(Request() with { Limits = new RunLimits { MaxDuration = TimeSpan.FromMilliseconds(50) } }, progress, CancellationToken.None);
        Assert.Equal("RunTimedOut", timedOut.ErrorCode);
        Assert.Empty(events);
    }

    [Fact]
    public async Task FakeAgentCanReturnScriptedFailureWithoutClaimingCompletion()
    {
        var runtime = new FakeAgentRuntime(result: new AgentRunResult { Status = AgentResultStatus.Failed, Summary = "Scripted failure", ErrorCode = "DiagnosticError" });
        var events = new List<AgentEvent>();
        var result = await runtime.ExecuteAsync(Request(), new InlineProgress(events.Add), CancellationToken.None);
        Assert.Equal("DiagnosticError", result.ErrorCode);
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Completed);
        Assert.Equal(AgentEventKind.Warning, events.Last().Kind);
    }

    private sealed class InlineProgress(Action<AgentEvent> accept) : IProgress<AgentEvent> { public void Report(AgentEvent value) => accept(value); }
    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Local task", ModelProfileId = "fake" };
}
