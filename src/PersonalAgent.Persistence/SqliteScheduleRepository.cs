using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteScheduleRepository(IDbContextFactory<AgentDbContext> factory, TimeProvider? timeProvider = null) : IScheduleRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<ScheduledTask> SaveAsync(ScheduledTask draft, long? expectedVersion = null, CancellationToken ct = default)
    {
        Validate(draft); var recurrence = draft.Schedule.Recurrence.Snapshot(); var json = JsonSerializer.Serialize(recurrence);
        await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Schedules.SingleOrDefaultAsync(value => value.Id == draft.Schedule.Id, ct);
        if (row is not null && row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported schedule payload version.");
        if (row is null ? expectedVersion is not null || draft.Schedule.Version != 0
            : expectedVersion != row.Version || draft.Schedule.Version != row.Version) throw new ScheduleVersionConflictException();
        if (row is not null && row.TaskDefinitionId != draft.Task.Id) throw new ArgumentException("A schedule cannot replace its task identity.");
        var task = await db.TaskDefinitions.SingleOrDefaultAsync(value => value.Id == draft.Task.Id, ct);
        var now = clock.GetUtcNow().ToUniversalTime();
        var sameCadence = row is not null && row.RecurrenceJson == json && row.TimeZoneId == draft.Schedule.TimeZoneId
            && row.Enabled && draft.Schedule.Enabled && task?.Enabled == true && draft.Task.Enabled;
        var anchor = row?.CreatedAtUtc ?? now;
        var next = !draft.Schedule.Enabled || !draft.Task.Enabled ? null : sameCadence ? row!.NextRunAtUtc
            : ScheduleCalculator.Next(recurrence, draft.Schedule.TimeZoneId, anchor, now);
        if (draft.Schedule.Enabled && draft.Task.Enabled && !sameCadence && recurrence.Mode == ScheduleMode.Once && next is null)
            throw new ArgumentException("Choose a future time for an enabled one-time schedule.");
        if (row is null) db.Schedules.Add(row = new() { Id = draft.Schedule.Id, TaskDefinitionId = draft.Task.Id, CreatedAtUtc = now });
        else row.Version = checked(row.Version + 1);
        if (task is null) db.TaskDefinitions.Add(task = new() { Id = draft.Task.Id, CreatedAtUtc = now });
        task.Name = draft.Task.Name; task.Instructions = draft.Task.Instructions; task.ModelProfileId = draft.Task.ModelProfileId;
        task.Enabled = draft.Task.Enabled; task.UpdatedAtUtc = now;
        row.Mode = recurrence.Mode; row.RecurrenceJson = json; row.TimeZoneId = draft.Schedule.TimeZoneId;
        row.Enabled = draft.Schedule.Enabled; row.UpdatedAtUtc = now; row.NextRunAtUtc = next;
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ScheduleVersionConflictException(); }
        return Read(row, task);
    }
    public async Task<ScheduledTask?> GetAsync(Guid scheduleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var result = await (from schedule in db.Schedules.AsNoTracking() join task in db.TaskDefinitions.AsNoTracking() on schedule.TaskDefinitionId equals task.Id
            where schedule.Id == scheduleId select new { Schedule = schedule, Task = task }).SingleOrDefaultAsync(ct);
        return result is null ? null : Read(result.Schedule, result.Task);
    }
    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (from schedule in db.Schedules.AsNoTracking() join task in db.TaskDefinitions.AsNoTracking() on schedule.TaskDefinitionId equals task.Id
            orderby schedule.CreatedAtUtc, schedule.Id select new { Schedule = schedule, Task = task }).ToArrayAsync(ct);
        return rows.Select(value => Read(value.Schedule, value.Task)).ToArray();
    }
    private static void Validate(ScheduledTask draft)
    {
        if (draft.Task is not { } task || draft.Schedule is not { } schedule || schedule.SchemaVersion != 1 || task.Id == Guid.Empty || schedule.Id == Guid.Empty
            || schedule.TaskDefinitionId != task.Id || schedule.Version < 0 || schedule.Recurrence is null
            || string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 256 || string.IsNullOrWhiteSpace(task.Instructions) || task.Instructions.Length > 16_384
            || string.IsNullOrWhiteSpace(task.ModelProfileId) || task.ModelProfileId.Length > 256) throw new ArgumentException("Invalid scheduled task identity or configuration.");
        schedule.Recurrence.Snapshot(); ScheduleCalculator.ResolveTimeZone(schedule.TimeZoneId);
    }
    private static ScheduledTask Read(ScheduleRow row, TaskDefinitionRow task)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported schedule payload version.");
        var recurrence = ScheduleRecurrence.Parse(row.RecurrenceJson);
        var result = new ScheduledTask(new() { Id = task.Id, Name = task.Name, Instructions = task.Instructions, ModelProfileId = task.ModelProfileId,
                Enabled = task.Enabled, CreatedAtUtc = task.CreatedAtUtc, UpdatedAtUtc = task.UpdatedAtUtc },
            new() { Id = row.Id, TaskDefinitionId = row.TaskDefinitionId, Recurrence = recurrence, TimeZoneId = row.TimeZoneId, Enabled = row.Enabled,
                Version = row.Version, CreatedAtUtc = row.CreatedAtUtc, UpdatedAtUtc = row.UpdatedAtUtc, NextRunAtUtc = row.NextRunAtUtc });
        Validate(result);
        if (row.Mode != recurrence.Mode || row.UpdatedAtUtc < row.CreatedAtUtc
            || !row.Enabled && row.NextRunAtUtc is not null
            || row.NextRunAtUtc is { } next && (next.UtcTicks == 0 || ScheduleCalculator.Next(recurrence, row.TimeZoneId, row.CreatedAtUtc, next.AddTicks(-1)) != next))
            throw new InvalidDataException("Persisted schedule cadence or next occurrence is inconsistent.");
        return result;
    }
}
