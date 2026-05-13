using System.Windows.Input;

namespace TradeDesktop.App.Commands;

public interface IAsyncRelayCommand : ICommand
{
    void RaiseCanExecuteChanged();
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : IAsyncRelayCommand
{
    private readonly Func<Task> _execute = execute;
    private readonly Func<bool>? _canExecute = canExecute;
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isRunning = true;
            RaiseCanExecuteChanged();
            await _execute();
        }
        catch (Exception ex)
        {
            // Bảo vệ app khỏi crash khi command async phát sinh lỗi không được handle ở tầng gọi.
            System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] Unhandled exception: {ex}");
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}