using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using PersonalAgent.IntegrationTests.Fakes;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerToolTests
{
    [Fact]
    public async Task ApprovalYieldReleasesLeaseAndInvalidatesCoordinatesUntilFreshCaptureAndEvaluation()
    {
        var access = new DesktopAccessCoordinator(_ => true); var native = new Native(); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => new WindowsComputerController(access, lease, native, native, native.GetMonitors));
        var binding = Registry(session).Bind(Request(runId));
        var captured = await binding.InvokeAsync("computer_screenshot", "before", Json(new { }), default);
        var generation = access.Snapshot.Generation;
        var old = captured.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        session.ConfirmModelObservation(); await session.YieldDesktopAsync();
        Assert.Null(access.Snapshot.OwnerRunId); Assert.Equal(1, native.Captures); Assert.Empty(native.Inputs);
        var stale = await binding.InvokeAsync("computer_click", "stale", Arguments("computer_click", old), default);
        Assert.Equal("StaleDesktopContext", stale.Result.ErrorCode); Assert.Null(access.Snapshot.OwnerRunId); Assert.Empty(native.Inputs);
        var fresh = await binding.InvokeAsync("computer_screenshot", "after", Json(new { }), default);
        var current = fresh.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        Assert.NotEqual(old, current); Assert.True(access.Snapshot.Generation > generation);
        var unevaluated = await binding.InvokeAsync("computer_click", "unevaluated", Arguments("computer_click", current), default);
        Assert.Equal("ScreenReevaluationRequired", unevaluated.Result.ErrorCode); Assert.Empty(native.Inputs);
        session.ConfirmModelObservation();
        var result = await binding.InvokeAsync("computer_click", "fresh-click", Arguments("computer_click", current), default);
        Assert.Equal(ToolResultStatus.Success, result.Result.Status); Assert.Single(native.Inputs);
    }

    private static readonly string[] Ids = ["computer_screenshot", "computer_click", "computer_scroll", "computer_type", "computer_press_key"];
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static AgentRequest Request(Guid runId) => new() { RunId = runId, ConversationId = Guid.NewGuid(), Instructions = "Own synthetic target", ModelProfileId = "local", AllowedToolIds = Ids };
    private static ToolRegistry Registry(ComputerToolSession session, bool authorize = true) => new(session.Tools, (_, _, ct) =>
    { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, (_, _, _) => ValueTask.FromResult(authorize));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeAndRealControllerUseSameFiveToolContractThroughMaf(bool realController)
    {
        var access = new DesktopAccessCoordinator(_ => true); var native = new Native(); var fake = new Controller();
        var runId = Guid.NewGuid(); var creates = 0;
        await using var session = new ComputerToolSession(runId, access, lease =>
        { creates++; fake.Generation = lease.Generation; return realController ? new WindowsComputerController(access, lease, native, native, native.GetMonitors, _ => true) : fake; });
        string? observation = null;
        using var client = new ScriptedChatClient((messages, options) =>
        {
            Assert.Equal(Ids.Order(), options!.Tools!.Select(tool => tool.Name).Order());
            Assert.All(options.Tools!, tool => Assert.False(tool is AIFunction)); // SDK cannot bypass registry dispatch.
            return Call("capture", "computer_screenshot", new { });
        }, (messages, _) =>
        {
            Assert.Single(messages, message => message.Role == ChatRole.User && message.Text == "Own synthetic target");
            Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
            var screenshot = LastObservation(messages);
            Assert.IsType<DataContent>(screenshot[1]);
            observation = ObservationId(messages);
            return Call("click", "computer_click", new { observationId = observation, imageX = 0, imageY = 0, button = "right" });
        }, (messages, _) => { var fresh = ObservationId(messages); Assert.NotEqual(observation, fresh); observation = fresh; return Call("scroll", "computer_scroll", new { observationId = observation, imageX = 0, imageY = 0, direction = "left", amount = 2 }); },
        (messages, _) => { var fresh = ObservationId(messages); Assert.NotEqual(observation, fresh); observation = fresh; return Call("type", "computer_type", new { observationId = observation, text = "Тест 🙂 你好" }); },
        (messages, _) => { var fresh = ObservationId(messages); Assert.NotEqual(observation, fresh); observation = fresh; return Call("key", "computer_press_key", new { observationId = observation, chord = "Ctrl+A" }); },
        (messages, _) =>
        {
            Assert.Equal(5, messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            return new(new ChatMessage(ChatRole.Assistant, "Five tool contract completed")) { FinishReason = ChatFinishReason.Stop };
        });
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask,
            Registry(session), session.ConfirmModelObservation);
        var result = await runtime.ExecuteAsync(Request(runId), new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Equal("Five tool contract completed", result.Summary);
        Assert.Equal(6, client.CallCount); Assert.Equal(1, creates);
        if (realController) { Assert.Equal(5, native.Captures); Assert.True(native.Inputs.Count >= 4); }
        else Assert.Equal(new[] { "capture", "confirm", "click:Right", "capture", "confirm", "scroll:Left:2", "capture", "confirm", "type:Тест 🙂 你好", "capture", "confirm", "key:Ctrl+A", "capture", "confirm" }, fake.Calls);
        await session.DisposeAsync(); Assert.Null(access.Snapshot.OwnerRunId);
    }

    [Theory]
    [InlineData("computer_click")]
    [InlineData("computer_scroll")]
    [InlineData("computer_type")]
    [InlineData("computer_press_key")]
    public async Task InputsRequireEvaluatedCurrentObservationAndOldCommandCannotReacquireAfterResume(string id)
    {
        var access = new DesktopAccessCoordinator(_ => true); var controller = new Controller(); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => { controller.Generation = lease.Generation; return controller; });
        var binding = Registry(session).Bind(Request(runId));
        var capture = await binding.InvokeAsync("computer_screenshot", "capture", Json(new { }), CancellationToken.None);
        var observation = capture.Result.StructuredData!.Value.GetProperty("observationId").GetString();
        var args = Arguments(id, observation!);
        var before = await binding.InvokeAsync(id, "unconfirmed", args, CancellationToken.None);
        Assert.Equal("ScreenReevaluationRequired", before.Result.ErrorCode); Assert.Equal(new[] { "capture" }, controller.Calls);
        session.ConfirmModelObservation(); var generation = access.Snapshot.Generation;
        access.TakeControl(); await access.ReleasePausedRunAsync(runId); access.ResumeRun(runId);
        var stale = await binding.InvokeAsync(id, "old", args, CancellationToken.None);
        Assert.Equal("StaleDesktopContext", stale.Result.ErrorCode); Assert.Null(access.Snapshot.OwnerRunId);
        Assert.Equal(2, controller.Calls.Count);
        var fresh = await binding.InvokeAsync("computer_screenshot", "fresh", Json(new { }), CancellationToken.None);
        Assert.True(access.Snapshot.Generation > generation);
        session.ConfirmModelObservation();
        Assert.Equal("FreshScreenshotRequired", (await binding.InvokeAsync(id, "old-id", args, CancellationToken.None)).Result.ErrorCode);
        var freshId = fresh.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        Assert.Equal(ToolResultStatus.Success, (await binding.InvokeAsync(id, "new-action", Arguments(id, freshId), CancellationToken.None)).Result.Status);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("coordinates")]
    [InlineData("ticks")]
    [InlineData("text")]
    [InlineData("monitor")]
    [InlineData("extra")]
    public async Task StrictSchemaRejectsBadInputBeforeControllerOrLease(string invalid)
    {
        var access = new DesktopAccessCoordinator(_ => true); var created = 0; var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, _ => { created++; return new Controller(); });
        var binding = Registry(session).Bind(Request(runId)); var observationId = new string('a', 32);
        var (id, arguments) = invalid switch
        {
            "button" => ("computer_click", Json(new { observationId, imageX = 0, imageY = 0, button = "double" })),
            "coordinates" => ("computer_click", Json(new { observationId, imageX = -1, imageY = 0, button = "left" })),
            "ticks" => ("computer_scroll", Json(new { observationId, imageX = 0, imageY = 0, direction = "down", amount = 11 })),
            "text" => ("computer_type", Json(new { observationId, text = new string('x', 4097) })),
            "monitor" => ("computer_screenshot", Json(new { area = "Monitor" })),
            _ => ("computer_screenshot", Json(new { unexpected = true }))
        };
        Assert.Equal("InvalidToolArguments", (await binding.InvokeAsync(id, "invalid", arguments, CancellationToken.None)).Result.ErrorCode);
        Assert.Equal(0, created); Assert.Null(access.Snapshot.OwnerRunId);
    }

    [Theory]
    [InlineData(AgentEventKind.ToolRequested, 0)]
    [InlineData(AgentEventKind.ToolCompleted, 1)]
    public async Task DefaultPolicyDeniesAndCancellationCannotStartNextToolOrModel(AgentEventKind cancelAt, int expectedCreates)
    {
        var access = new DesktopAccessCoordinator(_ => true); var created = 0; var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, grant => { created++; return new Controller { Generation = grant.Generation }; });
        var deniedRegistry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask);
        Assert.Equal("ToolPermissionDenied", (await deniedRegistry.Bind(Request(runId)).InvokeAsync("computer_screenshot", "denied", Json(new { }), CancellationToken.None)).Result.ErrorCode);
        using var stop = new CancellationTokenSource();
        using var client = new ScriptedChatClient((_, _) => Call("cancel", "computer_screenshot", new { }));
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, Registry(session));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteAsync(Request(runId), new Progress(value =>
        { if (value.Kind == cancelAt) stop.Cancel(); }), stop.Token));
        Assert.Equal(expectedCreates, created); Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task ProfileBoundRuntimeInfersVisionRequirementFromActuallyAllowedToolDescriptors()
    {
        var access = new DesktopAccessCoordinator(_ => true); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, _ => throw new InvalidOperationException("No controller should be created."));
        var provider = new Provider();
        var profile = new ModelProfile { Id = "local", ProviderId = "openai", ModelId = "opaque", SecretRef = "unused", Capabilities = new(false, true) };
        var runtime = new MafAgentRuntime(provider, profile, Registry(session), (_, _) => Task.CompletedTask, session.ConfirmModelObservation);
        var result = await runtime.ExecuteAsync(Request(runId), new Progress(), CancellationToken.None);
        Assert.Equal("VisionRequired", result.ErrorCode); Assert.Equal(0, provider.Opens); Assert.Null(access.Snapshot.OwnerRunId);
    }

    private static JsonElement Arguments(string id, string observationId) => id switch
    {
        "computer_click" => Json(new { observationId, imageX = 0, imageY = 0, button = "left" }),
        "computer_scroll" => Json(new { observationId, imageX = 0, imageY = 0, direction = "down", amount = 1 }),
        "computer_type" => Json(new { observationId, text = "hello" }),
        _ => Json(new { observationId, chord = "Ctrl+A" })
    };

    [Fact]
    public async Task EverySuccessfulInputInvalidatesOldCoordinatesAndRequiresEvaluationOfNewSnapshot()
    {
        var access = new DesktopAccessCoordinator(_ => true); var controller = new Controller(); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => { controller.Generation = lease.Generation; return controller; });
        var binding = Registry(session).Bind(Request(runId));
        var capture = await binding.InvokeAsync("computer_screenshot", "capture", Json(new { }), CancellationToken.None);
        var original = capture.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        session.ConfirmModelObservation();
        var input = await binding.InvokeAsync("computer_click", "input", Arguments("computer_click", original), CancellationToken.None);
        var fresh = input.Result.StructuredData!.Value.GetProperty("observation").GetProperty("observationId").GetString()!;
        Assert.NotEqual(original, fresh); Assert.Single(input.Result.Images); Assert.Equal(2, controller.Calls.Count(call => call == "capture"));
        var count = controller.Calls.Count;
        Assert.Equal("FreshScreenshotRequired", (await binding.InvokeAsync("computer_click", "old-coordinates", Arguments("computer_click", original), CancellationToken.None)).Result.ErrorCode);
        Assert.Equal("ScreenReevaluationRequired", (await binding.InvokeAsync("computer_click", "not-evaluated", Arguments("computer_click", fresh), CancellationToken.None)).Result.ErrorCode);
        Assert.Equal(count, controller.Calls.Count);
    }

    [Fact]
    public async Task ObservationDependencyMustBeAllowedAndItsPolicyRecheckedAfterInput()
    {
        var access = new DesktopAccessCoordinator(_ => true); var controller = new Controller(); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => { controller.Generation = lease.Generation; return controller; });
        var observations = 0;
        var registry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask, (descriptor, _, _) =>
            ValueTask.FromResult(descriptor.Id != "computer_screenshot" || ++observations == 1));
        Assert.Equal("ObservationToolUnavailable", Assert.Throws<ModelProviderException>(() => registry.Bind(Request(runId) with { AllowedToolIds = ["computer_click"] })).Code);
        Assert.Null(access.Snapshot.OwnerRunId);
        var binding = registry.Bind(Request(runId));
        var capture = await binding.InvokeAsync("computer_screenshot", "capture", Json(new { }), CancellationToken.None);
        session.ConfirmModelObservation();
        var original = capture.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        var outcome = await binding.InvokeAsync("computer_click", "action", Arguments("computer_click", original), CancellationToken.None);
        Assert.Equal(ToolResultStatus.OutcomeUnknown, outcome.Result.Status); Assert.Equal("PostActionObservationFailed", outcome.Result.ErrorCode);
        Assert.Equal("Success", outcome.Result.StructuredData!.Value.GetProperty("actionStatus").GetString());
        Assert.Equal(new[] { "capture", "confirm", "click:Left" }, controller.Calls);
        Assert.Equal(2, observations); Assert.Empty(outcome.Result.Images);
    }
    private static ChatResponse Call(string callId, string name, object arguments) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(callId, name, Json(arguments).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()))]))
    { FinishReason = ChatFinishReason.ToolCalls };

    [Fact]
    public async Task GuiBatchCannotExecuteBeforeFreshObservationPerAction()
    {
        var access = new DesktopAccessCoordinator(_ => true); var runId = Guid.NewGuid(); var creates = 0;
        await using var session = new ComputerToolSession(runId, access, _ => { creates++; return new Controller(); });
        var arguments = Arguments("computer_click", new string('a', 32)).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        using var client = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("one", "computer_click", arguments), new FunctionCallContent("two", "computer_click", arguments)]))
        { FinishReason = ChatFinishReason.ToolCalls });
        var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, Registry(session));
        var result = await runtime.ExecuteAsync(Request(runId), new Progress(), CancellationToken.None);
        Assert.Equal("FreshObservationPerActionRequired", result.ErrorCode); Assert.Equal(0, creates); Assert.Null(access.Snapshot.OwnerRunId);
    }

    [Fact]
    public async Task DriverRejectsChordBeforeEffectWithoutAnotherScreenshot()
    {
        var access = new DesktopAccessCoordinator(_ => true); var native = new Native(); var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => new WindowsComputerController(access, lease, native, native, native.GetMonitors));
        var binding = Registry(session).Bind(Request(runId));
        var capture = await binding.InvokeAsync("computer_screenshot", "capture", Json(new { }), CancellationToken.None);
        session.ConfirmModelObservation();
        var id = capture.Result.StructuredData!.Value.GetProperty("observationId").GetString()!;
        var result = await binding.InvokeAsync("computer_press_key", "key", Arguments("computer_press_key", id), CancellationToken.None);
        Assert.Equal(ToolResultStatus.Blocked, result.Result.Status); Assert.NotNull(result.Result.ErrorCode);
        Assert.Equal(1, native.Captures); Assert.Empty(native.Inputs); Assert.Empty(result.Result.Images);
    }
    private static AIContent[] LastObservation(IReadOnlyList<ChatMessage> messages) => messages.Last(message => message.Role == ChatRole.User && message.Contents.Any(content => content is DataContent)).Contents.ToArray();
    private static string ObservationId(IReadOnlyList<ChatMessage> messages)
    {
        var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last();
        using var metadata = JsonDocument.Parse((string)result.Result!);
        var data = metadata.RootElement.GetProperty("data");
        if (data.TryGetProperty("observation", out var observation)) data = observation;
        return data.GetProperty("observationId").GetString()!;
    }
    private sealed class Progress(Action<AgentEvent>? callback = null) : IProgress<AgentEvent> { public void Report(AgentEvent value) => callback?.Invoke(value); }
    private sealed class Provider : IModelProvider
    {
        public int Opens;
        public string ProviderId => "openai";
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken cancellationToken)
        { Opens++; throw new InvalidOperationException("Provider must not open."); }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Controller : IObservedComputerController
    {
        public long Generation; public List<string> Calls = [];
        public Task<ScreenCapture> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct = default)
        {
            Calls.Add("capture"); return Task.FromResult(new ScreenCapture
            {
                ImageBytes = [137, 80, 78, 71],
                ContentType = "image/png",
                ImageWidth = 4,
                ImageHeight = 3,
                SourceBounds = new(-4, 0, 4, 3),
                Monitors = [new("primary", new(-4, 0, 4, 3), 96, 96, true)],
                TimestampUtc = DateTimeOffset.UtcNow,
                DesktopGeneration = Generation
            });
        }
        public void ConfirmObservation(ScreenCapture observation) => Calls.Add("confirm");
        public Task ClickAsync(ScreenPoint point, MouseButton button, CancellationToken ct = default) { Assert.Equal(new(-4, 0), point); Calls.Add("click:" + button); return Task.CompletedTask; }
        public Task ScrollAsync(ScreenPoint point, ScrollDirection direction, int amount, CancellationToken ct = default) { Calls.Add($"scroll:{direction}:{amount}"); return Task.CompletedTask; }
        public Task TypeAsync(string text, CancellationToken ct = default) { Calls.Add("type:" + text); return Task.CompletedTask; }
        public Task PressKeyAsync(KeyCombination keys, CancellationToken ct = default) { Calls.Add("key:" + keys.Chord); return Task.CompletedTask; }
    }
    internal sealed class Native : IWindowsCaptureNative, IWindowsKeyboardNative
    {
        public int Captures; public List<NativeInput[]> Inputs = [];
        public int Origin = -4;
        public Action? OnSend;
        public bool IsDesktopAvailable() => true;
        public bool IsKeyDown(ushort key) => false;
        public IReadOnlyList<ScreenMonitor> GetMonitors() => [new("primary", new(Origin, 0, 4, 3), 96, 96, true)];
        public BitmapSource CaptureBitmap(ScreenBounds bounds, IReadOnlyList<ScreenMonitor> monitors)
        { Captures++; var pixels = Enumerable.Repeat((byte)Captures, 48).ToArray(); return BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 16); }
        public InputInsertion Send(NativeInput[] inputs) { Inputs.Add(inputs.ToArray()); OnSend?.Invoke(); return new((uint)inputs.Length, 0); }
    }
}
