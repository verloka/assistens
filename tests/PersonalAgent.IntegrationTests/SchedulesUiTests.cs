using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class SchedulesUiTests
{
    [Fact]
    public Task TenMinuteCheckCanBeEditedDisabledAndReopenedWithItsOwnHistory() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); Guid id; Guid runId;
        using (var host = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).Build())
        {
            var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
            var main = host.Services.GetRequiredService<MainWindow>(); var window = new SchedulesWindow(model);
            try
            {
                await Bind(main); await Bind(window); Assert.NotNull(main.FindName("SchedulesButton")); Assert.False(window.ShowActivated); Assert.False(window.Topmost);
                ((TextBox)window.FindName("ScheduleName")).Text = "Ten minute check";
                ((TextBox)window.FindName("ScheduleInstructions")).Text = "Read a synthetic local value";
                ((TextBox)window.FindName("ScheduleMinutes")).Text = "10";
                ((ComboBox)window.FindName("ScheduleTimezone")).Text = "Europe/Kyiv";
                await Pump();
                var save = (Button)window.FindName("SaveScheduleButton"); Assert.Same(model.SaveCommand, save.Command);
                await Execute(model.SaveCommand); await Pump();
                var created = Assert.Single(model.Schedules); id = created.Schedule.Id;
                Assert.Equal(10, created.Schedule.Recurrence.EveryMinutes); Assert.Equal("Ten minute check", created.Task.Name);
                Assert.Equal("Europe/Kyiv", created.Schedule.TimeZoneId); Assert.Equal(clock.Now.AddMinutes(10), created.Schedule.NextRunAtUtc);
                Assert.Equal(3, model.FutureTriggers.Count); Assert.Contains("Europe/Kyiv", model.FutureTriggers[0]);
                var repo = host.Services.GetRequiredService<IScheduleRepository>(); var runs = host.Services.GetRequiredService<TaskRunCoordinator>();
                clock.Now += TimeSpan.FromMinutes(10); await host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync();
                var run = Assert.Single(runs.GetRuns()); runId = run.Id;
                using (var stop = new CancellationTokenSource())
                {
                    var running = new TaskRunner(runs, new PersonalAgent.Agent.FakeAgentRuntime()).RunAsync(stop.Token);
                    await runs.WaitForTerminalAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); await running;
                }
                // The dispatcher advanced the schedule; refresh explicitly before editing the loaded revision.
                await Execute(model.ReloadCommand); await Bind(window);
                ((TextBox)window.FindName("ScheduleName")).Text = "Renamed check";
                ((TextBox)window.FindName("ScheduleInstructions")).Text = "New instructions for future runs"; await Pump();
                await Execute(model.SaveCommand); await Pump(); Assert.Equal("Renamed check", model.Selected!.Task.Name);
                Assert.Equal("Read a synthetic local value", (await host.Services.GetRequiredService<IRunRepository>().GetAsync(runId))!.Run.Instructions);
                var toggle = (Button)window.FindName("ToggleScheduleButton"); Assert.Same(model.ToggleCommand, toggle.Command);
                await Execute(model.ToggleCommand); await Pump();
                Assert.False(model.Selected!.Schedule.Enabled); Assert.Null(model.Selected.Schedule.NextRunAtUtc); Assert.Single(model.Schedules);
                Assert.Equal(runId, Assert.Single(model.History).Id); Assert.Single(((ListBox)window.FindName("ScheduleHistory")).Items);
                clock.Now += TimeSpan.FromMinutes(40); Assert.Empty(await host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync());
                Assert.Single(await host.Services.GetRequiredService<IRunRepository>().ListAsync()); Assert.Equal(id, (await repo.GetAsync(id))!.Schedule.Id);
            }
            finally { window.Close(); main.Close(); }
        }
        using var reopened = AppHost.CreateBuilder([], database.DatabasePath).ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).Build();
        var restored = reopened.Services.GetRequiredService<SchedulesViewModel>(); await restored.Initialization;
        restored.Selected = Assert.Single(restored.Schedules); await restored.HistoryLoading;
        Assert.Equal(id, restored.Selected.Schedule.Id); Assert.False(restored.Selected.Schedule.Enabled); Assert.Equal(runId, Assert.Single(restored.History).Id);
        await Execute(restored.ToggleCommand); Assert.True(restored.Selected!.Schedule.Enabled); Assert.True(restored.Selected.Schedule.NextRunAtUtc > clock.Now);
    });

    [Theory]
    [InlineData(ScheduleMode.Once)] [InlineData(ScheduleMode.Daily)] [InlineData(ScheduleMode.Weekdays)]
    public Task CalendarEditorRoundTripsLocalTimeTimezoneAndDays(ScheduleMode mode) => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); using var host = AppHost.CreateBuilder([], database.DatabasePath)
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).Build();
        var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
        var window = new SchedulesWindow(model);
        try
        {
            await Bind(window); ((TextBox)window.FindName("ScheduleInstructions")).Text = "Calendar fixture";
            ((ComboBox)window.FindName("ScheduleMode")).SelectedItem = mode;
            ((DatePicker)window.FindName("ScheduleOnceDate")).SelectedDate = new DateTime(2026, 10, 21);
            ((TextBox)window.FindName("ScheduleTime")).Text = "09:15";
            ((ComboBox)window.FindName("ScheduleTimezone")).Text = "Europe/Kyiv";
            foreach (var day in model.Days) day.Selected = day.Day is DayOfWeek.Wednesday or DayOfWeek.Friday;
            await Pump(); await Execute(model.SaveCommand); await Pump();
            var stored = Assert.Single(model.Schedules); Assert.Equal(mode, stored.Schedule.Recurrence.Mode);
            if (mode == ScheduleMode.Once) Assert.Equal(DateTimeOffset.Parse("2026-10-21T06:15:00Z"), stored.Schedule.Recurrence.OnceAtUtc);
            else Assert.Equal(new TimeOnly(9, 15), stored.Schedule.Recurrence.LocalTime);
            if (mode == ScheduleMode.Weekdays) Assert.Equal(new[] { DayOfWeek.Wednesday, DayOfWeek.Friday }, stored.Schedule.Recurrence.Days);
            Assert.Contains("09:15:00", model.FutureTriggers[0]);
            await Execute(model.NewCommand); await Execute(model.ReloadCommand); await Pump(); Assert.Equal("fake", model.ModelProfileId);
            ((ListBox)window.FindName("ScheduleList")).SelectedItem = model.Schedules.Single(); await Pump(); await model.HistoryLoading;
            Assert.Equal(mode, model.Mode); Assert.Equal("09:15:00", model.LocalTime); Assert.Equal("Europe/Kyiv", model.TimeZoneId);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InvalidEditsAndConcurrentRevisionCannotOverwriteTheSavedSchedule() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        using var host = AppHost.CreateBuilder([], database.DatabasePath).Build();
        var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
        model.Instructions = "Fixture";
        foreach (var invalid in new[] { "0", "-1", "nonsense", "1.5", "999999999999" })
        {
            model.EveryMinutes = invalid; await Execute(model.SaveCommand); Assert.Empty(model.Schedules); Assert.Contains("Check name", model.Message);
        }
        model.EveryMinutes = "10"; model.MisfirePolicy = MisfirePolicy.CatchUp; await Execute(model.SaveCommand); Assert.Empty(model.Schedules);
        model.MisfirePolicy = MisfirePolicy.RunOnce; await Execute(model.SaveCommand); var original = model.Selected!;
        var repo = host.Services.GetRequiredService<IScheduleRepository>();
        await repo.SaveAsync(original with { Task = original.Task with { Name = "Concurrent owner edit" } }, original.Schedule.Version);
        model.Name = "Stale edit"; await Execute(model.SaveCommand); Assert.Contains("changed", model.Message);
        await Execute(model.ToggleCommand); Assert.Contains("changed", model.Message);
        var current = (await repo.GetAsync(original.Schedule.Id))!; Assert.Equal("Concurrent owner edit", current.Task.Name); Assert.True(current.Schedule.Enabled);
        await Execute(model.ReloadCommand); Assert.Equal("Concurrent owner edit", model.Name);
        model.Mode = ScheduleMode.Weekdays; foreach (var day in model.Days) day.Selected = false;
        await Execute(model.SaveCommand); Assert.Contains("Check name", model.Message);
        Assert.Equal(ScheduleMode.Interval, (await repo.GetAsync(original.Schedule.Id))!.Schedule.Recurrence.Mode);
    });

    [Fact]
    public Task HistoryUsesScheduleOccurrenceIdentityEvenWhenTaskTemplateIsShared() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); using var host = AppHost.CreateBuilder([], database.DatabasePath)
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).Build();
        var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
        model.Instructions = "Shared template"; await Execute(model.SaveCommand); var first = model.Selected!;
        var repo = host.Services.GetRequiredService<IScheduleRepository>();
        var second = await repo.SaveAsync(first with { Schedule = first.Schedule with { Id = Guid.NewGuid(), Version = 0 } });
        clock.Now += TimeSpan.FromMinutes(10); await host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync();
        var commands = host.Services.GetRequiredService<TaskRunCoordinator>();
        await commands.SubmitAsync(new(Guid.NewGuid(), "Manual run sharing template", new("desktop"), TaskDefinitionId: first.Task.Id));
        Assert.Equal(3, (await host.Services.GetRequiredService<IRunRepository>().ListAsync()).Count);
        var firstRun = Assert.Single(await repo.ListRunsAsync(first.Schedule.Id)); var secondRun = Assert.Single(await repo.ListRunsAsync(second.Schedule.Id));
        Assert.NotEqual(firstRun.Id, secondRun.Id); Assert.Equal(first.Task.Id, secondRun.TaskDefinitionId);
        await Execute(model.ReloadCommand); model.Selected = model.Schedules.Single(value => value.Schedule.Id == second.Schedule.Id); await model.HistoryLoading;
        Assert.Equal(secondRun.Id, Assert.Single(model.History).Id);
        model.Selected = model.Schedules.Single(value => value.Schedule.Id == first.Schedule.Id); await model.HistoryLoading;
        Assert.Equal(firstRun.Id, Assert.Single(model.History).Id);
    });

    [Fact]
    public Task ProfileSelectionAndExactOnceInstantSurviveOrdinaryEditsWithoutCallingProvider() => OnWpfThread(async () =>
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); using var host = AppHost.CreateBuilder([], database.DatabasePath)
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).Build();
        await host.Services.GetRequiredService<IModelProfileRepository>().SaveAsync(new()
        { Id = "fixture-profile", ProviderId = "openai", ModelId = "fixture-model", SecretRef = "dpapi:" + Guid.NewGuid().ToString("N") });
        var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
        model.Instructions = "No provider call"; await Execute(model.SaveCommand); var first = model.Selected!;
        // The second 03:30 during the Kyiv fold is a valid explicitly scheduled instant.
        var instant = DateTimeOffset.Parse("2026-10-25T01:30:00.1234567Z");
        await host.Services.GetRequiredService<IScheduleRepository>().SaveAsync(first with
        {
            Task = first.Task with { ModelProfileId = "fixture-profile" },
            Schedule = first.Schedule with { TimeZoneId = "Europe/Kyiv", Recurrence = new() { Mode = ScheduleMode.Once, OnceAtUtc = instant } }
        }, first.Schedule.Version);
        await Execute(model.ReloadCommand);
        var window = new SchedulesWindow(model);
        try
        {
            await Bind(window); Assert.Equal("fixture-profile", ((ComboBox)window.FindName("ScheduleModel")).SelectedValue);
            ((TextBox)window.FindName("ScheduleName")).Text = "Renamed without changing the clock"; await Pump();
            await Execute(model.SaveCommand); Assert.Equal(instant, model.Selected!.Schedule.Recurrence.OnceAtUtc);
            Assert.Equal("fixture-profile", model.Selected.Task.ModelProfileId); Assert.Empty(await host.Services.GetRequiredService<IRunRepository>().ListAsync());
            await Execute(model.NewCommand); await Pump(); Assert.Equal("fake", model.ModelProfileId);
            ((ComboBox)window.FindName("ScheduleModel")).SelectedValue = "fixture-profile";
            ((TextBox)window.FindName("ScheduleInstructions")).Text = "Selected from profile list"; await Pump();
            await Execute(model.SaveCommand); Assert.Equal("fixture-profile", model.Selected!.Task.ModelProfileId);
            Assert.Equal(2, model.Schedules.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MissingStorageDisablesAllMutationCommands() => OnWpfThread(async () =>
    {
        using var host = AppHost.CreateBuilder([]).Build(); var model = host.Services.GetRequiredService<SchedulesViewModel>(); await model.Initialization;
        Assert.False(model.CanEdit); Assert.False(model.SaveCommand.CanExecute(null)); Assert.False(model.NewCommand.CanExecute(null)); Assert.False(model.ToggleCommand.CanExecute(null));
        Assert.Contains("unavailable", model.Message);
    });

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T08:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task Execute(AsyncCommand command) { Assert.True(command.CanExecute(null)); command.Execute(null); await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10)); }
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Bind(Window window) { window.Measure(new Size(1040, 820)); window.Arrange(new Rect(0, 0, 1040, 820)); window.UpdateLayout(); await Pump(); }
    private static Task OnWpfThread(Func<Task> action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await action(); result.TrySetResult(); } catch (Exception error) { result.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
