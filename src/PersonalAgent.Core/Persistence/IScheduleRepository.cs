using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Persistence;

public interface IScheduleRepository
{
    Task<ScheduledTask> SaveAsync(ScheduledTask draft, long? expectedVersion = null, CancellationToken ct = default);
    Task<ScheduledTask?> GetAsync(Guid scheduleId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskRun>> ListRunsAsync(Guid scheduleId, CancellationToken ct = default);
    Task<ScheduledTask?> GetForRunAsync(Guid runId, CancellationToken ct = default);
    Task<ScheduledTask?> CoalescePendingAsync(Guid runId, long expectedScheduleVersion, DateTimeOffset observedAtUtc, CancellationToken ct = default);
    Task<Guid?> FindOccurrenceRunAsync(Guid scheduleId, DateTimeOffset occurrenceUtc, CancellationToken ct = default);
    Task<bool> HasActiveRunAsync(Guid scheduleId, CancellationToken ct = default);
    Task<bool> AdvanceAsync(Guid scheduleId, long expectedVersion, DateTimeOffset occurrenceUtc, DateTimeOffset observedAtUtc, CancellationToken ct = default);
}
