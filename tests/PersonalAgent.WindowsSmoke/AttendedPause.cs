using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.WindowsSmoke;

internal static class AttendedPause
{
    // A tool can still be Pausing when the local gate and durable handoff are already confirmed.
    public static bool Acknowledged(TaskRun run, bool handoffConfirmed) => handoffConfirmed && run.PauseScope is not null;

    public static async Task WaitForResumeAsync(TaskRunCoordinator runs, Guid runId, Action ready, CancellationToken ct)
    {
        var boundary = runs.WaitAtBoundaryAsync(runId, new RunStepAccess(true, true, false), ct);
        while (!boundary.IsCompleted && runs.GetRun(runId).Status is not (RunStatus.Paused or RunStatus.PausedForUser))
            await Task.Delay(20, ct).ConfigureAwait(false);
        if (boundary.IsCompleted) { await boundary.ConfigureAwait(false); return; }
        ready();
        await boundary.ConfigureAwait(false);
    }
}
