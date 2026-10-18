using Json.Schema;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalAgent.Agent;

public sealed partial class ToolRegistry
{
    internal sealed record Entry(IAgentTool Tool, ToolDescriptor Descriptor, JsonSchema Schema);
    private readonly FrozenDictionary<string, Entry> entries;
    private readonly Func<ToolDescriptor, ToolExecutionContext, CancellationToken, ValueTask<bool>> authorize;
    private readonly Func<Guid, RunStepAccess, CancellationToken, Task> boundary;

    public ToolRegistry(IEnumerable<IAgentTool> tools,
        Func<Guid, RunStepAccess, CancellationToken, Task> waitAtBoundary,
        Func<ToolDescriptor, ToolExecutionContext, CancellationToken, ValueTask<bool>>? authorize = null)
    {
        boundary = waitAtBoundary;
        this.authorize = authorize ?? ((descriptor, _, _) => ValueTask.FromResult(
            descriptor.Source.Kind == ToolSourceKind.Local && descriptor.RiskLevel == ToolRiskLevel.ReadOnly
            && descriptor.IsReadOnly && !descriptor.RequiresDesktop));
        var registered = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var declared = tool.Descriptor;
            var descriptor = declared with { JsonSchema = declared.JsonSchema.Clone() };
            if (!ToolIdPattern().IsMatch(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.Description)
                || string.IsNullOrWhiteSpace(descriptor.Source.Id) || !Enum.IsDefined(descriptor.Source.Kind)
                || !Enum.IsDefined(descriptor.RiskLevel) || descriptor.JsonSchema.ValueKind != JsonValueKind.Object
                || !descriptor.JsonSchema.TryGetProperty("type", out var type) || type.GetString() != "object"
                || descriptor.JsonSchema.GetRawText().Length > 65_536)
                throw new ArgumentException("Invalid tool descriptor.", nameof(tools));
            ValidateReferences(descriptor.JsonSchema);
            var schema = JsonSchema.Build(descriptor.JsonSchema, new BuildOptions
            { Dialect = Dialect.Draft202012, SchemaRegistry = new() { Fetch = (_, _) => null } });
            if (!registered.TryAdd(descriptor.Id, new(tool, descriptor, schema)))
                throw new ArgumentException("Duplicate tool ID.", nameof(tools));
        }
        entries = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public BoundTools Bind(AgentRequest request)
    {
        var allowed = request.AllowedToolIds.Distinct(StringComparer.Ordinal).ToArray();
        if (allowed.Any(id => !entries.ContainsKey(id)))
            throw new ModelProviderException("ToolUnavailable", "An allowed tool is not registered; model execution cannot start.");
        return new(this, request.RunId, request.ConversationId, allowed.Select(id => entries[id]).ToArray());
    }

    public sealed record Invocation(ToolDescriptor Descriptor, ToolResult Result, IReadOnlyList<AIContent> Contents);

    public sealed class BoundTools
    {
        private readonly ToolRegistry registry;
        private readonly Guid runId, conversationId;
        private readonly FrozenDictionary<string, Entry> allowed;
        private readonly SemaphoreSlim invocation = new(1, 1);
        private readonly HashSet<string> seenCallIds = new(StringComparer.Ordinal);
        public IReadOnlyList<AIFunction> Functions { get; }
        public bool RequiresDesktop => allowed.Values.Any(entry => entry.Descriptor.RequiresDesktop);

        internal BoundTools(ToolRegistry registry, Guid runId, Guid conversationId, Entry[] allowed)
        {
            this.registry = registry; this.runId = runId; this.conversationId = conversationId;
            this.allowed = allowed.ToFrozenDictionary(entry => entry.Descriptor.Id, StringComparer.Ordinal);
            Functions = Array.AsReadOnly(allowed.Select(entry => (AIFunction)new Function(this, entry.Descriptor)).ToArray());
        }

        public async Task<Invocation> InvokeAsync(string toolId, string callId, JsonElement arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!allowed.TryGetValue(toolId, out var entry))
                throw new ModelProviderException("ToolNotAllowed", "The model requested an unapproved tool; no action was executed.");
            await invocation.WaitAsync(ct).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            ToolResult result;
            try
            {
                if (string.IsNullOrWhiteSpace(callId) || !seenCallIds.Add(callId)) result = Rejected("ToolCallReplay", "The tool call identity is invalid or has already been handled.");
                else if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 65_536 || !entry.Schema.Evaluate(arguments).IsValid)
                    result = Rejected("InvalidToolArguments", "Tool arguments do not match the registered schema.");
                else
                {
                    var descriptor = entry.Descriptor;
                    var context = new ToolExecutionContext(runId, conversationId, callId);
                    await registry.boundary(runId, new(descriptor.RequiresDesktop, descriptor.IsReadOnly && descriptor.RiskLevel == ToolRiskLevel.ReadOnly, descriptor.IndependentOfDesktop), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (descriptor.RiskLevel == ToolRiskLevel.Unknown || !await registry.authorize(descriptor, context, ct).ConfigureAwait(false))
                        result = Rejected("ToolPermissionDenied", "The tool is not authorized for this run.");
                    else
                    {
                        ct.ThrowIfCancellationRequested();
                        result = await entry.Tool.ExecuteAsync(new(arguments.Clone()), context, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                return Map(entry.Descriptor, result, clock.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (RunPersistenceException) { throw; }
            catch (Exception)
            { return Map(entry.Descriptor, new() { Status = ToolResultStatus.Failed, ErrorCode = "ToolExecutionFailed", Text = "Tool execution failed; no automatic retry was performed." }, clock.Elapsed); }
            finally { invocation.Release(); }
        }

        private static Invocation Map(ToolDescriptor descriptor, ToolResult result, TimeSpan elapsed)
        {
            if (!Enum.IsDefined(result.Status) || result.Images.Any(image => image.Bytes.IsEmpty || image.Bytes.Length > 20_000_000 || image.MediaType is not ("image/png" or "image/jpeg")))
                result = new() { Status = ToolResultStatus.Failed, ErrorCode = "InvalidToolResult", Text = "The tool returned unsupported content." };
            List<AIContent> contents = [new TextContent(JsonSerializer.Serialize(new
            {
                toolId = descriptor.Id, source = descriptor.Source, status = result.Status.ToString(), result.Text,
                data = result.StructuredData, result.ErrorCode, durationMs = elapsed.TotalMilliseconds
            }))];
            contents.AddRange(result.Images.Select(image => new DataContent(image.Bytes.ToArray(), image.MediaType)));
            return new(descriptor, result, contents.AsReadOnly());
        }
        private static ToolResult Rejected(string code, string message) => new() { Status = ToolResultStatus.Blocked, ErrorCode = code, Text = message };

        private sealed class Function(BoundTools binding, ToolDescriptor descriptor) : AIFunction
        {
            public override string Name => descriptor.Id;
            public override string Description => descriptor.Description;
            public override JsonElement JsonSchema => descriptor.JsonSchema;
            protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
                var result = await binding.InvokeAsync(Name, callId, JsonSerializer.SerializeToElement(arguments), cancellationToken).ConfigureAwait(false);
                return result.Contents;
            }
        }
    }

    private static void ValidateReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$ref" or "$dynamicRef" && (property.Value.ValueKind != JsonValueKind.String || !property.Value.GetString()!.StartsWith('#'))
                    || property.Name == "$schema" && property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema"
                    || property.Name == "$vocabulary")
                    throw new ArgumentException("Tool schemas must be self-contained Draft 2020-12 documents.");
                ValidateReferences(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateReferences(item);
    }
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolIdPattern();
}
