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

namespace PersonalAgent.IntegrationTests;

public sealed class ModelProfilesTests
{
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
                await Save(panel, profiles, "work", "openai", "model-one", "synthetic-openai-ui-key");
                var firstProfile = profiles.Selected!.Profile!;
                Assert.StartsWith("dpapi:", firstProfile.SecretRef);
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
            openai.ThrowOnTest = true;
            profiles.TestCommand.Execute(null); await profiles.TestCommand.ExecutionTask!;
            Assert.DoesNotContain("private-provider-secret", profiles.Message);
            Assert.Contains("Connection test failed", profiles.Message);
            Assert.Equal(0, openai.Opens);
            Assert.True(profiles.CanEdit);
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
    private sealed class Provider(string id) : IModelProvider
    {
        public string ProviderId => id;
        public int Opens, Tests;
        public bool HoldOpen, ThrowOnTest;
        public List<ModelProfile> Profiles { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++; Profiles.Add(profile); Entered.TrySetResult();
            if (HoldOpen) await Release.Task.WaitAsync(ct);
            return new(new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant, "Synthetic " + id + " result")) { FinishReason = ChatFinishReason.Stop }), profile);
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct)
        {
            Tests++;
            if (ThrowOnTest) throw new InvalidOperationException("private-provider-secret");
            return Task.FromResult(new ModelConnectionTest(true, null, "Synthetic success", profile.Capabilities));
        }
    }
}
