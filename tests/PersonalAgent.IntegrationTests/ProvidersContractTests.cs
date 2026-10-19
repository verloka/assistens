using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using static PersonalAgent.IntegrationTests.AnthropicModelProviderTests;

namespace PersonalAgent.IntegrationTests;

// All assertions are shared; only the scripted HTTP wire codec knows the provider.
public sealed class ProvidersContractTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ProvidersShareToolImageUsageMetadataAndFreshObservationContract(string provider)
    {
        await using var fixture = new Fixture(provider);
        string? previous = null; byte[]? previousImage = null;
        fixture.Wire.Respond = (body, turn) =>
        {
            Assert.Equal("contract-model", body.GetProperty("model").GetString());
            Assert.Contains("PersonalAgent application policy v1", body.ToString());
            if (turn == 1) return fixture.Wire.Tool("capture", "computer_screenshot", new { });
            var (callId, metadata, png) = fixture.Wire.Observation(body);
            Assert.Equal(turn == 2 ? "capture" : "click", callId);
            Assert.Equal(turn == 2 ? "computer_screenshot" : "computer_click", metadata.GetProperty("toolId").GetString());
            Assert.Equal((int)ToolSourceKind.Computer, metadata.GetProperty("source").GetProperty("Kind").GetInt32());
            Assert.Equal("Success", metadata.GetProperty("status").GetString());
            Assert.True(metadata.GetProperty("durationMs").GetDouble() >= 0);
            var data = metadata.GetProperty("data");
            if (data.TryGetProperty("observation", out var observation)) data = observation;
            var token = data.GetProperty("observationId").GetString()!;
            using var stream = new MemoryStream(png);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(4, image.PixelWidth); Assert.Equal(3, image.PixelHeight);
            if (turn == 2)
            {
                previous = token; previousImage = png;
                return fixture.Wire.Tool("click", "computer_click", new { observationId = token, imageX = 0, imageY = 0, button = "left" });
            }
            Assert.Equal(3, turn); Assert.NotEqual(previous, token); Assert.False(previousImage!.SequenceEqual(png));
            return fixture.Wire.Text("Common provider result");
        };
        var result = await fixture.ExecuteAsync();
        Assert.Null(fixture.Wire.Failure);
        Assert.Equal(AgentResultStatus.Completed, result.Status);
        Assert.Equal("Common provider result", result.Summary);
        Assert.Equal(new AgentUsage(21, 9), result.Usage);
        Assert.Equal(3, fixture.Wire.Posts); Assert.Equal(2, fixture.Native.Captures); Assert.Single(fixture.Native.Inputs);
        var requested = fixture.Events.Items.Where(e => e.Kind == AgentEventKind.ToolRequested).ToArray();
        var completed = fixture.Events.Items.Where(e => e.Kind == AgentEventKind.ToolCompleted).ToArray();
        Assert.Equal(2, requested.Length);
        Assert.Equal(requested.Select(e => e.ToolCallId), completed.Select(e => e.ToolCallId));
        Assert.Equal(new[] { "computer_screenshot", "computer_click" }, completed.Select(e => e.ToolId));
        Assert.Equal(AgentEventKind.Completed, fixture.Events.Items[^1].Kind);
    }

    [Theory]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    public async Task ProvidersShareTokenAndStepLimitsBeforeAnotherEffect(string provider, bool tokens)
    {
        await using var fixture = new Fixture(provider);
        fixture.Wire.Respond = (body, turn) => turn == 1 ? fixture.Wire.Tool("capture", "computer_screenshot", new { })
            : fixture.Wire.Tool("click", "computer_click", new { observationId = fixture.Wire.Observation(body).Metadata.GetProperty("data").GetProperty("observationId").GetString(), imageX = 0, imageY = 0, button = "left" });
        var result = await fixture.ExecuteAsync(tokens ? new() { MaxTokens = 9 } : new() { MaxSteps = 1 });
        Assert.Null(fixture.Wire.Failure);
        Assert.Equal(AgentResultStatus.Failed, result.Status);
        Assert.Equal(tokens ? "TokenLimitExceeded" : "StepLimitExceeded", result.ErrorCode);
        Assert.Equal(tokens ? 0 : 1, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
        Assert.Equal(tokens ? 1 : 2, fixture.Wire.Posts);
        Assert.DoesNotContain(fixture.Events.Items, e => e.Kind == AgentEventKind.Completed);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ProvidersShareMalformedArgumentsRefusalWithoutRetry(string provider)
    {
        await using var fixture = new Fixture(provider);
        fixture.Wire.Respond = (_, _) => fixture.Wire.Tool("invalid", "computer_screenshot", new { unexpected = true });
        var result = await fixture.ExecuteAsync();
        Assert.Null(fixture.Wire.Failure);
        Assert.Equal(AgentResultStatus.Blocked, result.Status);
        Assert.Equal("InvalidToolArguments", result.ErrorCode);
        Assert.Equal(1, fixture.Wire.Posts); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ProvidersShareCancellationOfInFlightModelWithoutAnotherRequestOrInput(string provider)
    {
        await using var fixture = new Fixture(provider);
        fixture.Wire.WaitForCancellation = true;
        using var stop = new CancellationTokenSource();
        var executing = fixture.ExecuteAsync(ct: stop.Token);
        await fixture.Wire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Wire.Posts); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
        Assert.DoesNotContain(fixture.Events.Items, e => e.Kind == AgentEventKind.Completed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ComputerToolTests.Native Native { get; } = new();
        public Wire Wire { get; }
        public Events Events { get; } = new();
        private readonly ComputerToolSession session;
        private readonly HttpClient http;
        private readonly MafAgentRuntime runtime;
        private readonly AgentRequest request;
        public Fixture(string provider)
        {
            Wire = new(provider); http = new(Wire);
            request = new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "contract", Instructions = "Synthetic provider contract", AllowedToolIds = ["computer_screenshot", "computer_click"] };
            var access = new DesktopAccessCoordinator(_ => true);
            session = new(request.RunId, access, lease => new WindowsComputerController(access, lease, Native, Native, Native.GetMonitors));
            var registry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
            IModelProvider model = provider == "anthropic" ? new AnthropicModelProvider(new Secrets(), http) : new OpenAiModelProvider(new Secrets(), http);
            var profile = Profile with { Id = "contract", ProviderId = provider, ModelId = "contract-model", Capabilities = new(true, true) };
            runtime = new(model, profile, registry, (_, _) => Task.CompletedTask, session.ConfirmModelObservation);
        }
        public Task<AgentRunResult> ExecuteAsync(RunLimits? limits = null, CancellationToken ct = default) => runtime.ExecuteAsync(request with { Limits = limits ?? new() }, Events, ct);
        public async ValueTask DisposeAsync() { await session.DisposeAsync(); http.Dispose(); }
    }
    private sealed class Events : IProgress<AgentEvent>
    { public List<AgentEvent> Items { get; } = []; public void Report(AgentEvent value) => Items.Add(value); }

    private sealed class Wire(string provider) : HttpMessageHandler
    {
        private bool Claude => provider == "anthropic";
        public int Posts;
        public Exception? Failure;
        public bool WaitForCancellation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<JsonElement, int, HttpResponseMessage>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Claude ? ModelResponse()
                : JsonResponse(new { id = "contract-model", @object = "model", created = 1, owned_by = "test" });
            Posts++; Entered.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            try
            {
                Assert.Equal(Claude ? "https://api.anthropic.com/v1/messages" : "https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                return Respond!(body.RootElement, Posts);
            }
            catch (Exception exception) { Failure = exception; throw; }
        }
        public (string CallId, JsonElement Metadata, byte[] Image) Observation(JsonElement body)
        {
            var messages = body.GetProperty("messages").EnumerateArray().ToArray();
            string callId, text, base64;
            if (Claude)
            {
                var blocks = messages.Where(m => m.GetProperty("role").GetString() == "user")
                    .SelectMany(m => m.GetProperty("content").EnumerateArray()).ToArray();
                var result = blocks.Last(b => b.GetProperty("type").GetString() == "tool_result");
                callId = result.GetProperty("tool_use_id").GetString()!;
                var content = result.GetProperty("content");
                text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
                base64 = blocks.Last(b => b.GetProperty("type").GetString() == "image").GetProperty("source").GetProperty("data").GetString()!;
            }
            else
            {
                var result = messages.Last(m => m.GetProperty("role").GetString() == "tool");
                callId = result.GetProperty("tool_call_id").GetString()!; text = result.GetProperty("content").GetString()!;
                var uri = messages.Where(m => m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    .SelectMany(m => m.GetProperty("content").EnumerateArray()).Last(b => b.GetProperty("type").GetString() == "image_url")
                    .GetProperty("image_url").GetProperty("url").GetString()!;
                base64 = uri[(uri.IndexOf(',') + 1)..];
            }
            using var metadata = JsonDocument.Parse(text);
            return (callId, metadata.RootElement.Clone(), Convert.FromBase64String(base64));
        }
        public HttpResponseMessage Tool(string id, string name, object input) => Claude ? Stream(
            Start(), new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } },
            new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } },
            new { type = "content_block_stop", index = 0 }, End("tool_use"), new { type = "message_stop" })
            : OpenAi(new { role = "assistant", tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(input) } } } }, "tool_calls");
        public HttpResponseMessage Text(string text) => Claude ? Stream(Start(),
            new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
            new { type = "content_block_stop", index = 0 }, End("end_turn"), new { type = "message_stop" })
            : OpenAi(new { role = "assistant", content = text }, "stop");
        private static object Start() => new { type = "message_start", message = new { id = "msg_contract", type = "message", role = "assistant", model = "contract-model",
            content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 7, output_tokens = 0 } } };
        private static object End(string finish) => new { type = "message_delta", delta = new { stop_reason = finish, stop_sequence = (string?)null }, usage = new { output_tokens = 3 } };
        private static HttpResponseMessage OpenAi(object delta, string finish) => new(HttpStatusCode.OK)
        { Content = new StringContent("data: " + JsonSerializer.Serialize(new { id = "contract", @object = "chat.completion.chunk", created = 1, model = "contract-model",
            usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 }, choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    }
}
