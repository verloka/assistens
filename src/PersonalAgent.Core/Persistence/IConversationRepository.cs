using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Persistence;

public interface IConversationRepository
{
    Task AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AddSessionAsync(AgentSessionSnapshot session, CancellationToken cancellationToken = default);
    // Latest preceding run only: never search back past a newer failed/incompatible execution.
    Task<AgentSessionSnapshot?> ReadLatestSessionAsync(Guid conversationId, Guid currentRunId, CancellationToken cancellationToken = default);
    Task<AgentSessionSnapshot?> TryReadSessionAsync(Guid sessionId, string providerId, string modelId, int schemaVersion, CancellationToken cancellationToken = default);
}
