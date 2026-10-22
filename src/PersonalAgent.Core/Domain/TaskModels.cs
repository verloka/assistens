using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.Core.Domain;

// Source identifies an adapter/trigger (e.g. desktop), without tying Core to channel SDKs.
public sealed record TaskOrigin(string Source, string? SenderId = null, string? ExternalId = null);
public sealed record SourceLink(string Title, Uri Url);

public sealed record TaskDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Instructions { get; init; }
    public required string ModelProfileId { get; init; }
    public bool Enabled { get; init; } = true;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record Conversation
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus
{
    Created, Queued, WaitingForDesktop, WaitingForUser, Running, Pausing,
    PausedForUser, Paused, WaitingForApproval, Blocked, Completed, Failed,
    Cancelled, Skipped, Interrupted
}

[JsonConverter(typeof(JsonStringEnumConverter<PauseScope>))]
public enum PauseScope { GuiOnly, EntireRun }

[JsonConverter(typeof(JsonStringEnumConverter<PauseReason>))]
public enum PauseReason { AutoPause, DesktopEnvironmentChanged }

public sealed record TaskRun
{
    public required Guid Id { get; init; }
    public Guid? TaskDefinitionId { get; init; }
    public required Guid ConversationId { get; init; }
    public required TaskOrigin Origin { get; init; }
    public required string Instructions { get; init; }
    public required string ModelProfileId { get; init; }
    public RunStatus Status { get; init; } = RunStatus.Created;
    public long RunVersion { get; init; }
    public PauseScope? PauseScope { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public DateTimeOffset? ContentClearedAtUtc { get; init; }
    public string? Summary { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<SourceLink> Sources { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<RunEventType>))]
public enum RunEventType
{
    RunCreated, RunStarted, StatusChanged, Progress, ToolRequested, ToolExecuted,
    ApprovalRequested, DesktopTakenOver, RunPaused, RunResumed, RunFinished, DesktopPauseConfirmed
}

public sealed record RunEvent
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required long Sequence { get; init; }
    public required RunEventType EventType { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Message { get; init; }
    public string? ToolId { get; init; }
    public Guid? ToolCallId { get; init; }
    public JsonElement? Payload { get; init; }
}
