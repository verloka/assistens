using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

public sealed class ChannelConversationReceiptRecorder(ApprovalOwner owner, ChannelRoute route, string senderId,
    IChannelConversationRepository links, TimeProvider? timeProvider = null) : IChannelDeliveryReceiptSink
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task RecordAsync(OutgoingMessage message, string externalMessageId, CancellationToken ct = default)
    {
        message = message.Snapshot();
        if (message.Destination != route) throw new ChannelAccessDeniedException();
        if (message.RunId is not { } runId || message.ConversationId is not { } conversationId) return;
        await links.BindAsync(new(owner, route, senderId, externalMessageId, conversationId, runId, clock.GetUtcNow(), OutgoingMessageId: message.Id), ct).ConfigureAwait(false);
    }
}
