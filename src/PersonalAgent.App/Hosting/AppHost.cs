using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App.ViewModels;

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
            services.AddHostedService<LifecycleLoggingService>();
        });
}
