using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using static PersonalAgent.IntegrationTests.AnthropicModelProviderTests;

namespace PersonalAgent.IntegrationTests;

public sealed class AnthropicCapabilityTests
{
    [Theory]
    [InlineData(false, false, false, "ToolCallingRequired")]
    [InlineData(true, false, true, "ToolCallingRequired")]
    [InlineData(false, true, true, "VisionRequired")]
    public async Task WrongCapabilitiesBlockBeforeSecretOrNetwork(bool images, bool tools, bool desktop, string code)
    {
        var secrets = new Secrets();
        var runtime = new MafAgentRuntime(new AnthropicModelProvider(secrets), Profile with { Capabilities = new(images, tools) }, desktop, (_, _) => Task.CompletedTask);
        var result = await runtime.ExecuteAsync(Request(), new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(0, secrets.Reads);
    }

    [Fact]
    public async Task ActualSdkToolImageAndFreshObservationRoundTripPreservesUsageAndModelSelection()
    {
        var access = new DesktopAccessCoordinator(_ => true);
        var native = new ComputerToolTests.Native();
        native.OnSend = () => native.Origin = 10;
        var request = Request() with { AllowedToolIds = ["computer_screenshot", "computer_click"] };
        await using var session = new ComputerToolSession(request.RunId, access,
            lease => new WindowsComputerController(access, lease, native, native, native.GetMonitors));
        var registry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
        var posts = 0;
        string? firstImage = null, firstObservation = null;
        using var handler = new Handler(async (message, ct) =>
        {
            if (message.Method == HttpMethod.Get) return ModelResponse();
            posts++;
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(Profile.ModelId, root.GetProperty("model").GetString());
            Assert.Equal(new[] { "computer_click", "computer_screenshot" }, root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());
            Assert.Contains("PersonalAgent", root.GetProperty("system").ToString());
            if (posts == 1) return ToolStream("capture", "computer_screenshot", new { });
            var blocks = root.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "user")
                .SelectMany(m => m.GetProperty("content").EnumerateArray()).ToArray();
            var tool = blocks.Last(b => b.GetProperty("type").GetString() == "tool_result");
            Assert.Equal(posts == 2 ? "capture" : "click", tool.GetProperty("tool_use_id").GetString());
            var toolContent = tool.GetProperty("content");
            var toolText = toolContent.ValueKind == JsonValueKind.String ? toolContent.GetString()!
                : string.Concat(toolContent.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
            Assert.DoesNotContain("iVBOR", toolText);
            using var metadata = JsonDocument.Parse(toolText);
            var data = metadata.RootElement.GetProperty("data");
            if (posts == 3) data = data.GetProperty("observation");
            var observation = data.GetProperty("observationId").GetString()!;
            Assert.Equal(posts == 2 ? -4 : 10, data.GetProperty("SourceBounds").GetProperty("X").GetInt32());
            var source = blocks.Last(b => b.GetProperty("type").GetString() == "image").GetProperty("source");
            Assert.Equal("base64", source.GetProperty("type").GetString());
            Assert.Equal("image/png", source.GetProperty("media_type").GetString());
            var image = source.GetProperty("data").GetString()!;
            using var stream = new MemoryStream(Convert.FromBase64String(image));
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(4, frame.PixelWidth); Assert.Equal(3, frame.PixelHeight);
            Assert.Contains(blocks, b => b.GetProperty("type").GetString() == "text" && b.GetProperty("text").GetString()!.Contains("Untrusted tool observation"));
            if (posts == 2)
            {
                firstImage = image; firstObservation = observation;
                return ToolStream("click", "computer_click", new { observationId = observation, imageX = 0, imageY = 0, button = "left" });
            }
            Assert.Equal(3, posts);
            Assert.NotEqual(firstImage, image); Assert.NotEqual(firstObservation, observation);
            return TextStream("Fresh Claude observation confirmed");
        });
        using var http = new HttpClient(handler);
        IAgentRuntime runtime = new MafAgentRuntime(new AnthropicModelProvider(new Secrets(), http),
            Profile with { Capabilities = new(true, true) }, registry, (_, _) => Task.CompletedTask, session.ConfirmModelObservation);
        var result = await runtime.ExecuteAsync(request, new Progress(), CancellationToken.None);
        Assert.Null(handler.Failure);
        Assert.Equal(AgentResultStatus.Completed, result.Status);
        Assert.Equal("Fresh Claude observation confirmed", result.Summary);
        Assert.Equal(new AgentUsage(33, 9), result.Usage); // Input includes both cache read and cache creation tokens.
        Assert.Equal(3, posts); Assert.Equal(2, native.Captures); Assert.Single(native.Inputs);
    }

    [Theory]
    [InlineData("max_tokens", "ModelResponseIncomplete")]
    [InlineData("refusal", "ModelContentFiltered")]
    [InlineData("pause_turn", "ModelResponseIncomplete")]
    public async Task UnconfirmedAnthropicStopReasonCannotBecomeCompleted(string finish, string code)
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? ModelResponse() : TextStream("partial", finish)));
        using var http = new HttpClient(handler);
        var result = await new MafAgentRuntime(new AnthropicModelProvider(new Secrets(), http),
            Profile with { Capabilities = new(false, true) }, false, (_, _) => Task.CompletedTask)
            .ExecuteAsync(Request(), new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public async Task ConnectionProbeUsesAnthropicToolChoiceAndSyntheticImageOnly(bool images, bool tools, int expectedProbes)
    {
        var probes = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return ModelResponse();
            probes++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("tool", body.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
            Assert.Equal("capability_probe", body.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
            var blocks = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
            var image = blocks.FirstOrDefault(b => b.GetProperty("type").GetString() == "image");
            var value = image.ValueKind != JsonValueKind.Undefined ? "red"
                : Regex.Match(body.RootElement.ToString(), "exactly: ([a-f0-9]{32})").Groups[1].Value;
            Assert.NotEmpty(value);
            if (image.ValueKind != JsonValueKind.Undefined)
            {
                using var stream = new MemoryStream(Convert.FromBase64String(image.GetProperty("source").GetProperty("data").GetString()!));
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal(32, frame.PixelWidth); Assert.Equal(32, frame.PixelHeight);
            }
            return ToolResponse("probe", "capability_probe", new { value });
        });
        using var http = new HttpClient(handler);
        var result = await new AnthropicModelProvider(new Secrets(), http).TestConnectionAsync(
            Profile with { Capabilities = new(images, tools) }, CancellationToken.None);
        Assert.True(result.Connected, result.Message);
        Assert.Equal(new(images, tools), result.VerifiedCapabilities);
        Assert.Equal(expectedProbes, probes);
    }

    [Fact]
    public async Task WrongSyntheticImageAnswerDoesNotVerifyVisionOrExecuteAFunction()
    {
        var probes = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return ModelResponse();
            probes++;
            var payload = await request.Content!.ReadAsStringAsync(ct);
            var value = probes == 1 ? Regex.Match(payload, "exactly: ([a-f0-9]{32})").Groups[1].Value : "blue";
            return ToolResponse("probe", "capability_probe", new { value });
        });
        using var http = new HttpClient(handler);
        var result = await new AnthropicModelProvider(new Secrets(), http).TestConnectionAsync(Profile with { Capabilities = new(true, true) }, CancellationToken.None);
        Assert.False(result.Connected); Assert.Null(result.VerifiedCapabilities);
        Assert.Equal("CapabilityProbeFailed", result.ErrorCode); Assert.Equal(2, probes);
    }

    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = Profile.Id, Instructions = "Synthetic fixture" };
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private static HttpResponseMessage ToolResponse(string id, string name, object input) => JsonResponse(new
    {
        id = "msg_probe", type = "message", role = "assistant", model = Profile.ModelId,
        content = new[] { new { type = "tool_use", id, name, input } },
        stop_reason = "tool_use", stop_sequence = (string?)null, usage = new { input_tokens = 7, output_tokens = 3 }
    });
    private static HttpResponseMessage ToolStream(string id, string name, object input) => Stream(
        new { type = "message_start", message = new { id = "msg_" + id, type = "message", role = "assistant", model = Profile.ModelId,
            content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null,
            usage = new { input_tokens = 7, output_tokens = 0, cache_read_input_tokens = 4, cache_creation_input_tokens = 2 } } },
        new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } },
        new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } },
        new { type = "content_block_stop", index = 0 },
        new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 3 } },
        new { type = "message_stop" });
}
