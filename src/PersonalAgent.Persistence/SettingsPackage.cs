using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Persistence;

// Allowlisted portable configuration only. No history, opaque references, tokens or grants.
public sealed record SettingsPackage(int SchemaVersion, IReadOnlyList<ModelSettings> Models, IReadOnlyList<McpSettings> Mcp,
    IReadOnlyList<ScheduleSettings> Schedules, IReadOnlyList<ChannelSettings> Channels, int RetentionDays);
public sealed record ModelSettings(string Id, string ProviderId, string ModelId, ModelCapabilities Capabilities,
    ModelGenerationSettings GenerationSettings, ModelPricing? Pricing);
public sealed record McpSettings(string Name, McpTransportKind Transport, string? Command, string? HttpOrigin,
    double ConnectTimeoutSeconds, double RequestTimeoutSeconds);
public sealed record ScheduleSettings(string Name, string ModelProfileId, ScheduleRecurrence Recurrence, string TimeZoneId,
    MisfirePolicy MisfirePolicy, ScheduledDesktopPolicy DesktopPolicy, int MaxCatchUpRuns);
public sealed record ChannelSettings(string Name, string ChannelId, string DestinationId, string SenderId,
    IReadOnlyList<NotificationEventKind> Events);
