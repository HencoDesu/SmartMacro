using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Один макрос библиотеки — целиком, каким его прочитали с диска: граф, паспорт бандла и опись
/// его шаблонов.
///
/// Появилась в F2 вместе с бандлами. До них «макрос» и «граф» были одним и тем же: файл содержал
/// ровно <see cref="MacroGraph"/>, и снимок библиотеки был списком графов. Теперь у файла есть то,
/// чего в графе нет и быть не должно, — <see cref="MacroBundleMetadata.Id"/>, даты и СОБСТВЕННЫЕ
/// ШАБЛОНЫ, — а спрашивают про них независимо от графа: валидатору нужна опись, чтобы поймать
/// ноду, назвавшую отсутствующий шаблон; диагностике — то же самое по всей библиотеке; браузеру в
/// редакторе — перечень файлов.
///
/// <see cref="MacroGraphStore.All"/> по-прежнему отдаёт голые графы: так на неё смотрит всё, что
/// про бандлы знать не обязано (оркестратор, хоткеи, <c>GetMacros</c>).
/// </summary>
/// <param name="Name">Имя макроса = основа имени файла. Оно же идентичность (§13.1).</param>
/// <param name="Graph">Граф из <c>nodes.json</c>.</param>
/// <param name="Metadata">Паспорт из <c>metadata.json</c>.</param>
/// <param name="TemplatePaths">
/// Пути шаблонов относительно <see cref="MacroBundleFormat.TemplateFolder"/> — <c>classes/Лучник.png</c>,
/// а не <c>templates/classes/Лучник.png</c>. БЕЗ БАЙТОВ: их читает кэш исполнителя, по требованию.
/// </param>
/// <param name="Path">Абсолютный путь к <c>.hsm</c>. По нему читает и кэш шаблонов, и браузер.</param>
public sealed record MacroLibraryEntry(
    string Name,
    MacroGraph Graph,
    MacroBundleMetadata Metadata,
    IReadOnlyList<string> TemplatePaths,
    string Path)
{
    /// <summary>
    /// Опись шаблонов бандла — то, чем валидатор сверяет имена из нод. Считается один раз при
    /// загрузке: запись неизменяемая, а спрашивают её на каждое сохранение и на каждую
    /// диагностику.
    /// </summary>
    public MacroTemplateInventory Templates { get; } = MacroTemplateInventory.FromPaths(TemplatePaths);
}
