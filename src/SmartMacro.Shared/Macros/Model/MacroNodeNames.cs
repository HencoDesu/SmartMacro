using System.Globalization;

namespace SmartMacro.Macros.Model;

/// <summary>
/// Как ноды называются: короткое имя семейства по типу и правило автогенерации подписи.
///
/// <b>Почему имя генерируется от ТИПА.</b> Читаемость нужна для ЧТЕНИЯ — полоса лога прогона,
/// панель переменных, замечания валидатора, — а не для выбора: рёбра рисуют, а ссылаются по
/// <see cref="MacroNode.Id"/>. Прежняя автогенерация давала <c>n1</c>, <c>n2</c>, <c>n3</c>, и
/// строка лога по умолчанию выглядела как <c>0:01.2 · n7 · ок</c>: осмысленной она становилась
/// только после того, как автор вручную переименует каждую ноду. Имя от типа чинит это без
/// ручной работы.
///
/// Латиница, а не русский, — в отличие от всего, что видит пользователь в интерфейсе. Имя ноды
/// набирают руками в узком поле и читают в моноширинной колонке лога рядом с именами шаблонов и
/// переменных, которые тоже латинские; <c>клик-1</c> в этом ряду выглядел бы чужеродно.
/// </summary>
public static class MacroNodeNames
{
    /// <summary>
    /// Короткое имя семейства ноды — основа автогенерируемой подписи (<c>click</c> → <c>click-1</c>)
    /// и запасной вариант показа, когда подпись не задана.
    /// </summary>
    public static string Prefix(MacroNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node switch
        {
            KeyPressNode => "key",
            ClickNode => "click",
            DelayNode => "delay",
            AddTagNode => "addtag",
            RemoveTagNode => "removetag",
            SetIconNode => "icon",
            RunSubmacroNode => "sub",
            FindElementNode => "find",
            WaitForElementNode => "wait",
            RecognizeTagNode => "recognize",
            // Новый тип ноды, о котором здесь ещё не знают. «node» лучше пустой строки: подпись
            // всё равно уникализируется номером, а сломать показ отсутствие ветки не должно.
            _ => "node",
        };
    }

    /// <summary>
    /// Подпись ноды в том виде, в каком её показывают. Пустое поле подменяется именем
    /// семейства, поэтому ни лог, ни инспектор не могут показать пустоту на месте имени.
    /// </summary>
    public static string Display(MacroNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.IsNullOrWhiteSpace(node.DisplayName) ? Prefix(node) : node.DisplayName;
    }

    /// <summary>
    /// Свежая подпись для ноды, добавляемой в граф: <c>{семейство}-{номер}</c>.
    ///
    /// Номер СКВОЗНОЙ по графу, а не свой у каждого семейства: последовательность
    /// <c>click-1</c>, <c>delay-2</c>, <c>find-3</c> заодно читается как порядок, в котором ноды
    /// заводили, а <c>click-1</c>, <c>delay-1</c>, <c>find-1</c> не сообщает вообще ничего.
    /// Берётся наименьший свободный номер, поэтому удаление ноды возвращает её номер в оборот, а
    /// не оставляет дырку навсегда.
    /// </summary>
    /// <param name="prefix">Имя семейства из <see cref="Prefix"/>.</param>
    /// <param name="existing">Подписи, которые уже заняты в этом графе.</param>
    public static string Generate(string prefix, IEnumerable<string> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(existing);

        var names = existing.ToHashSet(StringComparer.Ordinal);
        var usedNumbers = new HashSet<int>();
        foreach (var name in names)
        {
            if (TrailingNumber(name) is { } number)
            {
                usedNumbers.Add(number);
            }
        }

        for (var candidate = 1;; candidate++)
        {
            if (usedNumbers.Contains(candidate))
            {
                continue;
            }

            var name = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{candidate}");
            // Занят может быть и номер без дефиса, и целиком совпавшая подпись (её могли ввести
            // руками) — тогда просто идём дальше.
            if (names.Add(name))
            {
                return name;
            }
        }
    }

    // «click-12» → 12. Хвост без дефиса или с не-цифрами номером не считается: подпись, которую
    // автор набрал руками, номеров не занимает.
    private static int? TrailingNumber(string name)
    {
        var dash = name.LastIndexOf('-');
        if (dash < 0 || dash == name.Length - 1)
        {
            return null;
        }

        var tail = name.AsSpan(dash + 1);
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
