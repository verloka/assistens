using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolAccessPolicyTests
{
    [Theory]
    [InlineData(ToolRiskLevel.Unknown)]
    [InlineData(ToolRiskLevel.Navigation)]
    [InlineData(ToolRiskLevel.Write)]
    [InlineData(ToolRiskLevel.Destructive)]
    [InlineData(ToolRiskLevel.ExternalDisclosure)]
    public async Task ModelArgumentsAndPermissiveCallbackCannotElevateReadOnlyRun(ToolRiskLevel risk)
    {
        var request = Request(); var tool = new Tool(risk); var callbacks = 0;
        var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask,
            (_, _, _) => { callbacks++; return ValueTask.FromResult(true); },
            new(request.RunId, request.ConversationId, [ToolRiskLevel.ReadOnly]));
        using var client = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("one", "fixture", new Dictionary<string, object?> { ["risk"] = "ReadOnly", ["approved"] = true })]))
        { FinishReason = ChatFinishReason.ToolCalls });
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, registry);
        var result = await runtime.ExecuteAsync(request, new Progress<AgentEvent>(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.Equal("ToolPermissionDenied", result.ErrorCode);
        Assert.Equal(0, tool.Calls); Assert.Equal(0, callbacks); Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task RiskContextRestrictsButCannotReplaceAuthorizationOrBeReusedForAnotherRun()
    {
        var request = Request(); var tool = new Tool(ToolRiskLevel.ReadOnly);
        var access = new ToolAccessContext(request.RunId, request.ConversationId, [ToolRiskLevel.ReadOnly]);
        var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask, accessContext: access);
        var args = JsonSerializer.SerializeToElement(new { });
        Assert.Equal(ToolResultStatus.Success, (await registry.Bind(request).InvokeAsync("fixture", "one", args, default)).Result.Status);
        Assert.Equal("ToolPermissionDenied", (await registry.Bind(request with { RunId = Guid.NewGuid() }).InvokeAsync("fixture", "two", args, default)).Result.ErrorCode);
        tool = new(ToolRiskLevel.ReadOnly, ToolSourceKind.Mcp);
        registry = new([tool], (_, _, _) => Task.CompletedTask, accessContext: access);
        Assert.Equal("ToolPermissionDenied", (await registry.Bind(request).InvokeAsync("fixture", "three", args, default)).Result.ErrorCode);
        Assert.Equal(0, tool.Calls); // Category access alone cannot approve a remote connection/tool.
    }

    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "fixture",
        Instructions = "Synthetic policy regression", AllowedToolIds = ["fixture"] };
    private sealed class Tool(ToolRiskLevel risk, ToolSourceKind source = ToolSourceKind.Local) : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("fixture", "Trusted classified fixture",
            JsonSerializer.SerializeToElement(new { type = "object" }), new(source, "fixture"), risk, IsReadOnly: risk == ToolRiskLevel.ReadOnly);
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
        { Calls++; return Task.FromResult(new ToolResult { Status = ToolResultStatus.Success }); }
    }
}
