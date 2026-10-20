namespace PersonalAgent.Core.Domain;

public enum MessageRole { User, Assistant, Tool }

// Message content is untrusted conversation data, never application policy.
public sealed record ConversationMessage(Guid Id, Guid ConversationId, long Sequence, MessageRole Role,
    string Content, DateTimeOffset TimestampUtc, Guid? RunId = null, string? ExternalId = null)
{
    public IReadOnlyList<SourceLink> Sources { get; init; } = [];
    public string? ErrorCode { get; init; }
}

// Immutable provider-specific checkpoint. Callers must exclude credentials; state never authorizes replay.
public sealed record AgentSessionSnapshot(Guid Id, Guid ConversationId, string ProviderId, string ModelId,
    int SchemaVersion, string SerializedState, DateTimeOffset TimestampUtc);
