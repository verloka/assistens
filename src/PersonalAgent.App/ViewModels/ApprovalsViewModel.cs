using Microsoft.Extensions.Hosting;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PersonalAgent.App.ViewModels;

public sealed class ApprovalsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IApprovalRepository? repository;
    private readonly ApprovalOwner owner;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime;
    private ApprovalRequest? selected;
    private bool busy, acknowledged, disposed;
    private string message = "Select an action and review its destination and exact arguments. Closing this window makes no decision.";

    public ApprovalsViewModel(ApprovalOwner owner, IHostApplicationLifetime host, IApprovalRepository? repository = null, TimeProvider? timeProvider = null)
    {
        owner.Validate(); this.owner = owner; this.repository = repository; clock = timeProvider ?? TimeProvider.System;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
        RefreshCommand = new(RefreshAsync, () => CanSelect && repository is not null);
        ApproveCommand = new(() => DecideAsync(true), () => CanDecide && Acknowledged);
        DenyCommand = new(() => DecideAsync(false), () => CanDecide);
        Initialization = RefreshAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ApprovalRequest> Pending { get; } = [];
    public Task Initialization { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand DenyCommand { get; }
    public string Message => message;
    public bool CanSelect => !disposed && !busy && !lifetime.IsCancellationRequested;
    public bool CanDecide => CanSelect && repository is not null && selected is { Status: ApprovalStatus.Pending } request
        && request.Owner == owner && request.ExpiresAtUtc > clock.GetUtcNow() && Pending.Contains(request);
    public ApprovalRequest? Selected
    {
        get => selected;
        set
        {
            if (ReferenceEquals(selected, value)) return;
            selected = value; acknowledged = false;
            Changed(); Changed(nameof(Acknowledged)); Changed(nameof(ActionDescription)); Changed(nameof(Destination));
            Changed(nameof(Tool)); Changed(nameof(Risk)); Changed(nameof(Arguments)); Changed(nameof(Expires)); Changed(nameof(Correlation)); Refresh();
        }
    }
    public bool Acknowledged { get => acknowledged; set { acknowledged = value; Changed(); Refresh(); } }
    public string ActionDescription => selected?.Action.Description ?? "No action selected";
    public string Destination => selected?.Action.Destination ?? "";
    public string Tool => selected is { } request ? request.Action.ToolId + " (" + request.Action.Source.Kind + ": " + request.Action.Source.Id + ")" : "";
    public string Risk => selected?.Action.RiskLevel.ToString() ?? "";
    public string Arguments => selected is { } request ? JsonSerializer.Serialize(request.Action.Arguments, new JsonSerializerOptions { WriteIndented = true }) : "";
    public string Expires => selected is { } request ? request.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") : "";
    public string Correlation => selected is { } request ? "Run " + request.Action.RunId + "; call " + request.Action.ToolCallId : "";

    public async Task RefreshAsync()
    {
        if (!CanSelect) return;
        if (repository is null) { message = "Approval storage is unavailable. No action can be approved."; Refresh(); return; }
        busy = true; Refresh();
        try { Apply(await repository.ListPendingAsync(owner, lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Pending.Clear(); Selected = null; message = "Pending actions could not be loaded. No decision was made."; }
        finally { busy = false; Refresh(); }
    }

    private void Apply(IReadOnlyList<ApprovalRequest> current)
    {
        foreach (var old in Pending.ToArray())
            if (!current.Any(value => value.Id == old.Id && value.Version == old.Version && value.ActionHash == old.ActionHash)) Pending.Remove(old);
        foreach (var request in current)
            if (!Pending.Any(old => old.Id == request.Id)) Pending.Add(request);
        if (selected is not null && !Pending.Contains(selected)) Selected = null;
    }
    private async Task DecideAsync(bool approve)
    {
        if (!CanDecide || approve && !Acknowledged) return;
        var request = selected!; busy = true; Refresh();
        try
        {
            await repository!.DecideAsync(new(request.Id, request.Version, request.ActionHash, owner, approve), lifetime.Token);
            message = approve ? "Approved this exact action once. Execution still checks current policy and state." : "Denied. This action will not execute.";
            Apply(await repository.ListPendingAsync(owner, lifetime.Token)); Selected = null;
        }
        catch (ApprovalConflictException) { Acknowledged = false; Selected = null; message = "The request expired or changed. Refresh and review the current action."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { Acknowledged = false; Selected = null; }
        catch (Exception) { Acknowledged = false; Selected = null; message = "The decision could not be confirmed. Refresh its status before trying again."; }
        finally { busy = false; Refresh(); }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Refresh()
    {
        Changed(nameof(CanSelect)); Changed(nameof(CanDecide)); Changed(nameof(Message));
        RefreshCommand.Refresh(); ApproveCommand.Refresh(); DenyCommand.Refresh();
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); Refresh(); }
}
