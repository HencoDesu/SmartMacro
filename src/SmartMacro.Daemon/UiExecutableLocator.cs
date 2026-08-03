using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Daemon;

/// <summary>
/// Определяет путь к исполняемому файлу панели на Avalonia, который запускает пункт трея
/// «Открыть панель».
/// </summary>
/// <remarks>
/// Зеркало панельного <c>DaemonLauncher</c>: первым пользователь может запустить любой из двух
/// процессов, поэтому каждый обязан уметь поднять другой, и ищут они одинаково. Сам поиск — в
/// <see cref="PeerExecutableLocator"/>; здесь только имена. Раньше эти два локатора разъехались
/// (пусковик демона знал про дерево разработки, а этот — нет), и «Открыть панель» из трея молча
/// не работало под <c>dotnet run</c>.
///
/// Локаторов при этом всё равно два, а не один класс на оба направления: каждый знает свои три
/// имени и своего вызывающего, а объединённый пришлось бы параметризовать направлением — то
/// есть теми же тремя строками, только спрятанными за enum.
/// </remarks>
public static class UiExecutableLocator
{
    /// <summary>Имя файла процесса интерфейса — такое, каким его выпускает <c>SmartMacro.App.csproj</c>.</summary>
    public const string UiExecutableName = "SmartMacro.App.exe";

    private const string DaemonProjectFolder = "SmartMacro.Daemon";
    private const string AppProjectFolder = "SmartMacro.App";

    /// <summary>
    /// Пути, которые проверяются, по порядку. Вынесено отдельно, чтобы неудачный
    /// <see cref="Resolve"/> можно было записать в лог с точным перечнем мест, где искали:
    /// «не найдено» без пути — бесполезная диагностика.
    /// </summary>
    public static IReadOnlyList<string> ProbePaths(string baseDirectory) =>
        PeerExecutableLocator.ProbePaths(baseDirectory, UiExecutableName, DaemonProjectFolder, AppProjectFolder);

    /// <summary>
    /// Возвращает полный путь к исполняемому файлу интерфейса или <c>null</c>, если его нет ни
    /// в одном из мест, куда мы смотрим.
    /// </summary>
    /// <param name="baseDirectory">Каталог, в котором искать, — обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Проверка существования файла; по умолчанию <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null) =>
        PeerExecutableLocator.Resolve(
            baseDirectory, UiExecutableName, DaemonProjectFolder, AppProjectFolder, fileExists ?? File.Exists);
}
