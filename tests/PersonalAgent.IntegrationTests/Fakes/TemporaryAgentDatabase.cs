using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Persistence;
using System.IO;

namespace PersonalAgent.IntegrationTests.Fakes;

internal sealed class TemporaryAgentDatabase : IDisposable, IDbContextFactory<AgentDbContext>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.Tests", Guid.NewGuid().ToString("N"));
    public TemporaryAgentDatabase() => Directory.CreateDirectory(directory);
    public AgentDbContext Open() => new(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "history.db"), ForeignKeys = true, Pooling = false
    }.ToString()).Options);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    public AgentDbContext CreateDbContext() => Open();
}
