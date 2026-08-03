using SmartMacro.Macros.Model;

// Пространство имён намеренно не совпадает с путём файла: модель, валидатор и разборы носят имена
// SmartMacro.Macros.*, доставшиеся им ещё от жизни в Core, и стадия 1 сохранила их при переезде,
// чтобы переезд не оказался заодно и переименованием половины usings в обоих процессах. Сосед
// MacroVariableAnalysis лежит здесь же и на тех же условиях.
// ReSharper disable once CheckNamespace
namespace SmartMacro.Macros.Analysis;

/// <summary>Поле ноды, в котором названо имя шаблона. Символьное — формулировка принадлежит панели.</summary>
public enum TemplateSlot
{
    /// <summary>Одиночный шаблон: <c>FindElementNode.Template</c>.</summary>
    FindTemplate,

    /// <summary>Одиночный шаблон: <c>WaitForElementNode.Template</c>.</summary>
    WaitTemplate,

    /// <summary>Набор шаблонов: <c>RecognizeTagNode.TemplateSet</c>.</summary>
    RecognizeSet,
}

/// <summary>Одно место, где макрос называет шаблон.</summary>
/// <param name="MacroName">Граф, в котором это написано.</param>
/// <param name="NodeId">Нода внутри него.</param>
/// <param name="NodeName">Её подпись — то, что браузер шаблонов печатает в списке «кому нужен».</param>
/// <param name="Slot">Какое именно поле этой ноды.</param>
public sealed record TemplateReference(string MacroName, Guid NodeId, string NodeName, TemplateSlot Slot);

/// <summary>
/// Всё, что библиотека макросов говорит про одно имя шаблона.
/// </summary>
/// <param name="Name">Имя ровно в том виде, в каком его несёт нода. Регистр важен.</param>
/// <param name="IsSet">
/// <c>true</c> — это имя НАБОРА (подпапки), названное <c>RecognizeTag</c>; <c>false</c> — имя
/// одиночного файла, названное <c>Find</c>/<c>Wait</c>. Ключ разбора — пара «имя + вид», потому
/// что папка <c>classes</c> и файл <c>classes.png</c> суть разные вещи и обе законны.
/// </param>
/// <param name="References">Все места, где на него ссылаются, в порядке обхода библиотеки.</param>
public sealed record TemplateUsage(string Name, bool IsSet, IReadOnlyList<TemplateReference> References)
{
    /// <summary>Сколько РАЗНЫХ макросов на него ссылаются — то число, что показывает браузер.</summary>
    public int MacroCount => References.Select(r => r.MacroName).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>
/// Статический разбор «какой макрос какой шаблон называет» по библиотеке графов — вторая
/// половина браузера шаблонов (первую, «какие файлы лежат в папке», сообщает демон).
///
/// <b>Чистая функция и намеренно в Contracts</b>, ровно как <see cref="MacroVariableAnalysis"/>
/// рядом: нужна только модель, ни файлового ввода-вывода, ни реестра. Поэтому вопрос «кому нужен
/// этот шаблон» панель отвечает сама, по библиотеке, которая у неё уже есть, — нового типа
/// сообщения он не требует. Это тот же приём, что у бейджа целей (D4), и с тем же главным
/// правилом: реализация правила ОДНА, второй копии заводить нельзя.
///
/// <b>Имена шаблонов НЕ интерполируются.</b> В отличие от тега, пути иконки и имени
/// вызываемого макроса, <c>Template</c> и <c>TemplateSet</c> уезжают в примитивы такой строкой,
/// какая записана в ноде, — исполнитель не прогоняет их через
/// <see cref="MacroVariableNames.Placeholder"/>. Поэтому <c>{tag}</c> в имени шаблона здесь
/// считается частью имени, а не чтением переменной: разбор, который сообщал бы иначе, врал бы
/// про то, что делает движок.
/// </summary>
public static class MacroTemplateAnalysis
{
    /// <summary>
    /// Разбирает библиотеку целиком.
    /// </summary>
    /// <param name="macros">Библиотека графов.</param>
    /// <returns>
    /// По записи на «имя + вид», сперва наборы, затем одиночные, внутри — по имени. Пустое или
    /// пробельное имя не попадает никуда: это не ссылка на шаблон, а незаполненное поле, и
    /// сказать про него должен валидатор, а не браузер.
    /// </returns>
    public static IReadOnlyList<TemplateUsage> Analyze(IEnumerable<MacroGraph> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var found = new Dictionary<(string Name, bool IsSet), List<TemplateReference>>();

        foreach (var macro in macros)
        {
            foreach (var node in macro.Nodes)
            {
                switch (node)
                {
                    case FindElementNode n:
                        Add(found, n.Template, isSet: false, macro.Name, node, TemplateSlot.FindTemplate);
                        break;

                    case WaitForElementNode n:
                        Add(found, n.Template, isSet: false, macro.Name, node, TemplateSlot.WaitTemplate);
                        break;

                    case RecognizeTagNode n:
                        Add(found, n.TemplateSet, isSet: true, macro.Name, node, TemplateSlot.RecognizeSet);
                        break;

                    default:
                        // Остальные ноды шаблонов не касаются. НОВАЯ нода с картинкой попадёт
                        // сюда молча — единственное, о чём стоит помнить, добавляя такую.
                        break;
                }
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
        string macroName,
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

        references.Add(new TemplateReference(macroName, node.Id, MacroNodeNames.Display(node), slot));
    }
}
