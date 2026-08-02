using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

/// <summary>
/// The shell window. Everything visible is a mode view; this class only owns the three
/// things a view-model must not: the elapsed-time tick, the custom title bar's window
/// operations, and the guarantee that global hotkeys come back before the process exits.
/// </summary>
public partial class MainWindow : Window
{
    // The daemon does not re-register hotkeys when a client disconnects, so if we exit
    // while «Макросы» has them suspended they stay dead until the daemon restarts. The
    // resume is a local pipe round trip completed on the client's reader thread, so a
    // bounded block on the UI thread cannot deadlock — and two seconds is far more than a
    // named pipe needs.
    private static readonly TimeSpan HotkeyResumeOnExitTimeout = TimeSpan.FromSeconds(2);

    // Elapsed times in the run bar and the «Прогоны» list are rendered by the VM but ticked
    // from here: keeping the DispatcherTimer in the view is what lets every view-model in
    // this assembly stay free of Avalonia types and therefore unit-testable.
    private readonly DispatcherTimer _elapsedTimer;

    // Designer needs a parameterless ctor; the app uses the (vm) overload.
    public MainWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => (DataContext as ShellViewModel)?.RefreshElapsed());
        _elapsedTimer.Start();
    }

    public MainWindow(ShellViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    // Closing the window exits the panel process — the tray and the engine live in the
    // daemon now, so there is nothing left here worth keeping alive in the background.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _elapsedTimer.Stop();
        if (Vm is { } vm)
        {
            if (vm.HotkeysSuspended)
            {
                // Best effort, bounded: better a two-second stall on exit than global
                // hotkeys that never come back.
                vm.ResumeHotkeysIfSuspendedAsync().Wait(HotkeyResumeOnExitTimeout);
            }
            vm.Dispose();
        }
        base.OnClosing(e);
    }

    // ---- custom title bar ----------------------------------------------------------------

    // ExtendClientAreaToDecorationsHint took the OS caption away, so dragging the window is
    // ours to implement. Left button only, and not when the press landed on one of the
    // caption buttons (they handle their own click).
    //
    // NOT while maximised. BeginMoveDrag on a maximised extended-client-area window was
    // observed to leave the window HIDDEN (still a live process, WS_VISIBLE cleared, no
    // exception) — a state nothing in the app can recover from. Windows' own "drag a
    // maximised window to restore it" gesture is not worth reimplementing on top of that;
    // double-tap restores, which is the discoverable half of the same interaction.
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button
            || WindowState != WindowState.Normal
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }
        ToggleMaximised();
    }

    private void OnMinimiseClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClicked(object? sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---- run bar --------------------------------------------------------------------------

    private void OnStopPrimaryRunClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { PrimaryRun: { } run } vm)
        {
            _ = vm.Workspace.StopRunAsync(run);
        }
    }

    private void OnStopAllRunsClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            _ = vm.Workspace.StopAllRunsAsync();
        }
    }
}
