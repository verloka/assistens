using PersonalAgent.Core.Domain;

namespace PersonalAgent.Persistence.Entities;

// Persistence-only rows keep EF types and mappings out of Core domain contracts.
public sealed class ConversationRow
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TaskDefinitionRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string ModelProfileId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TaskRunRow
{
    public Guid Id { get; set; }
    public Guid? TaskDefinitionId { get; set; }
    public Guid ConversationId { get; set; }
    public string OriginJson { get; set; } = "{}";
    public string Instructions { get; set; } = "";
    public string ModelProfileId { get; set; } = "";
    public RunStatus Status { get; set; }
    public PauseScope? PauseScope { get; set; }
    public string? Reason { get; set; }
    public long RunVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset? ContentClearedAtUtc { get; set; }
    public string? Summary { get; set; }
    public string? ErrorCode { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public string RequestJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public string? SubmissionSource { get; set; }
    public string? SubmissionKey { get; set; }
    public string? SubmissionHash { get; set; }
}

public sealed class RunEventRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public RunEventType EventType { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Message { get; set; } = "";
    public string? ToolId { get; set; }
    public Guid? ToolCallId { get; set; }
    public string? PayloadJson { get; set; }
}
