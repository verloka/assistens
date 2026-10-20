using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Core.Application;

public sealed class ChannelConversationCorrelator(IChannelAccessPolicy policy, IChannelConversationRepository links, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<Guid?> ResolveAsync(IncomingMessage peer, CancellationToken ct = default)
    {
        if (peer.ReplyToMessageId is null && peer.TransportMessageId is null) return null;
        if (policy is not IChannelOwnerPolicy identity) throw new ChannelAccessDeniedException();
        var owner = await identity.RequireOwnerAsync(peer, ct).ConfigureAwait(false);
        if (peer.TransportMessageId is { } messageId)
        {
            var existing = await links.FindAsync(owner, peer.Route, peer.SenderId, messageId, ct).ConfigureAwait(false);
            if (existing is not null && existing.ExternalUpdateId != peer.ExternalMessageId) throw new ChannelConversationConflictException();
        }
        if (peer.ReplyToMessageId is null) return null;
        var link = await links.FindAsync(owner, peer.Route, peer.SenderId, peer.ReplyToMessageId, ct).ConfigureAwait(false) ?? throw new ChannelConversationNotFoundException();
        return link.ConversationId;
    }
    public async Task BindAsync(IncomingMessage peer, TaskRun run, CancellationToken ct = default)
    {
        if (peer.TransportMessageId is null) return;
        if (policy is not IChannelOwnerPolicy identity) throw new ChannelAccessDeniedException();
        var owner = await identity.RequireOwnerAsync(peer, ct).ConfigureAwait(false);
        await links.BindAsync(new(owner, peer.Route, peer.SenderId, peer.TransportMessageId, run.ConversationId, run.Id, clock.GetUtcNow(), peer.ExternalMessageId), ct).ConfigureAwait(false);
    }
}
