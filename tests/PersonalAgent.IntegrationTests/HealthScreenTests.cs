using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;

namespace PersonalAgent.IntegrationTests;

public sealed class HealthScreenTests
{
    [Fact]
    public Task HealthScreenBindsRealHostAndStaysResponsiveDuringLongOperation() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(logging => logging.ClearProviders()).Build();
        await host.StartAsync();
        var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            var model = Assert.IsType<HealthViewModel>(window.DataContext);
            await Bind(window);
            Assert.Equal("Running", ((TextBlock)window.FindName("HostStatusText")).Text);
            Assert.Contains("LifecycleLoggingService", ((ListBox)window.FindName("ComponentsList")).Items.Cast<string>());

            var button = (Button)window.FindName("DiagnosticButton");
            Assert.Same(model.RunDiagnosticCommand, button.Command);
            button.Command.Execute(null);
            var operation = Assert.IsAssignableFrom<Task>(model.RunDiagnosticCommand.ExecutionTask);
            Assert.True(model.IsBusy);
            Assert.False(button.Command.CanExecute(null));
            button.Command.Execute(null);
            Assert.Same(operation, model.RunDiagnosticCommand.ExecutionTask);

            // A real WPF dispatcher callback must run while the five-second operation is pending.
            var uiResponded = false;
            await window.Dispatcher.InvokeAsync(() => uiResponded = true, DispatcherPriority.Background);
            Assert.True(uiResponded);
            Assert.False(operation.IsCompleted);
            Assert.False(button.IsEnabled);

            await operation;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(model.IsBusy);
            Assert.True(button.IsEnabled);
            Assert.Equal("Test operation completed", ((TextBlock)window.FindName("DiagnosticStatusText")).Text);
        }
        finally
        {
            window.Close();
            await host.StopAsync();
        }
    });

    [Fact]
    public Task HealthScreenTracksHostTransitionsAndCancelsDiagnosticOnShutdown() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(logging => logging.ClearProviders()).Build();
        var model = host.Services.GetRequiredService<HealthViewModel>();
        var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            Assert.Equal("Starting", ((TextBlock)window.FindName("HostStatusText")).Text);
            Assert.False(model.RunDiagnosticCommand.CanExecute(null));

            await host.StartAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Running", ((TextBlock)window.FindName("HostStatusText")).Text);
            model.RunDiagnosticCommand.Execute(null);
            var operation = Assert.IsAssignableFrom<Task>(model.RunDiagnosticCommand.ExecutionTask);
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await operation;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal("Stopping", ((TextBlock)window.FindName("HostStatusText")).Text);
            Assert.Contains("cancelled", model.DiagnosticStatus);
            Assert.False(model.RunDiagnosticCommand.CanExecute(null));
            await host.StopAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Stopped", ((TextBlock)window.FindName("HostStatusText")).Text);
        }
        finally
        {
            window.Close();
            await host.StopAsync();
        }
    });

    private static async Task Bind(Window window)
    {
        // Load and lay out the actual XAML without showing a window or taking desktop focus.
        window.Measure(new Size(800, 450));
        window.Arrange(new Rect(0, 0, 800, 450));
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await test();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
