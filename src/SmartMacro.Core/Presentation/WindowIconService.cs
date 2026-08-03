using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;

namespace SmartMacro.Presentation;

/// <summary>
/// Ставит файл иконки в заголовок игрового окна и в его слот на панели задач. За ним стоит
/// <c>SetIconNode</c>, чей <c>IconPath</c> исполнитель уже подставил
/// (например, <c>"Assets/ClassIcons/{tag}.png"</c> → <c>"Assets/ClassIcons/Лучник.png"</c>).
///
/// Сверх голого вызова <see cref="IGameWindow.SetIconFromFile"/> он владеет двумя вещами:
///   * разрешением пути — относительные пути считаются от каталога приложения, чтобы файлы
///     макросов оставались переносимыми;
///   * парой повторов после применения — собственная инициализация PW после загрузки временами
///     сбрасывает наш WM_SETICON, поэтому иконка отправляется ещё раз на +2 с и +5 с. Дёшево и
///     идемпотентно (HICON закэширован в Native.WindowIconCache).
///
/// Синглтон в DI; состояния, кроме таблицы унаследованных псевдонимов, нет.
/// </summary>
public sealed partial class WindowIconService
{
    /// <summary>
    /// Запасной путь для поставляемого набора иконок PW: теги опознания — это русские имена
    /// классов, а в <c>Assets/ClassIcons</c> файлы названы по-английски. Когда подставленного
    /// пути не существует, мы пробуем ещё раз через эту таблицу, чтобы примеры <c>pw-boot</c> и
    /// <c>pw-identify</c> работали с теми ассетами, что уже лежат в репозитории.
    /// TODO(W0.4): удалить вместе с переименованием файлов иконок в имена тегов — самому движку
    /// незачем знать словарь классов PW.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyTagIconStems =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Маг"] = "mage",
            ["Воин"] = "warrior",
            ["Стрелок"] = "gunner",
            ["Друид"] = "druid",
            ["Оборотень"] = "tank", // Варвар / перевёртыш
            ["Странник"] = "rover",
            ["Жрец"] = "priest",
            ["Лучник"] = "archer",
            ["Паладин"] = "paladin",
            ["Шаман"] = "shaman",
            ["Убийца"] = "assassin",
            ["Бард"] = "bard",
            ["Мистик"] = "mystic",
            ["Страж"] = "guardian",
            ["ДухКрови"] = "bloodspirit",
            ["Жнец"] = "reaper",
            ["Призрак"] = "ghost",
            // Канлонг — в текущем наборе пользователя иконки нет; молча пропустится.
        };

    // Расписание повторов после применения — гонка с инициализацией PW сразу после загрузки.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly ILogger<WindowIconService> _logger;

    public WindowIconService(ILogger<WindowIconService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// По возможности применяет <paramref name="iconPath"/> к <paramref name="window"/> плюс два
    /// фоновых повтора. Любой сбой пишется в лог и докладывается, но никогда не бросается —
    /// косметическая иконка не имеет права обрывать прогон макроса.
    /// </summary>
    /// <returns><c>true</c>, если первое применение удалось.</returns>
    public bool TryApply(IGameWindow window, string iconPath)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return false;
        }

        var resolved = ResolvePath(iconPath);
        if (resolved is null)
        {
            LogIconMissing(iconPath);
            return false;
        }

        var applied = ApplyOnce(window, resolved);

        // Повторы «отправил и забыл». Повторяем, даже если первое применение не удалось: PW мог
        // быть в разгаре инициализации и потерять SendMessage, а более поздняя отправка вполне
        // может прижиться.
        _ = Task.Run(async () =>
        {
            foreach (var delay in RetryDelays)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    if (!window.IsAlive)
                    {
                        return;
                    }

                    ApplyOnce(window, resolved);
                }
                catch (Exception ex)
                {
                    LogIconException(ex, resolved);
                    return;
                }
            }
        });

        return applied;
    }

    // Относительный путь → каталог приложения. Если не попали, прежде чем сдаться, пробуем в той
    // же папке унаследованный псевдоним «русский тег → английская основа имени».
    private static string? ResolvePath(string iconPath)
    {
        var absolute = Path.IsPathRooted(iconPath)
            ? iconPath
            : Path.Combine(AppContext.BaseDirectory, iconPath);
        if (File.Exists(absolute))
        {
            return absolute;
        }

        var stem = Path.GetFileNameWithoutExtension(absolute);
        if (!LegacyTagIconStems.TryGetValue(stem, out var alias))
        {
            return null;
        }

        var aliased = Path.Combine(
            Path.GetDirectoryName(absolute) ?? AppContext.BaseDirectory,
            alias + Path.GetExtension(absolute));
        return File.Exists(aliased) ? aliased : null;
    }

    private bool ApplyOnce(IGameWindow window, string path)
    {
        try
        {
            if (window.SetIconFromFile(path))
            {
                LogIconApplied(path);
                return true;
            }

            LogIconLoadFailed(path);
            return false;
        }
        catch (Exception ex)
        {
            LogIconException(ex, path);
            return false;
        }
    }

    [LoggerMessage(LogLevel.Debug,
        "Icon file not found for '{IconPath}' (nor under its legacy alias); window icon stays default")]
    partial void LogIconMissing(string iconPath);

    [LoggerMessage(LogLevel.Information, "Window icon applied from {Path}")]
    partial void LogIconApplied(string path);

    [LoggerMessage(LogLevel.Warning, "Window icon load failed for {Path} (file may be corrupt or not a valid image)")]
    partial void LogIconLoadFailed(string path);

    [LoggerMessage(LogLevel.Error, "Window icon application threw for {Path}")]
    partial void LogIconException(Exception ex, string path);
}
