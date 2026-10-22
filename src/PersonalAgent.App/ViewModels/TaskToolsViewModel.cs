using Microsoft.Extensions.Hosting;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.App.ViewModels;

public sealed class TaskToolChoice(string label, RunToolPermission permission) : INotifyPropertyChanged
{
    private bool selected;
    private string targetsJson = "[]";
    public string Label { get; } = label;
    public RunToolPermission Permission { get; } = permission;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public bool NeedsTargets => Permission.Source.Kind == ToolSourceKind.Mcp && Permission.RiskLevel != ToolRiskLevel.ReadOnly;
    public string TargetsJson { get => targetsJson; set { targetsJson = value; PropertyChanged?.Invoke(this, new(nameof(TargetsJson))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Explicit refresh reads saved/current declarations only. It never connects or grants a future task.
public sealed class TaskToolsViewModel(McpConnectionManager manager, IDesktopAccessCoordinator desktop, IHostApplicationLifetime lifetime,
    IMcpConnectionRepository? connections = null, IApprovalRepository? approvals = null, IToolCallRepository? dispatches = null) : INotifyPropertyChanged
{
    private bool busy;
    private AsyncCommand? refreshCommand, clearCommand;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<TaskToolChoice> Choices { get; } = [];
    public bool Available => approvals is not null && dispatches is not null && !busy && !lifetime.ApplicationStopping.IsCancellationRequested;
    public string Message { get; private set; } = "No tools selected. Refresh to list Computer tools and connected, saved owner-reviewed MCP tools. Selection is for one task only.";
    public AsyncCommand RefreshCommand => refreshCommand ??= new(RefreshAsync, () => Available);
    public AsyncCommand ClearCommand => clearCommand ??= new(() => { Reset(); return Task.CompletedTask; }, () => Available);

    public async Task RefreshAsync()
    {
        if (!Available) return;
        busy = true; Choices.Clear(); Refresh();
        try
        {
            await using var prototype = new ComputerToolSession(Guid.NewGuid(), desktop);
            foreach (var tool in prototype.Tools)
                Choices.Add(new(tool.Descriptor.Id + " — " + tool.Descriptor.RiskLevel,
                    new(tool.Descriptor.Id, tool.Descriptor.Source, tool.Descriptor.RiskLevel, ToolPermissionEffect.Allow)));
            var saved = connections is null ? [] : await connections.ListAsync(lifetime.ApplicationStopping);
            foreach (var configuration in saved)
                foreach (var permission in configuration.Permissions)
                    try
                    {
                        var tool = manager.BindTool(configuration.Configuration.Id, permission);
                        if (tool.IsCurrent(configuration)) Choices.Add(new(configuration.Configuration.Name + " / " + tool.Descriptor.Id + " — " + permission.RiskLevel,
                            new(tool.Descriptor.Id, tool.Descriptor.Source, permission.RiskLevel, ToolPermissionEffect.Allow, permission.Version, BindingId: tool.BindingId)));
                    }
                    catch (McpConnectionException) { } // Disconnected/unreviewed declarations are unavailable, never auto-connected.
            Message = "Select each tool for this task. Screenshot disclosure and GUI input require exact approval. MCP mutations also need exact target rules.";
        }
        catch (Exception) { Choices.Clear(); Message = "Tool list could not be loaded. No selection or connection was made."; }
        finally { busy = false; Refresh(); }
    }

    public IReadOnlyList<RunToolPermission>? Snapshot()
    {
        if (busy) throw new InvalidOperationException("Wait for tool refresh.");
        var selected = Choices.Where(value => value.Selected).ToArray();
        if (selected.Length == 0) return null;
        if (!Available) throw new InvalidOperationException("Tool authorization storage is unavailable.");
        if (selected.Any(value => value.Permission.Source.Kind == ToolSourceKind.Computer && value.Permission.ToolId != "computer_screenshot")
            && !selected.Any(value => value.Permission.ToolId == "computer_screenshot")) throw new InvalidOperationException("Select screenshot along with GUI input.");
        return ToolPermissionPolicy.SnapshotSelection(selected.Select(value => value.Permission with
        {
            Destinations = value.NeedsTargets ? ParseTargets(value.TargetsJson) : null
        }).ToArray());
    }

    private static IReadOnlyList<ToolDestinationConstraint> ParseTargets(string json)
    {
        if (json.Length > 8192) throw new ArgumentException("Target rules are too large.");
        return JsonSerializer.Deserialize<ToolDestinationConstraint[]>(json,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 }) ?? throw new ArgumentException("Invalid target rules.");
    }
    public void Reset()
    {
        foreach (var choice in Choices) { choice.Selected = false; choice.TargetsJson = "[]"; }
        Message = "Selection cleared. Choose tools explicitly for each new task, including a continuation."; Refresh();
    }
    private void Refresh() { PropertyChanged?.Invoke(this, new(null)); RefreshCommand.Refresh(); ClearCommand.Refresh(); }
}
