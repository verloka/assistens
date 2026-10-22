using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PersonalAgent.Persistence;

public sealed class SqliteDatabaseMaintenance(SqliteAgentDbContextFactory contexts)
{
    public string BackupDirectory => contexts.DatabasePath + ".backups";
    public async Task<string?> MigrateWithBackupAsync(CancellationToken ct = default)
    {
        var existed = File.Exists(contexts.DatabasePath) && new FileInfo(contexts.DatabasePath).Length > 0;
        await using var db = contexts.CreateDbContext();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Except(db.Database.GetMigrations(), StringComparer.Ordinal).Any())
            throw new NotSupportedException("The database requires a newer application version.");
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Any();
        var backup = existed && pending ? await CreateBackupAsync(ct) : null;
        await db.Database.MigrateAsync(ct);
        return backup;
    }

    // Caller owns the normal database lock; SQLite's backup API includes committed WAL data.
    public async Task<string> CreateBackupAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BackupDirectory);
        var final = Path.Combine(BackupDirectory, $"history-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db");
        var temporary = final + ".tmp";
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = contexts.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await source.OpenAsync(ct); await target.OpenAsync(ct);
                source.BackupDatabase(target);
                using var check = target.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!Equals(await check.ExecuteScalarAsync(ct), "ok")) throw new InvalidDataException("Database backup verification failed.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, final);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
