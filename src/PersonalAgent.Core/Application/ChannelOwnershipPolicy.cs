using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

// The owner is supplied by the trusted local composition root, never from a channel body.
public sealed class ChannelOwnershipPolicy : IChannelAccessPolicy
{
    private readonly ApprovalOwner owner;
    private readonly IChannelBindingRepository bindings;
    private readonly IChannelConnectionPolicy? connectionPolicy;
    public ChannelOwnershipPolicy(ApprovalOwner owner, IChannelBindingRepository bindings, IChannelConnectionPolicy? connectionPolicy = null)
    { owner.Validate(); this.owner = owner; this.bindings = bindings; this.connectionPolicy = connectionPolicy; }

    public async ValueTask<bool> AllowInboundAsync(IncomingMessage message, CancellationToken ct) =>
        await ResolveAsync(message, ct).ConfigureAwait(false) is not null;
    public async ValueTask<bool> AllowOutboundAsync(OutgoingMessage message, CancellationToken ct)
    {
        var snapshot = message.Snapshot();
        if (connectionPolicy is not null && !await connectionPolicy.AllowOutboundAsync(snapshot, ct).ConfigureAwait(false)) return false;
        return (await bindings.ListAsync(owner, ct).ConfigureAwait(false)).Any(binding => binding.Status == ChannelBindingStatus.Approved && binding.Route == snapshot.Destination);
    }
    public async Task<ApprovalOwner> RequireOwnerAsync(IncomingMessage message, CancellationToken ct = default)
    {
        if (await ResolveAsync(message, ct).ConfigureAwait(false) is null) throw new ChannelAccessDeniedException();
        return owner;
    }
    private async Task<ChannelBinding?> ResolveAsync(IncomingMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var snapshot = message.Snapshot();
        if (connectionPolicy is not null && !await connectionPolicy.AllowInboundAsync(snapshot, ct).ConfigureAwait(false)) return null;
        var binding = await bindings.FindAsync(snapshot.Route, snapshot.SenderId, ct).ConfigureAwait(false);
        if (binding is null)
        {
            try { await bindings.RequestPairingAsync(snapshot.Route, snapshot.SenderId, owner, ct).ConfigureAwait(false); }
            catch (ChannelPairingLimitException) { }
            return null; // Observing a peer is never consent, including when another request races local approval.
        }
        return binding.Status == ChannelBindingStatus.Approved && binding.Owner == owner ? binding : null;
    }
}
