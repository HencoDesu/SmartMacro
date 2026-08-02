using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Views;

/// <summary>
/// «Окна». The view-model does the work; this file is the event plumbing the rows editor
/// style of this codebase uses instead of commands.
/// </summary>
public partial class WindowsView : UserControl
{
    // Capturing N game windows takes seconds, not milliseconds — the daemon wakes each
    // client, screenshots it and freezes it again. The default 10s request timeout would
    // fire well before a nine-client sweep finished.
    private static readonly TimeSpan DumpCapturesTimeout = TimeSpan.FromMinutes(2);

    public WindowsView() => AvaloniaXamlLoader.Load(this);

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    // ---- tags ------------------------------------------------------------------------

    private void OnAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            _ = row.AddTagAsync();
        }
    }

    private void OnBeginAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            row.BeginAddTag();
        }
    }

    // Enter in the tag box is the fast path — typing a tag and reaching for the mouse for
    // nine windows in a row gets old. Escape backs out of the inline box on a tagged row.
    private void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WindowRowViewModel row })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = row.AddTagAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                row.CancelAddTag();
                break;

            default:
                break;
        }
    }

    // The inline box only exists once the user asked for it, so it should already be
    // focused when it appears — otherwise the "+" costs a click AND a click.
    private void OnInlineTagBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
        }
    }

    private void OnRemoveTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TagChipViewModel chip })
        {
            chip.Remove();
        }
    }

    // ---- header actions -----------------------------------------------------------------

    private void OnIdentifyAllClicked(object? sender, RoutedEventArgs e) => Vm?.IdentifyAll();

    // Diagnostic — dump the vision pipeline's view of each live window. The sweep itself
    // runs in the daemon (it needs the window handles and OpenCV); all this does is ask for
    // it and open the folder that comes back.
    private async void OnDumpCapturesClicked(object? sender, RoutedEventArgs e)
    {
        if (Program.Services is not { } services)
        {
            return;
        }

        string? folder;
        try
        {
            folder = await services.Client.RequestAsync<string>(
                IpcMessageTypes.DumpCaptures,
                timeout: DumpCapturesTimeout);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось выгрузить отладочные снимки");
            return;
        }

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        // Pop the debug folder so the user can see results immediately.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // Folder opening is best-effort; the files are there either way.
            Serilog.Log.Debug(ex, "Не удалось открыть папку '{Folder}'", folder);
        }
    }
}
