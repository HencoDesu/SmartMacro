using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SmartMacro.App.Interop;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App;

/// <summary>
/// The Avalonia application object, and the panel's two lifecycle rules.
///
/// <b>Closing the window exits the process.</b> Before stage 3 the X button hid to a tray
/// icon, because this process WAS the engine and closing it would have stopped automation.
/// It isn't any more: the daemon keeps running, owns the tray, and can bring the panel back
/// on demand — so the window behaves like a window.
///
/// <b>The daemon's liveness is ours.</b> A panel whose connection has dropped can neither
/// show anything true nor change anything, so a lost connection is reported once and the
/// process exits rather than sitting there rendering a frozen snapshot.
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private IIpcClient? _client;
    private int _daemonLossReported;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var services = Program.Services;

            // The parameterless MainWindow is the designer's path; at runtime Services is
            // always there, because Program refuses to start Avalonia without a connection.
            var window = services is null
                ? new MainWindow()
                : new MainWindow(services.CreateShellViewModel());
            window.Icon = LoadIcon();

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            if (services is not null)
            {
                _client = services.Client;
                _client.EventReceived += OnDaemonEvent;
                _client.Disconnected += OnDaemonDisconnected;
                desktop.Exit += (_, _) => Detach();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://SmartMacro.App/Assets/icon.png"));
        return new WindowIcon(stream);
    }

    private void OnDaemonEvent(IpcEvent evt)
    {
        if (!string.Equals(evt.Type, IpcMessageTypes.ActivateWindow, StringComparison.Ordinal))
        {
            return;
        }
        Dispatcher.UIThread.Post(SurfaceMainWindow);
    }

    /// <summary>
    /// Brings the window back from minimised/buried and puts it in front. Raised by the
    /// tray's "Открыть панель" and by a second launch of this executable.
    /// </summary>
    private void SurfaceMainWindow()
    {
        if (_desktop?.MainWindow is not { } main)
        {
            return;
        }

        if (!main.IsVisible)
        {
            main.Show();
        }
        if (main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }
        main.Activate();

        // Windows refuses SetForegroundWindow to a process that isn't already the
        // foreground one, and the game's clients are full-screen — Activate() alone often
        // just flashes the taskbar button. The topmost bounce forces the z-order change
        // without leaving the panel pinned above everything.
        main.Topmost = true;
        main.Topmost = false;
        Serilog.Log.Information("Панель выведена на передний план по запросу демона");
    }

    private void OnDaemonDisconnected()
    {
        // The client reconnects on its own, but the UI does not resume from a gap it cannot
        // see the far side of — and a daemon that stopped is usually the user pressing
        // "Выход" in the tray. Report once; a reconnect loop must not stack dialogs.
        if (Interlocked.Exchange(ref _daemonLossReported, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Win32MessageBox.Error(
                "SmartMacro",
                "Служба SmartMacro остановлена — панель будет закрыта.\n\n" +
                "Запустите SmartMacro.Daemon.exe и откройте панель заново.");
            _desktop?.Shutdown();
        });
    }

    private void Detach()
    {
        if (_client is { } client)
        {
            client.EventReceived -= OnDaemonEvent;
            client.Disconnected -= OnDaemonDisconnected;
            _client = null;
        }
    }
}
