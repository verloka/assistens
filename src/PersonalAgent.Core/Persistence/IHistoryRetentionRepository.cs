namespace PersonalAgent.Core.Persistence;

public sealed record HistoryRetentionPolicy(bool Enabled = false, int Days = 30)
{
    public void Validate()
    {
        if (Days is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(Days));
    }
}

public interface IHistoryRetentionRepository
{
    Task<HistoryRetentionPolicy> ReadPolicyAsync(CancellationToken ct = default);
    Task SavePolicyAsync(HistoryRetentionPolicy policy, CancellationToken ct = default);
    // Explicit privacy exception: redact content, never remove safety/deduplication identities.
    Task<IReadOnlyList<StoredRun>> ClearExpiredAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct = default);
}
