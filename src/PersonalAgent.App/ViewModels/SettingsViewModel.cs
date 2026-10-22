using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence;
using System.ComponentModel;

namespace PersonalAgent.App.ViewModels;

public sealed class SettingsViewModel(SettingsTransfer? transfer, ApprovalOwner owner, IHostApplicationLifetime lifetime,
    SqliteDatabaseMaintenance? maintenance = null) : INotifyPropertyChanged
{
    private SettingsPackage? pending;
    private bool busy, applied;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Available => transfer is not null && !busy && !lifetime.ApplicationStopping.IsCancellationRequested;
    public bool CanApply => Available && pending is not null && !applied;
    public string Preview { get; private set; } = "Export contains configuration templates only. Import adds disabled copies and requires local model credentials with matching IDs/providers. Restart to load imported settings.";
    public string Message { get; private set; } = "No transfer has run.";
    public async Task<string?> ExportAsync()
    {
        if (!Available) return null;
        string? json = null;
        await RunAsync(async () => { json = await transfer!.ExportAsync(owner, lifetime.ApplicationStopping); Message = "Settings prepared without secrets or history."; });
        return json;
    }
    public void Load(string json)
    {
        if (!Available) return;
        pending = null; applied = false;
        try
        {
            pending = SettingsTransfer.Parse(json);
            Preview = $"Models to update with LOCAL credentials: {string.Join(", ", pending.Models.Select(item => item.Id))}\n"
                + $"Disabled copies: MCP {pending.Mcp.Count}, schedules {pending.Schedules.Count}, channels {pending.Channels.Count}. Retention: {pending.RetentionDays} days, off.\n"
                + "Task instructions, MCP arguments/URL path/query, credentials and permissions must be reviewed/re-entered. Existing history and active configurations remain.";
            Message = "Package validated. Review it, then explicitly Apply once. Restart is required after import.";
        }
        catch (Exception) { Message = "Settings file rejected. No configuration was changed."; }
        Refresh();
    }
    public Task ApplyAsync() => !CanApply ? Task.CompletedTask : RunAsync(async () =>
    {
        await transfer!.ImportAsync(pending!, owner, lifetime.ApplicationStopping);
        applied = true; pending = null;
        Message = "Settings imported. Restart, then review disabled templates and supply credentials/details before enabling anything.";
    });
    public Task BackupAsync() => !Available || maintenance is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var path = await Task.Run(() => maintenance.CreateBackupAsync(lifetime.ApplicationStopping), lifetime.ApplicationStopping);
        Message = "Private database backup saved locally: " + path;
    });
    public void FileFailure() { pending = null; Message = "File operation failed. No successful transfer was confirmed."; Refresh(); }
    private async Task RunAsync(Func<Task> action)
    {
        busy = true; Refresh();
        try { await action(); }
        catch (Exception) { Message = "Operation failed. Check the file/schema, matching local model credentials and storage. No successful import/backup was confirmed."; }
        finally { busy = false; Refresh(); }
    }
    private void Refresh() => PropertyChanged?.Invoke(this, new(null));
}
