using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using PersonalAgent.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class McpConnectionsUiTests
{
    [Fact]
    public Task ActualHttpEditorProtectsCredentialsPinsOwnerPermissionsAddsAnotherServerAndReopensWithoutNetwork() => OnWpfThread(async () =>
    {
        const string key = "synthetic-mcp-ui-key-do-not-log";
        var requests = 0; var calls = 0; var wrongAuth = 0;
        await using var first = McpTestServer.Program.CreateHttpServer(async context =>
        {
            Interlocked.Increment(ref requests);
            if (context.Request.Headers.Authorization != "Bearer " + key) Interlocked.Increment(ref wrongAuth);
            if (context.Request.Method == "POST")
            {
                context.Request.EnableBuffering(); using var body = await JsonDocument.ParseAsync(context.Request.Body); context.Request.Body.Position = 0;
                if (body.RootElement.GetProperty("method").GetString() == "tools/call") Interlocked.Increment(ref calls);
            }
        });
        await using var second = McpTestServer.Program.CreateHttpServer(listTools: () => [new Tool { Name = "new_server_read", Description = "Another configured server",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), Annotations = new() { ReadOnlyHint = true } }]);
        await first.StartAsync(); await second.StartAsync();
        using var database = new TemporaryAgentDatabase();
        Guid firstId;
        try
        {
            using (var host = Host(database))
            {
                await host.StartAsync(); var model = host.Services.GetRequiredService<McpConnectionsViewModel>(); await model.Initialization;
                var window = new McpConnectionsWindow(model); var main = host.Services.GetRequiredService<MainWindow>();
                try
                {
                    await Bind(window); await Bind(main);
                    Assert.InRange(((Button)main.FindName("McpConnectionsButton")).TranslatePoint(new Point(0, 0), main).Y, 0, 80);
                    Assert.Equal(0, requests); Assert.Empty(model.Choices);
                    FillHttp(window, "First server", Endpoint(first.Services), key);
                    Assert.False(model.ConnectCommand.CanExecute(null));
                    await Run(model.SaveCommand); await Pump();
                    Assert.Equal(0, requests); Assert.Equal("", ((PasswordBox)window.FindName("McpCredential")).Password);
                    var saved = model.Selected!.Profile; firstId = saved.Configuration.Id;
                    Assert.StartsWith("dpapi:", saved.Configuration.Http!.BearerSecretRef);
                    Assert.DoesNotContain(key, JsonSerializer.Serialize(saved));
                    await Run(model.ConnectCommand); await Pump();
                    Assert.Contains("Connected", model.Message); Assert.Equal(0, wrongAuth);
                    var row = Assert.Single(model.Tools); Assert.False(row.Allowed); Assert.Equal(ToolRiskLevel.Unknown, row.Risk);
                    Assert.Contains("message", ((TextBox)window.FindName("McpToolSchema")).Text);
                    row.Allowed = true; await Run(model.SaveToolsCommand);
                    Assert.Contains("Choose a risk", model.Message); Assert.Empty((await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync()).Single().Permissions);
                    row.Risk = ToolRiskLevel.ReadOnly; row.IndependentOfDesktop = true;
                    await Run(model.SaveToolsCommand); await Run(model.ConnectCommand); await Pump();
                    var reviewed = (await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync()).Single();
                    Assert.Equal(new[] { "echo" }, reviewed.Configuration.AllowedTools);
                    Assert.True(Assert.Single(model.Tools).Allowed);
                    var tool = host.Services.GetRequiredService<McpConnectionManager>().BindTool(firstId, Assert.Single(reviewed.Permissions));
                    var request = new AgentRequest { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "fake", Instructions = "Synthetic UI tool",
                        AllowedToolIds = [tool.Descriptor.Id] };
                    var denied = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask).Bind(request);
                    Assert.Equal("ToolPermissionDenied", (await denied.InvokeAsync(tool.Descriptor.Id, "denied", JsonSerializer.SerializeToElement(new { message = "hello" }), CancellationToken.None)).Result.ErrorCode);
                    Assert.Equal(0, calls);
                    var allowed = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true)).Bind(request);
                    Assert.Equal(ToolResultStatus.Success, (await allowed.InvokeAsync(tool.Descriptor.Id, "allowed", JsonSerializer.SerializeToElement(new { message = "hello" }), CancellationToken.None)).Result.Status);
                    Assert.Equal(1, calls);
                    // A changed destination cannot inherit the old bearer or tool grant silently.
                    ((TextBox)window.FindName("McpEndpoint")).Text = Endpoint(second.Services).AbsoluteUri;
                    Assert.False(model.Approved); Assert.False(model.ConnectCommand.CanExecute(null));
                    ((CheckBox)window.FindName("McpApproved")).IsChecked = true;
                    await Run(model.SaveCommand);
                    Assert.Contains("not transferred", model.Message);
                    Assert.Equal(Endpoint(first.Services), (await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync()).Single().Configuration.Http!.Endpoint);
                    // Add a distinct compatible server entirely through settings and existing binaries.
                    await Run(model.NewCommand);
                    FillHttp(window, "Second server", Endpoint(second.Services), "");
                    await Run(model.SaveCommand); await Run(model.ConnectCommand); await Pump();
                    var secondRow = Assert.Single(model.Tools);
                    Assert.Equal("new_server_read", secondRow.Name); Assert.False(secondRow.Allowed); Assert.Equal(ToolRiskLevel.Unknown, secondRow.Risk);
                    Assert.Equal(2, (await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync()).Count);
                }
                finally { window.Close(); main.Close(); await host.StopAsync(); }
            }
            var previousRequests = requests;
            using (var reopened = Host(database))
            {
                await reopened.StartAsync(); var model = reopened.Services.GetRequiredService<McpConnectionsViewModel>(); await model.Initialization;
                Assert.Equal(2, model.Choices.Count); Assert.Empty(reopened.Services.GetRequiredService<McpConnectionManager>().List());
                Assert.Equal(previousRequests, requests);
                Assert.Single(model.Choices.Single(choice => choice.Profile.Configuration.Id == firstId).Profile.Permissions);
                await reopened.StopAsync();
            }
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(database.DatabasePath)!, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(key, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        }
        finally { await first.StopAsync(); await second.StopAsync(); }
    });

    [Fact]
    public Task ActualStdioEditorUsesLiteralArgvProtectedEnvironmentAndCanDisconnectAnInFlightHandshake() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var processes = new Processes();
        using var host = Host(database, processes); await host.StartAsync();
        var model = host.Services.GetRequiredService<McpConnectionsViewModel>(); await model.Initialization;
        var window = new McpConnectionsWindow(model);
        try
        {
            await Bind(window);
            ((TextBox)window.FindName("McpName")).Text = "Local configured fixture";
            ((ComboBox)window.FindName("McpTransport")).SelectedItem = McpTransportKind.Stdio;
            ((TextBox)window.FindName("McpExecutable")).Text = Dotnet;
            const string literal = "literal & | % with spaces";
            ((TextBox)window.FindName("McpArguments")).Text = JsonSerializer.Serialize(new[] { typeof(McpTestServer.Program).Assembly.Location, literal });
            ((TextBox)window.FindName("McpEnvironmentName")).Text = "MCP_FIXTURE_SECRET";
            ((PasswordBox)window.FindName("McpCredential")).Password = "synthetic-stdio-ui-credential";
            ((CheckBox)window.FindName("McpEnabled")).IsChecked = true; ((CheckBox)window.FindName("McpApproved")).IsChecked = true;
            await Run(model.SaveCommand); Assert.Empty(processes.Pids);
            await Run(model.ConnectCommand); await Pump();
            var row = Assert.Single(model.Tools); row.Allowed = true; row.Risk = ToolRiskLevel.ReadOnly;
            await Run(model.SaveToolsCommand); await Run(model.ConnectCommand); await Pump();
            var profile = model.Selected!.Profile;
            var tool = host.Services.GetRequiredService<McpConnectionManager>().BindTool(profile.Configuration.Id, Assert.Single(profile.Permissions));
            var result = await tool.ExecuteAsync(new(JsonSerializer.SerializeToElement(new { message = "__fixture_environment__" })),
                new(Guid.NewGuid(), Guid.NewGuid(), "inspect-own-fixture"), CancellationToken.None);
            using var payload = JsonDocument.Parse(result.Text);
            Assert.True(payload.RootElement.GetProperty("ScopedPresent").GetBoolean());
            Assert.Contains(literal, payload.RootElement.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()));
            await Run(model.DisconnectCommand);
            Assert.All(processes.Pids, pid => Assert.False(IsRunning(pid)));
            // Change to a waiting child, explicitly remove old credentials for the new argv scope.
            ((TextBox)window.FindName("McpArguments")).Text = JsonSerializer.Serialize(new[] { typeof(McpTestServer.Program).Assembly.Location, "--fixture-no-protocol" });
            ((CheckBox)window.FindName("McpRemoveCredential")).IsChecked = true; ((CheckBox)window.FindName("McpApproved")).IsChecked = true;
            await Run(model.SaveCommand);
            var before = processes.Pids.Count;
            model.ConnectCommand.Execute(null);
            await Until(() => processes.Pids.Count == before + 1);
            Assert.False(model.CanEdit); Assert.True(model.DisconnectCommand.CanExecute(null));
            await Run(model.DisconnectCommand); await model.ConnectCommand.ExecutionTask!;
            Assert.All(processes.Pids, pid => Assert.False(IsRunning(pid)));
            await Run(model.RemoveCommand); Assert.Empty(model.Choices);
            Assert.Empty(await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync());
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public Task InvalidJsonArgsCannotLaunchOrStoreUnprotectedValues() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var processes = new Processes();
        using var host = Host(database, processes); await host.StartAsync();
        var model = host.Services.GetRequiredService<McpConnectionsViewModel>(); await model.Initialization;
        var window = new McpConnectionsWindow(model);
        try
        {
            await Bind(window); model.Name = "Invalid args"; model.Command = Dotnet; model.ArgumentsJson = "[true]";
            model.SetPendingSecret("synthetic-invalid-key"); model.Enabled = model.Approved = true;
            await Run(model.SaveCommand);
            Assert.Contains("could not be saved", model.Message); Assert.True(model.CanEdit); Assert.Empty(processes.Pids);
            Assert.Empty(await host.Services.GetRequiredService<IMcpConnectionRepository>().ListAsync());
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public async Task RepositoryRejectsPlaintextCredentialAndConflictingOwnerPermission()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteMcpConnectionRepository(database);
        var profile = new McpConnectionProfile { Configuration = new() { Id = Guid.NewGuid(), Name = "Invalid stored credential", Transport = McpTransportKind.StreamableHttp,
            Http = new(new Uri("https://fixture.invalid/mcp"), "plaintext-not-a-reference") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(profile));
        profile = profile with { Configuration = profile.Configuration with { Http = profile.Configuration.Http! with { BearerSecretRef = null } },
            Permissions = [new(Guid.NewGuid(), "wrong-tool", "old-version", ToolRiskLevel.ReadOnly)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(profile));
        Assert.Empty(await repository.ListAsync());
    }

    private static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    private static Uri Endpoint(IServiceProvider services) => new(Assert.Single(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
    private static IHost Host(TemporaryAgentDatabase database, Processes? processes = null) => AppHost.CreateBuilder([], database.DatabasePath)
        .ConfigureLogging(log => log.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets")));
            services.AddSingleton(new McpLifecycleOptions(3, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10)));
            if (processes is not null) services.AddSingleton<ILogger<StdioMcpTransportProvider>>(processes);
        }).Build();
    private static void FillHttp(McpConnectionsWindow window, string name, Uri endpoint, string secret)
    {
        ((TextBox)window.FindName("McpName")).Text = name;
        ((ComboBox)window.FindName("McpTransport")).SelectedItem = McpTransportKind.StreamableHttp;
        ((TextBox)window.FindName("McpEndpoint")).Text = endpoint.AbsoluteUri;
        ((CheckBox)window.FindName("McpLoopback")).IsChecked = true;
        ((PasswordBox)window.FindName("McpCredential")).Password = secret;
        ((CheckBox)window.FindName("McpEnabled")).IsChecked = true; ((CheckBox)window.FindName("McpApproved")).IsChecked = true;
    }
    private static async Task Run(AsyncCommand command) { Assert.True(command.CanExecute(null)); command.Execute(null); await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(15)); await Pump(); }
    private static async Task Bind(Window window) { window.Measure(new Size(650, 600)); window.Arrange(new Rect(0, 0, 650, 600)); window.UpdateLayout(); await Pump(); }
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Until(Func<bool> condition) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, stop.Token); }
    private static Task OnWpfThread(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            })); Dispatcher.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private sealed class Processes : ILogger<StdioMcpTransportProvider>
    {
        public ConcurrentQueue<int> Pids = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (id.Id == 4100 && state is IEnumerable<KeyValuePair<string, object?>> values)
                Pids.Enqueue(Convert.ToInt32(values.Single(pair => pair.Key == "ProcessId").Value));
        }
    }
}
