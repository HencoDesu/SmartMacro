using SmartMacro.Native;

namespace SmartMacro.Vision;

// Выбирает лучший по совпадению шаблон из НАБОРА внутри области захвата окна, которую задал
// вызывающий. За ним стоит RecognizeTagNode: ключ победившего шаблона становится тегом.
//
// Ключи шаблонов — свободные строки тегов (основы имён файлов набора на диске). Канонический
// сценарий в PW — игровое окно характеристик: открыть его (по умолчанию клавиша C), захватить
// кадр и сопоставить область значения «Класс: <имя>» с заранее отрисованными PNG имён классов.
public interface IClassMatcher
{
    /// <summary>
    /// Сопоставляет область <paramref name="region"/> скриншота с переданными шаблонами.
    /// </summary>
    /// <param name="screenshot">Полный захват клиентской области окна.</param>
    /// <param name="templates">Отображение «тег → байты PNG шаблона», например один набор шаблонов из <c>TemplateSetProvider</c>.</param>
    /// <param name="region">Обрезка в клиентских координатах, по которой сопоставляется набор. Пустая (Width или Height ≤ 0) = весь захват.</param>
    /// <param name="matchThreshold">Порог, заданный нодой; <c>null</c> = настроенное умолчание сопоставителя.</param>
    /// <returns>Подошедший тег с оценкой выше порога или <c>null</c>, если совпадения нет.</returns>
    TagMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region,
        double? matchThreshold = null);

    /// <summary>
    /// Диагностика — возвращает бинаризованный вид области со значением класса. Используется в
    /// сценарии проставления меток и дампа отладочных кадров, чтобы пользователь мог глазами
    /// убедиться, что область в appsettings.json настроена верно.
    /// </summary>
    byte[] DebugBinarizeClassRegion(byte[] screenshot);
}

/// <summary>
/// Результат сопоставления — тег победившего шаблона плюс оценка для подгонки порогов и
/// диагностики.
/// </summary>
public sealed record TagMatch(string Tag, double Score);
