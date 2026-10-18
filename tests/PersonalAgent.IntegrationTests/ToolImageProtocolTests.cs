using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.Core.Computer;
using PersonalAgent.Computer;
using System.IO;
using System.Windows.Media.Imaging;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolImageProtocolTests
{
    [Fact]
    public async Task SdkToolImageBecomesActualImageContentInHttpRequest()
    {
        using var handler = new ImageHttp(); using var http = new HttpClient(handler);
        var profile = new ModelProfile { Id = "test", ProviderId = "openai", ModelId = "test-model", SecretRef = "test" };
        using var connection = await new OpenAiModelProvider(new Secrets(), http).OpenAsync(profile, CancellationToken.None);
        List<ChatMessage> messages = [
            new(ChatRole.User, "Synthetic image protocol check"),
            new(ChatRole.Assistant, [new FunctionCallContent("capture-1", "computer_screenshot", new Dictionary<string, object?>())])];
        messages.AddRange(ToolResultMessages.Create("capture-1", [new TextContent("synthetic observation metadata"), new DataContent(new byte[] { 137, 80, 78, 71 }, "image/png")]));
        await connection.Client.GetResponseAsync(messages);
        Assert.NotNull(handler.Body);
        Assert.Contains("image_url", handler.Body);
        Assert.Contains("data:image/png;base64,iVBORw==", handler.Body);
        Assert.Contains("Untrusted tool observation", handler.Body);
    }

    [Fact]
    public async Task ActualSdkMafAndControllerSendFreshPostActionPngAndChangedGeometryInNextHttpRequest()
    {
        var access = new DesktopAccessCoordinator(_ => true); var native = new ComputerToolTests.Native();
        native.OnSend = () => native.Origin = 10;
        var runId = Guid.NewGuid();
        await using var session = new ComputerToolSession(runId, access, lease => new WindowsComputerController(access, lease, native, native, native.GetMonitors));
        var registry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
        using var handler = new RoundTripHttp(); using var http = new HttpClient(handler);
        var profile = new ModelProfile { Id = "test", ProviderId = "openai", ModelId = "test-model", SecretRef = "test", Capabilities = new(true, true) };
        var runtime = new MafAgentRuntime(new OpenAiModelProvider(new Secrets(), http), profile, registry, (_, _) => Task.CompletedTask, session.ConfirmModelObservation);
        var result = await runtime.ExecuteAsync(new()
        {
            RunId = runId,
            ConversationId = Guid.NewGuid(),
            Instructions = "Synthetic local target",
            ModelProfileId = "test",
            AllowedToolIds = ["computer_screenshot", "computer_click"]
        }, new InlineProgress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Completed, result.Status); Assert.Equal("Fresh image observed", result.Summary);
        Assert.Equal(3, handler.Posts); Assert.Equal(2, native.Captures); Assert.Single(native.Inputs);
        Assert.NotEqual(handler.BeforeImage, handler.AfterImage); Assert.NotEqual(handler.BeforeObservation, handler.AfterObservation);
    }

    private sealed class InlineProgress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class RoundTripHttp : HttpMessageHandler
    {
        public int Posts;
        public string? BeforeImage, AfterImage, BeforeObservation, AfterObservation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Json("{\"id\":\"test-model\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"}");
            Posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            if (Posts == 1) return Tool("capture", "computer_screenshot", "{}");
            var tool = messages.Last(message => message.GetProperty("role").GetString() == "tool");
            Assert.DoesNotContain("iVBOR", tool.GetProperty("content").GetString()!); // Binary is not nested JSON text.
            using var metadata = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
            var data = metadata.RootElement.GetProperty("data");
            if (Posts == 3) data = data.GetProperty("observation");
            var observation = data.GetProperty("observationId").GetString()!;
            Assert.Equal(Posts == 2 ? -4 : 10, data.GetProperty("SourceBounds").GetProperty("X").GetInt32());
            Assert.Equal(1, data.GetProperty("DesktopGeneration").GetInt64());
            var content = messages.Last(message => message.GetProperty("role").GetString() == "user" && message.GetProperty("content").ValueKind == JsonValueKind.Array).GetProperty("content");
            Assert.Contains("Untrusted tool observation", content[0].GetProperty("text").GetString()!);
            var uri = content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "image_url").GetProperty("image_url").GetProperty("url").GetString()!;
            Assert.StartsWith("data:image/png;base64,", uri);
            using var stream = new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(4, image.PixelWidth); Assert.Equal(3, image.PixelHeight);
            if (Posts == 2)
            {
                BeforeImage = uri; BeforeObservation = observation;
                return Tool("click", "computer_click", JsonSerializer.Serialize(new { observationId = observation, imageX = 0, imageY = 0, button = "left" }));
            }
            Assert.Equal(3, Posts); AfterImage = uri; AfterObservation = observation;
            return Stream(new { role = "assistant", content = "Fresh image observed" }, "stop");
        }
        private static HttpResponseMessage Tool(string id, string name, string arguments) => Stream(new
        { role = "assistant", tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments } } } }, "tool_calls");
        private static HttpResponseMessage Stream(object delta, string finish) => new(HttpStatusCode.OK)
        { Content = new StringContent("data: " + JsonSerializer.Serialize(new { id = "test", @object = "chat.completion.chunk", created = 1, model = "test-model", choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    internal sealed class ImageHttp : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Json("{\"id\":\"test-model\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"}");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Observed\"},\"finish_reason\":\"stop\"}]}");
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("synthetic-key");
    }
}
