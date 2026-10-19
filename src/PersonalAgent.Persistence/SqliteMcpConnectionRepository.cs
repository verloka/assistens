using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteMcpConnectionRepository(IDbContextFactory<AgentDbContext> factory) : IMcpConnectionRepository
{
    public async Task<IReadOnlyList<McpConnectionProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var database = factory.CreateDbContext();
        var rows = await database.McpConnections.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row =>
        {
            var profile = JsonSerializer.Deserialize<McpConnectionProfile>(row.ProfileJson) ?? throw Invalid();
            Validate(profile);
            if (row.Id != profile.Configuration.Id) throw Invalid();
            return profile;
        }).ToArray();
    }

    public async Task SaveAsync(McpConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        var json = JsonSerializer.Serialize(profile);
        await using var database = factory.CreateDbContext();
        var row = await database.McpConnections.FindAsync([profile.Configuration.Id], cancellationToken).ConfigureAwait(false);
        if (row is null) database.McpConnections.Add(row = new() { Id = profile.Configuration.Id });
        row.ProfileJson = json;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var database = factory.CreateDbContext();
        var row = await database.McpConnections.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (row is not null) { database.McpConnections.Remove(row); await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
    }

    private static void Validate(McpConnectionProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Configuration is not { } config || config.SchemaVersion != 1 || config.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(config.Name) || !Enum.IsDefined(config.Transport) || config.AllowedTools is null || profile.Permissions is null) throw Invalid();
        var references = (config.Stdio?.SecretEnvironment?.Values ?? []).Concat(config.Http?.BearerSecretRef is { } reference ? [reference] : []);
        if (references.Any(value => string.IsNullOrWhiteSpace(value) || !value.StartsWith("dpapi:", StringComparison.Ordinal) || !Guid.TryParseExact(value[6..], "N", out _))) throw Invalid();
        if (config.Http is { Endpoint: var uri } && (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme != "https" && !(config.Http.AllowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback))) throw Invalid();
        if (profile.Permissions.Any(permission => permission is null || permission.ConnectionId != config.Id || permission.RiskLevel == ToolRiskLevel.Unknown
            || !Enum.IsDefined(permission.RiskLevel) || permission.Version is not { Length: 64 } || permission.Version.Any(value => !char.IsAsciiHexDigit(value))
            || permission.ToolId is not { Length: 61 } || !permission.ToolId.StartsWith("mcp_" + config.Id.ToString("N") + "_", StringComparison.Ordinal))) throw Invalid();
        if (profile.Permissions.Select(permission => permission.ToolId).Distinct(StringComparer.Ordinal).Count() != profile.Permissions.Count) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Unsupported MCP connection profile or unprotected credential reference.");
}
