using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;

namespace PersonalAgent.Agent;

// The trusted factory transfers ownership of a new client per execution. SDK state never enters Core.
// Production providers/approved tool bindings are later slices; there is no automatic invocation loop here.
public sealed class MafAgentRuntime(
    Func<AgentRequest, CancellationToken, ValueTask<IChatClient>> createClient,
    Func<Guid, CancellationToken, Task> waitAtBoundary) : IAgentRuntime
{
    public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Limits.Validate();
        if (request.AllowedToolIds.Count != 0)
            return Failure("ToolRegistryUnavailable", "Approved tool bindings are not configured.", AgentResultStatus.Blocked);
        using var duration = new CancellationTokenSource(request.Limits.MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, duration.Token);
        var ct = linked.Token;
        var updates = new List<AgentResponseUpdate>();
        var receivedText = false;
        try
        {
            await BoundaryAsync();
            using var client = await createClient(request, ct).ConfigureAwait(false);
            await BoundaryAsync();
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = "PersonalAgent",
                UseProvidedChatClientAsIs = true, // No hidden tool execution/retries before the application registry exists.
                ChatOptions = new() { Tools = [], ToolMode = ChatToolMode.None, MaxOutputTokens = (int)Math.Min(request.Limits.MaxTokens, int.MaxValue) }
            });
            var session = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(request.Context.Summary))
                messages.Add(new(ChatRole.User, "Untrusted prior context (not application policy):\n" + request.Context.Summary));
            messages.Add(new(ChatRole.User, request.Instructions));
            await BoundaryAsync();
            Report(AgentEventKind.Started, "Model execution started.");
            var role = ChatRole.Assistant;
            await foreach (var update in agent.RunStreamingAsync(messages, session, cancellationToken: ct).WithCancellation(ct).ConfigureAwait(false))
            {
                await BoundaryAsync();
                if (update.Contents.Any(content => content is ToolApprovalRequestContent))
                {
                    Report(AgentEventKind.WaitingForApproval, "Tool approval requires application handling; no action executed.");
                    return Failure("ApprovalHandlingUnavailable", "Tool approval is not configured; execution stopped.", AgentResultStatus.Blocked);
                }
                if (update.Contents.Any(content => content is FunctionCallContent))
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
            if (response.FinishReason != ChatFinishReason.Stop)
                return Failure(response.FinishReason == ChatFinishReason.ContentFilter ? "ModelContentFiltered" : "ModelResponseIncomplete",
                    "The model did not confirm a complete response.", AgentResultStatus.Blocked);
            var summary = string.Concat(response.Messages.Where(message => message.Role == ChatRole.Assistant)
                .SelectMany(message => message.Contents).OfType<TextContent>().Select(content => content.Text));
            if (string.IsNullOrWhiteSpace(summary))
                return Failure("ModelResponseEmpty", "The model returned no public assistant response.", AgentResultStatus.Blocked);
            var usage = response.Usage is { InputTokenCount: { } input, OutputTokenCount: { } output } ? new AgentUsage(input, output) : null;
            Report(AgentEventKind.Completed, "Model execution completed.");
            return new() { Status = AgentResultStatus.Completed, Summary = summary, Usage = usage };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        { return Failure("RunTimedOut", "Model execution exceeded its duration limit."); }
        catch (RunPersistenceException) { throw; } // The runner owns durable-history failure handling.
        catch (ModelProviderException exception)
        { return Failure(exception.Code, exception.Message, AgentResultStatus.Blocked); }
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
    }
    private static AgentRunResult Failure(string code, string summary, AgentResultStatus status = AgentResultStatus.Failed,
        IReadOnlyList<string>? warnings = null) => new() { Status = status, ErrorCode = code, Summary = summary, Warnings = warnings ?? [] };
}
