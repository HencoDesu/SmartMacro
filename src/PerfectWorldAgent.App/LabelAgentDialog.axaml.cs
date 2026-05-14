using Avalonia.Controls;
using Avalonia.Interactivity;
using PerfectWorldAgent.App.ViewModels;

namespace PerfectWorldAgent.App;

public partial class LabelAgentDialog : Window
{
    // Designer-friendly parameterless ctor; real construction goes via the VM-taking
    // overload from MainWindow.OnLabelClicked.
    public LabelAgentDialog() => InitializeComponent();

    public LabelAgentDialog(LabelAgentDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LabelAgentDialogViewModel vm)
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
            // Otherwise the VM has populated ErrorMessage — dialog stays open for retry.
        }
        finally
        {
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
