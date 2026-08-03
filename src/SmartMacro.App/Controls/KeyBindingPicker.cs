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

/// <summary>
/// One keycap of a captured chord: <c>Ctrl</c>, <c>Shift</c>, <c>F1</c>.
/// </summary>
/// <param name="Text">What the cap reads.</param>
/// <param name="IsPrimary">
/// The main key rather than a modifier — drawn in the accent, so a chord's payload is
/// distinguishable from its prefix at a glance.
/// </param>
/// <param name="ShowsPlus">A "+" is drawn before this cap (everything but the first).</param>
public sealed record KeycapItem(string Text, bool IsPrimary, bool ShowsPlus);

/// <summary>
/// Game-style key-binding picker. Click the control → it enters "capture" mode; the next
/// KeyDown / mouse-button press becomes the bound input. Escape cancels, Delete / Backspace
/// clears.
///
/// <b>Wave D4 rebuilt the rendering, not the capture.</b> The capture semantics below are
/// fiddly and were tested by hand against real input, so they are untouched; what changed is
/// that the control used to be a <c>Button</c> whose <c>Content</c> was the string
/// "Ctrl+Shift+F1", and mockup 1f wants the four states of a real widget:
///
///   * <b>пусто</b> — dashed outline, «⌨ нажмите, чтобы задать»;
///   * <b>ловит</b> — pulsing accent outline with a dot, «Нажмите сочетание…»;
///   * <b>захвачено</b> — the chord as separate physical <see cref="KeycapItem"/> caps, not
///     as a string. The mockup is explicit about why: separate caps show WHAT was captured
///     and make a clash with an existing binding easier to spot;
///   * <b>конфликт</b> — the same caps over a danger outline, with <see cref="Conflict"/>
///     naming the owner («уже занят pw-immunity»).
///
/// It stays one class rather than splitting into "key picker" and "chord picker": both
/// consumers (a <c>KeyPressNode</c>'s key, a <c>HotkeyTrigger</c>'s chord) want all four
/// states, and the only difference between them is <see cref="CaptureModifiers"/> — which
/// already existed.
///
/// The bound value (<see cref="Key"/>) is a string matching <see cref="VirtualKey"/> member
/// names ("F1", "A", "D5", …), which is what the node model round-trips through JSON.
/// Keys not present in <see cref="VirtualKey"/> are ignored (Tab, Caps, modifiers used
/// alone, …) — the picker stays in capture mode so the user can try another key.
///
/// <see cref="CaptureModifiers"/> mode (for binding a global hotkey chord): the picker ALSO
/// tracks Ctrl/Shift/Alt/Win and ALSO accepts the mouse thumb/middle buttons. Mouse capture
/// is local-only — the cursor must be over the picker when the button goes down, since this
/// listens through PointerPressed and not a global hook. It stays opt-in because a
/// <c>KeyPressNode</c> would never want a mouse button bound as an in-game action key.
/// </summary>
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

    /// <summary>
    /// The chord is unusable and this says why («уже занят pw-immunity»). Supplied from
    /// outside: the control knows what was pressed, not what else is bound.
    /// </summary>
    public static readonly StyledProperty<string?> ConflictProperty =
        AvaloniaProperty.Register<KeyBindingPicker, string?>(nameof(Conflict));

    public static readonly DirectProperty<KeyBindingPicker, bool> IsCapturingProperty =
        AvaloniaProperty.RegisterDirect<KeyBindingPicker, bool>(
            nameof(IsCapturing),
            picker => picker._capturing);

    public static readonly DirectProperty<KeyBindingPicker, IReadOnlyList<KeycapItem>> KeycapsProperty =
        AvaloniaProperty.RegisterDirect<KeyBindingPicker, IReadOnlyList<KeycapItem>>(
            nameof(Keycaps),
            picker => picker.Keycaps);

    public static readonly DirectProperty<KeyBindingPicker, string> PromptTextProperty =
        AvaloniaProperty.RegisterDirect<KeyBindingPicker, string>(
            nameof(PromptText),
            picker => picker.PromptText);

    public static readonly DirectProperty<KeyBindingPicker, string> TrailingTextProperty =
        AvaloniaProperty.RegisterDirect<KeyBindingPicker, string>(
            nameof(TrailingText),
            picker => picker.TrailingText);

    public static readonly DirectProperty<KeyBindingPicker, bool> ShowsPromptProperty =
        AvaloniaProperty.RegisterDirect<KeyBindingPicker, bool>(
            nameof(ShowsPrompt),
            picker => picker.ShowsPrompt);

    private const string CapturePrompt = "Нажмите сочетание…";

    // The mockup prefixes this with ⌨ (U+2328). It cannot be used: that codepoint has an
    // emoji presentation, so Windows serves it from Segoe UI Emoji as a grey pictogram that
    // ignores Foreground — the trap Tokens.axaml documents for U+25B6. Nothing in the
    // non-emoji ranges reads as "keyboard", and the dashed outline already says "empty", so
    // the prompt is plain text.
    private const string EmptyPrompt = "нажмите, чтобы задать";
    private const string RebindHint = "клик — перезадать";

    private bool _capturing;
    private IReadOnlyList<KeycapItem> _keycaps = [];
    private string _promptText = EmptyPrompt;
    private string _trailingText = string.Empty;
    private bool _showsPrompt = true;

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

    /// <inheritdoc cref="ConflictProperty" />
    public string? Conflict
    {
        get => GetValue(ConflictProperty);
        set => SetValue(ConflictProperty, value);
    }

    /// <summary>
    /// The picker is armed and waiting for a key. Read-only; the view binds the amber
    /// "hotkeys are suspended" notice to it.
    /// </summary>
    public bool IsCapturing => _capturing;

    /// <summary>The captured chord as physical caps, left to right. Empty when nothing is bound.</summary>
    public IReadOnlyList<KeycapItem> Keycaps => _keycaps;

    /// <summary>Text shown INSTEAD of the caps: the empty prompt, or the armed prompt.</summary>
    public string PromptText => _promptText;

    /// <summary>Right-aligned note: the conflict message, or «клик — перезадать».</summary>
    public string TrailingText => _trailingText;

    /// <summary><c>true</c> when the prompt is showing rather than the caps.</summary>
    public bool ShowsPrompt => _showsPrompt;

    // Its own theme rather than Button's: the template is a keycap strip, not a
    // ContentPresenter. See Themes/Controls.axaml.
    protected override Type StyleKeyOverride => typeof(KeyBindingPicker);

    public KeyBindingPicker()
    {
        UpdateVisualState();
        Click += OnClick;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LostFocus += (_, _) => CancelCapture();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeyProperty
            || change.Property == ModifiersProperty
            || change.Property == MouseButtonProperty
            || change.Property == CaptureModifiersProperty
            || change.Property == ConflictProperty)
        {
            UpdateVisualState();
        }
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        SetAndRaise(IsCapturingProperty, ref _capturing, true);
        UpdateVisualState();
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

    // Captures mouse XButton1/2 presses while in capture mode. We ignore Left/Right
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
        SetAndRaise(IsCapturingProperty, ref _capturing, false);
        UpdateVisualState();
    }

    /// <summary>
    /// Recomputes everything the template renders, plus the pseudoclasses the theme styles
    /// react to. One method rather than a derived property per state so the four visual
    /// states can never be half-applied.
    /// </summary>
    private void UpdateVisualState()
    {
        var hasBinding = (CaptureModifiers && MouseButton != MouseButton.None)
                         || !string.IsNullOrEmpty(Key);

        SetAndRaise(KeycapsProperty, ref _keycaps, hasBinding ? BuildKeycaps() : []);
        SetAndRaise(PromptTextProperty, ref _promptText, _capturing ? CapturePrompt : EmptyPrompt);
        SetAndRaise(ShowsPromptProperty, ref _showsPrompt, _capturing || !hasBinding);

        var trailing = Conflict is { Length: > 0 } conflict
            ? conflict
            : hasBinding && !_capturing ? RebindHint : string.Empty;
        SetAndRaise(TrailingTextProperty, ref _trailingText, trailing);

        PseudoClasses.Set(":capturing", _capturing);
        PseudoClasses.Set(":unbound", !hasBinding);
        // A conflict is only worth shouting about once there IS a chord to complain about,
        // and never while the user is in the middle of replacing it.
        PseudoClasses.Set(":conflict", hasBinding && !_capturing && Conflict is { Length: > 0 });
    }

    // Win32 RegisterHotKey order is Ctrl+Shift+Alt+Win+Key by convention; we match it
    // for consistency with how users see hotkeys elsewhere in Windows.
    private IReadOnlyList<KeycapItem> BuildKeycaps()
    {
        var caps = new List<KeycapItem>(5);
        if (CaptureModifiers)
        {
            var mods = Modifiers;
            if (mods.HasFlag(HotkeyModifiers.Control)) caps.Add(new KeycapItem("Ctrl", false, caps.Count > 0));
            if (mods.HasFlag(HotkeyModifiers.Shift)) caps.Add(new KeycapItem("Shift", false, caps.Count > 0));
            if (mods.HasFlag(HotkeyModifiers.Alt)) caps.Add(new KeycapItem("Alt", false, caps.Count > 0));
            if (mods.HasFlag(HotkeyModifiers.Win)) caps.Add(new KeycapItem("Win", false, caps.Count > 0));
        }

        var main = CaptureModifiers && MouseButton != MouseButton.None
            ? MouseButtonLabel(MouseButton)
            : Key;
        caps.Add(new KeycapItem(main, true, caps.Count > 0));
        return caps;
    }

    private static string MouseButtonLabel(MouseButton button) => button switch
    {
        MouseButton.XButton1 => "Mouse4",
        MouseButton.XButton2 => "Mouse5",
        MouseButton.Middle => "MouseMiddle",
        _ => button.ToString(),
    };

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
