using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;

namespace PersonalAgent.App;

public sealed class TaskRunnerService(TaskRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}
