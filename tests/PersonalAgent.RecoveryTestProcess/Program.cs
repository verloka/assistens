using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace PersonalAgent.RecoveryTestProcess;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--secret-roundtrip")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var matched = false;
            try
            {
                var value = await new WindowsDpapiSecretStore(args[1]).ReadAsync(args[2]);
                if (value is not null)
                {
                    var clear = Encoding.UTF8.GetBytes(value);
                    try { matched = CryptographicOperations.FixedTimeEquals(SHA256.HashData(clear), Convert.FromHexString(args[3])); }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                }
            }
            catch (Exception error) { Console.WriteLine("SECRET_READ_FAILURE=" + error.GetType().Name); } // Type only, never value/body.
            Console.WriteLine(matched ? "SECRET_ROUNDTRIP_OK" : "SECRET_UNAVAILABLE");
            Environment.ExitCode = matched ? 0 : 1;
            return;
        }
        if (args.Length is not (2 or 3) || args.Length == 3 && args[2] != "--durable-dispatch")
            throw new ArgumentException("Expected temporary database, fake-effect file and optional durable dispatch fixture flag.");
        using var owner = new FileStream(args[0] + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var factory = new SqliteAgentDbContextFactory(args[0]);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
        using var coordinator = new TaskRunCoordinator(new SqliteRunRepository(factory));
        var loop = new TaskRunner(coordinator, new PendingWriteRuntime(args[1], args.Length == 3 ? factory : null, coordinator)).RunAsync(CancellationToken.None);
        await coordinator.SubmitAsync(new(Guid.NewGuid(), "Crash fixture only", new TaskOrigin("test"), AllowedToolIds: [args.Length == 3 ? "fixture_write" : "fixture.write"]));
        await loop;
    }

    private sealed class PendingWriteRuntime(string effectPath, SqliteAgentDbContextFactory? database, TaskRunCoordinator coordinator) : IAgentRuntime
    {
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            progress.Report(new AgentEvent
            {
                RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.ToolRequested,
                Message = "Synthetic local write dispatched; no acknowledgement", ToolId = database is null ? "fixture.write" : "fixture_write", ToolCallId = Guid.NewGuid()
            });
            if (database is not null)
            {
                var waiting = await coordinator.BeginToolApprovalAsync(request.RunId, coordinator.GetRun(request.RunId).RunVersion, "fixture_write", ct);
                var owner = new ApprovalOwner("test", "synthetic-crash-fixture");
                var action = new PendingToolAction(request.RunId, request.ConversationId, waiting.RunVersion, "crash-write", "fixture_write",
                    new(ToolSourceKind.Local, "sandbox-file"), ToolRiskLevel.Write, null, JsonSerializer.SerializeToElement(new { value = "effect" }),
                    "Synthetic sandbox write", "temporary fixture file");
                var approvals = new SqliteApprovalRepository(database);
                var approval = await approvals.CreateAsync(action, owner, TimeSpan.FromMinutes(1), ct);
                await approvals.DecideAsync(new(approval.Id, approval.Version, approval.ActionHash, owner, true), ct);
                var running = await coordinator.EndToolApprovalAsync(request.RunId, waiting.RunVersion, ct);
                await new SqliteToolCallRepository(database).BeginAsync(new("fixture_write", "Synthetic sandbox write",
                    JsonSerializer.SerializeToElement(new { type = "object" }), action.Source, ToolRiskLevel.Write), new(action.Arguments),
                    new(request.RunId, request.ConversationId, action.ToolCallId), running.RunVersion, approval.Id, ct);
            }
            // Only a sandbox file effect. No GUI, MCP, model or external destination.
            await File.AppendAllTextAsync(effectPath, "effect\n", ct);
            Console.WriteLine($"{(database is null ? "RECOVERY_READY" : "DISPATCH_READY")}={request.RunId:D}");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Fixture must be killed before acknowledgement.");
        }
    }
}
