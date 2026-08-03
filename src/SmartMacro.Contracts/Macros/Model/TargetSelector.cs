namespace SmartMacro.Macros.Model;

/// <summary>
/// Выбирает целевые окна по тегам с семантикой И: окно подходит, когда несёт все теги из
/// <see cref="RequireTags"/> и ни одного из <see cref="ExcludeTags"/>. Оба списка пусты =
/// подходит любое зарегистрированное окно. Теги — свободные строки, регистр важен, сравнение
/// со снимком <c>WindowRegistry</c> порядковое (ordinal).
/// </summary>
public sealed record TargetSelector
{
    /// <summary>Теги, которые окно обязано нести ВСЕ, чтобы подойти. Пусто = требований нет.</summary>
    public List<string> RequireTags { get; init; } = [];

    /// <summary>Теги, любой из которых дисквалифицирует окно. Пусто = исключений нет.</summary>
    public List<string> ExcludeTags { get; init; } = [];

    /// <summary>
    /// ТО САМОЕ правило совпадения — и единственная его реализация (волна D4).
    ///
    /// Живёт здесь, на селекторе, в Contracts, а не в исполнителе, потому что один и тот же
    /// ответ теперь нужен двум процессам. Демон спрашивает его через <c>SelectorEvaluator</c>,
    /// когда нода разветвляется; панель спрашивает напрямую, чтобы нарисовать бейдж
    /// «8 окон · кроме Склад». Бейдж, расходящийся с тем, во что движок на самом деле целится,
    /// был бы хуже отсутствия бейджа, поэтому второй копии этих двух циклов намеренно нет
    /// нигде.
    ///
    /// Пригодным для обеих сторон метод делает именно то, что он принимает ТЕГИ, а не окно: у
    /// Core есть <c>ManagedWindowInfo</c> (тегами там <see cref="IReadOnlySet{T}"/>), у панели —
    /// <c>WindowDto</c> (упорядоченный список), и ни одному из них не нужно знать о другом.
    /// </summary>
    /// <param name="tags">
    /// Теги окна. Сравнение в любом случае порядковое: LINQ-овый <c>Contains</c> уходит в
    /// собственный поиск <see cref="HashSet{T}"/>, если вызывающий передал именно его, а и
    /// <c>HashSet&lt;string&gt;</c> по умолчанию, и <c>List&lt;string&gt;</c> сравнивают
    /// порядково — так что два вызывающих не могут разойтись в трактовке регистра.
    /// </param>
    /// <returns><c>true</c>, если окно с тегами <paramref name="tags"/> является целью.</returns>
    public bool Matches(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in RequireTags)
        {
            if (!tags.Contains(tag))
            {
                return false;
            }
        }
        foreach (var tag in ExcludeTags)
        {
            if (tags.Contains(tag))
            {
                return false;
            }
        }
        return true;
    }
}
