using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PersonalAgent.IntegrationTests.Fakes;

namespace PersonalAgent.IntegrationTests;

public sealed class MafToolApprovalSpikeTests
{
    [Fact]
    public async Task LocalFunctionIsInvokedAndItsResultReturnsToTheModel()
    {
        var invocations = new List<string>();
        var tool = CreateTool(invocations);
        using var client = new ScriptedChatClient(
            (_, options) =>
            {
                Assert.Contains(options!.Tools!, candidate => candidate.Name == tool.Name);
                return FunctionCall("local-1", "hello");
            },
            (messages, _) =>
            {
                var result = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
                Assert.Equal("local-1", result.CallId);
                Assert.Null(result.Exception);
                Assert.Contains("echo:hello", JsonSerializer.Serialize(result.Result));
                return Final("Local tool completed");
            });
        var agent = new ChatClientAgent(client, name: "LocalDiagnostic", tools: [tool]);
        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync("Echo hello with the local tool.", session);

        Assert.Equal("Local tool completed", response.Text);
        Assert.Equal(["hello"], invocations);
        Assert.Equal(2, client.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalSuspendsTheToolAndResumesTheSameSessionWithTheDecision(bool approved)
    {
        var invocations = new List<string>();
        using var client = new ScriptedChatClient(
            (_, _) => FunctionCall("approval-1", "original"),
            (messages, _) =>
            {
                var result = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
                Assert.Equal("approval-1", result.CallId);
                Assert.Equal(approved ? 1 : 0, invocations.Count);
                if (approved)
                {
                    Assert.Null(result.Exception);
                    Assert.Contains("echo:original", JsonSerializer.Serialize(result.Result));
                }
                return Final(approved ? "Approved" : "Denied");
            });
        var agent = new ChatClientAgent(client, name: "ApprovalDiagnostic",
            tools: [new ApprovalRequiredAIFunction(CreateTool(invocations))]);
        var session = await agent.CreateSessionAsync();

        var pending = await agent.RunAsync("Request the protected local tool.", session);
        var request = Assert.Single(pending.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal("approval-1", request.ToolCall.CallId);
        Assert.Empty(invocations);
        Assert.Equal(1, client.CallCount);

        var response = await agent.RunAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]), session);

        Assert.Equal(approved ? "Approved" : "Denied", response.Text);
        Assert.Equal(approved ? ["original"] : [], invocations);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ApprovalCannotReplaceTheArgumentsOfTheFrameworkIssuedRequest()
    {
        var invocations = new List<string>();
        using var client = new ScriptedChatClient(
            (_, _) => FunctionCall("bound-1", "original"),
            (_, _) => Final("Continued"));
        var agent = new ChatClientAgent(client, name: "BoundApprovalDiagnostic",
            tools: [new ApprovalRequiredAIFunction(CreateTool(invocations))]);
        var session = await agent.CreateSessionAsync();
        var pending = await agent.RunAsync("Request protected local echo.", session);
        var request = Assert.Single(pending.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());

        var forged = new ToolApprovalResponseContent(request.RequestId, true,
            new FunctionCallContent("bound-1", "local_echo", new Dictionary<string, object?> { ["text"] = "changed" }));
        await agent.RunAsync(new ChatMessage(ChatRole.User, [forged]), session);

        Assert.Equal(["original"], invocations);
        Assert.DoesNotContain("changed", invocations);
    }

    [Fact]
    public async Task PreCancelledRunCannotInvokeTheModelOrLocalTool()
    {
        var invocations = new List<string>();
        using var client = new ScriptedChatClient((_, _) => FunctionCall("cancelled-1", "hello"));
        var agent = new ChatClientAgent(client, name: "CancellationDiagnostic", tools: [CreateTool(invocations)]);
        var session = await agent.CreateSessionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunAsync("Do not execute.", session, cancellationToken: cancellation.Token));

        Assert.Empty(invocations);
        Assert.Equal(0, client.CallCount);
    }

    private static AIFunction CreateTool(List<string> invocations) => AIFunctionFactory.Create((string text) =>
    {
        invocations.Add(text);
        return "echo:" + text;
    }, name: "local_echo", description: "Echoes test data in memory only.");

    private static ChatResponse FunctionCall(string callId, string text) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, "local_echo", new Dictionary<string, object?> { ["text"] = text })]))
        { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponse Final(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };
}
