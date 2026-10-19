using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using System.Text.Json;

namespace PersonalAgent.Mcp;

// This binding belongs behind ToolRegistry's shared argument, pause and per-run authorization gates.
public sealed class McpAgentTool : IAgentTool
{
    private readonly McpClientConnection connection;
    private readonly McpDiscoveredTool declaration;
    public ToolDescriptor Descriptor { get; }

    public McpAgentTool(McpClientConnection connection, McpDiscoveredTool declaration, McpToolPermission permission)
    {
        if (permission.ConnectionId != connection.Configuration.Id || permission.ToolId != declaration.Descriptor.Id
            || permission.Version != declaration.Version || permission.RiskLevel == ToolRiskLevel.Unknown || !Enum.IsDefined(permission.RiskLevel)
            || declaration.Descriptor.Source != new ToolSource(ToolSourceKind.Mcp, connection.Configuration.Id.ToString("N"))
            || !connection.Configuration.AllowedTools.Contains(declaration.ServerName, StringComparer.Ordinal)
            || !connection.IsToolCurrent(declaration))
            throw new McpConnectionException("McpToolReviewRequired", "The MCP tool is not enabled with a current owner-reviewed classification.");
        this.connection = connection; this.declaration = declaration;
        var readOnly = permission.RiskLevel == ToolRiskLevel.ReadOnly;
        Descriptor = declaration.Descriptor with { RiskLevel = permission.RiskLevel, IsReadOnly = readOnly,
            IndependentOfDesktop = readOnly && permission.IndependentOfDesktop };
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsToolCurrent(declaration)) return Blocked("McpToolUnavailable", "The connection or reviewed tool version is no longer available.");
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.Value.GetRawText());
        if (values is null) return Blocked("InvalidToolArguments", "MCP tool arguments must be an object.");
        try
        {
            var reply = await connection.CallToolAsync(declaration.ServerName, values, cancellationToken).ConfigureAwait(false);
            if (reply.IsError == true) return new() { Status = ToolResultStatus.Failed, ErrorCode = "McpToolError",
                Text = "The MCP server reported a tool error; no automatic retry was performed." };
            return Map(reply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (McpConnectionException error)
        {
            return new() { Status = ToolResultStatus.OutcomeUnknown, ErrorCode = error.Code,
                Text = "The MCP tool outcome was not confirmed; no action was retried." };
        }
        catch (Exception)
        {
            return new() { Status = ToolResultStatus.OutcomeUnknown, ErrorCode = "McpToolOutcomeUnknown",
                Text = "The MCP tool outcome was not confirmed; no action was retried." };
        }
    }

    private static ToolResult Map(CallToolResult reply)
    {
        if (reply.Content.Count > 100 || reply.StructuredContent?.GetRawText().Length > 65_536) return Unsupported();
        var text = new List<string>(); var images = new List<ToolImage>(); var sources = new List<SourceLink>();
        var characters = 0; long imageBytes = 0;
        foreach (var content in reply.Content)
        {
            switch (content)
            {
                case TextContentBlock block:
                    characters += block.Text.Length; if (characters > 65_536) return Unsupported();
                    text.Add(block.Text); break;
                case ImageContentBlock image when image.MimeType is "image/png" or "image/jpeg":
                    if (images.Count >= 4 || image.Data.Length > 28_000_000) return Unsupported();
                    var decoded = image.DecodedData;
                    imageBytes += decoded.Length;
                    if (decoded.IsEmpty || imageBytes > 20_000_000) return Unsupported();
                    images.Add(new(decoded.ToArray(), image.MimeType)); break;
                case ResourceLinkBlock link:
                    if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                        || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(link.Name) || link.Name.Length > 1024) return Unsupported();
                    sources.Add(new(link.Name, uri)); break;
                default: return Unsupported();
            }
        }
        return new() { Status = ToolResultStatus.Success, Text = string.Join("\n", text), StructuredData = reply.StructuredContent?.Clone(),
            Images = images.AsReadOnly(), Sources = sources.AsReadOnly() };
    }

    private static ToolResult Blocked(string code, string text) => new() { Status = ToolResultStatus.Blocked, ErrorCode = code, Text = text };
    private static ToolResult Unsupported() => new() { Status = ToolResultStatus.OutcomeUnknown, ErrorCode = "McpToolResultUnsupported",
        Text = "The MCP tool returned unsupported or oversized content; its effect must not be repeated automatically." };
}
