using Avalonia.Controls;
using Avalonia.Interactivity;
using PerfectWorldAgent.App.ViewModels;

namespace PerfectWorldAgent.App;

public partial class SettingsDialog : Window
{
    // Designer needs a parameterless ctor; real construction goes via the VM-taking
    // overload from MainWindow.OnSettingsClicked.
    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(SettingsDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
        {
            return;
        }

        // Disable buttons while we're awaiting persistence so a fast double-click can't
        // race the save flow.
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            if (await vm.SaveAsync())
            {
                Close(true);
            }
            // Otherwise vm.ErrorMessage is populated — dialog stays open for retry.
        }
        finally
        {
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnAddMacroRowClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            vm.AddMacroRow();
        }
    }

    private void OnRemoveMacroRowClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm) return;
        if (sender is not Button { DataContext: MacroHotkeyRow row }) return;
        vm.RemoveMacroRow(row);
    }
}
