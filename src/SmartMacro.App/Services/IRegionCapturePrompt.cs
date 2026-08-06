using System.Globalization;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Bundle;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.App.Services;

/// <summary>
/// Ради чего открыт диалог: вырезать одиночный шаблон, вырезать очередной файл набора или взять
/// одну ТОЧКУ.
///
/// У первых двух разница не в подписи поля, а в том, куда ляжет файл: одиночный шаблон живёт в
/// корне <c>templates/</c> и называется поимённо (<c>Find/Wait</c>), а тег — это файл внутри
/// набора (<c>MatchTemplateSet</c> называет набор целиком, и основа имени каждого файла в нём
/// становится кандидатом в теги).
///
/// <b><see cref="Point"/> — ВТОРОЙ РЕЖИМ ТОГО ЖЕ ОКНА, а не второе окно.</b> Кадр, панорама,
/// масштаб, дверь «файл с диска» и аренда побудки нужны ему ровно те же; отличается один жест —
/// щелчок по пикселю вместо протягивания рамки. Два почти одинаковых окна разъехались бы на
/// первой же правке одного из них, и разъехались бы в той самой части, которая стоила дороже
/// всего (перевод «экран → кадр»).
/// </summary>
public enum RegionCaptureKind
{
    /// <summary>Одиночный шаблон: <c>templates/{имя}.png</c>. Ноды <c>FindElement</c> и <c>WaitForElement</c>.</summary>
    Template,

    /// <summary>Тег внутри набора: <c>templates/{набор}/{тег}.png</c>. Нода <c>MatchTemplateSet</c>.</summary>
    Tag,

    /// <summary>Одна точка в клиентских координатах. Нода <c>Click</c>; имени не спрашивают, файла нет.</summary>
    Point,
}

/// <summary>
/// Всё, что диалогу выбора области нужно знать о том, ради чего его открыли.
/// </summary>
/// <param name="Kind">Спрашиваем имя файла или имя тега.</param>
/// <param name="MacroName">Макрос, в бандл которого ляжет вырезка, — для заголовка окна.</param>
/// <param name="NodeName">Подпись ноды, из которой пришли, — тоже для заголовка.</param>
/// <param name="Set">
/// Набор для <see cref="RegionCaptureKind.Tag"/> — то, что стоит в поле «набор» ноды. У
/// <see cref="RegionCaptureKind.Template"/> всегда <c>null</c>: одиночные шаблоны лежат в корне.
/// </param>
/// <param name="SuggestedName">
/// Чем заполнить поле имени. Для <c>Find</c>/<c>Wait</c> это уже набранное имя шаблона — тогда
/// «снял и нажал ОК» просто заменяет содержимое существующего файла.
/// </param>
/// <param name="ExistingNames">
/// Имена, уже занятые в этом наборе (или в корне). Диалог не запрещает их — заменить свой же
/// шаблон свежей вырезкой это нормальный ход, — но обязан ПРЕДУПРЕДИТЬ: цена ошибки здесь та же,
/// что у занятого имени макроса.
///
/// ⚠️ Это СНИМОК на момент открытия, и диалогу его мало: за один заход он кладёт в набор
/// одиннадцать вырезок подряд, и вторая «Лучник» — обычная опечатка. Поэтому диалог ведёт свой
/// список поверх этого и дописывает в него каждое сохранённое имя.
/// </param>
/// <param name="Windows">
/// Окна, которые демон отслеживает, СНИМКОМ на момент открытия.
///
/// Живой каталог сюда не заводится намеренно: он принадлежит view-model редактора, а диалог живёт
/// секунды. Цена названа — клиент, запущенный уже при открытом диалоге, в списке не появится, и
/// закрыть-открыть придётся вручную. Пустой список — не тупик: дверь «взять файл с диска»
/// существует ровно для случая «игра не запущена», и пустое состояние ведёт к ней.
/// </param>
public sealed record RegionCaptureRequest(
    RegionCaptureKind Kind,
    string MacroName,
    string NodeName,
    string? Set,
    string SuggestedName,
    IReadOnlyList<string> ExistingNames,
    IReadOnlyList<WindowDto> Windows)
{
    /// <summary>Заголовок окна: чей шаблон мы сейчас вырезаем.</summary>
    public string Heading => string.Format(
        CultureInfo.CurrentCulture, Strings.Dialog_Region_Heading, NodeName, MacroName);

    /// <summary>Подпись поля имени — «имя шаблона» либо «тег».</summary>
    public string NameLabel => Kind == RegionCaptureKind.Tag
        ? Strings.Dialog_Region_NameLabelTag
        : Strings.Dialog_Region_NameLabelTemplate;

    /// <summary>
    /// Куда ляжет файл, целиком: <c>templates/classes/Лучник.png</c>. Строка обновляется под полем
    /// имени по мере набора — она честнее любой подписи, потому что это ровно тот путь, который
    /// будет искать исполнитель.
    /// </summary>
    public string PathPreview(string name) => string.IsNullOrWhiteSpace(name)
        ? string.Empty
        : MacroBundleFormat.TemplateFolder + MacroBundleFormat.TemplatePath(Set, name.Trim());

    /// <summary>
    /// Годится ли имя в качестве имени шаблона (или тега).
    ///
    /// <b>Проверка — это круг через правило имён, а не свой список запрещённых символов.</b> Имя
    /// собирается в путь и тут же разбирается обратно; годится оно тогда, когда пара «набор + имя»
    /// пережила круг без изменений. Второй копии правила поэтому не заводится — та разъехалась бы
    /// с <see cref="MacroBundleFormat.TryParseTemplatePath"/> ровно так же, как разъехался бы
    /// второй экземпляр правила отбора целей.
    ///
    /// Ловится этим в первую очередь СЛЕШ, и он не выдуман. «classes/Лучник» в поле одиночного
    /// шаблона проходит и запись, и разбор — но разбирается как ПАРА (набор «classes», имя
    /// «Лучник»), тогда как нода <c>FindElement</c> ищет одиночный шаблон с именем
    /// «classes/Лучник». Файл лёг бы в бандл, а исполнитель его не нашёл бы никогда.
    /// </summary>
    public bool IsNameUsable(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        return MacroBundleFormat.TryParseTemplatePath(
                   MacroBundleFormat.TemplatePath(Set, trimmed), out var set, out var parsed)
               && string.Equals(set, Set, StringComparison.Ordinal)
               && string.Equals(parsed, trimmed, StringComparison.Ordinal);
    }
}

/// <summary>
/// Ответ человека: что он выделил и как это назвал.
/// </summary>
/// <param name="Name">Имя шаблона либо тег — уже подрезанное по краям.</param>
/// <param name="Png">Вырезка ТОЧНО по выделению. Она и станет шаблоном.</param>
/// <param name="Selection">Выделение в клиентских координатах кадра — без запаса.</param>
/// <param name="FrameWidth">Ширина кадра, из которого резали; <c>0</c> — неизвестна (файл с диска нестандартного размера).</param>
/// <param name="FrameHeight">Высота кадра; то же самое.</param>
public sealed record RegionCaptureResult(
    string Name,
    byte[] Png,
    ScreenRect Selection,
    int FrameWidth,
    int FrameHeight)
{
    /// <summary>
    /// Область поиска для ноды: выделение с запасом, обрезанное по кадру. Почему с запасом и
    /// почему именно таким — у <see cref="SearchRegion"/>.
    /// </summary>
    public ScreenRect Region => SearchRegion.Around(Selection, FrameWidth, FrameHeight);
}

/// <summary>
/// Кладёт вырезку в бандл, НЕ ЗАКРЫВАЯ диалог. <c>null</c> — легло; иначе текст отказа, который
/// диалог покажет у себя внизу.
///
/// <b>Зачем понадобился обратный вызов.</b> Кнопка «Сохранить и дальше» существует ради набора
/// классов: одиннадцать шаблонов, вырезанных ОДНОЙ рамкой у одиннадцати персонажей. Диалог,
/// закрывающийся после каждого, означал бы одиннадцать заходов и одиннадцать протягиваний рамки
/// заново, то есть одиннадцать чуть разных вырезок — ровно ту беду, от которой кнопка и спасает.
/// Значит, сохранять надо не выходя, а положить файл в бандл может только редактор.
/// </summary>
/// <param name="crop">Вырезка вместе с именем, под которым её просят положить.</param>
public delegate string? RegionCaptureSink(RegionCaptureResult crop);

/// <summary>
/// Показывает диалог «выдели область на свежем снимке окна» и возвращает вырезку.
///
/// <b>Шов ровно того же рода, что <see cref="IMacroNameConflictPrompt"/>, и по той же
/// единственной причине.</b> View-model этого проекта не знают ничего Avalonia-образного и
/// гоняются headless против поддельного клиента IPC; окно, открытое прямо из VM редактора,
/// сделало бы непроверяемым всё, что происходит ПОСЛЕ ответа, — а именно там и лежит вся
/// содержательная часть: запись шаблона в бандл и заполнение области ноды.
///
/// <b>Отсутствие реализации — это «Отмена».</b> VM собирается и без неё (путь дизайнера,
/// headless-тесты, где диалог не проверяют), и молча положить в бандл файл, ни о чём не спросив,
/// было бы худшим из возможных умолчаний.
///
/// <b>Два метода, одно окно.</b> Вырезка возвращает картинку с именем, выбор точки — пару чисел;
/// сводить их в один тип со взаимоисключающими полями значило бы заводить объединение там, где у
/// вызывающих и так две разные кнопки. Реализация у обоих одна — <c>RegionCaptureDialog</c> в
/// соответствующем режиме (<see cref="RegionCaptureKind"/>).
/// </summary>
public interface IRegionCapturePrompt
{
    /// <summary>
    /// Открывает диалог вырезки и ждёт.
    /// </summary>
    /// <param name="request">Ради чего открыли; <see cref="RegionCaptureRequest.Kind"/> — не <see cref="RegionCaptureKind.Point"/>.</param>
    /// <param name="commit">Куда класть вырезку. Зовётся на КАЖДУЮ, включая последнюю.</param>
    /// <returns>
    /// Последняя УЛОЖЕННАЯ вырезка — по ней нода заполняет область и имя шаблона; <c>null</c>,
    /// если не уложено ни одной.
    ///
    /// ⚠️ «Отмена» после нескольких сохранений возвращает последнее сохранённое, а не
    /// <c>null</c>: файлы уже в бандле, и оставить ноду с пустой областью при полном наборе
    /// шаблонов было бы хуже любой из двух честностей.
    /// </returns>
    Task<RegionCaptureResult?> AskAsync(RegionCaptureRequest request, RegionCaptureSink commit);

    /// <summary>
    /// Открывает то же окно в режиме точки и ждёт. <c>null</c> — отменили.
    /// </summary>
    /// <param name="request">Ради чего открыли; <see cref="RegionCaptureRequest.Kind"/> — <see cref="RegionCaptureKind.Point"/>.</param>
    Task<ScreenPoint?> AskPointAsync(RegionCaptureRequest request);
}
