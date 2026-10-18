using System.Text.Json;
using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Agents;

public enum ToolSourceKind { Local, Computer, Mcp }
public sealed record ToolSource(ToolSourceKind Kind, string Id);
public enum ToolRiskLevel { Unknown, ReadOnly, Write, Destructive, ExternalDisclosure }
public sealed record ToolDescriptor(string Id, string Description, JsonElement JsonSchema, ToolSource Source,
    ToolRiskLevel RiskLevel, bool RequiresDesktop = false, bool IsReadOnly = false, bool IndependentOfDesktop = false,
    string? ObservationToolId = null);
public sealed record ToolArguments(JsonElement Value);
public sealed record ToolExecutionContext(Guid RunId, Guid ConversationId, string ToolCallId);
public enum ToolResultStatus { Success, Failed, Blocked, OutcomeUnknown }
public sealed record ToolImage(ReadOnlyMemory<byte> Bytes, string MediaType);
public sealed record ToolResult
{
    public required ToolResultStatus Status { get; init; }
    public string Text { get; init; } = "";
    public JsonElement? StructuredData { get; init; }
    public IReadOnlyList<ToolImage> Images { get; init; } = [];
    public string? ErrorCode { get; init; }
    public IReadOnlyList<SourceLink> Sources { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken);
}
