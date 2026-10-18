using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence;
using System.IO;

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
            services.AddSingleton<TaskRunCoordinator>();
            services.AddSingleton<ITaskCommands>(provider => provider.GetRequiredService<TaskRunCoordinator>());
            services.AddSingleton<IAgentRuntime>(provider => new FakeAgentRuntime(
                [new("Read local task", TimeSpan.FromSeconds(1)), new("Prepare local result", TimeSpan.FromSeconds(1))],
                provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync));
            services.AddSingleton<TaskRunner>();
            services.AddHostedService<LifecycleLoggingService>();
            if (historyDatabasePath is not null)
            {
                services.AddSingleton(new SqliteAgentDbContextFactory(historyDatabasePath));
                services.AddSingleton<IRunRepository>(provider => new SqliteRunRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IConversationRepository>(provider => new SqliteConversationRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddHostedService<RunHistoryService>();
            }
            services.AddHostedService<TaskRunnerService>();
        });
}
