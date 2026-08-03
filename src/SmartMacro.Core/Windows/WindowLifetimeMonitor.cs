using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Settings;

namespace SmartMacro.Windows;

// Одна служба на все окна: раз в «Watch.WindowPollIntervalSeconds» обходит снимок
// WindowRegistry и снимает с регистрации те окна, чьих клиентов больше нет. Интервал читается
// перед каждой паузой, а не запоминается при создании, — правка настройки применяется со
// следующего тика.
//
// Зачем опрос вообще нужен: мёртвый hwnd, оставшийся в реестре, продолжает подходить под
// теговые селекторы, и каждое разветвление будет впустую тратить на него цикл
// активации/деактивации, упираясь в замороженный (а на деле уже уничтоженный) насос сообщений.
// Запись обязана исчезнуть быстро — вместе с тегами и с поднятым WindowClosed для подписчиков.
//
// Почему одна служба, а не объект на окно: до W0.4 этим занимался CharacterAgent — по
// экземпляру на клиента, каждый со своей задачей, своим CancellationTokenSource, своим
// логгером и уведомлением оркестратора через канал. Интервал опроса у всех был один и тот же,
// а тик — это единственный вызов IsWindow, так что N задач не покупали ничего, кроме четырёх
// слоёв обвязки вокруг факта «окно закрылось».
//
// Почему не внутри WindowRegistry (как предлагал тот TODO): реестр — чистый синхронный
// держатель состояния под одной блокировкой, и именно поэтому он тривиально проверяется
// тестами. Таймер и фоновая задача внутри него это сломали бы.
//
// Регистрации окон здесь НЕТ намеренно — она живёт в Orchestrator, в пути «появился процесс»,
// вплотную к запуску загрузочных макросов, потому что между этими двумя шагами обязан
// сохраняться порядок. Монитор — только про смерть окна.
public sealed partial class WindowLifetimeMonitor : IHostedService, IDisposable
{
    private readonly WindowRegistry _registry;
    private readonly ISettingsSource _settings;
    private readonly ILogger<WindowLifetimeMonitor> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WindowLifetimeMonitor(
        WindowRegistry registry,
        ISettingsSource settings,
        ILogger<WindowLifetimeMonitor> logger)
    {
        _registry = registry;
        _settings = settings;
        _logger = logger;
    }

    private TimeSpan PollInterval => TimeSpan.FromSeconds(_settings.Current.Watch.WindowPollIntervalSeconds);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("Window lifetime monitor already started.");
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        LogStarted(PollInterval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Гасит цикл опроса и снимает с регистрации всё, что в реестре ещё осталось: при выходе
    /// демона окна перестают быть управляемыми независимо от того, живы ли их процессы.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_loop is not null)
                {
                    await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _loop = null;
            }
        }

        UnregisterAll();
        LogStopped();
    }

    /// <summary>
    /// Один проход по реестру: снимает с регистрации окна, чей клиент больше не жив.
    /// </summary>
    /// <returns>Сколько записей убрано за этот проход.</returns>
    public int Sweep()
    {
        var removed = 0;
        foreach (var window in _registry.Snapshot())
        {
            // Запись без фасада (тесты, будущие регистрации со стороны UI) проверить нечем —
            // о живости такого окна нам никто не рассказывает, так что трогать его не наше
            // дело.
            var facade = _registry.TryGetWindow(window.Hwnd);
            if (facade is null || facade.IsAlive)
            {
                continue;
            }

            LogWindowGone(window.Hwnd.ToInt64(), window.ProcessName);
            if (_registry.Unregister(window.Hwnd))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Снимает с регистрации все окна разом — выключение демона.
    /// </summary>
    /// <returns>Сколько записей убрано.</returns>
    public int UnregisterAll()
    {
        var removed = 0;
        foreach (var window in _registry.Snapshot())
        {
            if (_registry.Unregister(window.Hwnd))
            {
                removed++;
            }
        }

        return removed;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex)
            {
                // Один сбойный тик не имеет права оставить остальные окна без присмотра.
                LogSweepFailed(ex);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Слежение за временем жизни окон запущено (опрос {Poll})")]
    partial void LogStarted(TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Слежение за временем жизни окон остановлено")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Information,
        "Окно hwnd=0x{Hwnd:X} процесса '{ProcessName}' больше не живо — снимаем с регистрации")]
    partial void LogWindowGone(long hwnd, string processName);

    [LoggerMessage(LogLevel.Error, "Проход по окнам не удался; цикл продолжается")]
    partial void LogSweepFailed(Exception ex);

    #endregion
}
