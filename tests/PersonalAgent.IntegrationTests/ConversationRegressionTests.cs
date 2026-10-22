using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ConversationRegressionTests
{
    private static readonly ApprovalOwner Owner = new("desktop", "conversation-regression-owner");
    private static readonly ChannelRoute Route = new("telegram", "regression-bot", "24");
    private static readonly SourceLink Issue = new("TEST-108", new("https://jira.example.test/browse/TEST-108"));

    [Theory]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    public async Task ConversationSurvivesDesktopChannelRestartAndProviderChangeWithoutDuplicatingConfirmedWrite(string nextProvider, bool repeatWrite)
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var bindings = new SqliteChannelBindingRepository(database);
        var pending = await bindings.RequestPairingAsync(Route, "42", Owner);
        await bindings.SetStatusAsync(pending.Id, pending.Version, Owner, true);
        var effect = new SandboxWrite(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "confirmed-write.txt"));
        StoredRun original;
        using (var host = Build(database, new Provider("openai", initial: true), effect))
        {
            await host.StartAsync();
            try
            {
                var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
                var profile = Profile("openai");
                var root = await runs.SubmitAsync(new(Guid.NewGuid(), "Notify the synthetic TEST-108 owner once", new("desktop"),
                    ModelProfileId: profile.Id, Profile: profile, AllowedToolIds: [effect.Descriptor.Id]));
                original = await Finished(host, root.Id);
                Assert.Equal(RunStatus.Completed, original.Run.Status);
                Assert.Equal(Issue, Assert.Single(original.Result!.Sources));
                await new ChannelConversationReceiptRecorder(Owner, Route, "42", host.Services.GetRequiredService<IChannelConversationRepository>()).RecordAsync(
                    new() { Id = Guid.NewGuid(), Destination = Route, Text = "Synthetic confirmed result", RunId = root.Id, ConversationId = root.ConversationId }, "1001");
            }
            finally { await host.StopAsync(); }
        }
        var incoming = new IncomingMessage { Route = Route, SenderId = "42", ExternalMessageId = "10", TransportMessageId = "2001",
            ReplyToMessageId = "1001", TimestampUtc = DateTimeOffset.UtcNow, Text = "Details about the finding; preserve the earlier completed decision" };
        Guid remoteId;
        var remoteProvider = new Provider(nextProvider, repeatWrite: repeatWrite);
        using (var host = Build(database, remoteProvider, effect))
        {
            await host.StartAsync();
            try
            {
                var router = host.Services.GetRequiredService<ChannelMessageRouter>();
                var remote = await router.SubmitAsync(incoming); remoteId = remote.Id;
                var stored = await Finished(host, remote.Id);
                Assert.Equal(original.Run.ConversationId, remote.ConversationId);
                Assert.Equal("channel/telegram", remote.Origin.Source);
                Assert.Empty(stored.Request.AllowedToolIds);
                Assert.Equal(repeatWrite ? RunStatus.Blocked : RunStatus.Completed, stored.Run.Status);
                if (repeatWrite) Assert.Equal("ToolNotAllowed", stored.Result!.ErrorCode);
                else Assert.Equal("TEST-108 details; owner notified once", stored.Result!.Summary);
                var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => router.SubmitAsync(incoming)));
                Assert.All(duplicates, run => Assert.Equal(remote.Id, run.Id));
                Assert.Equal(1, remoteProvider.Opens);
            }
            finally { await host.StopAsync(); }
        }
        var desktopProvider = new Provider(nextProvider);
        using (var host = Build(database, desktopProvider, effect))
        {
            await host.StartAsync();
            try
            {
                // Re-delivery after another host restart must not re-execute even a blocked reply.
                var duplicate = await host.Services.GetRequiredService<ChannelMessageRouter>().SubmitAsync(incoming);
                Assert.Equal(remoteId, duplicate.Id); Assert.Equal(0, desktopProvider.Opens);
                var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); var profile = Profile(nextProvider);
                var continuation = await runs.SubmitAsync(new(Guid.NewGuid(), "Continue the same finding on Desktop", new("desktop"),
                    ConversationId: original.Run.ConversationId, ModelProfileId: profile.Id, Profile: profile));
                var stored = await Finished(host, continuation.Id);
                Assert.Equal(RunStatus.Completed, stored.Run.Status);
                Assert.Equal("TEST-108 details; owner notified once", stored.Result!.Summary);
                Assert.Contains(Issue, stored.Request.Context.Sources!); Assert.Empty(stored.Request.AllowedToolIds);
                Assert.Equal(1, desktopProvider.Opens);
                Assert.Equal(3, (await host.Services.GetRequiredService<IRunRepository>().ListAsync()).Count);
                Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await host.Services.GetRequiredService<IRunRepository>().GetAsync(original.Run.Id)));
            }
            finally { await host.StopAsync(); }
        }
        Assert.Equal(1, effect.Calls);
        Assert.Equal(["one confirmed synthetic write"], await File.ReadAllLinesAsync(effect.Path));
        Assert.All(remoteProvider.Inputs.Concat(desktopProvider.Inputs), messages =>
        {
            Assert.All(messages, message => Assert.Equal(ChatRole.User, message.Role));
            Assert.Contains(messages, message => message.Text.Contains(Issue.Url.AbsoluteUri));
            Assert.Contains(messages, message => message.Text.Contains("owner notified once", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(messages.SelectMany(message => message.Contents), content => content is FunctionCallContent or FunctionResultContent);
        });
    }

    private static ModelProfile Profile(string provider) => new() { Id = "conversation-regression", ProviderId = provider,
        ModelId = "fixture", SecretRef = "synthetic-reference", Capabilities = new(false, true) };

    private static IHost Build(TemporaryAgentDatabase database, Provider provider, SandboxWrite effect) => AppHost.CreateBuilder([], database.DatabasePath)
        .ConfigureLogging(logging => logging.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton(Owner);
            services.AddSingleton<ITaskCommands>(services => new Commands(services.GetRequiredService<TaskRunCoordinator>(), Profile(provider.ProviderId)));
            services.AddSingleton<IAgentRuntime>(services => new Runtime(services.GetRequiredService<TaskRunCoordinator>(),
                services.GetRequiredService<IConversationRepository>(), provider, effect));
        }).Build();

    private static async Task<StoredRun> Finished(IHost host, Guid id)
    {
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (runs.GetResult(id) is null) await Task.Delay(10, timeout.Token);
        return (await host.Services.GetRequiredService<IRunRepository>().GetAsync(id))!;
    }

    private sealed class Runtime(TaskRunCoordinator runs, IConversationRepository sessions, Provider provider, SandboxWrite effect) : IAgentRuntime
    {
        public Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            // Explicit trusted fixture grants only the first sandbox action. Channels/history grant nothing.
            var registry = new ToolRegistry([effect], runs.WaitAtBoundaryAsync, (_, _, _) => ValueTask.FromResult(true));
            return new MafAgentRuntime(provider, request.Profile!, registry, runs.WaitAtBoundaryAsync, sessions: sessions).ExecuteAsync(request, progress, ct);
        }
    }
    private sealed class Provider(string id, bool initial = false, bool repeatWrite = false) : IModelProvider
    {
        public string ProviderId => id;
        public int Opens;
        public List<IReadOnlyList<ChatMessage>> Inputs { get; } = [];
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++;
            ChatResponse First(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
            {
                Inputs.Add(messages);
                Assert.Equal(initial ? 1 : 0, options!.Tools?.Count ?? 0);
                if (initial || repeatWrite) return new(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("notify-once", "sandbox_notify", new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls };
                return Final();
            }
            var client = initial ? new ScriptedChatClient(First, (_, _) => Final()) : new ScriptedChatClient(First);
            return Task.FromResult(new ModelConnection(client, profile));
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct) => throw new NotSupportedException();
        private static ChatResponse Final() => new(new ChatMessage(ChatRole.Assistant, "TEST-108 details; owner notified once")) { FinishReason = ChatFinishReason.Stop };
    }
    private sealed class SandboxWrite(string path) : IAgentTool
    {
        public string Path => path;
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("sandbox_notify", "Synthetic local sandbox only",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), new(ToolSourceKind.Local, "regression"), ToolRiskLevel.Write);
        public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await File.AppendAllLinesAsync(path, ["one confirmed synthetic write"], ct);
            return new() { Status = ToolResultStatus.Success, Text = "Owner notified once for TEST-108", Sources = [Issue] };
        }
    }
    private sealed class Commands(TaskRunCoordinator runs, ModelProfile profile) : ITaskCommands
    {
        public Task<TaskRun> SubmitAsync(SubmitTaskCommand command, CancellationToken ct = default) =>
            runs.SubmitAsync(command with { ModelProfileId = profile.Id, Profile = profile }, ct);
        public Task<TaskRun> CancelAsync(CancelRunCommand command, CancellationToken ct = default) => runs.CancelAsync(command, ct);
        public Task<TaskRun> PauseAsync(PauseRunCommand command, CancellationToken ct = default) => runs.PauseAsync(command, ct);
        public Task<TaskRun> ResumeAsync(ResumeRunCommand command, CancellationToken ct = default) => runs.ResumeAsync(command, ct);
        public Task<TaskRun> SkipAsync(SkipRunCommand command, CancellationToken ct = default) => runs.SkipAsync(command, ct);
    }
}
