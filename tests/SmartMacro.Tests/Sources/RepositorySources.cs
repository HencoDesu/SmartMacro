using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Общий вход для тестов, которые читают ИСХОДНЫЙ ТЕКСТ дерева, а не собранный код.
///
/// Такие тесты нужны потому, что часть правил этого проекта не выражается ни типом, ни
/// поведением: цветной эмодзи вместо глифа, срезанные хвосты букв, голый загрузчик XAML вместо
/// <c>InitializeComponent</c> — всё это собирается без единого предупреждения, проходит любой
/// поведенческий тест и обнаруживается только глазами на работающем приложении. Пока правило
/// живёт прозой в CLAUDE.md, оно держится на том, что следующий человек её прочтёт; здесь оно
/// держится на прогоне.
///
/// <b>Корень репозитория ищется подъёмом вверх до маркера, а не задаётся путём.</b> Сборка
/// тестов лежит в <c>tests\SmartMacro.Tests\bin\Debug\net10.0-windows\</c>, и сколько именно
/// уровней оттуда до корня — деталь раскладки MSBuild, которая менялась и ещё поменяется.
/// Маркером взят файл решения: он лежит ровно в корне, существует в любой ветке и не может
/// случайно оказаться в промежуточной папке — в отличие от <c>.git</c> (его нет в выгрузке
/// исходников), <c>src</c> (такая папка бывает вложенной) и <c>CLAUDE.md</c> (его копию можно
/// положить в подпроект).
/// </summary>
internal static class RepositorySources
{
    /// <summary>Файл, по которому опознаётся корень. Лежит ровно в одном месте дерева.</summary>
    private const string RootMarker = "SmartMacro.slnx";

    private static string? _root;
    private static IReadOnlyList<string>? _axamlFiles;
    private static IReadOnlyList<string>? _resxFiles;
    private static IReadOnlyList<string>? _allCSharpFiles;

    /// <summary>Абсолютный путь к корню репозитория.</summary>
    public static string Root => _root ??= LocateRoot();

    /// <summary>Вся разметка Avalonia в <c>src\</c>, кроме выхлопа сборки.</summary>
    public static IReadOnlyList<string> AxamlFiles =>
        _axamlFiles ??= EnumerateSources(Path.Combine(Root, "src"), "*.axaml");

    /// <summary>
    /// Все файлы ресурсов в <c>src\</c>. Появились вместе с выносом подписей из разметки: текст,
    /// который раньше стоял в <c>*.axaml</c>, лежит теперь здесь, и проверки, читающие ТО, ЧТО
    /// ВИДИТ ЧЕЛОВЕК, обязаны смотреть в оба места. Иначе вынос строки в ресурсы молча уводит её
    /// из-под проверки.
    /// </summary>
    public static IReadOnlyList<string> ResxFiles =>
        _resxFiles ??= EnumerateSources(Path.Combine(Root, "src"), "*.resx");

    /// <summary>Исходники C# одного проекта; <paramref name="projectFolder"/> — имя папки в <c>src\</c>.</summary>
    public static IReadOnlyList<string> CSharpFiles(string projectFolder) =>
        EnumerateSources(Path.Combine(Root, "src", projectFolder), "*.cs");

    /// <summary>Весь C# дерева <c>src\</c> — для проверок, которым не важно, чей это проект.</summary>
    public static IReadOnlyList<string> AllCSharpFiles =>
        _allCSharpFiles ??= EnumerateSources(Path.Combine(Root, "src"), "*.cs");

    /// <summary>Путь относительно корня — в сообщении об ошибке абсолютный только мешает.</summary>
    public static string Relative(string absolutePath) => Path.GetRelativePath(Root, absolutePath);

    /// <summary>
    /// Разметка как XML-дерево. Разбор именно XML, а не регулярками, даёт три вещи даром:
    /// комментарии становятся отдельным типом узла (и потому не попадают в проверки — иначе
    /// первым падал бы тот самый комментарий, в котором ловушка и описана), ссылки вида
    /// <c>&amp;#x25B8;</c> раскрываются в символ (в разметке подписи кнопок записаны и так, и
    /// напрямую — поиск по сырому тексту видел бы только вторые), а
    /// <see cref="LoadOptions.SetLineInfo"/> даёт номер строки для сообщения.
    /// </summary>
    public static XDocument LoadMarkup(string path) =>
        XDocument.Parse(File.ReadAllText(path), LoadOptions.SetLineInfo);

    /// <summary>Номер строки узла; 0, если разбор шёл без сведений о позиции.</summary>
    public static int LineOf(XObject node) =>
        node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    /// <summary>«файл:строка» для сообщения об ошибке.</summary>
    public static string Where(string file, XObject node) => $"{Relative(file)}:{LineOf(node)}";

    /// <summary>
    /// Текст C# с вычищенными комментариями (сохраняя переводы строк, чтобы номера строк не
    /// поехали).
    ///
    /// Нужно это ровно для одного: «упомянут» и «вызван» — разные вещи. Половина каталога IPC
    /// перечислена в xmldoc соседних констант и в <c>&lt;see cref&gt;</c> у обработчиков, так
    /// что поиск по сырому тексту засчитал бы живым конец, которого нет. Строковые литералы при
    /// этом СОХРАНЯЮТСЯ: вычищаются только комментарии, а не всё подряд, — и именно из-за
    /// литералов сканер обязан их различать, иначе <c>"avares://…"</c> внутри строки съел бы
    /// половину файла как комментарий до конца строки.
    /// </summary>
    public static string StripCommentsFromCSharp(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            // Обычный или интерполированный строковый литерал: '$' и '@' — обычные символы,
            // работа начинается с кавычки.
            if (c == '"')
            {
                i = CopyStringLiteral(source, i, result);
                continue;
            }

            if (c == '@' && Next(source, i) == '"')
            {
                i = CopyVerbatimLiteral(source, i, result);
                continue;
            }

            if (c == '\'')
            {
                i = CopyCharLiteral(source, i, result);
                continue;
            }

            if (c == '/' && Next(source, i) == '/')
            {
                i = BlankUntil(source, i, source.IndexOf('\n', i), result);
                continue;
            }

            if (c == '/' && Next(source, i) == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = BlankUntil(source, i, end < 0 ? -1 : end + 2, result);
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    private static char Next(string source, int i) => i + 1 < source.Length ? source[i + 1] : '\0';

    /// <summary>Копирует литерал как есть и возвращает позицию сразу за ним.</summary>
    private static int CopyStringLiteral(string source, int start, StringBuilder result)
    {
        // Сырой литерал ("""…"""): внутри него кавычки и слэши не значат ничего.
        if (Next(source, start) == '"' && start + 2 < source.Length && source[start + 2] == '"')
        {
            var close = source.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            var stop = close < 0 ? source.Length : close + 3;
            result.Append(source, start, stop - start);
            return stop;
        }

        var i = start + 1;
        while (i < source.Length)
        {
            if (source[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (source[i] == '"')
            {
                i++;
                break;
            }

            // Незакрытая кавычка до конца строки — такого в компилируемом коде не бывает, но
            // сканер не должен на этом уезжать в конец файла.
            if (source[i] == '\n')
            {
                break;
            }

            i++;
        }

        i = Math.Min(i, source.Length);
        result.Append(source, start, i - start);
        return i;
    }

    private static int CopyVerbatimLiteral(string source, int start, StringBuilder result)
    {
        var i = start + 2;
        while (i < source.Length)
        {
            if (source[i] != '"')
            {
                i++;
                continue;
            }

            // Удвоенная кавычка внутри @"…" — это экранированная кавычка, а не конец.
            if (i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                continue;
            }

            i++;
            break;
        }

        i = Math.Min(i, source.Length);
        result.Append(source, start, i - start);
        return i;
    }

    private static int CopyCharLiteral(string source, int start, StringBuilder result)
    {
        var i = start + 1;
        while (i < source.Length)
        {
            if (source[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (source[i] == '\'' || source[i] == '\n')
            {
                i++;
                break;
            }

            i++;
        }

        i = Math.Min(i, source.Length);
        result.Append(source, start, i - start);
        return i;
    }

    /// <summary>Заменяет кусок пробелами, оставляя переводы строк на месте.</summary>
    private static int BlankUntil(string source, int start, int end, StringBuilder result)
    {
        var stop = end < 0 ? source.Length : end;
        for (var i = start; i < stop; i++)
        {
            result.Append(source[i] == '\n' ? '\n' : ' ');
        }

        return stop;
    }

    private static string LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Не найден корень репозитория: подъём от «{AppContext.BaseDirectory}» вверх не встретил " +
            $"«{RootMarker}». Тесты папки Sources читают исходный текст дерева, поэтому запускать их " +
            "можно только из сборки, лежащей внутри репозитория. Если раскладка выхлопа изменилась — " +
            "менять надо маркер, а не путь.");
    }

    private static IReadOnlyList<string> EnumerateSources(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException(
                $"Нет папки «{folder}» — дерево исходников не там, где его ищет RepositorySources.");
        }

        return Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>В <c>bin\</c> и <c>obj\</c> лежат копии и порождённый код — читать их нельзя.</summary>
    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");
}
