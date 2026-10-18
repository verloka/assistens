using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Agents;

public interface IAgentRuntime
{
    // Implementations observe cancellation before starting another model/tool step.
    // Progress contains user-visible operations, never hidden model reasoning.
    Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken);
}

public sealed record RunLimits
{
    public int MaxSteps { get; init; } = 50;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
    public long MaxTokens { get; init; } = 20_000;

    public void Validate()
    {
        if (MaxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSteps));
        if (MaxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxDuration));
        if (MaxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(MaxTokens));
    }
}

// Provider-neutral context; opaque SDK sessions stay inside adapters.
public sealed record AgentContext(string? Summary = null, IReadOnlyList<SourceLink>? Sources = null);

public sealed record AgentRequest
{
    public required Guid RunId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string Instructions { get; init; }
    public required string ModelProfileId { get; init; }
    public IReadOnlyList<string> AllowedToolIds { get; init; } = [];
    public RunLimits Limits { get; init; } = new();
    public AgentContext Context { get; init; } = new();
}

public enum AgentResultStatus { Completed, Failed, Cancelled, Blocked }
public sealed record AgentUsage(long InputTokens, long OutputTokens, decimal? Cost = null, string? Currency = null);
public sealed record AgentArtifact(string Id, string ContentType, string Description);

public sealed record AgentRunResult
{
    public required AgentResultStatus Status { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<SourceLink> Sources { get; init; } = [];
    public IReadOnlyList<AgentArtifact> Artifacts { get; init; } = [];
    public AgentUsage? Usage { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public enum AgentEventKind { Started, Progress, ToolRequested, ToolCompleted, WaitingForApproval, Completed, Warning }
public sealed record AgentEvent
{
    public required Guid RunId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required AgentEventKind Kind { get; init; }
    public required string Message { get; init; }
    public string? ToolId { get; init; }
    public Guid? ToolCallId { get; init; }
}
