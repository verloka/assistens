using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Channels;

// Metadata only: a provider message identity cannot acquire authority from its displayed body.
public sealed record ChannelConversationLink(ApprovalOwner Owner, ChannelRoute Route, string SenderId, string ExternalMessageId,
    Guid ConversationId, Guid RunId, DateTimeOffset CreatedAtUtc, string? ExternalUpdateId = null, Guid? OutgoingMessageId = null)
{
    public void Validate()
    {
        Owner.Validate(); Route.Validate(); ChannelFields.Identity(SenderId, 256); ChannelFields.Identity(ExternalMessageId, 256);
        ChannelFields.OptionalIdentity(ExternalUpdateId, 256);
        if (ConversationId == Guid.Empty || RunId == Guid.Empty || CreatedAtUtc == default || OutgoingMessageId == Guid.Empty
            || (ExternalUpdateId is null) == (OutgoingMessageId is null)) throw new ArgumentException("Invalid external conversation link.");
    }
}
public sealed class ChannelConversationConflictException() : InvalidOperationException("External message already identifies a different conversation or run.");
public sealed class ChannelConversationNotFoundException() : InvalidOperationException("Reply target is not linked to an authorized conversation.");
