using System.Globalization;
using SmartMacro.Resources;

namespace SmartMacro.Contracts.Settings;

/// <summary>Одна причина, по которой настройки не приняты.</summary>
/// <param name="Field">
/// Путь к полю (<c>Watch.ProcessPollIntervalSeconds</c>, <c>Profiles[1].ProcessName</c>) либо
/// <c>null</c> для проблем уровня всего документа. Панель по нему подсвечивает виноватую строку.
/// </param>
/// <param name="Message">Человекочитаемое описание.</param>
public sealed record SettingsIssue(string? Field, string Message);

/// <summary>
/// Чистая проверка настроек — сосед валидатора графов и работает по тем же правилам.
///
/// <b>Здесь только ОШИБКИ.</b> Договорённость такая же, как у <c>SaveMacro</c>: пустой список
/// означает «принято», непустой — «не записано, вот почему». Предупреждений валидатор не
/// выпускает намеренно: поле, из-за которого сохранение всё равно состоится, — это подсказка
/// рядом с полем, а не строка в списке отказов, и смешивать их значило бы приучить читать список
/// как необязательный.
///
/// Гоняется НА ОБОИХ КОНЦАХ: панель показывает отказ, не отправляя запрос, демон отвергает
/// запрос независимо от панели. Одна реализация, а не две, — иначе интерфейс однажды разрешил бы
/// то, что движок не принимает, и наоборот.
/// </summary>
public static class AppSettingsValidator
{
    /// <summary>Границы интервала опроса процессов, секунды.</summary>
    public const int MinPollSeconds = 1;

    /// <summary>Верхняя граница интервалов опроса. Минута — уже «не заметит запуск клиента вовремя».</summary>
    public const int MaxPollSeconds = 60;

    /// <summary>Нижняя граница порога совпадения. Ниже — шаблон совпадает с чем угодно.</summary>
    public const double MinMatchThreshold = 0.05;

    /// <summary>Нижняя граница интервала опроса машинного зрения: один тик стоит 25–50 мс.</summary>
    public const int MinVisionPollMs = 50;

    /// <summary>Верхняя граница интервала опроса машинного зрения.</summary>
    public const int MaxVisionPollMs = 10_000;

    /// <summary>
    /// Верхняя граница пауз побудки и деактивации — теперь у хука процесса.
    ///
    /// Рядом жил <c>RecommendedMinSettleMs</c> = 150 («ниже этого PW начинает терять ввод»): он
    /// существовал ровно затем, чтобы не разъехаться с подсказкой у поля «ОСЕДАНИЕ». Поля на
    /// экране больше нет, подсказки тоже — и константа осталась бы кодом без читателя. Само число
    /// никуда не делось: оно записано у <see cref="ProcessHookSettings.SettleMs"/>, там, где
    /// теперь живёт и сама пауза.
    /// </summary>
    public const int MaxDelayMs = 10_000;

    /// <summary>Проверяет настройки целиком.</summary>
    /// <param name="settings">Проверяемый снимок.</param>
    /// <returns>Пустой список = принято.</returns>
    public static IReadOnlyList<SettingsIssue> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsIssue>();

        Range(issues, "Watch.ProcessPollIntervalSeconds", Strings.Settings_Engine_Field_ProcessPollInterval,
            settings.Watch.ProcessPollIntervalSeconds, MinPollSeconds, MaxPollSeconds,
            Strings.Settings_Engine_Unit_Seconds);
        Range(issues, "Watch.WindowPollIntervalSeconds", Strings.Settings_Engine_Field_WindowPollInterval,
            settings.Watch.WindowPollIntervalSeconds, MinPollSeconds, MaxPollSeconds,
            Strings.Settings_Engine_Unit_Seconds);

        Threshold(issues, "Vision.MatchThreshold", Strings.Settings_Engine_Field_MatchThreshold,
            settings.Vision.MatchThreshold);
        Range(issues, "Vision.PollIntervalMs", Strings.Settings_Engine_Field_VisionPollInterval,
            settings.Vision.PollIntervalMs, MinVisionPollMs, MaxVisionPollMs,
            Strings.Settings_Engine_Unit_Milliseconds);

        Threshold(issues, "Vision.ClassMatcher.MatchThreshold", Strings.Settings_Engine_Field_ClassMatchThreshold,
            settings.Vision.ClassMatcher.MatchThreshold);
        Range(issues, "Vision.ClassMatcher.LuminanceThreshold", Strings.Settings_Engine_Field_LuminanceThreshold,
            (int)settings.Vision.ClassMatcher.LuminanceThreshold, 0, 255, string.Empty);

        ValidateProfiles(settings, issues);
        ValidateHooks(settings, issues);
        return issues;
    }

    /// <summary>
    /// Границы пауз переехали сюда вместе с самими паузами: скобка пробуждения живёт в
    /// <see cref="AppSettings.Hooks"/>, а не полями профиля.
    ///
    /// Экран этих полей не показывает, так что ошибка здесь может приехать только из файла,
    /// правленного руками, — и приедет она в полосу «Не применено», без подсветки строки. Это
    /// правильный размен: пропустить в движок паузу устаканивания в десять минут хуже, чем
    /// сказать про поле, которого нет на экране. Путь поля называет имя процесса, чтобы в файле
    /// было понятно, куда смотреть.
    /// </summary>
    private static void ValidateHooks(AppSettings settings, List<SettingsIssue> issues)
    {
        // По алфавиту: порядок словаря зависит от порядка в файле, а список отказов, меняющий
        // порядок от перестановки строк в JSON, читается как случайный.
        foreach (var name in settings.Hooks.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            var hook = settings.Hooks[name];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Hooks[{name}]");

            Range(issues, $"{prefix}.SettleMs", Strings.Settings_Engine_Field_SettleDelay,
                hook.SettleMs, 0, MaxDelayMs, Strings.Settings_Engine_Unit_Milliseconds);
            Range(issues, $"{prefix}.DeactivateMs", Strings.Settings_Engine_Field_DeactivationDelay,
                hook.DeactivateMs, 0, MaxDelayMs, Strings.Settings_Engine_Unit_Milliseconds);
        }
    }

    private static void ValidateProfiles(AppSettings settings, List<SettingsIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < settings.Profiles.Count; i++)
        {
            var profile = settings.Profiles[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Profiles[{i}]");

            if (string.IsNullOrWhiteSpace(profile.ProcessName))
            {
                issues.Add(new SettingsIssue($"{prefix}.ProcessName",
                    Strings.Settings_Engine_Issue_ProcessNameEmpty));
            }
            else
            {
                // Расширение отвергается, а не срезается: Process.GetProcessesByName("notepad.exe")
                // молча не находит ничего, и профиль, который выглядит рабочим, не следил бы ни за
                // чем. Лучше отказать сразу, чем отдать пустой список окон.
                if (profile.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new SettingsIssue($"{prefix}.ProcessName", string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.Settings_Engine_Issue_ProcessNameHasExtension,
                        profile.ProcessName,
                        profile.ProcessName[..^4])));
                }

                if (!seen.Add(profile.ProcessName))
                {
                    issues.Add(new SettingsIssue($"{prefix}.ProcessName", string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.Settings_Engine_Issue_ProcessNameDuplicate,
                        profile.ProcessName)));
                }
            }
        }
    }

    private static void Range(List<SettingsIssue> issues, string field, string title, int value, int min, int max,
        string unit)
    {
        if (value >= min && value <= max)
        {
            return;
        }

        var suffix = unit.Length == 0 ? string.Empty : " " + unit;
        issues.Add(new SettingsIssue(field, string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Engine_Issue_OutOfRange,
            title,
            value,
            suffix,
            min,
            max)));
    }

    private static void Threshold(List<SettingsIssue> issues, string field, string title, double value)
    {
        if (value >= MinMatchThreshold && value <= 1.0)
        {
            return;
        }

        issues.Add(new SettingsIssue(field, string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Engine_Issue_ThresholdOutOfRange,
            title,
            value,
            MinMatchThreshold)));
    }
}
