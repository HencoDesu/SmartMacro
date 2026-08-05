using System.Globalization;
using System.Runtime.Versioning;
using SmartMacro.Contracts.Dto;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Diagnostics;
using SmartMacro.Resources;
using SmartMacro.Windows;

namespace SmartMacro.Settings;

/// <summary>
/// «Проверить среду» — шесть вопросов, которые демон может задать сам.
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
    private readonly IHotkeyRegistration _hotkeys;
    private readonly SettingsStore _settings;
    private readonly AutoStartManager _autoStart;

    public EnvironmentDiagnostics(
        WindowRegistry windows,
        MacroGraphStore macros,
        IHotkeyRegistration hotkeys,
        SettingsStore settings,
        AutoStartManager autoStart)
    {
        _windows = windows;
        _macros = macros;
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
            return new DiagnosticDto(DiagnosticIds.AutoStart, DiagnosticStatus.Ok,
                Strings_Engine.Diag_AutoStart_Ok_Title, mechanism);
        }

        return new DiagnosticDto(DiagnosticIds.AutoStart, DiagnosticStatus.Failed,
            Strings_Engine.Diag_AutoStart_Failed_Title,
            string.Format(
                CultureInfo.CurrentCulture,
                Strings_Engine.Diag_AutoStart_Failed_Detail,
                mechanism,
                startup is { RunAtLogon: true, RunElevated: true }
                    ? Strings_Engine.Diag_AutoStart_Failed_NeedAdmin
                    : Strings_Engine.Diag_AutoStart_Failed_SeeLog));
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
                Strings_Engine.Diag_Elevation_Blocked_Title,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Diag_Elevation_Blocked_Detail,
                    blocked,
                    WindowsWord(windows.Count)));
        }

        // Окон нет — сказать про UIPI нечего, и выдумывать нельзя. Но про сами права сказать
        // можно и нужно: это ровно тот случай, когда пользователь ещё не запустил игру и может
        // починить всё заранее.
        if (windows.Count == 0)
        {
            return elevated
                ? new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Ok,
                    Strings_Engine.Diag_Elevation_NoWindows_Ok_Title,
                    Strings_Engine.Diag_Elevation_NoWindows_Ok_Detail)
                : new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Warning,
                    Strings_Engine.Diag_Elevation_NoWindows_Warning_Title,
                    Strings_Engine.Diag_Elevation_NoWindows_Warning_Detail);
        }

        return new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Ok,
            Strings_Engine.Diag_Elevation_Ok_Title,
            string.Format(
                CultureInfo.CurrentCulture,
                Strings_Engine.Diag_Elevation_Ok_Detail,
                WindowsWord(windows.Count),
                elevated ? Strings_Engine.Diag_Elevation_Ok_ElevatedSuffix : string.Empty));
    }

    // «1 окно», «2 окна», «11 окон». Найдено глазами: на одном клиенте строка читалась как
    // «1 окон» — мелочь, но такие мелочи и создают ощущение, что текст никто не вычитывал.
    private static string WindowsWord(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_Windows_One, count),
        (2 or 3 or 4, not (12 or 13 or 14)) =>
            string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_Windows_Few, count),
        _ => string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_Windows_Many, count),
    };

    /// <summary>
    /// Шаблоны, которые называют ноды, против того, что лежит в их бандлах.
    ///
    /// С волны F2 общего дерева <c>templates/</c> нет: шаблон живёт внутри <c>.hsm</c> и
    /// принадлежит ровно одному макросу, поэтому сверка идёт ПОМАКРОСНО, а имя в отчёте
    /// называется вместе с макросом — иначе «нет Лучник.png» ничего не говорит о том, где искать.
    ///
    /// Правило «какой макрос какой шаблон называет» берётся из
    /// <see cref="MacroTemplateAnalysis"/> — той же реализации, что кормит валидатор и браузер
    /// шаблонов в редакторе. Второй копии этой логики быть не должно: диагностика, расходящаяся с
    /// тем, что показывает редактор, хуже отсутствующей.
    ///
    /// Дублирование с валидатором мнимое: тот говорит про ОТКРЫТЫЙ макрос, а сюда заходят, когда
    /// «просто не работает» и открывать по очереди десять макросов не хочется.
    /// </summary>
    private DiagnosticDto CheckTemplates()
    {
        var files = 0;
        var missing = new List<string>();

        foreach (var entry in _macros.Entries)
        {
            files += entry.TemplatePaths.Count;
            foreach (var usage in MacroTemplateAnalysis.Analyze(entry.Graph))
            {
                if (!entry.Templates.Has(usage.Name, usage.IsSet))
                {
                    missing.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        usage.IsSet ? Strings_Engine.Diag_Templates_Ref_Set : Strings_Engine.Diag_Templates_Ref_Single,
                        entry.Name,
                        usage.Name));
                }
            }
        }

        if (missing.Count == 0)
        {
            return new DiagnosticDto(DiagnosticIds.Templates, DiagnosticStatus.Ok,
                Strings_Engine.Diag_Templates_Ok_Title,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Diag_Templates_Ok_Detail,
                    files,
                    _macros.Entries.Count));
        }

        var detail = string.Format(
            CultureInfo.CurrentCulture, Strings_Engine.Diag_Templates_Missing_Detail, missing[0]);
        if (missing.Count > 1)
        {
            detail += string.Format(
                CultureInfo.CurrentCulture, Strings_Engine.Diag_Templates_Missing_More, missing.Count - 1);
        }

        return new DiagnosticDto(DiagnosticIds.Templates, DiagnosticStatus.Failed,
            Strings_Engine.Diag_Templates_Missing_Title, detail);
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
                Strings_Engine.Diag_DisplayScale_Ok_Title,
                Strings_Engine.Diag_DisplayScale_Ok_Detail)
            : new DiagnosticDto(DiagnosticIds.DisplayScale, DiagnosticStatus.Warning,
                string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_DisplayScale_Warning_Title, scale),
                Strings_Engine.Diag_DisplayScale_Warning_Detail);
    }

    /// <summary>
    /// Глобальные аккорды: сколько из привязанных демон действительно вооружил.
    ///
    /// <b>Не вооружить сочетание можно ДВУМЯ способами, и оба ведут к одному симптому</b> —
    /// клавиша нажимается, не происходит ничего. Первый: Win32 отказал в регистрации, потому что
    /// аккордом владеет другая программа (тот же список, что показывает ⚠ на строке библиотеки
    /// через <c>GetHotkeyFailures</c>). Второй появился в F3 вместе с инверсией авторства: в
    /// <c>macros/</c> может лечь файл, который никто не проверял, и хоткей макроса с ошибкой в
    /// графе демон не вооружает намеренно. Об этом здесь сказано отдельной строкой, потому что
    /// действие пользователя в двух случаях разное: сменить сочетание или починить граф.
    ///
    /// <b>Способ третий — ПРИОСТАНОВКА, и проверяется он первым.</b> Пока панель держит открытым
    /// режим «Макросы», зарегистрировано ноль аккордов: иначе <c>RegisterHotKey</c> проглатывал бы
    /// нажатия ровно тех сочетаний, которые пользователь в этот момент переназначает. Состояние
    /// законное, но при нём ответ «занято N из N» — прямая ложь, а экран, существующий ради
    /// вопроса «почему макрос просто не работает», лгать не имеет права. Отдельной проверкой это
    /// НЕ заведено намеренно: вопрос ровно тот же («сработает ли клавиша»), а число проверок
    /// прописью живёт в четырёх местах, два из которых в панели.
    /// </summary>
    private DiagnosticDto CheckHotkeys()
    {
        var failures = _hotkeys.Failures;
        if (_hotkeys.IsSuspended)
        {
            // Предупреждение, а не отказ: сама по себе приостановка — это работающий механизм, а
            // не поломка, и снимется она, как только редактор закроют. Но зелёной карточки здесь
            // быть не может: клавиши прямо сейчас молчат.
            return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Warning,
                Strings_Engine.Diag_Hotkeys_Suspended_Title,
                Strings_Engine.Diag_Hotkeys_Suspended_Detail);
        }

        var bound = _macros.All.Sum(macro => macro.Triggers.OfType<Macros.Model.HotkeyTrigger>().Count());
        var broken = _macros.Entries
            .Where(entry => entry.HasErrors && entry.Graph.Triggers.OfType<Macros.Model.HotkeyTrigger>().Any())
            .ToList();

        if (failures.Count == 0 && broken.Count == 0)
        {
            return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Ok,
                Strings_Engine.Diag_Hotkeys_Ok_Title,
                string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_Hotkeys_Ok_Detail, bound));
        }

        if (failures.Count == 0)
        {
            // Точку после замечания валидатора НЕ ставим: оно приходит уже с ней, и своя давала бы
            // «…не найдена в графе.. По клавише» — нашлось глазами на экране. Это правило живёт в
            // самой строке Diag_Hotkeys_BrokenGraph_Detail и в пояснении к ней.
            return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Failed,
                Strings_Engine.Diag_Hotkeys_BrokenGraph_Title,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Diag_Hotkeys_BrokenGraph_Detail,
                    broken.Count,
                    broken[0].Name,
                    broken[0].FirstError));
        }

        var detail = string.Format(
            CultureInfo.CurrentCulture,
            Strings_Engine.Diag_Hotkeys_Taken_Detail,
            failures.Count,
            bound,
            failures[0].MacroName);
        if (broken.Count > 0)
        {
            detail += string.Format(
                CultureInfo.CurrentCulture, Strings_Engine.Diag_Hotkeys_Taken_AlsoBroken, broken.Count);
        }

        return new DiagnosticDto(DiagnosticIds.Hotkeys, DiagnosticStatus.Failed,
            Strings_Engine.Diag_Hotkeys_Taken_Title, detail);
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
            ? new DiagnosticDto(DiagnosticIds.FolderWritable, DiagnosticStatus.Ok,
                Strings_Engine.Diag_Folder_Ok_Title, folder)
            : new DiagnosticDto(DiagnosticIds.FolderWritable, DiagnosticStatus.Failed,
                Strings_Engine.Diag_Folder_Failed_Title,
                string.Format(CultureInfo.CurrentCulture, Strings_Engine.Diag_Folder_Failed_Detail, folder));
    }
}
