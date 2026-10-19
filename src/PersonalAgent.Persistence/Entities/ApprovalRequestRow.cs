using PersonalAgent.Core.Security;

namespace PersonalAgent.Persistence.Entities;

public sealed class ApprovalRequestRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ToolCallId { get; set; } = "";
    public long RunVersion { get; set; }
    public string OwnerSource { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string ActionJson { get; set; } = "{}";
    public string ActionHash { get; set; } = "";
    public int PayloadSchemaVersion { get; set; } = 1;
    public ApprovalStatus Status { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
