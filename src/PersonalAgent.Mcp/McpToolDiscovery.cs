using ModelContextProtocol.Protocol;
using PersonalAgent.Core.Agents;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalAgent.Mcp;

// Discovery supplies untrusted declarations, never permissions or executable delegates.
public sealed record McpDiscoveredTool(string ServerName, string Version, ToolDescriptor Descriptor) : IAgentTool
{
    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolResult { Status = ToolResultStatus.Blocked, ErrorCode = "McpToolReviewRequired",
            Text = "The discovered MCP tool requires owner classification and an approved execution binding." });
    }
}

public sealed record McpDiscoveryResult(IReadOnlyList<McpDiscoveredTool> Tools, IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

public sealed class McpToolDiscovery
{
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly Dictionary<Guid, IReadOnlyList<McpDiscoveredTool>> previous = [];

    public async Task<McpDiscoveryResult> RefreshAsync(McpClientConnection connection, CancellationToken cancellationToken = default)
    {
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        var id = connection.Configuration.Id;
        try
        {
            var listed = await connection.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var tools = Map(id, listed.Select(tool => tool.ProtocolTool));
            var old = (previous.GetValueOrDefault(id) ?? []).ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);
            var current = tools.ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);
            var result = new McpDiscoveryResult(tools,
                Array.AsReadOnly(tools.Where(tool => !old.ContainsKey(tool.Descriptor.Id)).Select(tool => tool.Descriptor.Id).ToArray()),
                Array.AsReadOnly(tools.Where(tool => old.TryGetValue(tool.Descriptor.Id, out var value) && value.Version != tool.Version).Select(tool => tool.Descriptor.Id).ToArray()),
                Array.AsReadOnly(old.Keys.Where(key => !current.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray()));
            previous[id] = tools;
            connection.UpdateToolVersions(tools);
            return result;
        }
        catch (OperationCanceledException) { previous.Remove(id); connection.UpdateToolVersions([]); throw; }
        catch (McpConnectionException) { previous.Remove(id); connection.UpdateToolVersions([]); throw; }
        catch (Exception)
        {
            previous.Remove(id);
            connection.UpdateToolVersions([]);
            throw Invalid();
        }
        finally { refresh.Release(); }
    }

    internal static IReadOnlyList<McpDiscoveredTool> Map(Guid connectionId, IEnumerable<Tool> declarations)
    {
        if (connectionId == Guid.Empty) throw Invalid();
        var tools = new List<McpDiscoveredTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in declarations)
        {
            if (tools.Count >= 1000 || string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 256 || tool.Name.Any(char.IsControl)
                || !names.Add(tool.Name) || tool.Description?.Length > 16_384) throw Invalid();
            var schema = NormalizeSchema(tool.InputSchema);
            // 61 ASCII characters: fits both the registry and the provider's function-name limit.
            var id = "mcp_" + connectionId.ToString("N") + "_" + Hash(Encoding.UTF8.GetBytes(tool.Name))[..24];
            if (!ids.Add(id)) throw Invalid();
            var version = Fingerprint(tool, schema);
            tools.Add(new(tool.Name, version, new(id,
                string.IsNullOrWhiteSpace(tool.Description) ? "Untrusted MCP tool: " + tool.Name : tool.Description,
                schema, new(ToolSourceKind.Mcp, connectionId.ToString("N")), ToolRiskLevel.Unknown)));
        }
        return tools.OrderBy(tool => tool.Descriptor.Id, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    private static JsonElement NormalizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > 65_536) throw Invalid();
        ValidateReferences(schema);
        if (schema.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "object")) throw Invalid();
        var normalized = JsonNode.Parse(schema.GetRawText())!.AsObject();
        normalized["type"] = "object";
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static void ValidateReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "$ref" or "$dynamicRef" && (property.Value.ValueKind != JsonValueKind.String || !property.Value.GetString()!.StartsWith('#'))
                    || property.Name == "$schema" && (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema")
                    || property.Name == "$vocabulary") throw Invalid();
                ValidateReferences(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateReferences(item);
    }

    private static string Fingerprint(Tool tool, JsonElement normalized)
    {
        var declaration = JsonSerializer.SerializeToElement(tool);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(declaration, writer);
        return Hash(stream.ToArray());

        void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Name == "inputSchema" && value.Equals(declaration) ? normalized : property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static McpConnectionException Invalid() => new("McpToolDiscoveryInvalid", "The MCP tool catalog contains unsupported or conflicting declarations; no permissions were granted.");
}
