using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;

namespace PersonalAgent.IntegrationTests;

public sealed class ScheduleRepositoryTests
{
    [Theory]
    [InlineData(ScheduleMode.Once)] [InlineData(ScheduleMode.Interval)] [InlineData(ScheduleMode.Daily)] [InlineData(ScheduleMode.Weekdays)]
    public async Task EverySupportedModePersistsItsOwnStrictConfigurationAndUtcOccurrence(ScheduleMode mode)
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var draft = Draft(clock.Now); var recurrence = mode switch
        {
            ScheduleMode.Once => new ScheduleRecurrence { Mode = mode, OnceAtUtc = clock.Now.AddHours(1) },
            ScheduleMode.Interval => new ScheduleRecurrence { Mode = mode, EveryMinutes = 10 },
            ScheduleMode.Daily => new ScheduleRecurrence { Mode = mode, LocalTime = new(9, 30) },
            _ => new ScheduleRecurrence { Mode = mode, LocalTime = new(9, 30), Days = [DayOfWeek.Monday, DayOfWeek.Friday] }
        };
        var repository = new SqliteScheduleRepository(database, clock);
        var saved = await repository.SaveAsync(draft with { Schedule = draft.Schedule with { Recurrence = recurrence } });
        var restored = (await repository.GetAsync(saved.Schedule.Id))!;
        Assert.Equal(mode, restored.Schedule.Recurrence.Mode); Assert.NotNull(restored.Schedule.NextRunAtUtc);
        Assert.Equal(TimeSpan.Zero, restored.Schedule.NextRunAtUtc.Value.Offset); Assert.True(restored.Schedule.NextRunAtUtc > clock.Now);
        Assert.Equal(recurrence.Days.Order(), restored.Schedule.Recurrence.Days);
    }

    [Fact]
    public async Task ScheduleAndExistingTaskDefinitionStorageRoundTripWithComputedUtcNextAndNoExecution()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var draft = Draft(clock.Now);
        var saved = await repository.SaveAsync(draft with { Schedule = draft.Schedule with { NextRunAtUtc = clock.Now.AddYears(1) } });
        Assert.Equal(clock.Now.AddMinutes(10), saved.Schedule.NextRunAtUtc); Assert.Equal(TimeSpan.Zero, saved.Schedule.NextRunAtUtc!.Value.Offset);
        var reopened = new SqliteScheduleRepository(new SqliteAgentDbContextFactory(database.DatabasePath), clock);
        var stored = (await reopened.GetAsync(saved.Schedule.Id))!;
        Assert.Equal(saved.Task, stored.Task); Assert.Equal(saved.Schedule.NextRunAtUtc, stored.Schedule.NextRunAtUtc);
        Assert.Equal(ScheduleMode.Interval, stored.Schedule.Recurrence.Mode); Assert.Equal("Europe/Kyiv", stored.Schedule.TimeZoneId);
        Assert.Single(await reopened.ListAsync());
        await using var db = database.Open(); Assert.Single(await db.TaskDefinitions.ToArrayAsync()); Assert.Empty(await db.TaskRuns.ToArrayAsync());
    }

    [Fact]
    public async Task InvalidCadenceTimezoneIdentityAndPastOnceCannotPersistPartialTemplate()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var draft = Draft(clock.Now);
        foreach (var invalid in new[]
        {
            draft with { Schedule = draft.Schedule with { Recurrence = draft.Schedule.Recurrence with { EveryMinutes = 0 } } },
            draft with { Schedule = draft.Schedule with { TimeZoneId = "unknown/synthetic-zone" } },
            draft with { Schedule = draft.Schedule with { TaskDefinitionId = Guid.NewGuid() } },
            draft with { Task = draft.Task with { Instructions = "" } },
            draft with { Schedule = draft.Schedule with { Recurrence = new() { Mode = ScheduleMode.Once, OnceAtUtc = clock.Now } } }
        }) await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(invalid));
        await using var db = database.Open(); Assert.Empty(await db.Schedules.ToArrayAsync()); Assert.Empty(await db.TaskDefinitions.ToArrayAsync());
    }

    [Fact]
    public async Task SameCadenceEditPreservesDueOccurrenceAndDisableReenableKeepsTaskAndAnchor()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var saved = await repository.SaveAsync(Draft(clock.Now));
        clock.Now += TimeSpan.FromMinutes(35);
        var edited = await repository.SaveAsync(saved with { Task = saved.Task with { Name = "Renamed" },
            Schedule = saved.Schedule with { NextRunAtUtc = clock.Now.AddDays(1) } }, saved.Schedule.Version);
        Assert.Equal(saved.Schedule.NextRunAtUtc, edited.Schedule.NextRunAtUtc); Assert.Equal(1, edited.Schedule.Version);
        Assert.Equal(saved.Schedule.CreatedAtUtc, edited.Schedule.CreatedAtUtc);
        var disabled = await repository.SaveAsync(edited with { Schedule = edited.Schedule with { Enabled = false } }, edited.Schedule.Version);
        Assert.Null(disabled.Schedule.NextRunAtUtc);
        var enabled = await repository.SaveAsync(disabled with { Schedule = disabled.Schedule with { Enabled = true } }, disabled.Schedule.Version);
        Assert.Equal(saved.Schedule.CreatedAtUtc.AddMinutes(40), enabled.Schedule.NextRunAtUtc);
        Assert.Equal(saved.Task.Id, enabled.Task.Id); Assert.Equal(saved.Schedule.CreatedAtUtc, enabled.Schedule.CreatedAtUtc);
    }

    [Fact]
    public async Task CompetingVersionedSavesHaveOneWinnerAndNoPartialTaskOverwrite()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var saved = await repository.SaveAsync(Draft(clock.Now));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try { return await new SqliteScheduleRepository(database, clock).SaveAsync(saved with { Task = saved.Task with { Name = "Writer " + index } }, saved.Schedule.Version); }
            catch (ScheduleVersionConflictException) { return null; }
        })).ToArray();
        start.SetResult(); var winner = Assert.Single(await Task.WhenAll(attempts), value => value is not null)!;
        var stored = (await repository.GetAsync(saved.Schedule.Id))!;
        Assert.Equal(winner.Task.Name, stored.Task.Name); Assert.Equal(1, stored.Schedule.Version); Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task UnsupportedOrInconsistentStoredScheduleFailsClosed()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var saved = await repository.SaveAsync(Draft(clock.Now));
        await using var db = database.Open(); var row = await db.Schedules.SingleAsync();
        row.PayloadSchemaVersion = 2; await db.SaveChangesAsync(); await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync(saved.Schedule.Id));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SaveAsync(saved, saved.Schedule.Version));
        row.PayloadSchemaVersion = 1; row.NextRunAtUtc = clock.Now.AddMinutes(11); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => repository.GetAsync(saved.Schedule.Id));
    }

    [Fact]
    public async Task SharedTaskTemplateCanBeDisabledWithoutCorruptingOtherCadencesOrCreatingRuns()
    {
        using var database = new TemporaryAgentDatabase(); await Migrate(database); var clock = new Clock();
        var repository = new SqliteScheduleRepository(database, clock); var first = await repository.SaveAsync(Draft(clock.Now));
        await repository.SaveAsync(first with { Schedule = first.Schedule with { Id = Guid.NewGuid() } });
        await repository.SaveAsync(first with { Task = first.Task with { Enabled = false } }, first.Schedule.Version);
        var schedules = await repository.ListAsync(); Assert.Equal(2, schedules.Count); Assert.All(schedules, value => Assert.False(value.Task.Enabled));
        await using var db = database.Open(); Assert.Empty(await db.TaskRuns.ToArrayAsync());
    }
    private static async Task Migrate(TemporaryAgentDatabase database) { await using var db = database.Open(); await db.Database.MigrateAsync(); }
    private static ScheduledTask Draft(DateTimeOffset now)
    {
        var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Ten minute check", Instructions = "Synthetic scheduled task", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
        return new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 },
            TimeZoneId = "Europe/Kyiv", CreatedAtUtc = now, UpdatedAtUtc = now });
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T08:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
}
