using System.Windows.Input;

namespace PersonalAgent.App.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public Task? ExecutionTask { get; private set; }

    public bool CanExecute(object? parameter) => canExecute();

    // The owning ViewModel handles failures and publishes a user-visible result.
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            ExecutionTask = execute();
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
