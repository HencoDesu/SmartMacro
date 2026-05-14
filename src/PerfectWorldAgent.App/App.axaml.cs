using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace PerfectWorldAgent.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private bool _exitRequested;

    // Set to true by the tray "Exit" command before triggering Shutdown so MainWindow's
    // OnClosing hide-to-tray handler knows it's a real exit, not a window-close request.
    // Exposed via IsExitRequested for MainWindow to read.
    public bool IsExitRequested => _exitRequested;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Program.Services is { } services
                ? services.GetRequiredService<MainWindow>()
                : new MainWindow();

            // Keep the host process alive when the last window closes — we want the tray
            // icon to remain active so the user can re-open the window. Real exit only via
            // the tray Exit menu item (which calls desktop.Shutdown explicitly).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            InstallTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InstallTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Avalonia bundles AssetLoader to read AvaloniaResource entries via avares:// URIs.
        // The Bitmap is constructed once here and reused for both the tray and the window
        // icon — saves rasterising twice and keeps both visuals consistent.
        using var iconStream = AssetLoader.Open(new Uri("avares://PerfectWorldAgent.App/Assets/icon.png"));
        var icon = new WindowIcon(iconStream);

        if (desktop.MainWindow is { } main)
        {
            main.Icon = icon;
        }

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perfect World Agent",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("Show")
                    {
                        Command = new RelayCommand(() => ShowMainWindow(desktop)),
                    },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("Exit")
                    {
                        Command = new RelayCommand(() => RequestExit(desktop)),
                    },
                },
            },
        };

        // Single-click on the tray icon brings the main window back. The Clicked event
        // fires for left-click on Windows; right-click shows the menu automatically.
        _trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not { } main)
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

    private void RequestExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exitRequested = true;
        // Hide the tray icon promptly so it doesn't linger as a ghost while host services
        // drain. Disposal happens during shutdown but TrayIcons can be slow to disappear
        // on Windows when the shell explorer is under load.
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }
}

// Tiny ICommand implementation for the tray menu items. CommunityToolkit.Mvvm would give
// us [RelayCommand] but we're not pulling that in for a 2-line class.
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
