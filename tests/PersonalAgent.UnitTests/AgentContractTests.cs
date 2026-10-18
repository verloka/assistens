using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class AgentContractTests
{
    [Fact]
    public async Task AgentContractCarriesNeutralContextProgressResultAndUsage()
    {
        var request = Request() with
        {
            AllowedToolIds = ["test.read"], Context = new AgentContext("Prior result", [new SourceLink("Source", new Uri("https://example.com"))])
        };
        request = JsonSerializer.Deserialize<AgentRequest>(JsonSerializer.Serialize(request))!;
        var events = new List<AgentEvent>();
        IAgentRuntime runtime = new ContractProbe();
        var result = await runtime.ExecuteAsync(request, new InlineProgress(events.Add), CancellationToken.None);
        var restored = JsonSerializer.Deserialize<AgentRunResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(AgentResultStatus.Completed, restored.Status);
        Assert.Equal("Prior result", restored.Summary);
        Assert.Equal(request.Context.Sources, restored.Sources);
        Assert.Equal(new AgentUsage(10, 5), restored.Usage);
        Assert.Equal(request.RunId, Assert.Single(events).RunId);
        Assert.Equal("test.read", Assert.Single(events).ToolId);
    }

    [Fact]
    public async Task AgentContractAcceptsCancellationWithoutAnotherProgressEvent()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        IAgentRuntime runtime = new ContractProbe();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(Request(),
            new InlineProgress(_ => Assert.Fail("Cancelled execution must not report work.")), cancelled.Token));
    }

    [Fact]
    public void AgentLimitsRejectInvalidBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLimits { MaxSteps = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLimits { MaxDuration = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLimits { MaxTokens = -1 }.Validate());
        new RunLimits().Validate();
    }

    private sealed class ContractProbe : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Limits.Validate();
            progress.Report(new AgentEvent
            {
                RunId = request.RunId, TimestampUtc = DateTimeOffset.UtcNow, Kind = AgentEventKind.ToolCompleted,
                Message = "Read test value", ToolId = request.AllowedToolIds.Single()
            });
            return Task.FromResult(new AgentRunResult
            {
                Status = AgentResultStatus.Completed, Summary = request.Context.Summary!,
                Sources = request.Context.Sources!, Usage = new AgentUsage(10, 5)
            });
        }
    }

    private sealed class InlineProgress(Action<AgentEvent> accept) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value) => accept(value);
    }

    private static AgentRequest Request() => new()
    {
        RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Read", ModelProfileId = "fake"
    };
}
