using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Persistence;
using PersonalAgent.Core.Persistence;
using System.IO;

namespace PersonalAgent.App;

public sealed class RunHistoryService(SqliteAgentDbContextFactory contexts, TaskRunCoordinator coordinator, IRunRepository repository,
    IToolCallRepository? toolCalls = null) : IHostedService, IDisposable
{
    private FileStream? ownership;
    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(contexts.DatabasePath)!);
        try
        {
            // Recovery is valid only after the previous owner exited. The OS releases this lock on crash.
            ownership = new FileStream(contexts.DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await new SqliteDatabaseMaintenance(contexts).MigrateWithBackupAsync(cancellationToken);
            var safeQueued = await new RunRecovery(repository, toolCalls).RecoverAsync(cancellationToken);
            await coordinator.RestoreHistoryAsync(safeQueued, cancellationToken);
        }
        catch { Dispose(); throw; }
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { ownership?.Dispose(); ownership = null; }
}
