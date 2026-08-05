using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Один ПРОЧИТАВШИЙСЯ макрос библиотеки демона: граф, паспорт бандла, опись шаблонов — и вердикт
/// валидатора, вынесенный при загрузке.
///
/// Появилась в F2 вместе с бандлами. До них «макрос» и «граф» были одним и тем же: файл содержал
/// ровно <see cref="MacroGraph"/>, и снимок библиотеки был списком графов. Теперь у файла есть то,
/// чего в графе нет и быть не должно, — <see cref="MacroBundleMetadata.Id"/>, даты и СОБСТВЕННЫЕ
/// ШАБЛОНЫ, — а спрашивают про них независимо от графа.
///
/// <b>F3 добавила сюда <see cref="Issues"/>, и это не украшение.</b> Пока писал демон, работала
/// гарантия «оно в библиотеке ⇒ демон его принял»: <c>SaveMacro</c> отвечал списком проблем и не
/// записывал ничего, пока в графе была ошибка. Автор теперь панель, и гарантия сменилась на «кто-то
/// положил туда файл» — в том числе руками, в том числе из чужой сборки. Значит, судить о графе
/// обязан тот, кто его исполняет, и делать это при загрузке: иначе «хоткей нажимается, ничего не
/// происходит» вернулось бы с другой стороны — ровно тот дефект, который в D4 закрывали через
/// <c>GetHotkeyFailures</c>. Валидатор при этом ОБЩИЙ (<see cref="MacroGraphValidator"/> в
/// <c>Shared</c>) и опись ему подаётся ТА ЖЕ, что у панели, — два прогона одного правила не имеют
/// права разойтись.
///
/// <see cref="MacroGraphStore.All"/> по-прежнему отдаёт голые графы: так на неё смотрит всё, что
/// про бандлы знать не обязано (оркестратор, разрешение под-макросов).
/// </summary>
/// <param name="Name">Имя макроса = основа имени файла. Оно же идентичность (§5.7).</param>
/// <param name="Graph">Граф из <c>nodes.json</c>.</param>
/// <param name="Metadata">Паспорт из <c>metadata.json</c>.</param>
/// <param name="TemplatePaths">
/// Пути шаблонов относительно <see cref="MacroBundleFormat.TemplateFolder"/> — <c>classes/Лучник.png</c>,
/// а не <c>templates/classes/Лучник.png</c>. БЕЗ БАЙТОВ: их читает кэш исполнителя, по требованию.
/// </param>
/// <param name="Submacros">
/// Под-макросы бандла (волна F4) — то, из чего оркестратор собирает
/// <c>MacroRunContext.Submacros</c>. Приезжают вместе с графом и из того же файла, поэтому
/// «граф свежий, а функции старые» невозможно: у них одна запись библиотеки на всех.
/// </param>
/// <param name="Path">Абсолютный путь к <c>.hsm</c>. По нему читает кэш шаблонов.</param>
/// <param name="Issues">Что сказал валидатор обо ВСЁМ бандле при загрузке, вместе с предупреждениями.</param>
public sealed record MacroLibraryEntry(
    string Name,
    MacroGraph Graph,
    MacroBundleMetadata Metadata,
    IReadOnlyList<string> TemplatePaths,
    IReadOnlyList<MacroSubmacro> Submacros,
    string Path,
    IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>
    /// Под-макросы по их <c>Guid</c> — ровно то, что кладут в <c>MacroRunContext</c>. Считается
    /// один раз: запись неизменяемая, а спрашивают карту на каждый прогон.
    /// </summary>
    public IReadOnlyDictionary<Guid, MacroGraph> SubmacrosById { get; } =
        Submacros.ToDictionary(submacro => submacro.Id, submacro => submacro.Graph);

    /// <summary>
    /// Опись шаблонов бандла — то, чем валидатор сверяет имена из нод. Считается один раз при
    /// загрузке: запись неизменяемая, а спрашивают её на каждую диагностику.
    /// </summary>
    public MacroTemplateInventory Templates { get; } = MacroTemplateInventory.FromPaths(TemplatePaths);

    /// <summary>
    /// В графе есть ошибка, а значит, исполнять его нечем: обход упрётся в неё и оборвётся.
    /// Хоткей такого макроса демон НЕ вооружает — см. <c>HotkeyListener</c>.
    /// </summary>
    public bool HasErrors { get; } = Issues.Any(issue => issue.Severity == ValidationSeverity.Error);

    /// <summary>Первая ошибка одной строкой — то, что уходит в журнал при загрузке.</summary>
    public string? FirstError => Issues
        .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error)
        ?.Message;
}
