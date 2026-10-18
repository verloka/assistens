using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.App;

internal sealed class LifecycleLoggingService(ILogger<LifecycleLoggingService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(new EventId(1000, "HostStarting"), "Personal Agent hosted services starting.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(new EventId(1001, "HostStopping"), "Personal Agent hosted services stopping.");
        return Task.CompletedTask;
    }
}
