using Avalonia.Controls;
using Avalonia.Interactivity;
using PerfectWorldAgent.App.ViewModels;

namespace PerfectWorldAgent.App;

public partial class MacrosDialog : Window
{
    public MacrosDialog() => InitializeComponent();

    public MacrosDialog(MacrosDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosDialogViewModel vm)
        {
            vm.AddMacro();
        }
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosDialogViewModel vm) return;
        if (sender is not Button { DataContext: MacroRowViewModel row }) return;
        vm.RemoveMacro(row);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosDialogViewModel vm) return;

        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            if (await vm.SaveAsync())
            {
                Close(true);
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
