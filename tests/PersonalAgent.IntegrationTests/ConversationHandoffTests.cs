using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.IO;

namespace PersonalAgent.IntegrationTests;

public sealed class ConversationHandoffTests
{
    private static readonly SourceLink Issue = new("TEST-106", new("https://jira.example.test/browse/TEST-106"));

    [Fact]
    public async Task ConversationHandoffPreservesIssueSourcesAndDecisionAcrossProviderSwitchRestartAndCompatibleSession()
    {
        using var database = new TemporaryAgentDatabase(); var effectPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "one-sandbox-effect.txt");
        var first = await Initial(database, effectPath); var openai = new Provider("openai");
        var second = await Followup(database, openai, first.Run.ConversationId);
        var anthropic = new Provider("anthropic"); var third = await Followup(database, anthropic, first.Run.ConversationId);
        var fourth = await Followup(database, anthropic, first.Run.ConversationId);
        foreach (var run in new[] { second, third, fourth })
        {
            Assert.Equal(RunStatus.Completed, run.Run.Status); Assert.Equal("Details for TEST-106; owner already notified", run.Result!.Summary);
            Assert.Contains(Issue, run.Request.Context.Sources!); Assert.Contains("Owner notified once", run.Request.Context.DecisionContext); Assert.Empty(run.Request.AllowedToolIds);
        }
        Assert.Contains(anthropic.Messages[^1], message => message.Text.Contains("Untrusted saved conversation data"));
        Assert.All(openai.Messages.Concat(anthropic.Messages), messages =>
        {
            Assert.Contains(messages, message => message.Text.Contains("Untrusted prior source references") && message.Text.Contains(Issue.Url.AbsoluteUri));
            Assert.All(messages, message => Assert.Equal(ChatRole.User, message.Role));
        });
        Assert.Equal(["one confirmed sandbox effect"], await File.ReadAllLinesAsync(effectPath));
        Assert.Equal(1, openai.Opens); Assert.Equal(2, anthropic.Opens);
    }

    [Fact]
    public async Task ConversationHandoffCannotReplayPreviousWriteWhenModelRequestsItAgain()
    {
        using var database = new TemporaryAgentDatabase(); var effectPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "one-sandbox-effect.txt");
        var first = await Initial(database, effectPath); var provider = new Provider("openai") { RequestWrite = true };
        var next = await Followup(database, provider, first.Run.ConversationId);
        Assert.Equal(RunStatus.Blocked, next.Run.Status); Assert.Empty(next.Request.AllowedToolIds);
        Assert.Equal(1, provider.Opens); Assert.Equal(["one confirmed sandbox effect"], await File.ReadAllLinesAsync(effectPath));
        await using var db = database.Open(); Assert.Empty(await db.ToolCalls.ToArrayAsync());
    }

    private static async Task<StoredRun> Initial(TemporaryAgentDatabase database, string effectPath)
    {
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(new InitialRuntime(effectPath))).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Read TEST-106 and notify its owner once", new("test")));
            await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            return (await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id))!;
        }
        finally { await host.StopAsync(); }
    }

    private static async Task<StoredRun> Followup(TemporaryAgentDatabase database, Provider provider, Guid conversation)
    {
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => { services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(provider); }).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>(); var profile = new ModelProfile { Id = "handoff", ProviderId = provider.ProviderId,
                ModelId = "fixture", SecretRef = "synthetic-reference", Capabilities = new(false, true) };
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), "More details about that issue; preserve earlier decisions", new("desktop"), ConversationId: conversation, ModelProfileId: profile.Id, Profile: profile));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (runs.GetResult(run.Id) is null) await Task.Delay(10, timeout.Token);
            return (await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id))!;
        }
        finally { await host.StopAsync(); }
    }

    private sealed class InitialRuntime(string effectPath) : IAgentRuntime
    {
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            await File.AppendAllLinesAsync(effectPath, ["one confirmed sandbox effect"], ct);
            return new() { Status = AgentResultStatus.Completed, Summary = "Owner notified once for TEST-106", Sources = [Issue] };
        }
    }

    private sealed class Provider(string id) : IModelProvider
    {
        public string ProviderId => id;
        public bool RequestWrite; public int Opens;
        public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++;
            return Task.FromResult(new ModelConnection(new ScriptedChatClient((messages, options) =>
            {
                Messages.Add(messages);
                if (RequestWrite) return new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("old-write-again", "notify_owner", new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls };
                var knowsIssue = messages.Any(message => message.Text.Contains("Untrusted prior source references") && message.Text.Contains(Issue.Url.AbsoluteUri));
                var knowsDecision = messages.Any(message => message.Text.Contains("Untrusted completed work") && message.Text.Contains("Owner notified once"));
                return new(new ChatMessage(ChatRole.Assistant, knowsIssue && knowsDecision ? "Details for TEST-106; owner already notified" : "Missing context")) { FinishReason = ChatFinishReason.Stop };
            }), profile));
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }
}
