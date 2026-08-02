using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMacro.Native;

// Disambiguate — Avalonia.Input also has a MouseButton enum (different shape) that the
// `using Avalonia.Input;` directive pulls in. Picker stores Win32-shaped values that map
// to MSLLHOOKSTRUCT.mouseData high word, so we alias to ours.
using MouseButton = SmartMacro.Native.MouseButton;

namespace SmartMacro.App.Controls;

// Game-style key-binding picker. Click the control → it enters "capture" mode showing
// "Press a key..."; the next KeyDown / mouse-button press becomes the bound input.
// Escape cancels, Delete / Backspace clears.
//
// Live since W0.3: Views/MacrosView.axaml binds it twice — once for a KeyPressNode's key,
// once for a HotkeyTrigger's chord (CaptureModifiers mode). It briefly had no consumer between
// W0.2b (which deleted the settings hotkey editor and the legacy per-action macro editor)
// and W0.3, and was kept rather than deleted-and-rewritten because the capture semantics
// below are fiddly and tested by hand against real input.
//
// The bound value (Key property) is a string matching VirtualKey enum names ("F1", "A",
// "D5", ...), which is what the node model round-trips through JSON — callers parse with
// Enum.TryParse.
//
// Keys not present in VirtualKey are ignored (Tab, Caps, modifiers used alone, etc.) —
// the picker stays in capture mode so the user can try another key.
//
// CaptureModifiers mode (for binding a global hotkey chord): when set, the picker ALSO tracks
// Ctrl/Shift/Alt/Win state and ALSO accepts mouse XButton1/2 (the "back"/"forward" thumb
// buttons). Mouse capture is local-only — the cursor must be over the picker at the moment
// the button is pressed, since we listen via PointerPressed and not a global hook.
// CaptureModifiers stays opt-in because Character key bindings would never want a mouse
// button bound as an in-game action key.
public sealed class KeyBindingPicker : Button
{
    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<KeyBindingPicker, string>(
            nameof(Key),
            defaultValue: string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<HotkeyModifiers> ModifiersProperty =
        AvaloniaProperty.Register<KeyBindingPicker, HotkeyModifiers>(
            nameof(Modifiers),
            defaultValue: HotkeyModifiers.None,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<MouseButton> MouseButtonProperty =
        AvaloniaProperty.Register<KeyBindingPicker, MouseButton>(
            nameof(MouseButton),
            defaultValue: MouseButton.None,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CaptureModifiersProperty =
        AvaloniaProperty.Register<KeyBindingPicker, bool>(
            nameof(CaptureModifiers),
            defaultValue: false);

    private const string CapturePromptPlain = "Press a key...";
    private const string CapturePromptCombo = "Press a key or mouse button...";
    private const string EmptyPrompt = "click to set";

    private bool _capturing;

    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public HotkeyModifiers Modifiers
    {
        get => GetValue(ModifiersProperty);
        set => SetValue(ModifiersProperty, value);
    }

    public MouseButton MouseButton
    {
        get => GetValue(MouseButtonProperty);
        set => SetValue(MouseButtonProperty, value);
    }

    public bool CaptureModifiers
    {
        get => GetValue(CaptureModifiersProperty);
        set => SetValue(CaptureModifiersProperty, value);
    }

    // FluentTheme styles target `Button` by exact type; without redirecting the style
    // key, our subclass renders as bare unstyled text on the window background. This
    // makes us pick up the normal Button chrome (border, hover, pressed states).
    protected override Type StyleKeyOverride => typeof(Button);

    public KeyBindingPicker()
    {
        UpdateContent();
        Click += OnClick;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LostFocus += (_, _) => CancelCapture();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_capturing)
        {
            return;
        }
        if (change.Property == KeyProperty
            || change.Property == ModifiersProperty
            || change.Property == MouseButtonProperty)
        {
            UpdateContent();
        }
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        _capturing = true;
        Content = CaptureModifiers ? CapturePromptCombo : CapturePromptPlain;
        Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;

        switch (e.Key)
        {
            case Avalonia.Input.Key.Escape:
                CancelCapture();
                return;

            case Avalonia.Input.Key.Delete:
            case Avalonia.Input.Key.Back:
                ClearBinding();
                CancelCapture();
                return;
        }

        // Modifier keys alone don't make a useful binding — ignore so user can keep
        // trying combinations or finally press a real key.
        if (IsBareModifier(e.Key))
        {
            return;
        }

        var name = e.Key.ToString();
        if (!Enum.IsDefined(typeof(VirtualKey), name))
        {
            // Avalonia name doesn't match any VirtualKey we know about (OemPeriod,
            // ImeProcessed, etc.) — stay in capture mode for the user to retry.
            return;
        }

        if (CaptureModifiers)
        {
            Modifiers = ToHotkeyModifiers(e.KeyModifiers);
            MouseButton = MouseButton.None;
        }
        Key = name;
        CancelCapture();
    }

    // Captures mouse XButton1/2 presses while in capture mode. We ignore Left/Right/Middle
    // (the normal click that triggered capture comes through here too — wrapped in the
    // Avalonia Button click handling that fires OnClick separately, but we also need to
    // not accidentally bind it as a hotkey).
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_capturing || !CaptureModifiers)
        {
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        MouseButton button;
        if (props.IsXButton1Pressed)
        {
            button = MouseButton.XButton1;
        }
        else if (props.IsXButton2Pressed)
        {
            button = MouseButton.XButton2;
        }
        else if (props.IsMiddleButtonPressed)
        {
            button = MouseButton.Middle;
        }
        else
        {
            // Left/Right — pass through so the normal Button click handling still works
            // (entering capture mode in the first place uses left-click).
            return;
        }

        e.Handled = true;
        Modifiers = ToHotkeyModifiers(e.KeyModifiers);
        MouseButton = button;
        Key = string.Empty;
        CancelCapture();
    }

    private void ClearBinding()
    {
        Key = string.Empty;
        if (CaptureModifiers)
        {
            Modifiers = HotkeyModifiers.None;
            MouseButton = MouseButton.None;
        }
    }

    private void CancelCapture()
    {
        if (!_capturing)
        {
            return;
        }
        _capturing = false;
        UpdateContent();
    }

    private void UpdateContent()
    {
        // Mouse binding wins if set. Order matters because Key is empty when MouseButton
        // is bound, but the EmptyPrompt check on Key alone would still show "click to set".
        if (CaptureModifiers && MouseButton != MouseButton.None)
        {
            Content = FormatCombo(Modifiers, MouseButtonLabel(MouseButton));
            return;
        }

        if (string.IsNullOrEmpty(Key))
        {
            Content = EmptyPrompt;
            return;
        }

        if (CaptureModifiers && Modifiers != HotkeyModifiers.None)
        {
            Content = FormatCombo(Modifiers, Key);
            return;
        }

        Content = Key;
    }

    private static string MouseButtonLabel(MouseButton button) => button switch
    {
        MouseButton.XButton1 => "Mouse4",
        MouseButton.XButton2 => "Mouse5",
        MouseButton.Middle => "MouseMiddle",
        _ => button.ToString(),
    };

    // Win32 RegisterHotKey order is Ctrl+Shift+Alt+Win+Key by convention; we match it
    // for consistency with how users see hotkeys elsewhere in Windows.
    private static string FormatCombo(HotkeyModifiers mods, string mainPart)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(mainPart);
        return string.Join("+", parts);
    }

    private static HotkeyModifiers ToHotkeyModifiers(KeyModifiers km)
    {
        var result = HotkeyModifiers.None;
        if (km.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
        if (km.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (km.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (km.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Win;
        return result;
    }

    private static bool IsBareModifier(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl
            or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
            or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt
            or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin => true,
        _ => false,
    };
}
