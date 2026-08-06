using System.Globalization;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;

namespace SmartMacro.Macros.Analysis;

/// <summary>
/// Чем кончилась попытка выделить кусок графа в под-макрос.
/// </summary>
/// <param name="Refusal">
/// Почему извлечение не сделано, по-русски и с именами виноватых нод, — либо <c>null</c> при
/// успехе. Строка показывается пользователю как есть.
/// </param>
/// <param name="Parent">Новый граф родителя: выделение вырезано, на его месте нода вызова.</param>
/// <param name="Submacro">Новый под-макрос.</param>
/// <param name="CallNodeId">Id появившейся ноды вызова — редактор ставит на неё выделение.</param>
public sealed record MacroExtractionResult(
    string? Refusal,
    MacroGraph? Parent = null,
    MacroSubmacro? Submacro = null,
    Guid CallNodeId = default)
{
    /// <summary>Извлечение состоялось.</summary>
    public bool IsOk => Refusal is null;

    internal static MacroExtractionResult No(string refusal) => new(refusal);
}

/// <summary>
/// Выделение куска графа в под-макрос (волна F4) — чистая функция, живёт рядом с валидатором и
/// анализами по той же причине: ей нужна только модель, и проверять её надо саму по себе, потому
/// что именно здесь заводилась бы тихая порча графа.
///
/// ────────────────────────────────────────────────────────────────────────────────────────
/// <b>ЧТО ЗДЕСЬ ТРУДНО.</b> «Выделить кусок в функцию» звучит просто и таковым не является.
/// Под-макрос — это функция с ОДНИМ ВХОДОМ, которая возвращает управление вызывающему. Значит
/// выделенный подграф обязан иметь ровно один вход, а все его выходы — стать одним возвратом.
/// Выделение, у которого этого нет, извлекается НЕ ОДНОЗНАЧНО, и вариантов ровно три: отказать,
/// предложить починку или угадать намерение.
///
/// <b>Выбран отказ с объяснением.</b> Довод не в том, что так проще, а в том, что угадывать тут
/// нечего: два входа в выделение — это две разные функции, слипшиеся в одну, а выходы в разные
/// места — это развилка, которую надо сохранить снаружи. Любая догадка молча переписала бы
/// поведение макроса, а заметил бы это пользователь на живой игре, десятью клиентами. Тот же
/// принцип, по которому исполнитель обрывает прогон на неопределённой переменной вместо
/// подстановки пустой строки (§5.3): явный отказ вместо тихого промаха.
///
/// Отказ обязан быть ЧИНИБЕЛЬНЫМ, поэтому он называет ноды поимённо — «в выделении два входа:
/// click-3, find-7» говорит, что именно надо добавить или убрать из выделения, а «выделение не
/// извлекается» не говорит ничего.
///
/// <b>Три случая, когда извлечение определено однозначно:</b>
/// <list type="number">
///   <item><b>Один вход.</b> Снаружи в выделение (и от стартовой ноды графа) ведут рёбра ровно в
///     ОДНУ его ноду. Все они переезжают на ноду вызова.</item>
///   <item><b>Один выход.</b> Все рёбра, уходящие из выделения наружу, ведут в одну и ту же
///     ноду; она становится <c>Next</c> у ноды вызова. Внутри под-макроса такие рёбра становятся
///     возвратом.</item>
///   <item><b>Ни одного выхода.</b> Все рёбра выделения либо остаются внутри, либо кончают прогон.
///     Нода вызова получает <c>Next = null</c>.</item>
/// </list>
///
/// <b>Случай «часть выходов кончает прогон, часть уходит наружу» — тоже отказ</b>, и это не
/// педантизм. После извлечения возврат один: и «конец прогона», и «идти в T» стали бы одним и
/// тем же — «вернуться к вызывающему», а тот пойдёт в T. То есть ветка, которая раньше
/// ЗАКАНЧИВАЛА прогон, начала бы его продолжать. Молча менять поведение ветки, дорисованной
/// автором специально, нельзя.
///
/// <b>Вход, которого нет вовсе</b> (выделение недостижимо снаружи), разрешается отдельным
/// правилом: если внутри выделения ровно одна нода, в которую никто из выделения не ведёт, она и
/// есть вход. Иначе — отказ, потому что «с чего начинается функция» знает только автор.
/// ────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class MacroExtraction
{
    /// <summary>
    /// Вырезает <paramref name="selection"/> из <paramref name="graph"/> в под-макрос.
    ///
    /// Ничего не мутирует: и родитель, и под-макрос возвращаются новыми объектами, а исходный
    /// граф остаётся нетронутым. Редактор на отказе просто ничего не применяет.
    /// </summary>
    /// <param name="graph">Граф, из которого вырезают.</param>
    /// <param name="selection">Id выделенных нод.</param>
    /// <param name="submacroName">Подпись будущего под-макроса.</param>
    /// <param name="submacroId">Его личность; <c>null</c> — выдать новую.</param>
    public static MacroExtractionResult Extract(
        MacroGraph graph,
        IReadOnlyCollection<Guid> selection,
        string submacroName,
        Guid? submacroId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(submacroName);

        var byId = new Dictionary<Guid, MacroNode>();
        foreach (var node in graph.Nodes)
        {
            byId.TryAdd(node.Id, node);
        }

        var inside = selection.Where(byId.ContainsKey).ToHashSet();
        if (inside.Count == 0)
        {
            return MacroExtractionResult.No(Strings.Extraction_Refused_NothingSelected);
        }

        if (inside.Count == graph.Nodes.Count)
        {
            // Родитель остался бы из одной ноды вызова — то есть макрос, который только зовёт
            // свою же единственную функцию. Это не извлечение, а лишний уровень вложенности.
            return MacroExtractionResult.No(Strings.Extraction_Refused_WholeGraph);
        }

        if (inside.Any(id => byId[id] is RunSubmacroNode))
        {
            // Плоскость (см. MacroSubmacro): под-макрос не зовёт никого.
            return MacroExtractionResult.No(Strings.Extraction_Refused_ContainsCall);
        }

        if (FindEntry(graph, byId, inside) is not { } entry)
        {
            return MacroExtractionResult.No(EntryRefusal(graph, byId, inside));
        }

        if (FindExit(byId, inside) is not { } exit)
        {
            return MacroExtractionResult.No(ExitRefusal(byId, inside));
        }

        // ---- под-макрос: рёбра наружу становятся возвратом -----------------------------
        var submacroNodes = inside
            .Select(id => MacroNodeEdges.Retarget(byId[id], target =>
                target is { } value && inside.Contains(value) ? value : null))
            // Порядок нод графа — это порядок, в котором их заводили; сохраняем исходный, а не
            // порядок обхода множества, иначе подписи в логе поедут без всякой причины.
            .OrderBy(node => graph.Nodes.FindIndex(original => original.Id == node.Id))
            .ToList();

        var submacro = new MacroSubmacro(
            submacroId ?? Guid.NewGuid(),
            new MacroGraph
            {
                Name = submacroName.Trim(),
                // Триггеров у функции не бывает; см. MacroSubmacro.
                Triggers = [],
                StartNodeId = entry,
                Nodes = submacroNodes,
            });

        // ---- родитель: на месте выделения — нода вызова ---------------------------------
        var call = new RunSubmacroNode
        {
            SubmacroId = submacro.Id,
            Await = true,
            // Guid.Empty от FindExit означает «вернуться и закончить прогон».
            Next = exit == Guid.Empty ? null : exit,
            // Встаёт туда же, где стоял вход: чтение графа не должно поехать оттого, что кусок
            // свернули.
            Editor = byId[entry].Editor,
        };

        call = call with
        {
            DisplayName = MacroNodeNames.Generate(
                MacroNodeNames.Prefix(call),
                graph.Nodes.Where(node => !inside.Contains(node.Id)).Select(MacroNodeNames.Display)),
        };

        var parentNodes = new List<MacroNode>();
        foreach (var node in graph.Nodes)
        {
            if (!inside.Contains(node.Id))
            {
                // Всё, что вело во вход выделения, теперь ведёт в вызов.
                parentNodes.Add(MacroNodeEdges.Retarget(node, target => target == entry ? call.Id : target));
                continue;
            }

            // Ноду вызова ставим на место ВХОДА, чтобы её позиция в списке (а значит, и в
            // авто-раскладке при следующем открытии) осталась той же.
            if (node.Id == entry)
            {
                parentNodes.Add(call);
            }
        }

        return new MacroExtractionResult(
            null,
            new MacroGraph
            {
                Name = graph.Name,
                Triggers = graph.Triggers,
                StartNodeId = graph.StartNodeId == entry || inside.Contains(graph.StartNodeId)
                    ? call.Id
                    : graph.StartNodeId,
                Nodes = parentNodes,
            },
            submacro,
            call.Id);
    }

    // ---- вход ---------------------------------------------------------------------------

    private static Guid? FindEntry(MacroGraph graph, Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var entries = EntryCandidates(graph, byId, inside);
        if (entries.Count == 1)
        {
            return entries[0];
        }

        if (entries.Count > 1)
        {
            return null;
        }

        // Снаружи в выделение не ведёт ничего — оно недостижимо (или это осколок, оставшийся
        // после правки). Вход тогда определён, только если внутри есть ровно одна нода, в
        // которую не ведёт и изнутри.
        var heads = inside
            .Where(id => !inside.Any(other => MacroNodeEdges.Outgoing(byId[other])
                .Any(edge => edge.TargetId == id)))
            .ToList();
        return heads.Count == 1 ? heads[0] : null;
    }

    private static List<Guid> EntryCandidates(MacroGraph graph, Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var entries = new List<Guid>();
        if (inside.Contains(graph.StartNodeId))
        {
            entries.Add(graph.StartNodeId);
        }

        foreach (var (id, node) in byId)
        {
            if (inside.Contains(id))
            {
                continue;
            }

            foreach (var (_, target) in MacroNodeEdges.Outgoing(node))
            {
                if (target is { } value && inside.Contains(value) && !entries.Contains(value))
                {
                    entries.Add(value);
                }
            }
        }

        return entries;
    }

    private static string EntryRefusal(MacroGraph graph, Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var entries = EntryCandidates(graph, byId, inside);
        if (entries.Count > 1)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                Strings.Extraction_Refused_ManyEntries,
                entries.Count,
                Names(byId, entries));
        }

        var heads = inside
            .Where(id => !inside.Any(other => MacroNodeEdges.Outgoing(byId[other])
                .Any(edge => edge.TargetId == id)))
            .ToList();

        return heads.Count == 0
            ? Strings.Extraction_Refused_NoEntryClosedLoop
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Extraction_Refused_NoEntryFromOutside,
                Names(byId, heads));
    }

    // ---- выход --------------------------------------------------------------------------

    // Guid.Empty означает «вернуться и закончить прогон» (то есть Next = null у ноды вызова);
    // null означает «выходы не сходятся, извлечение не определено».
    private static Guid? FindExit(Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var (outside, endsRun) = Exits(byId, inside);
        return (outside.Count, endsRun) switch
        {
            (0, _) => Guid.Empty,
            (1, false) => outside[0],
            _ => null,
        };
    }

    private static (List<Guid> Outside, bool EndsRun) Exits(Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var outside = new List<Guid>();
        var endsRun = false;

        foreach (var id in inside)
        {
            foreach (var (_, target) in MacroNodeEdges.Outgoing(byId[id]))
            {
                if (target is not { } value)
                {
                    endsRun = true;
                }
                else if (!inside.Contains(value) && !outside.Contains(value))
                {
                    outside.Add(value);
                }
            }
        }

        return (outside, endsRun);
    }

    private static string ExitRefusal(Dictionary<Guid, MacroNode> byId, HashSet<Guid> inside)
    {
        var (outside, _) = Exits(byId, inside);

        // Сюда попадают ровно два случая (остальные извлекаются): выходов больше одного либо
        // выход один, но рядом с ним есть ветка, кончающая прогон.
        return string.Format(
            CultureInfo.CurrentCulture,
            outside.Count > 1
                ? Strings.Extraction_Refused_ManyExits
                : Strings.Extraction_Refused_MixedExits,
            Names(byId, outside));
    }

    private static string Names(Dictionary<Guid, MacroNode> byId, IEnumerable<Guid> ids) =>
        string.Join(", ", ids.Select(id => byId.TryGetValue(id, out var node)
            ? $"«{MacroNodeNames.Display(node)}»"
            : "«?»"));

    /// <summary>
    /// Свободная подпись для нового под-макроса: <c>функция-1</c>, <c>функция-2</c>, … Правило то
    /// же, что у подписи ноды, и той же реализацией — номер сквозной и берётся наименьший
    /// свободный.
    /// </summary>
    public static string FreeName(IEnumerable<string> existing) =>
        MacroNodeNames.Generate(Strings.Extraction_DefaultSubmacroNamePrefix, existing);
}
