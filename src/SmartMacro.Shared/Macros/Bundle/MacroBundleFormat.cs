namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Всё, что нужно знать о РАСКЛАДКЕ файла <c>.hsm</c>, — в одном месте: расширение, версия
/// формата и имена записей внутри архива.
///
/// <b>Бандл — это zip БЕЗ СЖАТИЯ</b> (<see cref="System.IO.Compression.CompressionLevel.NoCompression"/>,
/// то есть метод <c>Stored</c>). Довод не про скорость: PNG уже сжаты, а JSON рядом с ними
/// мелкий, так что дефлейт почти ничего не выигрывает, зато забирает два свойства, которые
/// стоят дороже байтов, — содержимое видно любым просмотрщиком и <c>grep</c>'ом прямо в файле,
/// и git умеет считать по такому файлу дельту, а не хранить каждую версию целиком. Побочно
/// исчезает и класс «зип-бомб»: без сжатия распакованный размер равен размеру файла.
///
/// Раскладка:
/// <code>
/// foo.hsm
///   metadata.json   версия формата, Guid, имя, описание, автор, даты
///   nodes.json      граф
///   templates/      шаблоны ЭТОГО макроса
///   submacro/       под-макросы
/// </code>
///
/// <see cref="MetadataEntry"/> и <see cref="GraphEntry"/> разнесены НЕ ради аккуратности:
/// метаданные обязаны прочитаться в тот момент, когда граф не читается, — тогда в библиотеке
/// видно нормальное имя с восклицательным знаком, а не «файл X — ошибка» (см. §13.1 спеки и
/// <see cref="MacroBundleReadResult"/>).
/// </summary>
public static class MacroBundleFormat
{
    /// <summary>Расширение файла бандла, с точкой. Сравнивать регистронезависимо — это NTFS.</summary>
    public const string Extension = ".hsm";

    /// <summary>
    /// Версия формата, которую эта сборка ПИШЕТ и единственная умеет читать.
    ///
    /// Ноль пишется в файл с самого первого дня, и в этом весь смысл поля: файл, переставший
    /// читаться, обязан давать «сделано другой версией формата», а не «файл повреждён» — это
    /// разные новости и разные действия пользователя. Больших скачков в рамках релиза не
    /// планируется, <c>v1</c> утверждается ближе к выпуску.
    /// </summary>
    public const int CurrentVersion = 0;

    /// <summary>Имя записи с метаданными.</summary>
    public const string MetadataEntry = "metadata.json";

    /// <summary>Имя записи с графом. Тот же диалект JSON, что и у <c>macros/*.json</c>.</summary>
    public const string GraphEntry = "nodes.json";

    /// <summary>
    /// Папка с шаблонами машинного зрения ЭТОГО макроса, со слешем на конце.
    /// Общего дерева <c>templates/</c> с волны F2 нет: каждый макрос верхнего уровня —
    /// остров, и цена этого (поправил шаблон — обойди все макросы) названа в §13.1.
    /// </summary>
    public const string TemplateFolder = "templates/";

    /// <summary>Расширение файла шаблона. Всё остальное внутри <see cref="TemplateFolder"/> шаблоном не считается.</summary>
    public const string TemplateExtension = ".png";

    /// <summary>
    /// Папка с под-макросами, со слешем на конце.
    ///
    /// Наполняет её волна F4; здесь имя папки существует затем, чтобы формат её ПРЕДУСМАТРИВАЛ
    /// уже сейчас — читатель перечисляет такие записи, писатель кладёт их обратно байт в байт,
    /// и бандл, собранный будущей версией, не теряет своих под-макросов, пройдя через
    /// сегодняшний код.
    /// </summary>
    public const string SubmacroFolder = "submacro/";

    /// <summary><c>true</c>, если имя файла похоже на бандл по расширению.</summary>
    public static bool HasBundleExtension(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Приводит путь внутри архива к виду, в котором он хранится: разделитель — прямой слеш.
    /// Вызывающий на Windows естественно напишет <c>classes\Лучник.png</c>, а в zip лежит
    /// <c>classes/Лучник.png</c>, и промах здесь выглядел бы как «шаблона нет».
    /// </summary>
    public static string NormalizeEntryPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// <c>true</c>, если относительный путь никуда не выводит за свою папку.
    ///
    /// В F1 из бандла ничего не распаковывается на диск, так что «zip slip» тут пока не
    /// эксплуатируется, — но перечислять такую запись как обычный шаблон значит однажды отдать
    /// её тому, кто будет распаковывать. Дешевле не пускать её в перечисление с самого начала.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizeEntryPath(path);
        if (normalized.Length == 0 || normalized.EndsWith('/'))
        {
            return false;
        }

        // Абсолютный путь и диск — не относительные пути по определению.
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            return false;
        }

        return !normalized.Split('/').Any(segment => segment is "..");
    }

    /// <summary>
    /// Разбирает путь внутри <see cref="TemplateFolder"/> в пару «набор + имя» — ту самую пару,
    /// которой шаблон называет нода.
    ///
    /// <b>Правило именования здесь ОДНО на весь проект</b>, и это главное, ради чего метод
    /// существует. Его читают трое: снимок шаблонов, который исполнитель кладёт в кэш; опись
    /// (<see cref="MacroTemplateInventory"/>), по которой валидатор ловит «нода называет шаблон,
    /// которого в бандле нет»; и каталог для браузера шаблонов в редакторе. Разъехавшись, они
    /// дали бы ровно ту ложь, из-за которой в D4 бейдж целей считали одной реализацией на два
    /// процесса.
    ///
    /// Раскладка досталась от упразднённого глобального дерева без единого изменения — <b>ни одно
    /// имя, которое несут ноды, при переезде в бандл не поменялось</b>:
    /// <code>
    ///   Find.png            → одиночный шаблон «Find»  (FindElement / WaitForElement)
    ///   classes/Лучник.png  → набор «classes», тег «Лучник»  (RecognizeTag)
    /// </code>
    /// Корень и подпапки — разные пространства имён: <c>Find</c> по имени тега из набора ничего не
    /// находит. Вложенность ГЛУБЖЕ одного уровня и любое расширение, кроме
    /// <see cref="TemplateExtension"/>, — не шаблон вовсе: назвать такую запись из ноды нечем, и
    /// перечислять её как шаблон значило бы обещать то, чего исполнитель не сделает.
    /// </summary>
    /// <param name="relativePath">Путь ОТНОСИТЕЛЬНО <see cref="TemplateFolder"/>.</param>
    /// <param name="set">Набор (подпапка) либо <c>null</c> — одиночный шаблон в корне.</param>
    /// <param name="name">Основа имени файла — то, что несёт нода (для набора это тег).</param>
    /// <returns><c>false</c>, если запись шаблоном не является.</returns>
    public static bool TryParseTemplatePath(string relativePath, out string? set, out string name)
    {
        set = null;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath) || !IsSafeRelativePath(relativePath))
        {
            return false;
        }

        var normalized = NormalizeEntryPath(relativePath);
        if (!normalized.EndsWith(TemplateExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Length > 2)
        {
            return false;
        }

        var stem = segments[^1][..^TemplateExtension.Length];
        if (stem.Length == 0)
        {
            return false;
        }

        set = segments.Length == 2 ? segments[0] : null;
        name = stem;
        return set is not { Length: 0 };
    }

    /// <summary>
    /// Обратное к <see cref="TryParseTemplatePath"/>: пара «набор + имя» в путь внутри
    /// <see cref="TemplateFolder"/>. Держится рядом с разбором, чтобы правило и его обращение
    /// нельзя было поправить порознь.
    /// </summary>
    public static string TemplatePath(string? set, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return set is null ? name + TemplateExtension : $"{set}/{name}{TemplateExtension}";
    }
}
