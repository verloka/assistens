using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Persistence;

public sealed record StoredRun(TaskRun Run, AgentRequest Request, IReadOnlyList<RunEvent> Events, AgentRunResult? Result);
public sealed record ScheduleSubmissionClaim(Guid ScheduleId, long ExpectedVersion, DateTimeOffset OccurrenceUtc);
public sealed class ScheduleSubmissionConflictException() : InvalidOperationException("The schedule or task changed before this occurrence could be admitted.");
public sealed record RunSubmissionIdentity(string Source, string Key, string IntentHash, ScheduleSubmissionClaim? Schedule = null);
public sealed class RunSubmissionConflictException() : InvalidOperationException("Submission key was already used with different execution inputs.");

// A successful update commits the run snapshot and appended events together.
// Stale versions fail without replay; command/occurrence idempotency is a separate boundary.
public interface IRunRepository
{
    Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> initialEvents, CancellationToken cancellationToken = default);
    Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> initialEvents,
        RunSubmissionIdentity identity, CancellationToken cancellationToken = default);
    Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents, AgentRunResult? result = null, CancellationToken cancellationToken = default);
}
