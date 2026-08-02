using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Native.Tray;

namespace SmartMacro.Daemon;

// The daemon's only user-facing surface: a tray icon with two commands.
//
//   Открыть панель — launches SmartMacro.App.exe from our own output directory.
//   Выход          — asks the host to shut down, which drains the hosted services
//                    (macro runs cancelled → agents stopped → hotkeys unregistered →
//                    this controller's StopAsync tears the icon back down).
//
// Win32TrayIcon raises ItemClicked on its message-pump thread, from inside the popup menu's
// modal loop. Both handlers therefore do nothing but hand the work to the thread pool: a
// blocking Process.Start (UAC consent can sit there for minutes) or a synchronous host
// shutdown would freeze the tray and, worse, wedge the shell's input processing while
// TrackPopupMenuEx is still on the stack.
internal sealed partial class TrayController : IHostedService, IDisposable
{
    private const string OpenPanelItemId = "open-panel";
    private const string ExitItemId = "exit";

    private const string Tooltip = "SmartMacro";
    private const string IconRelativePath = @"Assets\icon.ico";

    private readonly Win32TrayIcon _tray;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TrayController> _logger;
    private readonly Lock _uiProcessLock = new();

    // The UI process we spawned, while it's alive. Stage 2B/3 replaces the "already running"
    // no-op with an IPC ActivateWindow round-trip; until then the best we can do is refuse to
    // start a second copy (two panels would mean two composition roots — see Program.cs).
    private Process? _uiProcess;

    public TrayController(
        Win32TrayIcon tray,
        IHostApplicationLifetime lifetime,
        ILogger<TrayController> logger)
    {
        _tray = tray;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tray.ItemClicked += OnItemClicked;
        _tray.Start(
            Tooltip,
            Path.Combine(AppContext.BaseDirectory, IconRelativePath),
            [
                new TrayMenuItem(OpenPanelItemId, "Открыть панель", IsDefault: true),
                new TrayMenuItem(ExitItemId, "Выход"),
            ]);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tray.ItemClicked -= OnItemClicked;
        _tray.Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _tray.ItemClicked -= OnItemClicked;
        lock (_uiProcessLock)
        {
            _uiProcess?.Dispose();
            _uiProcess = null;
        }
    }

    // Pump thread — return immediately, always.
    private void OnItemClicked(string itemId)
    {
        switch (itemId)
        {
            case OpenPanelItemId:
                _ = Task.Run(OpenPanel);
                break;

            case ExitItemId:
                LogExitRequested();
                _ = Task.Run(_lifetime.StopApplication);
                break;

            default:
                LogUnknownItem(itemId);
                break;
        }
    }

    private void OpenPanel()
    {
        lock (_uiProcessLock)
        {
            if (_uiProcess is { HasExited: false })
            {
                LogPanelAlreadyRunning(_uiProcess.Id);
                return;
            }

            _uiProcess?.Dispose();
            _uiProcess = null;

            var path = UiExecutableLocator.Resolve(AppContext.BaseDirectory);
            if (path is null)
            {
                LogPanelNotFound(UiExecutableLocator.ProbePath(AppContext.BaseDirectory));
                return;
            }

            try
            {
                // UseShellExecute = true so the child inherits elevation the clean way: the
                // shell honours the App's requireAdministrator manifest instead of us
                // hand-rolling a token. (Both processes are elevated today — QUESTIONABLE,
                // see app.manifest.) It also sets a sane working directory by default.
                _uiProcess = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });

                if (_uiProcess is null)
                {
                    LogPanelStartReturnedNull(path);
                    return;
                }

                LogPanelStarted(path, _uiProcess.Id);
            }
            catch (Exception ex)
            {
                // A refused UAC prompt lands here as Win32Exception(ERROR_CANCELLED). Not
                // fatal — the daemon keeps running and the user can try again.
                LogPanelStartFailed(ex, path);
                _uiProcess = null;
            }
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Панель запущена: '{Path}' (pid {Pid})")]
    partial void LogPanelStarted(string path, int pid);

    [LoggerMessage(LogLevel.Information, "Панель уже запущена (pid {Pid}) — второй экземпляр не создаём")]
    partial void LogPanelAlreadyRunning(int pid);

    [LoggerMessage(LogLevel.Error, "Не найден исполняемый файл панели — искали '{ProbedPath}'")]
    partial void LogPanelNotFound(string probedPath);

    [LoggerMessage(LogLevel.Error, "Не удалось запустить панель '{Path}'")]
    partial void LogPanelStartFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Warning, "Process.Start вернул null для '{Path}' — панель не запущена")]
    partial void LogPanelStartReturnedNull(string path);

    [LoggerMessage(LogLevel.Information, "Выход из трея — останавливаем демона")]
    partial void LogExitRequested();

    [LoggerMessage(LogLevel.Warning, "Неизвестный пункт меню трея: '{ItemId}'")]
    partial void LogUnknownItem(string itemId);

    #endregion
}
