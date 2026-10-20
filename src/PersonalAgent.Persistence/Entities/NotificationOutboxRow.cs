using PersonalAgent.Core.Channels;

namespace PersonalAgent.Persistence.Entities;

public sealed class NotificationOutboxRow
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string ChannelId { get; set; }
    public required string ConnectionId { get; set; }
    public required string DestinationId { get; set; }
    public required string MessageJson { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public NotificationDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
}
