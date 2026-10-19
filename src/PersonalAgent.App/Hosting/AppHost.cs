using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using PersonalAgent.Mcp;
using System.IO;
using System.Security.Principal;

namespace PersonalAgent.App;

public static class AppHost
{
    public static string DefaultHistoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent", "history.db");

    // A path opts into persistent history. Tests/diagnostics can compose an isolated memory-only host.
    public static IHostBuilder CreateBuilder(string[] args, string? historyDatabasePath = null) => Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureServices(services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            services.AddSingleton<MainWindow>();
            services.AddSingleton<HealthViewModel>();
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<ModelProfilesViewModel>();
            services.AddSingleton<McpConnectionsViewModel>();
            services.AddSingleton<ApprovalsViewModel>();
            services.AddSingleton(_ =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new ApprovalOwner("desktop", identity.User?.Value ?? throw new InvalidOperationException("The local owner identity is unavailable."));
            });
            services.AddSingleton<McpConnectionFactory>();
            services.AddSingleton<IMcpTransportProvider, StdioMcpTransportProvider>();
            services.AddSingleton<IMcpTransportProvider, HttpMcpTransportProvider>();
            services.AddSingleton<McpToolDiscovery>();
            services.AddSingleton<McpConnectionManager>();
            services.AddSingleton<ControlPanelViewModel>();
            services.AddSingleton<ControlWindow>();
            services.AddSingleton<IDesktopAccessCoordinator>(_ => new DesktopAccessCoordinator());
            services.AddSingleton<TaskRunCoordinator>();
            services.AddSingleton<ISecretStore>(_ => new WindowsDpapiSecretStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent", "secrets")));
            services.AddSingleton<OpenAiModelProvider>();
            services.AddSingleton<AnthropicModelProvider>();
            services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<AnthropicModelProvider>());
            services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<OpenAiModelProvider>());
            services.AddSingleton<TakeControlHotkeyService>();
            services.AddSingleton<ITaskCommands>(provider => provider.GetRequiredService<TaskRunCoordinator>());
            services.AddSingleton<FakeAgentRuntime>(provider => new FakeAgentRuntime(
                [new("Read local task", TimeSpan.FromSeconds(1)), new("Prepare local result", TimeSpan.FromSeconds(1))],
                provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync));
            services.AddSingleton<IAgentRuntime>(provider => new ProfileAgentRuntime(provider.GetRequiredService<FakeAgentRuntime>(),
                provider.GetServices<IModelProvider>(), provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync));
            services.AddSingleton<TaskRunner>();
            services.AddHostedService<LifecycleLoggingService>();
            services.AddHostedService<McpLifecycleService>();
            if (historyDatabasePath is not null)
            {
                services.AddSingleton(new SqliteAgentDbContextFactory(historyDatabasePath));
                services.AddSingleton<IRunRepository>(provider => new SqliteRunRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IConversationRepository>(provider => new SqliteConversationRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IModelProfileRepository>(provider => new SqliteModelProfileRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IMcpConnectionRepository>(provider => new SqliteMcpConnectionRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IApprovalRepository>(provider => new SqliteApprovalRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IToolCallRepository>(provider => new SqliteToolCallRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IScheduleRepository>(provider => new SqliteScheduleRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddHostedService<RunHistoryService>();
            }
            services.AddHostedService<TaskRunnerService>();
        });
}
