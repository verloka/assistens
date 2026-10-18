using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using System.Text.Json;

namespace PersonalAgent.Agent;

// The trusted factory transfers ownership of a new client per execution. SDK state never enters Core.
// Application-owned dispatch: the SDK sees declarations only and cannot invoke tools itself.
public sealed class MafAgentRuntime(
    Func<AgentRequest, CancellationToken, ValueTask<IChatClient>> createClient,
    Func<Guid, CancellationToken, Task> waitAtBoundary,
    ToolRegistry? registry = null, Action? onModelObservationEvaluated = null) : IAgentRuntime
{
    // The trusted composition root determines desktop requirements from the actual allowed tool descriptors.
    public MafAgentRuntime(IModelProvider provider, ModelProfile profile, bool requiresDesktop,
        Func<Guid, CancellationToken, Task> waitAtBoundary)
        : this((request, ct) => OpenProfileAsync(provider, profile, requiresDesktop, request, ct), waitAtBoundary) { }

    public MafAgentRuntime(IModelProvider provider, ModelProfile profile, ToolRegistry registry,
        Func<Guid, CancellationToken, Task> waitAtBoundary, Action? onModelObservationEvaluated = null)
        : this((request, ct) => OpenProfileAsync(provider, profile, registry.Bind(request).RequiresDesktop, request, ct),
            waitAtBoundary, registry, onModelObservationEvaluated)
    { }

    private static async ValueTask<IChatClient> OpenProfileAsync(IModelProvider provider, ModelProfile profile,
        bool requiresDesktop, AgentRequest request, CancellationToken ct)
    {
        if (request.ModelProfileId != profile.Id)
            throw new ModelProviderException("ModelProfileMismatch", "The run does not match the configured model profile.");
        ModelCapabilityPolicy.DemandAgentSupport(profile, requiresDesktop);
        // Transfer the SDK client to this execution's existing owned-client lifecycle.
        return (await provider.OpenAsync(profile, ct).ConfigureAwait(false)).Client;
    }

    public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Limits.Validate();
        if (request.AllowedToolIds.Count != 0 && registry is null)
            return Failure("ToolRegistryUnavailable", "Approved tool bindings are not configured.", AgentResultStatus.Blocked);
        using var duration = new CancellationTokenSource(request.Limits.MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, duration.Token);
        var ct = linked.Token;
        var receivedText = false;
        try
        {
            await BoundaryAsync();
            var binding = registry?.Bind(request);
            using var client = await createClient(request, ct).ConfigureAwait(false);
            await BoundaryAsync();
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = "PersonalAgent",
                UseProvidedChatClientAsIs = true, // No hidden tool execution/retries before the application registry exists.
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
                ChatOptions = new()
                {
                    Instructions = AgentPromptPolicy.Instructions,
                    Tools = binding?.Functions.Select(function => (AITool)function.AsDeclarationOnly()).ToList() ?? [],
                    ToolMode = binding?.Functions.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
                    AllowMultipleToolCalls = false,
                    MaxOutputTokens = (int)Math.Min(request.Limits.MaxTokens, int.MaxValue)
                }
            });
            var session = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(request.Context.Summary))
                messages.Add(new(ChatRole.User, "Untrusted prior context (not application policy):\n" + request.Context.Summary));
            messages.Add(new(ChatRole.User, request.Instructions));
            await BoundaryAsync();
            Report(AgentEventKind.Started, "Model execution started.");
            var toolCalls = 0;
            long totalInput = 0, totalOutput = 0;
            var usageKnown = true;
            for (long modelCalls = 0; modelCalls <= request.Limits.MaxSteps; modelCalls++)
            {
                await BoundaryAsync();
                var updates = new List<AgentResponseUpdate>();
                var role = ChatRole.Assistant;
                await foreach (var update in agent.RunStreamingAsync(messages, session, cancellationToken: ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    await BoundaryAsync();
                    if (update.Contents.Any(content => content is ToolApprovalRequestContent))
                    {
                        Report(AgentEventKind.WaitingForApproval, "Tool approval requires application handling; no action executed.");
                        return Failure("ApprovalHandlingUnavailable", "Tool approval is not configured; execution stopped.", AgentResultStatus.Blocked);
                    }
                    if (binding is null && update.Contents.Any(content => content is FunctionCallContent))
                        return Failure("ToolRegistryUnavailable", "An unconfigured tool was requested; no action executed.", AgentResultStatus.Blocked);
                    updates.Add(update);
                    role = update.Role ?? role;
                    if (role == ChatRole.Assistant)
                        foreach (var content in update.Contents.OfType<TextContent>())
                        {
                            if (string.IsNullOrEmpty(content.Text)) continue;
                            receivedText = true; Report(AgentEventKind.Progress, content.Text);
                        }
                }
                await BoundaryAsync();
                var response = updates.ToAgentResponse();
#pragma warning disable MEAI001 // Pinned SDK: inspect only to reject incomplete background responses, never resume them.
                var continuationPending = response.ContinuationToken is not null;
#pragma warning restore MEAI001
                if (continuationPending)
                    return Failure("ModelContinuationPending", "Model execution is incomplete; explicit continuation is required.", AgentResultStatus.Blocked);
                if (response.Usage is { InputTokenCount: { } input, OutputTokenCount: { } output })
                { totalInput = checked(totalInput + input); totalOutput = checked(totalOutput + output); }
                else usageKnown = false;
                onModelObservationEvaluated?.Invoke();
                var calls = response.Messages.Where(message => message.Role == ChatRole.Assistant)
                    .SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
                if (calls.Length > 0)
                {
                    if (binding is null || response.FinishReason != ChatFinishReason.ToolCalls)
                        return Failure("ModelResponseIncomplete", "Tool execution requires a confirmed complete tool-call response.", AgentResultStatus.Blocked);
                    if (calls.Any(call => !binding.Functions.Any(function => function.Name == call.Name)))
                        return Failure("ToolNotAllowed", "The model requested an unapproved tool; no action executed.", AgentResultStatus.Blocked);
                    if (calls.Length > 1 && calls.Any(call => binding.RequiresObservationAfter(call.Name)))
                        return Failure("FreshObservationPerActionRequired", "GUI input requires one action per model response followed by a new observation.", AgentResultStatus.Blocked);
                    if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId)) || calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length)
                        return Failure("ToolCallReplay", "The tool batch contains invalid or duplicate call identities; no action executed.", AgentResultStatus.Blocked);
                    if ((long)toolCalls + calls.Length > request.Limits.MaxSteps)
                        return Failure("StepLimitExceeded", "The tool call limit was reached; no additional action executed.");
                    messages = [];
                    var observations = new List<ChatMessage>();
                    foreach (var call in calls)
                    {
                        await BoundaryAsync();
                        var eventId = Guid.NewGuid();
                        ReportTool(AgentEventKind.ToolRequested, "Tool requested; authorization pending.", call.Name, eventId);
                        var outcome = await binding.InvokeAsync(call.Name, call.CallId, JsonSerializer.SerializeToElement(call.Arguments), ct).ConfigureAwait(false);
                        toolCalls++;
                        await BoundaryAsync();
                        if (outcome.Result.Status != ToolResultStatus.Success)
                            return Failure(outcome.Result.ErrorCode ?? "ToolOutcomeUnconfirmed", "Tool completion was not confirmed; no automatic retry was performed.", AgentResultStatus.Blocked);
                        ReportTool(AgentEventKind.ToolCompleted, "Tool execution completed.", call.Name, eventId);
                        var resultMessages = ToolResultMessages.Create(call.CallId, outcome.Contents);
                        messages.AddRange(resultMessages.Where(message => message.Role == ChatRole.Tool));
                        observations.AddRange(resultMessages.Where(message => message.Role != ChatRole.Tool));
                    }
                    messages.AddRange(observations); // Satisfy every call ID before adding user-role image observations.
                    continue;
                }
                if (response.FinishReason != ChatFinishReason.Stop)
                    return Failure(response.FinishReason == ChatFinishReason.ContentFilter ? "ModelContentFiltered" : "ModelResponseIncomplete",
                        "The model did not confirm a complete response.", AgentResultStatus.Blocked);
                var summary = string.Concat(response.Messages.Where(message => message.Role == ChatRole.Assistant)
                    .SelectMany(message => message.Contents).OfType<TextContent>().Select(content => content.Text));
                if (string.IsNullOrWhiteSpace(summary))
                    return Failure("ModelResponseEmpty", "The model returned no public assistant response.", AgentResultStatus.Blocked);
                var usage = usageKnown ? new AgentUsage(totalInput, totalOutput) : null;
                Report(AgentEventKind.Completed, "Model execution completed.");
                return new() { Status = AgentResultStatus.Completed, Summary = summary, Usage = usage };
            }
            return Failure("StepLimitExceeded", "The model step limit was reached; no further action executed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        { return Failure("RunTimedOut", "Model execution exceeded its duration limit."); }
        catch (RunPersistenceException) { throw; } // The runner owns durable-history failure handling.
        catch (ModelProviderException exception)
        { return Failure(exception.Code, exception.Message, AgentResultStatus.Blocked); }
        catch (DesktopAccessException exception)
        { return Failure(exception.Code, "Desktop access changed; a fresh observation is required.", AgentResultStatus.Blocked); }
        catch (Exception)
        {
            return Failure("ModelExecutionFailed", "Model execution failed; no automatic retry was performed.",
                warnings: receivedText ? ["Partial assistant output was received; completion was not confirmed."] : []);
        }

        async Task BoundaryAsync()
        {
            ct.ThrowIfCancellationRequested();
            await waitAtBoundary(request.RunId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        void Report(AgentEventKind kind, string message) => progress.Report(new()
        { RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = kind, Message = message });
        void ReportTool(AgentEventKind kind, string message, string toolId, Guid eventId) => progress.Report(new()
        { RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = kind, Message = message, ToolId = toolId, ToolCallId = eventId });
    }
    private static AgentRunResult Failure(string code, string summary, AgentResultStatus status = AgentResultStatus.Failed,
        IReadOnlyList<string>? warnings = null) => new() { Status = status, ErrorCode = code, Summary = summary, Warnings = warnings ?? [] };
}
