using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Persistence;

public enum ToolCallDispatchStatus { Dispatched, Acknowledged, OutcomeUnknown }
public sealed record ToolCallDispatch(Guid Id, Guid RunId, string ToolCallId, string ToolId, Guid ApprovalRequestId,
    string ActionHash, ToolCallDispatchStatus Status, DateTimeOffset DispatchedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed class ToolDispatchDeniedException() : InvalidOperationException("The exact action approval or run is not valid for dispatch.");
public sealed class ToolDispatchConflictException(bool outcomeUnknown) : InvalidOperationException("The operation was already dispatched and cannot be repeated.")
{ public bool OutcomeUnknown { get; } = outcomeUnknown; }

public interface IToolCallRepository
{
    Task<ToolCallDispatch> BeginAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation,
        long expectedRunVersion, Guid approvalRequestId, CancellationToken ct = default);
    Task CompleteAsync(Guid dispatchId, ToolResultStatus outcome, CancellationToken ct = default);
    Task<IReadOnlyList<ToolCallDispatch>> ListAsync(Guid runId, CancellationToken ct = default);
    // Startup only, after obtaining exclusive store ownership. Never retries an operation.
    Task RecoverDispatchedAsync(CancellationToken ct = default);
}
