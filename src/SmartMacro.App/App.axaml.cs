using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Native.Dialogs;

namespace SmartMacro.App;

/// <summary>
/// Объект приложения Avalonia и два правила жизненного цикла панели.
///
/// <b>Закрыли окно — вышли из процесса.</b> До стадии 3 крестик прятал приложение в иконку
/// трея, потому что этот процесс И БЫЛ движком, и его закрытие остановило бы автоматизацию.
/// Больше это не так: демон продолжает работать, владеет треем и по требованию возвращает
/// панель на экран, — так что окно ведёт себя как окно.
///
/// <b>Жив демон — живы и мы.</b> Панель, у которой оборвалось соединение, не может ни
/// показать ничего правдивого, ни изменить ничего, поэтому потеря соединения сообщается один
/// раз и процесс завершается, а не сидит на экране, рисуя застывший снимок.
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

            // MainWindow без параметров — путь дизайнера; во время работы Services есть
            // всегда, потому что Program отказывается запускать Avalonia без соединения.
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
        using var stream = AssetLoader.Open(new Uri("avares://SmartMacro/Assets/icon.png"));
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
    /// Возвращает окно из свёрнутого или заваленного состояния и выводит его вперёд.
    /// Поднимается по «Открыть панель» в трее и по второму запуску этого же исполняемого файла.
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

        // Windows отказывает в SetForegroundWindow процессу, который и так не на переднем
        // плане, а клиенты игры идут в полный экран — от одного только Activate() кнопка на
        // панели задач часто просто помигает. Прыжок через topmost продавливает смену
        // z-порядка, не оставляя панель приколотой поверх всего остального.
        main.Topmost = true;
        main.Topmost = false;
        Serilog.Log.Information("Панель выведена на передний план по запросу демона");
    }

    private void OnDaemonDisconnected()
    {
        // Клиент переподключается сам, но UI не умеет продолжить с разрыва, дальнего края
        // которого он не видел, — а остановившийся демон обычно означает, что пользователь
        // нажал «Выход» в трее. Сообщаем один раз: цикл переподключения не должен складывать
        // диалоги стопкой.
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
