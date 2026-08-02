using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

public partial class MacrosDialog : Window
{
    // Designer needs a parameterless ctor; DI-side construction goes through the VM overload.
    public MacrosDialog() => InitializeComponent();

    public MacrosDialog(MacrosDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosDialogViewModel vm && sender is Button { DataContext: MacroRowViewModel row })
        {
            vm.Run(row);
        }
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosDialogViewModel vm && sender is Button { DataContext: MacroRowViewModel row })
        {
            vm.Stop(row);
        }
    }

    // Until W0.3 ships the node editor, "edit a macro" means "open its JSON file".
    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosDialogViewModel vm)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = vm.FolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"Не удалось открыть папку: {ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // The VM subscribes to the store and the run registry, both DI singletons that
    // outlive this transient dialog — unsubscribe or we leak a dialog per open.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }
}
