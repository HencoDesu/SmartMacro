using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>The two trigger kinds, for the editor's "add trigger" buttons.</summary>
public enum MacroTriggerKind
{
    Hotkey,
    ProcessAppeared,
}

/// <summary>
/// Base for the trigger rows of the editor. Same polymorphic-row pattern as the nodes:
/// one concrete VM per <see cref="MacroTrigger"/> type, rendered by an implicit
/// <c>DataTemplate</c>, mapping to and from the model in one place.
/// </summary>
public abstract class TriggerRowViewModel : ObservableObject
{
    /// <summary>Russian type label for the row header.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>Builds the model trigger from the current editor state.</summary>
    public abstract MacroTrigger ToTrigger();

    /// <summary>Field-level complaints in Russian; empty when the row is clean.</summary>
    public virtual IEnumerable<string> GetInputErrors() => [];

    /// <summary>Loads a model trigger into the matching row type.</summary>
    public static TriggerRowViewModel FromTrigger(MacroTrigger trigger) => trigger switch
    {
        HotkeyTrigger hotkey => new HotkeyTriggerRowViewModel(hotkey),
        ProcessAppearedTrigger process => new ProcessTriggerRowViewModel(process),
        _ => throw new NotSupportedException($"No editor for trigger type {trigger.GetType().Name}."),
    };

    /// <summary>Creates an empty row of the requested kind.</summary>
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
/// Editor for <see cref="HotkeyTrigger"/>, backed by <c>KeyBindingPicker</c> in
/// <c>CaptureModifiers</c> mode (chords plus the mouse thumb/middle buttons).
///
/// Editing one of these is exactly why the dialog suspends the hotkey listener: a chord
/// already registered with <c>RegisterHotKey</c> never reaches the picker's KeyDown
/// handler, so without suspension the combos most in need of re-binding are the ones that
/// cannot be re-bound.
/// </summary>
public sealed class HotkeyTriggerRowViewModel : TriggerRowViewModel
{
    private HotkeyModifiers _modifiers;
    private string _keyName;
    private MouseButton _mouseButton;

    public HotkeyTriggerRowViewModel(HotkeyTrigger trigger)
    {
        _modifiers = trigger.Modifiers;
        // Empty rather than "0" for the unset key, so the picker shows its own prompt.
        _keyName = trigger.Key == 0 ? string.Empty : trigger.Key.ToString();
        _mouseButton = trigger.MouseButton;
    }

    public override string TypeLabel => "Хоткей";

    public HotkeyModifiers Modifiers
    {
        get => _modifiers;
        set => SetField(ref _modifiers, value);
    }

    /// <summary><see cref="VirtualKey"/> member name, or empty for a mouse chord.</summary>
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
}

/// <summary>Editor for <see cref="ProcessAppearedTrigger"/>: just the process name to watch.</summary>
public sealed class ProcessTriggerRowViewModel : TriggerRowViewModel
{
    private string _processName;

    public ProcessTriggerRowViewModel(ProcessAppearedTrigger trigger) => _processName = trigger.ProcessName;

    public override string TypeLabel => "Появление процесса";

    /// <summary>Process name without extension, matched case-insensitively (e.g. <c>elementclient_64</c>).</summary>
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
