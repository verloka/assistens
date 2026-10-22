using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PersonalAgent.Persistence;

public sealed class SettingsTransfer(IDbContextFactory<AgentDbContext> factory)
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const string ImportedInstructions = "Review and enter task instructions before enabling this imported schedule.";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<string> ExportAsync(ApprovalOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var models = (await db.ModelProfiles.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct)).Select(row =>
        {
            var model = Read<ModelProfile>(row.ProfileJson);
            if (model.SchemaVersion != 1 || model.Id != row.Id) throw Invalid();
            return new ModelSettings(model.Id, model.ProviderId, model.ModelId, model.Capabilities, model.GenerationSettings, model.Pricing);
        }).ToArray();
        var mcp = (await db.McpConnections.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct)).Select(row =>
        {
            var profile = Read<McpConnectionProfile>(row.ProfileJson); var config = profile.Configuration;
            if (profile.SchemaVersion != 1 || config.SchemaVersion != 1 || config.Id != row.Id) throw Invalid();
            var origin = config.Http is { } http ? new UriBuilder(http.Endpoint) { UserName = "", Password = "", Path = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority) : null;
            return new McpSettings(Safe(config.Name), config.Transport, config.Stdio?.Command, origin, config.ConnectTimeout.TotalSeconds, config.RequestTimeout.TotalSeconds);
        }).ToArray();
        var schedules = (await (from row in db.Schedules.AsNoTracking() join task in db.TaskDefinitions.AsNoTracking() on row.TaskDefinitionId equals task.Id
            orderby row.Id select new { Row = row, Task = task }).ToArrayAsync(ct)).Select(item =>
        {
            if (item.Row.PayloadSchemaVersion != 1) throw Invalid();
            return new ScheduleSettings(Safe(item.Task.Name), item.Task.ModelProfileId, ScheduleRecurrence.Parse(item.Row.RecurrenceJson), item.Row.TimeZoneId,
                item.Row.MisfirePolicy, item.Row.DesktopPolicy, item.Row.MaxCatchUpRuns);
        }).ToArray();
        var channels = (await db.ChannelConfigurations.AsNoTracking().Where(row => row.OwnerId == owner.UserId && row.OwnerSource == owner.Source)
            .OrderBy(row => row.Id).ToArrayAsync(ct)).Select(row =>
        {
            if (row.PayloadSchemaVersion != 1) throw Invalid();
            var config = Read<ChannelConfiguration>(row.ConfigurationJson).Snapshot();
            return new ChannelSettings(Safe(config.Name), config.Route.ChannelId, config.Route.ChannelId == "desktop" ? "local" : config.Route.DestinationId,
                config.Route.ChannelId == "desktop" ? "local" : config.SenderId, config.Events);
        }).ToArray();
        var days = (await db.HistoryRetention.AsNoTracking().SingleOrDefaultAsync(ct))?.Days ?? 30;
        var json = JsonSerializer.Serialize(new SettingsPackage(1, models, mcp, schedules, channels, days), Json);
        Parse(json); // Apply the same strict transferable-field validation before creating a file.
        await transaction.CommitAsync(ct); return json;
    }

    public static SettingsPackage Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid();
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        CheckDuplicates(document.RootElement);
        var package = JsonSerializer.Deserialize<SettingsPackage>(json, Json) ?? throw Invalid();
        Validate(package); return package;
    }

    // Add new disabled identities, never overwrite active schedules/connections or safety records.
    // Models keep only explicitly configured local credentials; foreign secret refs cannot enter this DTO.
    public async Task ImportAsync(SettingsPackage package, ApprovalOwner owner, CancellationToken ct = default)
    {
        package = Parse(JsonSerializer.Serialize(package, Json)); owner.Validate();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var model in package.Models)
        {
            var row = await db.ModelProfiles.SingleOrDefaultAsync(row => row.Id == model.Id, ct) ?? throw new InvalidDataException("Configure matching model credentials locally before importing settings.");
            var local = Read<ModelProfile>(row.ProfileJson);
            if (local.SchemaVersion != 1 || local.Id != row.Id || local.ProviderId != model.ProviderId || local.SecretRef is null || !local.SecretRef.StartsWith("dpapi:", StringComparison.Ordinal)
                || !Guid.TryParseExact(local.SecretRef[6..], "N", out _)) throw new InvalidDataException("Local model credential binding does not match the import.");
            row.ProfileJson = JsonSerializer.Serialize(local with { ModelId = model.ModelId, Capabilities = model.Capabilities,
                GenerationSettings = model.GenerationSettings, Pricing = model.Pricing });
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var item in package.Mcp)
        {
            var id = Guid.NewGuid();
            var config = new McpConnectionConfig { Id = id, Name = item.Name, Transport = item.Transport,
                Stdio = item.Transport == McpTransportKind.Stdio ? new(item.Command!, []) : null,
                Http = item.HttpOrigin is { } origin ? new(new(origin), AllowLoopbackHttp: new Uri(origin).IsLoopback) : null,
                ConnectTimeout = TimeSpan.FromSeconds(item.ConnectTimeoutSeconds), RequestTimeout = TimeSpan.FromSeconds(item.RequestTimeoutSeconds) };
            db.McpConnections.Add(new() { Id = id, ProfileJson = JsonSerializer.Serialize(new McpConnectionProfile { Configuration = config }) });
        }
        foreach (var item in package.Schedules)
        {
            var taskId = Guid.NewGuid(); var id = Guid.NewGuid();
            db.TaskDefinitions.Add(new() { Id = taskId, Name = item.Name, Instructions = ImportedInstructions, ModelProfileId = item.ModelProfileId,
                Enabled = false, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Schedules.Add(new() { Id = id, TaskDefinitionId = taskId, Mode = item.Recurrence.Mode, RecurrenceJson = JsonSerializer.Serialize(item.Recurrence),
                TimeZoneId = item.TimeZoneId, Enabled = false, CreatedAtUtc = now, UpdatedAtUtc = now, MisfirePolicy = item.MisfirePolicy,
                DesktopPolicy = item.DesktopPolicy, MaxCatchUpRuns = item.MaxCatchUpRuns });
        }
        foreach (var item in package.Channels)
        {
            var id = Guid.NewGuid(); var route = item.ChannelId == "desktop" ? new ChannelRoute("desktop", "local", owner.UserId)
                : new(item.ChannelId, id.ToString("N"), item.DestinationId);
            var config = new ChannelConfiguration(id, owner, item.Name, route, item.ChannelId == "desktop" ? owner.UserId : item.SenderId,
                false, false, false, null, item.Events, now, Version: 1).Snapshot();
            if (await db.ChannelConfigurations.AnyAsync(row => row.OwnerSource == owner.Source && row.OwnerId == owner.UserId
                && row.ChannelId == route.ChannelId && row.ConnectionId == route.ConnectionId && row.DestinationId == route.DestinationId, ct))
                throw new InvalidDataException("An imported channel conflicts with local settings; no settings were applied.");
            db.ChannelConfigurations.Add(new() { Id = id, OwnerSource = owner.Source, OwnerId = owner.UserId, ChannelId = route.ChannelId,
                ConnectionId = route.ConnectionId, DestinationId = route.DestinationId, Version = 1, ConfigurationJson = JsonSerializer.Serialize(config) });
        }
        var retention = await db.HistoryRetention.SingleOrDefaultAsync(ct);
        if (retention is null) db.HistoryRetention.Add(retention = new());
        retention.Days = package.RetentionDays; retention.Enabled = false;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    private static void Validate(SettingsPackage package)
    {
        if (package.SchemaVersion != 1 || package.Models is null || package.Mcp is null || package.Schedules is null || package.Channels is null
            || new[] { package.Models.Count, package.Mcp.Count, package.Schedules.Count, package.Channels.Count }.Any(count => count > 1000)) throw Invalid();
        new HistoryRetentionPolicy(false, package.RetentionDays).Validate();
        var ids = new HashSet<string>(StringComparer.Ordinal) { "fake" };
        foreach (var model in package.Models)
        {
            if (model is null || !Identifier(model.Id, 100) || !ids.Add(model.Id) || !Identifier(model.ProviderId, 100) || !Identifier(model.ModelId, 200)
                || model.Capabilities is null || model.GenerationSettings is null || model.GenerationSettings.MaxOutputTokens is <= 0
                || model.GenerationSettings.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature is < 0 or > 2)
                || model.Pricing is { } pricing && (pricing.InputPerMillionTokens < 0 || pricing.OutputPerMillionTokens < 0
                    || pricing.Currency is not { Length: 3 } || pricing.Currency.Any(c => c is < 'A' or > 'Z'))) throw Invalid();
        }
        foreach (var item in package.Mcp)
        {
            if (item is null || !Text(item.Name, 256) || !Enum.IsDefined(item.Transport)
                || !double.IsFinite(item.ConnectTimeoutSeconds) || item.ConnectTimeoutSeconds is < 1 or > 120
                || !double.IsFinite(item.RequestTimeoutSeconds) || item.RequestTimeoutSeconds is < 1 or > 300) throw Invalid();
            if (item.Transport == McpTransportKind.Stdio)
            { if (item.HttpOrigin is not null || !Text(item.Command, 4096) || !Path.IsPathFullyQualified(item.Command!)) throw Invalid(); }
            else if (item.Command is not null || !Uri.TryCreate(item.HttpOrigin, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0 || uri.Query.Length != 0
                || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw Invalid();
        }
        foreach (var item in package.Schedules)
        {
            if (item is null || !Text(item.Name, 256) || !ids.Contains(item.ModelProfileId) || item.Recurrence is null
                || !Enum.IsDefined(item.MisfirePolicy) || !Enum.IsDefined(item.DesktopPolicy) || item.MaxCatchUpRuns is < 1 or > 10) throw Invalid();
            item.Recurrence.Snapshot(); ScheduleCalculator.ResolveTimeZone(item.TimeZoneId);
        }
        var desktop = false;
        foreach (var item in package.Channels)
        {
            if (item is null || !Text(item.Name, 128) || !Identifier(item.ChannelId, 64) || !Identifier(item.DestinationId, 256)
                || !Identifier(item.SenderId, 256) || item.Events is null || item.Events.Count > 6 || item.Events.Any(evt => !Enum.IsDefined(evt))
                || item.Events.Distinct().Count() != item.Events.Count) throw Invalid();
            if (item.ChannelId == "desktop") { if (desktop) throw Invalid(); desktop = true; }
        }
    }
    private static bool Identifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9._:-]*$|^-[0-9]+$", RegexOptions.CultureInvariant) && Safe(value) == value;
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl) && Safe(value) == value;
    private static string Safe(string text) => Regex.Replace(ConversationPrivacy.Filter(text), "dpapi:[a-fA-F0-9]{32}", "[redacted]", RegexOptions.CultureInvariant);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw Invalid();
    private static InvalidDataException Invalid() => new("Unsupported or invalid settings package.");
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
}
