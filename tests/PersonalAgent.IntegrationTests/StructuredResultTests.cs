using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class StructuredResultTests
{
    private static readonly SourceLink Source = new("Observed synthetic ticket", new Uri("https://example.invalid/ticket/123"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedToolSourcesWarningsAndCumulativeUsageProduceStructuredFinalResult(bool configuredPricing)
    {
        var result = await Execute(new ModelPricing(2, 8, "USD"), configuredPricing: configuredPricing);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Equal(Source, Assert.Single(result.Sources));
        Assert.Equal("Synthetic source may be incomplete.", Assert.Single(result.Warnings));
        Assert.Equal(20, result.Usage!.InputTokens); Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(configuredPricing ? 0.00008m : null, result.Usage.Cost);
        Assert.Equal(configuredPricing ? "USD" : null, result.Usage.Currency);
        Assert.DoesNotContain(result.Sources, source => source.Url.AbsoluteUri.Contains("invented"));
    }

    [Fact]
    public async Task FailureAfterToolRetainsObservedSourcesWarningsAndActualOverBudgetUsage()
    {
        var result = await Execute(null, maxTokens: 20);
        Assert.Equal(AgentResultStatus.Failed, result.Status); Assert.Equal("TokenLimitExceeded", result.ErrorCode);
        Assert.Equal(Source, Assert.Single(result.Sources)); Assert.NotEmpty(result.Warnings);
        Assert.Equal(new AgentUsage(20, 5), result.Usage); // Includes the report that exceeded the budget, not just the earlier turn.
    }

    [Fact]
    public async Task UnrepresentableConfiguredCostStaysUnknownWithoutChangingConfirmedTaskOutcome()
    {
        var result = await Execute(new(decimal.MaxValue, decimal.MaxValue, "USD"));
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Null(result.Usage!.Cost); Assert.Null(result.Usage.Currency);
        Assert.Contains(result.Warnings, warning => warning.Contains("cost is unknown"));
    }

    [Fact]
    public async Task NonHttpSourceIsRejectedAndCannotBecomeAResultSourceOrExecuteFollowingModelStep()
    {
        var tool = new Tool(new("Invalid source", new Uri("file:///not-accessed")));
        var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask);
        using var client = new ScriptedChatClient((_, _) => Call());
        var result = await Runtime(client, registry).ExecuteAsync(Request(), new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.Equal("InvalidToolResult", result.ErrorCode);
        Assert.Empty(result.Sources); Assert.Equal(1, client.CallCount); Assert.Equal(1, tool.Calls);
    }

    private static async Task<AgentRunResult> Execute(ModelPricing? pricing, bool configuredPricing = true, long maxTokens = 20_000)
    {
        var tool = new Tool(Source); var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask);
        using var client = new ScriptedChatClient((_, _) =>
        { var response = Call(); response.Usage = new() { InputTokenCount = 12, OutputTokenCount = 3 }; return response; },
        (_, _) => new(new ChatMessage(ChatRole.Assistant, "Observed summary; model prose mentions https://invented.invalid but does not supply typed evidence."))
        { FinishReason = ChatFinishReason.Stop, Usage = new() { InputTokenCount = 8, OutputTokenCount = 2 } });
        return await Runtime(client, registry, configuredPricing ? pricing : null).ExecuteAsync(Request() with { Limits = new() { MaxTokens = maxTokens } }, new Progress(), CancellationToken.None);
    }
    private static MafAgentRuntime Runtime(IChatClient client, ToolRegistry registry, ModelPricing? pricing = null) =>
        new((_, _) => ValueTask.FromResult(client), (_, _) => Task.CompletedTask, registry, pricing: pricing);
    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "local", Instructions = "Read the synthetic fixture", AllowedToolIds = ["local_source"] };
    private static ChatResponse Call() => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent("source-call", "local_source", new Dictionary<string, object?>())]))
    { FinishReason = ChatFinishReason.ToolCalls };
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class Tool(SourceLink source) : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("local_source", "Synthetic in-memory source", JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            new(ToolSourceKind.Local, "result-fixture"), ToolRiskLevel.ReadOnly, IsReadOnly: true, IndependentOfDesktop: true);
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++; return Task.FromResult(new ToolResult
            {
                Status = ToolResultStatus.Success,
                Text = "Synthetic observed source",
                Sources = [source, source],
                Warnings = ["Synthetic source may be incomplete.", "Synthetic source may be incomplete."]
            });
        }
    }
}
