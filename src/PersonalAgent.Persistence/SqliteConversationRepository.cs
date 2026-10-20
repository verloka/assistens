using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteConversationRepository(IDbContextFactory<AgentDbContext> contexts) : IConversationRepository
{
    public async Task AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        message = ConversationPrivacy.Filter(message);
        if (message.Id == Guid.Empty || message.ConversationId == Guid.Empty || message.Sequence <= 0 || !Enum.IsDefined(message.Role))
            throw new ArgumentException("Message requires identities, a positive sequence and a known role.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (message.RunId is { } runId && !await db.TaskRuns.AnyAsync(run => run.Id == runId && run.ConversationId == message.ConversationId, cancellationToken))
            throw new ArgumentException("Message run belongs to another conversation or is missing.");
        db.ConversationMessages.Add(ToRow(message));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ConversationMessages.AsNoTracking().Where(row => row.ConversationId == conversationId)
            .OrderBy(row => row.Sequence).ToListAsync(cancellationToken);
        return rows.Select(row => ConversationPrivacy.Filter(new ConversationMessage(row.Id, row.ConversationId, row.Sequence, row.Role,
            row.Content, row.TimestampUtc, row.RunId, row.ExternalId)
        {
            Sources = JsonSerializer.Deserialize<SourceLink[]>(row.SourcesJson) ?? throw new InvalidDataException("Missing message sources."),
            ErrorCode = row.ErrorCode
        })).ToArray();
    }

    internal static ConversationMessageRow ToRow(ConversationMessage message)
    {
        message = ConversationPrivacy.Filter(message);
        return new()
        {
            Id = message.Id, ConversationId = message.ConversationId, Sequence = message.Sequence,
            Role = message.Role, Content = message.Content, TimestampUtc = message.TimestampUtc,
            RunId = message.RunId, ExternalId = message.ExternalId,
            SourcesJson = JsonSerializer.Serialize(message.Sources), ErrorCode = message.ErrorCode
        };
    }

    public async Task AddSessionAsync(AgentSessionSnapshot session, CancellationToken cancellationToken = default)
    {
        if (session.Id == Guid.Empty || session.ConversationId == Guid.Empty || session.SchemaVersion <= 0)
            throw new ArgumentException("Session requires identities and a positive schema version.");
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ModelId);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (session.RunId is { } runId)
        {
            var run = await db.TaskRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId && run.ConversationId == session.ConversationId, cancellationToken);
            if (run is null) throw new ArgumentException("Session run belongs to another conversation or is missing.");
            var request = JsonSerializer.Deserialize<AgentRequest>(run.RequestJson);
            if (request?.Profile is not { } profile || profile.ProviderId != session.ProviderId || profile.ModelId != session.ModelId)
                throw new ArgumentException("Session provider/model does not match its run.");
        }
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id, ConversationId = session.ConversationId, ProviderId = session.ProviderId,
            ModelId = session.ModelId, SchemaVersion = session.SchemaVersion, SerializedState = session.SerializedState, TimestampUtc = session.TimestampUtc, RunId = session.RunId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AgentSessionSnapshot?> TryReadSessionAsync(Guid sessionId, string providerId, string modelId, int schemaVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.AgentSessions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken);
        if (row is null || row.ProviderId != providerId || row.ModelId != modelId || row.SchemaVersion != schemaVersion) return null;
        return ToSession(row);
    }

    public async Task<AgentSessionSnapshot?> ReadLatestSessionAsync(Guid conversationId, Guid currentRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.TaskRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == currentRunId && run.ConversationId == conversationId, cancellationToken);
        if (current is null) return null;
        var previous = await db.TaskRuns.AsNoTracking().Where(run => run.ConversationId == conversationId
            && run.Id != currentRunId && run.CreatedAtUtc <= current.CreatedAtUtc)
            .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.Id).FirstOrDefaultAsync(cancellationToken);
        if (previous?.Status != RunStatus.Completed || previous.ResultJson is null) return null;
        SqliteRunRepository.CheckSchema(previous);
        var row = await db.AgentSessions.AsNoTracking().SingleOrDefaultAsync(session => session.RunId == previous.Id && session.ConversationId == conversationId, cancellationToken);
        var profile = JsonSerializer.Deserialize<AgentRequest>(previous.RequestJson)?.Profile;
        if (row is not null && (profile is null || row.ProviderId != profile.ProviderId || row.ModelId != profile.ModelId)) return null;
        return row is null ? null : ToSession(row);
    }

    private static AgentSessionSnapshot ToSession(AgentSessionRow row) =>
        new(row.Id, row.ConversationId, row.ProviderId, row.ModelId, row.SchemaVersion, row.SerializedState, row.TimestampUtc) { RunId = row.RunId };
}
