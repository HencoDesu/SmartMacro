using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Шов между обходчиком графа и реальным миром: синтез ввода, зрение и косметика окна.
/// Исполнитель написан целиком против этого интерфейса, чтобы walker проверялся модульными
/// тестами на подделках, а будущий слой ScriptNode/Lua привязывался к тем же операциям. В W0.2b
/// он реализован поверх IGameWindow / AgentInputDispatcher / зрения. Операций с тегами здесь
/// намеренно НЕТ — они идут напрямую через <c>WindowRegistry</c> (единственного владельца
/// состояния тегов).
///
/// <b>Шаблоны приезжают БАЙТАМИ, а не именами (волна F2).</b> Раньше сюда передавали строку, а
/// разрешал её провайдер общего дерева <c>templates/</c> — один на процесс. С переездом шаблонов
/// внутрь бандла разрешение стало порунным (<see cref="IMacroTemplateSource"/>), и делает его
/// обходчик: имя без макроса больше ничего не значит, а протаскивать источник параметром в каждый
/// вызов означало бы, что слой «ввод, зрение и косметика по hwnd» знает про формат файла макроса.
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
    /// <paramref name="template"/> — байты PNG, уже разрешённые обходчиком из бандла.
    /// <paramref name="matchThreshold"/> — порог этой ноды; <c>null</c> = умолчание слоя зрения.
    /// </summary>
    Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region, double? matchThreshold,
        CancellationToken ct);

    /// <summary>
    /// Опрашивает окно, пока шаблон не появится или пока не истечёт <paramref name="timeoutMs"/>.
    /// Возвращает центр совпадения или <c>null</c> по таймауту.
    /// <paramref name="matchThreshold"/> — порог этой ноды; <c>null</c> = умолчание слоя зрения.
    /// </summary>
    Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region, int timeoutMs,
        double? matchThreshold, CancellationToken ct);

    /// <summary>
    /// Сопоставляет набор шаблонов с областью <paramref name="region"/> окна. Возвращает имя
    /// лучшего по совпадению шаблона (оно же тег) или <c>null</c>, если порог никто не взял.
    /// <paramref name="templates"/> — «тег → байты PNG», один набор из бандла макроса.
    /// <paramref name="matchThreshold"/> — порог этой ноды; <c>null</c> = умолчание сопоставителя.
    /// </summary>
    Task<string?> RecognizeAsync(IntPtr hwnd, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region,
        double? matchThreshold, CancellationToken ct);

    /// <summary>Ставит окну иконку из изображения по пути <paramref name="iconPath"/>.</summary>
    Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct);
}
