using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMacro.Resources;

namespace SmartMacro.Contracts.Settings;

/// <summary>
/// ЕДИНСТВЕННЫЙ сериализатор файла настроек. Диалект тот же, что у
/// <c>MacroGraphJson</c>, и по той же причине: файл рядом с exe открывают блокнотом, поэтому
/// отступы, перечисления строками и живая кириллица — не вкусовщина, а условие того, что правка
/// руками остаётся выполнимой.
///
/// В отличие от графов, <c>null</c> здесь НЕ опускается. У
/// <see cref="ProcessProfileSettings.ActivationLParam"/> отсутствие значения — рабочая
/// семантика («обычный процесс, пробуждение пропускается»), и человек, который откроет файл,
/// должен видеть, что поле есть и оно пустое, а не гадать, потеряно оно или так задумано.
/// </summary>
public static class AppSettingsJson
{
    /// <summary>Настройки (де)сериализации файла настроек. Менять их нельзя.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
        // Правка руками: имена свойств в файле пишутся так, как объявлены, но читаются в любом
        // регистре — блокнот прощает то, чего не прощает генератор.
        PropertyNameCaseInsensitive = true,
        // Кириллицы в настройках сегодня нет, но имя процесса — свободная строка, и экранировать
        // её в \uXXXX означало бы сделать файл нечитаемым ровно там, где написано осмысленное.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сериализует настройки в JSON с отступами — ровно то, что ляжет в файл.</summary>
    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, Options);
    }

    /// <summary>
    /// Читает настройки из JSON. Кривой вход и документ из литерального <c>null</c> бросают
    /// <see cref="JsonException"/> — решать, что делать с нечитаемым файлом, хранилищу, а не
    /// сериализатору.
    /// </summary>
    /// <exception cref="JsonException">JSON не разбирается или это литеральный <c>null</c>.</exception>
    public static AppSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonException(Strings_Engine.Settings_Engine_Json_NotParsed, ex);
        }

        return settings ?? throw new JsonException(Strings_Engine.Settings_Engine_Json_LiteralNull);
    }
}
