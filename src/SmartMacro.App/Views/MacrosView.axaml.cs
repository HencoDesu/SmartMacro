using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.Views;

/// <summary>
/// «Макросы» — the rows editor, formerly <c>MacrosDialog</c>.
///
/// Every handler below is the dialog's, unchanged. What the dialog did and this does NOT is
/// the hotkey bracket: suspending the daemon's global hotkeys is now scoped to the mode
/// being selected and lives in <see cref="ShellViewModel"/>, because a UserControl has no
/// open/close of its own to hang it on.
/// </summary>
public partial class MacrosView : UserControl
{
    public MacrosView() => AvaloniaXamlLoader.Load(this);

    private MacroEditorViewModel? Vm => DataContext as MacroEditorViewModel;

    // ---- library ---------------------------------------------------------------------

    private void OnNewMacroClicked(object? sender, RoutedEventArgs e) => Vm?.NewMacro();

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.Run(item);
        }
    }

    private async void OnStopMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            await vm.StopAsync(item);
        }
    }

    private async void OnDeleteMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            await vm.DeleteMacroAsync(item);
        }
    }

    // ---- triggers --------------------------------------------------------------------

    private void OnAddHotkeyTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.Hotkey);

    private void OnAddProcessTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.ProcessAppeared);

    private void OnRemoveTriggerClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: TriggerRowViewModel row })
        {
            vm.RemoveTrigger(row);
        }
    }

    // ---- nodes -----------------------------------------------------------------------

    private void OnAddNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroNodeKindOption option })
        {
            vm.AddNode(option.Kind);
            AddNodeButton.Flyout?.Hide();
        }
    }

    private void OnDeleteNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: NodeRowViewModel row })
        {
            vm.DeleteNode(row);
        }
    }

    private void OnMoveNodeUpClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: NodeRowViewModel row })
        {
            vm.MoveNodeUp(row);
        }
    }

    private void OnMoveNodeDownClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: NodeRowViewModel row })
        {
            vm.MoveNodeDown(row);
        }
    }

    private void OnIssueClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: ValidationIssueViewModel issue })
        {
            vm.SelectIssue(issue);
        }
    }

    // ---- editor actions --------------------------------------------------------------

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.SaveAsync();
        }
    }

    private void OnReloadClicked(object? sender, RoutedEventArgs e) => Vm?.ReloadFromDisk();

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
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
}
