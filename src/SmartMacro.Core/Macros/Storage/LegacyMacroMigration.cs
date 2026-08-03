using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Одноразовое преобразование <c>macros.json</c> времён до W0.2 (единый файл с именованными
/// макросами, каждый из которых — словарь «тег → плоский список действий») в графы нод.
///
/// Как ложатся формы:
///   * ОДИН теговый блок → один цепочный граф с именем макроса. Каждая нода действия несёт
///     <c>Target = RequireTags:[tag]</c>, а это воспроизводит прежнюю семантику рассылки
///     («эти действия выполняет каждое окно с этим тегом») и сохраняет граф запускаемым и с
///     хоткея, и из UI, — а ни тот ни другой контекст-окна не дают.
///   * МНОГО теговых блоков → граф-диспетчер с именем макроса: цепочка <c>RunMacroNode</c>, по
///     одной на тег, каждая с селектором <c>RequireTags:[tag]</c>, указывающая на порождённый
///     граф <c>{макрос}-{тег}</c>. В потеговых графах лежит исходная цепочка с нодами БЕЗ ЦЕЛИ,
///     потому что разветвление по селектору у диспетчера выдаёт каждому под-прогону собственное
///     контекст-окно.
///
/// Хоткеи раньше жили в отдельном <c>hotkeys.json</c>. Его <c>MacroBindings</c> («имя макроса →
/// аккорд») становятся <see cref="HotkeyTrigger"/> у соответствующего мигрированного графа, и
/// это ровно то объединение, ради которого волна и затевалась: привязка теперь живёт в том
/// макросе, который она запускает. А его <c>Bindings</c> (аккорды для зашитых в код рассылок)
/// приткнуть некуда — эти поведения теперь суть примеры <c>pw-*</c>, — поэтому их считают и о
/// них докладывают, а не выбрасывают молча.
/// </summary>
public static class LegacyMacroMigration
{
    /// <summary>Имя файла унаследованной библиотеки относительно каталога приложения.</summary>
    public const string LegacyFileName = "macros.json";

    /// <summary>Имя файла унаследованных настроек хоткеев относительно каталога приложения.</summary>
    public const string LegacyHotkeysFileName = "hotkeys.json";

    /// <summary>Суффикс, который дописывают к унаследованному файлу после миграции.</summary>
    public const string MigratedSuffix = ".migrated";

    private static readonly JsonSerializerOptions LegacyOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Итог одного прохода миграции.</summary>
    /// <param name="Graphs">Графы к записи: каждый диспетчер, а следом его потеговые дети.</param>
    /// <param name="SkippedMacros">Унаследованные макросы, выброшенные за отсутствием исполнимых действий.</param>
    /// <param name="AttachedHotkeys">Унаследованные хоткеи макросов, превращённые в триггеры графа.</param>
    /// <param name="OrphanedHotkeys">Унаследованные хоткеи, которым некуда деться (аккорды рассылочных действий либо имена без подходящего макроса).</param>
    public sealed record Result(
        List<MacroGraph> Graphs,
        int SkippedMacros,
        int AttachedHotkeys,
        int OrphanedHotkeys);

    /// <summary>
    /// Преобразует содержимое унаследованного <c>macros.json</c> — и, если его передали,
    /// макросные привязки унаследованного <c>hotkeys.json</c> — в графы.
    /// </summary>
    /// <param name="json">Сырое содержимое унаследованного <c>macros.json</c>.</param>
    /// <param name="hotkeysJson">Сырое содержимое унаследованного <c>hotkeys.json</c> либо <c>null</c>, если файла нет или он нечитаем.</param>
    /// <exception cref="JsonException">Документ с макросами не является унаследованным файлом макросов.</exception>
    public static Result Convert(string json, string? hotkeysJson = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var file = JsonSerializer.Deserialize<LegacyFile>(json, LegacyOptions)
                   ?? throw new JsonException("Legacy macro file is 'null'.");

        var result = new List<MacroGraph>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedMacros = 0;

        foreach (var macro in file.Macros)
        {
            var name = Sanitize(macro.Name);
            if (name.Length == 0)
            {
                skippedMacros++;
                continue;
            }

            // Выживают только те теговые блоки, которые действительно что-то делают: из
            // пустого списка вышел бы граф без начальной ноды.
            var blocks = macro.ActionsByTag
                .Where(pair => pair.Value is { Count: > 0 })
                .ToList();
            if (blocks.Count == 0)
            {
                skippedMacros++;
                continue;
            }

            name = Deduplicate(name, usedNames);

            if (blocks.Count == 1)
            {
                var (tag, actions) = (blocks[0].Key, blocks[0].Value);
                result.Add(BuildChain(name, actions, new TargetSelector { RequireTags = [tag] }));
                continue;
            }

            var dispatcherNodes = new List<MacroNode>(blocks.Count);
            var children = new List<MacroGraph>(blocks.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var (tag, actions) = (blocks[i].Key, blocks[i].Value);
                var childName = Deduplicate(Sanitize($"{name}-{tag}"), usedNames);
                children.Add(BuildChain(childName, actions, target: null));
                dispatcherNodes.Add(new RunMacroNode
                {
                    Id = NodeId(i),
                    MacroName = childName,
                    Target = new TargetSelector { RequireTags = [tag] },
                    Await = true,
                    Next = i + 1 < blocks.Count ? NodeId(i + 1) : null,
                });
            }

            result.Add(new MacroGraph
            {
                Name = name,
                StartNodeId = dispatcherNodes[0].Id,
                Nodes = dispatcherNodes,
            });
            result.AddRange(children);
        }

        var (attached, orphaned) = AttachLegacyHotkeys(result, hotkeysJson);
        return new Result(result, skippedMacros, attached, orphaned);
    }

    // hotkeys.json → триггеры. Пункт назначения есть только у MacroBindings: они называют
    // макрос, а макрос теперь и есть владелец своего аккорда. Второй список привязывал зашитые
    // в код рассылочные действия, а они теперь примеры-макросы — цеплять их не к чему, поэтому
    // их считают, чтобы вызывающий об этом доложил.
    private static (int Attached, int Orphaned) AttachLegacyHotkeys(List<MacroGraph> graphs, string? hotkeysJson)
    {
        if (string.IsNullOrWhiteSpace(hotkeysJson))
        {
            return (0, 0);
        }

        LegacyHotkeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LegacyHotkeyFile>(hotkeysJson, LegacyOptions);
        }
        catch (JsonException)
        {
            // Сломанный файл хоткеев не имеет права утопить миграцию макросов.
            return (0, 0);
        }

        if (file is null)
        {
            return (0, 0);
        }

        var byName = graphs.ToDictionary(graph => graph.Name, StringComparer.Ordinal);
        var attached = 0;
        var orphaned = file.Bindings.Count;

        foreach (var binding in file.MacroBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.MacroName)
                || !byName.TryGetValue(Sanitize(binding.MacroName), out var graph))
            {
                orphaned++;
                continue;
            }

            graph.Triggers.Add(new HotkeyTrigger(binding.Modifiers, binding.Key, binding.MouseButton));
            attached++;
        }

        return (attached, orphaned);
    }

    // Плоский список действий → линейная цепочка нод. `target` проштамповывается на каждую ноду
    // действия, которая его поддерживает; у DelayNode цели нет (пауза общая на весь прогон).
    private static MacroGraph BuildChain(string name, List<LegacyAction> actions, TargetSelector? target)
    {
        var nodes = new List<MacroNode>(actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            var id = NodeId(i);
            var next = i + 1 < actions.Count ? NodeId(i + 1) : null;
            nodes.Add(actions[i] switch
            {
                LegacyKeyPressAction key => new KeyPressNode { Id = id, Key = key.Key, Target = target, Next = next },
                LegacyDelayAction delay => new DelayNode { Id = id, Ms = delay.Ms, Next = next },
                LegacyClickAction click => new ClickNode
                {
                    Id = id,
                    Point = click.Point,
                    DoubleClick = click.DoubleClick,
                    Target = target,
                    Next = next,
                },
                var other => throw new JsonException($"Unsupported legacy action type {other.GetType().Name}."),
            });
        }

        return new MacroGraph { Name = name, StartNodeId = nodes[0].Id, Nodes = nodes };
    }

    private static string NodeId(int index) => $"n{index}";

    // Имена макросов становятся именами файлов; унаследованные имена были свободной формы.
    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var chars = name.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars).TrimEnd('.', ' ');
    }

    private static string Deduplicate(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var suffix = 2;; suffix++)
        {
            var candidate = $"{name}-{suffix}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // ---- унаследованные DTO --------------------------------------------------------
    // Зеркала удалённой модели SmartMacro.Macro, только на чтение. Живут здесь (а не остаются
    // жить в домене), чтобы прежняя форма продержалась ровно столько, сколько нужно миграции.
    //
    // Их сеттеры init выглядят неиспользуемыми, и анализатор так и говорит. Это неправда: их
    // заполняет System.Text.Json через отражение, и «прибраться» здесь — значит бесшумно
    // сломать миграцию, которую увидит только тот, кто переезжает со старой версии.

    private sealed class LegacyFile
    {
        public List<LegacyMacro> Macros { get; init; } = [];
    }

    private sealed class LegacyMacro
    {
        public string? Name { get; init; }

        /// <summary>Тег → действия. Имя свойства старше тегов (ключами были имена элементов перечисления классов).</summary>
        [JsonPropertyName("ActionsByClass")]
        public Dictionary<string, List<LegacyAction>> ActionsByTag { get; init; } = [];
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(LegacyKeyPressAction), typeDiscriminator: "key")]
    [JsonDerivedType(typeof(LegacyDelayAction), typeDiscriminator: "delay")]
    [JsonDerivedType(typeof(LegacyClickAction), typeDiscriminator: "click")]
    private abstract record LegacyAction;

    private sealed record LegacyKeyPressAction(VirtualKey Key) : LegacyAction;

    private sealed record LegacyDelayAction(int Ms) : LegacyAction;

    private sealed record LegacyClickAction(ScreenPoint Point, bool DoubleClick) : LegacyAction;

    private sealed class LegacyHotkeyFile
    {
        /// <summary>Аккорды для зашитых в код рассылочных действий. Пункта назначения при миграции у них нет.</summary>
        public List<LegacyBinding> Bindings { get; init; } = [];

        /// <summary>Аккорды, привязанные к макросу по имени, — они становятся триггерами этого макроса.</summary>
        public List<LegacyMacroBinding> MacroBindings { get; init; } = [];
    }

    private sealed class LegacyBinding
    {
        public string? Trigger { get; init; }
    }

    private sealed class LegacyMacroBinding
    {
        public string? MacroName { get; init; }
        public HotkeyModifiers Modifiers { get; init; }
        public VirtualKey Key { get; init; }
        public MouseButton MouseButton { get; init; } = MouseButton.None;
    }
}
