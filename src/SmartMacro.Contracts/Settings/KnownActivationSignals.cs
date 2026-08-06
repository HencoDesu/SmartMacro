namespace SmartMacro.Contracts.Settings;

/// <summary>
/// Таблица известных «сигналов побудки»: имя процесса → магический lParam, который уходит с
/// <c>WM_ACTIVATEAPP</c>, чтобы разморозить фоновое окно перед вводом.
///
/// <b>Применяется ПРИ СОЗДАНИИ хука, а не при чтении.</b> Разница несущая: отсутствие записи в
/// <see cref="AppSettings.Hooks"/> означает «обычный процесс, пробуждение пропускается», и на этой
/// семантике держатся не-игровые процессы. Подставляй мы значение по таблице на чтении, у
/// <c>notepad</c> оно тоже однажды взялось бы «само», а стереть его стало бы нечем. Подстановка на
/// создании даёт то, что нужно: <c>elementclient_64</c> получает 37336 сразу, <c>notepad</c>
/// остаётся без хука, а у того, кому попадётся сборка игры с другим значением, остаётся правка
/// файла руками.
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
    /// Собирает скобку пробуждения для нового процесса — либо <c>null</c>, если имя незнакомо.
    ///
    /// <c>null</c> здесь и есть ответ «обычный процесс»: у такого в <see cref="AppSettings.Hooks"/>
    /// не появляется записи вовсе, а не появляется запись с нулями. Паузы имеют смысл только
    /// вокруг сигнала побудки, поэтому и живут внутри той же записи.
    ///
    /// Одно место, где применяется правило «узнанному имени — известные значения». Вызывается
    /// панелью, когда пользователь добавляет профиль, и <see cref="AppSettings.Default"/>, когда
    /// демон впервые создаёт файл.
    /// </summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    public static ProcessHookSettings? NewHook(string? processName) =>
        For(processName) is { } lParam
            ? new ProcessHookSettings
            {
                ActivationLParam = lParam,
                SettleMs = 200,
                DeactivateMs = 100,
                // Умолчания перечислены явно, а не оставлены инициализаторам записи: это ТО САМОЕ
                // поведение, которое волна обязана повторить дословно, и читать его надо здесь, а
                // не собирать по двум файлам.
                On = [HookOn.Input, HookOn.Capture],
                Scope = HookLifetime.Action,
            }
            : null;
}
