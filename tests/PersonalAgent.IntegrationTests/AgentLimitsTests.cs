using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class AgentLimitsTests
{
    [Fact]
    public async Task ToolLimitStopsBeforeAnotherGuiAction()
    {
        await using var run = new ComputerRun(new() { MaxSteps = 1 });
        using var client = new ScriptedChatClient((_, _) => Call("capture", "computer_screenshot", new { }),
            (messages, _) => Click("too-many", Observation(messages)));
        var result = await run.Runtime(client).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("StepLimitExceeded", result.ErrorCode); Assert.Equal(2, client.CallCount);
        Assert.Equal(1, run.Native.Captures); Assert.Empty(run.Native.Inputs);
    }

    [Fact]
    public async Task NewCallAndObservationIdsCannotHideConsecutiveRepeatedClicks()
    {
        await using var run = new ComputerRun(new() { MaxRepeatedActions = 2 });
        using var client = new ScriptedChatClient((_, _) => Call("capture", "computer_screenshot", new { }),
            (messages, _) => Click("click-1", Observation(messages)),
            (messages, _) => Click("click-2", Observation(messages)),
            (messages, _) => Click("click-3", Observation(messages)));
        var result = await run.Runtime(client).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("RepeatedActionLimitExceeded", result.ErrorCode); Assert.Equal(4, client.CallCount);
        Assert.Equal(2, run.Native.Inputs.Count); Assert.Equal(3, run.Native.Captures);
    }

    [Fact]
    public async Task ExhaustedTokenBudgetPreventsEvenTheFirstScreenshot()
    {
        await using var run = new ComputerRun(new() { MaxTokens = 5 });
        using var client = new ScriptedChatClient((_, _) => WithUsage(Call("capture", "computer_screenshot", new { }), 2, 3));
        var result = await run.Runtime(client).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("TokenLimitExceeded", result.ErrorCode); Assert.Equal(1, client.CallCount);
        Assert.Equal(0, run.Native.Captures); Assert.Empty(run.Native.Inputs);
    }

    [Fact]
    public async Task RemainingOutputCapAndProfileSettingsPreservePolicyToolsAndStopCumulativeOverspend()
    {
        await using var run = new ComputerRun(new() { MaxTokens = 6 });
        using var client = new ScriptedChatClient((_, options) =>
        {
            Assert.Equal(5, options!.MaxOutputTokens); Assert.Equal(0.25f, options.Temperature);
            Assert.Equal(AgentPromptPolicy.Instructions, options.Instructions); Assert.Equal(5, options.Tools!.Count);
            return WithUsage(Call("capture", "computer_screenshot", new { }), 1, 1);
        }, (messages, options) =>
        {
            Assert.Equal(4, options!.MaxOutputTokens); Assert.Equal(0.25f, options.Temperature);
            Assert.Equal(AgentPromptPolicy.Instructions, options.Instructions); Assert.Equal(5, options.Tools!.Count);
            return WithUsage(Click("blocked", Observation(messages)), 3, 2);
        });
        var result = await run.Runtime(client, new(0.25f, 5)).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("TokenLimitExceeded", result.ErrorCode); Assert.Equal(2, client.CallCount);
        Assert.Equal(1, run.Native.Captures); Assert.Empty(run.Native.Inputs);
    }

    [Fact]
    public async Task UnknownUsageCannotAuthorizeToolEffectsButFinalTextRetainsExplicitUnknownUsage()
    {
        await using var run = new ComputerRun(new());
        using var missing = new ScriptedChatClient((_, _) =>
        { var response = Call("capture", "computer_screenshot", new { }); response.Usage = new(); return response; });
        var stopped = await run.Runtime(missing).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("TokenUsageUnavailable", stopped.ErrorCode); Assert.Equal(0, run.Native.Captures);
        using var final = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant, "Confirmed text-only result")) { FinishReason = ChatFinishReason.Stop, Usage = new() });
        var result = await run.Runtime(final).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Null(result.Usage); Assert.NotEmpty(result.Warnings);
        Assert.Empty(run.Native.Inputs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task InvalidOrOverflowingUsageStopsBeforeGui(long input, long output)
    {
        await using var run = new ComputerRun(new());
        using var client = new ScriptedChatClient((_, _) => WithUsage(Call("capture", "computer_screenshot", new { }), input, output));
        var result = await run.Runtime(client).ExecuteAsync(run.Request, new Progress(), CancellationToken.None);
        Assert.Equal("TokenLimitExceeded", result.ErrorCode); Assert.Equal(0, run.Native.Captures); Assert.Empty(run.Native.Inputs);
        Assert.Equal(1, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurationOrCallerCancellationAtClassifiedBoundaryPreventsCaptureAndFollowingRequests(bool callerCancel)
    {
        await using var run = new ComputerRun(new() { MaxDuration = TimeSpan.FromSeconds(callerCancel ? 10 : 1) });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry(run.Session.Tools, async (_, _, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }, (_, _, _) => ValueTask.FromResult(true));
        using var client = new ScriptedChatClient((_, _) => Call("capture", "computer_screenshot", new { }));
        using var stop = new CancellationTokenSource();
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, registry);
        var executing = runtime.ExecuteAsync(run.Request, new Progress(), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancel)
        { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(5))); }
        else Assert.Equal("RunTimedOut", (await executing.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
        Assert.Equal(0, run.Native.Captures); Assert.Empty(run.Native.Inputs); Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task LegacyLimitsHaveSafeDefaultAndInvalidRepeatLimitCannotCreateClient()
    {
        var legacy = JsonSerializer.Deserialize<RunLimits>("{\"MaxSteps\":5,\"MaxDuration\":\"00:00:10\",\"MaxTokens\":100}")!;
        legacy.Validate(); Assert.Equal(3, legacy.MaxRepeatedActions);
        var factories = 0;
        var runtime = new MafAgentRuntime((_, _) => { factories++; throw new InvalidOperationException(); }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.ExecuteAsync(new()
        { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "test", Instructions = "local", Limits = legacy with { MaxRepeatedActions = 0 } }, new Progress(), CancellationToken.None));
        Assert.Equal(0, factories);
    }

    private static string Observation(IReadOnlyList<ChatMessage> messages)
    {
        using var metadata = JsonDocument.Parse((string)messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last().Result!);
        var data = metadata.RootElement.GetProperty("data");
        return (data.TryGetProperty("observation", out var observation) ? observation : data).GetProperty("observationId").GetString()!;
    }
    private static ChatResponse Click(string id, string observation) => Call(id, "computer_click", new { observationId = observation, imageX = 0, imageY = 0, button = "left" });
    private static ChatResponse Call(string id, string tool, object arguments) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(id, tool, JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()))]))
    { FinishReason = ChatFinishReason.ToolCalls };
    private static ChatResponse WithUsage(ChatResponse response, long input, long output)
    { response.Usage = new() { InputTokenCount = input, OutputTokenCount = output }; return response; }
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class ComputerRun : IAsyncDisposable
    {
        public readonly ComputerToolTests.Native Native = new();
        public ComputerToolSession Session { get; }
        public AgentRequest Request { get; }
        private readonly ToolRegistry registry;
        public ComputerRun(RunLimits limits)
        {
            var access = new DesktopAccessCoordinator(_ => true); var runId = Guid.NewGuid();
            Session = new(runId, access, lease => new WindowsComputerController(access, lease, Native, Native, Native.GetMonitors));
            registry = new(Session.Tools, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, (_, _, _) => ValueTask.FromResult(true));
            Request = new()
            {
                RunId = runId,
                ConversationId = Guid.NewGuid(),
                ModelProfileId = "test",
                Instructions = "Synthetic local target",
                Limits = limits,
                AllowedToolIds = ["computer_screenshot", "computer_click", "computer_scroll", "computer_type", "computer_press_key"]
            };
        }
        public MafAgentRuntime Runtime(IChatClient client, ModelGenerationSettings? settings = null) =>
            new((_, _) => ValueTask.FromResult(client), (_, _) => Task.CompletedTask, registry, Session.ConfirmModelObservation, settings);
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }
}
