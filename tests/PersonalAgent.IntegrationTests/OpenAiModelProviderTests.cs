using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class OpenAiModelProviderTests
{
    private const string SyntheticKey = "synthetic-test-key-not-a-credential";
    private static ModelProfile Profile => new() { Id = "local-openai-test", ProviderId = "openai", ModelId = "configured-model", SecretRef = "test-reference" };

    [Fact]
    public async Task TestConnectionUsesRealSdkModelEndpointAndAuthorizationWithoutGenerationOrSecretInLogs()
    {
        using var logs = new SdkEvents();
        using var handler = new Handler((request, ct) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.openai.com/v1/models/configured-model", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(SyntheticKey, request.Headers.Authorization.Parameter);
            return Task.FromResult(ModelResponse());
        });
        using var http = new HttpClient(handler);
        IModelProvider provider = new OpenAiModelProvider(new Secrets(), http);
        var test = await provider.TestConnectionAsync(Profile, CancellationToken.None);
        Assert.True(test.Connected); Assert.Null(test.ErrorCode); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(test));
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(Profile));
        Assert.DoesNotContain(SyntheticKey, logs.Text);
    }

    [Theory]
    [InlineData(401, "ProviderAuthenticationFailed")]
    [InlineData(403, "ProviderAuthenticationFailed")]
    [InlineData(404, "ModelUnavailable")]
    [InlineData(429, "ProviderRateLimited")]
    [InlineData(500, "ProviderUnavailable")]
    public async Task HttpFailureIsSanitizedAndNeverRetried(int status, string code)
    {
        using var logs = new SdkEvents();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = SyntheticKey, type = "test_error", code = "test" } }), Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(handler);
        var provider = new OpenAiModelProvider(new Secrets(), http);
        var test = await provider.TestConnectionAsync(Profile, CancellationToken.None);
        Assert.False(test.Connected); Assert.Equal(code, test.ErrorCode); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(test));
        Assert.DoesNotContain(SyntheticKey, logs.Text);
    }

    [Fact]
    public async Task MissingOrUnreadableSecretFailsBeforeNetworkAndDoesNotExposeStoreError()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network must not start."));
        using var http = new HttpClient(handler);
        foreach (var store in new[] { new Secrets(null), new Secrets(throws: true) })
        {
            var test = await new OpenAiModelProvider(store, http).TestConnectionAsync(Profile, CancellationToken.None);
            Assert.False(test.Connected); Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(test));
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("provider")][InlineData("schema")][InlineData("model")][InlineData("temperature")][InlineData("tokens")]
    public async Task InvalidProfileFailsBeforeCredentialOrNetworkAccess(string invalid)
    {
        var profile = invalid switch
        {
            "provider" => Profile with { ProviderId = "unknown" },
            "schema" => Profile with { SchemaVersion = 999 },
            "model" => Profile with { ModelId = "../../outside" },
            "temperature" => Profile with { GenerationSettings = new(float.NaN) },
            _ => Profile with { GenerationSettings = new(MaxOutputTokens: 0) }
        };
        var store = new Secrets();
        var result = await new OpenAiModelProvider(store).TestConnectionAsync(profile, CancellationToken.None);
        Assert.False(result.Connected); Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task PreCancellationSkipsSecretAndInFlightCancellationStopsSdkWithoutRetry()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var store = new Secrets();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiModelProvider(store).OpenAsync(Profile, stop.Token));
        Assert.Equal(0, store.Reads);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ModelResponse(); });
        using var http = new HttpClient(handler);
        using var running = new CancellationTokenSource();
        var open = new OpenAiModelProvider(store, http).OpenAsync(Profile, running.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); running.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RealSdkClientStreamsThroughMafRuntimeUsingProfileModelAndNoRetry()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return ModelResponse();
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            Assert.Equal("configured-model", document.RootElement.GetProperty("model").GetString());
            Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
            const string events = "data: {\"id\":\"completion-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"configured-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"SDK local result\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"completion-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"configured-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var provider = new OpenAiModelProvider(new Secrets(), http);
        using var database = new Fakes.TemporaryAgentDatabase();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IModelProvider>(provider);
                services.AddSingleton<IAgentRuntime>(services => new MafAgentRuntime(
                    services.GetRequiredService<IModelProvider>(), Profile with { Capabilities = new(SupportsToolCalling: true) }, requiresDesktop: false,
                    services.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync));
            }).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic local request", new("test"), ModelProfileId: Profile.Id));
            var result = await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunStatus.Completed, result.Status); Assert.Equal("SDK local result", result.Summary);
            var stored = await host.Services.GetRequiredService<PersonalAgent.Core.Persistence.IRunRepository>().GetAsync(run.Id);
            Assert.DoesNotContain(SyntheticKey, JsonSerializer.Serialize(stored));
        }
        finally { await host.StopAsync(); }
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database.DatabasePath)));
    }

    [Fact]
    public async Task ProviderFailureBecomesExplainableBlockedRunAndDefaultHostKeepsLocalFake()
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders()).Build();
        Assert.IsType<OpenAiModelProvider>(host.Services.GetRequiredService<IModelProvider>());
        Assert.IsType<WindowsDpapiSecretStore>(host.Services.GetRequiredService<ISecretStore>());
        Assert.IsType<FakeAgentRuntime>(host.Services.GetRequiredService<IAgentRuntime>());
        var provider = new OpenAiModelProvider(new Secrets(null));
        var runtime = new MafAgentRuntime(async (_, ct) => (await provider.OpenAsync(Profile, ct)).Client, (_, _) => Task.CompletedTask);
        var result = await runtime.ExecuteAsync(new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = Profile.Id, Instructions = "Test" }, new InlineProgress(), CancellationToken.None);
        Assert.Equal(AgentResultStatus.Blocked, result.Status); Assert.Equal("SecretUnavailable", result.ErrorCode);
    }

    [Fact]
    public async Task DpapiPersistsOnlyCiphertextAndReopensWithOpaqueReferenceForCurrentWindowsOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PersonalAgent.SecretTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowsDpapiSecretStore(directory);
            var reference = await store.StoreAsync(SyntheticKey);
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.DoesNotContain(SyntheticKey, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            Assert.Equal(SyntheticKey, await new WindowsDpapiSecretStore(directory).ReadAsync(reference));
            Assert.Null(await store.ReadAsync("dpapi:" + Guid.NewGuid().ToString("N")));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadAsync("dpapi:../../outside"));
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(async () => await store.ReadAsync(reference));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static HttpResponseMessage ModelResponse() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"id\":\"configured-model\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"}", Encoding.UTF8, "application/json") };
    private sealed class InlineProgress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class Secrets(string? value = SyntheticKey, bool throws = false) : ISecretStore
    {
        public int Reads;
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
        { Reads++; if (throws) throw new InvalidOperationException(SyntheticKey); return ValueTask.FromResult(value); }
    }
    private sealed class SdkEvents : EventListener
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> events = new();
        public string Text => string.Join("\n", events);
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name.StartsWith("System-ClientModel", StringComparison.Ordinal)) EnableEvents(source, EventLevel.Verbose); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { if (data.Payload is not null) foreach (var value in data.Payload) events.Enqueue(value?.ToString() ?? ""); }
    }
}
