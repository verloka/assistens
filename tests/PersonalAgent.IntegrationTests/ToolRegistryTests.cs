using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolRegistryTests
{
    private const string Schema = """{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":8},"amount":{"type":"integer","minimum":1,"maximum":5}},"required":["text"],"additionalProperties":false}""";
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static AgentRequest Request(params string[] allowed) => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "test", Instructions = "local", AllowedToolIds = allowed };
    private static ToolRegistry Registry(IAgentTool tool) => new([tool], (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

    [Fact]
    public async Task RealMafCallsOnlyBoundFakeToolByStableSchemaAndReceivesUnifiedTextAndImageWithSource()
    {
        var image = new byte[] { 137, 80, 78, 71 };
        var tool = new Tool((args, context, ct) => Task.FromResult(new ToolResult
        { Status = ToolResultStatus.Success, Text = "echo:" + args.Value.GetProperty("text").GetString(), StructuredData = Json("{\"count\":1}"), Images = [new(image, "image/png")] }));
        var registry = new ToolRegistry([tool, new Tool(id: "not_allowed")], (_, _, _) => Task.CompletedTask);
        var request = Request("local_echo"); var bound = registry.Bind(request);
        using var client = new ScriptedChatClient((_, options) =>
        {
            var declaration = Assert.Single(options!.Tools!);
            Assert.Equal("local_echo", declaration.Name); Assert.Equal(Schema, ((AIFunction)declaration).JsonSchema.GetRawText());
            return new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "local_echo", new Dictionary<string, object?> { ["text"] = "hello" })]))
            { FinishReason = ChatFinishReason.ToolCalls };
        }, (messages, _) =>
        {
            var result = Assert.Single(messages.Where(message => message.Role == ChatRole.Tool).SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.Equal("call-1", result.CallId); Assert.Null(result.Exception);
            var content = Assert.IsAssignableFrom<IEnumerable<AIContent>>(result.Result).ToArray();
            using var metadata = JsonDocument.Parse(Assert.IsType<TextContent>(content[0]).Text);
            Assert.Equal("echo:hello", metadata.RootElement.GetProperty("Text").GetString());
            Assert.Equal("local-fixture", metadata.RootElement.GetProperty("source").GetProperty("Id").GetString());
            Assert.Equal("Success", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal(image, Assert.IsType<DataContent>(content[1]).Data.ToArray());
            return new(new ChatMessage(ChatRole.Assistant, "Local unified tool result received")) { FinishReason = ChatFinishReason.Stop };
        });
        var agent = new ChatClientAgent(client, name: "RegistryDiagnostic", tools: bound.Functions.Cast<AITool>().ToList());
        var session = await agent.CreateSessionAsync();
        Assert.Equal("Local unified tool result received", (await agent.RunAsync(request.Instructions, session)).Text);
        Assert.Equal(1, tool.Calls); Assert.Equal(2, client.CallCount);
        Assert.Equal(request.RunId, tool.Context!.RunId); Assert.Equal(request.ConversationId, tool.Context.ConversationId);
        Assert.Equal("call-1", tool.Context.ToolCallId);
    }

    [Theory]
    [InlineData("{}")][InlineData("{\"text\":3}")][InlineData("{\"text\":\"toolongvalue\"}")]
    [InlineData("{\"text\":\"ok\",\"extra\":true}")][InlineData("{\"text\":\"ok\",\"amount\":1.5}")][InlineData("{\"text\":\"ok\",\"amount\":6}")]
    public async Task SchemaRejectsMalformedArgumentsBeforeToolOrPolicy(string arguments)
    {
        var tool = new Tool(); var policy = 0;
        var registry = new ToolRegistry([tool], (_, _, _) => { policy++; return Task.CompletedTask; });
        var result = await registry.Bind(Request("local_echo")).InvokeAsync("local_echo", "bad", Json(arguments), CancellationToken.None);
        Assert.Equal("InvalidToolArguments", result.Result.ErrorCode); Assert.Equal(0, tool.Calls); Assert.Equal(0, policy);
    }

    [Fact]
    public async Task UnknownAllowedAndUnboundToolsFailClosedAndCompletedCallIdCannotReplay()
    {
        var tool = new Tool(); var registry = Registry(tool);
        Assert.Equal("ToolUnavailable", Assert.Throws<ModelProviderException>(() => registry.Bind(Request("missing"))).Code);
        var bound = registry.Bind(Request("local_echo"));
        await Assert.ThrowsAsync<ModelProviderException>(() => bound.InvokeAsync("missing", "x", Json("{}"), CancellationToken.None));
        Assert.Equal(ToolResultStatus.Success, (await bound.InvokeAsync("local_echo", "one", Json("{\"text\":\"ok\"}"), CancellationToken.None)).Result.Status);
        Assert.Equal("ToolCallReplay", (await bound.InvokeAsync("local_echo", "one", Json("{\"text\":\"changed\"}"), CancellationToken.None)).Result.ErrorCode);
        Assert.Equal(1, tool.Calls);
    }

    [Theory]
    [InlineData(ToolSourceKind.Mcp, ToolRiskLevel.ReadOnly, true)]
    [InlineData(ToolSourceKind.Local, ToolRiskLevel.Unknown, true)]
    [InlineData(ToolSourceKind.Local, ToolRiskLevel.Write, false)]
    public async Task UnapprovedSourceUnknownRiskAndSensitiveToolsAreDeniedByDefault(ToolSourceKind source, ToolRiskLevel risk, bool readOnly)
    {
        var tool = new Tool(); tool.Descriptor = tool.Descriptor with { Source = new(source, "configured-source"), RiskLevel = risk, IsReadOnly = readOnly };
        var result = await Registry(tool).Bind(Request("local_echo")).InvokeAsync("local_echo", "denied", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        Assert.Equal("ToolPermissionDenied", result.Result.ErrorCode); Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task PolicyIsRecheckedAfterPauseBoundaryAndCancellationWhileWaitingCannotInvoke()
    {
        var tool = new Tool(); tool.Descriptor = tool.Descriptor with { RequiresDesktop = true, IsReadOnly = false, RiskLevel = ToolRiskLevel.Write };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = true;
        var registry = new ToolRegistry([tool], async (_, access, ct) =>
        { Assert.True(access.RequiresDesktop); Assert.False(access.IsReadOnly); entered.TrySetResult(); await resume.Task.WaitAsync(ct); },
            (_, _, _) => ValueTask.FromResult(allow));
        var bound = registry.Bind(Request("local_echo"));
        var invocation = bound.InvokeAsync("local_echo", "paused", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, tool.Calls); allow = false; resume.SetResult();
        Assert.Equal("ToolPermissionDenied", (await invocation).Result.ErrorCode); Assert.Equal(0, tool.Calls);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bound.InvokeAsync("local_echo", "cancel", Json("{\"text\":\"ok\"}"), stop.Token));
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task ToolExceptionIsSanitizedAndNeverRetried()
    {
        var tool = new Tool((_, _, _) => throw new InvalidOperationException("private-secret-exception"));
        var result = await Registry(tool).Bind(Request("local_echo")).InvokeAsync("local_echo", "failure", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        Assert.Equal("ToolExecutionFailed", result.Result.ErrorCode); Assert.Equal(1, tool.Calls);
        Assert.DoesNotContain("private-secret", Assert.IsType<TextContent>(result.Contents[0]).Text);
    }

    [Fact]
    public async Task WaitingInvocationIsCancelledAndConcurrentCallsExecuteSequentially()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new Tool(async (_, _, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new() { Status = ToolResultStatus.Success }; });
        var bound = Registry(tool).Bind(Request("local_echo"));
        var first = bound.InvokeAsync("local_echo", "first", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var cancelled = bound.InvokeAsync("local_echo", "cancelled", Json("{\"text\":\"ok\"}"), stop.Token);
        var second = bound.InvokeAsync("local_echo", "second", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        Assert.False(second.IsCompleted); Assert.Equal(1, tool.Calls); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, tool.Calls);
    }

    [Theory]
    [InlineData(ToolResultStatus.Failed)][InlineData(ToolResultStatus.Blocked)][InlineData(ToolResultStatus.OutcomeUnknown)]
    public async Task NonSuccessResultRetainsStatusAndIsNotRetried(ToolResultStatus status)
    {
        var tool = new Tool((_, _, _) => Task.FromResult(new ToolResult { Status = status, ErrorCode = "controlled" }));
        var result = await Registry(tool).Bind(Request("local_echo")).InvokeAsync("local_echo", "status", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        Assert.Equal(status, result.Result.Status); Assert.Equal(1, tool.Calls);
        Assert.Contains(status.ToString(), Assert.IsType<TextContent>(result.Contents[0]).Text);
    }

    [Fact]
    public void DuplicateIdsExternalReferencesAndInvalidSchemasAreRejectedAndMetadataIsSnapshotted()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new Tool(), new Tool()], (_, _, _) => Task.CompletedTask));
        var tool = new Tool(); var original = tool.Descriptor;
        tool.Descriptor = original with { JsonSchema = Json("{\"type\":\"object\",\"$ref\":\"https://external.invalid/schema\"}") };
        Assert.Throws<ArgumentException>(() => Registry(tool));
        tool.Descriptor = original with { JsonSchema = Json("{\"type\":\"array\"}") };
        Assert.Throws<ArgumentException>(() => Registry(tool));
        tool.Descriptor = original; var registry = Registry(tool);
        tool.Descriptor = original with { Id = "changed", Source = new(ToolSourceKind.Mcp, "changed") };
        Assert.Equal("local_echo", Assert.Single(registry.Bind(Request("local_echo")).Functions).Name);
    }

    [Fact]
    public async Task AuthorizedMcpReadDoesNotTriggerUnrelatedObservationTool()
    {
        var mcp = new Tool(); mcp.Descriptor = mcp.Descriptor with { Source = new(ToolSourceKind.Mcp, "approved-fixture") };
        var observe = new Tool(id: "observe");
        var registry = new ToolRegistry([mcp, observe], (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
        var outcome = await registry.Bind(Request("local_echo", "observe")).InvokeAsync("local_echo", "mcp-read", Json("{\"text\":\"ok\"}"), CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, outcome.Result.Status); Assert.Equal(1, mcp.Calls); Assert.Equal(0, observe.Calls);
        Assert.Empty(outcome.Result.Images);
    }

    private sealed class Tool(Func<ToolArguments, ToolExecutionContext, CancellationToken, Task<ToolResult>>? execute = null, string id = "local_echo") : IAgentTool
    {
        public ToolDescriptor Descriptor { get; set; } = new(id, "In-memory echo fixture", Json(Schema), new(ToolSourceKind.Local, "local-fixture"), ToolRiskLevel.ReadOnly, IsReadOnly: true, IndependentOfDesktop: true);
        public int Calls; public ToolExecutionContext? Context;
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        { Calls++; Context = context; return execute?.Invoke(arguments, context, cancellationToken) ?? Task.FromResult(new ToolResult { Status = ToolResultStatus.Success, Text = "local result" }); }
    }
}
