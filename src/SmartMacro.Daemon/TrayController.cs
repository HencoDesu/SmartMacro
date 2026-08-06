using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Native.Tray;
using SmartMacro.Resources;

namespace SmartMacro.Daemon;

// Единственная обращённая к пользователю поверхность демона: иконка в трее с двумя командами.
//
//   Открыть панель — запускает SmartMacro.exe из корня установки (в поставке это папка уровнем
//                    выше нашей; UiExecutableLocator знает и про дерево разработки).
//   Выход          — просит хост завершиться, что расчищает hosted-сервисы (прогоны макросов
//                    отменяются → окна снимаются с регистрации → горячие клавиши
//                    разрегистрируются → StopAsync этого контроллера разбирает иконку).
//
// Win32TrayIcon поднимает ItemClicked в потоке своего насоса сообщений, изнутри модального
// цикла всплывающего меню. Поэтому оба обработчика не делают ничего, кроме передачи работы в
// пул потоков: блокирующий Process.Start (запрос UAC может простоять минутами) или синхронная
// остановка хоста подвесили бы трей и, что хуже, заклинили бы обработку ввода в оболочке,
// пока TrackPopupMenuEx ещё на стеке.
internal sealed partial class TrayController : IHostedService, IDisposable
{
    private const string OpenPanelItemId = "open-panel";
    private const string ExitItemId = "exit";

    private const string IconRelativePath = @"Assets\icon.ico";

    private readonly Win32TrayIcon _tray;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IIpcBroadcaster _broadcaster;
    private readonly ILogger<TrayController> _logger;
    private readonly Lock _uiProcessLock = new();

    // Процесс интерфейса, который мы породили, — пока он жив. Второй клик по «Открыть панель»
    // не запускает вторую копию (единственность экземпляра дополнительно обеспечена со стороны
    // интерфейса именованным мьютексом): он рассылает ActivateWindow всем подключённым
    // клиентам, и панель, уже открытая на экране, сама выходит на передний план.
    private Process? _uiProcess;

    public TrayController(
        Win32TrayIcon tray,
        IHostApplicationLifetime lifetime,
        IIpcBroadcaster broadcaster,
        ILogger<TrayController> logger)
    {
        _tray = tray;
        _lifetime = lifetime;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tray.ItemClicked += OnItemClicked;
        _tray.Start(
            Strings_Engine.Tray_Tooltip,
            Path.Combine(AppContext.BaseDirectory, IconRelativePath),
            [
                new TrayMenuItem(OpenPanelItemId, Strings_Engine.Tray_MenuItem_OpenPanel, IsDefault: true),
                new TrayMenuItem(ExitItemId, Strings_Engine.Tray_MenuItem_Exit),
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

    // Поток насоса сообщений — возвращать управление немедленно, всегда.
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
                // Панель может быть закрыта полноэкранными клиентами игры; она слушает это
                // событие и всплывает сама. «Отправил и забыл» — по замыслу: Broadcast никогда
                // не блокируется и не бросает исключений, а подтверждения, которого стоило бы
                // ждать, тут нет.
                LogPanelAlreadyRunning(_uiProcess.Id);
                _broadcaster.Broadcast(new IpcEvent(IpcMessageTypes.ActivateWindow));
                return;
            }

            _uiProcess?.Dispose();
            _uiProcess = null;

            var path = UiExecutableLocator.Resolve(AppContext.BaseDirectory);
            if (path is null)
            {
                LogPanelNotFound(string.Join(", ", UiExecutableLocator.ProbePaths(AppContext.BaseDirectory)));
                return;
            }

            try
            {
                // UseShellExecute = true, чтобы дочерний процесс получил повышение прав чистым
                // путём: оболочка сама учтёт манифест App с requireAdministrator, и нам не
                // придётся вручную мастерить токен. (Сегодня оба процесса идут с повышенными
                // правами — СОМНИТЕЛЬНО, см. app.manifest.) Заодно так по умолчанию
                // выставляется вменяемый рабочий каталог.
                _uiProcess = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    // Папка НАЙДЕННОГО файла, а не своя: в дереве разработки панель лежит в
                    // соседнем bin, и её собственные logs/ и appsettings.json должны
                    // разрешаться там, а не рядом с демоном.
                    WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
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
                // Отклонённый запрос UAC приходит сюда как Win32Exception(ERROR_CANCELLED).
                // Не фатально: демон продолжает работать, а пользователь может попробовать
                // снова.
                LogPanelStartFailed(ex, path);
                _uiProcess = null;
            }
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Панель запущена: '{Path}' (pid {Pid})")]
    partial void LogPanelStarted(string path, int pid);

    [LoggerMessage(LogLevel.Information, "Панель уже запущена (pid {Pid}) — просим её выйти на передний план")]
    partial void LogPanelAlreadyRunning(int pid);

    [LoggerMessage(LogLevel.Error, "Не найден исполняемый файл панели — искали: {ProbedPaths}")]
    partial void LogPanelNotFound(string probedPaths);

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
