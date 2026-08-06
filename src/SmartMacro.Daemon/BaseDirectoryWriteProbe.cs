using System.Globalization;
using SmartMacro.Resources;

namespace SmartMacro.Daemon;

/// <summary>
/// Проба пера в каталоге поставки: можно ли туда писать.
///
/// Раскладка портативная — всё состояние лежит В ПАПКЕ ПРОГРАММЫ, а не в
/// <c>%LOCALAPPDATA%</c>: <c>macros\*.json</c> и <c>settings.json</c> в корне, свой
/// <c>logs\smartmacro-*.log</c> у демона, <c>debug\</c> со снимками — в корне. Один путь на всё,
/// папку можно перенести или скопировать целиком. Плата — вся портативность держится на том, что
/// распаковали туда, куда разрешено писать: в <c>C:\Program Files</c> или на сетевой шаре без
/// прав не запишется НИЧЕГО, причём молча.
///
/// Каталогов, которые надо проверить, ДВА, и вызывающий проверяет оба (см. <c>Program.Main</c>):
/// корень установки, где данные пользователя, и своя папка демона, где <c>logs\</c> — то есть
/// первое, что создаст логгер, когда сообщать о беде будет уже нечем.
///
/// Молча — это не фигура речи. Сток File у Serilog глотает отказ и уходит в SelfLog, макрос
/// «сохранён» из панели вернул бы ошибку только на попытке записи, а <c>debug\</c> не появился
/// бы вовсе. Пользователь узнал бы об этом через час, по не тому симптому.
///
/// Отсюда решение: <b>отказ фатален, режима «только чтение» нет</b>. Демон затем и резидентный,
/// чтобы писать: библиотека макросов, журнал и дампы — не украшения, а его работа. Живая иконка
/// в трее над движком, который не может сохранить ни строчки, — обещание, которого он не
/// сдержит, и худшая из двух неудач: непонятный сбой через десять минут вместо внятного отказа
/// на первой секунде. Чинится это не в программе, а распаковкой в другое место, поэтому и
/// сказать нужно ровно это.
/// </summary>
public static class BaseDirectoryWriteProbe
{
    /// <summary>
    /// Пытается создать подкаталог и файл в нём, затем убирает за собой.
    /// </summary>
    /// <param name="directory">Каталог, откуда запущен процесс, — обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="failure">Причина отказа от файловой системы; <c>null</c>, если всё хорошо.</param>
    /// <returns><c>true</c>, если писать можно.</returns>
    /// <remarks>
    /// Проверяются ОБА права, а не одно: создание файла в каталоге (<c>FILE_ADD_FILE</c>) и
    /// создание подкаталога (<c>FILE_ADD_SUBDIRECTORY</c>) — разные биты ACL, и демону нужны оба
    /// (<c>logs\</c> и <c>macros\</c> он заводит сам). Проба, писавшая бы только файл, пропустила
    /// бы половину случаев.
    ///
    /// Имя пробы содержит идентификатор процесса: второй экземпляр демона доходит до этой строки
    /// раньше, чем упрётся в мьютекс, и два одновременных запуска не должны спорить за одно имя.
    /// </remarks>
    public static bool TryVerifyWritable(string directory, out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var probeDirectory = Path.Combine(directory, $".smartmacro-write-probe-{Environment.ProcessId}");
        var probeFile = Path.Combine(probeDirectory, "probe.tmp");

        try
        {
            Directory.CreateDirectory(probeDirectory);
            File.WriteAllText(probeFile, "smartmacro");
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            // Уборка не обязана удаться и не влияет на вердикт: если пробу создать получилось, а
            // удалить нет, писать в каталог всё равно можно — а мусор с именем, которое прямо
            // говорит, что это, найдётся глазами.
            try
            {
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Намеренно проглатываем.
            }
        }
    }

    /// <summary>
    /// Текст для окна с ошибкой. Отдельно от <see cref="TryVerifyWritable"/>, потому что проба
    /// должна быть тестируемой без Win32.
    /// </summary>
    public static string DescribeFailure(string directory, string? failure) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.Startup_WriteProbe_Failed,
            directory,
            failure ?? Strings.Startup_WriteProbe_ReasonUnknown);
}
