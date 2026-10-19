using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.WindowsSmoke;

namespace PersonalAgent.IntegrationTests;

public sealed class AttendedPauseTests
{
    [Fact]
    public async Task DurableHandoffInPausingStateReachesPauseBoundaryAndAllowsExplicitResumeWithoutDeadlock()
    {
        var desktop = new DesktopAccessCoordinator(_ => true); // Synthetic grant only; no native capture/input.
        using var runs = new TaskRunCoordinator(desktopAccess: desktop);
        using var takeover = new TakeControlHotkeyService(desktop, runs, NullLogger<TakeControlHotkeyService>.Instance);
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic attended hold", new("test")));
        // Standalone coordinator still has a queued run. Enter Running through its actual runner.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var runner = new TaskRunner(runs, new Runtime(async (request, ct) =>
        {
            var lease = await desktop.AcquireAsync(request.RunId, ct); entered.TrySetResult();
            await continueTool.Task.WaitAsync(ct);
            await AttendedPause.WaitForResumeAsync(runs, request.RunId, () => ready.TrySetResult(), ct);
            return new() { Status = AgentResultStatus.Completed, Summary = "Explicit attended resume completed" };
        })).RunAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await takeover.TakeControlAsync(run.Id);
            Assert.True(takeover.Handoff.Confirmed);
            Assert.Equal(RunStatus.Pausing, runs.GetRun(run.Id).Status);
            Assert.True(AttendedPause.Acknowledged(runs.GetRun(run.Id), takeover.Handoff.Confirmed));
            Assert.False(AttendedPause.Acknowledged(runs.GetRun(run.Id), false));
            continueTool.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paused = runs.GetRun(run.Id);
            Assert.True(paused.Status is RunStatus.Paused or RunStatus.PausedForUser);
            await runs.ResumeAsync(new(Guid.NewGuid(), run.Id, paused.RunVersion));
            Assert.Equal(RunStatus.Completed, (await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally { stop.Cancel(); await runner; }
    }
    private sealed class Runtime(Func<AgentRequest, CancellationToken, Task<AgentRunResult>> execute) : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct) => execute(request, ct);
    }
}
