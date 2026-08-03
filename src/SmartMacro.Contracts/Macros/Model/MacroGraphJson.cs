using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

// ReSharper disable once CheckNamespace — имена SmartMacro.Macros.* достались модели от жизни в Core.
namespace SmartMacro.Macros.Model;

/// <summary>
/// ЕДИНСТВЕННЫЙ сериализатор файлов с графами макросов. Хранилище (W0.2b), тесты и любая
/// оснастка обязаны ходить через этот помощник, чтобы все потребители сходились на одном
/// диалекте JSON: дискриминаторы <c>$type</c>, перечисления строками, вывод с отступами,
/// <c>null</c> опускаются.
/// </summary>
public static class MacroGraphJson
{
    /// <summary>
    /// Настройки, которыми пользуется любая (де)сериализация графа макроса. Выставлены наружу
    /// для тех, кому нужно вложить граф в бо́льшую нагрузку; менять их нельзя.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Правленные руками файлы не должны ломаться просто потому, что "$type" оказался не
        // первым свойством.
        AllowOutOfOrderMetadataProperties = true,
        // Кириллица остаётся кириллицей. По умолчанию STJ экранирует всё за пределами ASCII, и
        // тег «Лучник» уезжал на диск как "Лучник": не порча
        // (читается обратно тем же сериализатором), но файл, который автор правит руками и
        // смотрит диффом, становился нечитаемым ровно в тех местах, где написано что-то
        // осмысленное. Ослабление касается только вывода в файл — HTML-контекста здесь нет ни у
        // одного потребителя.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сериализует граф в JSON с отступами.</summary>
    public static string Serialize(MacroGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, Options);
    }

    /// <summary>
    /// Десериализует граф из JSON. Кривой вход — включая незнакомый дискриминатор <c>$type</c>
    /// и документ из литерального <c>null</c> — бросает <see cref="JsonException"/>.
    /// </summary>
    public static MacroGraph Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MacroGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<MacroGraph>(json, Options);
        }
        catch (NotSupportedException ex)
        {
            // Часть сбоев полиморфизма STJ сообщает как NotSupportedException; нормализуем,
            // чтобы вызывающим приходилось обрабатывать только JsonException.
            throw new JsonException("Macro graph JSON is not deserializable.", ex);
        }

        return graph ?? throw new JsonException("Macro graph JSON is 'null'.");
    }
}
