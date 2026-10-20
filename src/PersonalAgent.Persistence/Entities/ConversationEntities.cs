using PersonalAgent.Core.Domain;

namespace PersonalAgent.Persistence.Entities;

public sealed class ConversationMessageRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long Sequence { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public Guid? RunId { get; set; }
    public string? ExternalId { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public string? ErrorCode { get; set; }
}

public sealed class AgentSessionRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string SerializedState { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
}
