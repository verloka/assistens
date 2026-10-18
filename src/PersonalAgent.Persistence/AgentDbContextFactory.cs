using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PersonalAgent.Persistence;

// Used by migration tooling only; never opens a production history or reads secrets.
public sealed class AgentDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AgentDbContext>()
        .UseSqlite("Data Source=:memory:").Options);
}
