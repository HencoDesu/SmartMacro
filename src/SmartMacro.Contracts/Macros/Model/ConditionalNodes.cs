using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

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

    /// <summary>Если задано и шаблон найден, центр совпадения записывается в эту переменную прогона (как точка).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Ребро, по которому идут, когда шаблон найден; <c>null</c> = конец прогона.</summary>
    public string? Found { get; init; }

    /// <summary>Ребро, по которому идут, когда шаблон не найден; <c>null</c> = конец прогона.</summary>
    public string? NotFound { get; init; }
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

    /// <summary>Если задано и шаблон появился, центр совпадения записывается в эту переменную прогона (как точка).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Ребро, по которому идут, когда шаблон появился вовремя; <c>null</c> = конец прогона.</summary>
    public string? Found { get; init; }

    /// <summary>Ребро, по которому идут, когда ожидание истекло; <c>null</c> = конец прогона.</summary>
    public string? Timeout { get; init; }
}

/// <summary>
/// Сопоставляет НАБОР шаблонов с областью контекстного окна; побеждает лучшее совпадение выше
/// порога. Имя победившего шаблона записывается в <see cref="ResultVar"/> и (при
/// <see cref="ApplyTag"/>) вешается на окно как тег. Исход выбирает ребро:
/// <see cref="Matched"/> / <see cref="NotMatched"/>.
/// </summary>
public sealed record RecognizeTagNode : MacroNode
{
    /// <summary>Имя набора шаблонов (разрешается в <c>templates/{set}/*.png</c>; имя файла = тег).</summary>
    public required string TemplateSet { get; init; }

    /// <summary>Область в координатах клиентской области, с которой сопоставляют набор.</summary>
    public required ScreenRect Region { get; init; }

    /// <summary><c>true</c> (по умолчанию) = повесить на контекстное окно тег с именем победившего шаблона.</summary>
    public bool ApplyTag { get; init; } = true;

    /// <summary>Переменная прогона, куда пишется имя победившего шаблона. По умолчанию <c>"tag"</c>.</summary>
    public string ResultVar { get; init; } = "tag";

    /// <summary>Ребро, по которому идут, когда шаблон совпал; <c>null</c> = конец прогона.</summary>
    public string? Matched { get; init; }

    /// <summary>Ребро, по которому идут, когда выше порога не совпало ничего; <c>null</c> = конец прогона.</summary>
    public string? NotMatched { get; init; }
}
