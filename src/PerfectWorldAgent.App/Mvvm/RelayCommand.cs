namespace PerfectWorldAgent.App.Mvvm;

// Tiny ICommand implementation for tray menu items and other UI commands.
// CommunityToolkit.Mvvm would give us [RelayCommand] source-generation but we're not
// pulling that in for a 3-line class. CanExecute is always true; CanExecuteChanged is a
// no-op since none of our current commands' enable-state depends on viewmodel state.
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
