using System.Windows.Input;

namespace HashLynx.UI.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> reportError, Predicate<object?>? canExecute = null) : ICommand
{
    private bool _running;
    private event EventHandler? StateChanged;
    public event EventHandler? CanExecuteChanged
    {
        add { StateChanged += value; CommandManager.RequerySuggested += value; }
        remove { StateChanged -= value; CommandManager.RequerySuggested -= value; }
    }
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);
    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (Exception exception) { reportError(exception); }
        finally { _running = false; StateChanged?.Invoke(this, EventArgs.Empty); CommandManager.InvalidateRequerySuggested(); }
    }
}
