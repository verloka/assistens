using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class SqliteConversationRepository(IDbContextFactory<AgentDbContext> contexts) : IConversationRepository
{
    public async Task AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Id == Guid.Empty || message.ConversationId == Guid.Empty || message.Sequence <= 0 || !Enum.IsDefined(message.Role))
            throw new ArgumentException("Message requires identities, a positive sequence and a known role.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (message.RunId is { } runId && !await db.TaskRuns.AnyAsync(run => run.Id == runId && run.ConversationId == message.ConversationId, cancellationToken))
            throw new ArgumentException("Message run belongs to another conversation or is missing.");
        db.ConversationMessages.Add(new ConversationMessageRow
        {
            Id = message.Id, ConversationId = message.ConversationId, Sequence = message.Sequence,
            Role = message.Role, Content = message.Content, TimestampUtc = message.TimestampUtc,
            RunId = message.RunId, ExternalId = message.ExternalId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ConversationMessages.AsNoTracking().Where(row => row.ConversationId == conversationId)
            .OrderBy(row => row.Sequence).ToListAsync(cancellationToken);
        return rows.Select(row => new ConversationMessage(row.Id, row.ConversationId, row.Sequence, row.Role,
            row.Content, row.TimestampUtc, row.RunId, row.ExternalId)).ToArray();
    }

    public async Task AddSessionAsync(AgentSessionSnapshot session, CancellationToken cancellationToken = default)
    {
        if (session.Id == Guid.Empty || session.ConversationId == Guid.Empty || session.SchemaVersion <= 0)
            throw new ArgumentException("Session requires identities and a positive schema version.");
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ModelId);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id, ConversationId = session.ConversationId, ProviderId = session.ProviderId,
            ModelId = session.ModelId, SchemaVersion = session.SchemaVersion, SerializedState = session.SerializedState, TimestampUtc = session.TimestampUtc
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AgentSessionSnapshot?> TryReadSessionAsync(Guid sessionId, string providerId, string modelId, int schemaVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.AgentSessions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken);
        if (row is null || row.ProviderId != providerId || row.ModelId != modelId || row.SchemaVersion != schemaVersion) return null;
        return new AgentSessionSnapshot(row.Id, row.ConversationId, row.ProviderId, row.ModelId, row.SchemaVersion, row.SerializedState, row.TimestampUtc);
    }
}
