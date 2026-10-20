using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Application;

public sealed class ChannelApprovalPresenter(ChannelOwnershipPolicy ownership, IApprovalRepository approvals, IChannelActionRepository actions, TimeProvider? timeProvider = null,
    TaskRunCoordinator? runs = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<OutgoingMessage> CreateAsync(Guid messageId, IncomingMessage peer, Guid approvalId, CancellationToken ct = default)
    {
        peer = peer.Snapshot(); var owner = await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false);
        var request = (await approvals.ListPendingAsync(owner, ct).ConfigureAwait(false)).SingleOrDefault(item => item.Id == approvalId)
            ?? throw new ApprovalConflictException();
        var action = request.Action;
        var text = $"Approval required\nRun: {action.RunId:D}\nOperation: {action.Description}\nTool: {action.ToolId}\nSource: {action.Source.Kind}/{action.Source.Id}\nTool version: {action.ToolVersion}\nDestination: {action.Destination}\nRisk: {action.RiskLevel}\nArguments:\n{action.Arguments.GetRawText()}\nAction hash: {request.ActionHash}\nExpires: {request.ExpiresAtUtc:O}";
        var message = new OutgoingMessage { Id = messageId, Destination = peer.Route, Text = text, RunId = action.RunId, ConversationId = action.ConversationId };
        if (text.Length > 64_000) return (message with { Text = "Approval details are too large for channel review. Open Personal Agent to review the exact action." }).Snapshot();
        var expires = request.ExpiresAtUtc < clock.GetUtcNow().AddMinutes(5) ? request.ExpiresAtUtc : clock.GetUtcNow().AddMinutes(5);
        if (expires <= clock.GetUtcNow()) throw new ApprovalConflictException();
        var buttons = new List<ChannelMessageAction>();
        foreach (var kind in new[] { ChannelActionKind.Approve, ChannelActionKind.Reject })
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var hash = HashToken(token);
            await actions.SaveAsync(new(hash, messageId, HashToken(text), peer.Route, peer.SenderId, owner, kind, action.RunId, action.RunVersion, request.Id, request.Version, request.ActionHash, expires), ct).ConfigureAwait(false);
            buttons.Add(new(kind, Label(kind), token));
        }
        return (message with { Actions = buttons }).Snapshot();
    }
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<OutgoingMessage> CreateControlAsync(Guid messageId, IncomingMessage peer, Guid runId, ChannelActionKind kind, CancellationToken ct = default)
    {
        peer = peer.Snapshot(); var owner = await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false);
        var run = runs?.GetRuns().SingleOrDefault(item => item.Id == runId) ?? throw new ChannelAccessDeniedException();
        if (!CanControl(run, kind)) throw new ChannelAccessDeniedException();
        var text = $"{Label(kind)}\nRun: {run.Id:D}\nStatus: {run.Status}\nVersion: {run.RunVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await actions.SaveAsync(new(HashToken(token), messageId, HashToken(text), peer.Route, peer.SenderId, owner, kind, run.Id, run.RunVersion,
            Guid.Empty, 0, ControlHash(kind, run.Id, run.RunVersion), clock.GetUtcNow().AddMinutes(5)), ct).ConfigureAwait(false);
        return new OutgoingMessage { Id = messageId, Destination = peer.Route, Text = text, RunId = run.Id, ConversationId = run.ConversationId,
            Actions = [new(kind, Label(kind), token)] }.Snapshot();
    }

    internal static bool CanControl(TaskRun run, ChannelActionKind kind) => !TaskRunStateMachine.IsTerminal(run.Status) &&
        (kind == ChannelActionKind.Cancel || kind == ChannelActionKind.Pause && run.PauseScope is null &&
            run.Status is RunStatus.Queued or RunStatus.Running or RunStatus.WaitingForDesktop or RunStatus.WaitingForUser or RunStatus.WaitingForApproval);
    internal static string ControlHash(ChannelActionKind kind, Guid runId, long version) => HashToken(FormattableString.Invariant($"{kind}/{runId:D}/{version}"));
    internal static string Label(ChannelActionKind kind) => kind switch
    {
        ChannelActionKind.Approve => "Approve this action", ChannelActionKind.Reject => "Reject", ChannelActionKind.Cancel => "Cancel run",
        ChannelActionKind.Pause => "Pause run", _ => throw new ChannelAccessDeniedException()
    };

    public async Task ValidateAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        message = message.Snapshot();
        foreach (var button in message.Actions)
        {
            var grant = await actions.FindAsync(HashToken(button.Token), ct).ConfigureAwait(false) ?? throw new ChannelAccessDeniedException();
            if (grant.MessageId != message.Id || grant.PresentationHash != HashToken(message.Text) || grant.Route != message.Destination || grant.RunId != message.RunId
                || grant.Kind != button.Kind || button.Label != Label(grant.Kind)
                || grant.ExpiresAtUtc <= clock.GetUtcNow()) throw new ChannelAccessDeniedException();
            var owner = await ownership.RequireOwnerAsync(new IncomingMessage { Route = grant.Route, SenderId = grant.SenderId, ExternalMessageId = message.Id.ToString("N"),
                TimestampUtc = clock.GetUtcNow(), Text = "Review pending action" }, ct).ConfigureAwait(false);
            if (owner != grant.Owner) throw new ChannelAccessDeniedException();
            if (grant.Kind is ChannelActionKind.Cancel or ChannelActionKind.Pause)
            {
                var run = runs?.GetRuns().SingleOrDefault(item => item.Id == grant.RunId);
                if (run is null || run.RunVersion != grant.RunVersion || message.ConversationId != run.ConversationId || !CanControl(run, grant.Kind)
                    || grant.ActionHash != ControlHash(grant.Kind, grant.RunId, grant.RunVersion)) throw new ChannelAccessDeniedException();
                continue;
            }
            var request = (await approvals.ListPendingAsync(owner, ct).ConfigureAwait(false)).SingleOrDefault(item => item.Id == grant.ApprovalId);
            if (request is null || request.Version != grant.ApprovalVersion || request.ActionHash != grant.ActionHash || message.ConversationId != request.Action.ConversationId
                || request.Action.RunId != grant.RunId || request.Action.RunVersion != grant.RunVersion) throw new ApprovalConflictException();
        }
    }
}
