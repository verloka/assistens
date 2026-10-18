using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class PersistenceUpgradeTests
{
    [Fact]
    public async Task PersistenceUpgradePreservesLegacyHistoryAndAppendOnlyTriggersWithoutInventingSubmissionKeys()
    {
        using var database = new TemporaryAgentDatabase();
        var runId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var origin = JsonSerializer.Serialize(new TaskOrigin("legacy-test"));
        var request = JsonSerializer.Serialize(new AgentRequest { RunId = runId, ConversationId = conversationId, Instructions = "Legacy task", ModelProfileId = "fake" });
        await using (var legacy = database.Open())
        {
            await legacy.GetService<IMigrator>().MigrateAsync("20261018105532_InitialRunHistory");
            // Current EF mappings include new columns, so seed the actual older schema explicitly.
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ({conversationId}, {"Legacy conversation"}, {now.UtcTicks}, {now.UtcTicks})");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TaskRuns (Id, ConversationId, OriginJson, Instructions, ModelProfileId, Status, RunVersion, CreatedAtUtc, SourcesJson, RequestJson, PayloadSchemaVersion) VALUES ({runId}, {conversationId}, {origin}, {"Legacy task"}, {"fake"}, {"Created"}, {0L}, {now.UtcTicks}, {"[]"}, {request}, {1})");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RunEvents (Id, RunId, Sequence, EventType, TimestampUtc, Message) VALUES ({eventId}, {runId}, {1L}, {"RunCreated"}, {now.UtcTicks}, {"Legacy event"})");
        }
        await using (var upgraded = database.Open())
        {
            await upgraded.Database.MigrateAsync();
            await upgraded.Database.MigrateAsync();
            Assert.Equal(upgraded.Database.GetMigrations(), await upgraded.Database.GetAppliedMigrationsAsync());
            var row = await upgraded.TaskRuns.SingleAsync();
            Assert.Equal(runId, row.Id);
            Assert.Null(row.SubmissionSource);
            Assert.Null(row.SubmissionKey);
            Assert.Null(row.SubmissionHash);
            (await upgraded.RunEvents.SingleAsync()).Message = "Attempt to rewrite upgraded history";
            await Assert.ThrowsAsync<DbUpdateException>(() => upgraded.SaveChangesAsync());
        }
        var stored = (await new SqliteRunRepository(database).GetAsync(runId))!;
        Assert.Equal("Legacy task", stored.Run.Instructions);
        Assert.Equal(now, stored.Run.CreatedAtUtc);
        Assert.Equal(eventId, Assert.Single(stored.Events).Id);
        Assert.Equal("Legacy event", stored.Events[0].Message);
        Assert.Equal(runId, stored.Request.RunId);
        using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database));
        var current = await coordinator.SubmitAsync(new(Guid.NewGuid(), "Legacy task", new TaskOrigin("legacy-test")));
        Assert.NotEqual(runId, current.Id); // No historical CommandId/key existed to deduplicate.
        await using var reopened = database.Open();
        Assert.Equal(2, await reopened.TaskRuns.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistenceRecoveryHandlesCreatedCheckpointConservativelyAndIdempotently(bool knownLocal)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation { Id = Guid.NewGuid(), Title = "Created checkpoint", CreatedAtUtc = now, UpdatedAtUtc = now };
        var run = new TaskRun
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Instructions = "Not started", ModelProfileId = knownLocal ? "fake" : "unknown-provider",
            Origin = new TaskOrigin("test"), CreatedAtUtc = now
        };
        var request = new AgentRequest { RunId = run.Id, ConversationId = run.ConversationId, Instructions = run.Instructions, ModelProfileId = run.ModelProfileId };
        var repository = new SqliteRunRepository(database);
        await repository.CreateAsync(run, conversation, request, [new RunEvent
        {
            Id = Guid.NewGuid(), RunId = run.Id, Sequence = 1, EventType = RunEventType.RunCreated, TimestampUtc = now, Message = "Created before crash"
        }]);
        var first = await new RunRecovery(repository).RecoverAsync();
        Assert.Equal(knownLocal ? new[] { run.Id } : [], first);
        var recovered = (await repository.GetAsync(run.Id))!;
        Assert.Equal(knownLocal ? RunStatus.Queued : RunStatus.Blocked, recovered.Run.Status);
        Assert.Null(recovered.Run.StartedAtUtc);
        Assert.Null(recovered.Run.EndedAtUtc);
        Assert.Equal(2, recovered.Events.Count);
        Assert.Equal(first, await new RunRecovery(repository).RecoverAsync());
        Assert.Equal(recovered.Run.RunVersion, (await repository.GetAsync(run.Id))!.Run.RunVersion);
        Assert.Equal(2, (await repository.GetAsync(run.Id))!.Events.Count);
    }
}
