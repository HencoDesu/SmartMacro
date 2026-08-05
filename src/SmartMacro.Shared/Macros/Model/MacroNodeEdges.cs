namespace SmartMacro.Macros.Model;

/// <summary>
/// Рёбра ноды: перечислить и перенацелить. ОДНО место, где записан состав исходов каждого типа
/// ноды.
///
/// <b>Зачем понадобилось отдельно.</b> Чтение («какие у ноды исходы») жило приватным методом
/// внутри валидатора и там же и осталось бы, если бы волна F4 не завела ВТОРУЮ операцию —
/// перенацеливание. Выделение куска графа в под-макрос переписывает рёбра трижды: наружные
/// исходы выделения превращаются в возврат, входы в выделение переезжают на ноду вызова, и всё
/// это по одному и тому же списку полей. Две копии списка «у Find это Found и NotFound»
/// разошлись бы на первом же новом типе ноды, и разошлись бы молча: валидатор проверял бы одно,
/// извлечение переписывало бы другое.
///
/// Правило то же, что у разбора пути шаблона (<c>MacroBundleFormat.TryParseTemplatePath</c>):
/// правило и его обращение держатся рядом, чтобы их нельзя было поправить порознь.
/// </summary>
public static class MacroNodeEdges
{
    /// <summary>
    /// Исходящие рёбра ноды: имя исхода и его цель (<c>null</c> = конец прогона).
    ///
    /// Имена совпадают с именами полей модели и с именами исходов в <c>RunOutcomes</c>: замечание
    /// валидатора «ребро NotFound ведёт в ноду, которой нет» читается тем же словом, каким это
    /// ребро подписано на канве.
    /// </summary>
    public static IEnumerable<(string EdgeName, Guid? TargetId)> Outgoing(MacroNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node switch
        {
            KeyPressNode n => [("Next", n.Next)],
            ClickNode n => [("Next", n.Next)],
            DelayNode n => [("Next", n.Next)],
            AddTagNode n => [("Next", n.Next)],
            RemoveTagNode n => [("Next", n.Next)],
            SetIconNode n => [("Next", n.Next)],
            RunSubmacroNode n => [("Next", n.Next)],
            FindElementNode n => [("Found", n.Found), ("NotFound", n.NotFound)],
            WaitForElementNode n => [("Found", n.Found), ("Timeout", n.Timeout)],
            RecognizeTagNode n => [("Matched", n.Matched), ("NotMatched", n.NotMatched)],
            _ => [],
        };
    }

    /// <summary>
    /// Копия ноды, у которой каждая цель пропущена через <paramref name="map"/>.
    ///
    /// <paramref name="map"/> получает и <c>null</c> (то есть «конец прогона») — извлечение в
    /// под-макрос обязано различать «ребро в никуда» и «ребро наружу выделения», а вернуть на
    /// его место <c>null</c> имеет право не всякий вызывающий.
    /// </summary>
    public static MacroNode Retarget(MacroNode node, Func<Guid?, Guid?> map)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(map);
        return node switch
        {
            KeyPressNode n => n with { Next = map(n.Next) },
            ClickNode n => n with { Next = map(n.Next) },
            DelayNode n => n with { Next = map(n.Next) },
            AddTagNode n => n with { Next = map(n.Next) },
            RemoveTagNode n => n with { Next = map(n.Next) },
            SetIconNode n => n with { Next = map(n.Next) },
            RunSubmacroNode n => n with { Next = map(n.Next) },
            FindElementNode n => n with { Found = map(n.Found), NotFound = map(n.NotFound) },
            WaitForElementNode n => n with { Found = map(n.Found), Timeout = map(n.Timeout) },
            RecognizeTagNode n => n with { Matched = map(n.Matched), NotMatched = map(n.NotMatched) },
            _ => node,
        };
    }
}
