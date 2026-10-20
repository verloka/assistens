using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;

namespace PersonalAgent.IntegrationTests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task CompetingTicksCreateOneAtomicRunPerUtcOccurrenceAndCoalesceMissedIntervals()
    {
        await using var fixture = await Fixture.Create(); fixture.Clock.Now += TimeSpan.FromMinutes(35);
        var dispatchers = Enumerable.Range(0, 8).Select(_ => new ScheduleDispatcher(fixture.Schedules, fixture.Runs, timeProvider: fixture.Clock));
        await Task.WhenAll(dispatchers.Select(value => value.TickAsync()));
        var run = Assert.Single(await fixture.Repository.ListAsync());
        Assert.Equal(fixture.Task.Task.Id, run.TaskDefinitionId); Assert.Equal(RunStatus.Queued, run.Status);
        var occurrence = fixture.Task.Schedule.NextRunAtUtc!.Value;
        Assert.Equal(run.Id, await fixture.Schedules.FindOccurrenceRunAsync(fixture.Task.Schedule.Id, occurrence.ToOffset(TimeSpan.FromHours(3))));
        Assert.Equal(fixture.Task.Schedule.CreatedAtUtc.AddMinutes(40), (await fixture.Schedules.GetAsync(fixture.Task.Schedule.Id))!.Schedule.NextRunAtUtc);
        fixture.StartRunner(); await fixture.Runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Runtime.Calls); Assert.Empty(await fixture.Dispatcher.TickAsync());
        Assert.Equal(2, (await fixture.Repository.GetAsync(run.Id))!.Events.Count(value => value.EventType is RunEventType.RunCreated or RunEventType.RunStarted));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DisableOrEditDuringSubmissionCannotAdmitAnObsoleteTask(bool edit)
    {
        await using var fixture = await Fixture.Create(); fixture.Clock.Now += TimeSpan.FromMinutes(10);
        var gate = new CommandsGate(fixture.Runs); var dispatcher = new ScheduleDispatcher(fixture.Schedules, gate, timeProvider: fixture.Clock);
        var ticking = dispatcher.TickAsync(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = fixture.Task with { Task = fixture.Task.Task with { Instructions = edit ? "Edited instruction" : fixture.Task.Task.Instructions },
            Schedule = fixture.Task.Schedule with { Enabled = edit } };
        await fixture.Schedules.SaveAsync(changed, fixture.Task.Schedule.Version);
        gate.Release.TrySetResult(); var outcome = Assert.Single(await ticking);
        Assert.Equal("ScheduleChanged", outcome.ErrorCode); Assert.Empty(await fixture.Repository.ListAsync()); Assert.Equal(0, fixture.Runtime.Calls);
        if (edit)
        {
            await fixture.Dispatcher.TickAsync(); var run = Assert.Single(await fixture.Repository.ListAsync());
            Assert.Equal("Edited instruction", run.Instructions);
        }
    }

    [Fact]
    public async Task ActiveOccurrenceHoldsTheNextSlotUntilFinishedWithoutOverlappingRuns()
    {
        await using var fixture = await Fixture.Create(); fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await fixture.Dispatcher.TickAsync(); var first = Assert.Single(await fixture.Repository.ListAsync());
        fixture.Clock.Now += TimeSpan.FromMinutes(20);
        Assert.Equal("ScheduleRunActive", Assert.Single(await fixture.Dispatcher.TickAsync()).ErrorCode); Assert.Single(await fixture.Repository.ListAsync());
        fixture.StartRunner(); await fixture.Runs.WaitForTerminalAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var next = Assert.Single(await fixture.Dispatcher.TickAsync()); Assert.NotEqual(first.Id, next.RunId);
        await fixture.Runs.WaitForTerminalAsync(next.RunId!.Value).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, fixture.Runtime.Calls);
    }

    [Fact]
    public async Task AdvanceWithoutPersistedRunAndDisabledTemplateCannotLoseOrSubmitAnOccurrence()
    {
        await using var fixture = await Fixture.Create(); fixture.Clock.Now += TimeSpan.FromMinutes(10);
        Assert.False(await fixture.Schedules.AdvanceAsync(fixture.Task.Schedule.Id, fixture.Task.Schedule.Version, fixture.Task.Schedule.NextRunAtUtc!.Value, fixture.Clock.Now));
        Assert.Equal(fixture.Task.Schedule.NextRunAtUtc, (await fixture.Schedules.GetAsync(fixture.Task.Schedule.Id))!.Schedule.NextRunAtUtc);
        await fixture.Schedules.SaveAsync(fixture.Task with { Task = fixture.Task.Task with { Enabled = false } }, fixture.Task.Schedule.Version);
        Assert.Empty(await fixture.Dispatcher.TickAsync()); Assert.Empty(await fixture.Repository.ListAsync());
    }

    [Fact]
    public async Task HostedRestartAfterRunCommitBeforeScheduleAdvanceFindsOriginalRunWithoutExecutionReplay()
    {
        using var database = new TemporaryAgentDatabase(); var clock = new Clock();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var schedules = new SqliteScheduleRepository(database, clock); var task = await schedules.SaveAsync(Draft(clock.Now));
        clock.Now += TimeSpan.FromMinutes(35); var failedAdvance = new FailedAdvance(schedules); var firstRuntime = new Runtime();
        Guid firstId;
        using (var first = Host(database, clock, firstRuntime, failedAdvance))
        {
            await first.StartAsync(); await failedAdvance.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var runs = first.Services.GetRequiredService<TaskRunCoordinator>(); firstId = Assert.Single(runs.GetRuns()).Id;
            await runs.WaitForTerminalAsync(firstId).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, firstRuntime.Calls);
            await first.StopAsync();
        }
        Assert.Equal(task.Schedule.NextRunAtUtc, (await schedules.GetAsync(task.Schedule.Id))!.Schedule.NextRunAtUtc);
        for (var restart = 0; restart < 2; restart++)
        {
            var runtime = new Runtime(); using var host = Host(database, clock, runtime);
            await host.StartAsync();
            await Until(async () => (await schedules.GetAsync(task.Schedule.Id))!.Schedule.NextRunAtUtc > clock.Now);
            Assert.Equal(0, runtime.Calls); Assert.Equal(firstId, Assert.Single(await new SqliteRunRepository(database).ListAsync()).Id);
            await host.StopAsync();
        }
        Assert.Equal(1, firstRuntime.Calls);
    }

    private static IHost Host(TemporaryAgentDatabase database, Clock clock, Runtime runtime, IScheduleRepository? schedules = null) =>
        AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock); services.AddSingleton<IAgentRuntime>(runtime);
            if (schedules is not null) services.AddSingleton(schedules);
        }).Build();
    private static async Task Until(Func<Task<bool>> condition) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!await condition()) await Task.Delay(20, stop.Token); }
    private static ScheduledTask Draft(DateTimeOffset now)
    {
        var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Scheduler fixture", Instructions = "Synthetic schedule", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
        return new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 },
            TimeZoneId = "UTC", CreatedAtUtc = now, UpdatedAtUtc = now });
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object sync = new(); private DateTimeOffset now = DateTimeOffset.Parse("2026-10-19T08:00:00Z");
        public DateTimeOffset Now { get { lock (sync) return now; } set { lock (sync) now = value; } }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Runtime : IAgentRuntime
    {
        public int Calls;
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new AgentRunResult { Status = AgentResultStatus.Completed, Summary = "Synthetic scheduled result" }); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new(); public Clock Clock = new(); public Runtime Runtime = new();
        public IScheduleRepository Schedules = null!; public IRunRepository Repository = null!; public TaskRunCoordinator Runs = null!;
        public ScheduledTask Task = null!; public ScheduleDispatcher Dispatcher = null!;
        private CancellationTokenSource stop = new(); private Task? runner;
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); await using (var db = fixture.Database.Open()) await db.Database.MigrateAsync();
            fixture.Schedules = new SqliteScheduleRepository(fixture.Database, fixture.Clock); fixture.Repository = new SqliteRunRepository(fixture.Database);
            fixture.Task = await fixture.Schedules.SaveAsync(Draft(fixture.Clock.Now)); fixture.Runs = new(fixture.Repository);
            fixture.Dispatcher = new(fixture.Schedules, fixture.Runs, timeProvider: fixture.Clock); return fixture;
        }
        public void StartRunner() => runner = new TaskRunner(Runs, Runtime).RunAsync(stop.Token);
        public async ValueTask DisposeAsync() { stop.Cancel(); if (runner is not null) await runner; Runs.Dispose(); stop.Dispose(); Database.Dispose(); }
    }
    private sealed class CommandsGate(ITaskCommands inner) : ITaskCommands
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return await inner.SubmitAsync(command, ct); }
        public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken ct) => inner.PauseAsync(command, ct);
        public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken ct) => inner.ResumeAsync(command, ct);
        public Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken ct) => inner.CancelAsync(command, ct);
        public Task<TaskRun> SkipAsync(SkipRunCommand command, CancellationToken ct) => inner.SkipAsync(command, ct);
    }
    private sealed class FailedAdvance(IScheduleRepository inner) : IScheduleRepository
    {
        public Task<IReadOnlyList<TaskRun>> ListRunsAsync(Guid id, CancellationToken ct = default) => inner.ListRunsAsync(id, ct);
        public Task<ScheduledTask?> GetForRunAsync(Guid id, CancellationToken ct = default) => inner.GetForRunAsync(id, ct);
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScheduledTask> SaveAsync(ScheduledTask task, long? version = null, CancellationToken ct = default) => inner.SaveAsync(task, version, ct);
        public Task<ScheduledTask?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<Guid?> FindOccurrenceRunAsync(Guid id, DateTimeOffset occurrence, CancellationToken ct = default) => inner.FindOccurrenceRunAsync(id, occurrence, ct);
        public Task<bool> HasActiveRunAsync(Guid id, CancellationToken ct = default) => inner.HasActiveRunAsync(id, ct);
        public Task<bool> AdvanceAsync(Guid id, long version, DateTimeOffset occurrence, DateTimeOffset now, CancellationToken ct = default)
        { Entered.TrySetResult(); throw new InvalidOperationException("private-fixture-advance-error"); }
    }
}
