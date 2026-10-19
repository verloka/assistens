using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Persistence;

public interface IScheduleRepository
{
    Task<ScheduledTask> SaveAsync(ScheduledTask draft, long? expectedVersion = null, CancellationToken ct = default);
    Task<ScheduledTask?> GetAsync(Guid scheduleId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default);
}
