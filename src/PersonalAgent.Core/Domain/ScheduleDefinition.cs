using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleMode>))]
public enum ScheduleMode { Once, Interval, Daily, Weekdays }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduleRecurrence
{
    public required ScheduleMode Mode { get; init; }
    public DateTimeOffset? OnceAtUtc { get; init; }
    public int? EveryMinutes { get; init; }
    public TimeOnly? LocalTime { get; init; }
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public ScheduleRecurrence Snapshot()
    {
        if (!Enum.IsDefined(Mode) || Days is null || Days.Count > 7 || Days.Any(day => !Enum.IsDefined(day)) || Days.Distinct().Count() != Days.Count)
            throw new ArgumentException("Unsupported recurrence fields.");
        var valid = Mode switch
        {
            ScheduleMode.Once => OnceAtUtc is not null && EveryMinutes is null && LocalTime is null && Days.Count == 0,
            ScheduleMode.Interval => EveryMinutes is > 0 && OnceAtUtc is null && LocalTime is null && Days.Count == 0,
            ScheduleMode.Daily => LocalTime is not null && OnceAtUtc is null && EveryMinutes is null && Days.Count == 0,
            ScheduleMode.Weekdays => LocalTime is not null && OnceAtUtc is null && EveryMinutes is null && Days.Count > 0,
            _ => false
        };
        if (!valid) throw new ArgumentException("Choose exactly the fields required by the recurrence mode.");
        return this with { OnceAtUtc = OnceAtUtc?.ToUniversalTime(), Days = Array.AsReadOnly(Days.Order().ToArray()) };
    }
    public static ScheduleRecurrence Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) throw new ArgumentException("Invalid recurrence configuration.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
            throw new ArgumentException("Recurrence must be an unambiguous object.");
        return (JsonSerializer.Deserialize<ScheduleRecurrence>(json) ?? throw new ArgumentException("Missing recurrence configuration.")).Snapshot();
    }
}

public sealed record ScheduleDefinition
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required Guid TaskDefinitionId { get; init; }
    public required ScheduleRecurrence Recurrence { get; init; }
    public required string TimeZoneId { get; init; }
    public bool Enabled { get; init; } = true;
    public long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? NextRunAtUtc { get; init; }
}
public sealed record ScheduledTask(TaskDefinition Task, ScheduleDefinition Schedule);
public sealed class ScheduleVersionConflictException() : InvalidOperationException("The schedule changed; reload its current version before saving.");
