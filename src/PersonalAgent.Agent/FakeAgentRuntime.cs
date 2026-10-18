using PersonalAgent.Core.Agents;

namespace PersonalAgent.Agent;

public sealed record FakeAgentStep(string Message, TimeSpan Delay = default);

// Local diagnostic adapter. It never calls a model, tool, desktop or network.
public sealed class FakeAgentRuntime : IAgentRuntime
{
    private readonly FakeAgentStep[] steps;
    private readonly Func<Guid, CancellationToken, Task> boundary;
    private readonly AgentRunResult result;

    public FakeAgentRuntime(IEnumerable<FakeAgentStep>? steps = null,
        Func<Guid, CancellationToken, Task>? waitAtBoundary = null, AgentRunResult? result = null)
    {
        this.steps = (steps ?? [new("Read local task"), new("Prepare local result")]).ToArray();
        if (this.steps.Any(step => string.IsNullOrWhiteSpace(step.Message) || step.Delay < TimeSpan.Zero))
            throw new ArgumentException("Steps require a public message and a nonnegative delay.", nameof(steps));
        boundary = waitAtBoundary ?? ((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        this.result = result ?? new AgentRunResult
        {
            Status = AgentResultStatus.Completed,
            Summary = "Local fake task completed; no external actions performed.",
            Usage = new AgentUsage(0, 0)
        };
    }

    public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Limits.Validate();
        if (request.ModelProfileId != "fake")
            return Failure("FakeProfileRequired", "Select the fake profile to run a local diagnostic.", AgentResultStatus.Blocked);
        if (steps.Length > request.Limits.MaxSteps)
            return Failure("StepLimitExceeded", "Fake script exceeds the configured step limit.");

        using var duration = new CancellationTokenSource(request.Limits.MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, duration.Token);
        var ct = linked.Token;
        try
        {
            await BoundaryAsync();
            Report(AgentEventKind.Started, "Local fake task started");
            foreach (var step in steps)
            {
                await BoundaryAsync();
                Report(AgentEventKind.Progress, step.Message);
                if (step.Delay > TimeSpan.Zero) await Task.Delay(step.Delay, ct);
            }
            await BoundaryAsync();
            Report(result.Status == AgentResultStatus.Completed ? AgentEventKind.Completed : AgentEventKind.Warning, result.Summary);
            return result;
        }
        catch (OperationCanceledException) when (duration.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failure("RunTimedOut", "Local fake task exceeded its duration limit.");
        }

        async Task BoundaryAsync()
        {
            ct.ThrowIfCancellationRequested();
            await boundary(request.RunId, ct);
            ct.ThrowIfCancellationRequested();
        }
        void Report(AgentEventKind kind, string message) => progress.Report(new AgentEvent
        {
            RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = kind, Message = message
        });
    }

    private static AgentRunResult Failure(string code, string message, AgentResultStatus status = AgentResultStatus.Failed) =>
        new() { Status = status, ErrorCode = code, Summary = message };
}
