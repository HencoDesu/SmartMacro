using System.Text;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Одна строка папки <c>macros/</c>: файл <c>.hsm</c> ровно в том виде, в каком он прочитался.
///
/// <b>Нечитаемый бандл отсюда НЕ пропадает</b>, и в этом весь смысл записи. До волны F3 хранилище
/// демона молча пропускало такой файл, а панель узнавала о библиотеке через <c>GetMacros</c>, то
/// есть видела только уцелевшие макросы: файл существовал, но в интерфейсе его не было вовсе.
/// Теперь папку читает панель, и «файл лежит, но с ним беда» — это состояние строки, а не повод
/// её не показать (§5.7).
///
/// <b>Два независимых вердикта, и это тоже требование §5.7.</b> <see cref="Metadata"/> может
/// прочитаться, когда <see cref="Graph"/> — <c>null</c>: <c>metadata.json</c> и <c>nodes.json</c>
/// потому и разнесены. Тогда в библиотеке видно НАСТОЯЩЕЕ имя и описание с восклицательным знаком,
/// а не «файл X — ошибка», из которой не понять даже, какой это был макрос.
/// </summary>
/// <param name="Name">
/// Имя макроса = ОСНОВА ИМЕНИ ФАЙЛА, и она же идентичность (§5.7). Главенствует над полем
/// <c>Name</c> внутри бандла: переименовали файл проводником — значит переименовали макрос.
/// </param>
/// <param name="Path">Абсолютный путь к <c>.hsm</c>.</param>
/// <param name="Graph">Граф из <c>nodes.json</c> либо <c>null</c>, если он не прочитался.</param>
/// <param name="Metadata">Паспорт из <c>metadata.json</c> либо <c>null</c>, если не прочитался и он.</param>
/// <param name="TemplatePaths">
/// Пути шаблонов ОТНОСИТЕЛЬНО <see cref="MacroBundleFormat.TemplateFolder"/> — <c>classes/Лучник.png</c>,
/// а не <c>templates/classes/Лучник.png</c>. Без байтов.
/// </param>
/// <param name="Submacros">Под-макросы бандла, разобранные (волна F4). Пусто у макроса без них.</param>
/// <param name="SubmacroFaults">Записи <c>submacro/</c>, которые не разобрались, — по строке на каждую.</param>
/// <param name="Fault">Что помешало прочитать; <see cref="MacroBundleFault.None"/> — всё прочлось.</param>
/// <param name="FaultMessage">Вердикт читателя целиком, для показа и для журнала.</param>
/// <param name="NameOverridden">
/// Имя внутри бандла разошлось с основой имени файла, и победил файл. Флагом, а не строкой в
/// журнале, потому что <c>Shared</c> логгера не имеет и иметь не должен: сообщить об этом — дело
/// владельца снимка, а он в каждом процессе свой.
/// </param>
public sealed record MacroBundleEntry(
    string Name,
    string Path,
    MacroGraph? Graph,
    MacroBundleMetadata? Metadata,
    IReadOnlyList<string> TemplatePaths,
    IReadOnlyList<MacroSubmacro> Submacros,
    IReadOnlyList<string> SubmacroFaults,
    MacroBundleFault Fault,
    string? FaultMessage,
    bool NameOverridden = false)
{
    /// <summary>Граф на месте — макрос можно показать на canvas и выполнить.</summary>
    public bool IsReadable => Graph is not null;

    /// <summary>
    /// Опись шаблонов бандла — то, чем валидатор ловит «нода называет шаблон, которого нет».
    /// Считается один раз: запись неизменяемая, а спрашивают опись на каждое сохранение и на
    /// каждую проверку среды.
    /// </summary>
    public MacroTemplateInventory Templates { get; } = MacroTemplateInventory.FromPaths(TemplatePaths);

    /// <summary>
    /// Вердикт валидатора обо ВСЁМ бандле — граф, его под-макросы и всё, что между ними.
    ///
    /// Метод живёт здесь, а не у каждого вызывающего, ровно по правилу бейджа целей из D4: демон
    /// судит бандл при загрузке, панель — при показе строки библиотеки, и два прогона одного
    /// правила не имеют права разойтись. До F4 обе стороны писали
    /// <c>Validate(entry.Graph, entry.Templates)</c> буква в букву; с появлением второго и
    /// третьего аргумента такое совпадение перестало быть надёжным.
    /// </summary>
    /// <returns>Пусто у нечитаемого бандла: судить там не о чем.</returns>
    public IReadOnlyList<ValidationIssue> Validate() =>
        Graph is null ? [] : MacroGraphValidator.ValidateBundle(Graph, Submacros, Templates, SubmacroFaults);
}

/// <summary>
/// Чей файл лежит по целевому имени — ЕДИНСТВЕННОЕ, чего запись не может выяснить сама.
///
/// Личность макроса — это основа имени его файла (§5.7), поэтому «записать макрос «pw-login» в
/// <c>pw-login.hsm</c>» и «уничтожить чужой макрос» с точки зрения папки выглядят совершенно
/// одинаково. Различает их только тот, у кого открыт редактор: он один знает, что сейчас правят
/// «pw-buff», а имя ему поменяли на «pw-login».
///
/// До появления этого перечисления запись не спрашивала ни у кого: переименование в занятое имя
/// давало файл, где граф от одного макроса, а шаблоны и паспорт от другого, после чего исходный
/// файл удалялся — два макроса становились одним, и в статусе значилось «Сохранено».
/// </summary>
public enum MacroSaveTarget
{
    /// <summary>
    /// Файл под этим именем — ЭТОТ ЖЕ макрос: обычное пересохранение. Умолчание, и оно же
    /// правило идентичности «имя файла и есть личность»: у вызывающего, который про открытые
    /// редакторы ничего не знает (тесты, оснастка), другого разумного прочтения нет.
    /// </summary>
    Own,

    /// <summary>
    /// Файл под этим именем — ЧУЖОЙ макрос: отказаться, бросив
    /// <see cref="MacroNameTakenException"/> и не тронув ни байта. Так обязан звать всякий, чей
    /// макрос лежит под ДРУГИМ именем (переименование) или не лежит вовсе (черновик).
    /// </summary>
    Foreign,

    /// <summary>
    /// Файл под этим именем чужой, и пользователь СОГЛАСИЛСЯ его заменить. Целевой бандл
    /// перезаписывается целиком: наследовать от него нечего — «заменить» и означает, что того
    /// макроса больше нет. Вложения при этом переносятся из СВОЕГО бандла (см.
    /// <c>renamedFrom</c>), а не из уничтожаемого.
    /// </summary>
    ForeignReplace,
}

/// <summary>
/// Целевое имя занято чужим макросом, а разрешения его заменить не давали.
///
/// Наследуется от <see cref="IOException"/> намеренно: всякий, кто пишет в папку, ловит
/// файловые отказы и без нас, так что забывший о занятом имени вызывающий получит внятное
/// сообщение, а не необработанное исключение.
/// </summary>
public sealed class MacroNameTakenException : IOException
{
    public MacroNameTakenException(string name, string path)
        : base($"Макрос «{name}» в библиотеке уже есть.")
    {
        Name = name;
        Path = path;
    }

    /// <summary>Занятое имя.</summary>
    public string Name { get; }

    /// <summary>Путь к файлу, который стоит на пути.</summary>
    public string Path { get; }
}

/// <summary>
/// Бандл, который надо было прочитать перед перезаписью, не читается — и записать поверх него
/// значило бы уничтожить его содержимое.
///
/// Возникает ровно там, где цикл «прочитать всё → поменять одно → записать всё» не может
/// выполнить первый шаг: файл занят, <c>nodes.json</c> испорчен при целом паспорте, бандл сделан
/// будущей версией формата. Раньше все три были неотличимы от «файла нет», и запись шла дальше с
/// пустым набором шаблонов.
/// </summary>
public sealed class MacroBundleUnreadableException : IOException
{
    public MacroBundleUnreadableException(string path, MacroBundleFault fault, string? verdict)
        : base($"Бандл «{path}» не читается, а перезапись уничтожила бы его содержимое. " +
               (verdict ?? "Причина неизвестна."))
    {
        Path = path;
        Fault = fault;
    }

    /// <summary>Путь к нечитаемому бандлу.</summary>
    public string Path { get; }

    /// <summary>Вердикт читателя — разные значения требуют от пользователя разных действий.</summary>
    public MacroBundleFault Fault { get; }
}

/// <summary>
/// Папка <c>macros/</c> как библиотека: перечислить, прочитать, записать, удалить, импортировать.
///
/// <b>Живёт в <c>Shared</c>, потому что с волны F3 в эту папку смотрят ОБА процесса — и по-разному.</b>
/// Панель здесь единственный автор (редактор в ней, значит она и пишет), демон — только читатель
/// и исполнитель. Макрос перестал ходить по трубе вовсе: процессы делят файловую систему, так что
/// импорт — это копирование файла, а не запрос. Две реализации одного чтения разъехались бы ровно
/// тем способом, из-за которого в D4 бейдж целей считают одной реализацией на два процесса: панель
/// показала бы одно, движок выполнил бы другое.
///
/// Что здесь ЕСТЬ и почему именно здесь:
/// <list type="bullet">
///   <item><b>Правило «основа имени файла главнее поля Name»</b> — иначе панель и демон по-разному
///     назвали бы один и тот же макрос.</item>
///   <item><b>Проверка имени по правилам NTFS</b> — имя И ЕСТЬ основа имени файла, и панель обязана
///     отказать по тому же правилу, по которому упала бы запись.</item>
///   <item><b>Цикл «прочитать всё → поменять одно → записать всё»</b> — писатель умеет только файл
///     целиком (это условие атомарной замены), поэтому сохранение графа обязано переносить шаблоны,
///     под-макросы и паспорт. Забыть это значит стереть шаблоны, то есть ровно то, ради чего бандл
///     и заведён.</item>
/// </list>
///
/// Чего здесь НЕТ: наблюдателя за папкой и снимка. Это состояние, а не арифметика над файлами, и
/// живёт оно у владельца — <c>MacroGraphStore</c> в демоне, <c>MacroLibrary</c> в панели.
/// </summary>
public static class MacroBundleFolder
{
    /// <summary>Имя папки с макросами относительно корня установки.</summary>
    public const string FolderName = "macros";

    /// <summary>
    /// Маска бандлов — она же фильтр наблюдателя у обоих владельцев. Временный файл атомарной
    /// записи (<c>{имя}.hsm.tmp</c>) под неё не попадает ни длинным именем, ни коротким 8.3
    /// («FOOHSM~1.TMP»: 8.3 берёт первые три символа ПОСЛЕДНЕГО расширения) — см.
    /// <see cref="MacroBundleWriter"/>.
    /// </summary>
    public const string Filter = "*" + MacroBundleFormat.Extension;

    /// <summary>Папка с макросами внутри корня установки.</summary>
    public static string In(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return System.IO.Path.Combine(root, FolderName);
    }

    /// <summary>Путь к бандлу макроса с таким именем — существует он или ещё нет.</summary>
    public static string PathFor(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return System.IO.Path.Combine(directory, name + MacroBundleFormat.Extension);
    }

    /// <summary>
    /// Проверяет имя макроса по правилам имён файлов NTFS: имя И ЕСТЬ основа имени файла.
    ///
    /// Один экземпляр правила на оба процесса. Панель проверяет до записи (чтобы сказать «имя не
    /// может содержать /», а не показать невнятно упавший ввод-вывод), демон — потому что читает
    /// то, что ему положили.
    /// </summary>
    /// <param name="name">Проверяемое имя.</param>
    /// <returns>Описание ошибки или <c>null</c>, если имя годится.</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Имя макроса не может быть пустым.";
        }

        if (name.Length > 100)
        {
            return "Имя макроса должно быть не длиннее 100 символов.";
        }

        var invalid = name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars());
        if (invalid >= 0)
        {
            return $"Имя макроса не может содержать «{name[invalid]}».";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "Имя макроса не может заканчиваться точкой или пробелом.";
        }

        if (IsReservedDeviceName(name))
        {
            return $"«{name}» — зарезервированное имя устройства Windows.";
        }

        return null;
    }

    /// <summary>
    /// Все бандлы папки, по имени. НЕ БРОСАЕТ: нечитаемая папка даёт пустой список, нечитаемый
    /// бандл — строку с <see cref="MacroBundleEntry.Fault"/>. Одна правка руками не имеет права
    /// опустошить библиотеку и не имеет права спрятать испорченный файл.
    /// </summary>
    public static IReadOnlyList<MacroBundleEntry> Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var entries = new List<MacroBundleEntry>();
        foreach (var path in Enumerate(directory))
        {
            entries.Add(ReadEntry(path));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    /// <summary>Пути всех <c>*.hsm</c> папки в устойчивом порядке; пусто, если папку не прочитать.</summary>
    public static IReadOnlyList<string> Enumerate(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            return
            [
                .. Directory.EnumerateFiles(directory, Filter)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Читает один бандл в строку библиотеки. НЕ БРОСАЕТ — читатель бандла не бросает тоже, а
    /// нечитаемый файл здесь нормальный исход, а не исключительный.
    ///
    /// Расхождение имени внутри бандла с основой имени файла разрешается в пользу ФАЙЛА: граф
    /// подменяется копией с именем-стемом. Иначе переименование файла проводником оставляло бы
    /// макрос, который в списке зовётся одним именем, а хоткеем запускается под другим.
    /// </summary>
    public static MacroBundleEntry ReadEntry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var read = MacroBundleReader.Read(path);

        // Читатель различает «повреждён», «занят» и «сделан другой версией формата» — переносим
        // его вердикт целиком, не пытаясь пересказать своими словами.
        var fault = read.Metadata.IsOk ? read.GraphFault : read.Metadata.Fault;
        var message = read.Metadata.IsOk ? read.GraphMessage : read.Metadata.Message;

        var graph = read.Graph;
        var overridden = graph is not null && !string.Equals(graph.Name, stem, StringComparison.Ordinal);
        if (overridden && graph is not null)
        {
            graph = new MacroGraph
            {
                Name = stem,
                Triggers = graph.Triggers,
                StartNodeId = graph.StartNodeId,
                Nodes = graph.Nodes,
            };
        }

        return new MacroBundleEntry(
            stem,
            path,
            graph,
            read.Metadata.Metadata,
            read.TemplatePaths,
            read.Submacros,
            read.SubmacroFaults,
            fault,
            message,
            overridden);
    }

    /// <summary>
    /// Пишет граф в <c>{directory}/{graph.Name}.hsm</c> — атомарно, не теряя вложений и не
    /// уничтожая чужого макроса.
    ///
    /// <b>Пишется бандл целиком, а меняется в нём только граф.</b> Шаблоны, под-макросы и паспорт
    /// (в том числе <see cref="MacroBundleMetadata.Id"/> и дату создания) читаем из существующего
    /// файла и кладём обратно; меняется лишь дата правки.
    ///
    /// <b>Вложения наследуются от СВОЕГО бандла, а не от того, что лежит по целевому пути.</b>
    /// Раньше читалось наоборот — сперва цель, потом <paramref name="renamedFrom"/>, — и
    /// переименование в занятое имя давало гибрид: граф от переименованного макроса, шаблоны и
    /// паспорт от затираемого. Свой бандл — это <paramref name="renamedFrom"/>, если это
    /// переименование, и целевой файл, если мы просто пересохраняемся под своим же именем; у
    /// черновика своего бандла нет вовсе, и наследовать ему не от чего.
    ///
    /// <b>Два отказа, и оба — про потерю данных.</b> Занятое чужим макросом имя (см.
    /// <see cref="MacroSaveTarget"/>) и нечитаемый СВОЙ бандл (см.
    /// <see cref="MacroBundleContentResult"/>): «файла нет» — единственный отказ чтения, после
    /// которого писать с пустыми вложениями законно.
    /// </summary>
    /// <param name="directory">Папка с макросами; создаётся, если её ещё нет.</param>
    /// <param name="graph">Сохраняемый граф; его имя становится основой имени файла.</param>
    /// <param name="submacros">
    /// Новый набор под-макросов (волна F4) либо <c>null</c> — «оставить те, что в файле».
    ///
    /// Различие несущее. <c>null</c> нужен всякому, кто про под-макросы не знает и знать не должен
    /// (правка шаблона, скажем); список — редактору, который держит их все и пишет бандл целиком.
    /// Пустой список означает ровно «под-макросов больше нет» и стирает их, а <c>null</c> — нет.
    /// </param>
    /// <param name="renamedFrom">
    /// Прежнее имя, если это переименование. Без него переименование теряло бы шаблоны: редактор
    /// переименовывает записью под новым именем и удалением старого файла (именно в таком порядке
    /// — сбой между шагами обязан оставить две копии, а не ноль), а под новым именем бандла ещё
    /// нет, и наследовать вложения не от чего.
    /// </param>
    /// <param name="target">
    /// Чей файл лежит по целевому имени. Умолчание — «наш же» (обычное пересохранение); всё
    /// остальное обязан сказать тот, кто знает больше, — см. <see cref="MacroSaveTarget"/>.
    /// </param>
    /// <exception cref="ArgumentException">Имя графа не годится в качестве имени файла.</exception>
    /// <exception cref="MacroNameTakenException">Имя занято чужим макросом, заменять не разрешали.</exception>
    /// <exception cref="MacroBundleUnreadableException">Свой бандл есть, но не читается.</exception>
    public static void Save(
        string directory,
        MacroGraph graph,
        IReadOnlyList<MacroSubmacro>? submacros = null,
        string? renamedFrom = null,
        MacroSaveTarget target = MacroSaveTarget.Own)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(graph);
        if (ValidateName(graph.Name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(graph));
        }

        Directory.CreateDirectory(directory);
        var path = PathFor(directory, graph.Name);

        if (target == MacroSaveTarget.Foreign && File.Exists(path))
        {
            throw new MacroNameTakenException(graph.Name, path);
        }

        // СВОЙ бандл: переименование несёт прежнее имя, обычное пересохранение — это целевой
        // файл, а у черновика (и у согласованной замены чужого) своего бандла нет.
        var ownPath = renamedFrom is not null
            ? PathFor(directory, renamedFrom)
            : target == MacroSaveTarget.Own ? path : null;

        var previous = ownPath is null ? null : ReadOwnContent(ownPath);

        MacroBundleWriter.Write(path, new MacroBundleContent
        {
            Metadata = previous?.Metadata.Touch() ?? MacroBundleMetadata.CreateNew(graph.Name),
            Graph = graph,
            Templates = previous?.Templates ?? [],
            Submacros = submacros is null
                ? previous?.Submacros ?? []
                : MergeSubmacros(previous?.Submacros ?? [], submacros),
            Extras = previous?.Extras ?? [],
        });
    }

    /// <summary>
    /// Кладёт новый набор под-макросов поверх содержимого папки <c>submacro/</c>, СОХРАНЯЯ всё,
    /// что под-макросом не является.
    ///
    /// Замена подчищает только те записи, чьё имя разбирается по правилу
    /// <see cref="MacroBundleFormat.TryParseSubmacroPath"/>. Остальное — заметка автора рядом,
    /// файл будущей версии формата — переносится как было: потерять его значило бы сделать ровно
    /// ту потерю, ради недопущения которой бандл и заведён.
    /// </summary>
    private static IReadOnlyList<MacroBundleFile> MergeSubmacros(
        IReadOnlyList<MacroBundleFile> existing,
        IReadOnlyList<MacroSubmacro> submacros)
    {
        var files = existing
            .Where(file => !MacroBundleFormat.TryParseSubmacroPath(file.Path, out _))
            .ToList();

        foreach (var submacro in submacros)
        {
            files.Add(new MacroBundleFile(
                MacroBundleFormat.SubmacroPath(submacro.Id),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(MacroGraphJson.Serialize(submacro.Graph))));
        }

        return files;
    }

    /// <summary>Удаляет бандл макроса. <c>false</c> — такого файла нет (это не ошибка).</summary>
    public static bool Delete(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (ValidateName(name) is not null)
        {
            return false;
        }

        var path = PathFor(directory, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Правит набор шаблонов бандла тем же циклом «прочитать всё → поменять одно → записать всё».
    ///
    /// Бандл переписывается ЦЕЛИКОМ, так что цена добавления одного PNG равна цене сохранения
    /// макроса. Для файлов в десятки килобайт это ничто, а взамен «добавили шаблон» ничем не
    /// отличается от «сохранили граф»: тот же временный файл, та же замена, то же событие.
    /// </summary>
    /// <param name="directory">Папка с макросами.</param>
    /// <param name="name">Макрос, чей бандл правим.</param>
    /// <param name="edit">
    /// Новый набор шаблонов по старому либо <c>null</c>, если менять нечего (тогда файл не
    /// переписывается вовсе).
    /// </param>
    /// <returns><c>false</c>, если макроса нет или правка ничего не меняет.</returns>
    /// <exception cref="MacroBundleUnreadableException">
    /// Бандл есть, но не читается. Здесь тот же довод, что и у записи графа: файл переписывается
    /// ЦЕЛИКОМ, значит правка одного PNG в нечитаемом бандле уничтожила бы все остальные. Раньше
    /// это возвращалось тем же <c>false</c>, что и «такого макроса нет», и в интерфейс попадало
    /// одно и то же невнятное объяснение на два совершенно разных случая.
    /// </exception>
    public static bool EditTemplates(
        string directory,
        string name,
        Func<IReadOnlyList<MacroBundleFile>, IReadOnlyList<MacroBundleFile>?> edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(edit);
        if (ValidateName(name) is not null)
        {
            return false;
        }

        var path = PathFor(directory, name);
        if (ReadOwnContent(path) is not { } content)
        {
            return false;
        }

        if (edit(content.Templates) is not { } templates)
        {
            return false;
        }

        MacroBundleWriter.Write(path, content with
        {
            Metadata = content.Metadata.Touch(),
            Templates = templates,
        });
        return true;
    }

    /// <summary>
    /// Кладёт чужой <c>.hsm</c> в папку — это и есть импорт целиком.
    ///
    /// <b>Копирование, а не разбор с пересборкой</b>, и это не лень: бандл существует ровно затем,
    /// чтобы отданный файл работал у получателя байт в байт. Пропустив его через сегодняшнего
    /// писателя, мы бы потеряли всё, чего сегодняшняя версия формата не знает, — то есть сделали
    /// бы ровно ту потерю, от которой формат защищает.
    ///
    /// <b>Занятое имя — вопрос к ЧЕЛОВЕКУ, а не к программе.</b> Раньше импорт молча брал
    /// свободное имя с суффиксом. Вариант остался (<see cref="FreeName"/> никуда не делся, и
    /// вызывающий передаёт его сюда как <paramref name="targetName"/>), но выбирать между «взять
    /// свободное имя» и «заменить существующий макрос» программа за пользователя не должна: цена
    /// второго — чужие шаблоны, а цена первого — библиотека, в которой лежат «pw-login» и
    /// «pw-login-2», и никто уже не помнит, чем они отличаются.
    ///
    /// Основа имени файла главнее поля <c>Name</c> внутри, так что скопированный бандл честно
    /// назовётся новым именем.
    /// </summary>
    /// <param name="directory">Папка с макросами; создаётся, если её ещё нет.</param>
    /// <param name="sourcePath">Путь к импортируемому файлу.</param>
    /// <param name="targetName">
    /// Имя, под которым положить, либо <c>null</c> — «под своим», то есть под основой имени
    /// исходного файла.
    /// </param>
    /// <param name="replace">Заменить существующий макрос с таким именем. Только по согласию человека.</param>
    /// <returns>Имя, под которым макрос лёг в библиотеку.</returns>
    /// <exception cref="ArgumentException">Файл не похож на бандл либо его имя не годится для NTFS.</exception>
    /// <exception cref="MacroNameTakenException">Имя занято, а заменять не разрешали.</exception>
    public static string Import(
        string directory,
        string sourcePath,
        string? targetName = null,
        bool replace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        // Достаточно того, что файл ОТКРЫВАЕТСЯ бандлом и в нём есть паспорт. Версию не проверяем:
        // бандл будущей версии — это законный файл, который просто не прочтёт ЭТА сборка, и
        // отказать ему в месте на диске значило бы соврать ровно тем способом, ради недопущения
        // которого поле версии и заведено. В библиотеке он встанет строкой с пометкой.
        var metadata = MacroBundleReader.ReadMetadata(sourcePath);
        if (metadata.Fault is MacroBundleFault.Missing or MacroBundleFault.NotAnArchive
            or MacroBundleFault.EntryMissing)
        {
            throw new ArgumentException(
                metadata.Message ?? $"«{sourcePath}» — не бандл SmartMacro.", nameof(sourcePath));
        }

        var name = targetName ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        if (ValidateName(name) is { } nameError)
        {
            throw new ArgumentException(
                nameError,
                targetName is null ? nameof(sourcePath) : nameof(targetName));
        }

        Directory.CreateDirectory(directory);
        var path = PathFor(directory, name);
        if (File.Exists(path))
        {
            if (!replace)
            {
                throw new MacroNameTakenException(name, path);
            }

            // Замена идёт тем же механизмом, что и сохранение: копия рядом, затем ReplaceFile.
            // Прямой File.Copy(overwrite: true) отказал бы ровно тогда, когда бандл в этот момент
            // читает демон, — читатель открывает файл с FileShare.Read|Delete, а перезапись
            // просит доступ на запись.
            var temp = path + MacroBundleWriter.TempSuffix;
            try
            {
                File.Copy(sourcePath, temp, overwrite: true);
                MacroBundleWriter.PlaceAtomically(temp, path);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return name;
        }

        File.Copy(sourcePath, path);
        return name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не вышло — и ладно: следующая запись этого макроса ляжет по тому же имени.
        }
    }

    /// <summary>
    /// Свободное имя: <paramref name="stem"/>, а если занято — <c>stem-2</c>, <c>stem-3</c>, …
    /// Регистр не различаем: это NTFS.
    ///
    /// Правило суффикса осталось прежним, а вот применяет его теперь не программа: имя
    /// предлагается человеку в вопросе о занятом имени, и он решает, брать ли его вместо замены.
    /// </summary>
    public static string FreeName(string directory, string stem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        if (!File.Exists(PathFor(directory, stem)))
        {
            return stem;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}";
            if (!File.Exists(PathFor(directory, candidate)))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Читает СВОЙ бандл перед перезаписью. <c>null</c> — файла нет, и это ЕДИНСТВЕННЫЙ отказ, на
    /// котором писать законно: наследовать не от чего, потому что наследовать неоткуда.
    ///
    /// Всякий другой отказ — это отказ записи целиком. Прежде здесь стояла проверка
    /// <c>File.Exists</c>, и «файл есть, но не прочитался» было неотличимо от «файла нет»: занятый
    /// на миг файл, испорченный <c>nodes.json</c> при целом паспорте и бандл БУДУЩЕЙ версии
    /// формата все трое получали новый <c>Guid</c> и пустой набор шаблонов. Различать это умеет сам
    /// читатель — он и различает.
    /// </summary>
    private static MacroBundleContent? ReadOwnContent(string path)
    {
        var read = MacroBundleReader.ReadContent(path);
        if (read.Content is { } content)
        {
            return content;
        }

        return read.IsMissing
            ? null
            : throw new MacroBundleUnreadableException(path, read.Fault, read.Message);
    }

    private static bool IsReservedDeviceName(string name)
    {
        // CON, PRN, AUX, NUL, COM0-9, LPT0-9 — в Windows негодны как основы имён файлов.
        var stem = name.Split('.')[0];
        if (stem is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && char.IsAsciiDigit(stem[3]);
    }
}
