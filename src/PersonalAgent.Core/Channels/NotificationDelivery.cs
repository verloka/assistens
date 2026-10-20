namespace PersonalAgent.Core.Channels;

public enum NotificationDeliveryStatus { Pending, Sending, Delivered, Failed, OutcomeUnknown }
public sealed record NotificationDelivery(Guid EventId, OutgoingMessage Message, NotificationDeliveryStatus Status, int Attempts,
    long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset NextAttemptAtUtc,
    string? ErrorCode = null, DateTimeOffset? DeliveredAtUtc = null);
public sealed class NotificationConflictException() : InvalidOperationException("Notification identity, payload or revision changed.");
public sealed record NotificationDeliveryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);
    public int BatchSize { get; init; } = 20;
    public void Validate()
    {
        if (MaxAttempts is < 1 or > 10 || BatchSize is < 1 or > 100 || InitialRetryDelay <= TimeSpan.Zero || MaxRetryDelay < InitialRetryDelay
            || MaxRetryDelay > TimeSpan.FromHours(1) || MaxAge <= TimeSpan.Zero || MaxAge > TimeSpan.FromDays(7)) throw new ArgumentException("Invalid notification delivery limits.");
    }
}
