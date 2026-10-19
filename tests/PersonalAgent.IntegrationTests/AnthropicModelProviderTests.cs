using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class AnthropicModelProviderTests
{
    internal const string SyntheticKey = "synthetic-anthropic-key-not-real";
    internal static readonly ModelProfile Profile = new() { Id = "claude-test", ProviderId = "anthropic", ModelId = "configured-claude", SecretRef = "synthetic-reference" };

    [Fact]
    public async Task ExplicitSdkConfigurationUsesFixedEndpointAndOnlyProtectedStoreCredential()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.anthropic.com/v1/models/configured-claude", request.RequestUri!.AbsoluteUri);
            Assert.Equal(SyntheticKey, Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(ModelResponse());
        });
        using var http = new HttpClient(handler);
        var provider = new AnthropicModelProvider(new Secrets(), http);
        using (var connection = await provider.OpenAsync(Profile, CancellationToken.None))
        {
            var sdk = connection.Client.GetService<IAnthropicClient>();
            Assert.NotNull(sdk);
            Assert.Equal(0, sdk.MaxRetries);
            Assert.Equal("https://api.anthropic.com", sdk.BaseUrl);
            Assert.Null(sdk.AuthToken);
            Assert.Equal(TimeSpan.FromSeconds(15), sdk.Timeout);
        }
        // Disposing the owned SDK does not dispose the composition root's injected transport.
        using var second = await provider.OpenAsync(Profile, CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(Profile));
    }

    [Theory]
    [InlineData(401, "ProviderAuthenticationFailed")]
    [InlineData(403, "ProviderAuthenticationFailed")]
    [InlineData(404, "ModelUnavailable")]
    [InlineData(408, "ProviderUnavailable")]
    [InlineData(429, "ProviderRateLimited")]
    [InlineData(500, "ProviderUnavailable")]
    [InlineData(529, "ProviderUnavailable")]
    public async Task HttpFailureIsSanitizedAndNeverRetried(int status, string code)
    {
        using var handler = new Handler((_, _) => Task.FromResult(JsonResponse(new
        { type = "error", error = new { type = "api_error", message = SyntheticKey } }, status)));
        using var http = new HttpClient(handler);
        var result = await new AnthropicModelProvider(new Secrets(), http).TestConnectionAsync(Profile, CancellationToken.None);
        Assert.False(result.Connected);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("schema")]
    [InlineData("model")]
    [InlineData("temperature")]
    [InlineData("tokens")]
    [InlineData("pricing")]
    public async Task InvalidProfileFailsBeforeCredentialAccess(string invalid)
    {
        var profile = invalid switch
        {
            "provider" => Profile with { ProviderId = "openai" },
            "schema" => Profile with { SchemaVersion = 999 },
            "model" => Profile with { ModelId = "../../outside" },
            "temperature" => Profile with { GenerationSettings = new(1.5f) },
            "tokens" => Profile with { GenerationSettings = new(MaxOutputTokens: 0) },
            _ => Profile with { Pricing = new(-1, 0, "USD") }
        };
        var secrets = new Secrets();
        var result = await new AnthropicModelProvider(secrets).TestConnectionAsync(profile, CancellationToken.None);
        Assert.False(result.Connected);
        Assert.Equal(0, secrets.Reads);
    }

    [Fact]
    public async Task MissingOrUnreadableCredentialDoesNotStartNetworkOrExposeStoreError()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not access network"));
        using var http = new HttpClient(handler);
        foreach (var store in new[] { new Secrets(null), new Secrets(throws: true) })
        {
            var result = await new AnthropicModelProvider(store, http).TestConnectionAsync(Profile, CancellationToken.None);
            Assert.False(result.Connected);
            Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(result));
        }
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreCancellationSkipsSecretsAndInFlightCancellationStopsSdkWithoutRetry()
    {
        var secrets = new Secrets();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnthropicModelProvider(secrets).OpenAsync(Profile, cancelled.Token));
        Assert.Equal(0, secrets.Reads);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ModelResponse(); });
        using var http = new HttpClient(handler);
        using var stop = new CancellationTokenSource();
        var opening = new AnthropicModelProvider(secrets, http).OpenAsync(Profile, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ActualSdkStreamsThroughExistingRuntimeHostAndSqliteWithConfiguredModelAndUsage()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return ModelResponse();
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(Profile.ModelId, body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(123, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(0.25, body.RootElement.GetProperty("temperature").GetDouble());
            return TextStream("Claude SDK local result");
        });
        using var http = new HttpClient(handler);
        var profile = Profile with { Capabilities = new(SupportsToolCalling: true), GenerationSettings = new(0.25f, 123) };
        using var database = new TemporaryAgentDatabase();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider =>
                new MafAgentRuntime(new AnthropicModelProvider(new Secrets(), http), profile, requiresDesktop: false,
                    provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync))).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic Claude request", new("test"), ModelProfileId: profile.Id));
            var final = await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunStatus.Completed, final.Status);
            Assert.Equal("Claude SDK local result", final.Summary);
            var stored = await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id);
            Assert.Equal(new AgentUsage(7, 3), stored!.Result!.Usage);
            Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(stored));
        }
        finally { await host.StopAsync(); }
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database.DatabasePath)));
    }

    [Fact]
    public void AppComposesAnthropicWithoutChangingTheDefaultLocalRuntimeOrOpenAiRegistration()
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders()).Build();
        Assert.IsType<AnthropicModelProvider>(host.Services.GetRequiredService<AnthropicModelProvider>());
        Assert.IsType<OpenAiModelProvider>(host.Services.GetRequiredService<IModelProvider>());
        Assert.IsType<FakeAgentRuntime>(host.Services.GetRequiredService<IAgentRuntime>());
    }

    internal static HttpResponseMessage ModelResponse() => JsonResponse(new
    { id = "canonical-claude-id", type = "model", display_name = "Synthetic Claude", created_at = "2026-01-01T00:00:00Z" });
    internal static HttpResponseMessage JsonResponse(object body, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage TextStream(string text, string finish = "end_turn") => Stream(
        new { type = "message_start", message = new { id = "msg_synthetic", type = "message", role = "assistant", model = Profile.ModelId,
            content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 7, output_tokens = 0 } } },
        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
        new { type = "content_block_stop", index = 0 },
        new { type = "message_delta", delta = new { stop_reason = finish, stop_sequence = (string?)null }, usage = new { output_tokens = 3 } },
        new { type = "message_stop" });
    internal static HttpResponseMessage Stream(params object[] events) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Concat(events.Select(value => "event: " + JsonSerializer.SerializeToElement(value).GetProperty("type").GetString()
        + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n")), Encoding.UTF8, "text/event-stream") };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        public Exception? Failure;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            try { return await send(request, ct); }
            catch (Exception exception) { Failure = exception; throw; }
        }
    }
    internal sealed class Secrets(string? value = SyntheticKey, bool throws = false) : ISecretStore
    {
        public int Reads;
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
        { Reads++; if (throws) throw new InvalidOperationException(SyntheticKey); return ValueTask.FromResult(value); }
    }
}
