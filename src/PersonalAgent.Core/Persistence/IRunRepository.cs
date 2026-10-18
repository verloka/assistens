using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Persistence;

public sealed record StoredRun(TaskRun Run, AgentRequest Request, IReadOnlyList<RunEvent> Events, AgentRunResult? Result);

// A successful update commits the run snapshot and appended events together.
// Stale versions fail without replay; command/occurrence idempotency is a separate boundary.
public interface IRunRepository
{
    Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> initialEvents, CancellationToken cancellationToken = default);
    Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents, AgentRunResult? result = null, CancellationToken cancellationToken = default);
}
