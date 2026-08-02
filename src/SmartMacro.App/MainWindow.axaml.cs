using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SmartMacro.App.ViewModels;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Orchestration;
using SmartMacro.Vision;

namespace SmartMacro.App;

public partial class MainWindow : Window
{
    // Designer needs a parameterless ctor; DI uses the (vm) overload.
    public MainWindow() => InitializeComponent();

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

        base.OnClosing(e);
    }

    // Manual tag-assignment path: operator types a tag into the row's TextBox and
    // clicks Assign. Bypasses ClassMatcher entirely — useful when auto-id fails or
    // during initial template setup. Silent no-op if agent is already identified or
    // the tag is blank. Transitional UI — W0.3 replaces it with tag chips.
    private void OnAssignTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentRowViewModel row })
        {
            row.TryAssignTag();
        }
    }

    // Diagnostic — dump the identification pipeline state for each live agent:
    //   *-full.png       — raw PrintWindow capture
    //   *-class-bin.png  — ClassMatcher.DebugBinarizeClassRegion (what MatchTemplate sees)
    // Lets us see whether the configured ClassMatcher region lands on the stats window
    // class-value text, and whether binarisation produces a readable mask. Open the
    // in-game stats window (default C) before clicking this — without stats open, the
    // class region will be empty.
    private async void OnDumpCapturesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (Program.Services is not { } services)
        {
            return;
        }

        var matcher = services.GetRequiredService<IClassMatcher>();
        var debugDir = Path.Combine(AppContext.BaseDirectory, "debug");
        Directory.CreateDirectory(debugDir);

        var rows = vm.Agents.ToArray();
        foreach (var row in rows)
        {
            var stem = SafeFileName(row.Agent.IsIdentified
                ? row.Agent.Name
                : $"unidentified-{row.GetHashCode():X8}");

            byte[] fullCapture;
            try
            {
                fullCapture = row.Agent.CaptureScreenshot();
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

    // Opens the transitional macro list (Run/Stop + a pointer at the macros folder).
    // W0.3 replaces it with a real node editor; the dialog resolves its dependencies from
    // the container here because dialogs are transient while the store/registry are
    // singletons.
    private async void OnMacrosClicked(object? sender, RoutedEventArgs e)
    {
        if (Program.Services is not { } services)
        {
            return;
        }

        try
        {
            var dialogVm = new MacrosDialogViewModel(
                services.GetRequiredService<MacroGraphStore>(),
                services.GetRequiredService<MacroRunRegistry>(),
                services.GetRequiredService<Orchestrator>());
            var dialog = new MacrosDialog(dialogVm);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Macros dialog failed to open");
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
