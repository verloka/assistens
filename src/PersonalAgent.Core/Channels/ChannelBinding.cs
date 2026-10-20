using PersonalAgent.Core.Security;
using System.Text.Json.Serialization;

namespace PersonalAgent.Core.Channels;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelBindingStatus>))]
public enum ChannelBindingStatus { Pending, Approved, Revoked }
public sealed record ChannelBinding(Guid Id, ChannelRoute Route, string SenderId, ApprovalOwner Owner,
    ChannelBindingStatus Status, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int SchemaVersion = 1);
public sealed class ChannelBindingConflictException() : InvalidOperationException("The channel binding changed or is unavailable to this owner.");
public sealed class ChannelPairingLimitException() : InvalidOperationException("Pending channel pairing capacity was reached.");
