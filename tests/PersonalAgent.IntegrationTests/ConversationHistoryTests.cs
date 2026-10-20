using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ConversationHistoryTests
{
    private static readonly SourceLink Issue = new("TEST-104", new("https://jira.example.test/browse/TEST-104"));

    [Theory]
    [InlineData(AgentResultStatus.Completed)]
    [InlineData(AgentResultStatus.Failed)]
    [InlineData(AgentResultStatus.Blocked)]
    [InlineData(AgentResultStatus.Cancelled)]
    public async Task ConversationHistoryStoresOrderedRunOutcomeAndReopensForNewFollowup(AgentResultStatus status)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var commands = new SubmitTaskCommand(Guid.NewGuid(), "Explain TEST-104", new("desktop"), ExternalKey: new("fixture", "update-104"));
        Guid conversation; Guid id;
        using (var runs = new TaskRunCoordinator(new SqliteRunRepository(database), conversations: new SqliteConversationRepository(database)))
        {
            using var stop = new CancellationTokenSource();
            var runtime = new FakeAgentRuntime(result: new() { Status = status, Summary = "Verified TEST-104", Sources = [Issue], ErrorCode = status == AgentResultStatus.Completed ? null : "FixtureError" });
            var loop = new TaskRunner(runs, runtime).RunAsync(stop.Token);
            var run = await runs.SubmitAsync(commands); id = run.Id; conversation = run.ConversationId;
            if (status == AgentResultStatus.Blocked)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (runs.GetResult(id) is null) await Task.Delay(10, timeout.Token);
                Assert.Equal(RunStatus.Blocked, runs.GetRun(id).Status);
            }
            else await runs.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(id, (await runs.SubmitAsync(commands with { CommandId = Guid.NewGuid() })).Id);
            stop.Cancel(); await loop;
        }
        var reopened = new SqliteConversationRepository(database);
        var messages = await reopened.ReadMessagesAsync(conversation);
        Assert.Equal([1L, 2L], messages.Select(message => message.Sequence));
        Assert.Equal([MessageRole.User, MessageRole.Assistant], messages.Select(message => message.Role));
        Assert.All(messages, message => { Assert.Equal(id, message.RunId); Assert.Equal(TimeSpan.Zero, message.TimestampUtc.Offset); });
        Assert.Equal("Explain TEST-104", messages[0].Content);
        Assert.Equal("Verified TEST-104", messages[1].Content); Assert.Equal(Issue, Assert.Single(messages[1].Sources));
        Assert.Equal(status == AgentResultStatus.Completed ? null : "FixtureError", messages[1].ErrorCode);
        using var next = new TaskRunCoordinator(new SqliteRunRepository(database), conversations: reopened);
        var followup = await next.SubmitAsync(new(Guid.NewGuid(), "Details", new("channel/fixture"), ConversationId: conversation));
        var request = (await new SqliteRunRepository(database).GetAsync(followup.Id))!.Request;
        Assert.Equal(2, request.Context.Messages!.Count);
        Assert.Contains(request.Context.Messages!, message => message.Content == "Verified TEST-104" && message.Sources.Contains(Issue));
        Assert.Empty(request.AllowedToolIds); Assert.Equal(3, (await reopened.ReadMessagesAsync(conversation)).Count);
    }

    [Fact]
    public async Task ConversationHistoryFilterAppliesOnWriteLegacyReadAndNeutralRunFallback()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        using var runs = new TaskRunCoordinator(new SqliteRunRepository(database), conversations: new SqliteConversationRepository(database));
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "TEST-104 password=hunter-secret", new("test")));
        var repo = new SqliteConversationRepository(database);
        await repo.AppendMessageAsync(new(Guid.NewGuid(), run.ConversationId, 2, MessageRole.Assistant,
            "TEST-104 api_key=sample-secret Bearer opaque-secret", DateTimeOffset.UtcNow, run.Id)
        { Sources = [new("token=title-secret", new("https://alice:user-secret@jira.example.test/browse/TEST-104?token=url-secret&issue=104"))], ErrorCode = "password=error-secret" });
        await repo.AppendMessageAsync(new(Guid.NewGuid(), run.ConversationId, 3, MessageRole.Tool, "private-tool-body", DateTimeOffset.UtcNow, run.Id));
        await using (var db = database.Open())
        {
            var serialized = JsonSerializer.Serialize(await db.ConversationMessages.ToArrayAsync());
            foreach (var secret in new[] { "hunter-secret", "sample-secret", "opaque-secret", "title-secret", "user-secret", "url-secret", "error-secret" }) Assert.DoesNotContain(secret, serialized);
            // Simulate pre-filter legacy content without overwriting raw task snapshots.
            await db.ConversationMessages.Where(row => row.Sequence == 2).ExecuteUpdateAsync(set => set.SetProperty(row => row.Content, "Legacy TEST-104 password=legacy-secret"));
        }
        var next = await runs.SubmitAsync(new(Guid.NewGuid(), "Details", new("test"), ConversationId: run.ConversationId));
        var context = (await new SqliteRunRepository(database).GetAsync(next.Id))!.Request.Context;
        Assert.Contains(context.Messages!, message => message.Content.Contains("Legacy TEST-104 [redacted]"));
        Assert.DoesNotContain("legacy-secret", JsonSerializer.Serialize(context));
        Assert.DoesNotContain(context.Messages!, message => message.Role == MessageRole.Tool || message.Content.Contains("private-tool-body"));
    }

    [Fact]
    public async Task ConversationHistoryFailureRollsBackAdmissionAndTerminalStateWithoutPartialMessages()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        using var runs = new TaskRunCoordinator(new SqliteRunRepository(database));
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailHistory BEFORE INSERT ON ConversationMessages BEGIN SELECT RAISE(ABORT, 'history unavailable'); END;");
        await Assert.ThrowsAsync<RunPersistenceException>(() => runs.SubmitAsync(new(Guid.NewGuid(), "No partial admission", new("test"))));
        await using (var db = database.Open())
        {
            Assert.Empty(await db.TaskRuns.ToArrayAsync()); Assert.Empty(await db.RunEvents.ToArrayAsync()); Assert.Empty(await db.Conversations.ToArrayAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailHistory;");
        }
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "No partial completion", new("test")));
        var repository = new SqliteRunRepository(database);
        var stored = (await repository.GetAsync(run.Id))!;
        var cancelled = TaskRunStateMachine.Transition(stored.Run, RunStatus.Cancelled, stored.Run.RunVersion, DateTimeOffset.UtcNow);
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailOutcome BEFORE INSERT ON ConversationMessages WHEN NEW.Role = 'Assistant' BEGIN SELECT RAISE(ABORT, 'outcome unavailable'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.UpdateAsync(cancelled, stored.Run.RunVersion,
            [new() { Id = Guid.NewGuid(), RunId = run.Id, Sequence = 3, EventType = RunEventType.RunFinished, TimestampUtc = cancelled.EndedAtUtc!.Value, Message = "Cancelled" }]));
        Assert.Equal(RunStatus.Queued, (await repository.GetAsync(run.Id))!.Run.Status);
        Assert.Equal(2, (await repository.GetAsync(run.Id))!.Events.Count);
        Assert.Single(await new SqliteConversationRepository(database).ReadMessagesAsync(run.ConversationId));
    }

    [Fact]
    public async Task ConversationHistoryConcurrentAdmissionsKeepOneOrderedMessagePerRun()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        using var root = new TaskRunCoordinator(new SqliteRunRepository(database));
        var first = await root.SubmitAsync(new(Guid.NewGuid(), "Root", new("test")));
        var attempts = Enumerable.Range(1, 8).Select(index => Task.Run(async () =>
        {
            using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(database));
            return await coordinator.SubmitAsync(new(Guid.NewGuid(), "Concurrent " + index, new("test"), ConversationId: first.ConversationId));
        }));
        var admitted = await Task.WhenAll(attempts);
        var messages = await new SqliteConversationRepository(database).ReadMessagesAsync(first.ConversationId);
        Assert.Equal(Enumerable.Range(1, 9).Select(index => (long)index), messages.Select(message => message.Sequence));
        Assert.Equal(9, messages.Select(message => message.RunId).Distinct().Count());
        Assert.All(admitted, run => Assert.Single(messages, message => message.RunId == run.Id && message.Content == run.Instructions));
    }

    [Fact]
    public async Task ConversationHistoryMixedLegacyRunAndNewMessagesRetainEarlierFilteredSummary()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        using var runs = new TaskRunCoordinator(new SqliteRunRepository(database), conversations: new SqliteConversationRepository(database));
        using var stop = new CancellationTokenSource();
        var loop = new TaskRunner(runs, new FakeAgentRuntime(result: new() { Status = AgentResultStatus.Completed, Summary = "Legacy TEST-104 password=summary-secret", Sources = [Issue] })).RunAsync(stop.Token);
        try
        {
            var first = await runs.SubmitAsync(new(Guid.NewGuid(), "Legacy question password=input-secret", new("test")));
            await runs.WaitForTerminalAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
            await using (var db = database.Open()) await db.ConversationMessages.Where(row => row.RunId == first.Id).ExecuteDeleteAsync();
            var next = await runs.SubmitAsync(new(Guid.NewGuid(), "Modern follow-up", new("test"), ConversationId: first.ConversationId));
            await runs.WaitForTerminalAsync(next.Id).WaitAsync(TimeSpan.FromSeconds(10));
            var third = await runs.SubmitAsync(new(Guid.NewGuid(), "Keep earlier issue", new("test"), ConversationId: first.ConversationId));
            var context = (await new SqliteRunRepository(database).GetAsync(third.Id))!.Request.Context;
            Assert.Equal(4, context.Messages!.Count);
            Assert.Contains(context.Messages!, message => message.RunId == first.Id && message.Role == MessageRole.User);
            Assert.Contains(context.Messages!, message => message.RunId == first.Id && message.Role == MessageRole.Assistant && message.Sources.Contains(Issue));
            Assert.DoesNotContain("summary-secret", JsonSerializer.Serialize(context)); Assert.DoesNotContain("input-secret", JsonSerializer.Serialize(context));
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task ConversationHistoryMigrationPreservesLegacyMessagesAndInterruptedOutcome()
    {
        using var database = new TemporaryAgentDatabase(); var conversation = Guid.NewGuid(); var message = Guid.NewGuid();
        await using (var db = database.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261020154350_ChannelConversationLinks");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ({conversation}, 'Legacy', {1L}, {1L})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ConversationMessages (Id, ConversationId, Sequence, Role, Content, TimestampUtc) VALUES ({message}, {conversation}, 1, 'Assistant', 'Legacy TEST-104', {1L})");
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        }
        var messages = new SqliteConversationRepository(database);
        var legacy = Assert.Single(await messages.ReadMessagesAsync(conversation));
        Assert.Equal(message, legacy.Id); Assert.Empty(legacy.Sources); Assert.Null(legacy.ErrorCode);
        using var runs = new TaskRunCoordinator(new SqliteRunRepository(database));
        var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Interrupted TEST-104", new("test"), ConversationId: conversation));
        var repo = new SqliteRunRepository(database);
        var running = TaskRunStateMachine.Transition(run, RunStatus.Running, run.RunVersion, DateTimeOffset.UtcNow);
        await repo.UpdateAsync(running, run.RunVersion, [new() { Id = Guid.NewGuid(), RunId = run.Id, Sequence = 3, EventType = RunEventType.RunStarted, TimestampUtc = running.StartedAtUtc!.Value, Message = "Started" }]);
        await new RunRecovery(repo).RecoverAsync(); await new RunRecovery(repo).RecoverAsync();
        var restored = await messages.ReadMessagesAsync(conversation);
        Assert.Equal(3, restored.Count); Assert.Equal("RunInterrupted", restored[^1].ErrorCode);
        Assert.Contains("automatic replay is disabled", restored[^1].Content);
    }
}
