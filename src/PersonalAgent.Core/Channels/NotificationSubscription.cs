namespace PersonalAgent.Core.Channels;

public enum NotificationEventKind { Completed, Failed, ApprovalNeeded, PausedForUser, Blocked, ImportantFinding }
public sealed record NotificationSubscription(ChannelRoute Route, IReadOnlyList<NotificationEventKind> Events, DateTimeOffset SinceUtc)
{
    public NotificationSubscription Snapshot()
    {
        Route.Validate();
        if (SinceUtc == default || Events is null || Events.Count is < 1 or > 6 || Events.Any(item => !Enum.IsDefined(item)) || Events.Distinct().Count() != Events.Count)
            throw new ArgumentException("Invalid notification subscription.");
        return this with { Events = Array.AsReadOnly(Events.ToArray()), SinceUtc = SinceUtc.ToUniversalTime() };
    }
}
public interface INotificationSubscriptionSource
{
    Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default);
}
public sealed class FixedNotificationSubscriptions(IEnumerable<NotificationSubscription> subscriptions) : INotificationSubscriptionSource
{
    private readonly IReadOnlyList<NotificationSubscription> snapshot = Array.AsReadOnly(subscriptions.Select(item => item.Snapshot()).ToArray());
    public Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(snapshot); }
}
