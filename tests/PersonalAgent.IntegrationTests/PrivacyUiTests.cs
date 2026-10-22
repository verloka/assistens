using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class PrivacyUiTests
{
    [Fact]
    public Task PrivacyXamlValidatesSavesAndClearsSelectedHistoryWithoutChangingRunStatus() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); var run = await HistoryRetentionTests.SeedAsync(database);
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(logging => logging.ClearProviders()).Build();
        await host.StartAsync();
        var main = host.Services.GetRequiredService<MainWindow>();
        var dashboard = host.Services.GetRequiredService<DashboardViewModel>();
        var model = host.Services.GetRequiredService<PrivacyViewModel>();
        var window = new PrivacyWindow(model);
        try
        {
            window.Measure(new Size(560, 460)); window.Arrange(new Rect(0, 0, 560, 460)); window.UpdateLayout();
            Assert.NotNull(main.FindName("PrivacyButton")); Assert.False(window.ShowActivated);
            model.ReloadCommand.Execute(null); await model.ReloadCommand.ExecutionTask!;
            Assert.False(model.Enabled); Assert.Equal("30", model.Days);
            dashboard.SelectedHistory = dashboard.History.Single(r => r.Id == run.Id);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(dashboard.CurrentRun!.ContentClearedAtUtc);

            var days = (TextBox)window.FindName("RetentionDays"); days.Text = "0";
            days.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.SaveCommand.Execute(null); await model.SaveCommand.ExecutionTask!;
            Assert.Contains("1 to 36500", model.Message);
            Assert.Equal(new HistoryRetentionPolicy(), await host.Services.GetRequiredService<IHistoryRetentionRepository>().ReadPolicyAsync());
            days.Text = "30"; days.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var clear = (Button)window.FindName("ClearExpiredHistory");
            Assert.Same(model.ClearCommand, clear.Command);
            clear.Command.Execute(null); await model.ClearCommand.ExecutionTask!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("1 tasks", model.Message);
            Assert.NotNull(dashboard.CurrentRun!.ContentClearedAtUtc);
            Assert.NotNull(dashboard.SelectedHistory!.ContentClearedAtUtc);
            Assert.Equal(run.Status, dashboard.CurrentRun.Status);
            Assert.Equal(SqliteHistoryRetentionRepository.ClearedText, dashboard.Summary);
            Assert.All(dashboard.Events, e => Assert.Equal(SqliteHistoryRetentionRepository.ClearedText, e.Message));
            model.Enabled = true; model.SaveCommand.Execute(null); await model.SaveCommand.ExecutionTask!;
            Assert.Equal(new(true, 30), await host.Services.GetRequiredService<IHistoryRetentionRepository>().ReadPolicyAsync());
        }
        finally { window.Close(); main.Close(); await host.StopAsync(); }
    });

    internal static Task OnWpfThread(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
