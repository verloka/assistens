using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

public sealed class NotificationDispatcher
{
    private readonly INotificationOutbox outbox;
    private readonly ChannelMessageRouter router;
    private readonly TimeProvider clock;
    private readonly NotificationDeliveryOptions options;
    public NotificationDispatcher(INotificationOutbox outbox, ChannelMessageRouter router, TimeProvider? timeProvider = null, NotificationDeliveryOptions? options = null)
    { this.outbox = outbox; this.router = router; clock = timeProvider ?? TimeProvider.System; this.options = options ?? new(); this.options.Validate(); }
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var processed = 0;
        while (processed < options.BatchSize)
        {
            ct.ThrowIfCancellationRequested(); var delivery = await outbox.ClaimNextAsync(clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (delivery is null) break;
            var status = NotificationDeliveryStatus.Delivered; string? error = null; var now = clock.GetUtcNow(); var next = now;
            if (delivery.Attempts > options.MaxAttempts) { status = NotificationDeliveryStatus.Failed; error = "DeliveryAttemptsExhausted"; }
            else if (now - delivery.CreatedAtUtc >= options.MaxAge) { status = NotificationDeliveryStatus.Failed; error = "DeliveryExpired"; }
            else
            {
                try { await router.SendAsync(delivery.Message, ct).ConfigureAwait(false); }
                catch (ChannelDeliveryException ex)
                {
                    now = clock.GetUtcNow(); error = "DeliveryRejected";
                    if (ex.OutcomeUnknown) { status = NotificationDeliveryStatus.OutcomeUnknown; error = "DeliveryOutcomeUnknown"; }
                    else if (ex.Retryable && delivery.Attempts < options.MaxAttempts)
                    {
                        var seconds = Math.Min(options.MaxRetryDelay.TotalSeconds, options.InitialRetryDelay.TotalSeconds * Math.Pow(2, delivery.Attempts - 1));
                        var delay = TimeSpan.FromSeconds(seconds);
                        if (ex.RetryAfter is { } minimum && minimum > delay) delay = minimum;
                        if (delay < options.MaxAge - (now - delivery.CreatedAtUtc)) { status = NotificationDeliveryStatus.Pending; next = now + delay; error = "DeliveryRetryScheduled"; }
                        else { status = NotificationDeliveryStatus.Failed; error = "DeliveryExpired"; }
                    }
                    else { status = NotificationDeliveryStatus.Failed; error = ex.AcceptedParts > 0 ? "DeliveryPartiallySent" : delivery.Attempts >= options.MaxAttempts ? "DeliveryAttemptsExhausted" : "DeliveryRejected"; }
                }
                catch (Exception ex) when (ex is ChannelAccessDeniedException or ArgumentException or ApprovalConflictException or NotSupportedException)
                { status = NotificationDeliveryStatus.Failed; error = "DeliveryNotAuthorizedOrInvalid"; }
                catch (Exception) { status = NotificationDeliveryStatus.OutcomeUnknown; error = "DeliveryOutcomeUnknown"; }
            }
            // Persist cancellation/unknown outcome even when shutdown canceled the dispatch token.
            await outbox.FinishAsync(delivery.Message.Id, delivery.Version, status, clock.GetUtcNow(), next, error, CancellationToken.None).ConfigureAwait(false);
            processed++; ct.ThrowIfCancellationRequested();
        }
        return processed;
    }
}
