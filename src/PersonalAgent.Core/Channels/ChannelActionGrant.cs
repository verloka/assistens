using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Channels;

// Immutable scope; possession of a transport token never bypasses fresh owner/action authorization.
public sealed record ChannelActionGrant(string TokenHash, Guid MessageId, string PresentationHash, ChannelRoute Route, string SenderId, ApprovalOwner Owner,
    ChannelActionKind Kind, Guid RunId, long RunVersion, Guid ApprovalId, long ApprovalVersion, string ActionHash, DateTimeOffset ExpiresAtUtc, int SchemaVersion = 1)
{
    public void Validate()
    {
        if (SchemaVersion != 1) throw new NotSupportedException("Unsupported channel action scope.");
        Route.Validate(); Owner.Validate();
        if (TokenHash.Length != 64 || TokenHash.Any(c => !char.IsAsciiHexDigit(c)) || ActionHash.Length != 64 || ActionHash.Any(c => !char.IsAsciiHexDigit(c))
            || MessageId == Guid.Empty || PresentationHash.Length != 64 || PresentationHash.Any(c => !char.IsAsciiHexDigit(c))
            || string.IsNullOrWhiteSpace(SenderId) || SenderId.Length > 256 || SenderId.Any(char.IsControl)
            || Kind is not (ChannelActionKind.Approve or ChannelActionKind.Reject or ChannelActionKind.Cancel or ChannelActionKind.Pause) || RunId == Guid.Empty
            || (Kind is ChannelActionKind.Approve or ChannelActionKind.Reject ? ApprovalId == Guid.Empty : ApprovalId != Guid.Empty || ApprovalVersion != 0)
            || RunVersion < 0 || ApprovalVersion < 0 || ExpiresAtUtc == default) throw new ArgumentException("Invalid channel action scope.");
    }
}
