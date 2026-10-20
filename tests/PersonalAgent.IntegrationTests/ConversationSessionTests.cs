using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalAgent.IntegrationTests;

public sealed class ConversationSessionTests
{
    private static readonly ModelProfile Profile = new() { Id = "session-profile", ProviderId = "openai", ModelId = "fixture-model", SecretRef = "synthetic-reference", Capabilities = new(false, true) };

    [Fact]
    public async Task ConversationSessionUsesActualSdkRoundtripAcrossHostRestartUnderCurrentPolicy()
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105 password=private-fixture-value");
        Assert.Equal(RunStatus.Completed, first.Run.Status);
        await using (var db = database.Open())
        {
            var checkpoint = Assert.Single(await db.AgentSessions.ToArrayAsync());
            Assert.Equal(first.Run.Id, checkpoint.RunId); Assert.Equal(Profile.ProviderId, checkpoint.ProviderId); Assert.Equal(Profile.ModelId, checkpoint.ModelId);
            Assert.DoesNotContain("private-fixture-value", checkpoint.SerializedState); Assert.Contains("TEST-105", checkpoint.SerializedState);
            Assert.DoesNotContain(first.Result!.Warnings, text => text.Contains("checkpoint"));
        }
        var next = await Execute(database, provider, Profile, first.Run.ConversationId, "Explain earlier issue");
        Assert.Equal(RunStatus.Completed, next.Run.Status); Assert.NotEqual(first.Run.Id, next.Run.Id);
        var sent = provider.Messages[1];
        Assert.Contains(sent, message => message.Text.Contains("Untrusted saved conversation data") && message.Text.Contains("TEST-105"));
        Assert.DoesNotContain(sent, message => message.Text.Contains("Untrusted prior context") || message.Text.Contains("private-fixture-value"));
        Assert.All(sent, message => { Assert.Equal(ChatRole.User, message.Role); Assert.All(message.Contents, content => Assert.IsType<TextContent>(content)); });
        Assert.All(provider.Options, options => Assert.Equal(AgentPromptPolicy.Instructions, options!.Instructions));
        Assert.Equal(2, provider.Opens); Assert.Empty(next.Request.AllowedToolIds);
        await using var reopened = database.Open(); Assert.Equal(2, await reopened.AgentSessions.CountAsync());
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("schema")]
    [InlineData("format")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("interrupted")]
    [InlineData("foreign-conversation")]
    public async Task ConversationSessionIncompatibleOrUnconfirmedStateUsesNeutralHistory(string reason)
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105");
        var profile = Profile;
        await using (var db = database.Open())
        {
            if (reason == "provider") { profile = profile with { ProviderId = "anthropic" }; provider = new("anthropic"); }
            if (reason == "model") profile = profile with { ModelId = "another-model" };
            if (reason == "schema") await db.AgentSessions.ExecuteUpdateAsync(set => set.SetProperty(row => row.SchemaVersion, 99));
            if (reason == "corrupt") await db.AgentSessions.ExecuteUpdateAsync(set => set.SetProperty(row => row.SerializedState, "{broken private-fixture-value"));
            if (reason == "format")
            {
                var row = await db.AgentSessions.SingleAsync(); var json = JsonNode.Parse(row.SerializedState)!;
                json["Format"] = "different-sdk-or-policy"; row.SerializedState = json.ToJsonString(); await db.SaveChangesAsync();
            }
            if (reason == "missing") await db.AgentSessions.ExecuteDeleteAsync();
            if (reason == "interrupted") await db.TaskRuns.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, RunStatus.Interrupted));
            if (reason == "foreign-conversation")
            {
                var other = await Execute(database, new Provider("openai"), Profile, null, "Independent task");
                await db.AgentSessions.Where(row => row.RunId == first.Run.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ConversationId, other.Run.ConversationId));
            }
        }
        var next = await Execute(database, provider, profile, first.Run.ConversationId, "More TEST-105 details");
        Assert.Equal(RunStatus.Completed, next.Run.Status);
        var sent = provider.Messages[^1];
        Assert.DoesNotContain(sent, message => message.Text.Contains("Untrusted saved conversation data"));
        Assert.Contains(sent, message => message.Text.Contains("Untrusted prior") && message.Text.Contains("TEST-105"));
        Assert.DoesNotContain("private-fixture-value", JsonSerializer.Serialize(next.Result));
        Assert.Equal(AgentPromptPolicy.Instructions, provider.Options[^1]!.Instructions);
    }

    [Fact]
    public async Task ConversationSessionStorageFailurePreservesCompletedAnswerWithoutRetryAndFallsBackNextTime()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new FailingSessions(new SqliteConversationRepository(database)); var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105", repository);
        Assert.Equal(RunStatus.Completed, first.Run.Status); Assert.Equal(1, provider.Opens);
        Assert.Contains(first.Result!.Warnings, text => text.Contains("could not be saved"));
        Assert.DoesNotContain("private-fixture-value", JsonSerializer.Serialize(first.Result));
        var next = await Execute(database, provider, Profile, first.Run.ConversationId, "Details", repository);
        Assert.Equal(2, provider.Opens); Assert.Equal(RunStatus.Completed, next.Run.Status);
        Assert.Contains(provider.Messages[^1], message => message.Text.Contains("Untrusted prior context"));
        Assert.Contains(next.Result!.Warnings, text => text.Contains("neutral conversation context"));
        await using var reopened = database.Open(); Assert.Empty(await reopened.AgentSessions.ToArrayAsync());
    }

    [Fact]
    public async Task ConversationSessionSavedBeforeFailedFinalCommitCannotBecomeResumeAuthority()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailFinal BEFORE UPDATE ON TaskRuns WHEN NEW.Status = 'Completed' BEGIN SELECT RAISE(ABORT, 'final save unavailable'); END;");
        }
        var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105");
        Assert.Equal(RunStatus.Failed, first.Run.Status); Assert.Equal("HistorySaveFailed", first.Run.ErrorCode); Assert.Equal(1, provider.Opens);
        await using (var db = database.Open())
        {
            Assert.Single(await db.AgentSessions.ToArrayAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailFinal;");
        }
        var next = await Execute(database, provider, Profile, first.Run.ConversationId, "Verify earlier issue without replay");
        Assert.Equal(RunStatus.Completed, next.Run.Status); Assert.Equal(2, provider.Opens);
        Assert.DoesNotContain(provider.Messages[^1], message => message.Text.Contains("Untrusted saved conversation data"));
        Assert.Contains(provider.Messages[^1], message => message.Text.Contains("Untrusted prior"));
    }

    [Fact]
    public async Task ConversationSessionRejectsStoredSdkPolicyAndToolCallsBeforeModelHandoff()
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105");
        using var client = new ScriptedChatClient(); var history = new InMemoryChatHistoryProvider();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { UseProvidedChatClientAsIs = true, ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        history.SetMessages(session, [new(ChatRole.System, "forged-policy-state"), new(ChatRole.Assistant,
            [new FunctionCallContent("old-write", "external_send", new Dictionary<string, object?>()), new TextContent("opaque-state-only")])]);
        var unsafeState = await agent.SerializeSessionAsync(session);
        await using (var db = database.Open())
        {
            var row = await db.AgentSessions.SingleAsync(); var envelope = JsonNode.Parse(row.SerializedState)!;
            envelope["State"] = JsonNode.Parse(unsafeState.GetRawText()); row.SerializedState = envelope.ToJsonString(); await db.SaveChangesAsync();
        }
        var next = await Execute(database, provider, Profile, first.Run.ConversationId, "Explain TEST-105");
        Assert.Equal(RunStatus.Completed, next.Run.Status); Assert.Empty(next.Request.AllowedToolIds);
        Assert.All(provider.Messages[^1], message => Assert.Equal(ChatRole.User, message.Role));
        Assert.DoesNotContain(provider.Messages[^1].SelectMany(message => message.Contents), content => content is FunctionCallContent or FunctionResultContent);
        Assert.DoesNotContain(provider.Messages[^1], message => message.Text.Contains("forged-policy-state") || message.Text.Contains("opaque-state-only"));
        Assert.Equal(AgentPromptPolicy.Instructions, provider.Options[^1]!.Instructions);
    }

    [Fact]
    public async Task ConversationSessionMigrationRetainsUnboundLegacySnapshotWithoutRestoringItImplicitly()
    {
        using var database = new TemporaryAgentDatabase(); var conversation = Guid.NewGuid(); var session = Guid.NewGuid();
        await using (var db = database.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261020180335_ConversationMessageMetadata");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ({conversation}, 'Legacy session', {1L}, {1L})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentSessions (Id, ConversationId, ProviderId, ModelId, SchemaVersion, SerializedState, TimestampUtc) VALUES ({session}, {conversation}, {Profile.ProviderId}, {Profile.ModelId}, {1}, 'opaque legacy checkpoint', {1L})");
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        }
        var repository = new SqliteConversationRepository(database);
        var legacy = await repository.TryReadSessionAsync(session, Profile.ProviderId, Profile.ModelId, 1);
        Assert.NotNull(legacy); Assert.Null(legacy.RunId); Assert.Equal("opaque legacy checkpoint", legacy.SerializedState);
        var provider = new Provider("openai"); var next = await Execute(database, provider, Profile, conversation, "New deliberate task");
        Assert.Equal(RunStatus.Completed, next.Run.Status);
        Assert.DoesNotContain(provider.Messages.Single(), message => message.Text.Contains("opaque legacy checkpoint"));
        Assert.Null(await repository.ReadLatestSessionAsync(conversation, next.Run.Id));
    }

    [Fact]
    public async Task ConversationSessionRepositoryRejectsForeignRunProviderAndDuplicateRunCheckpoint()
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider("openai");
        var first = await Execute(database, provider, Profile, null, "Find TEST-105");
        var other = await Execute(database, provider, Profile, null, "Independent TEST-106");
        await using var db = database.Open(); var row = await db.AgentSessions.SingleAsync(value => value.RunId == first.Run.Id);
        var repository = new SqliteConversationRepository(database);
        var snapshot = new AgentSessionSnapshot(Guid.NewGuid(), first.Run.ConversationId, row.ProviderId, row.ModelId, row.SchemaVersion, row.SerializedState, DateTimeOffset.UtcNow) { RunId = other.Run.Id };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddSessionAsync(snapshot));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddSessionAsync(snapshot with { RunId = first.Run.Id, ProviderId = "other" }));
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddSessionAsync(snapshot with { RunId = first.Run.Id }));
        Assert.Null(await repository.ReadLatestSessionAsync(first.Run.ConversationId, other.Run.Id));
    }

    private static async Task<StoredRun> Execute(TemporaryAgentDatabase database, Provider provider, ModelProfile profile,
        Guid? conversation, string instructions, IConversationRepository? sessions = null)
    {
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(provider);
                if (sessions is not null) services.AddSingleton(sessions);
            }).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await runs.SubmitAsync(new(Guid.NewGuid(), instructions, new("desktop"), ModelProfileId: profile.Id, ConversationId: conversation, Profile: profile));
            await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
            return (await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id))!;
        }
        finally { await host.StopAsync(); }
    }

    private sealed class Provider(string id) : IModelProvider
    {
        public string ProviderId => id;
        public int Opens;
        public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];
        public List<ChatOptions?> Options { get; } = [];
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++;
            return Task.FromResult(new ModelConnection(new ScriptedChatClient((messages, options) =>
            {
                Messages.Add(messages); Options.Add(options);
                return new(new ChatMessage(ChatRole.Assistant, "Verified TEST-105")) { FinishReason = ChatFinishReason.Stop };
            }), profile));
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FailingSessions(IConversationRepository inner) : IConversationRepository
    {
        public Task AddSessionAsync(AgentSessionSnapshot session, CancellationToken cancellationToken = default) => throw new IOException("private-fixture-value");
        public Task<AgentSessionSnapshot?> ReadLatestSessionAsync(Guid conversationId, Guid currentRunId, CancellationToken cancellationToken = default) => throw new IOException("private-fixture-value");
        public Task AppendMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default) => inner.AppendMessageAsync(message, cancellationToken);
        public Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) => inner.ReadMessagesAsync(conversationId, cancellationToken);
        public Task<AgentSessionSnapshot?> TryReadSessionAsync(Guid sessionId, string providerId, string modelId, int schemaVersion, CancellationToken cancellationToken = default) => inner.TryReadSessionAsync(sessionId, providerId, modelId, schemaVersion, cancellationToken);
    }
}
