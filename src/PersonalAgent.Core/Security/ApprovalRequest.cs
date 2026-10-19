using PersonalAgent.Core.Agents;
using System.Security.Cryptography;
using System.Text.Json;

namespace PersonalAgent.Core.Security;

public sealed record ApprovalOwner(string Source, string UserId)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 128 || string.IsNullOrWhiteSpace(UserId) || UserId.Length > 256)
            throw new ArgumentException("An approval requires an authenticated owner identity.");
    }
}

public sealed record PendingToolAction(Guid RunId, Guid ConversationId, long RunVersion, string ToolCallId,
    string ToolId, ToolSource Source, ToolRiskLevel RiskLevel, string? ToolVersion, JsonElement Arguments,
    string Description, string Destination)
{
    public PendingToolAction Snapshot()
    {
        if (RunId == Guid.Empty || ConversationId == Guid.Empty || RunVersion < 0
            || string.IsNullOrWhiteSpace(ToolCallId) || ToolCallId.Length > 256 || string.IsNullOrWhiteSpace(ToolId) || ToolId.Length > 64
            || Source is null || !Enum.IsDefined(Source.Kind) || string.IsNullOrWhiteSpace(Source.Id) || Source.Id.Length > 256
            || !Enum.IsDefined(RiskLevel) || RiskLevel is ToolRiskLevel.Unknown or ToolRiskLevel.ReadOnly
            || Source.Kind == ToolSourceKind.Mcp && (!Guid.TryParseExact(Source.Id, "N", out _) || ToolVersion is not { Length: 64 } || ToolVersion.Any(value => !char.IsAsciiHexDigit(value)))
            || ToolVersion?.Length > 256 || Arguments.ValueKind != JsonValueKind.Object || Arguments.GetRawText().Length > 65_536
            || string.IsNullOrWhiteSpace(Description) || Description.Length > 4096 || string.IsNullOrWhiteSpace(Destination) || Destination.Length > 2048)
            throw new ArgumentException("A pending action requires exact bounded validated operation and destination metadata.");
        return this with { Arguments = Arguments.Clone() };
    }

    public string Fingerprint()
    {
        var snapshot = Snapshot();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(JsonSerializer.SerializeToElement(snapshot), writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new ArgumentException("Duplicate action payload properties cannot be approved.");
            writer.WriteStartObject();
            foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

public enum ApprovalStatus { Pending, Approved, Rejected, Expired }
public sealed record ApprovalRequest(Guid Id, PendingToolAction Action, string ActionHash, ApprovalOwner Owner,
    ApprovalStatus Status, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? DecidedAtUtc = null, ApprovalOwner? DecisionBy = null);
public sealed record DecideApprovalCommand(Guid RequestId, long ExpectedVersion, string ActionHash, ApprovalOwner Owner, bool Approve);
public sealed class ApprovalConflictException() : InvalidOperationException("The approval is unavailable, stale, expired or does not match this owner and action.");
