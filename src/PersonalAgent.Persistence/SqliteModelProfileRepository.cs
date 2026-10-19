using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteModelProfileRepository(IDbContextFactory<AgentDbContext> factory) : IModelProfileRepository
{
    public async Task<IReadOnlyList<ModelProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var database = factory.CreateDbContext();
        var rows = await database.ModelProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row =>
        {
            var profile = JsonSerializer.Deserialize<ModelProfile>(row.ProfileJson) ?? throw new InvalidDataException("Invalid model profile.");
            Validate(profile);
            if (profile.Id != row.Id) throw new InvalidDataException("Model profile identity mismatch.");
            return profile;
        }).ToArray();
    }

    public async Task SaveAsync(ModelProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        await using var database = factory.CreateDbContext();
        var row = await database.ModelProfiles.FindAsync([profile.Id], cancellationToken).ConfigureAwait(false);
        if (row is null) database.ModelProfiles.Add(row = new ModelProfileRow { Id = profile.Id });
        row.ProfileJson = JsonSerializer.Serialize(profile);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(ModelProfile profile)
    {
        if (profile.SchemaVersion != 1 || string.IsNullOrWhiteSpace(profile.Id) || profile.Id == "fake"
            || string.IsNullOrWhiteSpace(profile.ProviderId) || string.IsNullOrWhiteSpace(profile.ModelId)
            || string.IsNullOrWhiteSpace(profile.SecretRef) || !profile.SecretRef.StartsWith("dpapi:", StringComparison.Ordinal)
            || !Guid.TryParseExact(profile.SecretRef[6..], "N", out _)
            || profile.Capabilities is null || profile.GenerationSettings is null)
            throw new InvalidDataException("Unsupported model profile configuration.");
    }
}
