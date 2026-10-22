using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class HistoryRetentionTests
{
    private const string Private = "synthetic-private-history-117";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset Old = Now.AddDays(-90);

    [Fact]
    public async Task ClearRemovesAllHistoryCopiesButKeepsLedgerAndSubmissionIdentityAcrossRestart()
    {
        using var database = new TemporaryAgentDatabase();
        var command = new SubmitTaskCommand(Guid.NewGuid(), Private, new("test"), ExternalKey: new("retention-test", "update-1"));
        var run = await SeedAsync(database, command);
        var (approvalId, deliveryId) = await AddLedgersAsync(database, run, ToolCallDispatchStatus.Acknowledged, NotificationDeliveryStatus.Delivered);
        var repository = new SqliteRunRepository(database);
        var before = (await repository.GetAsync(run.Id))!;
        string submission, call;
        await using (var db = database.Open())
        {
            var row = await db.TaskRuns.SingleAsync();
            submission = JsonSerializer.Serialize(new { row.SubmissionHash, row.SubmissionKey, row.SubmissionSource });
            call = JsonSerializer.Serialize(await db.ToolCalls.SingleAsync());
        }
        var retention = new SqliteHistoryRetentionRepository(database);
        using (var coordinator = new TaskRunCoordinator(repository, conversations: new SqliteConversationRepository(database)))
        {
            await coordinator.LoadFinishedHistoryAsync();
            Assert.Equal(1, await coordinator.ClearExpiredHistoryAsync(retention, new(false, 30), Now));
            Assert.Equal(Now, coordinator.GetRun(run.Id).ContentClearedAtUtc);
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(coordinator.GetEvents(run.Id)));
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(coordinator.GetResult(run.Id)));
        }
        var after = (await repository.GetAsync(run.Id))!;
        Assert.Equal(before.Run.Status, after.Run.Status); Assert.Equal(before.Run.RunVersion, after.Run.RunVersion);
        Assert.Equal(before.Run.CreatedAtUtc, after.Run.CreatedAtUtc); Assert.Equal(before.Run.EndedAtUtc, after.Run.EndedAtUtc);
        Assert.Equal(before.Events.Select(e => (e.Id, e.Sequence, e.EventType, e.TimestampUtc, e.ToolId, e.ToolCallId)),
            after.Events.Select(e => (e.Id, e.Sequence, e.EventType, e.TimestampUtc, e.ToolId, e.ToolCallId)));
        Assert.Empty(after.Request.AllowedToolIds); Assert.Null(after.Request.Profile);
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(after));
        await using (var db = database.Open())
        {
            var row = await db.TaskRuns.SingleAsync();
            Assert.Equal(submission, JsonSerializer.Serialize(new { row.SubmissionHash, row.SubmissionKey, row.SubmissionSource }));
            Assert.Equal(call, JsonSerializer.Serialize(await db.ToolCalls.SingleAsync()));
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(await db.RunEvents.ToArrayAsync()));
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(await db.ConversationMessages.ToArrayAsync()));
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(await db.ApprovalRequests.ToArrayAsync()));
            Assert.DoesNotContain(Private, JsonSerializer.Serialize(await db.NotificationOutbox.ToArrayAsync()));
            Assert.DoesNotContain(Private, (await db.Conversations.SingleAsync()).Title);
            Assert.Empty(await db.AgentSessions.ToArrayAsync());
            Assert.Equal(2, await db.ConversationMessages.CountAsync()); // Identity/sequence/external-ID tombstones.
        }
        Assert.Null(await new SqliteApprovalRepository(database).GetAsync(approvalId));
        await Assert.ThrowsAsync<ApprovalConflictException>(() => new SqliteApprovalRepository(database).DecideAsync(new(approvalId, 0, new('a', 64), new("desktop", "fixture"), true)));
        Assert.Equal(NotificationDeliveryStatus.Delivered, (await new SqliteNotificationOutbox(database).GetAsync(deliveryId))!.Status);
        Assert.Empty(await retention.ClearExpiredAsync(Now.AddDays(-30), Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateAsync(after.Run with { RunVersion = after.Run.RunVersion + 1 }, after.Run.RunVersion, []));

        using var restarted = new TaskRunCoordinator(repository, conversations: new SqliteConversationRepository(database));
        await restarted.RestoreHistoryAsync([]);
        var duplicate = await restarted.SubmitAsync(command with { CommandId = Guid.NewGuid() });
        Assert.Equal(run.Id, duplicate.Id); Assert.Equal(RunStatus.Completed, duplicate.Status);
        await Assert.ThrowsAsync<RunSubmissionConflictException>(() => restarted.SubmitAsync(command with { CommandId = Guid.NewGuid(), Instructions = "Changed intent" }));
        var followup = await restarted.SubmitAsync(new(Guid.NewGuid(), "Fresh request", new("test"), ConversationId: run.ConversationId));
        var context = (await repository.GetAsync(followup.Id))!.Request.Context;
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(context));
        Assert.Empty(context.Messages ?? []); Assert.Null(context.Summary);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("interrupted")]
    [InlineData("recent")]
    [InlineData("other-active-run")]
    [InlineData("pending-approval")]
    [InlineData("unconsumed-approval")]
    [InlineData("unknown-tool")]
    [InlineData("dispatched-tool")]
    [InlineData("pending-delivery")]
    [InlineData("sending-delivery")]
    [InlineData("unknown-delivery")]
    [InlineData("input-uncertain")]
    [InlineData("unknown-error")]
    [InlineData("recent-message")]
    [InlineData("schema")]
    public async Task UnsafeOrRecentConversationIsKeptByteForByteWhileIndependentOldConversationClears(string reason)
    {
        using var database = new TemporaryAgentDatabase();
        var protectedRun = await SeedAsync(database);
        var availableRun = await SeedAsync(database);
        var toolStatus = reason == "unknown-tool" ? ToolCallDispatchStatus.OutcomeUnknown
            : reason == "dispatched-tool" ? ToolCallDispatchStatus.Dispatched : ToolCallDispatchStatus.Acknowledged;
        var deliveryStatus = reason switch { "pending-delivery" => NotificationDeliveryStatus.Pending, "sending-delivery" => NotificationDeliveryStatus.Sending,
            "unknown-delivery" => NotificationDeliveryStatus.OutcomeUnknown, _ => NotificationDeliveryStatus.Delivered };
        await AddLedgersAsync(database, protectedRun, toolStatus, deliveryStatus);
        await using (var db = database.Open())
        {
            var row = await db.TaskRuns.SingleAsync(r => r.Id == protectedRun.Id);
            if (reason == "active") row.Status = RunStatus.Paused;
            if (reason == "interrupted") row.Status = RunStatus.Interrupted;
            if (reason == "recent") row.EndedAtUtc = Now;
            if (reason == "input-uncertain") row.ErrorCode = "InputCleanupUncertain";
            if (reason == "unknown-error") row.ErrorCode = "ToolOutcomeUnknown";
            if (reason == "schema") row.PayloadSchemaVersion = 99;
            if (reason == "recent-message") (await db.ConversationMessages.FirstAsync(m => m.RunId == protectedRun.Id)).TimestampUtc = Now;
            if (reason is "pending-approval" or "unconsumed-approval")
            {
                var approval = await db.ApprovalRequests.SingleAsync();
                if (reason == "pending-approval") approval.Status = ApprovalStatus.Pending;
                else db.ToolCalls.Remove(await db.ToolCalls.SingleAsync());
            }
            await db.SaveChangesAsync();
        }
        if (reason == "other-active-run")
        {
            using var commands = new TaskRunCoordinator(new SqliteRunRepository(database));
            await commands.SubmitAsync(new(Guid.NewGuid(), Private, new("test"), ConversationId: protectedRun.ConversationId));
        }
        var before = await ContentSnapshot(database, protectedRun);
        var cleared = await new SqliteHistoryRetentionRepository(database).ClearExpiredAsync(Now.AddDays(-30), Now);
        Assert.Equal(availableRun.Id, Assert.Single(cleared).Run.Id);
        Assert.Equal(before, await ContentSnapshot(database, protectedRun));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackEveryRedactionAndDoesNotPublishSanitizedCache()
    {
        using var database = new TemporaryAgentDatabase();
        var run = await SeedAsync(database); await AddLedgersAsync(database, run, ToolCallDispatchStatus.Acknowledged, NotificationDeliveryStatus.Delivered);
        using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database)); await coordinator.LoadFinishedHistoryAsync();
        var before = await ContentSnapshot(database, run);
        await using (var db = database.Open()) await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER RejectRedaction BEFORE UPDATE ON RunEvents BEGIN SELECT RAISE(ABORT, 'synthetic storage failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => coordinator.ClearExpiredHistoryAsync(new SqliteHistoryRetentionRepository(database), new(false, 30), Now));
        Assert.Equal(before, await ContentSnapshot(database, run)); Assert.Null(coordinator.GetRun(run.Id).ContentClearedAtUtc);
        Assert.Equal(Private, coordinator.GetRun(run.Id).Instructions);
    }

    [Fact]
    public async Task EventIdentityAndArbitraryPayloadRemainImmutableEvenAfterPrivacyCleanup()
    {
        using var database = new TemporaryAgentDatabase(); var run = await SeedAsync(database);
        await new SqliteHistoryRetentionRepository(database).ClearExpiredAsync(Now.AddDays(-30), Now);
        await using var db = database.Open();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE RunEvents SET Sequence = Sequence + 100"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE RunEvents SET Message = 'replacement body'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE RunEvents SET PayloadJson = 'null'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM RunEvents"));
        Assert.Equal(4, await db.RunEvents.CountAsync());
    }

    [Fact]
    public async Task ClearedSourceRejectsLatePayloadWritesAndLateNotificationPublication()
    {
        using var database = new TemporaryAgentDatabase(); var run = await SeedAsync(database);
        var repository = new SqliteRunRepository(database); var stored = (await repository.GetAsync(run.Id))!;
        var conversation = new SqliteConversationRepository(database);
        await new SqliteHistoryRetentionRepository(database).ClearExpiredAsync(Now.AddDays(-30), Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.AppendMessageAsync(new(Guid.NewGuid(), run.ConversationId, 99, MessageRole.Assistant, Private, Now, run.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.AddSessionAsync(new(Guid.NewGuid(), run.ConversationId, "fixture", "fixture", 1, Private, Now)));
        var late = new OutgoingMessage { Id = Guid.NewGuid(), Destination = new("test", "fixture", "peer"), RunId = run.Id, ConversationId = run.ConversationId, Text = Private };
        await Assert.ThrowsAsync<NotificationConflictException>(() => new SqliteNotificationOutbox(database).EnqueueAsync(stored.Events.Last().Id, late));
        await using var db = database.Open(); Assert.Empty(await db.NotificationOutbox.ToArrayAsync());
        Assert.Empty(await db.AgentSessions.ToArrayAsync()); Assert.DoesNotContain(Private, JsonSerializer.Serialize(await db.ConversationMessages.ToArrayAsync()));
    }

    [Fact]
    public async Task PolicyDefaultsOffPersistsAndActualHostedSweepClearsOnlyWhenEnabled()
    {
        using var database = new TemporaryAgentDatabase(); var run = await SeedAsync(database);
        var retention = new SqliteHistoryRetentionRepository(database);
        Assert.Equal(new HistoryRetentionPolicy(), await retention.ReadPolicyAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => retention.SavePolicyAsync(new(true, 0)));
        using (var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders()).Build())
        {
            await host.StartAsync();
            var service = host.Services.GetRequiredService<HistoryRetentionService>();
            Assert.Equal(0, await service.SweepAsync());
            Assert.Null(host.Services.GetRequiredService<TaskRunCoordinator>().GetRun(run.Id).ContentClearedAtUtc);
            await host.Services.GetRequiredService<IHistoryRetentionRepository>().SavePolicyAsync(new(true, 30));
            Assert.Equal(1, await service.SweepAsync());
            Assert.NotNull(host.Services.GetRequiredService<TaskRunCoordinator>().GetRun(run.Id).ContentClearedAtUtc);
            await host.StopAsync();
        }
        Assert.Equal(new(true, 30), await new SqliteHistoryRetentionRepository(database).ReadPolicyAsync());
        using var restarted = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders()).Build();
        await restarted.StartAsync();
        Assert.NotNull(restarted.Services.GetRequiredService<TaskRunCoordinator>().GetRun(run.Id).ContentClearedAtUtc);
        Assert.Equal(0, await restarted.Services.GetRequiredService<HistoryRetentionService>().SweepAsync());
        await restarted.StopAsync();
    }

    internal static async Task<TaskRun> SeedAsync(TemporaryAgentDatabase database, SubmitTaskCommand? command = null)
    {
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database);
        using var coordinator = new TaskRunCoordinator(repository);
        var run = await coordinator.SubmitAsync(command ?? new(Guid.NewGuid(), Private, new("test")));
        var running = TaskRunStateMachine.Transition(run, RunStatus.Running, run.RunVersion, DateTimeOffset.UtcNow);
        await repository.UpdateAsync(running, run.RunVersion, [Event(run.Id, 3)]);
        var result = new AgentRunResult { Status = AgentResultStatus.Completed, Summary = Private,
            Sources = [new(Private, new("https://example.test/" + Private))], Artifacts = [new(Private, "text/plain", Private)], Warnings = [Private] };
        var complete = TaskRunStateMachine.Transition(running, RunStatus.Completed, running.RunVersion, DateTimeOffset.UtcNow) with { Summary = Private, Sources = result.Sources };
        await repository.UpdateAsync(complete, running.RunVersion, [Event(run.Id, 4)], result);
        await using (var db = database.Open())
        {
            var row = await db.TaskRuns.SingleAsync(r => r.Id == run.Id);
            row.CreatedAtUtc = Old; row.StartedAtUtc = Old; row.EndedAtUtc = Old;
            var request = JsonSerializer.Deserialize<AgentRequest>(row.RequestJson)!;
            row.RequestJson = JsonSerializer.Serialize(request with { Context = new(Private, result.Sources) });
            var conversation = await db.Conversations.SingleAsync(c => c.Id == run.ConversationId);
            conversation.CreatedAtUtc = Old; conversation.UpdatedAtUtc = Old;
            foreach (var message in await db.ConversationMessages.Where(m => m.RunId == run.Id).ToArrayAsync()) message.TimestampUtc = Old;
            db.AgentSessions.Add(new() { Id = Guid.NewGuid(), ConversationId = run.ConversationId, ProviderId = "fixture", ModelId = "fixture", SchemaVersion = 1, SerializedState = Private, TimestampUtc = Old });
            await db.SaveChangesAsync();
        }
        return (await repository.GetAsync(run.Id))!.Run;
    }

    private static RunEvent Event(Guid runId, long sequence) => new() { Id = Guid.NewGuid(), RunId = runId, Sequence = sequence,
        EventType = RunEventType.RunFinished, TimestampUtc = Old, Message = Private, Payload = JsonSerializer.SerializeToElement(new { body = Private }) };

    private static async Task<(Guid ApprovalId, Guid DeliveryId)> AddLedgersAsync(TemporaryAgentDatabase database, TaskRun run, ToolCallDispatchStatus toolStatus, NotificationDeliveryStatus deliveryStatus)
    {
        await using var db = database.Open();
        var action = new PendingToolAction(run.Id, run.ConversationId, 1, "call-117", "fixture.write", new(ToolSourceKind.Local, "fixture"),
            ToolRiskLevel.Write, "1", JsonSerializer.SerializeToElement(new { body = Private }), Private, "fixture");
        var approval = new ApprovalRequestRow { Id = Guid.NewGuid(), RunId = run.Id, ToolCallId = action.ToolCallId, RunVersion = 1,
            OwnerId = "fixture", OwnerSource = "desktop", ActionHash = action.Fingerprint(), ActionJson = JsonSerializer.Serialize(action), Status = ApprovalStatus.Approved,
            CreatedAtUtc = Old, ExpiresAtUtc = Old.AddHours(1), DecidedAtUtc = Old };
        db.ApprovalRequests.Add(approval);
        db.ToolCalls.Add(new() { Id = Guid.NewGuid(), RunId = run.Id, ToolCallId = action.ToolCallId, ToolId = action.ToolId, ApprovalRequestId = approval.Id,
            ActionHash = action.Fingerprint(), Status = toolStatus, DispatchedAtUtc = Old, FinishedAtUtc = toolStatus == ToolCallDispatchStatus.Dispatched ? null : Old });
        var deliveryId = Guid.NewGuid();
        db.NotificationOutbox.Add(new() { Id = deliveryId, EventId = (await db.RunEvents.FirstAsync(e => e.RunId == run.Id)).Id,
            ChannelId = "test", ConnectionId = "fixture", DestinationId = "peer", Status = deliveryStatus, CreatedAtUtc = Old, UpdatedAtUtc = Old, NextAttemptAtUtc = Old,
            MessageJson = JsonSerializer.Serialize(new OutgoingMessage { Id = deliveryId, Destination = new("test", "fixture", "peer"), Text = Private, RunId = run.Id, ConversationId = run.ConversationId }) });
        await db.SaveChangesAsync(); return (approval.Id, deliveryId);
    }

    private static async Task<string> ContentSnapshot(TemporaryAgentDatabase database, TaskRun run)
    {
        await using var db = database.Open();
        return JsonSerializer.Serialize(new { Run = await db.TaskRuns.SingleAsync(r => r.Id == run.Id), Events = await db.RunEvents.Where(e => e.RunId == run.Id).OrderBy(e => e.Sequence).ToArrayAsync(),
            Messages = await db.ConversationMessages.Where(m => m.ConversationId == run.ConversationId).OrderBy(m => m.Sequence).ToArrayAsync(),
            Sessions = await db.AgentSessions.Where(s => s.ConversationId == run.ConversationId).ToArrayAsync(), Approvals = await db.ApprovalRequests.Where(a => a.RunId == run.Id).ToArrayAsync(),
            Calls = await db.ToolCalls.Where(c => c.RunId == run.Id).ToArrayAsync(), Deliveries = await db.NotificationOutbox.ToArrayAsync() });
    }
}
