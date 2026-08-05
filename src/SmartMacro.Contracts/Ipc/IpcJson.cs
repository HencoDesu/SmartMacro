using System.Text.Json;
using SmartMacro.Macros.Model;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// ЕДИНСТВЕННЫЙ сериализатор для всего, что пересекает управляющий канал, — конвертов и нагрузок.
///
/// Настройки по-прежнему сняты копией с <see cref="MacroGraphJson.Options"/>, но обоснование у
/// этого с волны F3 стало скромнее. <b>Раньше по трубе ездил ГРАФ</b> (<c>SaveMacro</c>,
/// <c>GetMacros</c>), и второй, собранный вручную объект настроек расходился бы с файловым
/// диалектом ровно до того дня, когда граф, прекрасно сохраняющийся на диск, перестал бы пролезать
/// по проводу. Макрос по трубе больше не ходит вовсе: библиотекой владеет панель и правит её
/// файлами. Копия осталась потому, что нужное здесь — строковые перечисления и разрешение на
/// метаданные не по порядку — уже настроено там, а два набора одних и тех же правил стоят дороже,
/// чем один. Единственное намеренное отличие — <see cref="JsonSerializerOptions.WriteIndented"/>:
/// транспорт у нас JSON Lines, поэтому в сообщении не должно быть перевода строки.
/// </summary>
public static class IpcJson
{
    /// <summary>
    /// Настройки, которыми пользуется любая (де)сериализация IPC. Только для чтения — если
    /// нужен вариант, снимите копию.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Сериализует объект нагрузки в <see cref="JsonElement"/>, который несёт конверт.
    /// </summary>
    public static JsonElement Write<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// Материализует нагрузку конверта в её типизированную форму. Отсутствующая нагрузка
    /// (элемент <c>null</c>) или JSON-<c>null</c> дают <c>default</c>: если сообщение без
    /// аргументов читают как типизированную нагрузку, это ошибка вызывающего, и обнаружить её
    /// ему, а не получить здесь исключение.
    /// </summary>
    /// <exception cref="JsonException">Нагрузка есть, но под <typeparamref name="T"/> не подходит.</exception>
    public static T? Read<T>(JsonElement? payload) =>
        payload is not { } element || element.ValueKind == JsonValueKind.Null
            ? default
            : element.Deserialize<T>(Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(MacroGraphJson.Options)
        {
            // JSON Lines: одно сообщение в строке, так что никакого форматирования отступами.
            WriteIndented = false,
        };
        // populateMissingResolver: копия не наследует TypeInfoResolver (у MacroGraphJson он
        // заполняется лениво при первом использовании), а MakeReadOnly() отказывается
        // замораживать настройки без него. Этот флаг ставит резолвер по умолчанию, на
        // рефлексии, — тот самый, который мы получили бы неявно и так.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
