using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalAgent.IntegrationTests;

public sealed class ModelCapabilityTests
{
    private static ModelProfile Profile => new()
    { Id = "cap-test", ProviderId = "openai", ModelId = "opaque-model-name", SecretRef = "synthetic-ref" };

    [Theory]
    [InlineData(false, false, false, "ToolCallingRequired")]
    [InlineData(true, false, true, "ToolCallingRequired")]
    [InlineData(false, true, true, "VisionRequired")]
    public async Task IncompatibleAgentProfileBlocksBeforeProviderOrModelAccess(bool images, bool tools, bool desktop, string code)
    {
        var provider = new Provider();
        var runtime = new MafAgentRuntime(provider, Profile with { Capabilities = new(images, tools) }, desktop, (_, _) => Task.CompletedTask);
        var result = await runtime.ExecuteAsync(Request(), new Progress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.Equal(code, result.ErrorCode);
        Assert.Equal(0, provider.Opens); Assert.NotEmpty(result.Summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CompatibleToolsOrDesktopPreflightReachesProviderAndMismatchedProfileCannotOpen(bool images, bool desktop)
    {
        var provider = new Provider();
        var profile = Profile with { Capabilities = new(images, true) };
        // Open throws a known sentinel after preflight; no actual model/network/tool is called.
        var runtime = new MafAgentRuntime(provider, profile, desktop, (_, _) => Task.CompletedTask);
        var result = await runtime.ExecuteAsync(Request(), new Progress(), CancellationToken.None);
        Assert.Equal("ReachedProvider", result.ErrorCode); Assert.Equal(1, provider.Opens);
        result = await runtime.ExecuteAsync(Request() with { ModelProfileId = "other" }, new Progress(), CancellationToken.None);
        Assert.Equal("ModelProfileMismatch", result.ErrorCode); Assert.Equal(1, provider.Opens);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public async Task TestConnectionProbesOnlyConfiguredCapabilitiesWithSyntheticContentAndNoToolInvocation(bool images, bool tools, int probes)
    {
        using var handler = new ProbeHttp(); using var http = new HttpClient(handler);
        var result = await new OpenAiModelProvider(new Secrets(), http).TestConnectionAsync(
            Profile with { Capabilities = new(images, tools) }, CancellationToken.None);
        Assert.True(result.Connected, result.Message); Assert.Equal(new(images, tools), result.VerifiedCapabilities);
        Assert.Equal(probes, handler.Probes); Assert.Equal(images, handler.SawImage);
    }

    [Theory]
    [InlineData(false, null, 1024)]
    [InlineData(true, 4096, 4096)]
    [InlineData(true, 32, 32)]
    [InlineData(true, 8192, 4096)]
    public async Task ExplicitReasoningSettingAndBoundedProfileLimitReachTheActualSdk(bool disableReasoning, int? limit, int expectedLimit)
    {
        using var handler = new ProbeHttp(); using var http = new HttpClient(handler);
        var profile = Profile with { Capabilities = new(true, true), GenerationSettings = new(MaxOutputTokens: limit, DisableReasoning: disableReasoning) };
        var result = await new OpenAiModelProvider(new Secrets(), http).TestConnectionAsync(profile, CancellationToken.None);
        Assert.True(result.Connected, result.Message); Assert.Equal(2, handler.Probes);
        Assert.Equal(expectedLimit, handler.OutputLimit); Assert.Equal(disableReasoning ? "none" : null, handler.ReasoningEffort);
        var legacy = JsonSerializer.Deserialize<ModelGenerationSettings>("{\"Temperature\":null,\"MaxOutputTokens\":4096}")!;
        Assert.False(legacy.DisableReasoning);
        Assert.Equal("{\"Temperature\":null,\"MaxOutputTokens\":4096}", JsonSerializer.Serialize(legacy));
    }

    [Theory]
    [InlineData("text")][InlineData("wrong-name")][InlineData("wrong-arguments")][InlineData("extra-argument")][InlineData("length")][InlineData("http-error")]
    public async Task UnconfirmedCapabilityProbeCannotBecomeSuccessfulOrExposeRawProviderError(string invalid)
    {
        using var handler = new ProbeHttp(invalid); using var http = new HttpClient(handler);
        var result = await new OpenAiModelProvider(new Secrets(), http).TestConnectionAsync(
            Profile with { Capabilities = new(false, true) }, CancellationToken.None);
        Assert.False(result.Connected); Assert.Equal("CapabilityProbeFailed", result.ErrorCode);
        Assert.Null(result.VerifiedCapabilities); Assert.Equal(1, handler.Probes);
        Assert.DoesNotContain("private-probe-error", result.Message);
        Assert.Equal(invalid switch { "length" => ModelProbeDiagnostic.ToolsOutputLimit, "http-error" => ModelProbeDiagnostic.ToolsRequestFailed,
            _ => ModelProbeDiagnostic.ToolsResponseInvalid }, result.ProbeDiagnostic);
    }

    [Fact]
    public async Task FailedImageProbeDoesNotClaimVisionFromAWorkingFunctionProbe()
    {
        using var handler = new ProbeHttp("wrong-image"); using var http = new HttpClient(handler);
        var result = await new OpenAiModelProvider(new Secrets(), http).TestConnectionAsync(
            Profile with { Capabilities = new(true, true) }, CancellationToken.None);
        Assert.False(result.Connected); Assert.Null(result.VerifiedCapabilities);
        Assert.True(handler.SawImage); Assert.Equal(2, handler.Probes);
        Assert.Equal(ModelProbeDiagnostic.ImageResponseInvalid, result.ProbeDiagnostic);
    }

    private static AgentRequest Request() => new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Test", ModelProfileId = Profile.Id };
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class Provider : IModelProvider
    {
        public int Opens;
        public string ProviderId => "openai";
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken cancellationToken)
        { Opens++; throw new ModelProviderException("ReachedProvider", "Preflight accepted the configured tool capability."); }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Secrets : PersonalAgent.Core.Security.ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("synthetic-probe-key");
    }
    private sealed class ProbeHttp(string? invalid = null) : HttpMessageHandler
    {
        public int Probes;
        public bool SawImage;
        public int OutputLimit;
        public string? ReasoningEffort;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Json("{\"id\":\"opaque-model-name\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"}");
            Probes++;
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            OutputLimit = document.RootElement.GetProperty("max_completion_tokens").GetInt32();
            ReasoningEffort = document.RootElement.TryGetProperty("reasoning_effort", out var effort) ? effort.GetString() : null;
            Assert.Equal("capability_probe", document.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("capability_probe", document.RootElement.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
            var image = payload.Contains("image_url", StringComparison.Ordinal); SawImage |= image;
            if (image) Assert.Contains("data:image/png;base64,", payload);
            var value = image ? (invalid == "wrong-image" ? "blue" : "red") : Regex.Match(payload, "exactly: ([a-f0-9]{32})").Groups[1].Value;
            Assert.NotEmpty(value);
            if (invalid == "http-error") return new(HttpStatusCode.BadRequest) { Content = new StringContent("private-probe-error") };
            if (invalid == "text") return Json("{\"id\":\"probe\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"opaque-model-name\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"no function\"},\"finish_reason\":\"stop\"}]}");
            var arguments = new Dictionary<string, object?> { ["value"] = invalid == "wrong-arguments" ? "wrong" : value };
            if (invalid == "extra-argument") arguments["unexpected"] = true;
            return Json(JsonSerializer.Serialize(new
            {
                id = "probe", @object = "chat.completion", created = 1, model = "opaque-model-name",
                choices = new[] { new { index = 0, finish_reason = invalid == "length" ? "length" : "tool_calls",
                    message = new { role = "assistant", content = (string?)null,
                        tool_calls = new[] { new { id = "probe-call", type = "function", function = new
                        { name = invalid == "wrong-name" ? "other" : "capability_probe", arguments = JsonSerializer.Serialize(arguments) } } } } } }
            }));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
