using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Шов между обходчиком графа и реальным миром: синтез ввода, зрение и косметика окна.
/// Исполнитель написан целиком против этого интерфейса, чтобы walker проверялся модульными
/// тестами на подделках, а будущий слой ScriptNode/Lua привязывался к тем же операциям. В W0.2b
/// он реализован поверх IGameWindow / AgentInputDispatcher / зрения. Операций с тегами здесь
/// намеренно НЕТ — они идут напрямую через <c>WindowRegistry</c> (единственного владельца
/// состояния тегов).
/// </summary>
public interface IMacroPrimitives
{
    /// <summary>Нажимает <paramref name="key"/> в окне.</summary>
    Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct);

    /// <summary>Кликает левой кнопкой в точку <paramref name="point"/> в клиентских координатах; <paramref name="doubleClick"/> — для двойного клика.</summary>
    Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct);

    /// <summary>
    /// Одноразовое сопоставление шаблона по окну (с обрезкой по <paramref name="region"/>, если
    /// она задана). Возвращает центр совпадения или <c>null</c>, если ничего не нашлось.
    /// </summary>
    Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, string template, ScreenRect? region, CancellationToken ct);

    /// <summary>
    /// Опрашивает окно, пока шаблон не появится или пока не истечёт <paramref name="timeoutMs"/>.
    /// Возвращает центр совпадения или <c>null</c> по таймауту.
    /// </summary>
    Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, string template, ScreenRect? region, int timeoutMs,
        CancellationToken ct);

    /// <summary>
    /// Сопоставляет набор шаблонов с областью <paramref name="region"/> окна. Возвращает имя
    /// лучшего по совпадению шаблона (оно же тег) или <c>null</c>, если порог никто не взял.
    /// </summary>
    Task<string?> RecognizeAsync(IntPtr hwnd, string templateSet, ScreenRect region, CancellationToken ct);

    /// <summary>Ставит окну иконку из изображения по пути <paramref name="iconPath"/>.</summary>
    Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct);
}
