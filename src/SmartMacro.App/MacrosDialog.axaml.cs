using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

public partial class MacrosDialog : Window
{
    public MacrosDialog() => InitializeComponent();

    public MacrosDialog(MacrosDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosDialogViewModel vm) vm.AddMacro();
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosDialogViewModel vm) return;
        if (sender is not Button { DataContext: MacroRowViewModel row }) return;
        vm.RemoveMacro(row);
    }

    private void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosDialogViewModel vm) return;
        if (sender is not Button { DataContext: MacroRowViewModel row }) return;
        vm.RunMacro(row);
    }

    private void OnAddClassPanelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroRowViewModel row })
        {
            row.AddClassPanel();
        }
    }

    private void OnRemoveClassPanelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel } && panel.Parent is not null)
        {
            panel.Parent.RemoveClassPanel(panel);
        }
    }

    private void OnAddKeyPressClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel })
        {
            panel.AddKeyPress();
        }
    }

    private void OnAddDelayClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel })
        {
            panel.AddDelay();
        }
    }

    private void OnAddClickClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel })
        {
            panel.AddClick();
        }
    }

    private void OnRemoveActionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroActionRowViewModel action } && action.Parent is not null)
        {
            action.Parent.RemoveAction(action);
        }
    }

    private void OnMoveActionUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroActionRowViewModel action } && action.Parent is not null)
        {
            action.Parent.MoveActionUp(action);
        }
    }

    private void OnMoveActionDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroActionRowViewModel action } && action.Parent is not null)
        {
            action.Parent.MoveActionDown(action);
        }
    }

    private void OnMoveClassPanelUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel } && panel.Parent is not null)
        {
            panel.Parent.MoveClassPanelUp(panel);
        }
    }

    private void OnMoveClassPanelDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MacroClassPanelViewModel panel } && panel.Parent is not null)
        {
            panel.Parent.MoveClassPanelDown(panel);
        }
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

    // Unsubscribe from MacrosChanged event to prevent leak if the library outlives us
    // (it does — MacroLibrary is a DI singleton, dialog is transient).
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }
}
