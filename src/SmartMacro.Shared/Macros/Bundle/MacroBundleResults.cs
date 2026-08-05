using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Чем именно кончилось чтение. Разные значения — разные новости для пользователя, и в этом
/// весь смысл перечисления: «повреждён» и «сделано другой версией формата» требуют от него
/// разных действий, а слипшись в одно «ошибка» не требуют никаких.
/// </summary>
public enum MacroBundleFault
{
    /// <summary>Прочиталось.</summary>
    None,

    /// <summary>Файла нет по указанному пути.</summary>
    Missing,

    /// <summary>Файл есть, но не открывается как zip, либо не читается с диска.</summary>
    NotAnArchive,

    /// <summary>Архив открылся, но обязательной записи в нём нет.</summary>
    EntryMissing,

    /// <summary>Запись есть, но её содержимое не разбирается.</summary>
    Malformed,

    /// <summary>
    /// Версия формата не та, которую понимает эта сборка. НЕ «повреждён»: файл целый, просто
    /// сделан другой версией программы.
    /// </summary>
    UnsupportedVersion,
}

/// <summary>
/// Исход чтения <c>metadata.json</c> — отдельный от графа, потому что читать его можно
/// отдельно (<see cref="MacroBundleReader.ReadMetadata(string)"/>).
/// </summary>
/// <param name="Metadata">Паспорт или <c>null</c>, если прочитать не вышло.</param>
/// <param name="FormatVersion">
/// Версия формата, если её удалось достать из файла, — она читается ПЕРВОЙ и отдельно от
/// остальных полей ровно затем, чтобы у бандла чужой версии было что сказать о себе, даже когда
/// весь остальной паспорт не разбирается.
/// </param>
/// <param name="Fault">Что случилось.</param>
/// <param name="Message">Человекочитаемое объяснение или <c>null</c> при успехе.</param>
public sealed record MacroBundleMetadataResult(
    MacroBundleMetadata? Metadata,
    int? FormatVersion,
    MacroBundleFault Fault,
    string? Message)
{
    /// <summary>Паспорт прочитан.</summary>
    public bool IsOk => Fault == MacroBundleFault.None && Metadata is not null;

    internal static MacroBundleMetadataResult Ok(MacroBundleMetadata metadata) =>
        new(metadata, metadata.FormatVersion, MacroBundleFault.None, null);

    internal static MacroBundleMetadataResult Failed(MacroBundleFault fault, string message, int? formatVersion = null) =>
        new(null, formatVersion, fault, message);
}

/// <summary>
/// Исход полного чтения бандла. <b>Два независимых вердикта в одном объекте</b>, и это и есть
/// требование §5.7 в виде типа: <see cref="Metadata"/> может быть успешным, когда
/// <see cref="Graph"/> — <c>null</c>. Тогда библиотека показывает нормальное имя с
/// восклицательным знаком вместо строки «файл X — ошибка», из которой пользователю не понять
/// даже, какой это был макрос.
///
/// Обратный порядок невозможен по построению: граф не читается, пока не прочитан паспорт, —
/// сперва надо узнать версию формата.
/// </summary>
/// <param name="Metadata">Исход чтения <c>metadata.json</c>.</param>
/// <param name="Graph">Граф или <c>null</c>, если его прочитать не вышло.</param>
/// <param name="GraphFault">Что случилось с графом.</param>
/// <param name="GraphMessage">Человекочитаемое объяснение или <c>null</c> при успехе.</param>
/// <param name="TemplatePaths">
/// Пути шаблонов ОТНОСИТЕЛЬНО <see cref="MacroBundleFormat.TemplateFolder"/> — то есть
/// <c>classes/Лучник.png</c>, а не <c>templates/classes/Лучник.png</c>: вызывающий думает
/// набором и именем, и лишний префикс он бы всё равно отрезал. Отсортированы по порядку.
/// БЕЗ БАЙТОВ: список — это метаданные, пиксели — по требованию
/// (<see cref="MacroBundleReader.ReadTemplate(string,string)"/>), ровно тот же довод, что у
/// <c>TemplateDto</c> в протоколе.
/// </param>
/// <param name="Submacros">
/// Под-макросы из <see cref="MacroBundleFormat.SubmacroFolder"/>, РАЗОБРАННЫЕ, по возрастанию
/// подписи (волна F4).
///
/// В отличие от шаблонов, читаются целиком и сразу, а не по требованию, и довод здесь обратный:
/// под-макрос — это JSON размером с <c>nodes.json</c>, он нужен И демону (исполнять), И панели
/// (показать дерево библиотеки и открыть на канве), и оба спрашивают его сразу же. «Список
/// метаданных плюс содержимое по требованию» окупается на килобайтах PNG, а здесь стоило бы
/// второго открытия архива ради тех же байтов.
/// </param>
/// <param name="SubmacroFaults">
/// Записи <c>submacro/</c>, которые под-макросом назвались, но не разобрались, — по строке
/// объяснения на каждую.
///
/// <b>Не сливаются с <see cref="GraphFault"/> намеренно.</b> Испорченный под-макрос не имеет
/// права спрятать родительский граф: строка библиотеки должна показать настоящее имя макроса и
/// сказать, ЧТО именно в нём сломано, — а не «файл X — ошибка», из которой не понять даже, какой
/// это был макрос. Вердикт превращается в ошибку валидации у
/// <c>MacroGraphValidator.ValidateBundle</c>, так что триггеры такого макроса демон не вооружает.
/// </param>
public sealed record MacroBundleReadResult(
    MacroBundleMetadataResult Metadata,
    MacroGraph? Graph,
    MacroBundleFault GraphFault,
    string? GraphMessage,
    IReadOnlyList<string> TemplatePaths,
    IReadOnlyList<MacroSubmacro> Submacros,
    IReadOnlyList<string> SubmacroFaults)
{
    /// <summary>Прочитано всё: и паспорт, и граф.</summary>
    public bool IsOk => Metadata.IsOk && GraphFault == MacroBundleFault.None && Graph is not null;
}

/// <summary>
/// Один шаблон бандла глазами браузера: где лежит, какого размера — и без единого пикселя.
/// Возвращает <see cref="MacroBundleReader.ReadTemplateCatalog"/>; довод против байтов здесь тот
/// же, что у <c>TemplateDto</c> в протоколе.
/// </summary>
/// <param name="Path">Путь относительно <see cref="MacroBundleFormat.TemplateFolder"/>, например <c>classes/Лучник.png</c>.</param>
/// <param name="Size">Ширина и высота в пикселях; 0×0, если запись не разобралась как PNG.</param>
/// <param name="Bytes">Размер записи. Он же размер файла: бандл не сжимается.</param>
public sealed record MacroBundleTemplateInfo(string Path, (int Width, int Height) Size, long Bytes);

/// <summary>Один файл внутри бандла: путь относительно своей папки и содержимое.</summary>
/// <param name="Path">
/// Относительный путь, разделитель — прямой слеш (<see cref="MacroBundleFormat.NormalizeEntryPath"/>
/// приводит к нему и обратные). Например <c>classes/Лучник.png</c> или <c>Find.png</c>.
/// </param>
/// <param name="Bytes">Содержимое файла как есть.</param>
public sealed record MacroBundleFile(string Path, byte[] Bytes);

/// <summary>Всё, что писатель кладёт в один <c>.hsm</c>.</summary>
public sealed record MacroBundleContent
{
    /// <summary>Паспорт. <see cref="MacroBundleMetadata.FormatVersion"/> писатель проставляет сам.</summary>
    public required MacroBundleMetadata Metadata { get; init; }

    /// <summary>Граф.</summary>
    public required MacroGraph Graph { get; init; }

    /// <summary>Шаблоны этого макроса; пути относительно <see cref="MacroBundleFormat.TemplateFolder"/>.</summary>
    public IReadOnlyList<MacroBundleFile> Templates { get; init; } = [];

    /// <summary>
    /// Содержимое <see cref="MacroBundleFormat.SubmacroFolder"/> СЫРЫМИ БАЙТАМИ; пути
    /// относительно неё.
    ///
    /// Байтами, а не <see cref="MacroSubmacro"/>, и это несущее свойство цикла «прочитать всё →
    /// поменять одно → записать всё»: запись, которую сегодняшний разбор под-макросом не считает
    /// (заметка автора, файл будущей версии формата), обязана пережить перезапись бандла байт в
    /// байт. Разбор её потерял бы — то есть сделал бы ровно ту потерю, от которой формат и
    /// защищает.
    /// </summary>
    public IReadOnlyList<MacroBundleFile> Submacros { get; init; } = [];

    /// <summary>
    /// Всё ОСТАЛЬНОЕ, что лежит в архиве, — сырыми байтами и путями ОТ КОРНЯ бандла
    /// (<c>заметка.txt</c>, <c>templates/описание.md</c>, <c>templates/куски/вложенный.png</c>).
    ///
    /// <b>То же правило, что уже действовало для <see cref="Submacros"/>, — просто теперь оно
    /// действует ВЕЗДЕ.</b> Формат намеренно store-only, «чтобы содержимое было видно любым
    /// просмотрщиком», то есть пользователю прямо предложено лазить внутрь бандла архиватором.
    /// А писатель пересобирает архив с нуля и до сих пор переносил только то, что опознал:
    /// <c>metadata.json</c>, <c>nodes.json</c>, PNG по правилу разбора и содержимое
    /// <c>submacro/</c>. Всё прочее исчезало при первом же «Сохранить» — и заметка автора рядом с
    /// шаблонами, и запись, которую положила туда БУДУЩАЯ версия формата. Довод, записанный у
    /// <see cref="Submacros"/> («потерять его значило бы сделать ровно ту потерю, ради
    /// недопущения которой бандл и заведён»), к корню и к <c>templates/</c> относится слово в
    /// слово.
    ///
    /// Цена названа: неопознанный файл переносится при каждой перезаписи, так что мегабайтное
    /// вложение будет стоить мегабайта на каждое сохранение. Это ровно та же цена, что у
    /// <c>submacro/</c>, и предпочтительнее тихой пропажи.
    ///
    /// Запись-«папка» и путь, вылезающий за пределы архива, сюда НЕ попадают: файлами они не
    /// являются, а второе — заготовка zip slip, и переносить её значило бы однажды отдать её
    /// тому, кто станет распаковывать (см. <see cref="MacroBundleFormat.IsSafeRelativePath"/>).
    /// </summary>
    public IReadOnlyList<MacroBundleFile> Extras { get; init; } = [];
}

/// <summary>
/// Исход чтения бандла ЦЕЛИКОМ — того чтения, которым начинается «прочитать всё → поменять одно
/// → записать всё».
///
/// <b>Отдельный тип вместо <c>MacroBundleContent?</c>, и вся его ценность в одном различии:
/// «файла нет» против «файл есть, но не прочитался».</b> Пока оба исхода были одним <c>null</c>,
/// сохранение на любом из них писало бандл с ПУСТЫМ набором шаблонов и новым <c>Guid</c> — то
/// есть уничтожало вложения. Под это попадали три случая, и ни один не выдуманный: файл, на миг
/// занятый другим процессом; целый <c>metadata.json</c> при неразбирающемся <c>nodes.json</c>; и
/// бандл БУДУЩЕЙ версии формата — который лежит на месте намеренно (§5.7: отодвинуть его значило
/// бы соврать ровно тем способом, ради недопущения которого поле версии и заведено), а внутри у
/// него шаблоны, вырезанные из игры руками.
///
/// Отсюда правило вызывающего, записанное здесь один раз: <see cref="MacroBundleFault.Missing"/>
/// — ЕДИНСТВЕННЫЙ отказ, на котором писать с пустыми вложениями законно. На любом другом запись
/// обязана отказаться и назвать причину.
/// </summary>
/// <param name="Content">Содержимое или <c>null</c>, если прочитать не вышло.</param>
/// <param name="Fault">Что случилось.</param>
/// <param name="Message">Вердикт читателя целиком — для показа и для журнала.</param>
public sealed record MacroBundleContentResult(
    MacroBundleContent? Content,
    MacroBundleFault Fault,
    string? Message)
{
    /// <summary>Содержимое прочитано.</summary>
    public bool IsOk => Fault == MacroBundleFault.None && Content is not null;

    /// <summary>Файла просто нет — единственный отказ, после которого писать законно.</summary>
    public bool IsMissing => Fault == MacroBundleFault.Missing;

    internal static MacroBundleContentResult Ok(MacroBundleContent content) =>
        new(content, MacroBundleFault.None, null);

    internal static MacroBundleContentResult Failed(MacroBundleFault fault, string? message) =>
        new(null, fault, message);
}
