using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Переменные в области видимости прогона: имя → <see cref="VariableValue"/> (строка | число |
/// точка). Пишут их триггер (всегда засевает <see cref="CursorVariableName"/>) и условные ноды
/// (<c>ResultVar</c>/<c>FoundPointVar</c>); читают через <c>PointVar</c> и через интерполяцию
/// <c>{name}</c> в строковых параметрах. Под-прогонам достаётся <see cref="Clone"/>, а не тот же
/// экземпляр, — записи между прогонами не протекают.
///
/// Не потокобезопасно по замыслу: пишет только единственный walker одного прогона; параллельные
/// ветки разветвления лишь читают, а у под-прогонов свои копии.
/// </summary>
public sealed class MacroVariables
{
    /// <summary>
    /// Переменная, которую пишет любой триггер: позиция курсора в момент срабатывания. Это
    /// псевдоним константы из Contracts, а не повторение литерала: панель переменных подписывает
    /// то же имя как «триггер (сид)», и разъехаться этим двоим нельзя.
    /// </summary>
    public const string CursorVariableName = MacroVariableNames.Cursor;

    private readonly Dictionary<string, VariableValue> _values;

    /// <summary>Создаёт пустой набор переменных.</summary>
    public MacroVariables()
    {
        _values = new Dictionary<string, VariableValue>(StringComparer.Ordinal);
    }

    private MacroVariables(Dictionary<string, VariableValue> values)
    {
        _values = values;
    }

    /// <summary>Сколько переменных определено.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// Все установленные на данный момент переменные — для доклада <c>VariableSet</c> в поток
    /// событий прогона в начале обхода. Перечисляется на собственном потоке walker'а до первой
    /// ноды, и это единственный момент, когда писать точно никто не может, — см. замечание о
    /// потокобезопасности выше.
    /// </summary>
    public IEnumerable<KeyValuePair<string, VariableValue>> Entries => _values;

    /// <summary>
    /// Создаёт набор переменных для нового прогона от триггера: <c>cursor</c> ставится в
    /// позицию курсора на момент срабатывания. Через это проходит любой путь запуска (хоткей,
    /// появление процесса, кнопка «Запустить» в UI).
    /// </summary>
    public static MacroVariables ForTrigger(ScreenPoint cursorPosition)
    {
        var variables = new MacroVariables();
        variables.Set(CursorVariableName, cursorPosition);
        return variables;
    }

    /// <summary>Ставит (или перезаписывает) переменную. Регистр в именах важен.</summary>
    public void Set(string name, VariableValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
    }

    /// <summary>Чтение, которое не бросает.</summary>
    public bool TryGet(string name, out VariableValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _values.TryGetValue(name, out value!);
    }

    /// <summary>Читает переменную; на отсутствующее имя бросает <see cref="MacroVariableNotFoundException"/>.</summary>
    public VariableValue Get(string name)
    {
        return TryGet(name, out var value) ? value : throw new MacroVariableNotFoundException(name);
    }

    /// <summary>
    /// Читает переменную, которая обязана держать точку (ClickNode.PointVar). На отсутствующее
    /// имя бросает <see cref="MacroVariableNotFoundException"/>; на значение не-точку —
    /// <see cref="MacroVariableTypeMismatchException"/>.
    /// </summary>
    public ScreenPoint GetPoint(string name)
    {
        var value = Get(name);
        return value is PointValue point
            ? point.Value
            : throw new MacroVariableTypeMismatchException(name, "point", value);
    }

    /// <summary>
    /// Заменяет каждый заполнитель <c>{name}</c> в <paramref name="template"/> строкой показа
    /// соответствующей переменной. Заполнитель, ссылающийся на неопределённую переменную,
    /// бросает <see cref="MacroVariableNotFoundException"/> — исполнитель обрывает прогон.
    /// </summary>
    public string Interpolate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return MacroVariableNames.Placeholder().Replace(template, match => Get(match.Groups[1].Value).DisplayString);
    }

    /// <summary>Независимая копия для под-прогона: чтения наследуются, записи обратно не текут.</summary>
    public MacroVariables Clone()
    {
        return new MacroVariables(new Dictionary<string, VariableValue>(_values, StringComparer.Ordinal));
    }
}
