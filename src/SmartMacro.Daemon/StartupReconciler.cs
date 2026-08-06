using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Settings;
using SmartMacro.Settings;

namespace SmartMacro.Daemon;

/// <summary>
/// Держит запись автозапуска в согласии с галочкой — при старте демона и после каждой правки
/// настроек.
///
/// Отдельная размещённая служба, а не строчка в <c>SettingsStore</c>, по причине, которая в этом
/// проекте уже записана инвариантом: <b>хранилище не имеет права трогать состояние за пределами
/// своего файла</b>. Правка реестра — побочный эффект уровня системы, и он обязан быть видим на
/// месте вызова, а не случаться оттого, что кто-то построил объект.
///
/// <b>Согласование при старте, а не только по правке, — несущее.</b> Демон обязан работать без
/// панели: она по требованию, её может не быть сутками. Установка, где файл настроек говорит
/// «запускать при входе», а записи в системе нет (её снёс чистильщик, или папку перенесли, или
/// файл принесли с другой машины), должна чиниться сама при первом же запуске, а не ждать, пока
/// кто-нибудь откроет экран настроек и нажмёт «Применить». И оно ПОЛНОЕ: снятая галочка обязана
/// запись удалять, иначе программа продолжит запускаться после того, как её об этом перестали
/// просить.
///
/// <b>Вторая галочка сюда не относится.</b> Права — вопрос старта процесса, а не записи в
/// системе, и решается он до построения хоста (<see cref="ElevationRelaunch"/>): к моменту, когда
/// эта служба получает управление, повышение либо уже получено, либо о его отсутствии уже
/// сказано. Перепроверять здесь нечего, а перезапускать работающий демон, который, возможно,
/// ведёт прогон по десяти клиентам, — тем более.
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Reconcile(_settings.Current);
        _settings.SettingsChanged += OnSettingsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        return Task.CompletedTask;
    }

    // Синхронно, прямо на потоке события. Так стало можно, когда из согласования ушёл
    // Планировщик: запуск schtasks занимал сотни миллисекунд и его приходилось отцеплять от
    // await'а, чтобы панель не ждала службу задач ради ответа «число в поле принято». Запись
    // одного значения в свою же ветку реестра столько не стоит.
    private void OnSettingsChanged(AppSettings settings) => Reconcile(settings);

    private void Reconcile(AppSettings settings)
    {
        try
        {
            var failure = _autoStart.Apply(settings, Environment.ProcessPath ?? string.Empty);
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

    [LoggerMessage(LogLevel.Warning, "Автозапуск не согласован: {Reason}")]
    partial void LogAutoStartFailed(string reason);

    [LoggerMessage(LogLevel.Error, "Согласование автозапуска не удалось")]
    partial void LogReconcileFailed(Exception ex);
}
