using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;

namespace PersonalAgent.App;

public sealed class SchedulerService(ScheduleDispatcher dispatcher, ILogger<SchedulerService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, string?> failures = [];
    private bool unavailable;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                foreach (var outcome in await dispatcher.TickAsync(stoppingToken).ConfigureAwait(false))
                {
                    if (outcome.ErrorCode is { } error && (!failures.TryGetValue(outcome.ScheduleId, out var prior) || prior != error))
                        logger.LogWarning("Schedule {ScheduleId} occurrence {OccurrenceUtc} status {ErrorCode}; no operation was replayed.", outcome.ScheduleId, outcome.OccurrenceUtc, error);
                    if (outcome.ErrorCode is { } code) failures[outcome.ScheduleId] = code; else failures.Remove(outcome.ScheduleId);
                }
                unavailable = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                if (!unavailable) logger.LogWarning("Schedule storage is unavailable or invalid; no scheduled work was dispatched by this tick.");
                unavailable = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
