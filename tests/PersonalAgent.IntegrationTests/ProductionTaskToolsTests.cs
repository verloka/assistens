using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Mcp;
using PersonalAgent.Persistence;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ProductionTaskToolsTests
{
    [Theory]
    [InlineData("success", "computer_click")]
    [InlineData("success", "computer_scroll")]
    [InlineData("success", "computer_type")]
    [InlineData("success", "computer_press_key")]
    [InlineData("image-changed", "computer_click")]
    [InlineData("geometry-changed", "computer_click")]
    [InlineData("reject", "computer_click")]
    [InlineData("cancel", "computer_click")]
    [InlineData("takeover-resume", "computer_click")]
    public Task OrdinaryDashboardGuiUsesExactApprovalFreshRevalidationAndOneDispatch(string mode, string inputTool) => PrivacyUiTests.OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Start();
        string? old = null;
        fixture.Provider.Respond = (messages, options, turn) =>
        {
            Assert.Equal(new[] { inputTool, "computer_screenshot" }.Order(), options!.Tools!.Select(tool => tool.Name).Order());
            Assert.Equal(AgentPromptPolicy.Instructions, options.Instructions);
            if (turn == 1) return Call("screen", "computer_screenshot", new { });
            if (turn == 2)
            {
                old = Observation(messages);
                object args = inputTool switch
                {
                    "computer_click" => new { observationId = old, imageX = 0, imageY = 0, button = "left" },
                    "computer_scroll" => new { observationId = old, imageX = 0, imageY = 0, direction = "down", amount = 1 },
                    "computer_type" => new { observationId = old, text = "Тест 🙂 你好" },
                    _ => new { observationId = old, chord = "Ctrl+A" }
                };
                return Call("input", inputTool, args);
            }
            Assert.NotEqual(old, Observation(messages)); return Text("Production GUI composition completed");
        };
        await fixture.Tools.RefreshAsync();
        Assert.Equal(5, fixture.Tools.Choices.Count); Assert.All(fixture.Tools.Choices, item => Assert.False(item.Selected));
        fixture.Select("computer_screenshot", inputTool);
        var execution = fixture.Submit();
        await fixture.Pending("computer_screenshot");
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        await fixture.Decide(true);
        await fixture.Pending(inputTool);
        Assert.Equal(1, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
        Assert.All(fixture.Tools.Choices, item => Assert.False(item.Selected));
        if (mode == "image-changed") fixture.Native.StableScreen = false;
        if (mode == "geometry-changed") fixture.Native.Origin = 20;
        if (mode == "takeover-resume")
        {
            var repository = fixture.Host.Services.GetRequiredService<IApprovalRepository>();
            var pending = Assert.Single(await repository.ListPendingAsync(Fixture.Owner));
            await fixture.Runs.PauseAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
            await Assert.ThrowsAsync<ApprovalConflictException>(() => repository.DecideAsync(new(pending.Id, pending.Version, pending.ActionHash, Fixture.Owner, true)));
            await fixture.WaitStatus(RunStatus.Paused);
            await fixture.Runs.ResumeAsync(new(Guid.NewGuid(), fixture.RunId, fixture.Runs.GetRun(fixture.RunId).RunVersion));
        }
        else if (mode == "cancel") await fixture.Cancel();
        else await fixture.Decide(mode != "reject");
        if (mode == "success")
        {
            await fixture.Pending("computer_screenshot"); // Post-input observation is separately approved.
            Assert.Equal(2, fixture.Native.Captures); Assert.NotEmpty(fixture.Native.Inputs);
            await fixture.Decide(true); await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunStatus.Completed, fixture.Dashboard.CurrentRun!.Status); Assert.Equal(3, fixture.Native.Captures);
            Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await fixture.Dispatches.ListAsync(fixture.RunId)).Status);
        }
        else
        {
            if (mode != "cancel") { await fixture.WaitStatus(RunStatus.Blocked); await fixture.Cancel(); }
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(fixture.Native.Inputs); Assert.Empty(await fixture.Dispatches.ListAsync(fixture.RunId));
            Assert.Equal(mode is "image-changed" or "geometry-changed" ? 2 : 1, fixture.Native.Captures);
        }
        Assert.Null(fixture.Desktop.Snapshot.OwnerRunId); Assert.Equal(1, fixture.Provider.Opens);
        var stored = (await fixture.RunsStore.GetAsync(fixture.RunId))!;
        Assert.Equal(2, stored.Request.ToolPermissions!.Count); Assert.Equal(2, stored.Request.AllowedToolIds.Count);
    });

    [Fact]
    public Task ActualToolCheckboxCanBeSelectedWithoutEffectsAndStartRemainsReachable() => PrivacyUiTests.OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Start(); await fixture.Tools.RefreshAsync();
        var surface = (FrameworkElement)fixture.Main.Content;
        surface.Measure(new(650, 600)); surface.Arrange(new(0, 0, 650, 600)); surface.UpdateLayout();
        fixture.Selector.Measure(new(760, 570)); fixture.Selector.Arrange(new(0, 0, 760, 570)); fixture.Selector.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Selector.UpdateLayout();
        var choices = (ItemsControl)fixture.Selector.FindName("TaskToolChoices"); Assert.Equal(5, choices.Items.Count);
        var item = (FrameworkElement)choices.ItemTemplate.LoadContent(); item.DataContext = fixture.Tools.Choices[0];
        item.Measure(new(700, 200)); item.Arrange(new(0, 0, 700, 200)); item.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var check = FindCheckBox(item); Assert.NotNull(check);
        check.SetCurrentValue(CheckBox.IsCheckedProperty, true); check.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
        Assert.Equal("computer_screenshot", Assert.Single(fixture.Tools.Snapshot()!).ToolId);
        Assert.Equal(0, fixture.Provider.Opens); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Runs.GetRuns());
        var start = (Button)fixture.Main.FindName("SubmitTaskButton");
        Assert.True(start.ActualHeight > 0);
        start.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); surface.UpdateLayout();
        Assert.InRange(start.TranslatePoint(new(0, 0), surface).Y + start.ActualHeight, 1, 600);
    });

    private static CheckBox? FindCheckBox(DependencyObject parent)
    {
        if (parent is CheckBox check) return check;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindCheckBox(System.Windows.Media.VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    [Fact]
    public Task DefaultContinuationAndProfileChangesCannotInheritToolsAndInputNeedsScreenshot() => PrivacyUiTests.OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Start();
        await fixture.Tools.RefreshAsync(); fixture.Select("computer_click");
        Assert.Throws<InvalidOperationException>(() => fixture.Tools.Snapshot()); fixture.Tools.Reset();
        fixture.Select("computer_screenshot");
        fixture.Profiles.Selected = fixture.Profiles.Choices[0];
        Assert.All(fixture.Tools.Choices, item => Assert.False(item.Selected));
        fixture.Profiles.Selected = fixture.Profiles.Choices.Single(item => item.Profile is not null);
        fixture.Provider.Respond = (_, options, _) => { Assert.Empty(options!.Tools!); return Text("Text only"); };
        await fixture.Submit().WaitAsync(TimeSpan.FromSeconds(10)); var first = fixture.Dashboard.CurrentRun!;
        fixture.Dashboard.ContinueConversation = true;
        await fixture.Submit().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(first.ConversationId, fixture.Dashboard.CurrentRun!.ConversationId);
        Assert.Null((await fixture.RunsStore.GetAsync(fixture.RunId))!.Request.ToolPermissions);
        Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
    });

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("target-denied")]
    [InlineData("revoked")]
    [InlineData("reconnected")]
    [InlineData("queued-reconnected")]
    [InlineData("queued-reclassified")]
    public Task OrdinaryDashboardMcpIntersectsSavedCurrentConnectionAndExactTargets(string mode) => PrivacyUiTests.OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Start();
        var calls = 0;
        await using var server = McpTestServer.Program.CreateHttpServer(listTools: () => [new ModelContextProtocol.Protocol.Tool
        {
            Name = "echo", Description = "Production local fixture", InputSchema = JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { message = new { type = "string" } }, required = new[] { "message" }, additionalProperties = false })
        }], callTool: (_, _) =>
        { Interlocked.Increment(ref calls); return ValueTask.FromResult(new ModelContextProtocol.Protocol.CallToolResult { Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "Synthetic MCP acknowledged" }] }); });
        await server.StartAsync();
        try
        {
            var endpoint = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/mcp");
            var manager = fixture.Host.Services.GetRequiredService<McpConnectionManager>();
            var config = new McpConnectionConfig { Id = Guid.NewGuid(), Name = "Production fixture", Enabled = true, Approved = true,
                Transport = McpTransportKind.StreamableHttp, Http = new(endpoint, AllowLoopbackHttp: true), AllowedTools = ["echo"] };
            var status = await manager.ConnectAsync(config);
            var declaration = Assert.Single(status.Tools);
            var grant = new McpToolPermission(config.Id, declaration.Descriptor.Id, declaration.Version,
                mode == "read" ? ToolRiskLevel.ReadOnly : ToolRiskLevel.Write, true);
            var profile = new McpConnectionProfile { Configuration = config, Permissions = [grant] };
            var repository = fixture.Host.Services.GetRequiredService<IMcpConnectionRepository>(); await repository.SaveAsync(profile);
            await fixture.Tools.RefreshAsync(); var choice = fixture.Tools.Choices.Single(value => value.Permission.ToolId == grant.ToolId);
            choice.Selected = true;
            if (mode != "read")
            {
                Assert.Throws<ArgumentException>(() => fixture.Tools.Snapshot());
                choice.TargetsJson = """[{"JsonPointer":"/message","AllowedValues":["allowed"]}]""";
            }
            fixture.Provider.Respond = (_, options, turn) =>
            {
                if (options!.Tools!.Count == 0) return Text("Earlier queued task completed");
                Assert.Equal(grant.ToolId, Assert.Single(options!.Tools!).Name);
                return turn == 1 ? Call("mcp-call", grant.ToolId, new { message = mode == "target-denied" ? "foreign" : "allowed" }) : Text("Production MCP composition completed");
            };
            if (mode.StartsWith("queued-", StringComparison.Ordinal))
            {
                fixture.Provider.HoldOpen = true;
                var selected = fixture.Profiles.Selected!.Profile!;
                await fixture.Runs.SubmitAsync(new(Guid.NewGuid(), "Queue barrier", new("desktop"), ModelProfileId: selected.Id, Profile: selected));
                await fixture.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var execution = fixture.Submit();
            if (mode.StartsWith("queued-", StringComparison.Ordinal))
            {
                await Fixture.Until(() => Task.FromResult(fixture.Dashboard.CurrentRun is not null && fixture.Runs.GetRun(fixture.RunId).Status == RunStatus.Queued));
                if (mode == "queued-reconnected") await manager.ConnectAsync(config);
                else await repository.SaveAsync(profile with { Permissions = [grant with { IndependentOfDesktop = false }] });
                fixture.Provider.HoldOpen = false; fixture.Provider.Release.TrySetResult();
            }
            if (mode is "write" or "revoked" or "reconnected")
            {
                await fixture.Pending(grant.ToolId); Assert.Equal(0, calls); Assert.Null(fixture.Desktop.Snapshot.OwnerRunId);
                if (mode == "revoked") await repository.SaveAsync(profile with { Permissions = [] });
                if (mode == "reconnected") await manager.ConnectAsync(config);
                await fixture.Decide(true);
            }
            if (mode is "target-denied" or "revoked" or "reconnected" or "queued-reconnected" or "queued-reclassified")
            {
                await fixture.WaitStatus(RunStatus.Blocked); Assert.Equal(0, calls);
                Assert.Empty(await fixture.Dispatches.ListAsync(fixture.RunId)); await fixture.Cancel();
                if (mode.StartsWith("queued-", StringComparison.Ordinal)) Assert.Equal(1, fixture.Provider.Opens);
            }
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode is "read" or "write") Assert.Equal(1, calls);
            Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
            if (mode == "write") Assert.Equal(ToolCallDispatchStatus.Acknowledged, Assert.Single(await fixture.Dispatches.ListAsync(fixture.RunId)).Status);
            if (mode == "read")
            {
                fixture.Provider.Respond = (_, options, _) => { Assert.Empty(options!.Tools!); return Text("No inherited MCP grant"); };
                await fixture.Submit().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, calls);
            }
        }
        finally { await server.StopAsync(); }
    });

    private static ChatResponse Call(string id, string tool, object args) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(id, tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!)])) { FinishReason = ChatFinishReason.ToolCalls };
    private static ChatResponse Text(string value) => new(new ChatMessage(ChatRole.Assistant, value)) { FinishReason = ChatFinishReason.Stop };
    private static string Observation(IReadOnlyList<ChatMessage> messages)
    {
        using var metadata = JsonDocument.Parse((string)messages.SelectMany(value => value.Contents).OfType<FunctionResultContent>().Last().Result!);
        var data = metadata.RootElement.GetProperty("data"); if (data.TryGetProperty("observation", out var observed)) data = observed;
        return data.GetProperty("observationId").GetString()!;
    }

    [Theory]
    [InlineData("channel/telegram")]
    [InlineData("schedule")]
    [InlineData("vision")]
    public Task NonDesktopOriginsAndMissingVisionCannotUseAnOtherwiseValidSelection(string source) => PrivacyUiTests.OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Start(); await fixture.Tools.RefreshAsync(); fixture.Select("computer_screenshot");
        var selection = fixture.Tools.Snapshot()!; var profile = fixture.Profiles.Selected!.Profile!;
        if (source == "vision") profile = profile with { Capabilities = new(false, true) };
        var run = await fixture.Runs.SubmitAsync(new(Guid.NewGuid(), "No ambient grants", new(source == "vision" ? "desktop" : source),
            ModelProfileId: profile.Id, Profile: profile, AllowedToolIds: selection.Select(rule => rule.ToolId).ToArray(), ToolPermissions: selection));
        await Fixture.Until(() => Task.FromResult(fixture.Runs.GetRun(run.Id).Status == RunStatus.Blocked));
        var stored = (await fixture.RunsStore.GetAsync(run.Id))!;
        Assert.Equal(source == "vision" ? "VisionRequired" : "LocalToolSelectionRequired", stored.Result!.ErrorCode);
        Assert.Equal(0, fixture.Provider.Opens); Assert.Equal(0, fixture.Native.Captures); Assert.Empty(fixture.Native.Inputs);
        Assert.Empty(await fixture.Host.Services.GetRequiredService<IApprovalRepository>().ListPendingAsync(Fixture.Owner));
    });

    [Fact]
    public async Task SelectionIsDeeplySnapshottedPartOfDedupIntentAndNeverAutoRestoredAfterRestart()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database); Guid runId;
        using (var runs = new TaskRunCoordinator(repository))
        {
            var ids = new[] { "mcp_selected" }; var values = new[] { "allowed" };
            var permissions = new List<RunToolPermission> { new(ids[0], new(ToolSourceKind.Mcp, Guid.NewGuid().ToString("N")), ToolRiskLevel.ReadOnly,
                ToolPermissionEffect.Allow, "v1", [new("/message", values)], Guid.NewGuid().ToString("N")) };
            var profile = new ModelProfile { Id = "selected", ProviderId = "production-fixture", ModelId = "synthetic", SecretRef = "dpapi:" + Guid.NewGuid().ToString("N") };
            var command = new SubmitTaskCommand(Guid.NewGuid(), "Selected one time", new("desktop"), ModelProfileId: profile.Id, Profile: profile,
                ExternalKey: new("fixture", "selected-once"), AllowedToolIds: ids, ToolPermissions: permissions);
            runId = (await runs.SubmitAsync(command)).Id; ids[0] = "changed"; values[0] = "foreign"; permissions.Clear();
            var stored = (await repository.GetAsync(runId))!; var rule = Assert.Single(stored.Request.ToolPermissions!);
            Assert.Equal("mcp_selected", Assert.Single(stored.Request.AllowedToolIds));
            Assert.Equal("allowed", Assert.Single(Assert.Single(rule.Destinations!).AllowedValues));
            var retry = command with { CommandId = Guid.NewGuid(), AllowedToolIds = stored.Request.AllowedToolIds, ToolPermissions = stored.Request.ToolPermissions };
            Assert.Equal(runId, (await runs.SubmitAsync(retry)).Id);
            await Assert.ThrowsAsync<RunSubmissionConflictException>(() => runs.SubmitAsync(retry with { CommandId = Guid.NewGuid(),
                ToolPermissions = [rule with { BindingId = Guid.NewGuid().ToString("N") }] }));
            await Assert.ThrowsAsync<RunSubmissionConflictException>(() => runs.SubmitAsync(retry with { CommandId = Guid.NewGuid(),
                ToolPermissions = [rule with { Destinations = [new("/message", ["different"])] }] }));
        }
        var provider = new Provider();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IModelProvider>(provider)).Build();
        await host.StartAsync();
        try
        {
            Assert.Equal(RunStatus.Blocked, host.Services.GetRequiredService<TaskRunCoordinator>().GetRun(runId).Status);
            Assert.Equal("RecoveryNeedsReview", (await repository.GetAsync(runId))!.Run.ErrorCode); Assert.Equal(0, provider.Opens);
            Assert.DoesNotContain("ToolPermissions", JsonSerializer.Serialize(new AgentRequest { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "legacy", ModelProfileId = "fake" }));
            Assert.DoesNotContain("ToolPermissions", JsonSerializer.Serialize(new SubmitTaskCommand(Guid.NewGuid(), "legacy", new("test"))));
        }
        finally { await host.StopAsync(); }
    }

    private sealed class Provider : IModelProvider
    {
        public string ProviderId => "production-fixture";
        public int Opens;
        public bool HoldOpen;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<IReadOnlyList<ChatMessage>, ChatOptions?, int, ChatResponse> Respond = (_, _, _) => Text("Default text task");
        public async Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Opens++; var turn = 0; Entered.TrySetResult();
            if (HoldOpen) await Release.Task.WaitAsync(ct);
            return new ModelConnection(new ScriptedChatClient(Enumerable.Range(0, 8)
                .Select(_ => (Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>)((messages, options) => Respond(messages, options, ++turn))).ToArray()), profile);
        }
        public Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new();
        public Provider Provider = new();
        public ComputerToolTests.Native Native = new() { StableScreen = true };
        public DesktopAccessCoordinator Desktop = new(_ => true);
        public IHost Host = null!;
        public MainWindow Main = null!;
        public TaskToolsWindow Selector = null!;
        public TaskToolsViewModel Tools => Host.Services.GetRequiredService<TaskToolsViewModel>();
        public ModelProfilesViewModel Profiles => Host.Services.GetRequiredService<ModelProfilesViewModel>();
        public DashboardViewModel Dashboard => Host.Services.GetRequiredService<DashboardViewModel>();
        public TaskRunCoordinator Runs => Host.Services.GetRequiredService<TaskRunCoordinator>();
        public IRunRepository RunsStore => Host.Services.GetRequiredService<IRunRepository>();
        public IToolCallRepository Dispatches => Host.Services.GetRequiredService<IToolCallRepository>();
        public Guid RunId => Dashboard.CurrentRun!.Id;
        public static readonly ApprovalOwner Owner = new("desktop", "production-tools-owner");
        public static async Task<Fixture> Start()
        {
            var fixture = new Fixture();
            fixture.Host = AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
            {
                services.AddSingleton(Owner); services.AddSingleton<IModelProvider>(fixture.Provider);
                services.AddSingleton<IDesktopAccessCoordinator>(fixture.Desktop);
                services.AddSingleton<Func<DesktopLease, IObservedComputerController>>(_ => lease =>
                    new WindowsComputerController(fixture.Desktop, lease, fixture.Native, fixture.Native, fixture.Native.GetMonitors, _ => true));
                services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(Path.Combine(Path.GetDirectoryName(fixture.Database.DatabasePath)!, "secrets")));
            }).Build();
            await fixture.Host.StartAsync();
            await fixture.Host.Services.GetRequiredService<IModelProfileRepository>().SaveAsync(new() { Id = "production", ProviderId = fixture.Provider.ProviderId,
                ModelId = "synthetic", SecretRef = "dpapi:" + Guid.NewGuid().ToString("N"), Capabilities = new(true, true) });
            fixture.Main = fixture.Host.Services.GetRequiredService<MainWindow>();
            await fixture.Profiles.Initialization;
            fixture.Profiles.Selected = fixture.Profiles.Choices.Single(value => value.Profile is not null);
            fixture.Main.Measure(new(900, 900)); fixture.Main.Arrange(new(0, 0, 900, 900)); fixture.Main.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            fixture.Selector = new TaskToolsWindow(fixture.Tools, fixture.Dashboard);
            Assert.NotNull(fixture.Main.FindName("TaskToolsButton")); Assert.NotNull(fixture.Selector.FindName("RefreshTaskTools"));
            Assert.Empty(fixture.Runs.GetRuns()); Assert.Equal(0, fixture.Provider.Opens); return fixture;
        }
        public void Select(params string[] ids) { foreach (var choice in Tools.Choices) choice.Selected = ids.Contains(choice.Permission.ToolId); }
        public Task Submit()
        {
            Dashboard.Instructions = "Synthetic production task"; Dashboard.SubmitCommand.Execute(null); return Dashboard.SubmitCommand.ExecutionTask!;
        }
        public async Task Pending(string tool)
        {
            await Until(async () => Dashboard.CurrentRun is not null && (await Host.Services.GetRequiredService<IApprovalRepository>().ListPendingAsync(Owner)).Any(value => value.Action.ToolId == tool));
        }
        public async Task Decide(bool approve)
        {
            var model = Host.Services.GetRequiredService<ApprovalsViewModel>(); await model.Initialization; await model.RefreshAsync();
            model.Selected = Assert.Single(model.Pending); model.Acknowledged = approve;
            var command = approve ? model.ApproveCommand : model.DenyCommand; Assert.True(command.CanExecute(null));
            command.Execute(null); await command.ExecutionTask!;
        }
        public Task WaitStatus(RunStatus status) => Until(() => Task.FromResult(Dashboard.CurrentRun is not null && Runs.GetRun(RunId).Status == status));
        public async Task Cancel() { await Runs.CancelAsync(new(Guid.NewGuid(), RunId, Runs.GetRun(RunId).RunVersion)); }
        public static async Task Until(Func<Task<bool>> predicate)
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!await predicate()) await Task.Delay(15, timeout.Token); }
        public async ValueTask DisposeAsync() { Selector.Close(); Main.Close(); await Host.StopAsync(); Host.Dispose(); Database.Dispose(); }
    }
}
