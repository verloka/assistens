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
            if (!await coordinator.StartExecutionAsync(runId, linked.Token).ConfigureAwait(false)) return;
            var result = await runtime.ExecuteAsync(execution.Request, new RunProgress(coordinator, runId), linked.Token).ConfigureAwait(false);
            if (result.Status == AgentResultStatus.Completed) await coordinator.WaitAtBoundaryAsync(runId, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            await coordinator.FinishExecutionAsync(runId, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await coordinator.InterruptExecutionAsync(runId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        {
            await coordinator.FinishExecutionAsync(runId, Failure("RunTimedOut", "Task exceeded its duration limit.")).ConfigureAwait(false);
        }
        catch (RunPersistenceException)
        {
            await coordinator.FinishExecutionAsync(runId, Failure("HistorySaveFailed", "Task history could not be saved; execution stopped.")).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Exception text may contain tool/provider secrets. Public result is intentionally bounded.
            await coordinator.FinishExecutionAsync(runId, Failure("AgentExecutionFailed", "Agent execution failed.")).ConfigureAwait(false);
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
            coordinator.RecordProgressAsync(value).GetAwaiter().GetResult();
        }
    }
}
