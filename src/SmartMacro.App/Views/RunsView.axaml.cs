using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App.Views;

/// <summary>«Прогоны» — the full run list. The run bar in the shell is its one-line summary.</summary>
public partial class RunsView : UserControl
{
    public RunsView() => AvaloniaXamlLoader.Load(this);

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    private void OnStopRunClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: RunningMacroRowViewModel run })
        {
            _ = vm.Workspace.StopRunAsync(run);
        }
    }

    private void OnStopAllRunsClicked(object? sender, RoutedEventArgs e) =>
        _ = Vm?.Workspace.StopAllRunsAsync();
}
