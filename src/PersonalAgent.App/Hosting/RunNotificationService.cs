using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;

namespace PersonalAgent.App;

public sealed class RunNotificationService(RunNotificationPublisher publisher, ILogger<RunNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); var unavailable = false;
        try
        {
            do
            {
                try { await publisher.TickAsync(stoppingToken).ConfigureAwait(false); unavailable = false; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { if (!unavailable) logger.LogWarning("Notification publication is unavailable; persisted run events remain for retry."); unavailable = true; }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
