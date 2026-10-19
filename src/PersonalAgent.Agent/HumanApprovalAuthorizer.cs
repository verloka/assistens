using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using System.Collections.Concurrent;

namespace PersonalAgent.Agent;

// Run-scoped trusted composition supplies policy, authenticated owner and exact operation presentation.
// No SDK session is restarted and no tool call is replayed while this gate awaits a decision.
public sealed class HumanApprovalAuthorizer(TaskRunCoordinator runs, IApprovalRepository approvals,
    Func<ToolDescriptor, ToolExecutionContext, ToolPermissionDecision> evaluate,
    ApprovalOwner owner, Func<ToolDescriptor, ToolArguments, (string Description, string Destination)> describe,
    IDesktopAccessCoordinator desktop, Func<CancellationToken, Task>? yieldDesktop = null,
    TimeSpan? lifetime = null, TimeProvider? timeProvider = null) : IToolInvocationAuthorizer
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid RunId, string CallId), (ApprovalRequest Request, long RunVersion)> receipts = new();

    public async ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision = evaluate(tool, invocation);
        if (decision == ToolPermissionDecision.Denied) return false;
        if (decision == ToolPermissionDecision.Allowed) return tool.RiskLevel == ToolRiskLevel.ReadOnly;
        if (decision != ToolPermissionDecision.RequiresApproval) return false;
        if (tool.RiskLevel is ToolRiskLevel.Unknown or ToolRiskLevel.ReadOnly) return false;
        var run = runs.GetRun(invocation.RunId);
        if (run.ConversationId != invocation.ConversationId || run.Status != RunStatus.Running || run.PauseScope is not null) return false;
        if (yieldDesktop is not null) await yieldDesktop(ct).ConfigureAwait(false);
        var access = desktop.Snapshot;
        if (access.OwnerRunId == invocation.RunId || access.InputCleanupUncertain) return false;
        var presentation = describe(tool, arguments);
        TaskRun waiting;
        try { waiting = await runs.BeginToolApprovalAsync(run.Id, run.RunVersion, tool.Id, ct).ConfigureAwait(false); }
        catch (RunVersionConflictException) { return false; }
        catch (InvalidOperationException error) when (error is not RunPersistenceException) { return false; }
        var action = new PendingToolAction(waiting.Id, waiting.ConversationId, waiting.RunVersion, invocation.ToolCallId,
            tool.Id, tool.Source, tool.RiskLevel, tool.Version, arguments.Value.Clone(), presentation.Description, presentation.Destination);
        var request = await approvals.CreateAsync(action, owner, lifetime ?? TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        while (request.Status == ApprovalStatus.Pending)
        {
            ct.ThrowIfCancellationRequested();
            var current = runs.GetRun(run.Id);
            if (current.Status != RunStatus.WaitingForApproval || current.RunVersion != waiting.RunVersion) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            request = await approvals.GetAsync(request.Id, ct).ConfigureAwait(false) ?? throw new ApprovalConflictException();
        }
        var allowed = request.Status == ApprovalStatus.Approved && request.Owner == owner && request.ActionHash == action.Fingerprint()
            && request.ExpiresAtUtc > clock.GetUtcNow() && evaluate(tool, invocation) == ToolPermissionDecision.RequiresApproval;
        TaskRun resumed;
        try { resumed = await runs.EndToolApprovalAsync(run.Id, waiting.RunVersion, ct).ConfigureAwait(false); }
        catch (RunVersionConflictException) { return false; }
        catch (InvalidOperationException error) when (error is not RunPersistenceException) { return false; }
        ct.ThrowIfCancellationRequested();
        if (allowed) receipts[(invocation.RunId, invocation.ToolCallId)] = (request, resumed.RunVersion);
        return allowed;
    }

    public async ValueTask<bool> ValidateDispatchAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var decision = evaluate(tool, invocation);
        if (decision == ToolPermissionDecision.Allowed) return tool.RiskLevel == ToolRiskLevel.ReadOnly;
        if (decision != ToolPermissionDecision.RequiresApproval || !receipts.TryRemove((invocation.RunId, invocation.ToolCallId), out var receipt)) return false;
        var current = runs.GetRun(invocation.RunId);
        if (current.RunVersion != receipt.RunVersion || current.Status != RunStatus.Running || current.PauseScope is not null) return false;
        var request = await approvals.GetAsync(receipt.Request.Id, ct).ConfigureAwait(false);
        if (request is null || request.Status != ApprovalStatus.Approved || request.Version != receipt.Request.Version
            || request.Owner != owner || request.ExpiresAtUtc <= clock.GetUtcNow()) return false;
        var exact = receipt.Request.Action with { RunId = invocation.RunId, ConversationId = invocation.ConversationId, ToolCallId = invocation.ToolCallId,
            ToolId = tool.Id, Source = tool.Source, RiskLevel = tool.RiskLevel, ToolVersion = tool.Version, Arguments = arguments.Value };
        return exact.Fingerprint() == request.ActionHash;
    }
}
