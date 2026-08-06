using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Analysis;

/// <summary>Поле ноды, в котором названо имя шаблона. Символьное — формулировка принадлежит панели.</summary>
public enum TemplateSlot
{
    /// <summary>Одиночный шаблон: <c>FindElementNode.Template</c>.</summary>
    FindTemplate,

    /// <summary>Одиночный шаблон: <c>WaitForElementNode.Template</c>.</summary>
    WaitTemplate,

    /// <summary>Набор шаблонов: <c>MatchTemplateSetNode.TemplateSet</c>.</summary>
    MatchSet,
}

/// <summary>Одно место в графе, где макрос называет шаблон.</summary>
/// <param name="NodeId">Нода — по нему её находят и подсвечивают.</param>
/// <param name="NodeName">Её подпись — по ней её печатают. Ни того, ни другого поодиночке не хватает.</param>
/// <param name="Slot">Какое именно поле этой ноды.</param>
public sealed record TemplateReference(Guid NodeId, string NodeName, TemplateSlot Slot);

/// <summary>
/// Всё, что ОДИН граф говорит про одно имя шаблона.
/// </summary>
/// <param name="Name">Имя ровно в том виде, в каком его несёт нода. Регистр важен.</param>
/// <param name="IsSet">
/// <c>true</c> — это имя НАБОРА (подпапки), названное <c>MatchTemplateSet</c>; <c>false</c> — имя
/// одиночного файла, названное <c>Find</c>/<c>Wait</c>. Ключ разбора — пара «имя + вид», потому
/// что папка <c>classes</c> и файл <c>classes.png</c> суть разные вещи и обе законны.
/// </param>
/// <param name="References">Все ноды, которые на него ссылаются, в порядке обхода графа.</param>
public sealed record TemplateUsage(string Name, bool IsSet, IReadOnlyList<TemplateReference> References);

/// <summary>
/// Статический разбор «какие шаблоны называет этот граф».
///
/// <b>Раньше разбор шёл по всей БИБЛИОТЕКЕ</b> и отвечал на вопрос «каким макросам нужен этот
/// шаблон» — вопрос браузера шаблонов, когда дерево <c>templates/</c> было общим. С волны F2
/// общего дерева нет: шаблон лежит внутри бандла и принадлежит ровно одному макросу, так что
/// вопрос перестал существовать вместе с ответом. Разбор остался, но сузился до одного графа, и
/// потребителей у него теперь двое:
/// <list type="bullet">
///   <item><b>валидатор</b> — сверяет эти имена с описью бандла
///     (<see cref="Bundle.MacroTemplateInventory"/>) и говорит «нода называет шаблон, которого в
///     бандле нет» ДО запуска, а не строчкой в журнале посреди прогона;</item>
///   <item><b>браузер шаблонов внутри редактора</b> — показывает у каждого файла, какие ноды его
///     называют, и приглушает те, что не называет никто.</item>
/// </list>
/// Реализация правила ОДНА, второй копии заводить нельзя, — то же требование, что у бейджа целей
/// (D4): диагностика, расходящаяся с тем, что делает исполнитель, хуже отсутствующей.
///
/// <b>Чистая функция и намеренно в Shared</b>, ровно как <see cref="MacroVariableAnalysis"/>
/// рядом: нужна только модель, ни файлового ввода-вывода, ни реестра.
///
/// <b>Имена шаблонов НЕ интерполируются.</b> В отличие от тега и пути иконки,
/// <c>Template</c> и <c>TemplateSet</c> уезжают в примитивы такой строкой, какая записана
/// в ноде, — исполнитель не прогоняет их через <see cref="MacroVariableNames.Placeholder"/>.
/// Поэтому <c>{tag}</c> в имени шаблона здесь считается частью имени, а не чтением переменной:
/// разбор, который сообщал бы иначе, врал бы про то, что делает движок.
/// </summary>
public static class MacroTemplateAnalysis
{
    /// <summary>
    /// Разбирает один граф.
    /// </summary>
    /// <param name="macro">Граф.</param>
    /// <returns>
    /// По записи на «имя + вид», сперва наборы, затем одиночные, внутри — по имени. Пустое или
    /// пробельное имя не попадает никуда: это не ссылка на шаблон, а незаполненное поле, и
    /// сказать про него должна отдельная проверка, а не эта.
    /// </returns>
    public static IReadOnlyList<TemplateUsage> Analyze(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var found = new Dictionary<(string Name, bool IsSet), List<TemplateReference>>();

        foreach (var node in macro.Nodes)
        {
            switch (node)
            {
                case FindElementNode n:
                    Add(found, n.Template, isSet: false, node, TemplateSlot.FindTemplate);
                    break;

                case WaitForElementNode n:
                    Add(found, n.Template, isSet: false, node, TemplateSlot.WaitTemplate);
                    break;

                case MatchTemplateSetNode n:
                    Add(found, n.TemplateSet, isSet: true, node, TemplateSlot.MatchSet);
                    break;

                default:
                    // Остальные ноды шаблонов не касаются. НОВАЯ нода с картинкой попадёт
                    // сюда молча — единственное, о чём стоит помнить, добавляя такую.
                    break;
            }
        }

        return
        [
            .. found
                .OrderByDescending(pair => pair.Key.IsSet)
                .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                .Select(pair => new TemplateUsage(pair.Key.Name, pair.Key.IsSet, pair.Value))
        ];
    }

    private static void Add(
        Dictionary<(string, bool), List<TemplateReference>> found,
        string? name,
        bool isSet,
        MacroNode node,
        TemplateSlot slot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = (name, isSet);
        if (!found.TryGetValue(key, out var references))
        {
            references = [];
            found[key] = references;
        }

        references.Add(new TemplateReference(node.Id, MacroNodeNames.Display(node), slot));
    }
}
