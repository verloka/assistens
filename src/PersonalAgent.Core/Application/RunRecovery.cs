using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using System.Text.Json;

namespace PersonalAgent.Core.Application;

public sealed class RunRecovery(IRunRepository repository, IToolCallRepository? toolCalls = null)
{
    public async Task<IReadOnlyList<Guid>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var queued = new List<Guid>();
        if (toolCalls is not null) await toolCalls.RecoverDispatchedAsync(cancellationToken);
        foreach (var summary in await repository.ListAsync(cancellationToken))
        {
            if (TaskRunStateMachine.IsTerminal(summary.Status)) continue;
            var stored = (await repository.GetAsync(summary.Id, cancellationToken))!;
            var previous = stored.Run;
            if (previous.Status == RunStatus.Blocked && previous.ErrorCode == "RecoveryNeedsReview") continue;
            if (previous.ErrorCode == "InputCleanupUncertain") continue; // Retain failed-release evidence and manual block.
            var unstarted = previous.Status is RunStatus.Created or RunStatus.Queued;
            // Current known-safe runtime is local fake with no tools. Unknown models/tools never auto-start.
            var safe = unstarted && stored.Request.ModelProfileId == "fake" && stored.Request.AllowedToolIds.Count == 0;
            if (safe && previous.Status == RunStatus.Queued) { queued.Add(previous.Id); continue; }
            var status = safe ? RunStatus.Queued : unstarted ? RunStatus.Blocked : RunStatus.Interrupted;
            var unknown = toolCalls is not null && (await toolCalls.ListAsync(previous.Id, cancellationToken))
                .Any(call => call.Status == ToolCallDispatchStatus.OutcomeUnknown);
            var reason = safe ? "Recovered unstarted local diagnostic."
                : unstarted ? "Queued task needs review after restart; this model/tool set cannot start automatically."
                : unknown ? "A dispatched operation has an unknown outcome. Verify its real-world result before starting another task; automatic replay is disabled."
                : "Application restarted during execution. Verify external effects before starting another task; automatic replay is disabled.";
            var next = TaskRunStateMachine.Transition(previous, status, previous.RunVersion, DateTimeOffset.UtcNow, reason) with
            {
                Summary = reason, ErrorCode = unknown ? "ToolOutcomeUnknown" : status == RunStatus.Interrupted ? "RunInterrupted" : status == RunStatus.Blocked ? "RecoveryNeedsReview" : null
            };
            var step = new RunEvent
            {
                Id = Guid.NewGuid(), RunId = previous.Id, Sequence = stored.Events.Count == 0 ? 1 : stored.Events[^1].Sequence + 1,
                EventType = status == RunStatus.Interrupted ? RunEventType.RunFinished : RunEventType.StatusChanged,
                TimestampUtc = DateTimeOffset.UtcNow, Message = reason,
                Payload = JsonSerializer.SerializeToElement(new { previousStatus = previous.Status.ToString(), automaticReplay = false })
            };
            await repository.UpdateAsync(next, previous.RunVersion, [step], cancellationToken: cancellationToken);
            if (safe) queued.Add(previous.Id);
        }
        return queued;
    }
}
