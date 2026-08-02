using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App;

public partial class MainWindow : Window
{
    // Capturing N game windows takes seconds, not milliseconds — the daemon wakes each
    // client, screenshots it and freezes it again. The default 10s request timeout would
    // fire well before a nine-client sweep finished.
    private static readonly TimeSpan DumpCapturesTimeout = TimeSpan.FromMinutes(2);

    // Elapsed times in the running-macros panel are rendered by the VM but ticked from
    // here: keeping the DispatcherTimer in the view is what lets every view-model in this
    // assembly stay free of Avalonia types and therefore unit-testable.
    private readonly DispatcherTimer _elapsedTimer;

    // Designer needs a parameterless ctor; the app uses the (vm) overload.
    public MainWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.RefreshElapsed());
        _elapsedTimer.Start();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    // Closing the window exits the panel process — the tray and the engine live in the
    // daemon now, so there is nothing left here worth keeping alive in the background.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _elapsedTimer.Stop();
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosing(e);
    }

    private void OnAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            _ = row.AddTagAsync();
        }
    }

    // Enter in the tag box is the fast path — typing a tag and reaching for the mouse for
    // nine windows in a row gets old.
    private void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: WindowRowViewModel row })
        {
            e.Handled = true;
            _ = row.AddTagAsync();
        }
    }

    private void OnRemoveTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TagChipViewModel chip })
        {
            chip.Remove();
        }
    }

    private void OnStopRunClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { DataContext: RunningMacroRowViewModel run })
        {
            _ = vm.StopRunAsync(run);
        }
    }

    private void OnStopAllRunsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _ = vm.StopAllRunsAsync();
        }
    }

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

    // Opens the node editor. The dialog is transient and gets a fresh view-model each time;
    // the connection it talks over is the process-wide one.
    private async void OnMacrosClicked(object? sender, RoutedEventArgs e)
    {
        if (Program.Services is not { } services)
        {
            return;
        }

        try
        {
            var dialog = new MacrosDialog(services.CreateMacroEditorViewModel());
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Macro editor failed to open");
        }
    }
}
