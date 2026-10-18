using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence;

namespace PersonalAgent.RecoveryTestProcess;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected temporary database and fake-effect file paths.");
        using var owner = new FileStream(args[0] + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var factory = new SqliteAgentDbContextFactory(args[0]);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
        using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(factory));
        var loop = new TaskRunner(coordinator, new PendingWriteRuntime(args[1])).RunAsync(CancellationToken.None);
        await coordinator.SubmitAsync(new(Guid.NewGuid(), "Crash fixture only", new TaskOrigin("test"), AllowedToolIds: ["fixture.write"]));
        await loop;
    }

    private sealed class PendingWriteRuntime(string effectPath) : IAgentRuntime
    {
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            progress.Report(new AgentEvent
            {
                RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.ToolRequested,
                Message = "Synthetic local write dispatched; no acknowledgement", ToolId = "fixture.write", ToolCallId = Guid.NewGuid()
            });
            // Only a sandbox file effect. No GUI, MCP, model or external destination.
            await File.AppendAllTextAsync(effectPath, "effect\n", ct);
            Console.WriteLine($"RECOVERY_READY={request.RunId:D}");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Fixture must be killed before acknowledgement.");
        }
    }
}
