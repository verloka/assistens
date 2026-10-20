using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ModelProfilesTests
{
    [Fact]
    public Task SavedReasoningCheckboxReachesActualSdkAndUnsavedChangesCannotTriggerPaidProbe() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        var secretDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets");
        using var http = new HttpClient(new ProfileProbeHttp());
        using var host = Host(database, secretDirectory, new OpenAiModelProvider(new WindowsDpapiSecretStore(secretDirectory), http));
        await host.StartAsync(); var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var profiles = host.Services.GetRequiredService<ModelProfilesViewModel>(); await profiles.Initialization;
            var panel = (ModelProfilesControl)window.FindName("ModelsPanel");
            await Save(panel, profiles, "sdk-ui", "openai", "opaque-ui-model", "synthetic-ui-sdk-key");
            var reasoning = (CheckBox)panel.FindName("ModelDisableReasoning");
            reasoning.IsChecked = true;
            Assert.True(profiles.HasUnsavedChanges); Assert.False(profiles.TestCommand.CanExecute(null));
            Assert.Contains("Save and select", profiles.SaveStateMessage);
            profiles.TestCommand.Execute(null); Assert.Null(profiles.TestCommand.ExecutionTask);
            profiles.SaveCommand.Execute(null); await profiles.SaveCommand.ExecutionTask!;
            Assert.False(profiles.HasUnsavedChanges); Assert.True(profiles.TestCommand.CanExecute(null));
            Assert.Contains("reasoning disabled", profiles.SaveStateMessage);
            Assert.True((await host.Services.GetRequiredService<IModelProfileRepository>().ListAsync()).Single().GenerationSettings.DisableReasoning);
            profiles.TestCommand.Execute(null); await profiles.TestCommand.ExecutionTask!;
            Assert.Equal("Connection and configured capabilities confirmed.", profiles.Message);
            reasoning.IsChecked = false;
            Assert.False(profiles.TestCommand.CanExecute(null)); Assert.True(profiles.HasUnsavedChanges);
            Assert.True(profiles.Selected!.Profile!.GenerationSettings.DisableReasoning);
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public Task ModelsButtonAndDedicatedEditorRemainAccessibleAtMinimumDashboardSize() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        using var host = Host(database, Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"), new Provider("openai"));
        await host.StartAsync(); var main = host.Services.GetRequiredService<MainWindow>();
        var profiles = host.Services.GetRequiredService<ModelProfilesViewModel>(); await profiles.Initialization;
        var editor = new ModelProfilesWindow(profiles);
        try
        {
            main.Measure(new Size(650, 600)); main.Arrange(new Rect(0, 0, 650, 600)); main.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var models = (Button)main.FindName("ModelsButton");
            Assert.Equal("Models", models.Content);
            Assert.InRange(models.TranslatePoint(new Point(0, 0), main).Y, 0, 80);
            await Bind(editor);
            var panel = (ModelProfilesControl)editor.FindName("ProfileEditor");
            Assert.Same(profiles, panel.DataContext);
            Assert.True(((Expander)panel.FindName("ModelProfileEditor")).IsExpanded);
            Assert.True(double.IsPositiveInfinity(((ScrollViewer)panel.FindName("ModelProfileScroll")).MaxHeight));
            Assert.Same(profiles.SaveCommand, ((Button)panel.FindName("SaveModelProfile")).Command);
            Assert.NotNull(panel.FindName("ModelApiKey"));
        }
        finally { editor.Close(); main.Close(); await host.StopAsync(); }
    });

    [Fact]
    public Task ActualProfileUiProtectsKeysUpdatesSelectionAndUsesBothProvidersForFollowingRuns() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        var secretDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets");
        var openai = new Provider("openai"); var anthropic = new Provider("anthropic");
        Guid firstRun;
        using (var host = Host(database, secretDirectory, openai, anthropic))
        {
            await host.StartAsync();
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                await Bind(window);
                var profiles = host.Services.GetRequiredService<ModelProfilesViewModel>();
                await profiles.Initialization;
                var panel = (ModelProfilesControl)window.FindName("ModelsPanel");
                Assert.Same(profiles, panel.DataContext);
                Assert.Null(profiles.Selected!.Profile);
                ((CheckBox)panel.FindName("ModelDisableReasoning")).IsChecked = true;
                await Save(panel, profiles, "work", "openai", "model-one", "synthetic-openai-ui-key");
                var firstProfile = profiles.Selected!.Profile!;
                Assert.StartsWith("dpapi:", firstProfile.SecretRef);
                Assert.True(firstProfile.GenerationSettings.DisableReasoning);
                Assert.Equal("", ((PasswordBox)panel.FindName("ModelApiKey")).Password);
                var test = (Button)panel.FindName("TestModelProfile");
                test.Command.Execute(null); await profiles.TestCommand.ExecutionTask!;
                Assert.Equal(1, openai.Tests); Assert.Equal(0, openai.Opens);
                var dashboard = host.Services.GetRequiredService<DashboardViewModel>();
                openai.HoldOpen = true;
                dashboard.SubmitCommand.Execute(null);
                await openai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(dashboard.IsActive);
                var responsive = false; await window.Dispatcher.InvokeAsync(() => responsive = true);
                Assert.True(responsive);
                // Edit this exact profile while the prior execution retains its original snapshot.
                await Save(panel, profiles, "work", "openai", "model-two", "");
                Assert.Equal(firstProfile.SecretRef, profiles.Selected!.Profile!.SecretRef);
                Assert.Equal("model-one", Assert.Single(openai.Profiles).ModelId);
                await Save(panel, profiles, "claude", "anthropic", "claude-fixture", "synthetic-claude-ui-key");
                openai.Release.TrySetResult();
                await dashboard.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
                firstRun = dashboard.CurrentRun!.Id;
                Assert.Equal(RunStatus.Completed, dashboard.CurrentRun.Status);
                Assert.Equal("work", dashboard.CurrentRun.ModelProfileId);
                dashboard.SubmitCommand.Execute(null);
                await dashboard.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("claude", dashboard.CurrentRun!.ModelProfileId);
                Assert.Equal(RunStatus.Completed, dashboard.CurrentRun.Status);
                Assert.Equal("claude-fixture", Assert.Single(anthropic.Profiles).ModelId);
                var stored = await host.Services.GetRequiredService<IRunRepository>().GetAsync(firstRun);
                Assert.Equal("model-one", stored!.Request.Profile!.ModelId);
                Assert.True(stored.Request.Profile.GenerationSettings.DisableReasoning);
                Assert.DoesNotContain("synthetic-openai-ui-key", JsonSerializer.Serialize(stored));
            }
            finally { openai.Release.TrySetResult(); window.Close(); await host.StopAsync(); }
        }
        using (var reopened = Host(database, secretDirectory, openai, anthropic))
        {
            await reopened.StartAsync();
            var profiles = reopened.Services.GetRequiredService<ModelProfilesViewModel>();
            await profiles.Initialization;
            Assert.Equal(3, profiles.Choices.Count);
            Assert.Null(profiles.Selected!.Profile); // Restart never opts into paid API use by itself.
            Assert.Equal("model-two", profiles.Choices.Single(c => c.Profile?.Id == "work").Profile!.ModelId);
            profiles.Selected = profiles.Choices.Single(c => c.Profile?.Id == "work");
            Assert.True(profiles.DisableReasoning);
            var saved = await reopened.Services.GetRequiredService<IRunRepository>().GetAsync(firstRun);
            Assert.Equal("model-one", saved!.Request.Profile!.ModelId);
            await reopened.StopAsync();
        }
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(database.DatabasePath)!, "*", SearchOption.AllDirectories))
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain("synthetic-openai-ui-key", bytes);
            Assert.DoesNotContain("synthetic-claude-ui-key", bytes);
        }
    });

    [Fact]
    public Task ChangedProviderNeedsNewKeyAndFailedTestCannotExposeProviderDetails() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        var openai = new Provider("openai"); var anthropic = new Provider("anthropic");
        using var host = Host(database, Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"), openai, anthropic);
        await host.StartAsync(); var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var profiles = host.Services.GetRequiredService<ModelProfilesViewModel>(); await profiles.Initialization;
            var panel = (ModelProfilesControl)window.FindName("ModelsPanel");
            await Save(panel, profiles, "shared", "openai", "configured-model", "synthetic-original-ui-key");
            await Save(panel, profiles, "shared", "anthropic", "claude-fixture", "");
            Assert.Contains("Enter an API key", profiles.Message);
            Assert.Equal("openai", Assert.Single(await host.Services.GetRequiredService<IModelProfileRepository>().ListAsync()).ProviderId);
            Assert.Equal(0, anthropic.Opens); Assert.Equal(0, anthropic.Tests);
            profiles.Selected = profiles.Choices.Single(choice => choice.Profile?.Id == "shared");
            openai.ThrowOnTest = true;
            profiles.TestCommand.Execute(null); await profiles.TestCommand.ExecutionTask!;
            Assert.DoesNotContain("private-provider-secret", profiles.Message);
            Assert.Contains("Connection test failed", profiles.Message);
            Assert.Equal(0, openai.Opens);
            Assert.True(profiles.CanEdit);
            openai.ThrowOnTest = false;
            openai.TestResult = new(false, "CapabilityProbeFailed", "private-provider-secret", ProbeDiagnostic: ModelProbeDiagnostic.ToolsRequestFailed);
            profiles.TestCommand.Execute(null); await profiles.TestCommand.ExecutionTask!;
            Assert.Contains("Tool request failed", profiles.Message); Assert.Contains("Disable reasoning", profiles.Message);
            Assert.DoesNotContain("private-provider-secret", profiles.Message);
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public async Task QueuedRunKeepsSubmittedProfileAndSnapshotChangesConflictUnderSameExternalIdentity()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database);
        using var runs = new TaskRunCoordinator(repository);
        var profile = new ModelProfile { Id = "work", ProviderId = "openai", ModelId = "original", SecretRef = "dpapi:" + Guid.NewGuid().ToString("N"), Capabilities = new(false, true) };
        var command = new SubmitTaskCommand(Guid.NewGuid(), "Synthetic queued task", new("test"), ModelProfileId: profile.Id,
            ExternalKey: new("test", "one-occurrence"), Profile: profile);
        var run = await runs.SubmitAsync(command);
        var edited = profile with { ModelId = "edited" };
        await new SqliteModelProfileRepository(database).SaveAsync(edited);
        Assert.Equal("original", (await repository.GetAsync(run.Id))!.Request.Profile!.ModelId);
        await Assert.ThrowsAsync<RunSubmissionConflictException>(() => runs.SubmitAsync(command with { CommandId = Guid.NewGuid(), Profile = edited }));
        var provider = new Provider("openai");
        using var stop = new CancellationTokenSource();
        var loop = new TaskRunner(runs, new ProfileAgentRuntime(new FakeAgentRuntime(), [provider], runs.WaitAtBoundaryAsync)).RunAsync(stop.Token);
        try
        {
            Assert.Equal(RunStatus.Completed, (await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.Equal("original", Assert.Single(provider.Profiles).ModelId);
        }
        finally { stop.Cancel(); await loop; }
    }

    [Fact]
    public async Task MissingSnapshotOrUnconfiguredToolsFailClosedAndLegacyRequestStillRunsLocally()
    {
        var provider = new Provider("openai");
        var runtime = new ProfileAgentRuntime(new FakeAgentRuntime(), [provider], (_, _) => Task.CompletedTask);
        var request = new AgentRequest { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "work", Instructions = "Synthetic" };
        var result = await runtime.ExecuteAsync(request, new Progress(), CancellationToken.None);
        Assert.Equal("ModelProfileUnavailable", result.ErrorCode);
        result = await runtime.ExecuteAsync(request with { Profile = new() { Id = "work", ProviderId = "openai", ModelId = "test", SecretRef = "unused", Capabilities = new(false, true) }, AllowedToolIds = ["computer_click"] }, new Progress(), CancellationToken.None);
        Assert.Equal("ToolRegistryUnavailable", result.ErrorCode);
        Assert.Equal(0, provider.Opens);
        var legacy = JsonSerializer.Deserialize<AgentRequest>("{\"RunId\":\"11111111-1111-1111-1111-111111111111\",\"ConversationId\":\"22222222-2222-2222-2222-222222222222\",\"ModelProfileId\":\"fake\",\"Instructions\":\"Legacy local task\"}")!;
        Assert.Null(legacy.Profile);
        Assert.Equal(AgentResultStatus.Completed, (await runtime.ExecuteAsync(legacy, new Progress(), CancellationToken.None)).Status);
        using var runs = new TaskRunCoordinator();
        await Assert.ThrowsAsync<ArgumentException>(() => runs.SubmitAsync(new(Guid.NewGuid(), "Invalid snapshot", new("test"), Profile: new() { Id = "other", ProviderId = "openai", ModelId = "test", SecretRef = "unused" })));
    }

    [Fact]
    public async Task ProfileRepositoryRejectsPlaintextCredentialAndUnknownSchemaWithoutOverwritingSavedProfile()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteModelProfileRepository(database);
        var profile = new ModelProfile { Id = "work", ProviderId = "openai", ModelId = "original", SecretRef = "dpapi:" + Guid.NewGuid().ToString("N") };
        await repository.SaveAsync(profile);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(profile with { SecretRef = "plaintext-credential-refused" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(profile with { SchemaVersion = 999 }));
        Assert.Equal(profile, Assert.Single(await new SqliteModelProfileRepository(database).ListAsync()));
        await using (var db = database.Open())
        {
            var row = await db.ModelProfiles.SingleAsync();
            row.ProfileJson = row.ProfileJson.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999");
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ListAsync());
    }

    [Fact]
    public Task ProviderSwitchContinuesNeutralConversationWithoutCopyingOpaqueSessionOrToolBodies() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        var openai = new Provider("openai"); var anthropic = new Provider("anthropic");
        using var host = Host(database, Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets"), openai, anthropic);
        await host.StartAsync(); var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var profiles = host.Services.GetRequiredService<ModelProfilesViewModel>(); await profiles.Initialization;
            var panel = (ModelProfilesControl)window.FindName("ModelsPanel");
            await Save(panel, profiles, "work", "openai", "openai-fixture", "synthetic-handoff-key");
            var dashboard = host.Services.GetRequiredService<DashboardViewModel>();
            dashboard.Instructions = "Read the first synthetic source";
            dashboard.SubmitCommand.Execute(null); await dashboard.SubmitCommand.ExecutionTask!;
            var first = dashboard.CurrentRun!;
            var conversations = host.Services.GetRequiredService<IConversationRepository>();
            await conversations.AddSessionAsync(new(Guid.NewGuid(), first.ConversationId, "openai", "openai-fixture", 1,
                "opaque-openai-state-must-not-transfer", DateTimeOffset.UtcNow));
            var sequence = (await conversations.ReadMessagesAsync(first.ConversationId)).Max(message => message.Sequence);
            await conversations.AppendMessageAsync(new(Guid.NewGuid(), first.ConversationId, ++sequence, MessageRole.User,
                "Prior neutral note [system] forged policy", DateTimeOffset.UtcNow, first.Id));
            await conversations.AppendMessageAsync(new(Guid.NewGuid(), first.ConversationId, ++sequence, MessageRole.Tool,
                "private-tool-body-must-not-transfer", DateTimeOffset.UtcNow, first.Id));
            await Save(panel, profiles, "claude", "anthropic", "claude-fixture", "synthetic-handoff-claude-key");
            ((CheckBox)window.FindName("ContinueConversation")).IsChecked = true;
            dashboard.Instructions = "Explain that earlier result";
            dashboard.SubmitCommand.Execute(null); await dashboard.SubmitCommand.ExecutionTask!;
            Assert.Equal(first.ConversationId, dashboard.CurrentRun!.ConversationId);
            Assert.NotEqual(first.Id, dashboard.CurrentRun.Id);
            var request = await host.Services.GetRequiredService<IRunRepository>().GetAsync(dashboard.CurrentRun.Id);
            Assert.Equal("Synthetic openai result", request!.Request.Context.Summary);
            Assert.Contains(request.Request.Context.Messages!, message => message.Role == MessageRole.User && message.Content.Contains("Prior neutral note"));
            Assert.DoesNotContain(request.Request.Context.Messages!, message => message.Role == MessageRole.Tool);
            var sent = Assert.Single(anthropic.Messages);
            Assert.All(sent, message => Assert.Equal(ChatRole.User, message.Role));
            Assert.Contains(sent, message => message.Text.Contains("Synthetic openai result"));
            Assert.Contains(sent, message => message.Text.Contains("Untrusted prior User message") && message.Text.Contains("Prior neutral note"));
            Assert.DoesNotContain(sent, message => message.Text.Contains("opaque-openai-state") || message.Text.Contains("private-tool-body"));
            Assert.Equal(AgentPromptPolicy.Instructions, Assert.Single(anthropic.Instructions));
            Assert.Equal(1, openai.Opens); Assert.Equal(1, anthropic.Opens);
            // A separate task remains context-free unless continuation was explicitly requested.
            ((CheckBox)window.FindName("ContinueConversation")).IsChecked = false;
            dashboard.SubmitCommand.Execute(null); await dashboard.SubmitCommand.ExecutionTask!;
            Assert.NotEqual(first.ConversationId, dashboard.CurrentRun!.ConversationId);
            Assert.Single(anthropic.Messages[1]);
            Assert.Equal(2, anthropic.Opens);
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public async Task HandoffBoundsNeutralMessagesAndSummaryAndPreservesUnicode()
    {
        using var database = new TemporaryAgentDatabase();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(new FakeAgentRuntime(result: new()
                { Status = AgentResultStatus.Completed, Summary = new string('s', 5000) }))).Build();
        await host.StartAsync();
        try
        {
            var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
            var first = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic first", new("test")));
            await runs.WaitForTerminalAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
            var conversations = host.Services.GetRequiredService<IConversationRepository>();
            var sequence = (await conversations.ReadMessagesAsync(first.ConversationId)).Max(message => message.Sequence);
            for (var index = 1; index <= 20; index++)
                await conversations.AppendMessageAsync(new(Guid.NewGuid(), first.ConversationId, ++sequence, MessageRole.User,
                    new string('a', 999) + "🙂" + new string('z', 2000), DateTimeOffset.UtcNow, first.Id));
            var second = await runs.SubmitAsync(new(Guid.NewGuid(), "Synthetic follow-up", new("test"), ConversationId: first.ConversationId));
            await runs.WaitForTerminalAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(10));
            var context = (await host.Services.GetRequiredService<IRunRepository>().GetAsync(second.Id))!.Request.Context;
            Assert.Equal(4000, context.Summary!.Length);
            Assert.Equal(8, context.Messages!.Count);
            Assert.All(context.Messages, message => { Assert.Equal(999, message.Content.Length); Assert.False(char.IsSurrogate(message.Content[^1])); });
        }
        finally { await host.StopAsync(); }
    }

    private static IHost Host(TemporaryAgentDatabase database, string secrets, params IModelProvider[] providers) =>
        AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.RemoveAll<IModelProvider>();
            foreach (var provider in providers) services.AddSingleton(provider);
            services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(secrets));
        }).Build();

    private static async Task Save(ModelProfilesControl panel, ModelProfilesViewModel profiles, string id, string provider, string model, string key)
    {
        ((TextBox)panel.FindName("ModelProfileId")).Text = id;
        ((ComboBox)panel.FindName("ModelProviderSelector")).SelectedItem = provider;
        ((TextBox)panel.FindName("ModelIdentifier")).Text = model;
        ((PasswordBox)panel.FindName("ModelApiKey")).Password = key;
        ((Button)panel.FindName("SaveModelProfile")).Command.Execute(null);
        await profiles.SaveCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
    }
    private static async Task Bind(Window window)
    {
        window.Measure(new Size(900, 800)); window.Arrange(new Rect(0, 0, 900, 800)); window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static Task OnWpfThread(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); done.TrySetResult(); }
                catch (Exception exception) { done.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class ProfileProbeHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return Json("{\"id\":\"opaque-ui-model\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"}");
            var payload = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("none", document.RootElement.GetProperty("reasoning_effort").GetString());
            var nonce = Regex.Match(payload, "exactly: ([a-f0-9]{32})").Groups[1].Value;
            Assert.NotEmpty(nonce);
            return Json(JsonSerializer.Serialize(new { id = "ui-probe", @object = "chat.completion", created = 1, model = "opaque-ui-model",
                choices = new[] { new { index = 0, finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = "ui-call", type = "function", function = new { name = "capability_probe", arguments = JsonSerializer.Serialize(new { value = nonce }) } } } } } } }));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class Provider(string id) : IModelProvider
    {
        public string ProviderId => id;
        public int Opens, Tests;
        public bool HoldOpen, ThrowOnTest;
        public ModelConnectionTest? TestResult;
        public List<ModelProfile> Profiles { get; } = [];
        public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];
        public List<string?> Instructions { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++; Profiles.Add(profile); Entered.TrySetResult();
            if (HoldOpen) await Release.Task.WaitAsync(ct);
            return new(new ScriptedChatClient((messages, options) =>
            {
                Messages.Add(messages); Instructions.Add(options?.Instructions);
                return new(new ChatMessage(ChatRole.Assistant, "Synthetic " + id + " result")) { FinishReason = ChatFinishReason.Stop };
            }), profile);
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct)
        {
            Tests++;
            if (ThrowOnTest) throw new InvalidOperationException("private-provider-secret");
            return Task.FromResult(TestResult ?? new ModelConnectionTest(true, null, "Synthetic success", profile.Capabilities));
        }
    }
}
