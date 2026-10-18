using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceSchemaTests
{
    [Fact]
    public async Task PersistenceMigrationsCreateEmptyFileReapplyAndReopenWithoutDataLoss()
    {
        using var database = new TemporaryAgentDatabase();
        var id = Guid.NewGuid();
        var instant = DateTimeOffset.Parse("2026-10-18T14:00:00+03:00");
        await using (var db = database.Open())
        {
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
            Assert.Contains("TaskDefinitions", tables);
            Assert.Contains("TaskRuns", tables);
            Assert.Contains("RunEvents", tables);
            Assert.Contains("Conversations", tables);
            db.Conversations.Add(new ConversationRow { Id = id, Title = "Durable test", CreatedAtUtc = instant, UpdatedAtUtc = instant });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Open())
        {
            await db.Database.MigrateAsync();
            var conversation = await db.Conversations.SingleAsync();
            Assert.Equal(id, conversation.Id);
            Assert.Equal(instant.ToUniversalTime(), conversation.CreatedAtUtc);
            Assert.Equal(TimeSpan.Zero, conversation.CreatedAtUtc.Offset);
            Assert.Equal(instant.UtcTicks, await db.Database.SqlQueryRaw<long>("SELECT CreatedAtUtc AS Value FROM Conversations").SingleAsync());
            Assert.Single(await db.Conversations.Where(row => row.CreatedAtUtc > instant.AddMinutes(-1)).OrderBy(row => row.CreatedAtUtc).ToListAsync());
        }
    }

    [Fact]
    public async Task PersistenceSchemaEnforcesForeignKeysUniqueSequenceAndAppendOnlyEvents()
    {
        using var database = new TemporaryAgentDatabase();
        var conversationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var db = database.Open())
        {
            await db.Database.MigrateAsync();
            db.Conversations.Add(new ConversationRow { Id = conversationId, Title = "Test", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            db.TaskRuns.Add(new TaskRunRow { Id = runId, ConversationId = conversationId, CreatedAtUtc = DateTimeOffset.UtcNow, Status = RunStatus.Queued });
            db.RunEvents.Add(new RunEventRow { Id = eventId, RunId = runId, Sequence = 1, EventType = RunEventType.RunCreated, Message = "Created", TimestampUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await Rejected(db => { db.RunEvents.Add(new RunEventRow { Id = Guid.NewGuid(), RunId = runId, Sequence = 1 }); return Task.CompletedTask; });
        await Rejected(db => { db.RunEvents.Add(new RunEventRow { Id = Guid.NewGuid(), RunId = Guid.NewGuid(), Sequence = 2 }); return Task.CompletedTask; });
        await Rejected(async db => (await db.RunEvents.SingleAsync()).Message = "Rewrite history");
        await Rejected(async db => db.RunEvents.Remove(await db.RunEvents.SingleAsync()));
        await Rejected(async db => db.Conversations.Remove(await db.Conversations.SingleAsync()));
        await using var reopened = database.Open();
        Assert.Equal("Created", (await reopened.RunEvents.SingleAsync()).Message);
        Assert.Single(await reopened.TaskRuns.ToListAsync());

        async Task Rejected(Func<PersonalAgent.Persistence.AgentDbContext, Task> mutate)
        {
            await using var db = database.Open();
            await mutate(db);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task PersistenceSchemaStoresVersionedPayloadsAndApplicationManagedConcurrency()
    {
        using var database = new TemporaryAgentDatabase();
        await using var db = database.Open();
        await db.Database.MigrateAsync();
        var version = db.Model.FindEntityType(typeof(TaskRunRow))!.FindProperty(nameof(TaskRunRow.RunVersion))!;
        Assert.True(version.IsConcurrencyToken);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, version.ValueGenerated);
        var conversation = new ConversationRow { Id = Guid.NewGuid(), Title = "Test" };
        db.Conversations.Add(conversation);
        db.TaskRuns.Add(new TaskRunRow { Id = Guid.NewGuid(), ConversationId = conversation.Id, PayloadSchemaVersion = 0 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Conversations.ToListAsync());
        Assert.Empty(await db.TaskRuns.ToListAsync());
    }
}
