using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>Два вида триггеров — для кнопок «добавить триггер» в редакторе.</summary>
public enum MacroTriggerKind
{
    Hotkey,
    ProcessAppeared,
}

/// <summary>
/// Основа для строк-триггеров редактора. Тот же приём полиморфной строки, что и у нод: по одной
/// конкретной VM на тип <see cref="MacroTrigger"/>, отрисовка неявным <c>DataTemplate</c>,
/// перекладывание в модель и обратно — в одном месте.
/// </summary>
public abstract class TriggerRowViewModel : ObservableObject
{
    /// <summary>Русская подпись типа для шапки строки.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>Собирает триггер модели из текущего состояния редактора.</summary>
    public abstract MacroTrigger ToTrigger();

    /// <summary>Претензии по полям, по-русски; пусто, когда строка чиста.</summary>
    public virtual IEnumerable<string> GetInputErrors() => [];

    /// <summary>Загружает триггер модели в строку подходящего типа.</summary>
    public static TriggerRowViewModel FromTrigger(MacroTrigger trigger) => trigger switch
    {
        HotkeyTrigger hotkey => new HotkeyTriggerRowViewModel(hotkey),
        ProcessAppearedTrigger process => new ProcessTriggerRowViewModel(process),
        _ => throw new NotSupportedException($"No editor for trigger type {trigger.GetType().Name}."),
    };

    /// <summary>Создаёт пустую строку запрошенного вида.</summary>
    public static TriggerRowViewModel Create(MacroTriggerKind kind) => kind switch
    {
        MacroTriggerKind.Hotkey => new HotkeyTriggerRowViewModel(
            new HotkeyTrigger(HotkeyModifiers.None, default)),
        MacroTriggerKind.ProcessAppeared => new ProcessTriggerRowViewModel(
            new ProcessAppearedTrigger(string.Empty)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown trigger kind."),
    };
}

/// <summary>
/// Редактор <see cref="HotkeyTrigger"/>, за которым стоит <c>KeyBindingPicker</c> в режиме
/// <c>CaptureModifiers</c> (сочетания плюс боковые и средняя кнопки мыши).
///
/// Правка такой строки — ровно та причина, по которой режим приостанавливает слушателя
/// хоткеев: сочетание, уже зарегистрированное через <c>RegisterHotKey</c>, до обработчика
/// KeyDown ловушки не доходит никогда, так что без приостановки перебиндить нельзя было бы
/// именно те сочетания, которые больше всего в этом нуждаются.
/// </summary>
public sealed class HotkeyTriggerRowViewModel : TriggerRowViewModel
{
    private HotkeyModifiers _modifiers;
    private string _keyName;
    private MouseButton _mouseButton;
    private string? _conflict;

    public HotkeyTriggerRowViewModel(HotkeyTrigger trigger)
    {
        _modifiers = trigger.Modifiers;
        // Для неназначенной клавиши — пусто, а не «0», чтобы ловушка показала своё приглашение.
        _keyName = trigger.Key == 0 ? string.Empty : trigger.Key.ToString();
        _mouseButton = trigger.MouseButton;
    }

    public override string TypeLabel => "Хоткей";

    /// <summary>
    /// Почему это сочетание не сделает того, что по нему кажется, либо <c>null</c>, когда оно
    /// свободно. Четвёртое состояние ловушки (макет 1f).
    ///
    /// В один и тот же слот попадают два разных отказа, потому что из кресла пользователя это
    /// один и тот же отказ — «нажал, и ничего не произошло»:
    ///   * «уже занят pw-immunity» — сочетание забрал другой макрос библиотеки. Об этом панель
    ///     знает сама; считает это <c>MacroEditorViewModel</c>.
    ///   * «занят другим приложением» — Win32 <c>RegisterHotKey</c> отказал. Увидеть это может
    ///     только демон, и он сообщает об этом через <c>GetHotkeyFailures</c>.
    /// </summary>
    public string? Conflict
    {
        get => _conflict;
        internal set
        {
            if (SetField(ref _conflict, value))
            {
                OnPropertyChanged(nameof(HasConflict));
            }
        }
    }

    /// <summary><c>true</c>, когда есть конфликт, который надо нарисовать.</summary>
    public bool HasConflict => _conflict is not null;

    public HotkeyModifiers Modifiers
    {
        get => _modifiers;
        set => SetField(ref _modifiers, value);
    }

    /// <summary>Имя члена <see cref="VirtualKey"/> либо пусто для сочетания с мышью.</summary>
    public string KeyName
    {
        get => _keyName;
        set => SetField(ref _keyName, value ?? string.Empty);
    }

    public MouseButton MouseButton
    {
        get => _mouseButton;
        set => SetField(ref _mouseButton, value);
    }

    public override MacroTrigger ToTrigger() => new HotkeyTrigger(
        _modifiers,
        Enum.TryParse<VirtualKey>(_keyName, out var key) ? key : default,
        _mouseButton);

    public override IEnumerable<string> GetInputErrors()
    {
        var trigger = (HotkeyTrigger)ToTrigger();
        if (!trigger.IsKeyboard && !trigger.IsMouse)
        {
            yield return "Хоткей: сочетание не назначено.";
        }
    }

    /// <summary>
    /// Каноническая личность сочетания — то, чем эта строка сравнивается с остальной
    /// библиотекой. <c>null</c> для триггера, к которому ничего не привязано: несвязанное
    /// сочетание не конфликтует ни с чем, а объявить одну пустую ловушку столкнувшейся с
    /// другой — значит шуметь на макросе, который просто ещё не доделали.
    ///
    /// Строка, а не кортеж, — чтобы она могла быть ключом словаря сочетаний библиотеки без
    /// самописного компаратора и чтобы клавиатурное сочетание никогда не оказалось равно
    /// мышиному, у которого совпали флаги модификаторов.
    /// </summary>
    internal static string? ChordKey(HotkeyTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (trigger.IsMouse)
        {
            return $"M:{(int)trigger.Modifiers}:{(int)trigger.MouseButton}";
        }

        return trigger.IsKeyboard ? $"K:{(int)trigger.Modifiers}:{(int)trigger.Key}" : null;
    }

    /// <summary>Личность сочетания, которое эта строка держит прямо сейчас.</summary>
    internal string? ChordKey() => ChordKey((HotkeyTrigger)ToTrigger());
}

/// <summary>Редактор <see cref="ProcessAppearedTrigger"/>: одно только имя процесса, за которым следим.</summary>
public sealed class ProcessTriggerRowViewModel : TriggerRowViewModel
{
    private string _processName;

    public ProcessTriggerRowViewModel(ProcessAppearedTrigger trigger) => _processName = trigger.ProcessName;

    public override string TypeLabel => "Появление процесса";

    /// <summary>Имя процесса без расширения, сопоставляется без учёта регистра (например, <c>elementclient_64</c>).</summary>
    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value ?? string.Empty);
    }

    public override MacroTrigger ToTrigger() => new ProcessAppearedTrigger(_processName);

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_processName))
        {
            yield return "Появление процесса: имя процесса не задано.";
        }
    }
}
