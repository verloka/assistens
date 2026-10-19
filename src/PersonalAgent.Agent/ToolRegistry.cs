using Json.Schema;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Security;
using PersonalAgent.Core.Persistence;
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
    private readonly ToolAccessContext? accessContext;
    private readonly IToolInvocationAuthorizer? invocationAuthorizer;
    private readonly IToolCallRepository? toolCalls;

    public ToolRegistry(IEnumerable<IAgentTool> tools,
        Func<Guid, RunStepAccess, CancellationToken, Task> waitAtBoundary,
        Func<ToolDescriptor, ToolExecutionContext, CancellationToken, ValueTask<bool>>? authorize = null,
        ToolAccessContext? accessContext = null, IToolInvocationAuthorizer? invocationAuthorizer = null, IToolCallRepository? toolCalls = null)
    {
        boundary = waitAtBoundary;
        this.accessContext = accessContext;
        this.invocationAuthorizer = invocationAuthorizer;
        this.toolCalls = toolCalls;
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
        foreach (var id in allowed)
            if (entries[id].Descriptor.ObservationToolId is { } observationId &&
                (!allowed.Contains(observationId, StringComparer.Ordinal) || !entries[observationId].Descriptor.IsReadOnly
                 || entries[observationId].Descriptor.ObservationToolId is not null))
                throw new ModelProviderException("ObservationToolUnavailable", "A tool requires an explicitly allowed read-only observation tool before it can execute.");
        return new(this, request.RunId, request.ConversationId, allowed.Select(id => entries[id]).ToArray(), accessContext);
    }

    public sealed record Invocation(ToolDescriptor Descriptor, ToolResult Result, IReadOnlyList<AIContent> Contents);

    public sealed class BoundTools
    {
        private readonly ToolRegistry registry;
        private readonly Guid runId, conversationId;
        private readonly ToolAccessContext? accessContext;
        private readonly FrozenDictionary<string, Entry> allowed;
        private readonly SemaphoreSlim invocation = new(1, 1);
        private readonly HashSet<string> seenCallIds = new(StringComparer.Ordinal);
        public IReadOnlyList<AIFunction> Functions { get; }
        public bool RequiresDesktop => allowed.Values.Any(entry => entry.Descriptor.RequiresDesktop);
        internal bool RequiresObservationAfter(string toolId) => allowed[toolId].Descriptor.ObservationToolId is not null;

        internal BoundTools(ToolRegistry registry, Guid runId, Guid conversationId, Entry[] allowed, ToolAccessContext? accessContext)
        {
            this.registry = registry; this.runId = runId; this.conversationId = conversationId;
            this.accessContext = accessContext;
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
            ToolResult? completedAction = null;
            try
            {
                var result = await ExecuteOneAsync(entry, callId, arguments, ct).ConfigureAwait(false);
                if (result.Status == ToolResultStatus.Success && entry.Descriptor.ObservationToolId is { } observationId)
                {
                    completedAction = result with { Sources = result.Sources.ToArray(), Warnings = result.Warnings.ToArray() };
                    var observationEntry = allowed[observationId];
                    var observed = await ExecuteOneAsync(observationEntry, callId + ":observation", JsonSerializer.SerializeToElement(new { }), ct).ConfigureAwait(false);
                    var data = JsonSerializer.SerializeToElement(new
                    {
                        actionStatus = "Success",
                        actionData = result.StructuredData,
                        observationToolId = observationId,
                        observationSource = observationEntry.Descriptor.Source,
                        observation = observed.StructuredData,
                        observationErrorCode = observed.ErrorCode
                    });
                    if (observed.Status == ToolResultStatus.Success && observed.Images.Count > 0)
                        result = result with { StructuredData = data, Images = observed.Images,
                            Sources = result.Sources.Concat(observed.Sources).ToArray(), Warnings = result.Warnings.Concat(observed.Warnings).ToArray(),
                            Text = result.Text + " Fresh post-action observation attached; use its new observationId for the next input." };
                    else
                        result = new()
                        {
                            Status = ToolResultStatus.OutcomeUnknown,
                            ErrorCode = "PostActionObservationFailed",
                            StructuredData = data,
                            Sources = result.Sources.Concat(observed.Sources).ToArray(),
                            Warnings = result.Warnings.Concat(observed.Warnings).ToArray(),
                            Text = "The action completed but its resulting UI could not be observed. Do not repeat the action; check the current state explicitly."
                        };
                }
                return Map(entry.Descriptor, result, clock.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (RunPersistenceException) { throw; }
            catch (Exception)
            {
                return Map(entry.Descriptor, new()
                {
                    Status = completedAction is not null ? ToolResultStatus.OutcomeUnknown : ToolResultStatus.Failed,
                    ErrorCode = completedAction is not null ? "PostActionObservationFailed" : "ToolExecutionFailed",
                    Sources = completedAction?.Sources ?? [], Warnings = completedAction?.Warnings ?? [],
                    Text = completedAction is not null ? "The action completed but its resulting observation failed; do not repeat the action." : "Tool execution failed; no automatic retry was performed."
                }, clock.Elapsed);
            }
            finally { invocation.Release(); }
        }

        private async Task<ToolResult> ExecuteOneAsync(Entry entry, string callId, JsonElement arguments, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(callId) || !seenCallIds.Add(callId)) return Rejected("ToolCallReplay", "The tool call identity is invalid or has already been handled.");
            if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 65_536 || !entry.Schema.Evaluate(arguments).IsValid)
                return Rejected("InvalidToolArguments", "Tool arguments do not match the registered schema.");
            var descriptor = entry.Descriptor;
            var context = new ToolExecutionContext(runId, conversationId, callId);
            await registry.boundary(runId, new(descriptor.RequiresDesktop, descriptor.IsReadOnly && descriptor.RiskLevel == ToolRiskLevel.ReadOnly, descriptor.IndependentOfDesktop), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (descriptor.RiskLevel == ToolRiskLevel.Unknown || accessContext is not null && !accessContext.Allows(descriptor, context)
                || !await registry.authorize(descriptor, context, ct).ConfigureAwait(false))
                return Rejected("ToolPermissionDenied", "The tool is not authorized for this run.");
            ct.ThrowIfCancellationRequested();
            ToolDispatchAuthorization? dispatchAuthorization = null;
            if (registry.invocationAuthorizer is not null)
            {
                if (!await registry.invocationAuthorizer.AuthorizeAsync(descriptor, new(arguments.Clone()), context, ct).ConfigureAwait(false))
                    return Rejected("ToolPermissionDenied", "The action was denied, expired or no longer matches its approval.");
                // Human wait never lends authority across a pause, cancellation or changed run policy.
                await registry.boundary(runId, new(descriptor.RequiresDesktop, descriptor.IsReadOnly && descriptor.RiskLevel == ToolRiskLevel.ReadOnly, descriptor.IndependentOfDesktop), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (accessContext is not null && !accessContext.Allows(descriptor, context)
                    || !await registry.authorize(descriptor, context, ct).ConfigureAwait(false))
                    return Rejected("ToolPermissionDenied", "The tool is no longer authorized for this run.");
                dispatchAuthorization = await registry.invocationAuthorizer.ValidateDispatchAsync(descriptor, new(arguments.Clone()), context, ct).ConfigureAwait(false);
                if (!dispatchAuthorization.Allowed)
                    return Rejected("ToolPermissionDenied", "The approved action expired or changed before dispatch.");
            }
            ct.ThrowIfCancellationRequested();
            ToolCallDispatch? dispatch = null;
            if (registry.toolCalls is not null && !descriptor.IsReadOnly)
            {
                if (dispatchAuthorization is not { ApprovalRequestId: { } approvalId, ExpectedRunVersion: { } runVersion })
                    return Rejected("ToolDispatchDenied", "A durable exact action approval is required before this operation.");
                try { dispatch = await registry.toolCalls.BeginAsync(descriptor, new(arguments.Clone()), context, runVersion, approvalId, ct).ConfigureAwait(false); }
                catch (ToolDispatchConflictException error)
                { return error.OutcomeUnknown ? Unknown() : Rejected("ToolCallReplay", "This operation was already acknowledged and cannot be repeated."); }
                catch (ToolDispatchDeniedException) { return Rejected("ToolDispatchDenied", "The action or run no longer matches its durable approval."); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { return Rejected("ToolDispatchSaveFailed", "The dispatch reservation could not be confirmed; no tool was invoked."); }
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await entry.Tool.ExecuteAsync(new(arguments.Clone()), context, ct).ConfigureAwait(false);
                if (dispatch is not null)
                {
                    // Ack is local history, not another external operation; caller cancellation must not erase it.
                    try { await registry.toolCalls!.CompleteAsync(dispatch.Id, result.Status, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { return Unknown(); }
                    if (result.Status != ToolResultStatus.Success) return result with { Status = ToolResultStatus.OutcomeUnknown,
                        Text = "The dispatched operation was not acknowledged as successful. Verify its real-world outcome; no retry was performed." };
                }
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (dispatch is not null) await MarkUnknownAsync(dispatch.Id).ConfigureAwait(false);
                throw;
            }
            catch (Exception) when (dispatch is not null)
            { await MarkUnknownAsync(dispatch.Id).ConfigureAwait(false); return Unknown(); }
        }

        private async Task MarkUnknownAsync(Guid dispatchId)
        {
            try { await registry.toolCalls!.CompleteAsync(dispatchId, ToolResultStatus.OutcomeUnknown, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { } // Durable Dispatched remains unsafe to replay and is recovered as OutcomeUnknown.
        }
        private static ToolResult Unknown() => new() { Status = ToolResultStatus.OutcomeUnknown, ErrorCode = "ToolOutcomeUnknown",
            Text = "The operation outcome is unknown. Verify its real-world state before starting a new action; no retry was performed." };

        private static Invocation Map(ToolDescriptor descriptor, ToolResult result, TimeSpan elapsed)
        {
            result = result with { Sources = result.Sources.ToArray(), Warnings = result.Warnings.ToArray() };
            if (!Enum.IsDefined(result.Status) || result.Sources.Any(source => string.IsNullOrWhiteSpace(source.Title) || !source.Url.IsAbsoluteUri || source.Url.Scheme is not ("https" or "http"))
                || result.Images.Any(image => image.Bytes.IsEmpty || image.Bytes.Length > 20_000_000 || image.MediaType is not ("image/png" or "image/jpeg")))
                result = new() { Status = ToolResultStatus.Failed, ErrorCode = "InvalidToolResult", Text = "The tool returned unsupported content." };
            List<AIContent> contents = [new TextContent(JsonSerializer.Serialize(new
            {
                toolId = descriptor.Id, source = descriptor.Source, status = result.Status.ToString(), result.Text,
                data = result.StructuredData, result.ErrorCode, result.Sources, result.Warnings, durationMs = elapsed.TotalMilliseconds
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
