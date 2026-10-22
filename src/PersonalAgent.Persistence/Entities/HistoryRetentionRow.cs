namespace PersonalAgent.Persistence.Entities;

public sealed class HistoryRetentionRow
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public int Days { get; set; } = 30;
}
