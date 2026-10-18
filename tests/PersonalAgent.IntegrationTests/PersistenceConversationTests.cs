using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceConversationTests
{
    [Fact]
    public async Task PersistenceConversationMessagesAndVersionedSessionSurviveReopenWithoutSdkTypes()
    {
        using var database = new TemporaryAgentDatabase();
        var conversationId = await CreateConversation(database);
        var now = DateTimeOffset.Parse("2026-10-18T15:00:00+03:00");
        var messages = new[]
        {
            new ConversationMessage(Guid.NewGuid(), conversationId, 1, MessageRole.User, "Read local value", now, ExternalId: "message-1"),
            new ConversationMessage(Guid.NewGuid(), conversationId, 2, MessageRole.Tool, "Untrusted tool content", now),
            new ConversationMessage(Guid.NewGuid(), conversationId, 3, MessageRole.Assistant, "Local answer", now)
        };
        var session = new AgentSessionSnapshot(Guid.NewGuid(), conversationId, "fake-provider", "fake-model", 2, "opaque-checkpoint", now);
        IConversationRepository writer = new SqliteConversationRepository(database);
        foreach (var message in messages) await writer.AppendMessageAsync(message);
        await writer.AddSessionAsync(session);
        IConversationRepository reopened = new SqliteConversationRepository(database);
        Assert.Equal(messages, await reopened.ReadMessagesAsync(conversationId));
        var restored = await reopened.TryReadSessionAsync(session.Id, session.ProviderId, session.ModelId, session.SchemaVersion);
        Assert.Equal(session, restored);
        Assert.Equal(TimeSpan.Zero, restored!.TimestampUtc.Offset);
        Assert.All(await reopened.ReadMessagesAsync(conversationId), message => Assert.Equal(TimeSpan.Zero, message.TimestampUtc.Offset));
        Assert.Equal(session, JsonSerializer.Deserialize<AgentSessionSnapshot>(JsonSerializer.Serialize(session)));
        Assert.Equal(messages[0], JsonSerializer.Deserialize<ConversationMessage>(JsonSerializer.Serialize(messages[0])));
    }

    [Fact]
    public async Task PersistenceConversationNeverTransfersIncompatibleProviderModelOrSchemaState()
    {
        using var database = new TemporaryAgentDatabase();
        var id = await CreateConversation(database);
        var repository = new SqliteConversationRepository(database);
        var session = new AgentSessionSnapshot(Guid.NewGuid(), id, "provider-a", "model-a", 2, "opaque state", DateTimeOffset.UtcNow);
        await repository.AddSessionAsync(session);
        await repository.AppendMessageAsync(new(Guid.NewGuid(), id, 1, MessageRole.Assistant, "Neutral summary", DateTimeOffset.UtcNow));
        Assert.Null(await repository.TryReadSessionAsync(session.Id, "provider-b", "model-a", 2));
        Assert.Null(await repository.TryReadSessionAsync(session.Id, "provider-a", "model-b", 2));
        Assert.Null(await repository.TryReadSessionAsync(session.Id, "provider-a", "model-a", 1));
        Assert.Equal("Neutral summary", Assert.Single(await repository.ReadMessagesAsync(id)).Content);
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddSessionAsync(session with { SerializedState = "replacement" }));
        Assert.Equal("opaque state", (await repository.TryReadSessionAsync(session.Id, "provider-a", "model-a", 2))!.SerializedState);
    }

    [Fact]
    public async Task PersistenceConversationRejectsDuplicateMessageIdentitySequenceAndCrossConversationRun()
    {
        using var database = new TemporaryAgentDatabase();
        var id = await CreateConversation(database);
        var otherId = await CreateConversation(database);
        var repository = new SqliteConversationRepository(database);
        var message = new ConversationMessage(Guid.NewGuid(), id, 1, MessageRole.User, "One", DateTimeOffset.UtcNow, ExternalId: "update-1");
        await repository.AppendMessageAsync(message);
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AppendMessageAsync(message with { Id = Guid.NewGuid(), ExternalId = null }));
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AppendMessageAsync(message with { Id = Guid.NewGuid(), Sequence = 2 }));
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AppendMessageAsync(message with { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid() }));
        await using var db = database.Open();
        var runId = Guid.NewGuid();
        db.TaskRuns.Add(new TaskRunRow { Id = runId, ConversationId = otherId, Status = RunStatus.Created });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AppendMessageAsync(message with { Id = Guid.NewGuid(), Sequence = 2, ExternalId = null, RunId = runId }));
        Assert.Single(await repository.ReadMessagesAsync(id));
    }

    private static async Task<Guid> CreateConversation(TemporaryAgentDatabase database)
    {
        await using var db = database.Open();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        db.Conversations.Add(new ConversationRow { Id = id, Title = "Diagnostic conversation", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }
}
