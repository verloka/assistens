using PersonalAgent.Core.Persistence;

namespace PersonalAgent.Persistence.Entities;

public sealed class ToolCallRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ToolCallId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public Guid ApprovalRequestId { get; set; }
    public string ActionHash { get; set; } = "";
    public int PayloadSchemaVersion { get; set; } = 1;
    public ToolCallDispatchStatus Status { get; set; }
    public DateTimeOffset DispatchedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
