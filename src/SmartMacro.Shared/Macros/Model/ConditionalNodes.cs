using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

// ПОРОГ СОВПАДЕНИЯ ПРИНАДЛЕЖИТ НОДЕ, А НЕ КОНФИГУ — про MatchThreshold всех трёх нод ниже.
//
// «Глобальный» порог и раньше был выдумкой: их было два — Vision:Window (0.7) для Find/Wait и
// Vision:ClassMatcher (0.6) для Recognize, — то есть настройка шла по ТИПУ ОПЕРАЦИИ, хотя
// точность нужна разная по МЕСТУ: крупная кнопка на однотонной подложке узнаётся с запасом, а
// короткое слово в окне характеристик — впритык. Подкрутив общий порог под одну ноду, автор
// неминуемо ломал другую. null = взять умолчание слоя зрения, то самое из Vision:*.

/// <summary>
/// Однократный поиск шаблона в контекстном окне. Исход выбирает ребро:
/// <see cref="Found"/>, если шаблон нашёлся, иначе <see cref="NotFound"/>.
/// </summary>
public sealed record FindElementNode : MacroNode
{
    /// <summary>Имя шаблона (слой примитивов разрешает его в <c>templates/{name}.png</c>).</summary>
    public required string Template { get; init; }

    /// <summary>Область поиска в координатах клиентской области; <c>null</c> = всё окно.</summary>
    public ScreenRect? Region { get; init; }

    /// <summary>Порог совпадения в долях единицы, диапазон (0; 1]; <c>null</c> = умолчание слоя зрения.</summary>
    public double? MatchThreshold { get; init; }

    /// <summary>Если задано и шаблон найден, центр совпадения записывается в эту переменную прогона (как точка).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Нода, к которой идут, когда шаблон найден; <c>null</c> = конец прогона.</summary>
    public Guid? Found { get; init; }

    /// <summary>Нода, к которой идут, когда шаблон не найден; <c>null</c> = конец прогона.</summary>
    public Guid? NotFound { get; init; }
}

/// <summary>
/// Опрашивает контекстное окно на предмет шаблона, пока тот не появится или пока не выйдут
/// <see cref="TimeoutMs"/>. Исход выбирает ребро: <see cref="Found"/> / <see cref="Timeout"/>.
/// </summary>
public sealed record WaitForElementNode : MacroNode
{
    /// <summary>Имя шаблона (слой примитивов разрешает его в <c>templates/{name}.png</c>).</summary>
    public required string Template { get; init; }

    /// <summary>Область поиска в координатах клиентской области; <c>null</c> = всё окно.</summary>
    public ScreenRect? Region { get; init; }

    /// <summary>Сколько миллисекунд продолжать опрос, прежде чем сдаться.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>Порог совпадения в долях единицы, диапазон (0; 1]; <c>null</c> = умолчание слоя зрения.</summary>
    public double? MatchThreshold { get; init; }

    /// <summary>Если задано и шаблон появился, центр совпадения записывается в эту переменную прогона (как точка).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Нода, к которой идут, когда шаблон появился вовремя; <c>null</c> = конец прогона.</summary>
    public Guid? Found { get; init; }

    /// <summary>Нода, к которой идут, когда ожидание истекло; <c>null</c> = конец прогона.</summary>
    public Guid? Timeout { get; init; }
}

/// <summary>
/// Сопоставляет НАБОР шаблонов с областью контекстного окна; побеждает лучшее совпадение выше
/// порога. Имя победившего шаблона записывается в <see cref="ResultVar"/>. Исход выбирает ребро:
/// <see cref="Matched"/> / <see cref="NotMatched"/>.
///
/// <b>Нода даёт ЗНАЧЕНИЕ и ничего больше.</b> До переименования она называлась
/// <c>RecognizeTagNode</c> и вторым действием вешала найденное имя тегом на окно — по галочке
/// <c>ApplyTag</c>, взведённой по умолчанию. Сопоставление и разметка окна — разные действия, и
/// нода для второго уже есть: <see cref="AddTagNode"/> с подстановкой <c>{tag}</c>. Пока они были
/// склеены, «сопоставить, но не помечать» требовало снять галочку, о существовании которой надо
/// было знать, а цепочка «сопоставил → повесил тег → поставил иконку» наполовину пряталась внутри
/// одной коробки на канве.
///
/// <b>При промахе переменная НЕ ПИШЕТСЯ</b> — прогон уходит по <see cref="NotMatched"/>, а
/// <see cref="ResultVar"/> остаётся ровно тем, чем был (чаще всего — неопределённым). На это
/// опирается статический разбор «кто пишет, кто читает»: он числит ноду писателем, но писателем
/// УСЛОВНЫМ, и ветка <see cref="NotMatched"/>, ведущая к чтению <c>{tag}</c>, оборвёт прогон на
/// чтении неопределённой переменной. Обратное поведение (писать пустую строку) выглядело бы
/// добрее и стоило бы тега с пустым именем на окне.
/// </summary>
public sealed record MatchTemplateSetNode : MacroNode
{
    /// <summary>Имя набора шаблонов (разрешается в <c>templates/{set}/*.png</c>; имя файла = значение).</summary>
    public required string TemplateSet { get; init; }

    /// <summary>Область в координатах клиентской области, с которой сопоставляют набор.</summary>
    public required ScreenRect Region { get; init; }

    /// <summary>Порог совпадения в долях единицы, диапазон (0; 1]; <c>null</c> = умолчание слоя зрения.</summary>
    public double? MatchThreshold { get; init; }

    /// <summary>
    /// Переменная прогона, куда пишется имя победившего шаблона. По умолчанию <c>"tag"</c>.
    ///
    /// <b>Обязательна</b> (пустое значение — ошибка валидации): ничего, кроме этой записи, нода
    /// наблюдаемого не производит, и без имени переменной она сводится к развилке
    /// «совпало / не совпало» с выброшенным результатом.
    /// </summary>
    public string ResultVar { get; init; } = "tag";

    /// <summary>Нода, к которой идут, когда шаблон совпал; <c>null</c> = конец прогона.</summary>
    public Guid? Matched { get; init; }

    /// <summary>Нода, к которой идут, когда выше порога не совпало ничего; <c>null</c> = конец прогона.</summary>
    public Guid? NotMatched { get; init; }
}
