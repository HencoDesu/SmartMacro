namespace SmartMacro.Contracts.Settings;

/// <summary>
/// Таблица известных «сигналов побудки»: имя процесса → магический lParam, который уходит с
/// <c>WM_ACTIVATEAPP</c>, чтобы разморозить фоновое окно перед вводом.
///
/// <b>Применяется ПРИ СОЗДАНИИ профиля, а не при чтении.</b> Разница несущая:
/// <see cref="ProcessProfileSettings.ActivationLParam"/> со значением <c>null</c> означает
/// «обычный процесс, пробуждение пропускается», и на этой семантике держатся профили не-игровых
/// процессов. Подставляй мы значение по таблице на чтении, у профиля <c>notepad</c> оно тоже
/// однажды взялось бы «само», а стереть его стало бы нечем. Подстановка на создании даёт то, что
/// нужно: <c>elementclient_64</c> получает 37336 сразу, <c>notepad</c> остаётся без сигнала, а у
/// того, кому попадётся сборка игры с другим значением, остаётся правка файла руками.
///
/// Поэтому же числа нет в интерфейсе. Это добытый реверсом костыль под одну игру, а не
/// настройка: стереть его — значит получить фоновые окна, молча перестающие принимать ввод, без
/// единой ошибки в журнале.
/// </summary>
public static class KnownActivationSignals
{
    // 0x91D8. Добыто реверсом клиента Perfect World; проверено на elementclient_64 автора.
    private const uint PerfectWorldClient = 37336;

    private static readonly Dictionary<string, uint> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elementclient_64"] = PerfectWorldClient,
        ["elementclient"] = PerfectWorldClient,
    };

    /// <summary>Сигнал побудки для узнанного имени процесса либо <c>null</c> — «обычный процесс».</summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    public static uint? For(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && Table.TryGetValue(processName, out var lParam)
            ? lParam
            : null;

    /// <summary>
    /// Собирает профиль для нового процесса: сигнал побудки по таблице, а вместе с ним и паузы,
    /// которые без него не нужны.
    ///
    /// Одно место, где применяется правило «узнанному имени — известные значения». Вызывается
    /// панелью, когда пользователь добавляет профиль, и <see cref="AppSettings.Default"/>, когда
    /// демон впервые создаёт файл.
    /// </summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    public static ProcessProfileSettings NewProfile(string processName)
    {
        var lParam = For(processName);
        return new ProcessProfileSettings
        {
            ProcessName = processName,
            ActivationLParam = lParam,
            // Паузы имеют смысл только вокруг сигнала побудки: без него нечего ни размораживать,
            // ни укладывать обратно спать, и GameWindow их пропускает целиком.
            SettleDelayMs = lParam is null ? 0 : 200,
            DeactivationDelayMs = lParam is null ? 0 : 100,
        };
    }
}
