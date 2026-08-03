using System.Globalization;
using System.Runtime.Versioning;
using SmartMacro.Contracts.Dto;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Diagnostics;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Settings;

/// <summary>
/// «Проверить сейчас» — пять вопросов к среде, которые демон может задать сам.
///
/// Существует потому, что <b>почти все отказы этого приложения средовые</b>, а проявляются они
/// одинаково: макрос просто не работает, и в журнале при этом ничего нет. Не хватило прав — ввод
/// молча отбрасывается на уровне ОС. Не тот масштаб экрана — шаблоны, снятые пиксель-в-пиксель,
/// перестают попадать. Файла шаблона нет — узнаёшь об этом на середине прогона. Собрать это в
/// несколько строк, которые можно прочитать до того, как что-то сломается, дешевле, чем каждый
/// раз расследовать заново.
///
/// Здесь их <b>шесть</b>: права, шаблоны, масштаб экрана, хоткеи, папка на запись, автозапуск.
/// Седьмую — время ответа канала — делает панель: демон не может честно измерить время ответа
/// самому себе. Считать их по головам приходится в четырёх местах (здесь, у
/// <c>RunDiagnostics</c>, в сводке и в подсказке пустого состояния), и когда добавилась
/// проверка автозапуска, ни одно из них не поправили — везде осталось «пять из шести», а сама
/// новая проверка не попала в перечни, которые видит пользователь. Добавляешь проверку —
/// пройди по всем четырём.
///
/// <b>Проверки безопасны в том смысле, что ничего не настраивают и не чинят.</b> Совсем без
/// побочных эффектов обходится не всякая: проверка прав шлёт окнам <c>WM_NULL</c>, а проверка
/// папки создаёт и удаляет в ней временный файл — иначе «доступно на запись» пришлось бы
/// выводить из ACL, а это ровно то умозаключение, которое и врёт в интересных случаях. Кнопку
/// жмут, когда что-то уже не работает, так что менять состояние здесь нельзя ничем, кроме
/// того, что немедленно возвращается назад.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnvironmentDiagnostics
{
    /// <summary>Масштаб экрана, под который сняты все шаблоны автора.</summary>
    private const int BaselineScalePercent = 100;

    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly TemplateSetProvider _templates;
    private readonly IHotkeyRegistration _hotkeys;
    private readonly SettingsStore _settings;
    private readonly AutoStartManager _autoStart;

    public EnvironmentDiagnostics(
        WindowRegistry windows,
        MacroGraphStore macros,
        TemplateSetProvider templates,
        IHotkeyRegistration hotkeys,
        SettingsStore settings,
        AutoStartManager autoStart)
    {
        _windows = windows;
        _macros = macros;
        _templates = templates;
        _hotkeys = hotkeys;
        _settings = settings;
        _autoStart = autoStart;
    }

    /// <summary>Прогоняет все проверки. Порядок фиксирован — панель на него не опирается, но глазами читать удобнее.</summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<IReadOnlyList<DiagnosticDto>> RunAsync(CancellationToken cancellationToken = default) =>
    [
        CheckElevation(),
        CheckTemplates(),
        CheckDisplayScale(),
        CheckHotkeys(),
        CheckFolderWritable(),
        await CheckAutoStartAsync(cancellationToken).ConfigureAwait(false),
    ];

    /// <summary>
    /// Зарегистрирован ли автозапуск так, как просят галочки.
    ///
    /// Отдельная проверка, а не сообщение в ответе на <c>SaveSettings</c>, по двум причинам.
    /// Во-первых, разойтись состояние может и БЕЗ участия панели: задачу снёс чистильщик, папку
    /// перенесли, файл настроек принесли с другой машины. Во-вторых, единственная реальная причина
    /// отказа — не хватило прав, а это ровно та категория, ради которой блок диагностики и заведён.
    /// </summary>
    private async Task<DiagnosticDto> CheckAutoStartAsync(CancellationToken cancellationToken)
    {
        var startup = _settings.Current.Startup;
        var mechanism = AutoStartManager.DescribeMechanism(startup);

        if (await _autoStart.IsRegisteredAsync(_settings.Current, cancellationToken).ConfigureAwait(false))
        {
            return new DiagnosticDto(DiagnosticIds.AutoStart, DiagnosticStatus.Ok, "Автозапуск", mechanism);
        }

        return new DiagnosticDto(DiagnosticIds.AutoStart, DiagnosticStatus.Failed,
            "Автозапуск не зарегистрирован",
            $"Галочки просят «{mechanism}», но в системе этого нет. "
            + (startup is { RunAtLogon: true, RunElevated: true }
                ? "Задачу в Планировщике с наивысшими правами может создать только администратор — "
                  + "перезапустите демон от администратора и нажмите «Применить» ещё раз."
                : "Подробности — в журнале демона."));
    }

    /// <summary>
    /// Права против UIPI.
    ///
    /// <b>Проверяется настоящей отправкой, а не выводом «мы не повышены».</b> Одного факта
    /// «демон не администратор» мало: если игра тоже запущена обычным пользователем, всё
    /// работает, и красная карточка была бы ложной тревогой — а ложные тревоги учат не читать
    /// диагностику. Поэтому каждому зарегистрированному окну уходит безобидное <c>WM_NULL</c>, и
    /// считаются те, что ответили отказом доступа. Ровно это происходит с настоящим вводом.
    /// </summary>
    private DiagnosticDto CheckElevation()
    {
        var elevated = EnvironmentProbe.IsElevated();
        var windows = _windows.Snapshot();

        var blocked = 0;
        foreach (var window in windows)
        {
            if (EnvironmentProbe.IsBlockedByUipi(window.Hwnd))
            {
                blocked++;
            }
        }

        if (blocked > 0)
        {
            return new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Failed,
                "Ввод в окна с повышением блокируется",
                string.Create(CultureInfo.CurrentCulture,
                    $"{blocked} из {WindowsWord(windows.Count)} отклоняют сообщения: ")
                + "клиенты запущены от администратора, а демон — нет (UIPI). Блокируются и ввод, и захват экрана. "
                + "Включите «Работать с правами администратора» и перезапустите демон.");
        }

        // Окон нет — сказать про UIPI нечего, и выдумывать нельзя. Но про сами права сказать
        // можно и нужно: это ровно тот случай, когда пользователь ещё не запустил игру и может
        // починить всё заранее.
        if (windows.Count == 0)
        {
            return elevated
                ? new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Ok,
                    "Права демона", "администратор; окон пока нет")
                : new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Warning,
                    "Демон работает без прав администратора",
                    "Окон сейчас нет, так что проверить нечего. Но если клиенты запускаются от администратора, "
                    + "ввод в них будет молча отбрасываться (UIPI).");
        }

        return new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Ok,
            "Ввод в окна проходит",
            $"{WindowsWord(windows.Count)} принимают сообщения{(elevated ? "; демон повышен" : "")}");
    }

    // «1 окно», «2 окна», «11 окон». Найдено глазами: на одном клиенте строка читалась как
    // «1 окон» — мелочь, но такие мелочи и создают ощущение, что текст никто не вычитывал.
    private static string WindowsWord(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => string.Create(CultureInfo.CurrentCulture, $"{count} окно"),
        (2 or 3 or 4, not (12 or 13 or 14)) => string.Create(CultureInfo.CurrentCulture, $"{count} окна"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{count} окон"),
    };

    /// <summary>
    /// Шаблоны, которые называют ноды, против файлов на диске.
    ///
    /// Правило «какой макрос какой шаблон называет» берётся из
    /// <see cref="MacroTemplateAnalysis"/> — той же реализации, что показывает браузер шаблонов.
    /// Второй копии этой логики быть не должно: диагностика, расходящаяся с режимом «Шаблоны»,
    /// хуже отсутствующей.
    /// </summary>
    private DiagnosticDto CheckTemplates()
    {
        var onDisk = _templates.Catalog();
        var sets = new HashSet<string>(
            onDisk.Where(file => file.Set is not null).Select(file => file.Set!), StringComparer.OrdinalIgnoreCase);
        var singles = new HashSet<string>(
            onDisk.Where(file => file.Set is null).Select(file => file.Name), StringComparer.OrdinalIgnoreCase);

        var missing = new List<TemplateUsage>();
        foreach (var usage in MacroTemplateAnalysis.Analyze(_macros.All))
        {
            var exists = usage.IsSet ? sets.Contains(usage.Name) : singles.Contains(usage.Name);
            if (!exists)
            {
                missing.Add(usage);
            }
        }

        if (missing.Count == 0)
        {
            return new DiagnosticDto(DiagnosticIds.Templates, DiagnosticStatus.Ok,
                "Шаблоны на месте",
                string.Create(CultureInfo.CurrentCulture, $"файлов {onDisk.Count}, все ссылки разрешаются"));
        }

        var first = missing[0];
        var name = first.IsSet ? $"templates/{first.Name}/" : $"templates/{first.Name}.png";
        var detail = string.Create(CultureInfo.CurrentCulture,
            $"{name} отсутствует, на него ссылаются шагов: {first.References.Count} в макросах: {first.MacroCount}.");
        if (missing.Count > 1)
        {
            detail += string.Create(CultureInfo.CurrentCulture, $" И ещё ненайденных имён: {missing.Count - 1}.");
        }

        return new DiagnosticDto(DiagnosticIds.Templates, DiagnosticStatus.Failed, "Файл шаблона не найден", detail);
    }

    /// <summary>
    /// Масштабирование экрана.
    ///
    /// Предупреждение, а не отказ: работать оно не мешает, но все шаблоны и все координаты в
    /// нодах сняты пиксель-в-пиксель при 100%, и при другом масштабе распознавание начнёт
    /// промахиваться, не сообщив об этом ни строчкой.
    /// </summary>
    private static DiagnosticDto CheckDisplayScale()
    {
        var scale = EnvironmentProbe.DisplayScalePercent();
        return scale == BaselineScalePercent
            ? new DiagnosticDto(DiagnosticIds.DisplayScale, DiagnosticStatus.Ok,
                "Масштабирование экрана", "100% — как при съёмке шаблонов")
            : new DiagnosticDto(DiagnosticIds.DisplayScale, DiagnosticStatus.Warning,
                string.Create(CultureInfo.CurrentCulture, $"Масштабирование экрана {scale}%"),
                "Шаблоны и координаты в нодах сняты при 100% — они сдвинутся, и распознавание будет промахиваться "
                + "без единой ошибки в журнале.");
    }

    /// <summary>
    /// Глобальные аккорды: сколько из привязанных Win32 действительно отдал.
    ///
    /// Тот же список, что показывает ⚠ на строке библиотеки макросов (<c>GetHotkeyFailures</c>),
    /// — здесь он просто сведён в одно число.
    /// </summary>
    private DiagnosticDto CheckHotkeys()
    {
        var failures = _hotkeys.Failures;
        var bound = _macros.All.Sum(macro => macro.Triggers.OfType<Macros.Model.HotkeyTrigger>().Count());

        if (failures.Count == 0)
        {
            return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Ok,
                "Глобальные хоткеи",
                string.Create(CultureInfo.CurrentCulture, $"занято {bound} из {bound}"));
        }

        return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Failed,
            "Часть хоткеев занята другой программой",
            string.Create(CultureInfo.CurrentCulture,
                $"Win32 отказал в регистрации {failures.Count} из {bound}; первый — макрос "
                + $"«{failures[0].MacroName}». Такой макрос по клавише не запустится."));
    }

    /// <summary>
    /// Папка приложения на запись. Здесь лежат макросы, настройки и журнал, так что «нет»
    /// означает, что не сохранится вообще ничего, — и узнать об этом лучше до, а не после правки
    /// графа.
    /// </summary>
    private DiagnosticDto CheckFolderWritable()
    {
        var folder = _settings.FolderPath;
        return EnvironmentProbe.IsFolderWritable(folder)
            ? new DiagnosticDto(DiagnosticIds.FolderWritable, DiagnosticStatus.Ok, "Папка приложения на запись", folder)
            : new DiagnosticDto(DiagnosticIds.FolderWritable, DiagnosticStatus.Failed,
                "Папка приложения не пишется",
                $"{folder} — сюда не сохранятся ни макросы, ни настройки, ни журнал. "
                + "Перенесите программу из Program Files или дайте права на папку.");
    }
}
