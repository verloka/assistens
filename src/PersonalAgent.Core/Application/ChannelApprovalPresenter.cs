using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

public sealed class ChannelApprovalPresenter(ChannelOwnershipPolicy ownership, IApprovalRepository approvals, IChannelActionRepository actions, TimeProvider? timeProvider = null)
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
            buttons.Add(new(kind, kind == ChannelActionKind.Approve ? "Approve this action" : "Reject", token));
        }
        return (message with { Actions = buttons }).Snapshot();
    }
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task ValidateAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        message = message.Snapshot();
        foreach (var button in message.Actions)
        {
            var grant = await actions.FindAsync(HashToken(button.Token), ct).ConfigureAwait(false) ?? throw new ChannelAccessDeniedException();
            if (grant.MessageId != message.Id || grant.PresentationHash != HashToken(message.Text) || grant.Route != message.Destination || grant.RunId != message.RunId
                || grant.Kind != button.Kind || button.Label != (grant.Kind == ChannelActionKind.Approve ? "Approve this action" : "Reject")
                || grant.ExpiresAtUtc <= clock.GetUtcNow()) throw new ChannelAccessDeniedException();
            var owner = await ownership.RequireOwnerAsync(new IncomingMessage { Route = grant.Route, SenderId = grant.SenderId, ExternalMessageId = message.Id.ToString("N"),
                TimestampUtc = clock.GetUtcNow(), Text = "Review pending action" }, ct).ConfigureAwait(false);
            if (owner != grant.Owner) throw new ChannelAccessDeniedException();
            var request = (await approvals.ListPendingAsync(owner, ct).ConfigureAwait(false)).SingleOrDefault(item => item.Id == grant.ApprovalId);
            if (request is null || request.Version != grant.ApprovalVersion || request.ActionHash != grant.ActionHash || message.ConversationId != request.Action.ConversationId
                || request.Action.RunId != grant.RunId || request.Action.RunVersion != grant.RunVersion) throw new ApprovalConflictException();
        }
    }
}
