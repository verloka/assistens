using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;

namespace PersonalAgent.App;

public static class AppHost
{
    public static IHostBuilder CreateBuilder(string[] args) => Host.CreateDefaultBuilder(args)
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
            services.AddHostedService<TaskRunnerService>();
        });
}
