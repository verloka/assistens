using PersonalAgent.Core.Domain;

namespace PersonalAgent.Persistence.Entities;

public sealed class ScheduleRow
{
    public Guid Id { get; set; }
    public Guid TaskDefinitionId { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public ScheduleMode Mode { get; set; }
    public string RecurrenceJson { get; set; } = "{}";
    public string TimeZoneId { get; set; } = "";
    public bool Enabled { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? NextRunAtUtc { get; set; }
}
