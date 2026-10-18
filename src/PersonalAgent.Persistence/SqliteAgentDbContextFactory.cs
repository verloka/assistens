using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PersonalAgent.Persistence;

public sealed class SqliteAgentDbContextFactory(string databasePath) : IDbContextFactory<AgentDbContext>
{
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);
    public AgentDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath, ForeignKeys = true, Pooling = false
    }.ToString()).Options);
}
