using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using System.IO;
using System.Windows;

namespace PersonalAgent.App;

// Explicit release diagnostic only; no production data, OS hooks/tray, shown windows or external adapters.
public static class PackageSmoke
{
    public static async Task RunAsync()
    {
        if (!LocalAssembly(typeof(object)) || !LocalAssembly(typeof(Window)))
            throw new InvalidOperationException("Package smoke requires the bundled runtime.");
        var directory = Directory.CreateTempSubdirectory("PersonalAgent.PackageSmoke-");
        try
        {
            var path = Path.Combine(directory.FullName, "history.db");
            using var host = AppHost.CreateBuilder([], path).ConfigureLogging(log => log.ClearProviders())
                .ConfigureServices(services => services.AddSingleton<ISecretStore>(new WindowsDpapiSecretStore(Path.Combine(directory.FullName, "secrets"))))
                .Build();
            await host.StartAsync();
            MainWindow? window = null;
            try
            {
                window = host.Services.GetRequiredService<MainWindow>();
                window.Measure(new Size(900, 800)); window.Arrange(new Rect(0, 0, 900, 800)); window.UpdateLayout();
                if (window.IsVisible || window.FindName("SettingsButton") is null) throw new InvalidOperationException();
                var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var run = await runs.SubmitAsync(new(Guid.NewGuid(), "Package local demo", new("package-smoke"), AllowedToolIds: []), timeout.Token);
                while (runs.GetRun(run.Id).Status != RunStatus.Completed) await Task.Delay(25, timeout.Token);
                if (runs.GetRuns().Count != 1 || host.Services.GetRequiredService<IDesktopAccessCoordinator>().Snapshot.OwnerRunId is not null
                    || (await host.Services.GetRequiredService<IRunRepository>().GetAsync(run.Id))?.Run.Status != RunStatus.Completed)
                    throw new InvalidOperationException();
            }
            finally { window?.Close(); await host.StopAsync(); }
            Console.WriteLine("PACKAGE SMOKE OK; runtimeLocal=True; wpfLocal=True; sqlite=True; xaml=True; completed=1; nativeInput=0; productionData=False; startupChanged=False.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static bool LocalAssembly(Type type) => string.Equals(Path.GetDirectoryName(type.Assembly.Location),
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
}
