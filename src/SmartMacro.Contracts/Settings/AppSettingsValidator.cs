using System.Globalization;

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

    /// <summary>Верхняя граница пауз активации и деактивации.</summary>
    public const int MaxDelayMs = 10_000;

    /// <summary>
    /// Ниже этой паузы после сигнала побудки PW начинает терять ввод. НЕ ошибка — подсказка
    /// рядом с полем; порог живёт здесь, чтобы текст подсказки и это число не разъехались.
    /// </summary>
    public const int RecommendedMinSettleMs = 150;

    /// <summary>Проверяет настройки целиком.</summary>
    /// <param name="settings">Проверяемый снимок.</param>
    /// <returns>Пустой список = принято.</returns>
    public static IReadOnlyList<SettingsIssue> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsIssue>();

        Range(issues, "Watch.ProcessPollIntervalSeconds", "Поиск новых процессов",
            settings.Watch.ProcessPollIntervalSeconds, MinPollSeconds, MaxPollSeconds, "с");
        Range(issues, "Watch.WindowPollIntervalSeconds", "Проверка живости окон",
            settings.Watch.WindowPollIntervalSeconds, MinPollSeconds, MaxPollSeconds, "с");

        Threshold(issues, "Vision.MatchThreshold", "Порог совпадения", settings.Vision.MatchThreshold);
        Range(issues, "Vision.PollIntervalMs", "Интервал опроса распознавания",
            settings.Vision.PollIntervalMs, MinVisionPollMs, MaxVisionPollMs, "мс");

        Threshold(issues, "Vision.ClassMatcher.MatchThreshold", "Порог распознавания класса",
            settings.Vision.ClassMatcher.MatchThreshold);
        Range(issues, "Vision.ClassMatcher.LuminanceThreshold", "Порог яркости",
            (int)settings.Vision.ClassMatcher.LuminanceThreshold, 0, 255, string.Empty);

        ValidateProfiles(settings, issues);
        return issues;
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
                issues.Add(new SettingsIssue($"{prefix}.ProcessName", "Имя процесса не может быть пустым."));
            }
            else
            {
                // Расширение отвергается, а не срезается: Process.GetProcessesByName("notepad.exe")
                // молча не находит ничего, и профиль, который выглядит рабочим, не следил бы ни за
                // чем. Лучше отказать сразу, чем отдать пустой список окон.
                if (profile.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new SettingsIssue($"{prefix}.ProcessName",
                        $"«{profile.ProcessName}» — имя процесса пишется без расширения: "
                        + $"«{profile.ProcessName[..^4]}»."));
                }

                if (!seen.Add(profile.ProcessName))
                {
                    issues.Add(new SettingsIssue($"{prefix}.ProcessName",
                        $"Профиль для «{profile.ProcessName}» уже есть — имена процессов не различаются регистром."));
                }
            }

            Range(issues, $"{prefix}.SettleDelayMs", "Оседание", profile.SettleDelayMs, 0, MaxDelayMs, "мс");
            Range(issues, $"{prefix}.DeactivationDelayMs", "Деактивация", profile.DeactivationDelayMs, 0, MaxDelayMs,
                "мс");
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
        issues.Add(new SettingsIssue(field, string.Create(CultureInfo.CurrentCulture,
            $"{title}: {value}{suffix} — допустимо от {min}{suffix} до {max}{suffix}.")));
    }

    private static void Threshold(List<SettingsIssue> issues, string field, string title, double value)
    {
        if (value >= MinMatchThreshold && value <= 1.0)
        {
            return;
        }

        issues.Add(new SettingsIssue(field, string.Create(CultureInfo.CurrentCulture,
            $"{title}: {value:F2} — допустимо от {MinMatchThreshold:F2} до 1.00.")));
    }
}
