using System.Text;
using SmartMacro.App.Macros;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;

namespace SmartMacro.Tests.ViewModels;

/// <summary>
/// Настоящая папка <c>macros/</c> во временном каталоге плюс <see cref="MacroLibrary"/> над ней.
///
/// <b>Настоящая, а не подделка, и это решение.</b> С волны F3 панель — единственный автор
/// макросов, и «сохранить» означает «записать zip и перечитать папку». Подделав библиотеку
/// интерфейсом, мы проверяли бы вместо этого собственный мок: атомарная замена, перенос шаблонов
/// при переименовании, «основа имени файла главнее поля Name» и нечитаемый бандл в списке — всё
/// это свойства ФАЙЛОВ, и заменителя у них нет. Файлы здесь — десятки килобайт, так что цена
/// нулевая.
///
/// Наблюдатель выключен: он перезагружает снимок с потока пула, а тесты view-model работают с
/// коллекциями синхронно. Сам наблюдатель проверяется отдельно, на настоящих задержках.
/// </summary>
internal sealed class TempLibrary : IDisposable
{
    private static readonly Lazy<MacroLibrary> SharedLibrary = new(CreateShared);

    /// <summary>
    /// Общая ПУСТАЯ библиотека для тестов, которым папка нужна лишь потому, что редактор без неё
    /// не собирается: раскладка канвы, бейдж целей, панель отладчика. Никто из них в неё не
    /// пишет, так что делить её безопасно, а заводить временную папку на каждый из полусотни
    /// тестов — платить за то, чего они не проверяют. Тестам, которым содержимое ВАЖНО, положен
    /// свой экземпляр.
    /// </summary>
    public static MacroLibrary Shared => SharedLibrary.Value;

    private static MacroLibrary CreateShared()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartmacro-tests-shared");
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        };
        return new MacroLibrary(root, watch: false);
    }

    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), $"smartmacro-panel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Library = new MacroLibrary(Root, watch: false);
    }

    /// <summary>Корень установки — то, что панель вычисляет через <c>InstallationLayout</c>.</summary>
    public string Root { get; }

    /// <summary>Библиотека над ним.</summary>
    public MacroLibrary Library { get; }

    /// <summary>Папка <c>macros/</c> внутри корня.</summary>
    public string FolderPath => Library.FolderPath;

    /// <summary>Путь к бандлу с таким именем — существует он или ещё нет.</summary>
    public string PathFor(string name) => MacroBundleFolder.PathFor(FolderPath, name);

    /// <summary>
    /// Пишет бандл МИМО библиотеки — так выглядит файл, положенный в папку проводником или чужой
    /// сборкой. Снимок после этого перечитывается вручную: наблюдатель здесь выключен.
    /// </summary>
    public void WriteExternally(MacroGraph graph, params (string Path, string Bytes)[] templates)
    {
        Directory.CreateDirectory(FolderPath);
        MacroBundleWriter.Write(PathFor(graph.Name), new MacroBundleContent
        {
            Metadata = MacroBundleMetadata.CreateNew(graph.Name),
            Graph = graph,
            Templates = [.. templates.Select(t => new MacroBundleFile(t.Path, Encoding.UTF8.GetBytes(t.Bytes)))],
        });
        Library.Refresh();
    }

    /// <summary>Кладёт в папку файл с расширением <c>.hsm</c>, бандлом не являющийся.</summary>
    public void WriteJunk(string stem, string content = "это вообще не zip")
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(PathFor(stem), content);
        Library.Refresh();
    }

    public void Dispose()
    {
        Library.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Уборка временных файлов — не то, что здесь проверяется.
        }
    }
}
