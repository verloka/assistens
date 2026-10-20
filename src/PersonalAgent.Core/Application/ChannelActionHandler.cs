using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

// Tokens select immutable scopes, never commands supplied by channel text. Existing repository CAS,
// run revisions and the dispatch ledger are the durable single-use authority, including after restart.
public sealed class ChannelActionHandler(ChannelOwnershipPolicy ownership, IChannelActionRepository actions,
    IApprovalRepository approvals, ChannelOwnerCommands commands, TaskRunCoordinator runs, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const string Stale = "Action is no longer available. Review the current state in Personal Agent.";

    public async Task<string> HandleAsync(IncomingMessage incoming, CancellationToken ct = default)
    {
        var peer = incoming.Snapshot();
        var owner = await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false);
        if (peer.Attachments.Count != 0 || peer.ActionToken is not { Length: 43 } token || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ChannelAccessDeniedException();
        var grant = await actions.FindAsync(ChannelApprovalPresenter.HashToken(token), ct).ConfigureAwait(false) ?? throw new ChannelAccessDeniedException();
        grant.Validate();
        if (grant.Route != peer.Route || grant.SenderId != peer.SenderId || grant.Owner != owner) throw new ChannelAccessDeniedException();
        if (grant.ExpiresAtUtc <= clock.GetUtcNow()) return Stale;
        ct.ThrowIfCancellationRequested();
        try
        {
            if (grant.Kind is ChannelActionKind.Approve or ChannelActionKind.Reject)
            {
                var request = await approvals.GetAsync(grant.ApprovalId, ct).ConfigureAwait(false);
                if (request is null || request.Status != ApprovalStatus.Pending || request.Owner != owner || request.Version != grant.ApprovalVersion
                    || request.ActionHash != grant.ActionHash || request.Action.RunId != grant.RunId || request.Action.RunVersion != grant.RunVersion) return Stale;
                // Owner is resolved again at the shared application boundary, then the repository
                // atomically checks the pending action, TTL and current WaitingForApproval revision.
                await commands.DecideAsync(peer, new(grant.ApprovalId, grant.ApprovalVersion, grant.ActionHash, owner, grant.Kind == ChannelActionKind.Approve), ct).ConfigureAwait(false);
                return grant.Kind == ChannelActionKind.Approve ? "Exact action approved." : "Exact action rejected.";
            }
            var run = runs.GetRuns().SingleOrDefault(item => item.Id == grant.RunId);
            if (run is null || run.RunVersion != grant.RunVersion || !ChannelApprovalPresenter.CanControl(run, grant.Kind)
                || grant.ActionHash != ChannelApprovalPresenter.ControlHash(grant.Kind, grant.RunId, grant.RunVersion)) return Stale;
            var commandId = new Guid(Convert.FromHexString(grant.TokenHash).AsSpan(0, 16));
            if (grant.Kind == ChannelActionKind.Cancel)
                await commands.CancelAsync(peer, new(commandId, grant.RunId, grant.RunVersion), ct).ConfigureAwait(false);
            else
                await commands.PauseAsync(peer, new(commandId, grant.RunId, grant.RunVersion), ct).ConfigureAwait(false);
            return grant.Kind == ChannelActionKind.Cancel ? "Run cancelled." : "Run pause requested.";
        }
        catch (ApprovalConflictException) { return Stale; }
        catch (RunVersionConflictException) { return Stale; }
    }
}
