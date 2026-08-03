using SmartMacro.Macros.Model;

// ReSharper disable once CheckNamespace — имена SmartMacro.Macros.* достались разборам от жизни в Core.
namespace SmartMacro.Macros.Analysis;

/// <summary>
/// Поле, в которое переменную пишут или из которого читают. Символьное, никогда не русское, —
/// то же правило, что у <c>RunOutcomes</c>: демон и анализатор называют слот, а формулировка
/// («в пути к иконке») и цвет принадлежат панели.
/// </summary>
public enum VariableSlot
{
    /// <summary>Запись: центр совпадения у <c>FindElement</c> / <c>WaitForElement</c>.</summary>
    FoundPointVar,

    /// <summary>Запись: имя победившего шаблона у <c>RecognizeTag</c>.</summary>
    ResultVar,

    /// <summary>Чтение: <c>ClickNode.PointVar</c> — единственное чтение в модели, идущее не через подстановку.</summary>
    PointVar,

    /// <summary>Чтение: <c>{var}</c> внутри тега у <c>AddTag</c> / <c>RemoveTag</c>.</summary>
    Tag,

    /// <summary>Чтение: <c>{var}</c> внутри пути у <c>SetIcon</c>.</summary>
    IconPath,

    /// <summary>Чтение: <c>{var}</c> внутри имени макроса у <c>RunMacro</c>.</summary>
    MacroName,
}

/// <summary>Что лежит в переменной — насколько об этом можно судить по одному только графу.</summary>
public enum VariableKind
{
    /// <summary>Граф ничего не уточняет: переменную только подставляют в строку, и всё.</summary>
    Unknown,

    /// <summary>Экранная точка — <c>cursor</c>, какой-нибудь <c>FoundPointVar</c> или то, что читает <c>PointVar</c>.</summary>
    Point,

    /// <summary>Строка — тег, который записала <c>RecognizeTag</c>.</summary>
    Text,
}

/// <summary>Одно место, где переменную трогают.</summary>
/// <param name="NodeId">Нода, которая её трогает, — по нему панель подсвечивает коробку на канве.</param>
/// <param name="NodeName">Её подпись — по ней панель называет ноду в карточке переменной.</param>
/// <param name="Slot">Какое именно поле этой ноды.</param>
public sealed record VariableReference(Guid NodeId, string NodeName, VariableSlot Slot);

/// <summary>
/// Всё, что граф говорит об одной переменной: кто её пишет, кто читает, что в ней лежит.
/// </summary>
/// <param name="Name">Имя переменной ровно в том виде, в каком оно написано между фигурными скобками. Регистр важен.</param>
/// <param name="Kind">Выводится из ЗАПИСИ, если она есть, иначе — из чтений.</param>
/// <param name="SeededByTrigger">
/// <c>true</c> только для <see cref="MacroVariableNames.Cursor"/> — единственной переменной,
/// которую не пишет ни одна нода и которая есть в каждом прогоне. Панель показывает её как
/// «пишет: триггер (сид)».
/// </param>
/// <param name="Writes">Ноды, которые ей присваивают, в порядке следования в графе. Для сида от триггера — пусто.</param>
/// <param name="Reads">Ноды, которые её потребляют, в порядке следования в графе.</param>
public sealed record MacroVariableInfo(
    string Name,
    VariableKind Kind,
    bool SeededByTrigger,
    IReadOnlyList<VariableReference> Writes,
    IReadOnlyList<VariableReference> Reads)
{
    /// <summary>
    /// <c>false</c>, когда нода читает переменную, которой ничто и никогда не присваивает
    /// значение, — в рантайме это прерванный прогон, а не подстановка пустой строки
    /// (spec §5.3).
    /// </summary>
    public bool IsDefined => SeededByTrigger || Writes.Count > 0;

    /// <summary><c>false</c> у переменной, которую записали и ни разу не использовали.</summary>
    public bool IsRead => Reads.Count > 0;
}

/// <summary>
/// Статический разбор «кто пишет / кто читает» по графу макроса — данные, на которых стоит
/// панель переменных из макета 1d.
///
/// <b>Чистая функция и намеренно в Contracts.</b> Ей нужна только модель и больше ничего: ни
/// реестра, ни файлового ввода-вывода, ни идущего обхода. Это делает её тестируемой саму по
/// себе (а именно там её ошибки и жили бы) и пригодной для панели, у которой ссылки на Core
/// нет и не будет. Живое ЗНАЧЕНИЕ переменной — отдельная забота, оно приезжает потоком событий
/// прогона как <c>RunEventKind.VariableSet</c>; этот класс знает только форму графа.
///
/// <b>Множество пишущих по замыслу закрыто и крошечно</b> (spec §5.3, §14): триггер засевает
/// <c>cursor</c>, условные ноды пишут <c>FoundPointVar</c> / <c>ResultVar</c>. Никакой
/// <c>SetVariableNode</c> не существует и не появится, поэтому анализ не может протухнуть,
/// прозевав пишущего, которого кто-то добавил позже: новый пишущий был бы новым типом ноды, а
/// тот попадает в switch внутри <see cref="Collect"/> как заметная на компиляции дыра.
/// </summary>
public static class MacroVariableAnalysis
{
    /// <summary>
    /// Разбирает один граф.
    ///
    /// Всегда содержит <see cref="MacroVariableNames.Cursor"/> независимо от того, упоминает
    /// ли его граф: он есть в каждом прогоне, а панель, перечисляющая только упомянутые
    /// переменные, спрятала бы единственное значение, которое всегда доступно, чтобы вставить
    /// его в ноду.
    /// </summary>
    /// <returns>
    /// По записи на переменную. Порядок такой, что сперва идут те, которые нода ПИШЕТ, — в том
    /// порядке, в каком их писатель встречается в <see cref="MacroGraph.Nodes"/>, — а затем
    /// только читаемые. Для типичного графа «опознать и повесить тег» это поднимает наверх
    /// самое интересное, а <c>cursor</c> опускает вниз.
    /// </returns>
    public static IReadOnlyList<MacroVariableInfo> Analyze(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);

        for (var index = 0; index < macro.Nodes.Count; index++)
        {
            Collect(macro.Nodes[index], index, found);
        }

        // Сид от триггера. Добавляем последним, чтобы граф, который его ещё и ЧИТАЕТ, успел
        // записать это чтение (и его порядковый номер): здесь мы дописываем только половинку
        // «пишет триггер».
        var cursor = Get(found, MacroVariableNames.Cursor, order: macro.Nodes.Count);
        cursor.SeededByTrigger = true;
        cursor.Kind = VariableKind.Point;

        return
        [
            .. found.Values
                .OrderBy(entry => entry.Writes.Count > 0 ? 0 : 1)
                .ThenBy(entry => entry.Order)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => new MacroVariableInfo(
                    entry.Name,
                    entry.Kind,
                    entry.SeededByTrigger,
                    entry.Writes,
                    entry.Reads))
        ];
    }

    /// <summary>
    /// Имена переменных, подставляемых в <paramref name="template"/>, по порядку и без
    /// повторов. Использует ТУ ЖЕ регулярку, которой подставляет исполнитель, — см.
    /// <see cref="MacroVariableNames.Placeholder"/>.
    /// </summary>
    public static IReadOnlyList<string> PlaceholdersIn(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return [];
        }

        List<string>? names = null;
        foreach (var match in MacroVariableNames.Placeholder().EnumerateMatches(template))
        {
            // EnumerateMatches групп не отдаёт, поэтому имя — это диапазон минус фигурные скобки.
            var name = template.Substring(match.Index + 1, match.Length - 2);
            names ??= [];
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names ?? (IReadOnlyList<string>)[];
    }

    private static void Collect(MacroNode node, int order, Dictionary<string, Entry> found)
    {
        switch (node)
        {
            // ---- пишущие: закрытый набор из spec §5.3 -----------------------------------

            case FindElementNode n:
                Write(found, n.FoundPointVar, node, VariableSlot.FoundPointVar, VariableKind.Point, order);
                break;

            case WaitForElementNode n:
                Write(found, n.FoundPointVar, node, VariableSlot.FoundPointVar, VariableKind.Point, order);
                break;

            case RecognizeTagNode n:
                Write(found, n.ResultVar, node, VariableSlot.ResultVar, VariableKind.Text, order);
                break;

            // ---- читающие ---------------------------------------------------------------

            case ClickNode n:
                // Единственное чтение, которое называет переменную прямо, а не подставляет её.
                Read(found, n.PointVar, node, VariableSlot.PointVar, VariableKind.Point, order);
                break;

            case AddTagNode n:
                Interpolated(found, n.Tag, node, VariableSlot.Tag, order);
                break;

            case RemoveTagNode n:
                Interpolated(found, n.Tag, node, VariableSlot.Tag, order);
                break;

            case SetIconNode n:
                Interpolated(found, n.IconPath, node, VariableSlot.IconPath, order);
                break;

            case RunMacroNode n:
                Interpolated(found, n.MacroName, node, VariableSlot.MacroName, order);
                break;

            default:
                // KeyPressNode и DelayNode переменных не касаются. НОВЫЙ тип ноды попадёт сюда
                // молча — единственное, о чём стоит помнить, когда каталог разрастётся.
                break;
        }
    }

    private static void Interpolated(
        Dictionary<string, Entry> found,
        string? template,
        MacroNode node,
        VariableSlot slot,
        int order)
    {
        foreach (var name in PlaceholdersIn(template))
        {
            // Подстановка о типе не говорит ничего — строковое представление есть у всего.
            Read(found, name, node, slot, VariableKind.Unknown, order);
        }
    }

    private static void Write(
        Dictionary<string, Entry> found,
        string? name,
        MacroNode node,
        VariableSlot slot,
        VariableKind kind,
        int order)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var entry = Get(found, name, order);
        entry.Writes.Add(Reference(node, slot));
        // Запись о типе говорит достоверно; чтение — только догадывается.
        entry.Kind = kind;
    }

    private static void Read(
        Dictionary<string, Entry> found,
        string? name,
        MacroNode node,
        VariableSlot slot,
        VariableKind kind,
        int order)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var entry = Get(found, name, order);
        entry.Reads.Add(Reference(node, slot));
        if (entry.Kind == VariableKind.Unknown && entry.Writes.Count == 0)
        {
            entry.Kind = kind;
        }
    }

    // Имя снимается ЗДЕСЬ, в момент разбора, а не резолвится панелью позже: разбор гоняют по
    // тому же графу, который панель и показывает, так что вторая карта «id → подпись» была бы
    // лишним местом для рассинхрона.
    private static VariableReference Reference(MacroNode node, VariableSlot slot) =>
        new(node.Id, MacroNodeNames.Display(node), slot);

    private static Entry Get(Dictionary<string, Entry> found, string name, int order)
    {
        if (!found.TryGetValue(name, out var entry))
        {
            entry = new Entry(name, order);
            found[name] = entry;
        }

        return entry;
    }

    // Мутабельно, пока идёт сбор; в конце проецируется в неизменяемую запись.
    private sealed class Entry(string name, int order)
    {
        public string Name { get; } = name;

        /// <summary>Индекс ноды, которая упомянула переменную первой, — он же порядок показа.</summary>
        public int Order { get; } = order;

        public VariableKind Kind { get; set; } = VariableKind.Unknown;

        public bool SeededByTrigger { get; set; }

        public List<VariableReference> Writes { get; } = [];

        public List<VariableReference> Reads { get; } = [];
    }
}
