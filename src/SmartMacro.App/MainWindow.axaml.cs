using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.App;

public partial class MainWindow : Window
{
    // Elapsed times in the running-macros panel are rendered by the VM but ticked from
    // here: keeping the DispatcherTimer in the view is what lets every view-model in this
    // assembly stay free of Avalonia types and therefore unit-testable.
    private readonly DispatcherTimer _elapsedTimer;

    // Designer needs a parameterless ctor; DI uses the (vm) overload.
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

    // Hide-to-tray on window close. Real shutdown only via the tray Exit menu, which
    // sets App.IsExitRequested before calling Shutdown — we let that flow through.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is App app && !app.IsExitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _elapsedTimer.Stop();
        base.OnClosing(e);
    }

    private void OnAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            row.AddTag();
        }
    }

    // Enter in the tag box is the fast path — typing a tag and reaching for the mouse for
    // nine windows in a row gets old.
    private void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: WindowRowViewModel row })
        {
            e.Handled = true;
            row.AddTag();
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
            vm.StopRun(run);
        }
    }

    private void OnStopAllRunsClicked(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.StopAllRuns();

    // Diagnostic — dump the vision pipeline's view of each live window:
    //   *-full.png       — raw PrintWindow capture
    //   *-class-bin.png  — ClassMatcher.DebugBinarizeClassRegion (what MatchTemplate sees)
    // Lets us check whether a configured region lands where it should and whether
    // binarisation produces a readable mask. Open the in-game stats window (default C)
    // before clicking this — without it, the class region is empty.
    private async void OnDumpCapturesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || Program.Services is not { } services)
        {
            return;
        }

        var registry = services.GetRequiredService<WindowRegistry>();
        var matcher = services.GetRequiredService<IClassMatcher>();
        var debugDir = Path.Combine(AppContext.BaseDirectory, "debug");
        Directory.CreateDirectory(debugDir);

        foreach (var row in vm.Windows.ToArray())
        {
            // The registry is the only hwnd → drivable-window lookup there is; a row whose
            // window died between the snapshot and here simply has nothing to capture.
            if (registry.TryGetWindow(row.Hwnd) is not { } window)
            {
                continue;
            }

            var label = row.Tags.Count > 0 ? row.Tags[0].Text : row.HwndHex;
            var stem = SafeFileName($"{row.ProcessName}-{label}");

            byte[] fullCapture;
            try
            {
                fullCapture = window.CaptureScreenshot();
                await File.WriteAllBytesAsync(Path.Combine(debugDir, $"{stem}-full.png"), fullCapture);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(debugDir, $"{stem}.error.txt"), ex.ToString());
                continue;
            }

            try
            {
                var binarised = matcher.DebugBinarizeClassRegion(fullCapture);
                await File.WriteAllBytesAsync(Path.Combine(debugDir, $"{stem}-class-bin.png"), binarised);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(debugDir, $"{stem}-class-bin.error.txt"), ex.ToString());
            }
        }

        // Pop the debug folder so the user can see results immediately.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = debugDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Folder opening is best-effort; the files are there either way.
        }
    }

    // Opens the node editor. Dependencies are resolved from the container here rather
    // than injected into MainWindow because the dialog is transient while the store,
    // run registry, hotkey listener and orchestrator are all singletons.
    private async void OnMacrosClicked(object? sender, RoutedEventArgs e)
    {
        if (Program.Services is not { } services)
        {
            return;
        }

        try
        {
            var dialogVm = new MacroEditorViewModel(
                services.GetRequiredService<MacroGraphStore>(),
                services.GetRequiredService<MacroRunRegistry>(),
                services.GetRequiredService<IMacroLauncher>(),
                services.GetRequiredService<IHotkeySuspension>(),
                services.GetRequiredService<IUiDispatcher>());
            var dialog = new MacrosDialog(dialogVm);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Macro editor failed to open");
        }
    }

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var span = s.ToCharArray();
        for (var i = 0; i < span.Length; i++)
        {
            if (Array.IndexOf(invalid, span[i]) >= 0)
            {
                span[i] = '_';
            }
        }
        return new string(span);
    }
}
