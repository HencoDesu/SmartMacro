using SmartMacro.Contracts.Dto;

namespace SmartMacro.Settings;

/// <summary>
/// Собирает то, что видит экран настроек, из двух источников с РАЗНЫМ временем жизни: файла
/// настроек (переживает перезапуск) и живого уровня журнала (не переживает).
///
/// Существует ради одного правила — <b>снимок собирается в одном месте</b>. Его строят двое:
/// диспетчер, отвечая на <c>GetSettings</c>, и сервер, рассылая <c>SettingsChanged</c>. Собери
/// они его каждый по-своему, ответ на запрос и содержимое пуша однажды разошлись бы, и панель
/// показывала бы разное в зависимости от того, что пришло последним.
///
/// Заодно это единственная точка, где уровень журнала МЕНЯЕТСЯ: сдвиг уровня обязан поднять то
/// же <see cref="Changed"/>, что и правка файла, — иначе вторая открытая панель осталась бы с
/// устаревшим значением.
/// </summary>
public sealed class SettingsSnapshotProvider
{
    private readonly SettingsStore _store;
    private readonly ILogLevelSwitch _logLevel;

    public SettingsSnapshotProvider(SettingsStore store, ILogLevelSwitch logLevel)
    {
        _store = store;
        _logLevel = logLevel;
        _store.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// Поднимается, когда меняется что угодно из показанного на экране настроек: файл (правка из
    /// панели, сброс, правка блокнотом) или живой уровень журнала.
    /// </summary>
    public event Action<SettingsSnapshotDto>? Changed;

    /// <summary>Текущее состояние экрана настроек целиком.</summary>
    public SettingsSnapshotDto Snapshot() =>
        new(_store.Current, _logLevel.Current, _store.FilePath, _store.FolderPath);

    /// <summary>
    /// Двигает минимальный уровень журнала демона.
    ///
    /// <b>Ничего не сохраняет — и это не упущение.</b> Постоянный уровень остаётся в
    /// <c>appsettings.json</c>, чтобы падение на чтении файла настроек можно было расследовать
    /// уровнем, известным ДО этого чтения; см. <see cref="ILogLevelSwitch"/>. Сдвиг живёт до
    /// перезапуска демона, и интерфейс обязан это сказать.
    /// </summary>
    /// <param name="level">Новый минимальный уровень.</param>
    public void SetLogLevel(LogLevelDto level)
    {
        _logLevel.Current = level;
        Changed?.Invoke(Snapshot());
    }

    /// <summary>Отцепляется от хранилища. Идемпотентно — <c>-=</c> на неподписанном обработчике безвреден.</summary>
    public void Detach() => _store.SettingsChanged -= OnSettingsChanged;

    private void OnSettingsChanged(Contracts.Settings.AppSettings settings) => Changed?.Invoke(Snapshot());
}
