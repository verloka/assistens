using System.Collections.ObjectModel;
using System.ComponentModel;

namespace PersonalAgent.App.ViewModels;

public sealed class RunDiagnosticsViewModel(RunDiagnosticsService diagnostics) : INotifyPropertyChanged
{
    private RunDiagnostic? selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RunDiagnostic> Runs { get; } = [];
    public ObservableCollection<DeliveryDiagnostic> DeliveryProblems { get; } = [];
    public RunDiagnostic? Selected
    {
        get => selected;
        set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public void Refresh()
    {
        var id = selected?.RunId;
        var snapshot = diagnostics.Snapshot();
        Runs.Clear(); foreach (var row in snapshot) Runs.Add(row);
        DeliveryProblems.Clear(); foreach (var row in diagnostics.DeliveryProblems) DeliveryProblems.Add(row);
        Selected = Runs.FirstOrDefault(row => row.RunId == id) ?? Runs.FirstOrDefault();
    }
}
