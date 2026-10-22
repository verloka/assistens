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
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class SetupWizardTests
{
    [Fact]
    public Task FreshDataProfileUsesActualWizardForOneDurableReadOnlyLocalTaskWithNoApiOrDesktop() => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider();
        using var host = Host(database, provider); await host.StartAsync();
        var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
        var model = host.Services.GetRequiredService<SetupWizardViewModel>(); await model.Profiles.Initialization;
        var main = host.Services.GetRequiredService<MainWindow>(); var window = new SetupWizardWindow(model);
        try
        {
            window.Measure(new Size(650, 510)); window.Arrange(new Rect(0, 0, 650, 510)); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(window.ShowActivated); Assert.NotNull(main.FindName("SetupWizardButton"));
            Assert.Empty(runs.GetRuns()); Assert.Equal(0, provider.Tests); Assert.Equal(0, provider.Opens);
            Assert.Null(model.Profiles.Selected!.Profile); Assert.False(model.TestCommand.CanExecute(null));
            await Next(model); Assert.Equal(1, model.Step); Assert.True(model.NextCommand.CanExecute(null));
            await Next(model); Assert.Equal(2, model.Step); await Next(model); Assert.Equal(3, model.Step);
            Assert.Empty(runs.GetRuns()); Assert.Equal(0, provider.Tests); Assert.Equal(0, provider.Opens);
            var button = (Button)window.FindName("SetupFirstRun"); Assert.Same(model.RunCommand, button.Command);
            model.RunCommand.Execute(null); var first = model.RunCommand.ExecutionTask!;
            model.RunCommand.Execute(null); Assert.Same(first, model.RunCommand.ExecutionTask);
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Local demo completed", model.Message); Assert.False(model.RunCommand.CanExecute(null));
            var run = Assert.Single(runs.GetRuns()); Assert.Equal(model.RunId, run.Id); Assert.Equal(RunStatus.Completed, run.Status);
            var saved = (await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id))!;
            Assert.Empty(saved.Request.AllowedToolIds); Assert.Null(saved.Request.Profile);
            Assert.Equal(2, saved.Request.Limits.MaxSteps); Assert.Equal(1024, saved.Request.Limits.MaxTokens);
            Assert.DoesNotContain(saved.Events, evt => evt.EventType == RunEventType.ToolRequested);
            Assert.Null(host.Services.GetRequiredService<IDesktopAccessCoordinator>().Snapshot.OwnerRunId);
            Assert.Equal(0, provider.Tests); Assert.Equal(0, provider.Opens);
        }
        finally { window.Close(); main.Close(); await host.StopAsync(); }
        using var reopened = Host(database, provider); await reopened.StartAsync();
        Assert.Equal(RunStatus.Completed, Assert.Single(reopened.Services.GetRequiredService<TaskRunCoordinator>().GetRuns()).Status);
        Assert.Equal(0, provider.Opens); await reopened.StopAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CloudSetupRequiresExplicitCurrentImageToolProbeAndFirstTaskNeverReceivesTools(bool modelRequestsForbiddenTool) => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider { RequestTool = modelRequestsForbiddenTool };
        using var host = Host(database, provider); await host.StartAsync();
        var wizard = host.Services.GetRequiredService<SetupWizardViewModel>(); await wizard.Profiles.Initialization;
        await SaveCloud(wizard.Profiles);
        Assert.Equal(0, provider.Tests); Assert.Equal(0, provider.Opens);
        await Next(wizard); Assert.False(wizard.NextCommand.CanExecute(null));
        wizard.TestCommand.Execute(null); await wizard.TestCommand.ExecutionTask!;
        Assert.Equal(1, provider.Tests); Assert.Equal(wizard.Profiles.Selected!.Profile, wizard.Profiles.VerifiedProfile);
        Assert.True(wizard.NextCommand.CanExecute(null));
        // An edit invalidates the capability confirmation even though the saved selection remains.
        wizard.Profiles.ModelId = "changed-model"; Assert.Null(wizard.Profiles.VerifiedProfile); Assert.False(wizard.NextCommand.CanExecute(null));
        wizard.Profiles.ModelId = "fixture-model"; Assert.False(wizard.NextCommand.CanExecute(null));
        wizard.TestCommand.Execute(null); await wizard.TestCommand.ExecutionTask!; Assert.Equal(2, provider.Tests);
        await Next(wizard); await Next(wizard);
        wizard.RunCommand.Execute(null); await wizard.RunCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, provider.Opens); Assert.False(wizard.RunCommand.CanExecute(null));
        var saved = (await host.Services.GetRequiredService<IRunRepository>().GetAsync(wizard.RunId!.Value))!;
        Assert.Empty(saved.Request.AllowedToolIds); Assert.NotNull(saved.Request.Profile);
        Assert.Equal(modelRequestsForbiddenTool ? RunStatus.Blocked : RunStatus.Completed, saved.Run.Status);
        Assert.DoesNotContain(saved.Events, evt => evt.EventType == RunEventType.ToolRequested);
        Assert.Null(host.Services.GetRequiredService<IDesktopAccessCoordinator>().Snapshot.OwnerRunId);
        Assert.DoesNotContain("private-provider", wizard.Message);
        Assert.Empty(provider.Tools); await host.StopAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedOrIncompleteProbeCannotUnlockCloudTaskAndDisplaysNoProviderBody(bool incomplete) => PrivacyUiTests.OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var provider = new Provider { BadProbe = true, IncompleteProbe = incomplete };
        using var host = Host(database, provider); await host.StartAsync();
        var wizard = host.Services.GetRequiredService<SetupWizardViewModel>(); await wizard.Profiles.Initialization;
        await SaveCloud(wizard.Profiles); await Next(wizard);
        wizard.TestCommand.Execute(null); await wizard.TestCommand.ExecutionTask!;
        Assert.False(wizard.NextCommand.CanExecute(null)); Assert.False(wizard.RunCommand.CanExecute(null));
        Assert.Null(wizard.Profiles.VerifiedProfile); Assert.DoesNotContain("private-provider", wizard.Message);
        Assert.Empty(host.Services.GetRequiredService<TaskRunCoordinator>().GetRuns()); Assert.Equal(0, provider.Opens);
        await host.StopAsync();
    });

    private static IHost Host(TemporaryAgentDatabase database, Provider provider) => AppHost.CreateBuilder([], database.DatabasePath)
        .ConfigureLogging(builder => builder.ClearProviders()).ConfigureServices(services =>
        {
            services.RemoveAll<IModelProvider>(); services.AddSingleton<IModelProvider>(provider);
            services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.DatabasePath)!, "secrets")));
        }).Build();
    private static async Task SaveCloud(ModelProfilesViewModel model)
    {
        model.ProfileId = "setup-fixture"; model.ProviderId = "openai"; model.ModelId = "fixture-model";
        model.SupportsImages = true; model.SupportsToolCalling = true; model.SetPendingSecret("synthetic-setup-key");
        model.SaveCommand.Execute(null); await model.SaveCommand.ExecutionTask!;
        Assert.NotNull(model.Selected!.Profile);
    }
    private static async Task Next(SetupWizardViewModel model)
    {
        Assert.True(model.NextCommand.CanExecute(null)); model.NextCommand.Execute(null); await model.NextCommand.ExecutionTask!;
    }
    private sealed class Provider : IModelProvider
    {
        public string ProviderId => "openai";
        public bool BadProbe, IncompleteProbe, RequestTool;
        public int Tests, Opens;
        public List<AITool> Tools = [];
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct)
        {
            Tests++;
            if (BadProbe && !IncompleteProbe) throw new InvalidOperationException("private-provider-response");
            return Task.FromResult(new ModelConnectionTest(true, null, "private-provider-message", IncompleteProbe ? null : profile.Capabilities));
        }
        public Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            Opens++;
            return Task.FromResult(new ModelConnection(new ScriptedChatClient((_, options) =>
            {
                Tools.AddRange(options?.Tools ?? []);
                return RequestTool ? new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("forbidden", "private-provider-tool", new Dictionary<string, object?>())]))
                    { FinishReason = ChatFinishReason.ToolCalls }
                    : new(new ChatMessage(ChatRole.Assistant, "READY")) { FinishReason = ChatFinishReason.Stop };
            }), profile));
        }
    }
}
