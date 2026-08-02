using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App;

public partial class MacrosDialog : Window
{
    // Designer needs a parameterless ctor; DI-side construction goes through the VM overload.
    public MacrosDialog() => InitializeComponent();

    public MacrosDialog(MacroEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private MacroEditorViewModel? Vm => DataContext as MacroEditorViewModel;

    // Global hotkeys go down for as long as this window is open. Win32 RegisterHotKey
    // swallows presses of a chord it already owns, so a chord that is currently bound to a
    // macro would never reach the picker — precisely the chord a user is most likely to be
    // re-binding. Suspend on open, resume on close (from the CURRENT library, so anything
    // just saved is registered immediately).
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm)
        {
            _ = SuspendAsync(vm);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Vm is { } vm)
        {
            // Resume before disposing: Dispose only detaches event handlers, but the order
            // makes the intent explicit — hotkeys must come back even if the dialog is
            // being torn down.
            _ = ResumeAsync(vm);
            vm.Dispose();
        }
    }

    private static async Task SuspendAsync(MacroEditorViewModel vm)
    {
        try
        {
            await vm.SuspendHotkeysAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to suspend global hotkeys for the macro editor");
        }
    }

    private static async Task ResumeAsync(MacroEditorViewModel vm)
    {
        try
        {
            await vm.ResumeHotkeysAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to resume global hotkeys after closing the macro editor");
        }
    }

    // ---- library ---------------------------------------------------------------------

    private void OnNewMacroClicked(object? sender, RoutedEventArgs e) => Vm?.NewMacro();

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.Run(item);
        }
    }

    private void OnStopMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.Stop(item);
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
