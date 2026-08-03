using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Settings;
using SmartMacro.Settings;

namespace SmartMacro.Daemon;

/// <summary>
/// Держит записи автозапуска в согласии с галочками — при старте демона и после каждой правки
/// настроек.
///
/// Отдельная размещённая служба, а не строчка в <c>SettingsStore</c>, по причине, которая в этом
/// проекте уже записана инвариантом: <b>хранилище не имеет права трогать состояние за пределами
/// своего файла</b>. Правка реестра и Планировщика — побочный эффект уровня системы, и он обязан
/// быть видим на месте вызова, а не случаться оттого, что кто-то построил объект.
///
/// <b>Согласование при старте, а не только по правке, — несущее.</b> Демон обязан работать без
/// панели: она по требованию, её может не быть сутками. Установка, где файл настроек говорит
/// «запускать при входе», а записи в системе нет (её снёс чистильщик, или папку перенесли, или
/// файл принесли с другой машины), должна чиниться сама при первом же запуске, а не ждать, пока
/// кто-нибудь откроет экран настроек и нажмёт «Применить».
/// </summary>
internal sealed partial class StartupReconciler : IHostedService
{
    private readonly SettingsStore _settings;
    private readonly AutoStartManager _autoStart;
    private readonly ILogger<StartupReconciler> _logger;

    public StartupReconciler(
        SettingsStore settings,
        AutoStartManager autoStart,
        ILogger<StartupReconciler> logger)
    {
        _settings = settings;
        _autoStart = autoStart;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Права проверяются ОДИН раз, при старте, и только сообщением в журнал. Перезапустить
        // себя через runas отсюда нельзя: exe демона помечен requireAdministrator, то есть
        // процесс и так всегда повышен, и ветка «— / ✓» из таблицы галочек в текущей сборке
        // недостижима. Проверка оставлена и написана честно — она станет рабочей ровно в тот
        // день, когда манифест сменят на asInvoker, — но САМА себя не перезапускает: молчаливый
        // перезапуск демона, который в этот момент, возможно, ведёт прогон по десяти клиентам,
        // хуже строки в журнале.
        if (AutoStartManager.NeedsElevationRelaunch(_settings.Current))
        {
            LogNotElevated();
        }

        var failure = await _autoStart
            .ApplyAsync(_settings.Current, Environment.ProcessPath ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            LogAutoStartFailed(failure);
        }

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        return Task.CompletedTask;
    }

    // Отцеплено от await намеренно: обработчик события хранилища зовётся из-под его семафора
    // записи (и с потока наблюдателя за файлом), а schtasks — это запуск процесса на несколько
    // сотен миллисекунд. Держать на нём сохранение настроек значило бы, что панель ждёт
    // Планировщик, чтобы узнать, что число в поле принято.
    private void OnSettingsChanged(AppSettings settings) => _ = ReconcileAsync(settings);

    private async Task ReconcileAsync(AppSettings settings)
    {
        try
        {
            var failure = await _autoStart
                .ApplyAsync(settings, Environment.ProcessPath ?? string.Empty)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                LogAutoStartFailed(failure);
            }
        }
        catch (Exception ex)
        {
            // Автозапуск не настроился — это неприятность, а не повод уронить демон, который
            // прямо сейчас, возможно, ведёт прогон.
            LogReconcileFailed(ex);
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "Настройки требуют прав администратора, но процесс не повышен: ввод и захват экрана в окна клиентов, "
        + "запущенных от администратора, будут молча отбрасываться (UIPI). Перезапустите демон от администратора.")]
    partial void LogNotElevated();

    [LoggerMessage(LogLevel.Warning, "Автозапуск не согласован: {Reason}")]
    partial void LogAutoStartFailed(string reason);

    [LoggerMessage(LogLevel.Error, "Согласование автозапуска после правки настроек не удалось")]
    partial void LogReconcileFailed(Exception ex);
}
