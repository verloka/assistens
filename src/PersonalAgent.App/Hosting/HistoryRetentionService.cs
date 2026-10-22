using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;

namespace PersonalAgent.App;

public sealed class HistoryRetentionService(IHistoryRetentionRepository retention, TaskRunCoordinator coordinator,
    ILogger<HistoryRetentionService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var policy = await retention.ReadPolicyAsync(ct);
        return policy.Enabled ? await coordinator.ClearExpiredHistoryAsync(retention, policy,
            (timeProvider ?? TimeProvider.System).GetUtcNow(), ct) : 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "History retention failed; retained history requires review."); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
