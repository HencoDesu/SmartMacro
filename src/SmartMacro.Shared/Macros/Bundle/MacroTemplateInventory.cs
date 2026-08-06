namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Что за шаблоны есть в бандле — ИМЕНАМИ, без единого байта.
///
/// Существует ради одного вопроса: «нода называет шаблон — а он в бандле есть?». С волны F2 на
/// этот вопрос отвечает валидатор (<c>MacroGraphValidator</c>), то есть ответ приходит при
/// сохранении и при загрузке библиотеки, а не в разделе «НЕТ ФАЙЛА» браузера и не строчкой в
/// журнале посреди прогона. Это и есть повышение класса ошибки, ради которого шаблоны переехали
/// внутрь макроса: набор шаблонов бандла известен ровно так же точно, как имена в нодах.
///
/// <b>Два пространства имён, а не одно.</b> <see cref="Sets"/> — подпапки, которые целиком
/// называет <c>MatchTemplateSet</c>; <see cref="Singles"/> — файлы в корне, которые поимённо называют
/// <c>FindElement</c>/<c>WaitForElement</c>. Папка <c>classes</c> и файл <c>classes.png</c> — две
/// разные законные вещи, поэтому <see cref="Has"/> спрашивает и вид тоже.
///
/// Сравнение имён — <b>с учётом регистра</b>, как и у исполнителя: шаблон разрешается по
/// перечислению записей архива, а не через файловую систему, поэтому регистронезависимость NTFS
/// сюда не протекает и «Лучник» с «лучник» — разные имена.
/// </summary>
public sealed class MacroTemplateInventory
{
    private readonly HashSet<string> _singles;
    private readonly HashSet<string> _sets;

    private MacroTemplateInventory(HashSet<string> singles, HashSet<string> sets)
    {
        _singles = singles;
        _sets = sets;
    }

    /// <summary>Опись пустого бандла. Всякая ссылка на шаблон в ней отсутствует.</summary>
    public static MacroTemplateInventory Empty { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Имена одиночных шаблонов (файлы в корне <c>templates/</c> бандла).</summary>
    public IReadOnlyCollection<string> Singles => _singles;

    /// <summary>Имена наборов (подпапки <c>templates/</c> бандла).</summary>
    public IReadOnlyCollection<string> Sets => _sets;

    /// <summary><c>true</c>, когда в бандле нет ни одного шаблона.</summary>
    public bool IsEmpty => _singles.Count == 0 && _sets.Count == 0;

    /// <summary>
    /// Строит опись по путям из <see cref="MacroBundleReadResult.TemplatePaths"/> — то есть по
    /// путям ОТНОСИТЕЛЬНО <see cref="MacroBundleFormat.TemplateFolder"/>. Записи, которые
    /// шаблоном не являются (не PNG, глубже одного уровня), отбрасываются
    /// <see cref="MacroBundleFormat.TryParseTemplatePath"/>.
    /// </summary>
    public static MacroTemplateInventory FromPaths(IEnumerable<string> templatePaths)
    {
        ArgumentNullException.ThrowIfNull(templatePaths);

        var singles = new HashSet<string>(StringComparer.Ordinal);
        var sets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in templatePaths)
        {
            if (!MacroBundleFormat.TryParseTemplatePath(path, out var set, out var name))
            {
                continue;
            }

            if (set is null)
            {
                singles.Add(name);
            }
            else
            {
                sets.Add(set);
            }
        }

        return new MacroTemplateInventory(singles, sets);
    }

    /// <summary><c>true</c>, когда имя разрешается в этом бандле.</summary>
    /// <param name="name">Имя ровно в том виде, в каком его несёт нода.</param>
    /// <param name="isSet"><c>true</c> — спрашивают про НАБОР (<c>MatchTemplateSet</c>), иначе про одиночный файл.</param>
    public bool Has(string name, bool isSet) =>
        !string.IsNullOrEmpty(name) && (isSet ? _sets.Contains(name) : _singles.Contains(name));
}
