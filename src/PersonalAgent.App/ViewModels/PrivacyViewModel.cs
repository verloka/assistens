using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;
using System.ComponentModel;
using System.Globalization;

namespace PersonalAgent.App.ViewModels;

public sealed class PrivacyViewModel : INotifyPropertyChanged
{
    private readonly IHistoryRetentionRepository? retention;
    private readonly TaskRunCoordinator coordinator;
    private bool busy;
    private string message = "Automatic cleanup is off until enabled and saved. Reload to read saved settings.";
    public PrivacyViewModel(TaskRunCoordinator coordinator, IHistoryRetentionRepository? retention = null)
    {
        this.coordinator = coordinator; this.retention = retention;
        ReloadCommand = new(() => RunAsync(async () =>
        {
            var policy = await retention!.ReadPolicyAsync();
            Enabled = policy.Enabled; Days = policy.Days.ToString(CultureInfo.InvariantCulture);
            message = "Saved retention settings loaded.";
        }), Available);
        SaveCommand = new(() => RunAsync(async () =>
        {
            await retention!.SavePolicyAsync(Policy());
            message = "Retention saved. Enabled cleanup runs at startup and hourly.";
        }), Available);
        ClearCommand = new(() => RunAsync(async () =>
        {
            var count = await coordinator.ClearExpiredHistoryAsync(retention!, Policy(), DateTimeOffset.UtcNow);
            message = $"Private content cleared from {count} tasks. Recent, unfinished and uncertain conversations were kept.";
        }), Available);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Enabled { get; set; }
    public string Days { get; set; } = "30";
    public string Message => message;
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ClearCommand { get; }
    private bool Available() => retention is not null && !busy;
    private HistoryRetentionPolicy Policy()
    {
        if (!int.TryParse(Days, NumberStyles.None, CultureInfo.InvariantCulture, out var days)) throw new ArgumentException();
        var policy = new HistoryRetentionPolicy(Enabled, days); policy.Validate(); return policy;
    }
    private async Task RunAsync(Func<Task> action)
    {
        busy = true; Refresh();
        try { await action(); }
        catch (ArgumentException) { message = "Enter a whole retention period from 1 to 36500 days."; }
        catch (Exception) { message = "Privacy operation failed. Review storage availability; cleanup was not confirmed."; }
        finally { busy = false; Refresh(); }
    }
    private void Refresh()
    {
        PropertyChanged?.Invoke(this, new(null));
        ReloadCommand.Refresh(); SaveCommand.Refresh(); ClearCommand.Refresh();
    }
}
