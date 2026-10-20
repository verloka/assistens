using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Agent;
using PersonalAgent.App;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

[Collection("Desktop UI")]
public sealed class ScheduledDesktopTests
{
    [Fact]
    public async Task SchedulerRestartDuringBusyAdmissionNeverReplaysOldRunOrReusesItsWarning()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.Zero) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, hosted: true);
        var old = await fixture.Warning(); var oldId = fixture.RunId; var oldNative = fixture.Native;
        fixture.Gate.Present(old.Id); fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("ScheduleRunActive", Assert.Single(await fixture.Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync()).ErrorCode);
        fixture.NoGui(); Assert.Equal(1, fixture.RuntimeCalls);
        await fixture.Restart();
        var history = await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync();
        Assert.Equal(RunStatus.Interrupted, history.Single(value => value.Id == oldId).Status);
        var current = Assert.Single(history, value => !TaskRunStateMachine.IsTerminal(value.Status)); fixture.RunId = current.Id;
        Assert.NotEqual(oldId, fixture.RunId); var next = await fixture.Warning(); Assert.NotEqual(old.Id, next.Id);
        Assert.False(fixture.Gate.Decide(old.Id, ScheduledDesktopDecision.StartNow)); fixture.NoGui();
        Assert.Equal(0, oldNative.Captures); Assert.Empty(oldNative.Inputs); Assert.Equal(1, fixture.RuntimeCalls);
        fixture.Gate.Present(next.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15)); idle.Snapshot = new(true, TimeSpan.FromSeconds(60));
        await fixture.Finished(RunStatus.Completed); Assert.Single(fixture.Native.Inputs);
        await fixture.Restart(); Assert.Equal(0, fixture.RuntimeCalls); fixture.NoGui(); Assert.Empty(fixture.Gate.GetWarnings());
        Assert.Equal(2, (await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync()).Count);
    }

    [Fact]
    public async Task SchedulerRestartAfterExpiryRetainsSkippedOutcomeAndFutureSlotWithoutNewExecution()
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, hosted: true);
        var old = await fixture.Warning(); var id = fixture.RunId; fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Finished(RunStatus.Skipped); fixture.NoGui();
        var expected = (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.NextRunAtUtc;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await fixture.Restart(); fixture.NoGui(); Assert.Equal(0, fixture.RuntimeCalls); Assert.Empty(fixture.Gate.GetWarnings());
            Assert.False(fixture.Gate.Decide(old.Id, ScheduledDesktopDecision.StartNow));
            var run = Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
            Assert.Equal(id, run.Id); Assert.Equal(RunStatus.Skipped, run.Status); Assert.Equal("DeferredExpired", run.ErrorCode);
            Assert.Equal(expected, (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.NextRunAtUtc);
        }
    }

    [Fact]
    public async Task SchedulerConcurrentTicksDuringActiveKeyboardFixtureKeepOneWarningUntilIdleWithoutGuiInterference()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.Zero) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, hosted: true);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        var dispatchers = Enumerable.Range(0, 8).Select(_ => new ScheduleDispatcher(fixture.Schedules, fixture.Runs, timeProvider: fixture.Clock));
        var outcomes = await Task.WhenAll(dispatchers.Select(value => value.TickAsync()));
        Assert.All(outcomes, value => Assert.Equal("ScheduleRunActive", Assert.Single(value).ErrorCode));
        fixture.NoGui(); Assert.Equal(warning.Id, fixture.Gate.GetWarnings().Single().Id); Assert.Equal(1, fixture.RuntimeCalls);
        Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
        idle.Snapshot = new(true, TimeSpan.FromSeconds(60)); await fixture.Finished(RunStatus.Completed);
        Assert.Single(fixture.Native.Inputs); Assert.Empty(await fixture.Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync());
        Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
    }

    [Fact]
    public async Task SchedulerKyivFoldWhileBusyHasOneCalendarOccurrenceAcrossHostedRestart()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.Zero) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, hosted: true,
            options: new() { MaxDesktopWait = TimeSpan.FromHours(2) }, startAtUtc: DateTimeOffset.Parse("2026-10-25T00:20:00Z"),
            recurrence: new() { Mode = ScheduleMode.Daily, LocalTime = new(3, 30) }, timeZoneId: "Europe/Kyiv");
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), fixture.Schedule.Schedule.NextRunAtUtc);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await fixture.Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync());
        fixture.NoGui(); Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
        Assert.Equal(warning.Id, fixture.Gate.GetWarnings().Single().Id);
        idle.Snapshot = new(true, TimeSpan.FromSeconds(60)); await fixture.Finished(RunStatus.Completed);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T01:30:00Z"), (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.NextRunAtUtc);
        await fixture.Restart(); Assert.Equal(0, fixture.RuntimeCalls); fixture.NoGui(); Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CountdownCannotGrantBeforePresentationAndFifteenSeconds(bool immediateAcquire)
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown, immediateAcquire);
        var warning = await fixture.Warning(); Assert.Equal("Synthetic scheduled check", warning.TaskName);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(350);
        fixture.NoGui(); Assert.Null(fixture.Gate.GetWarnings().Single().SecondsRemaining);
        Assert.True(fixture.Gate.Present(warning.Id)); fixture.Clock.Advance(TimeSpan.FromSeconds(14)); await Task.Delay(350);
        fixture.NoGui(); Assert.Equal(1, fixture.Gate.GetWarnings().Single().SecondsRemaining);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await fixture.Finished(RunStatus.Completed);
        Assert.True(fixture.Native.Captures > 0); Assert.Single(fixture.Native.Inputs);
    }

    [Theory]
    [InlineData(ScheduledDesktopPolicy.StartWhenIdle)] [InlineData(ScheduledDesktopPolicy.AskEveryTime)] [InlineData(ScheduledDesktopPolicy.StartAfterCountdown)]
    public Task ExplicitStartInActualXamlPermitsOnlyThisRequest(ScheduledDesktopPolicy policy) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(policy); var warning = await fixture.Warning();
        var model = new ScheduledDesktopWarningViewModel(fixture.Gate, warning.Id); var window = new ScheduledDesktopWarningWindow(model);
        try
        {
            await Bind(window); Assert.False(window.ShowActivated); Assert.False(window.Topmost);
            Assert.Equal(warning.TaskName, ((TextBlock)window.FindName("WarningTaskName")).Text);
            var button = (Button)window.FindName("StartScheduledDesktopButton"); Assert.Same(model.StartCommand, button.Command); Assert.False(button.IsDefault);
            Assert.False(button.Command!.CanExecute(null)); model.Presented(); await Bind(window);
            if (policy != ScheduledDesktopPolicy.StartAfterCountdown) { fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(350); fixture.NoGui(); }
            Assert.True(button.Command.CanExecute(null)); button.Command.Execute(null); await model.StartCommand.ExecutionTask!;
            await fixture.Finished(RunStatus.Completed); Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow));
            Assert.True(fixture.Native.Captures > 0); Assert.Single(fixture.Native.Inputs); Assert.Empty(fixture.Gate.GetWarnings());
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task SkipAndDisablePersistTerminalOccurrenceWithoutNativeEffects(bool disable) => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown); var warning = await fixture.Warning();
        var model = new ScheduledDesktopWarningViewModel(fixture.Gate, warning.Id); var window = new ScheduledDesktopWarningWindow(model);
        try
        {
            await Bind(window); model.Presented(); await Bind(window);
            var button = (Button)window.FindName(disable ? "DisableScheduledDesktopButton" : "SkipScheduledDesktopButton");
            Assert.True(button.Command!.CanExecute(null)); button.Command.Execute(null);
            await (disable ? model.DisableCommand : model.SkipCommand).ExecutionTask!;
            await fixture.Finished(RunStatus.Skipped); fixture.NoGui();
            var stored = (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!;
            Assert.Equal(!disable, stored.Schedule.Enabled); Assert.Equal(!disable, stored.Schedule.NextRunAtUtc is not null);
            Assert.Single(await fixture.Schedules.ListRunsAsync(stored.Schedule.Id));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClosingWarningDefersWithoutGrantingEvenAfterCountdownThenExplicitStartWorks() => OnWpfThread(async () =>
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown); var warning = await fixture.Warning();
        var model = new ScheduledDesktopWarningViewModel(fixture.Gate, warning.Id); var first = new ScheduledDesktopWarningWindow(model);
        await Bind(first); model.Presented(); first.Close(); fixture.Clock.Advance(TimeSpan.FromMinutes(2)); await Task.Delay(350);
        fixture.NoGui(); Assert.True(fixture.Gate.GetWarnings().Single().Deferred);
        var reopened = new ScheduledDesktopWarningWindow(model);
        try
        {
            await Bind(reopened); model.Refresh(); Assert.Contains("Deferred", model.Status);
            model.StartCommand.Execute(null); await model.StartCommand.ExecutionTask!; await fixture.Finished(RunStatus.Completed);
        }
        finally { reopened.Close(); }
    });

    [Fact]
    public async Task CancelAndConfigurationChangeInvalidateOldConsent()
    {
        await using (var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown))
        {
            var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id);
            var task = (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!;
            await fixture.Schedules.SaveAsync(task with { Schedule = task.Schedule with { DesktopPolicy = ScheduledDesktopPolicy.AskEveryTime } }, task.Schedule.Version);
            await Until(() => fixture.Gate.GetWarnings().SingleOrDefault()?.Id is { } next && next != warning.Id);
            Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)); fixture.NoGui();
            var replacement = fixture.Gate.GetWarnings().Single(); fixture.Gate.Present(replacement.Id);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(350); fixture.NoGui();
            Assert.Equal(ScheduledDesktopPolicy.AskEveryTime, replacement.Policy);
            Assert.True(fixture.Gate.Decide(replacement.Id, ScheduledDesktopDecision.StartNow)); await fixture.Finished(RunStatus.Completed);
        }
        await using (var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown))
        {
            var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id);
            var run = fixture.Runs.GetRun(fixture.RunId); await fixture.Runs.CancelAsync(new(Guid.NewGuid(), run.Id, run.RunVersion));
            Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow));
            await fixture.Finished(RunStatus.Cancelled); fixture.NoGui();
        }
    }

    [Fact]
    public async Task BusyInputKeepsDefaultPolicyClosedUntilIdleThresholdAndCountdownBothPermit()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.Zero) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await Task.Delay(350); fixture.NoGui();
        idle.Snapshot = new(true, TimeSpan.FromSeconds(59)); await Task.Delay(350); fixture.NoGui();
        idle.Snapshot = new(true, TimeSpan.FromSeconds(60)); await fixture.Finished(RunStatus.Completed);
        Assert.True(idle.Reads > 1); Assert.Single(fixture.Native.Inputs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownIdleOrReadFailureDoesNotAuthorizeAutomaticAccessAndLockedDesktopCannotBeOverridden(bool fail)
    {
        var idle = new Idle { Snapshot = new(true, null), Fail = fail };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(350); fixture.NoGui();
        idle.Fail = false; idle.Snapshot = new(false, TimeSpan.FromHours(1));
        Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)); fixture.NoGui();
        idle.Snapshot = new(true, TimeSpan.Zero); // Deliberate Start now may override busy input on an available desktop.
        Assert.True(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)); await fixture.Finished(RunStatus.Completed);
    }

    [Fact]
    public async Task InputDuringAwaitedAdmissionWriteRequiresFreshWarningBeforeAutomaticGrant()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.FromSeconds(60)) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, blockAdmissionWrite: true);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.AdmissionWrite!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); fixture.NoGui();
        idle.Snapshot = new(true, TimeSpan.Zero); fixture.AdmissionWrite.Release.SetResult();
        await Until(() => fixture.Gate.GetWarnings().SingleOrDefault()?.Id is { } id && id != warning.Id);
        Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)); fixture.NoGui();
        idle.Snapshot = new(true, TimeSpan.FromSeconds(60)); await Task.Delay(350); fixture.NoGui();
        var next = await fixture.Warning(); fixture.Gate.Present(next.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.Finished(RunStatus.Completed);
    }

    [Fact]
    public async Task LocallyAcceptedDeferDuringAutomaticAdmissionWriteIsNotLost()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.FromSeconds(60)) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, blockAdmissionWrite: true);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.AdmissionWrite!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var version = fixture.Runs.GetRun(fixture.RunId).RunVersion;
        Assert.True(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.Defer)); fixture.AdmissionWrite.Release.SetResult();
        await Until(() => fixture.Runs.GetRun(fixture.RunId) is { Status: RunStatus.WaitingForDesktop } current && current.RunVersion > version);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(350); fixture.NoGui();
        Assert.True(fixture.Gate.GetWarnings().Single().Deferred);
        Assert.True(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow)); await fixture.Finished(RunStatus.Completed);
    }

    [Theory]
    [InlineData(ScheduledDesktopDecision.SkipOnce)] [InlineData(ScheduledDesktopDecision.DisableSchedule)]
    public async Task LocallyAcceptedSkipOrDisableDuringAutomaticAdmissionWriteHasNoGuiEffect(ScheduledDesktopDecision decision)
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.FromSeconds(60)) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, blockAdmissionWrite: true);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.AdmissionWrite!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Gate.Decide(warning.Id, decision)); fixture.AdmissionWrite.Release.SetResult();
        await fixture.Finished(RunStatus.Skipped); fixture.NoGui();
        Assert.Equal(decision != ScheduledDesktopDecision.DisableSchedule, (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.Enabled);
    }

    [Fact]
    public async Task HeadlessWarningExpiresAtDefaultTtlAndCoalescesOverdueSlotsWithoutImmediateRequeue()
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown);
        var warning = await fixture.Warning(); Assert.Equal(TimeSpan.FromMinutes(30), warning.WaitRemaining);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1)); await Task.Delay(350); fixture.NoGui();
        Assert.Equal(warning.Id, fixture.Gate.GetWarnings().Single().Id);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await fixture.Finished(RunStatus.Skipped); fixture.NoGui();
        var stored = (await fixture.Host.Services.GetRequiredService<IRunRepository>().GetAsync(fixture.RunId))!;
        Assert.Equal("DeferredExpired", stored.Run.ErrorCode); Assert.Contains("DeferredExpired", stored.Run.Reason);
        Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow));
        Assert.True((await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.NextRunAtUtc > fixture.Clock.GetUtcNow());
        Assert.Empty(await fixture.Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync());
        Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
    }

    [Fact]
    public async Task DeferAndConfigurationRefreshDoNotExtendOriginalDeadline()
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle);
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); Assert.True(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.Defer));
        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        var current = (await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!;
        await fixture.Schedules.SaveAsync(current with { Task = current.Task with { Name = "Owner rename during defer" } }, current.Schedule.Version);
        await Until(() => fixture.Gate.GetWarnings().SingleOrDefault()?.Id is { } id && id != warning.Id);
        var replacement = fixture.Gate.GetWarnings().Single(); Assert.Equal(TimeSpan.FromMinutes(10), replacement.WaitRemaining);
        Assert.False(fixture.Gate.Decide(warning.Id, ScheduledDesktopDecision.StartNow));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10)); await fixture.Finished(RunStatus.Skipped); fixture.NoGui();
        Assert.Equal("DeferredExpired", fixture.Runs.GetRun(fixture.RunId).ErrorCode);
    }

    [Fact]
    public async Task ThirtyMissedTenMinuteChecksRemainOneWaitingRunAndOneWarningThenExecuteOneCurrentCheck()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.Zero) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle,
            options: new() { MaxDesktopWait = TimeSpan.FromHours(6) });
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id);
        var dispatcher = fixture.Host.Services.GetRequiredService<ScheduleDispatcher>();
        for (var index = 0; index < 30; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal("ScheduleRunActive", Assert.Single(await dispatcher.TickAsync()).ErrorCode);
            Assert.Equal(warning.Id, fixture.Gate.GetWarnings().Single().Id);
            Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
        }
        fixture.NoGui(); idle.Snapshot = new(true, TimeSpan.FromSeconds(60)); await fixture.Finished(RunStatus.Completed);
        Assert.Single(fixture.Native.Inputs); Assert.Empty(await dispatcher.TickAsync());
        var stored = (await fixture.Host.Services.GetRequiredService<IRunRepository>().GetAsync(fixture.RunId))!;
        Assert.Single(stored.Events, value => value.Message.Contains("Waiting for scheduled desktop warning"));
        Assert.Single(await fixture.Host.Services.GetRequiredService<IRunRepository>().ListAsync());
        Assert.True((await fixture.Schedules.GetAsync(fixture.Schedule.Schedule.Id))!.Schedule.NextRunAtUtc > fixture.Clock.GetUtcNow());
    }

    [Fact]
    public async Task ExpiryDuringAwaitedAdmissionWriteCannotGrantDesktop()
    {
        var idle = new Idle { Snapshot = new(true, TimeSpan.FromSeconds(60)) };
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartWhenIdle, idle: idle, blockAdmissionWrite: true,
            options: new() { MaxDesktopWait = TimeSpan.FromMinutes(1) });
        var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.AdmissionWrite!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); fixture.NoGui();
        fixture.Clock.Advance(TimeSpan.FromSeconds(46)); fixture.AdmissionWrite.Release.SetResult();
        await fixture.Finished(RunStatus.Skipped); fixture.NoGui(); Assert.Equal("DeferredExpired", fixture.Runs.GetRun(fixture.RunId).ErrorCode);
    }

    [Fact]
    public async Task CoalescingUsesVersionedPersistedWaitingIdentityAndDoesNotChangeExplicitMisfirePolicy()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var clock = new Clock(); var repo = new SqliteScheduleRepository(database, clock);
        using var runs = new TaskRunCoordinator(new SqliteRunRepository(database));
        var now = clock.GetUtcNow(); var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Coalescing fixture", Instructions = "Synthetic waiting run", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
        var schedule = await repo.SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC", CreatedAtUtc = now, UpdatedAtUtc = now }));
        clock.Advance(TimeSpan.FromMinutes(10)); await new ScheduleDispatcher(repo, runs, timeProvider: clock).TickAsync();
        var runId = Assert.Single(runs.GetRuns()).Id; var runtime = new WaitingRuntime(runs);
        using var stop = new CancellationTokenSource(); var running = new TaskRunner(runs, runtime).RunAsync(stop.Token);
        try
        {
            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromMinutes(10));
            var current = (await repo.GetAsync(schedule.Schedule.Id))!;
            Assert.Null(await repo.CoalescePendingAsync(Guid.NewGuid(), current.Schedule.Version, clock.GetUtcNow()));
            var changed = await repo.SaveAsync(current with { Schedule = current.Schedule with { MisfirePolicy = MisfirePolicy.Skip } }, current.Schedule.Version);
            Assert.Null(await repo.CoalescePendingAsync(runId, current.Schedule.Version, clock.GetUtcNow()));
            Assert.Null(await repo.CoalescePendingAsync(runId, changed.Schedule.Version, clock.GetUtcNow()));
            Assert.Equal(current.Schedule.NextRunAtUtc, (await repo.GetAsync(current.Schedule.Id))!.Schedule.NextRunAtUtc);
            changed = await repo.SaveAsync(changed with { Schedule = changed.Schedule with { MisfirePolicy = MisfirePolicy.RunOnce } }, changed.Schedule.Version);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.CoalescePendingAsync(runId, changed.Schedule.Version, clock.GetUtcNow())));
            var coalesced = Assert.Single(attempts.OfType<ScheduledTask>()); Assert.True(coalesced.Schedule.NextRunAtUtc > clock.GetUtcNow());
            runtime.Release.TrySetResult(); await runs.WaitForTerminalAsync(runId).WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = (await repo.GetAsync(changed.Schedule.Id))!; clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Null(await repo.CoalescePendingAsync(runId, terminal.Schedule.Version, clock.GetUtcNow()));
            Assert.Equal(terminal.Schedule.NextRunAtUtc, (await repo.GetAsync(changed.Schedule.Id))!.Schedule.NextRunAtUtc);
        }
        finally { runtime.Release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task DesktopPolicyPersistsAndUnknownPolicyCannotPartiallyUpdateTask()
    {
        using var database = new TemporaryAgentDatabase(); await using (var db = database.Open()) await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow; var repo = new SqliteScheduleRepository(database);
        var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Policy fixture", Instructions = "Fixture", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
        var stored = await repo.SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id,
            Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = "UTC", CreatedAtUtc = now, UpdatedAtUtc = now }));
        foreach (var policy in Enum.GetValues<ScheduledDesktopPolicy>())
        {
            stored = await repo.SaveAsync(stored with { Schedule = stored.Schedule with { DesktopPolicy = policy } }, stored.Schedule.Version);
            var reopened = (await new SqliteScheduleRepository(new SqliteAgentDbContextFactory(database.DatabasePath)).GetAsync(stored.Schedule.Id))!;
            Assert.Equal(policy, reopened.Schedule.DesktopPolicy);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(stored with
        { Task = stored.Task with { Name = "Invalid edit" }, Schedule = stored.Schedule with { DesktopPolicy = (ScheduledDesktopPolicy)999 } }, stored.Schedule.Version));
        Assert.Equal("Policy fixture", (await repo.GetAsync(stored.Schedule.Id))!.Task.Name);
    }

    [Fact]
    public async Task TakeControlDuringCountdownKeepsResourceClosedAndRequiresNewWarningAfterResume()
    {
        await using var fixture = await Fixture.Create(ScheduledDesktopPolicy.StartAfterCountdown); var warning = await fixture.Warning(); fixture.Gate.Present(warning.Id);
        fixture.Desktop.TakeControl(); fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await Until(() => fixture.Gate.GetWarnings().Count == 0); fixture.NoGui();
        Assert.Equal(DesktopAccessState.PausedForUser, fixture.Desktop.Snapshot.State);
        fixture.Desktop.ResumeRun(fixture.RunId); var next = await fixture.Warning(); Assert.NotEqual(warning.Id, next.Id);
        fixture.NoGui(); fixture.Gate.Present(next.Id); Assert.True(fixture.Gate.Decide(next.Id, ScheduledDesktopDecision.StartNow));
        await fixture.Finished(RunStatus.Completed); Assert.Single(fixture.Native.Inputs);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = DateTimeOffset.Parse("2026-10-20T08:00:00Z").UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
        public void SetInitial(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }
    private sealed class WaitingRuntime(TaskRunCoordinator runs) : IAgentRuntime
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            await runs.BeginScheduledDesktopWaitAsync(request.RunId, runs.GetRun(request.RunId).RunVersion, ct); Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await runs.EndScheduledDesktopWaitAsync(request.RunId, runs.GetRun(request.RunId).RunVersion, false, ct);
            return new() { Status = AgentResultStatus.Completed, Summary = "Synthetic waiting run completed without GUI" };
        }
    }
    private sealed class Idle : ISystemIdleSource
    {
        private SystemIdleSnapshot snapshot = new(true, null);
        public SystemIdleSnapshot Snapshot { get => Volatile.Read(ref snapshot); set => Volatile.Write(ref snapshot, value); }
        public volatile bool Fail;
        public int Reads;
        public SystemIdleSnapshot Read() { Interlocked.Increment(ref Reads); if (Fail) throw new InvalidOperationException("Synthetic native failure"); return Snapshot; }
    }
    private sealed class AdmissionWriteGate(IRunRepository inner) : IRunRepository
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;
        public Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, CancellationToken ct = default) => inner.CreateAsync(run, conversation, request, events, ct);
        public Task<StoredRun> GetOrCreateAsync(TaskRun run, Conversation conversation, AgentRequest request, IReadOnlyList<RunEvent> events, RunSubmissionIdentity identity, CancellationToken ct = default) => inner.GetOrCreateAsync(run, conversation, request, events, identity, ct);
        public Task<StoredRun?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async Task UpdateAsync(TaskRun run, long version, IReadOnlyList<RunEvent> events, AgentRunResult? result = null, CancellationToken ct = default)
        {
            if (run.Status == RunStatus.Running && (await inner.GetAsync(run.Id, ct))!.Run.Status == RunStatus.WaitingForDesktop && Interlocked.Exchange(ref held, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await inner.UpdateAsync(run, version, events, result, ct);
        }
    }
    private sealed class Runtime(IServiceProvider services, bool immediateAcquire) : IAgentRuntime
    {
        public readonly ComputerToolTests.Native Native = new();
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<AgentRunResult> ExecuteAsync(AgentRequest request, IProgress<AgentEvent> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); await Ready.Task.WaitAsync(ct);
            var desktop = services.GetRequiredService<IDesktopAccessCoordinator>();
            if (immediateAcquire) await desktop.AcquireAsync(request.RunId, ct);
            await using var session = new ComputerToolSession(request.RunId, desktop,
                lease => new WindowsComputerController(desktop, lease, Native, Native, Native.GetMonitors));
            var registry = new ToolRegistry(session.Tools, (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
            var bound = registry.Bind(request with { AllowedToolIds = ["computer_screenshot", "computer_click"] });
            var capture = await bound.InvokeAsync("computer_screenshot", "fixture-capture", JsonSerializer.SerializeToElement(new { }), ct);
            if (capture.Result.Status != ToolResultStatus.Success) return new() { Status = AgentResultStatus.Blocked, Summary = "Fixture GUI refused", ErrorCode = capture.Result.ErrorCode };
            session.ConfirmModelObservation(); var id = capture.Result.StructuredData!.Value.GetProperty("observationId").GetString();
            var click = await bound.InvokeAsync("computer_click", "fixture-click", JsonSerializer.SerializeToElement(new { observationId = id, imageX = 0, imageY = 0, button = "left" }), ct);
            return new() { Status = click.Result.Status == ToolResultStatus.Success ? AgentResultStatus.Completed : AgentResultStatus.Blocked, Summary = "Fixture scheduled GUI", ErrorCode = click.Result.ErrorCode };
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAgentDatabase Database = new(); public Clock Clock = new(); public IHost Host = null!;
        public ScheduledDesktopGate Gate => Host.Services.GetRequiredService<ScheduledDesktopGate>();
        public IScheduleRepository Schedules => Host.Services.GetRequiredService<IScheduleRepository>();
        public TaskRunCoordinator Runs => Host.Services.GetRequiredService<TaskRunCoordinator>();
        public IDesktopAccessCoordinator Desktop => Host.Services.GetRequiredService<IDesktopAccessCoordinator>();
        public ComputerToolTests.Native Native => runtime.Native;
        public int RuntimeCalls => runtime.Calls;
        public ScheduledTask Schedule = null!; public Guid RunId; private Runtime runtime = null!;
        public AdmissionWriteGate? AdmissionWrite;
        private CancellationTokenSource stop = new(); private Task runner = Task.CompletedTask;
        private Func<IHost> build = null!;
        private bool hosted;
        public static async Task<Fixture> Create(ScheduledDesktopPolicy policy, bool immediateAcquire = false, Idle? idle = null, bool blockAdmissionWrite = false,
            ScheduledDesktopOptions? options = null, bool hosted = false, DateTimeOffset? startAtUtc = null, ScheduleRecurrence? recurrence = null, string timeZoneId = "UTC")
        {
            var fixture = new Fixture { hosted = hosted }; if (startAtUtc is { } start) fixture.Clock.SetInitial(start);
            await using (var db = fixture.Database.Open()) await db.Database.MigrateAsync();
            var idleSource = idle ?? new Idle();
            fixture.build = () => AppHost.CreateBuilder([], fixture.Database.DatabasePath).ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(fixture.Clock);
                services.AddSingleton<ISystemIdleSource>(idleSource);
                services.AddSingleton(options ?? new ScheduledDesktopOptions());
                services.AddSingleton<IAgentRuntime>(provider => fixture.runtime = new(provider, immediateAcquire));
                if (blockAdmissionWrite) services.AddSingleton<IRunRepository>(fixture.AdmissionWrite = new(new SqliteRunRepository(fixture.Database)));
                services.AddSingleton<IDesktopAccessCoordinator>(provider => new DesktopAccessCoordinator(_ => true,
                    (id, ct) => provider.GetRequiredService<ScheduledDesktopGate>().WaitAsync(id, ct)));
            }).Build();
            fixture.Host = fixture.build(); fixture.runtime = (Runtime)fixture.Host.Services.GetRequiredService<IAgentRuntime>();
            var now = fixture.Clock.GetUtcNow(); var task = new TaskDefinition { Id = Guid.NewGuid(), Name = "Synthetic scheduled check", Instructions = "Only fixture desktop", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
            fixture.Schedule = await fixture.Schedules.SaveAsync(new(task, new() { Id = Guid.NewGuid(), TaskDefinitionId = task.Id,
                Recurrence = recurrence ?? new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = timeZoneId, DesktopPolicy = policy, CreatedAtUtc = now, UpdatedAtUtc = now }));
            fixture.Clock.Advance(fixture.Schedule.Schedule.NextRunAtUtc!.Value - fixture.Clock.GetUtcNow());
            if (hosted) await fixture.Host.StartAsync();
            await fixture.Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync();
            fixture.RunId = Assert.Single(fixture.Runs.GetRuns()).Id;
            fixture.runtime.Ready.TrySetResult();
            if (!hosted) fixture.runner = new TaskRunner(fixture.Runs, fixture.runtime).RunAsync(fixture.stop.Token);
            return fixture;
        }
        public async Task Restart()
        {
            Assert.True(hosted); await Host.StopAsync(); Host.Dispose(); Host = build(); runtime = (Runtime)Host.Services.GetRequiredService<IAgentRuntime>();
            await Host.StartAsync(); await Host.Services.GetRequiredService<ScheduleDispatcher>().TickAsync(); runtime.Ready.TrySetResult();
        }
        public void NoGui() { Assert.Equal(0, Native.Captures); Assert.Empty(Native.Inputs); Assert.Null(Desktop.Snapshot.OwnerRunId); }
        public async Task<ScheduledDesktopWarning> Warning() { await Until(() => Gate.GetWarnings().Count == 1); return Gate.GetWarnings().Single(); }
        public async Task Finished(RunStatus expected)
        {
            TaskRun run;
            try { run = await Runs.WaitForTerminalAsync(RunId).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException error) { throw new InvalidOperationException($"State={Runs.GetRun(RunId).Status}/{Runs.GetRun(RunId).ErrorCode}; native={Native.Captures}/{Native.Inputs.Count}; resource={Desktop.Snapshot}; warnings={JsonSerializer.Serialize(Gate.GetWarnings())}", error); }
            Assert.Equal(expected, run.Status);
            Assert.Equal(expected, (await Host.Services.GetRequiredService<IRunRepository>().GetAsync(RunId))!.Run.Status);
            await Until(() => Gate.GetWarnings().Count == 0);
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); if (hosted) await Host.StopAsync(); else await runner.WaitAsync(TimeSpan.FromSeconds(5)); Host.Dispose(); stop.Dispose(); Database.Dispose(); }
    }
    private static async Task Until(Func<bool> condition) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, stop.Token); }
    private static async Task Bind(Window window) { window.Measure(new Size(620, 400)); window.Arrange(new Rect(0, 0, 620, 400)); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static Task OnWpfThread(Func<Task> action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await action(); result.TrySetResult(); } catch (Exception error) { result.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
