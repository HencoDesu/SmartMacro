using System.Text;

namespace SmartMacro.Io;

/// <summary>
/// Замена файла ЦЕЛИКОМ вместо записи поверх: сперва рядом пишется временный файл, затем он
/// одним переименованием СТАНОВИТСЯ боевым. Читатель либо увидит прежний файл, либо новый, но
/// никогда — половину.
///
/// <b>Живёт отдельно от бандла, потому что вызывающих двое и они из разных процессов.</b> Схема
/// была написана для <see cref="Macros.Bundle.MacroBundleWriter"/> (панель пишет <c>.hsm</c>), а
/// точно то же самое нужно демону для <c>settings.json</c>: у обоих файлов есть наблюдатель,
/// который поймает половину, и у обоих есть пользователь, чью работу усечённый файл уничтожит.
/// Второй экземпляр этой логики разъехался бы молча, а отказ проявлялся бы изредка и
/// невоспроизводимо — только когда файл в этот момент читают.
///
/// Схема:
/// <list type="number">
///   <item>записать целиком во временный файл <b>в той же папке</b> — замена атомарна только в
///     пределах тома, так что временный файл в <c>%TEMP%</c> обесценил бы всю затею;</item>
///   <item>заменить существующий файл этим временным.</item>
/// </list>
/// <b>Существующий файл во временный НЕ переименовывается.</b> Это выглядит как аккуратная
/// подстраховка («сохраним старый на случай сбоя»), а на деле создаёт окно, в котором файла нет
/// вовсе: наблюдатель, попавший в это окно, честно доложит, что макрос удалили, — и хоткей
/// снимется с регистрации.
///
/// ⚠️ <b>Замена делается <see cref="File.Replace(string,string,string?,bool)"/>, а НЕ
/// <see cref="File.Move(string,string,bool)"/>, и это измерено, а не вычитано.</b> Очевидный
/// выбор — <c>Move</c> с перезаписью, то есть <c>MoveFileEx</c> с <c>MOVEFILE_REPLACE_EXISTING</c>.
/// Он падает с <c>UnauthorizedAccessException</c>, если получателя кто-то держит открытым, —
/// причём падает ДАЖЕ ТОГДА, когда читатель честно открыл файл с <c>FileShare.Delete</c>.
/// <c>ReplaceFile</c> (за которым стоит <c>File.Replace</c>) в этой же ситуации отрабатывает.
/// Проверено на .NET 10 / Windows, все четыре сочетания:
/// <code>
///   Move    + FileShare.Read|Delete → UnauthorizedAccessException
///   Move    + FileShare.Read        → UnauthorizedAccessException
///   Replace + FileShare.Read|Delete → успех
///   Replace + FileShare.Read        → IOException «файл занят другим процессом»
/// </code>
/// Отсюда два следствия, и оба несущие. Первое: замена делается <c>Replace</c>. Второе:
/// <c>FileShare.Delete</c> у читателя (<see cref="Macros.Bundle.MacroBundleReader"/>) — не
/// украшение, а вторая половина того же механизма; сняв его, мы получим сохранение, которое падает
/// ровно тогда, когда файл в этот момент читают.
///
/// <c>ReplaceFile</c> требует существующего получателя, поэтому первая запись идёт обычным
/// переименованием: заменять там нечего, и открыть файл, которого ещё нет, тоже некому.
///
/// ⚠️ <b>Имя временного файла обязано ПРОМАХИВАТЬСЯ мимо фильтра наблюдателя за целевым файлом</b>,
/// иначе собственная запись поднимет лишнее событие — а у обоих сегодняшних вызывающих за таким
/// событием стоит перечитывание папки, перерегистрация хоткеев и сброс кэша шаблонов. Суффикс
/// <see cref="TempSuffix"/> добавляется К ПОЛНОМУ ИМЕНИ, и промахивается он в обеих формах:
/// длинное <c>foo.hsm.tmp</c> не подходит под <c>*.hsm</c>, а короткое 8.3 берёт первые три
/// символа ПОСЛЕДНЕГО расширения, то есть <c>FOOHSM~1.TMP</c>. Для <c>settings.json</c> фильтр —
/// само имя файла, и <c>settings.json.tmp</c> (8.3: <c>SETTIN~1.TMP</c>) не совпадает с ним ни в
/// одной из форм. Заводя ТРЕТЬЕГО вызывающего, проверьте это про его фильтр.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Суффикс временного файла. Публичен, потому что на него смотрят и наблюдатели за папками,
    /// и тесты.
    /// </summary>
    public const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Путь временного файла для целевого — В ТОЙ ЖЕ ПАПКЕ, см. довод в шапке класса.</summary>
    public static string TempPathFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path + TempSuffix;
    }

    /// <summary>
    /// Пишет файл атомарно: <paramref name="write"/> наполняет временный файл, затем тот заменяет
    /// боевой. Недописанный временный файл не остаётся.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var temp = TempPathFor(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                // Явный сброс до переименования: содержимое обязано быть на диске раньше, чем имя
                // начнёт обещать, что оно там есть.
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, path);
        }
        catch
        {
            // Недописанный временный файл не оставляем: под фильтр наблюдателя он не попадает, но
            // мусор рядом с рабочими файлами пользователь увидит и будет гадать, что это.
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Атомарно пишет текст в UTF-8 без BOM.</summary>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(path, stream => stream.Write(Utf8NoBom.GetBytes(text)));
    }

    /// <summary>
    /// То же асинхронно — для тех, кто пишет из обработчика запроса и не имеет права занимать
    /// поток на время ввода-вывода. Замена (<see cref="Replace"/>) остаётся синхронной: это один
    /// системный вызов, и асинхронной формы у него нет.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var temp = TempPathFor(path);
        try
        {
            await using (var stream = new FileStream(
                             temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Ставит готовый временный файл на место боевого — вторая половина атомарной записи.
    ///
    /// Отдельным методом ради вызывающего, который наполняет временный файл не потоком, а
    /// копированием: импорт кладёт в библиотеку чужой <c>.hsm</c> байт в байт, и «заменить
    /// существующий файл» у него ровно та же задача с теми же граблями (<c>ReplaceFile</c>, а не
    /// <c>MoveFileEx</c>; см. доводы в шапке класса).
    /// </summary>
    public static void Replace(string temp, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temp);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (File.Exists(path))
            {
                // ignoreMetadataErrors: замена не должна падать из-за того, что не удалось
                // перенести атрибуты или ACL (сетевой диск, чужая папка) — содержимое важнее.
                // Резервная копия не запрашивается: старый файл нам не нужен, а лишний «*.bak»
                // рядом с рабочими файлами пользователь заметит и будет гадать, что это.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (FileNotFoundException)
        {
            // Гонка: между проверкой и заменой файл успели удалить. Тогда это просто первая
            // запись, и переименования достаточно.
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Убирает временный файл, если он остался. Неудача — не беда: следующая запись ляжет по тому же имени.</summary>
    public static void TryDelete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не вышло — и ладно.
        }
    }
}
