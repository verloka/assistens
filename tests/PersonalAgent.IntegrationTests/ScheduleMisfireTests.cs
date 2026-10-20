using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Collections.Concurrent;

namespace PersonalAgent.IntegrationTests;

public sealed class ScheduleMisfireTests
{
    [Theory]
    [InlineData(MisfirePolicy.RunOnce)] [InlineData(MisfirePolicy.Skip)]
    public async Task MissedIntervalsRunOnceOrPersistSkippedWithoutQueueingRuntime(MisfirePolicy policy)
    {
        await using var fixture = await Fixture.Create(policy); fixture.Clock.Now += TimeSpan.FromHours(5);
        var result = Assert.Single(await fixture.Dispatcher.TickAsync()); var id = result.RunId!.Value;
        await fixture.Runs.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        // Sentinel proves the queue drained; an initially Skipped run must never enter it.
        var sentinel = await fixture.Runs.SubmitAsync(new(Guid.NewGuid(), "Independent sentinel", new("test")));
        await fixture.Runs.WaitForTerminalAsync(sentinel.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var run = (await fixture.Repository.GetAsync(id))!;
        Assert.Equal(policy == MisfirePolicy.Skip ? RunStatus.Skipped : RunStatus.Completed, run.Run.Status);
        Assert.Equal(policy == MisfirePolicy.Skip ? 0 : 1, fixture.Runtime.Count(id));
        Assert.Equal(fixture.Created.Schedule.CreatedAtUtc.AddHours(6), (await fixture.Schedules.GetAsync(fixture.Created.Schedule.Id))!.Schedule.NextRunAtUtc);
        Assert.Empty(await fixture.Dispatcher.TickAsync());
        if (policy == MisfirePolicy.Skip) { Assert.Null(run.Run.StartedAtUtc); Assert.NotNull(run.Run.EndedAtUtc); Assert.DoesNotContain(run.Events, value => value.EventType == RunEventType.RunStarted); }
    }

    [Fact]
    public async Task CatchUpBudgetSurvivesNameEditAndKernelRestartAndExecutesAtMostConfiguredBatch()
    {
        await using var fixture = await Fixture.Create(MisfirePolicy.CatchUp); fixture.Clock.Now += TimeSpan.FromHours(5);
        var first = Assert.Single(await fixture.Dispatcher.TickAsync()).RunId!.Value;
        await fixture.Runs.WaitForTerminalAsync(first).WaitAsync(TimeSpan.FromSeconds(5));
        var state = (await fixture.Schedules.GetAsync(fixture.Created.Schedule.Id))!;
        Assert.Equal(2, state.Schedule.CatchUpRemaining); Assert.Equal(fixture.Clock.Now, state.Schedule.CatchUpUntilUtc);
        state = await fixture.Schedules.SaveAsync(state with { Task = state.Task with { Name = "Renamed during catch up" } }, state.Schedule.Version);
        Assert.Equal(2, state.Schedule.CatchUpRemaining);
        await fixture.Restart();
        for (var tick = 0; tick < 8; tick++)
            foreach (var outcome in await fixture.Dispatcher.TickAsync())
                if (outcome.RunId is { } id) await fixture.Runs.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        var runs = await fixture.Repository.ListAsync(); Assert.Equal(3, runs.Count);
        Assert.All(runs, run => Assert.Equal(1, fixture.Runtime.Count(run.Id)));
        state = (await fixture.Schedules.GetAsync(fixture.Created.Schedule.Id))!;
        Assert.True(state.Schedule.NextRunAtUtc > fixture.Clock.Now); Assert.Null(state.Schedule.CatchUpRemaining); Assert.Null(state.Schedule.CatchUpUntilUtc);
    }

    [Fact]
    public async Task FrequentOrUnboundedCatchUpCannotBeSaved()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); var repository = new SqliteScheduleRepository(database, clock);
        var task = Draft(clock.Now, MisfirePolicy.CatchUp);
        foreach (var limit in new[] { 0, 11 })
            await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(task with { Schedule = task.Schedule with { MaxCatchUpRuns = limit } }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(task with { Schedule = task.Schedule with { Recurrence = task.Schedule.Recurrence with { EveryMinutes = 10 } } }));
        await using var check = database.Open(); Assert.Empty(await check.Schedules.ToArrayAsync()); Assert.Empty(await check.TaskDefinitions.ToArrayAsync());
    }

    [Fact]
    public async Task ExistingScheduleMigrationKeepsRunOnceDefaultsAndOriginalUtcOccurrence()
    {
        using var database = new TemporaryAgentDatabase(); var id = Guid.NewGuid(); var taskId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var db = database.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261019132613_ScheduleDefinitions");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TaskDefinitions (Id,Name,Instructions,ModelProfileId,Enabled,CreatedAtUtc,UpdatedAtUtc) VALUES ({taskId},{"Legacy"},{"Synthetic legacy"},{"fake"},{true},{now.UtcTicks},{now.UtcTicks})");
            var json = System.Text.Json.JsonSerializer.Serialize(new ScheduleRecurrence { Mode = ScheduleMode.Interval, EveryMinutes = 10 });
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Schedules (Id,TaskDefinitionId,PayloadSchemaVersion,Mode,RecurrenceJson,TimeZoneId,Enabled,Version,CreatedAtUtc,UpdatedAtUtc,NextRunAtUtc) VALUES ({id},{taskId},{1},{"Interval"},{json},{"UTC"},{true},{0},{now.UtcTicks},{now.UtcTicks},{now.AddMinutes(10).UtcTicks})");
            await db.Database.MigrateAsync();
        }
        var restored = (await new SqliteScheduleRepository(database).GetAsync(id))!;
        Assert.Equal(MisfirePolicy.RunOnce, restored.Schedule.MisfirePolicy); Assert.Equal(3, restored.Schedule.MaxCatchUpRuns);
        Assert.Equal(ScheduledDesktopPolicy.StartWhenIdle, restored.Schedule.DesktopPolicy);
        Assert.Equal(now.AddMinutes(10), restored.Schedule.NextRunAtUtc); Assert.Null(restored.Schedule.CatchUpRemaining);
    }

    private static ScheduledTask Draft(DateTimeOffset now, MisfirePolicy policy)
    {
        var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Misfire fixture", Instructions = "Synthetic scheduled task", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
        return new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 60 },
            TimeZoneId = "Europe/Kyiv", MisfirePolicy = policy, CreatedAtUtc = now, UpdatedAtUtc = now });
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T08:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Runtime : IAgentRuntime
    {
        private readonly ConcurrentDictionary<Guid, int> calls = new();
        public int Count(Guid id) => calls.GetValueOrDefault(id);
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { calls.AddOrUpdate(request.RunId, 1, (_, count) => count + 1); return Task.FromResult(new AgentRunResult { Status = AgentResultStatus.Completed, Summary = "Synthetic misfire result" }); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new(); public Clock Clock = new(); public Runtime Runtime = new();
        public SqliteScheduleRepository Schedules = null!; public SqliteRunRepository Repository = null!;
        public TaskRunCoordinator Runs = null!; public ScheduleDispatcher Dispatcher = null!; public ScheduledTask Created = null!;
        private CancellationTokenSource stop = new(); private Task runner = null!;
        public static async Task<Fixture> Create(MisfirePolicy policy)
        {
            var fixture = new Fixture(); await using (var db = fixture.Database.Open()) await db.Database.MigrateAsync();
            fixture.Schedules = new(fixture.Database, fixture.Clock); fixture.Repository = new(fixture.Database);
            fixture.Created = await fixture.Schedules.SaveAsync(Draft(fixture.Clock.Now, policy)); fixture.Start(); return fixture;
        }
        private void Start()
        { Runs = new(Repository); Dispatcher = new(Schedules, Runs, timeProvider: Clock); runner = new TaskRunner(Runs, Runtime).RunAsync(stop.Token); }
        public async Task Restart()
        {
            stop.Cancel(); await runner; Runs.Dispose(); stop.Dispose(); stop = new();
            Schedules = new(new SqliteAgentDbContextFactory(Database.DatabasePath), Clock); Repository = new(Database);
            var safeQueue = await new RunRecovery(Repository).RecoverAsync();
            Runs = new(Repository); await Runs.RestoreHistoryAsync(safeQueue); Dispatcher = new(Schedules, Runs, timeProvider: Clock);
            runner = new TaskRunner(Runs, Runtime).RunAsync(stop.Token);
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await runner; Runs.Dispose(); stop.Dispose(); Database.Dispose(); }
    }
}
