using PersonalAgent.Core.Channels;

namespace PersonalAgent.Core.Persistence;

public interface INotificationOutbox
{
    Task<NotificationDelivery> EnqueueAsync(Guid eventId, OutgoingMessage message, CancellationToken ct = default);
    Task<NotificationDelivery?> GetAsync(Guid messageId, CancellationToken ct = default);
    Task<NotificationDelivery?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct = default);
    Task<NotificationDelivery> FinishAsync(Guid messageId, long expectedVersion, NotificationDeliveryStatus status,
        DateTimeOffset now, DateTimeOffset nextAttemptAt, string? errorCode, CancellationToken ct = default);
    // Only under exclusive startup ownership, never on a periodic/live delivery tick.
    Task RecoverSendingAsync(DateTimeOffset now, CancellationToken ct = default);
}
