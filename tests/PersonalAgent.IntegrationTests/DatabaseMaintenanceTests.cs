using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class DatabaseMaintenanceTests
{
    [Fact]
    public async Task ActualHostedUpgradeMakesVerifiedPreMigrationBackupAndPreservesLegacyHistoryWithoutExecution()
    {
        using var database = new TemporaryAgentDatabase(); var runId = await SeedLegacy(database);
        var maintenance = new SqliteDatabaseMaintenance(new(database.DatabasePath));
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
        await host.StartAsync();
        try
        {
        var backup = Assert.Single(Directory.GetFiles(maintenance.BackupDirectory, "*.db")); Assert.EndsWith(".db", backup);
        Assert.Empty(Directory.GetFiles(maintenance.BackupDirectory, "*.tmp"));
        await using (var connection = Open(backup))
        {
            await connection.OpenAsync();
            Assert.Equal("ok", await Scalar(connection, "PRAGMA quick_check"));
            Assert.Equal("Legacy private result", await Scalar(connection, "SELECT Summary FROM TaskRuns"));
            Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
            Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('TaskRuns') WHERE name = 'ContentClearedAtUtc'"));
            Assert.Equal("Legacy private event", await Scalar(connection, "SELECT Message FROM RunEvents"));
        }
        var stored = (await new SqliteRunRepository(database).GetAsync(runId))!;
        Assert.Equal(RunStatus.Completed, stored.Run.Status); Assert.Equal("Legacy private result", stored.Run.Summary);
        Assert.Equal("Legacy private event", Assert.Single(stored.Events).Message);
        Assert.Equal(0, stored.Run.RunVersion); Assert.Equal(runId, Assert.Single(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns()).Id);
        }
        finally { await host.StopAsync(); }
        var beforeReopen = (await new SqliteRunRepository(database).GetAsync(runId))!;
        using var reopened = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
        await reopened.StartAsync();
        try
        {
        Assert.Single(Directory.GetFiles(maintenance.BackupDirectory, "*.db")); // SQLite may create its own WAL/SHM sidecars on inspection.
        Assert.Equal(JsonSerializer.Serialize(beforeReopen), JsonSerializer.Serialize(await new SqliteRunRepository(database).GetAsync(runId)));
        }
        finally { await reopened.StopAsync(); }
    }

    [Fact]
    public async Task BackupFailureStopsHostBeforeMigrationOrRecoveryAndReleasesExclusiveOwnership()
    {
        using var database = new TemporaryAgentDatabase(); await SeedLegacy(database);
        var maintenance = new SqliteDatabaseMaintenance(new(database.DatabasePath));
        await File.WriteAllTextAsync(maintenance.BackupDirectory, "synthetic backup destination failure");
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
        await Assert.ThrowsAnyAsync<IOException>(() => host.StartAsync());
        Assert.Empty(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns());
        using (new FileStream(database.DatabasePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await using var connection = Open(database.DatabasePath); await connection.OpenAsync();
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal("Completed", await Scalar(connection, "SELECT Status FROM TaskRuns"));
        Assert.Equal("Legacy private event", await Scalar(connection, "SELECT Message FROM RunEvents"));
    }

    [Fact]
    public async Task ConsistentSqliteBackupIncludesCommittedWalWithoutCopyingProtectedStoreFiles()
    {
        using var database = new TemporaryAgentDatabase(); await HistoryRetentionTests.SeedAsync(database);
        await using var source = database.Open(); await source.Database.OpenConnectionAsync();
        await source.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL"); await source.Database.ExecuteSqlRawAsync("PRAGMA wal_autocheckpoint=0");
        await source.Database.ExecuteSqlRawAsync("CREATE TABLE BackupWitness (Value TEXT NOT NULL)");
        await source.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BackupWitness VALUES ({"committed private WAL content"})");
        Assert.True(new FileInfo(database.DatabasePath + "-wal").Length > 0);
        var secretDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"); Directory.CreateDirectory(secretDirectory);
        await File.WriteAllBytesAsync(Path.Combine(secretDirectory, "fixture.secret"), [1, 2, 3, 4]);
        var maintenance = new SqliteDatabaseMaintenance(new(database.DatabasePath)); var backup = await maintenance.CreateBackupAsync();
        Assert.Single(Directory.GetFiles(maintenance.BackupDirectory));
        await using var copy = Open(backup); await copy.OpenAsync();
        Assert.Equal("committed private WAL content", await Scalar(copy, "SELECT Value FROM BackupWitness"));
        Assert.Equal(1L, await Scalar(copy, "SELECT COUNT(*) FROM TaskRuns"));
        Assert.Equal(4L, await Scalar(copy, "SELECT COUNT(*) FROM RunEvents"));
        Assert.Empty(Directory.GetFiles(maintenance.BackupDirectory, "*.secret"));
    }

    [Fact]
    public async Task NewerUnknownMigrationFailsClosedWithoutRecoveryOrAutomaticRestore()
    {
        using var database = new TemporaryAgentDatabase(); var run = await HistoryRetentionTests.SeedAsync(database);
        var before = JsonSerializer.Serialize(await new SqliteRunRepository(database).GetAsync(run.Id));
        await using (var db = database.Open()) await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({"20990101000000_Future"}, {"99.0"})");
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(builder => builder.ClearProviders()).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => host.StartAsync());
        Assert.Empty(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns());
        Assert.Equal(before, JsonSerializer.Serialize(await new SqliteRunRepository(database).GetAsync(run.Id)));
        Assert.False(Directory.Exists(database.DatabasePath + ".backups"));
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    private static async Task<Guid> SeedLegacy(TemporaryAgentDatabase database)
    {
        var runId = Guid.NewGuid(); var conversationId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var db = database.Open(); await db.GetService<IMigrator>().MigrateAsync("20261018105532_InitialRunHistory");
        var request = JsonSerializer.Serialize(new AgentRequest { RunId = runId, ConversationId = conversationId, Instructions = "Legacy private input", ModelProfileId = "fake" });
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ({conversationId}, {"Legacy title"}, {now.UtcTicks}, {now.UtcTicks})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TaskRuns (Id, ConversationId, OriginJson, Instructions, ModelProfileId, Status, RunVersion, CreatedAtUtc, Summary, SourcesJson, RequestJson, PayloadSchemaVersion) VALUES ({runId}, {conversationId}, {JsonSerializer.Serialize(new TaskOrigin("test"))}, {"Legacy private input"}, {"fake"}, {"Completed"}, {0L}, {now.UtcTicks}, {"Legacy private result"}, {"[]"}, {request}, {1})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RunEvents (Id, RunId, Sequence, EventType, TimestampUtc, Message) VALUES ({Guid.NewGuid()}, {runId}, {1L}, {"RunFinished"}, {now.UtcTicks}, {"Legacy private event"})");
        return runId;
    }
}
