using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace SmartMacro.App;

public partial class App : Application
{
    private TrayIconHost? _trayHost;

    // Read by MainWindow.OnClosing — when true the X button proceeds with the actual
    // shutdown instead of hiding the window to the tray. Set by TrayIconHost.RequestExit
    // just before triggering Shutdown.
    public bool IsExitRequested => _trayHost?.IsExitRequested ?? false;

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

            _trayHost = new TrayIconHost(desktop);
            _trayHost.Install();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
