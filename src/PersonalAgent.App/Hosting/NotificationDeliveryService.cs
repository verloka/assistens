using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.App;

// Registered after RunHistoryService: migrations and exclusive database ownership precede recovery.
public sealed class NotificationDeliveryService(INotificationOutbox outbox, NotificationDispatcher dispatcher,
    ILogger<NotificationDeliveryService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await outbox.RecoverSendingAsync(clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); var unavailable = false;
        try
        {
            do
            {
                try { await dispatcher.TickAsync(stoppingToken).ConfigureAwait(false); unavailable = false; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    if (!unavailable) logger.LogWarning("Notification storage is unavailable or invalid; claimed deliveries will not be replayed automatically.");
                    unavailable = true;
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
