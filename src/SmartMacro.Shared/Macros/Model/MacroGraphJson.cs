using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Что за незнакомый <c>$type</c> встретился в графе, если он там есть.
    ///
    /// <b>Зовут ТОЛЬКО с пути отказа</b> — после того, как <see cref="Deserialize"/> уже бросил, —
    /// и это несущая часть замысла: разбор здесь второй, а платить за него на успешном чтении
    /// нечем и незачем.
    ///
    /// <b>Зачем вообще.</b> Незнакомый дискриминатор STJ сообщает как <c>JsonException</c>, то
    /// есть неотличимо от «в файле не JSON», и читатель бандла объявлял такой файл ПОВРЕЖДЁННЫМ,
    /// приложив английскую строку каркаса. Ни то ни другое не правда: файл целый, версия формата
    /// своя, не хватает ровно одного — типа ноды. Сказать это можно только назвав тип, и назвать
    /// его надо тем словом, каким он написан в файле, — иначе искать нечего.
    ///
    /// <b>Каталог берётся у самой модели</b> (атрибуты <see cref="JsonDerivedTypeAttribute"/>), а
    /// не переписывается списком: второй список разошёлся бы с первым на первом же новом типе
    /// ноды — ровно так же, как разошлись бы две копии правила отбора целей.
    ///
    /// Разбор идёт по СТРУКТУРЕ (<c>Nodes[]</c> и <c>Triggers[]</c>), а не «любой <c>$type</c> в
    /// документе»: искать пропажу в файле придётся в разных местах, и сказать, в каком именно,
    /// стоит десяти строк.
    /// </summary>
    /// <param name="json">Текст, на котором споткнулся <see cref="Deserialize"/>.</param>
    /// <param name="discriminator">Первый незнакомый <c>$type</c> или <c>null</c>.</param>
    /// <param name="isTrigger"><c>true</c> — он найден среди триггеров, иначе среди нод.</param>
    /// <returns><c>true</c>, когда незнакомый тип найден; <c>false</c> — беда в чём-то другом.</returns>
    public static bool TryFindUnknownType(string json, out string? discriminator, out bool isTrigger)
    {
        discriminator = null;
        isTrigger = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            // Документ не собирается даже как произвольный JSON — значит он и правда повреждён,
            // и вердикт «повреждён» честен.
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (FirstUnknown(document.RootElement, "Nodes", KnownNodeTypes) is { } node)
            {
                discriminator = node;
                return true;
            }

            if (FirstUnknown(document.RootElement, "Triggers", KnownTriggerTypes) is { } trigger)
            {
                discriminator = trigger;
                isTrigger = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>Дискриминаторы <c>$type</c>, объявленные на <see cref="MacroNode"/>.</summary>
    private static readonly HashSet<string> KnownNodeTypes = DiscriminatorsOf<MacroNode>();

    /// <summary>Дискриминаторы <c>$type</c>, объявленные на <see cref="MacroTrigger"/>.</summary>
    private static readonly HashSet<string> KnownTriggerTypes = DiscriminatorsOf<MacroTrigger>();

    private static HashSet<string> DiscriminatorsOf<T>() =>
    [
        .. typeof(T)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.TypeDiscriminator as string)
            .OfType<string>(),
    ];

    private static string? FirstUnknown(JsonElement root, string arrayName, HashSet<string> known)
    {
        // Имя свойства сравнивается без учёта регистра: STJ читает граф так же, и правленный
        // руками файл с «nodes» вместо «Nodes» разбирается им нормально.
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, arrayName, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("$type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() is { } value
                    && !known.Contains(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
