namespace PersonalAgent.Core.Domain;

public static class TaskRunStateMachine
{
    public static bool IsTerminal(RunStatus status) => status is
        RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Skipped or RunStatus.Interrupted;

    public static bool CanTransition(RunStatus from, RunStatus to) => from switch
    {
        RunStatus.Created => to is RunStatus.Queued or RunStatus.Cancelled or RunStatus.Failed or RunStatus.Skipped,
        RunStatus.Queued => to is RunStatus.Running or RunStatus.WaitingForDesktop or RunStatus.WaitingForUser
            or RunStatus.Blocked or RunStatus.Cancelled or RunStatus.Failed or RunStatus.Skipped,
        RunStatus.Running => to is RunStatus.WaitingForDesktop or RunStatus.WaitingForUser or RunStatus.WaitingForApproval
            or RunStatus.Pausing or RunStatus.Blocked or RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Interrupted,
        RunStatus.WaitingForDesktop or RunStatus.WaitingForUser or RunStatus.WaitingForApproval =>
            to is RunStatus.Running or RunStatus.Pausing or RunStatus.Blocked or RunStatus.Cancelled or RunStatus.Failed or RunStatus.Interrupted,
        RunStatus.Pausing => to is RunStatus.Paused or RunStatus.PausedForUser or RunStatus.Cancelled or RunStatus.Failed or RunStatus.Interrupted,
        RunStatus.Paused or RunStatus.PausedForUser => to is RunStatus.Running or RunStatus.Blocked or RunStatus.Cancelled or RunStatus.Interrupted,
        // Returning from Blocked is an explicit requeue, not an automatic continuation.
        RunStatus.Blocked => to is RunStatus.Queued or RunStatus.Cancelled or RunStatus.Failed or RunStatus.Interrupted,
        _ => false
    };

    // Produces a new snapshot; the store must compare-and-swap RunVersion atomically.
    // This method is not a GUI lease/gate and cannot establish that native input stopped.
    public static TaskRun Transition(TaskRun run, RunStatus next, long expectedVersion,
        DateTimeOffset timestamp, string? reason = null, PauseScope? pauseScope = null)
    {
        if (run.RunVersion != expectedVersion)
            throw new RunVersionConflictException(run.Id, expectedVersion, run.RunVersion);
        if (!CanTransition(run.Status, next))
            throw new InvalidOperationException($"Run {run.Id}: transition {run.Status} -> {next} is not allowed.");
        var utc = timestamp.ToUniversalTime();
        if (utc < run.CreatedAtUtc || (run.StartedAtUtc is { } started && utc < started))
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Transition cannot precede run creation/start.");
        if (next is RunStatus.Blocked or RunStatus.Failed or RunStatus.Interrupted && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A failure/block/interruption requires a reason.", nameof(reason));
        var scope = next is RunStatus.Pausing or RunStatus.Paused or RunStatus.PausedForUser
            ? pauseScope ?? run.PauseScope ?? Domain.PauseScope.EntireRun
            : (PauseScope?)null;
        if (scope is not null && !Enum.IsDefined(scope.Value))
            throw new ArgumentOutOfRangeException(nameof(pauseScope));

        return run with
        {
            Status = next,
            RunVersion = checked(run.RunVersion + 1),
            Reason = reason,
            PauseScope = scope,
            StartedAtUtc = next == RunStatus.Running ? run.StartedAtUtc ?? utc : run.StartedAtUtc,
            EndedAtUtc = IsTerminal(next) ? utc : null
        };
    }
}

public sealed class RunVersionConflictException(Guid runId, long expectedVersion, long actualVersion)
    : InvalidOperationException($"Run {runId}: expected version {expectedVersion}, actual {actualVersion}.")
{
    public Guid RunId { get; } = runId;
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}
