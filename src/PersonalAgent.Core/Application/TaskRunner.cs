using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Application;

// One session-local queue consumer. Adapters share coordinator step boundaries;
// this loop does not implement native GUI leases or provider-specific replay.
public sealed class TaskRunner(TaskRunCoordinator coordinator, IAgentRuntime runtime)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var reader = coordinator.AttachRunner();
        try
        {
            await foreach (var runId in reader.ReadAllAsync(stoppingToken))
                await ExecuteAsync(runId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { coordinator.DetachRunner(); }
    }

    private async Task ExecuteAsync(Guid runId, CancellationToken stoppingToken)
    {
        var execution = coordinator.GetExecution(runId);
        using var duration = new CancellationTokenSource(execution.Request.Limits.MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, execution.Cancellation, duration.Token);
        try
        {
            if (!await coordinator.StartExecutionAsync(runId, linked.Token)) return;
            var result = await runtime.ExecuteAsync(execution.Request, new RunProgress(coordinator, runId), linked.Token);
            if (result.Status == AgentResultStatus.Completed) await coordinator.WaitAtBoundaryAsync(runId, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            coordinator.FinishExecution(runId, result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            coordinator.InterruptExecution(runId);
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        {
            coordinator.FinishExecution(runId, Failure("RunTimedOut", "Task exceeded its duration limit."));
        }
        catch (Exception)
        {
            // Exception text may contain tool/provider secrets. Public result is intentionally bounded.
            coordinator.FinishExecution(runId, Failure("AgentExecutionFailed", "Agent execution failed."));
        }
        finally { coordinator.ReleaseExecution(runId); }
    }

    private static AgentRunResult Failure(string code, string summary) => new()
    {
        Status = AgentResultStatus.Failed, ErrorCode = code, Summary = summary
    };

    private sealed class RunProgress(TaskRunCoordinator coordinator, Guid runId) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value)
        {
            if (value.RunId != runId) throw new InvalidOperationException("Progress run ID does not match execution.");
            coordinator.RecordProgress(value);
        }
    }
}
