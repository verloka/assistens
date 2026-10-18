using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PersonalAgent.App;

public static class AppHost
{
    public static IHostBuilder CreateBuilder(string[] args) => Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureServices(services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            services.AddSingleton<MainWindow>();
            services.AddHostedService<LifecycleLoggingService>();
        });
}
