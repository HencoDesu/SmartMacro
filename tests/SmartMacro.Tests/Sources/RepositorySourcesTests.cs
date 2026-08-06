namespace SmartMacro.Tests.Sources;

/// <summary>
/// Проверки самой оснастки чтения исходников.
///
/// Тесты этой папки утверждают что-то о дереве, поэтому их собственный отказ должен выглядеть
/// как отказ, а не как зелёный прогон по пустому списку. Отсюда две вещи: подъём до корня обязан
/// находить корень, а перечисление разметки — не быть пустым.
///
/// Отдельно проверяется очистка комментариев. Она нужна ровно там, где «упомянут» и «вызван» —
/// разные вещи, и ошибиться в ней можно молча в обе стороны: съесть половину файла, наткнувшись
/// на <c>//</c> внутри строкового литерала, или наоборот засчитать живым конец протокола,
/// который на самом деле только описан в xmldoc.
/// </summary>
public class RepositorySourcesTests
{
    [Test]
    public async Task TheRootIsFoundByClimbingToTheSolutionFile()
    {
        await Assert.That(File.Exists(Path.Combine(RepositorySources.Root, "SmartMacro.slnx"))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(RepositorySources.Root, "src"))).IsTrue();
    }

    [Test]
    public async Task TheMarkupInventoryIsNotEmptyAndSkipsBuildOutput()
    {
        var files = RepositorySources.AxamlFiles;

        await Assert.That(files.Count).IsGreaterThan(0);
        await Assert.That(files.All(File.Exists)).IsTrue();

        // В bin\ и obj\ лежат копии той же разметки: посчитав их, проверки удвоили бы каждую
        // находку и однажды упали бы на файле, которого в дереве уже нет.
        var fromBuildOutput = files
            .Select(RepositorySources.Relative)
            .Where(path => path.Contains(@"\bin\", StringComparison.Ordinal)
                        || path.Contains(@"\obj\", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(fromBuildOutput.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CSharpFilesAreEnumeratedPerProject()
    {
        var core = RepositorySources.CSharpFiles("SmartMacro.Core");

        await Assert.That(core.Count).IsGreaterThan(0);
        await Assert.That(core.All(path => path.Contains("SmartMacro.Core", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task TheResourceInventoryIsNotEmpty()
    {
        // Страховка от тихого сужения: проверка глифов читает разметку, РЕСУРСЫ и код, и
        // сломать её теперь можно, не трогая саму проверку, — достаточно чтобы перечисление
        // перестало что-либо находить. Тогда вынесенная в resx подпись снова поедет мимо.
        var files = RepositorySources.ResxFiles;

        await Assert.That(files.Count).IsGreaterThan(0);
        await Assert.That(files.All(File.Exists)).IsTrue();
    }

    [Test]
    public async Task TheWholeTreeOfCSharpIsWiderThanOneProject()
    {
        var all = RepositorySources.AllCSharpFiles;
        var app = RepositorySources.CSharpFiles("SmartMacro.App");

        await Assert.That(all.Count).IsGreaterThan(app.Count);
        await Assert.That(app.All(all.Contains)).IsTrue();
    }

    [Test]
    public async Task LineAndBlockCommentsGoAway()
    {
        var stripped = RepositorySources.StripCommentsFromCSharp(
            "var a = 1; // IpcMessageTypes.GetWindows\n" +
            "/* IpcMessageTypes.AddTag */ var b = 2;\n" +
            "/// <see cref=\"IpcMessageTypes.RemoveTag\"/>\n" +
            "var c = 3;\n");

        await Assert.That(stripped).DoesNotContain("IpcMessageTypes");
        await Assert.That(stripped).Contains("var a = 1;");
        await Assert.That(stripped).Contains("var b = 2;");
        await Assert.That(stripped).Contains("var c = 3;");
    }

    [Test]
    public async Task ASlashPairInsideAStringIsNotAComment()
    {
        // Тот самый случай, ради которого сканер вообще различает литералы: «avares://…» в этом
        // дереве встречается, и наивная очистка съела бы остаток строки.
        var stripped = RepositorySources.StripCommentsFromCSharp(
            "var uri = \"avares://SmartMacro/Assets/icon.png\"; var next = IpcMessageTypes.GetWindows;\n");

        await Assert.That(stripped).Contains("avares://SmartMacro/Assets/icon.png");
        await Assert.That(stripped).Contains("IpcMessageTypes.GetWindows");
    }

    [Test]
    public async Task VerbatimStringsAndCharLiteralsDoNotDerailTheScanner()
    {
        var stripped = RepositorySources.StripCommentsFromCSharp(
            "var path = @\"C:\\a\\b\"; var quote = '\"'; var live = IpcMessageTypes.AddTag; // хвост\n");

        await Assert.That(stripped).Contains(@"C:\a\b");
        await Assert.That(stripped).Contains("IpcMessageTypes.AddTag");
        await Assert.That(stripped).DoesNotContain("хвост");
    }

    [Test]
    public async Task StrippingKeepsTheLineNumbering()
    {
        // Номера строк идут в сообщения об ошибках, поэтому вычищенный текст обязан сохранять
        // столько же переводов строки, сколько было.
        const string source = "var a = 1;\n/* один\n   два\n   три */\nvar b = 2;\n";

        var stripped = RepositorySources.StripCommentsFromCSharp(source);

        await Assert.That(stripped.Count(ch => ch == '\n')).IsEqualTo(source.Count(ch => ch == '\n'));
    }
}
