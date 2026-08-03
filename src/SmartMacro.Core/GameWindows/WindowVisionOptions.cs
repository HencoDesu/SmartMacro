namespace SmartMacro.GameWindows;

// Настройки для IGameWindow.FindElementAsync/WaitForElementAsync. Привязываются к
// "Vision:Window" в appsettings.json. Конвейер: полутона → MatchTemplate CCoeffNormed →
// сравнение с MatchThreshold.
//
// Никакого LuminanceThreshold здесь нет (стадия 4B удалила свойство, которое никто не читал):
// этот путь намеренно НЕ бинаризует. Элементы игрового интерфейса лежат на полупрозрачных
// подложках, где просвечивающий снизу мир делает фиксированный порог яркости нестабильным.
// ClassMatcher — исключение, которое бинаризует до сих пор: там сплошной текст на сплошной
// панели характеристик.
public sealed class WindowVisionOptions
{
    // Отсечка по оценке Cv2.MatchTemplate CCoeffNormed. Выше неё = элемент на месте. Шаблоны
    // загрузочного сценария — обычно чёткие кнопки интерфейса и элементы HUD, так что 0.7 —
    // безопасное значение по умолчанию.
    public double MatchThreshold { get; init; } = 0.7;

    // Как часто WaitForElementAsync заново захватывает кадр и заново сопоставляет. 500 мс —
    // золотая середина: запаса хватает на PrintWindow + OpenCV (~50 мс на всё) и при этом
    // процессор не жжётся впустую.
    public int PollIntervalMs { get; init; } = 500;
}
