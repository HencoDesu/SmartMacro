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
/// <see cref="ProcessProfileSettings.InputMethod"/> отсутствие значения — рабочая семантика
/// («взять из умолчания»), и человек, который откроет файл, должен видеть, что поле есть и оно
/// пустое, а не гадать, потеряно оно или так задумано.
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
    public static AppSettings Deserialize(string json) => Deserialize(json, out _);

    /// <summary>
    /// То же самое, но сообщает, сколько скобок пробуждения собрано из СТАРОЙ формы файла, —
    /// чтобы хранилище могло сказать об этом в журнал. Молчаливая миграция здесь недопустима: она
    /// меняет смысл файла, который пользователь правил руками.
    /// </summary>
    /// <param name="json">Содержимое файла настроек.</param>
    /// <param name="adoptedHooks">Сколько записей приехало из старых полей профилей.</param>
    /// <exception cref="JsonException">JSON не разбирается или это литеральный <c>null</c>.</exception>
    public static AppSettings Deserialize(string json, out int adoptedHooks)
    {
        ArgumentNullException.ThrowIfNull(json);
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonException(Strings.Settings_Engine_Json_NotParsed, ex);
        }

        if (settings is null)
        {
            throw new JsonException(Strings.Settings_Engine_Json_LiteralNull);
        }

        adoptedHooks = 0;
        // Второй разбор — только когда хуков не оказалось: у мигрированного (то есть любого
        // записанного нами) файла блок есть, и лишнего прохода по документу он не стоит.
        if (settings.Hooks.Count > 0 || LegacyHooks(json) is not { Count: > 0 } adopted)
        {
            return settings;
        }

        adoptedHooks = adopted.Count;
        return settings with { Hooks = adopted };
    }

    /// <summary>
    /// ОДНОРАЗОВОЕ ЧТЕНИЕ СТАРОЙ ФОРМЫ: до появления блока <c>Hooks</c> сигнал побудки и обе паузы
    /// лежали полями профиля.
    ///
    /// <b>Зачем это вообще есть, при отменённой обратной совместимости.</b> Сами по себе эти три
    /// поля стали бы после смены схемы неизвестными членами, а неизвестные члены
    /// <c>System.Text.Json</c> молча пропускает — то есть у всех, кто уже пользуется программой,
    /// скобка пробуждения тихо исчезла бы при первом же сохранении из панели. Симптом был бы
    /// «макросы перестали работать», причина — невидимая, а вернуть число неоткуда: в интерфейсе
    /// его нет и не будет. Это ровно та потеря чужой работы, из-за которой
    /// <c>SettingsStore</c> не переписывает даже нечитаемый файл.
    ///
    /// <b>Читается ТОЛЬКО когда члена <c>Hooks</c> нет вовсе.</b> Пустой <c>"Hooks": {}</c> — это
    /// осознанное «скобок нет», и перебивать его старыми полями значило бы отменять решение
    /// пользователя. Различие «нет ключа ≠ ключ пуст» здесь такое же несущее, как у
    /// <see cref="ProcessHookSettings"/> и у описи шаблонов в валидаторе графов.
    ///
    /// Файл при этом НЕ переписывается: миграция живёт в памяти до первого сохранения из панели.
    /// </summary>
    private static IReadOnlyDictionary<string, ProcessHookSettings>? LegacyHooks(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Сюда мы попадаем только после успешного Deserialize, так что это недостижимо; но
            // молча уронить чтение настроек из-за миграции было бы худшим из возможных исходов.
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || Member(document.RootElement, "Hooks") is not null
                || Member(document.RootElement, "Profiles") is not { ValueKind: JsonValueKind.Array } profiles)
            {
                return null;
            }

            var hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object
                    || Member(profile, "ProcessName") is not { ValueKind: JsonValueKind.String } name
                    || name.GetString() is not { Length: > 0 } processName
                    || Member(profile, "ActivationLParam") is not { ValueKind: JsonValueKind.Number } lParam
                    || !lParam.TryGetUInt32(out var signal))
                {
                    // Профиль без сигнала — это и был «обычный процесс»; хука ему не полагается.
                    continue;
                }

                hooks[processName] = new ProcessHookSettings
                {
                    ActivationLParam = signal,
                    SettleMs = Number(profile, "SettleDelayMs"),
                    DeactivateMs = Number(profile, "DeactivationDelayMs"),
                    // Старая скобка срабатывала и на вводе, и на захвате, и жила ровно одно
                    // действие. Умолчания повторяют это дословно — миграция не имеет права
                    // поменять поведение заодно.
                    On = [HookOn.Input, HookOn.Capture],
                    Scope = HookLifetime.Action,
                };
            }

            return hooks;
        }
    }

    // Поиск члена без учёта регистра: Options разрешают писать имена как угодно, и миграция
    // обязана видеть файл ровно так же, как его увидел десериализатор.
    private static JsonElement? Member(JsonElement owner, string name)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static int Number(JsonElement owner, string name) =>
        Member(owner, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : 0;
}
