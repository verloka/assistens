using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.IO;

namespace PersonalAgent.IntegrationTests;

public sealed class DashboardTests
{
    [Fact]
    public Task DashboardUsesCommonCommandsAndShowsLiveProgressWhileDispatcherResponds() => OnWpfThread(async () =>
    {
        var entered = Signal();
        var release = Signal();
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => new FakeAgentRuntime(
                [new("Observable local step")], async (id, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    await provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync(id, ct);
                }))).Build();
        await host.StartAsync();
        var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var model = host.Services.GetRequiredService<DashboardViewModel>();
            var submit = (Button)window.FindName("SubmitTaskButton");
            Assert.Same(model.SubmitCommand, submit.Command);
            ((TextBox)window.FindName("TaskInstructions")).Text = "Read a local test value";
            Assert.Equal("Read a local test value", model.Instructions);
            submit.Command.Execute(null);
            var execution = model.SubmitCommand.ExecutionTask!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.IsActive);
            Assert.False(execution.IsCompleted);
            Assert.False(submit.Command.CanExecute(null));
            var responsive = false;
            await window.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Background);
            Assert.True(responsive);

            var paused = Until(model, () => model.Status == "Paused");
            var pause = (Button)window.FindName("PauseTaskButton");
            Assert.Same(model.PauseCommand, pause.Command);
            pause.Command.Execute(null);
            await model.PauseCommand.ExecutionTask!;
            release.SetResult();
            await paused;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal("Paused", ((TextBlock)window.FindName("RunStatusText")).Text);
            Assert.False(execution.IsCompleted);
            var id = model.CurrentRun!.Id;
            var resume = (Button)window.FindName("ResumeTaskButton");
            Assert.True(resume.Command.CanExecute(null));
            resume.Command.Execute(null);
            await model.ResumeCommand.ExecutionTask!;
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(id, model.CurrentRun.Id);
            Assert.Equal("Completed", ((TextBlock)window.FindName("RunStatusText")).Text);
            Assert.Contains("fake", ((TextBlock)window.FindName("RunSummaryText")).Text);
            Assert.Same(model.Events, ((ListBox)window.FindName("RunEventsList")).ItemsSource);
            Assert.Contains(model.Events, e => e.Message == "Observable local step");
            Assert.Equal(Enumerable.Range(1, model.Events.Count).Select(n => (long)n), model.Events.Select(e => e.Sequence));
            Assert.False(model.IsActive);
        }
        finally { release.TrySetResult(); window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public Task DashboardCancelAndShutdownDoNotFreezeDispatcherOrResumeTask() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => new FakeAgentRuntime(
                waitAtBoundary: async (id, ct) =>
                {
                    await provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync(id, ct);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }))).Build();
        await host.StartAsync();
        var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var model = host.Services.GetRequiredService<DashboardViewModel>();
            var running = Until(model, () => model.Status == "Running");
            model.SubmitCommand.Execute(null);
            await running;
            var first = model.SubmitCommand.ExecutionTask!;
            var cancel = (Button)window.FindName("CancelTaskButton");
            Assert.Same(model.CancelCommand, cancel.Command);
            cancel.Command.Execute(null);
            await model.CancelCommand.ExecutionTask!;
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Cancelled, model.CurrentRun!.Status);
            Assert.False(model.ResumeCommand.CanExecute(null));

            running = Until(model, () => model.Status == "Running");
            model.SubmitCommand.Execute(null);
            await running;
            var second = model.SubmitCommand.ExecutionTask!;
            var id = model.CurrentRun!.Id;
            await host.StopAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RunStatus.Interrupted, host.Services.GetRequiredService<TaskRunCoordinator>().GetRun(id).Status);
            Assert.False(model.SubmitCommand.CanExecute(null));
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public Task DashboardReopensPersistedHistoryThroughRealHostAndXaml() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        Guid id;
        IReadOnlyList<RunEvent> original;
        using (var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => new FakeAgentRuntime(
                [new("Persisted visible step")], provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync))).Build())
        {
            await host.StartAsync();
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                await Bind(window);
                var model = host.Services.GetRequiredService<DashboardViewModel>();
                model.SubmitCommand.Execute(null);
                await model.SubmitCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(RunStatus.Completed, model.CurrentRun!.Status);
                id = model.CurrentRun.Id;
                original = model.Events.ToArray();
                Assert.Single(model.History);
            }
            finally { window.Close(); await host.StopAsync(); }
        }
        using (var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).Build())
        {
            await host.StartAsync();
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                await Bind(window);
                var model = host.Services.GetRequiredService<DashboardViewModel>();
                var history = (ComboBox)window.FindName("RunHistoryList");
                Assert.Same(model.History, history.ItemsSource);
                Assert.Equal(id, Assert.Single(model.History).Id);
                history.SelectedItem = model.History[0];
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(id, model.CurrentRun!.Id);
                Assert.Equal("Completed", ((TextBlock)window.FindName("RunStatusText")).Text);
                Assert.Contains("fake", ((TextBlock)window.FindName("RunSummaryText")).Text);
                Assert.Equal(original, model.Events);
                Assert.Contains(model.Events, value => value.Message == "Persisted visible step");
                Assert.False(model.IsActive);
                Assert.False(model.ResumeCommand.CanExecute(null));
            }
            finally { window.Close(); await host.StopAsync(); }
        }
    });

    [Fact]
    public Task DashboardStartupShowsInterruptedAttemptAndNeverOffersResume() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
        var repository = new SqliteRunRepository(database);
        Guid id;
        using (var beforeCrash = new TaskRunCoordinator(repository))
        {
            var queued = await beforeCrash.SubmitAsync(new(Guid.NewGuid(), "Interrupted local fixture", new TaskOrigin("test")));
            id = queued.Id;
            var running = TaskRunStateMachine.Transition(queued, RunStatus.Running, queued.RunVersion, DateTimeOffset.UtcNow);
            await repository.UpdateAsync(running, queued.RunVersion, [new RunEvent
            {
                Id = Guid.NewGuid(), RunId = id, Sequence = 3, EventType = RunEventType.RunStarted,
                TimestampUtc = DateTimeOffset.UtcNow, Message = "Last durable running state"
            }]);
        }
        using var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).Build();
        await host.StartAsync();
        var window = host.Services.GetRequiredService<MainWindow>();
        try
        {
            await Bind(window);
            var model = host.Services.GetRequiredService<DashboardViewModel>();
            ((ComboBox)window.FindName("RunHistoryList")).SelectedItem = Assert.Single(model.History);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal(id, model.CurrentRun!.Id);
            Assert.Equal("Interrupted", ((TextBlock)window.FindName("RunStatusText")).Text);
            Assert.Contains("Verify external effects", ((TextBlock)window.FindName("RunSummaryText")).Text);
            Assert.False(model.ResumeCommand.CanExecute(null));
            Assert.Equal(RunStatus.Interrupted, (await repository.GetAsync(id))!.Run.Status);
        }
        finally { window.Close(); await host.StopAsync(); }
    });

    [Fact]
    public async Task HistoryOwnershipPreventsSecondHostFromRecoveringLiveRun()
    {
        using var database = new TemporaryAgentDatabase();
        var entered = Signal();
        var release = Signal();
        using var first = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<IAgentRuntime>(provider => new FakeAgentRuntime(waitAtBoundary: async (id, ct) =>
            {
                entered.TrySetResult(); await release.Task.WaitAsync(ct);
                await provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync(id, ct);
            }))).Build();
        await first.StartAsync();
        try
        {
            var state = first.Services.GetRequiredService<TaskRunCoordinator>();
            var run = await state.SubmitAsync(new(Guid.NewGuid(), "Live owned task", new TaskOrigin("test")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var second = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).Build())
            {
                await Assert.ThrowsAsync<IOException>(() => second.StartAsync());
                await second.StopAsync();
            }
            Assert.Equal(RunStatus.Running, state.GetRun(run.Id).Status);
            var repository = new SqliteRunRepository(database);
            Assert.Equal(RunStatus.Running, (await repository.GetAsync(run.Id))!.Run.Status);
            release.SetResult();
            await state.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await first.StopAsync(); }
        using var reopened = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders()).Build();
        await reopened.StartAsync();
        await reopened.StopAsync();
    }

    private static async Task Until(DashboardViewModel model, Func<bool> predicate)
    {
        if (predicate()) return;
        var signal = Signal();
        PropertyChangedEventHandler handler = (_, _) => { if (predicate()) signal.TrySetResult(); };
        model.PropertyChanged += handler;
        try { if (predicate()) return; await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { model.PropertyChanged -= handler; }
    }

    private static async Task Bind(Window window)
    {
        window.Measure(new Size(900, 800)); window.Arrange(new Rect(0, 0, 900, 800)); window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = Signal();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
