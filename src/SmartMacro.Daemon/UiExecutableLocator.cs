namespace SmartMacro.Daemon;

/// <summary>
/// Определяет путь к исполняемому файлу панели на Avalonia, который запускает пункт трея
/// «Открыть панель».
/// </summary>
/// <remarks>
/// Оба исполняемых файла лежат бок о бок в одном выходном каталоге, поэтому «рядом со мной» —
/// это и есть вся стратегия поиска: ни реестра, ни PATH, ни конфигурации. Оставлено чистой
/// функцией от базового каталога плюс внедряемая проверка существования файла, чтобы это
/// можно было тестировать, не трогая файловую систему.
/// </remarks>
public static class UiExecutableLocator
{
    /// <summary>Имя файла процесса интерфейса — такое, каким его выпускает <c>SmartMacro.App.csproj</c>.</summary>
    public const string UiExecutableName = "SmartMacro.App.exe";

    /// <summary>
    /// Единственный путь, в который мы смотрим. Вынесен отдельно, чтобы неудачный
    /// <see cref="Resolve"/> можно было записать в лог с точным местом, где искали:
    /// «не найдено» без пути — бесполезная диагностика.
    /// </summary>
    public static string ProbePath(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        return Path.Combine(baseDirectory, UiExecutableName);
    }

    /// <summary>
    /// Возвращает полный путь к исполняемому файлу интерфейса или <c>null</c>, если его там нет.
    /// </summary>
    /// <param name="baseDirectory">Каталог, в котором искать, — обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Проверка существования файла; по умолчанию <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        var candidate = ProbePath(baseDirectory);
        var exists = fileExists ?? File.Exists;
        return exists(candidate) ? candidate : null;
    }
}
