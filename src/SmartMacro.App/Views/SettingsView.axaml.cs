using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App.Views;

/// <summary>
/// «Настройки». Всю работу делает view-model; этот файл — та самая проводка событий, которую в
/// этом коде используют вместо команд.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnApplyClicked(object? sender, RoutedEventArgs e) => _ = Vm?.ApplyAsync();

    private void OnRevertClicked(object? sender, RoutedEventArgs e) => Vm?.Revert();

    private void OnResetClicked(object? sender, RoutedEventArgs e) => _ = Vm?.ResetAsync();

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e) => Vm?.OpenFolder();

    private void OnRunDiagnosticsClicked(object? sender, RoutedEventArgs e) => _ = Vm?.RunDiagnosticsAsync();

    private void OnAddProfileClicked(object? sender, RoutedEventArgs e) => Vm?.AddProfile();

    // Enter в поле имени — быстрый путь: набрать имя и потянуться за мышью надоедает уже на
    // втором профиле. Тот же приём, что у поля тега в режиме «Окна».
    private void OnNewProfileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Vm?.AddProfile();
    }

    private void OnRemoveProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProcessProfileRowViewModel row })
        {
            Vm?.RemoveProfile(row);
        }
    }
}
