using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ApprovalRepositoryTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "synthetic-owner");

    [Fact]
    public async Task PendingAndDecidedActionSurviveRealSqliteReopenWithoutDispatchOrPayloadReplacement()
    {
        using var database = new TemporaryAgentDatabase(); var action = await Seed(database);
        var repository = new SqliteApprovalRepository(database);
        var request = await repository.CreateAsync(action, Owner, TimeSpan.FromMinutes(5));
        var reopened = new SqliteApprovalRepository(new SqliteAgentDbContextFactory(database.DatabasePath));
        var stored = (await reopened.GetAsync(request.Id))!;
        Assert.Equal(request.ActionHash, stored.Action.Fingerprint()); Assert.Equal(ApprovalStatus.Pending, stored.Status);
        Assert.Equal("synthetic-target", stored.Action.Arguments.GetProperty("target").GetString());
        Assert.Single(await reopened.ListPendingAsync(Owner)); Assert.Empty(await reopened.ListPendingAsync(Owner with { UserId = "other" }));
        var decided = await reopened.DecideAsync(Command(stored));
        Assert.Equal(ApprovalStatus.Approved, decided.Status); Assert.Equal(1, decided.Version); Assert.Equal(Owner, decided.DecisionBy);
        var reopenedDecision = (await repository.GetAsync(request.Id))!;
        Assert.Equal(decided.Status, reopenedDecision.Status); Assert.Equal(decided.Version, reopenedDecision.Version);
        Assert.Equal(decided.ActionHash, reopenedDecision.ActionHash); Assert.Equal(decided.DecisionBy, reopenedDecision.DecisionBy);
        Assert.Equal(decided.DecidedAtUtc, reopenedDecision.DecidedAtUtc);
        Assert.Empty(await repository.ListPendingAsync(Owner));
        Assert.Equal(RunStatus.WaitingForApproval, (await new SqliteRunRepository(database).GetAsync(action.RunId))!.Run.Status);
    }

    [Fact]
    public async Task WrongOwnerToolPayloadRevisionAndRepeatedDecisionCannotConsumePendingRequest()
    {
        using var database = new TemporaryAgentDatabase(); var action = await Seed(database); var repository = new SqliteApprovalRepository(database);
        var request = await repository.CreateAsync(action, Owner, TimeSpan.FromMinutes(5)); var command = Command(request);
        foreach (var wrong in new[] { command with { Owner = Owner with { UserId = "other" } }, command with { Owner = Owner with { Source = "telegram" } },
            command with { ExpectedVersion = 9 }, command with { RequestId = Guid.NewGuid() },
            command with { ActionHash = (action with { ToolId = "different_tool" }).Fingerprint() },
            command with { ActionHash = (action with { Arguments = JsonSerializer.SerializeToElement(new { target = "attacker" }) }).Fingerprint() } })
            await Assert.ThrowsAsync<ApprovalConflictException>(() => repository.DecideAsync(wrong));
        Assert.Equal(ApprovalStatus.Pending, (await repository.GetAsync(request.Id))!.Status);
        await repository.DecideAsync(command with { Approve = false });
        await Assert.ThrowsAsync<ApprovalConflictException>(() => repository.DecideAsync(command));
        Assert.Equal(ApprovalStatus.Rejected, (await repository.GetAsync(request.Id))!.Status);
    }

    [Fact]
    public async Task ConcurrentDecisionsHaveExactlyOneDurableWinner()
    {
        using var database = new TemporaryAgentDatabase(); var action = await Seed(database);
        var request = await new SqliteApprovalRepository(database).CreateAsync(action, Owner, TimeSpan.FromMinutes(5));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try { await new SqliteApprovalRepository(database).DecideAsync(Command(request) with { Approve = index % 2 == 0 }); return true; }
            catch (ApprovalConflictException) { return false; }
        })).ToArray();
        start.SetResult(); Assert.Single(await Task.WhenAll(attempts), value => value);
        var stored = (await new SqliteApprovalRepository(database).GetAsync(request.Id))!;
        Assert.Equal(1, stored.Version); Assert.Contains(stored.Status, new[] { ApprovalStatus.Approved, ApprovalStatus.Rejected });
    }

    [Theory]
    [InlineData("expired")] [InlineData("revision")] [InlineData("cancelled")]
    public async Task ExpiryOrRunRevisionInvalidationPreventsDecision(string invalidation)
    {
        using var database = new TemporaryAgentDatabase(); var action = await Seed(database); var clock = new Clock();
        var repository = new SqliteApprovalRepository(database, clock);
        var request = await repository.CreateAsync(action, Owner, TimeSpan.FromMinutes(1));
        if (invalidation == "expired") clock.Now += TimeSpan.FromMinutes(1);
        else
        {
            var runs = new SqliteRunRepository(database); var stored = (await runs.GetAsync(action.RunId))!;
            var changed = invalidation == "cancelled" ? TaskRunStateMachine.Transition(stored.Run, RunStatus.Cancelled, stored.Run.RunVersion, DateTimeOffset.UtcNow)
                : stored.Run with { RunVersion = stored.Run.RunVersion + 1 };
            await runs.UpdateAsync(changed, stored.Run.RunVersion, [Event(action.RunId, 4)]);
        }
        await Assert.ThrowsAsync<ApprovalConflictException>(() => repository.DecideAsync(Command(request)));
        if (invalidation == "expired")
        {
            var expired = (await repository.GetAsync(request.Id))!;
            Assert.Equal(ApprovalStatus.Expired, expired.Status); Assert.Equal(1, expired.Version); Assert.Null(expired.DecisionBy);
            Assert.Empty(await repository.ListPendingAsync(Owner));
        }
    }

    [Fact]
    public async Task DuplicateCallAndCorruptOrUnknownStoredPayloadFailClosed()
    {
        using var database = new TemporaryAgentDatabase(); var action = await Seed(database); var repository = new SqliteApprovalRepository(database);
        var request = await repository.CreateAsync(action, Owner, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ApprovalConflictException>(() => repository.CreateAsync(action with { Description = "Changed" }, Owner, TimeSpan.FromMinutes(1)));
        await using var db = database.Open();
        var row = await db.ApprovalRequests.SingleAsync(); row.PayloadSchemaVersion = 2; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync(request.Id));
        row.PayloadSchemaVersion = 1; row.ActionJson = JsonSerializer.Serialize(action with { Destination = "other" }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => repository.DecideAsync(Command(request)));
        Assert.Equal(ApprovalStatus.Pending, (await db.ApprovalRequests.AsNoTracking().SingleAsync()).Status);
    }

    private static async Task<PendingToolAction> Seed(TemporaryAgentDatabase database)
    {
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow; var conversation = new Conversation { Id = Guid.NewGuid(), Title = "Synthetic approval", CreatedAtUtc = now, UpdatedAtUtc = now };
        var run = new TaskRun { Id = Guid.NewGuid(), ConversationId = conversation.Id, Origin = new("desktop", Owner.UserId),
            Instructions = "Synthetic approval only; no dispatch", ModelProfileId = "fake", CreatedAtUtc = now, Status = RunStatus.Queued, RunVersion = 1 };
        var repository = new SqliteRunRepository(database);
        await repository.CreateAsync(run, conversation, new() { RunId = run.Id, ConversationId = conversation.Id, Instructions = run.Instructions, ModelProfileId = "fake" }, [Event(run.Id, 1)]);
        foreach (var status in new[] { RunStatus.Running, RunStatus.WaitingForApproval })
        {
            var next = TaskRunStateMachine.Transition(run, status, run.RunVersion, DateTimeOffset.UtcNow);
            await repository.UpdateAsync(next, run.RunVersion, [Event(run.Id, next.RunVersion)]); run = next;
        }
        return new(run.Id, run.ConversationId, run.RunVersion, "exact-sdk-call", "fixture_write", new(ToolSourceKind.Local, "fixture"),
            ToolRiskLevel.Write, null, JsonSerializer.SerializeToElement(new { target = "synthetic-target", text = "synthetic data" }), "Write synthetic fixture", "synthetic-target");
    }
    private static RunEvent Event(Guid run, long sequence) => new() { Id = Guid.NewGuid(), RunId = run, Sequence = sequence,
        EventType = RunEventType.Progress, TimestampUtc = DateTimeOffset.UtcNow, Message = "Synthetic approval fixture" };
    private static DecideApprovalCommand Command(ApprovalRequest request) => new(request.Id, request.Version, request.ActionHash, Owner, true);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
