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
    /// Общего дерева <c>templates/</c> после волны F2 не будет: каждый макрос верхнего уровня —
    /// остров, и цена этого (поправил шаблон — обойди все макросы) названа в §13.1.
    /// </summary>
    public const string TemplateFolder = "templates/";

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
}
