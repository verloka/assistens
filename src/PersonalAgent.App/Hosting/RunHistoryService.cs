using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Persistence;
using System.IO;

namespace PersonalAgent.App;

public sealed class RunHistoryService(SqliteAgentDbContextFactory contexts, TaskRunCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(contexts.DatabasePath)!);
        await using var db = contexts.CreateDbContext();
        await db.Database.MigrateAsync(cancellationToken);
        await coordinator.LoadFinishedHistoryAsync(cancellationToken);
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
