using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Channels;

public sealed record ChannelConfiguration(Guid Id, ApprovalOwner Owner, string Name, ChannelRoute Route, string SenderId,
    bool Enabled, bool ReceiveTasks, bool PeerAuthorized, string? SecretReference, IReadOnlyList<NotificationEventKind> Events,
    DateTimeOffset SinceUtc, long Version = 0, string? RemoteIdentity = null, int SchemaVersion = 1, string? RemoteUsername = null)
{
    public ChannelConfiguration Snapshot()
    {
        Owner.Validate(); Route.Validate();
        ChannelFields.Identity(Name, 128); ChannelFields.Identity(SenderId, 256);
        if (SchemaVersion != 1 || Id == Guid.Empty || Version < 0 || SinceUtc == default || Events is null || Events.Count > 6
            || Events.Any(item => !Enum.IsDefined(item)) || Events.Distinct().Count() != Events.Count
            || SecretReference is not null && (!SecretReference.StartsWith("dpapi:", StringComparison.Ordinal) || !Guid.TryParseExact(SecretReference[6..], "N", out _))
            || RemoteIdentity?.Length > 256 || Enabled && (!PeerAuthorized || SecretReference is not null && string.IsNullOrWhiteSpace(RemoteIdentity)))
            throw new ArgumentException("Invalid or unverified channel configuration.");
        if (RemoteUsername is not null && (RemoteUsername.Length is < 1 or > 64 || RemoteUsername.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("Invalid channel username metadata.");
        return this with { Events = Array.AsReadOnly(Events.ToArray()), SinceUtc = SinceUtc.ToUniversalTime() };
    }
}
public sealed class ChannelConfigurationConflictException() : InvalidOperationException("Channel configuration changed or conflicts with an existing connection.");

// Optional connection-specific constraints, composed separately from exact local-owner bindings.
public interface IChannelConnectionPolicy : IChannelAccessPolicy;
public interface IOutboundChannelCatalog
{
    Task<IOutboundChannel?> FindAsync(ChannelRoute route, CancellationToken ct = default);
}
