using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PersonalAgent.App.ViewModels;

public sealed class ScheduleDay(DayOfWeek day) : INotifyPropertyChanged
{
    private bool selected;
    public DayOfWeek Day { get; } = day;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ScheduleProfileChoice(string Id, string Label);

public sealed class SchedulesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IScheduleRepository? repository;
    private readonly IModelProfileRepository? profiles;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime;
    private ScheduledTask? selected;
    private bool busy, disposed;
    private string message = "Create a schedule or select one to edit. Reload discards unsaved changes.";

    public SchedulesViewModel(IHostApplicationLifetime host, IScheduleRepository? repository = null,
        IModelProfileRepository? profiles = null, TimeProvider? timeProvider = null)
    {
        this.repository = repository; this.profiles = profiles; clock = timeProvider ?? TimeProvider.System;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        SaveCommand = new(SaveAsync, () => CanEdit);
        ReloadCommand = new(ReloadAsync, () => CanEdit);
        NewCommand = new(() => { NewEditor(); return Task.CompletedTask; }, () => CanEdit);
        ToggleCommand = new(ToggleAsync, () => CanEdit && selected is not null);
        NewEditor(); Initialization = ReloadAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ScheduledTask> Schedules { get; } = [];
    public ObservableCollection<TaskRun> History { get; } = [];
    public ObservableCollection<string> FutureTriggers { get; } = [];
    public ObservableCollection<ScheduleProfileChoice> Profiles { get; } = [];
    public IReadOnlyList<ScheduleMode> Modes { get; } = Enum.GetValues<ScheduleMode>();
    public IReadOnlyList<MisfirePolicy> MisfirePolicies { get; } = Enum.GetValues<MisfirePolicy>();
    public IReadOnlyList<string> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).Order().ToArray();
    public IReadOnlyList<ScheduleDay> Days { get; } = Enum.GetValues<DayOfWeek>().Select(day => new ScheduleDay(day)).ToArray();
    public Task Initialization { get; }
    public Task HistoryLoading { get; private set; } = Task.CompletedTask;
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    public bool CanEdit => repository is not null && !busy && !disposed;
    public string Message => repository is null ? "Schedule storage is unavailable." : message;
    public string ToggleLabel => selected?.Schedule.Enabled == true ? "Disable schedule" : "Enable schedule";
    public ScheduledTask? Selected
    {
        get => selected;
        set
        {
            if (!CanEdit || value is not null && !Schedules.Contains(value)) return;
            selected = value; Changed(); History.Clear(); FutureTriggers.Clear();
            if (value is not null) LoadEditor(value);
            HistoryLoading = LoadHistoryAsync(value);
            Refresh();
        }
    }

    // Text fields are parsed at save time, so an invalid edit cannot leave an old numeric value silently saved.
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string ModelProfileId { get; set; } = "fake";
    public ScheduleMode Mode { get; set; }
    public string EveryMinutes { get; set; } = "10";
    public DateTime? OnceDate { get; set; }
    public string LocalTime { get; set; } = "09:00";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool Enabled { get; set; } = true;
    public MisfirePolicy MisfirePolicy { get; set; }
    public string MaxCatchUpRuns { get; set; } = "3";

    private void NewEditor()
    {
        selected = null; History.Clear(); FutureTriggers.Clear();
        Name = "New check"; Instructions = ""; ModelProfileId = "fake"; Mode = ScheduleMode.Interval;
        EveryMinutes = "10"; OnceDate = clock.GetLocalNow().Date.AddDays(1); LocalTime = "09:00";
        TimeZoneId = TimeZoneInfo.Local.Id; Enabled = true; MisfirePolicy = MisfirePolicy.RunOnce; MaxCatchUpRuns = "3";
        foreach (var day in Days) day.Selected = day.Day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        Changed(""); Refresh();
    }
    private void LoadEditor(ScheduledTask value)
    {
        var schedule = value.Schedule; var recurrence = schedule.Recurrence;
        Name = value.Task.Name; Instructions = value.Task.Instructions; ModelProfileId = value.Task.ModelProfileId;
        Mode = recurrence.Mode; EveryMinutes = (recurrence.EveryMinutes ?? 10).ToString(CultureInfo.InvariantCulture);
        TimeZoneId = schedule.TimeZoneId; Enabled = schedule.Enabled;
        var once = recurrence.OnceAtUtc is { } at ? TimeZoneInfo.ConvertTime(at, ScheduleCalculator.ResolveTimeZone(TimeZoneId)) : clock.GetLocalNow();
        OnceDate = once.Date; LocalTime = (recurrence.LocalTime ?? TimeOnly.FromDateTime(once.DateTime)).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var day in Days) day.Selected = recurrence.Days.Contains(day.Day);
        MisfirePolicy = schedule.MisfirePolicy; MaxCatchUpRuns = schedule.MaxCatchUpRuns.ToString(CultureInfo.InvariantCulture);
        if (!Profiles.Any(profile => profile.Id == ModelProfileId)) Profiles.Add(new(ModelProfileId, ModelProfileId + " (unavailable profile)"));
        if (schedule.Enabled && value.Task.Enabled && schedule.NextRunAtUtc is { } next)
        {
            var zone = ScheduleCalculator.ResolveTimeZone(TimeZoneId);
            for (var index = 0; index < 3; index++)
            {
                FutureTriggers.Add($"{TimeZoneInfo.ConvertTime(next, zone):yyyy-MM-dd HH:mm:ss zzz} ({zone.Id}); UTC {next:yyyy-MM-dd HH:mm:ss}");
                if (ScheduleCalculator.Next(recurrence, TimeZoneId, schedule.CreatedAtUtc, next) is not { } following) break;
                next = following;
            }
        }
        else FutureTriggers.Add(!value.Task.Enabled ? "Task template is disabled." : schedule.Enabled ? "No future trigger." : "Schedule disabled.");
        Changed("");
    }
    private async Task LoadHistoryAsync(ScheduledTask? value)
    {
        if (value is null || repository is null) return;
        try
        {
            var runs = await repository.ListRunsAsync(value.Schedule.Id, lifetime.Token);
            if (selected != value || disposed) return;
            History.Clear(); foreach (var run in runs) History.Add(run);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (selected == value) { History.Clear(); message = "Run history could not be loaded. Reload to try again."; Refresh(); } }
    }
    private async Task ReloadAsync()
    {
        if (!CanEdit) return;
        busy = true; Refresh();
        try { await LoadAsync(selected?.Schedule.Id); message = "Schedules reloaded. Select a schedule or create a new one."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Schedules could not be loaded. Reload before editing."; }
        finally { busy = false; Refresh(); }
    }
    private async Task LoadAsync(Guid? selection)
    {
        var current = await repository!.ListAsync(lifetime.Token);
        var modelProfiles = profiles is null ? [] : await profiles.ListAsync(lifetime.Token);
        var editingProfile = ModelProfileId;
        Profiles.Clear(); Profiles.Add(new("fake", "Local demo (no API)"));
        foreach (var profile in modelProfiles) Profiles.Add(new(profile.Id, $"{profile.Id}: {profile.ProviderId} / {profile.ModelId}"));
        // Clear selection before notifying the list binding; it must not replace the loaded revision.
        selected = null; Schedules.Clear(); foreach (var value in current) Schedules.Add(value);
        selected = current.SingleOrDefault(value => value.Schedule.Id == selection);
        History.Clear(); FutureTriggers.Clear();
        if (selected is not null) LoadEditor(selected);
        else { ModelProfileId = editingProfile; Changed(""); }
        Changed(nameof(Selected)); HistoryLoading = LoadHistoryAsync(selected); await HistoryLoading;
    }
    private ScheduleRecurrence Recurrence()
    {
        if (Mode == ScheduleMode.Interval)
        {
            if (!int.TryParse(EveryMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) throw new ArgumentException();
            return new() { Mode = Mode, EveryMinutes = minutes };
        }
        if (!TimeOnly.TryParseExact(LocalTime, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new ArgumentException();
        if (Mode != ScheduleMode.Once)
        {
            var originalTime = selected?.Schedule.Recurrence.LocalTime;
            if (originalTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) == LocalTime) time = originalTime.Value;
            return new() { Mode = Mode, LocalTime = time, Days = Mode == ScheduleMode.Weekdays ? Days.Where(day => day.Selected).Select(day => day.Day).ToArray() : [] };
        }
        if (OnceDate is null) throw new ArgumentException();
        if (selected is { Schedule.Recurrence.OnceAtUtc: { } stored } original && original.Schedule.TimeZoneId == TimeZoneId)
        {
            var displayed = TimeZoneInfo.ConvertTime(stored, ScheduleCalculator.ResolveTimeZone(TimeZoneId));
            if (OnceDate.Value.Date == displayed.Date && LocalTime == displayed.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                return new() { Mode = Mode, OnceAtUtc = stored };
        }
        var local = DateTime.SpecifyKind(OnceDate.Value.Date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        var zone = ScheduleCalculator.ResolveTimeZone(TimeZoneId);
        if (zone.IsInvalidTime(local)) throw new ArgumentException();
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new() { Mode = Mode, OnceAtUtc = new DateTimeOffset(local, offset).ToUniversalTime() };
    }
    private async Task SaveAsync()
    {
        if (!CanEdit) return;
        busy = true; Refresh();
        try
        {
            if (!Profiles.Any(profile => profile.Id == ModelProfileId) || !int.TryParse(MaxCatchUpRuns, NumberStyles.None, CultureInfo.InvariantCulture, out var cap)) throw new ArgumentException();
            var now = clock.GetUtcNow(); var task = selected?.Task ?? new TaskDefinition { Id = Guid.NewGuid(), Name = "", Instructions = "", ModelProfileId = "fake", CreatedAtUtc = now, UpdatedAtUtc = now };
            var schedule = selected?.Schedule ?? new ScheduleDefinition { Id = Guid.NewGuid(), TaskDefinitionId = task.Id, Recurrence = new() { Mode = ScheduleMode.Interval, EveryMinutes = 10 }, TimeZoneId = TimeZoneId, CreatedAtUtc = now, UpdatedAtUtc = now };
            var draft = new ScheduledTask(task with { Name = Name.Trim(), Instructions = Instructions.Trim(), ModelProfileId = ModelProfileId },
                schedule with { Recurrence = Recurrence(), TimeZoneId = TimeZoneId, Enabled = Enabled, MisfirePolicy = MisfirePolicy, MaxCatchUpRuns = cap });
            var saved = await repository!.SaveAsync(draft, selected?.Schedule.Version, lifetime.Token);
            await LoadAsync(saved.Schedule.Id); message = "Schedule saved. Existing runs retain their original instructions.";
        }
        catch (ScheduleVersionConflictException) { message = "The schedule changed. Reload and review before saving again."; }
        catch (ArgumentException) { message = "Check name, instructions, recurrence, timezone and model. Use a future date, positive whole minutes and HH:mm time. CatchUp requires an hourly/calendar schedule and a limit of 1–10."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Save could not be confirmed. Reload to check the stored schedule before trying again."; }
        finally { busy = false; Refresh(); }
    }
    private async Task ToggleAsync()
    {
        if (!CanEdit || selected is null) return;
        busy = true; Refresh();
        try
        {
            var saved = await repository!.SaveAsync(selected with { Schedule = selected.Schedule with { Enabled = !selected.Schedule.Enabled } }, selected.Schedule.Version, lifetime.Token);
            await LoadAsync(saved.Schedule.Id); message = saved.Schedule.Enabled ? "Schedule enabled." : "Schedule disabled. Existing runs and history are retained; active runs have separate controls.";
        }
        catch (ScheduleVersionConflictException) { message = "The schedule changed. Reload and review before changing its state."; }
        catch (ArgumentException) { message = "Schedule cannot be enabled with this configuration. Review its next occurrence and save a future time."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { message = "Change could not be confirmed. Reload to check the stored schedule."; }
        finally { busy = false; Refresh(); }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Refresh()
    {
        Changed(nameof(CanEdit)); Changed(nameof(Message)); Changed(nameof(ToggleLabel));
        SaveCommand.Refresh(); ReloadCommand.Refresh(); NewCommand.Refresh(); ToggleCommand.Refresh();
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); Refresh(); }
}
