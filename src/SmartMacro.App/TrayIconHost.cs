using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using SmartMacro.App.Mvvm;

namespace SmartMacro.App;

// Encapsulates the tray icon's lifecycle and the show-main / exit-app commands. App.cs
// installs one of these on framework-init-completed; the host owns the TrayIcon and the
// Exit flag the MainWindow's hide-to-tray handler reads.
internal sealed class TrayIconHost : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private TrayIcon? _trayIcon;
    private bool _exitRequested;

    public bool IsExitRequested => _exitRequested;

    public TrayIconHost(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
    }

    /// <summary>
    /// Builds the tray icon + menu and applies the same icon to the main window. Both
    /// surfaces share one Bitmap so they look identical and we don't rasterise twice.
    /// </summary>
    public void Install()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://SmartMacro.App/Assets/icon.png"));
        var icon = new WindowIcon(iconStream);

        if (_desktop.MainWindow is { } main)
        {
            main.Icon = icon;
        }

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "SmartMacro",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("Show")
                    {
                        Command = new RelayCommand(ShowMainWindow),
                    },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("Exit")
                    {
                        Command = new RelayCommand(RequestExit),
                    },
                },
            },
        };

        // Single-click on the tray icon brings the main window back. The Clicked event
        // fires for left-click on Windows; right-click shows the menu automatically.
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
    }

    /// <summary>
    /// Brings the main window back from a hidden / minimised state and activates it.
    /// Posted to the UI thread because callers (tray click, menu command) can run on
    /// arbitrary threads.
    /// </summary>
    public void ShowMainWindow()
    {
        if (_desktop.MainWindow is not { } main)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!main.IsVisible)
            {
                main.Show();
            }
            if (main.WindowState == WindowState.Minimized)
            {
                main.WindowState = WindowState.Normal;
            }
            main.Activate();
        });
    }

    /// <summary>
    /// Sets <see cref="IsExitRequested"/> and triggers application shutdown. The exit flag
    /// is read by MainWindow's OnClosing handler so it proceeds with the actual close
    /// instead of hiding to the tray.
    /// </summary>
    public void RequestExit()
    {
        _exitRequested = true;
        // Hide the tray icon promptly so it doesn't linger as a ghost while host services
        // drain. Disposal happens during shutdown but TrayIcons can be slow to disappear
        // on Windows when the shell explorer is under load.
        Dispose();
        Dispatcher.UIThread.Post(() => _desktop.Shutdown());
    }

    public void Dispose()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}
