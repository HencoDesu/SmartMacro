namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// Где корень установки — то есть где лежат <c>macros/</c>, <c>templates/</c>,
/// <c>settings.json</c> и <c>debug/</c>. Тот же довод, что и у <see cref="PeerExecutableLocator"/>
/// рядом: ответ нужен ОБОИМ процессам (демон там хранит состояние, панель показывает путь и
/// открывает папку кнопкой), а другой общей сборки у них нет.
///
/// <b>Раскладка поставки:</b>
/// <code>
///   SmartMacro\
///     SmartMacro.exe        панель, single-file — единственное, что видно в корне
///     macros\  templates\  settings.json  logs\  debug\
///     daemon\
///       SmartMacro.Daemon.exe + рантайм + appsettings.json + logs\
/// </code>
///
/// <b>Правило ровно одно и оно решается на компиляции: в DEBUG корень — своя папка, в Release —
/// родительская.</b> Никакого зондирования файловой системы и никаких файлов-маркеров. В дереве
/// разработки каждый проект собирается в свой <c>bin\</c>, и «рядом с собой» там правильный
/// ответ; в поставке демон лежит в <c>daemon\</c>, и корень уровнем выше.
///
/// ⚠️ Следствие, которое лучше знать заранее: <b>Release-сборка, запущенная прямо из
/// <c>bin\Release\net10.0-windows\</c>, будет искать состояние в <c>bin\Release\</c></b> — на
/// уровень выше своего выхлопа. Это последовательно (правило одно, исключений нет), но выглядит
/// неожиданно, если не знать. Гонять Release из дерева сборки — не тот сценарий, ради которого
/// стоит заводить второй механизм.
///
/// Ошибка здесь молчаливая и дорогая: демон заведёт себе свои <c>macros\</c>, панель будет
/// смотреть в другие, библиотека окажется пустой — и ни одной строки об ошибке. Поэтому
/// вычисленный корень демон ПИШЕТ В ЖУРНАЛ на старте (<c>Program.Main</c>), а проба пера на
/// запись этого не ловит по построению: она про права, а не про адрес.
///
/// Как и у соседа, тут только арифметика над путями — файловую систему не трогаем (в Contracts
/// файловый ввод-вывод не заезжает по жёсткому правилу, см. комментарий в
/// <c>SmartMacro.Contracts.csproj</c>).
/// </summary>
public static class InstallationLayout
{
    /// <summary>Имя подпапки с демоном внутри корня поставки.</summary>
    public const string DaemonFolderName = "daemon";

    /// <summary>
    /// Раскладка поставки (демон в подпапке) или дерева разработки (каждый проект в своём
    /// <c>bin\</c>). Решается конфигурацией сборки и ничем больше.
    /// </summary>
    public static bool IsShippedLayout =>
#if DEBUG
        false;
#else
        true;
#endif

    /// <summary>
    /// Корень установки с точки зрения ДЕМОНА — боевая перегрузка, раскладку берёт из
    /// конфигурации сборки.
    /// </summary>
    /// <param name="daemonDirectory">Каталог демона, обычно <see cref="AppContext.BaseDirectory"/>.</param>
    public static string RootFromDaemonDirectory(string daemonDirectory) =>
        RootFromDaemonDirectory(daemonDirectory, IsShippedLayout);

    /// <summary>
    /// Корень установки с точки зрения ДЕМОНА. Раскладка параметром, а не <c>#if</c>, чтобы обе
    /// ветки можно было проверить одним прогоном тестов: конфигурация сборки тестов иначе решала
    /// бы, какую половину правила мы вообще видим.
    /// </summary>
    /// <param name="daemonDirectory">Каталог демона, обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="shippedLayout">
    /// <c>true</c> — поставка (корень уровнем выше), <c>false</c> — дерево разработки (корень —
    /// своя папка).
    /// </param>
    public static string RootFromDaemonDirectory(string daemonDirectory, bool shippedLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(daemonDirectory);
        var normalized = Normalize(daemonDirectory);
        if (!shippedLayout)
        {
            return normalized;
        }

        // Родитель корня диска — тот же корень диска: подниматься некуда, и это не повод падать.
        // Демон, распакованный в C:\, будет держать состояние в C:\ — странно, но честно.
        return Path.GetDirectoryName(normalized) is { Length: > 0 } parent ? parent : normalized;
    }

    /// <summary>
    /// Корень установки с точки зрения ПАНЕЛИ — боевая перегрузка.
    /// </summary>
    /// <param name="panelDirectory">Каталог панели, обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="daemonDirectory">
    /// Каталог найденного демона либо <c>null</c>, если его не нашли (см. <c>DaemonLauncher</c>).
    /// </param>
    public static string RootFromPanelDirectory(string panelDirectory, string? daemonDirectory) =>
        RootFromPanelDirectory(panelDirectory, daemonDirectory, IsShippedLayout);

    /// <summary>
    /// Корень установки с точки зрения ПАНЕЛИ. В поставке панель ЛЕЖИТ в корне, поэтому ответ —
    /// её собственная папка, и демона для этого искать не нужно. В дереве разработки панель
    /// собирается в свой <c>bin\</c>, где нет ни <c>macros\</c>, ни <c>settings.json</c>, — там
    /// корнем служит папка демона.
    ///
    /// Если демона не нашли вовсе, отвечаем своей папкой: в поставке это верно, а в дереве
    /// разработки поднимать панели всё равно нечего.
    /// </summary>
    /// <param name="panelDirectory">Каталог панели, обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="daemonDirectory">Каталог найденного демона либо <c>null</c>.</param>
    /// <param name="shippedLayout">
    /// <c>true</c> — поставка, <c>false</c> — дерево разработки.
    /// </param>
    public static string RootFromPanelDirectory(string panelDirectory, string? daemonDirectory, bool shippedLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelDirectory);
        return shippedLayout || string.IsNullOrWhiteSpace(daemonDirectory)
            ? Normalize(panelDirectory)
            : Normalize(daemonDirectory);
    }

    /// <summary>Папка демона внутри корня поставки — <c>{root}\daemon</c>.</summary>
    /// <param name="root">Корень установки.</param>
    public static string DaemonDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(Normalize(root), DaemonFolderName);
    }

    // AppContext.BaseDirectory всегда приходит с завершающим разделителем, а Path.GetDirectoryName
    // на такой строке отдаёт саму папку, а не её родителя. Снимаем разделитель один раз здесь,
    // чтобы каждый вызывающий не помнил об этом отдельно.
    private static string Normalize(string directory) =>
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
