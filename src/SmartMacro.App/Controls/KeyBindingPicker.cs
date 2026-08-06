using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMacro.Native;

// Разводим имена: у Avalonia.Input тоже есть перечисление MouseButton (другой формы), и его
// затягивает директива `using Avalonia.Input;`. Ловушка хранит значения в форме Win32,
// ложащиеся в старшее слово MSLLHOOKSTRUCT.mouseData, поэтому псевдоним заводим на наше.
using MouseButton = SmartMacro.Native.MouseButton;
using SmartMacro.Resources;

namespace SmartMacro.App.Controls;

/// <summary>
/// Один кейкап пойманного сочетания: <c>Ctrl</c>, <c>Shift</c>, <c>F1</c>.
/// </summary>
/// <param name="Text">Что написано на кейкапе.</param>
/// <param name="IsPrimary">
/// Основная клавиша, а не модификатор, — рисуется акцентом, чтобы нагрузка сочетания
/// отличалась от его приставки с одного взгляда.
/// </param>
/// <param name="ShowsPlus">Перед этим кейкапом рисуется «+» (у всех, кроме первого).</param>
public sealed record KeycapItem(string Text, bool IsPrimary, bool ShowsPlus);

/// <summary>
/// Ловушка привязки клавиш в игровом духе. Клик по контролу → он входит в режим «ловит»;
/// следующее нажатие клавиши или кнопки мыши становится привязанным вводом. Escape отменяет,
/// Delete и Backspace очищают.
///
/// <b>Волна D4 переделала отрисовку, а не ловлю.</b> Семантика ловли ниже мудрёная и была
/// вручную проверена на живом вводе, поэтому её не тронули; изменилось то, что раньше контрол
/// был <c>Button</c>, у которого в <c>Content</c> лежала строка «Ctrl+Shift+F1», а макет 1f
/// хочет четыре состояния настоящего виджета:
///
///   * <b>пусто</b> — пунктирный контур, «⌨ нажмите, чтобы задать»;
///   * <b>ловит</b> — пульсирующий акцентный контур с точкой, «Нажмите сочетание…»;
///   * <b>захвачено</b> — сочетание отдельными физическими кейкапами
///     <see cref="KeycapItem"/>, а не строкой. Макет прямо объясняет почему: раздельные
///     кейкапы показывают, ЧТО именно поймано, и позволяют легче заметить столкновение с уже
///     существующей привязкой;
///   * <b>конфликт</b> — те же кейкапы поверх тревожного контура, а <see cref="Conflict"/>
///     называет владельца («уже занят pw-immunity»).
///
/// Класс остался одним, а не разделился на «ловушку клавиши» и «ловушку сочетания»: оба
/// потребителя (клавиша у <c>KeyPressNode</c>, сочетание у <c>HotkeyTrigger</c>) хотят всех
/// четырёх состояний, а разница между ними одна — <see cref="CaptureModifiers"/>, который и
/// так уже был.
///
/// Привязанное значение (<see cref="Key"/>) — строка, совпадающая с именами членов
/// <see cref="VirtualKey"/> («F1», «A», «D5», …), и именно её модель ноды гоняет через JSON.
/// Клавиши, которых в <see cref="VirtualKey"/> нет, игнорируются (Tab, Caps, модификаторы сами
/// по себе, …) — ловушка остаётся в режиме ловли, чтобы пользователь попробовал другую.
///
/// Режим <see cref="CaptureModifiers"/> (для привязки глобального сочетания): ловушка ТАКЖЕ
/// следит за Ctrl/Shift/Alt/Win и ТАКЖЕ принимает боковые и среднюю кнопки мыши. Ловля мыши
/// работает только локально — в момент нажатия курсор должен быть над ловушкой, потому что
/// слушает она через PointerPressed, а не через глобальный хук. Опциональной она остаётся
/// потому, что <c>KeyPressNode</c> никогда не захотел бы привязать кнопку мыши как игровую
/// клавишу действия.
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
    /// Сочетание непригодно, и здесь сказано почему («уже занят pw-immunity»). Приходит
    /// снаружи: контрол знает, что нажали, но не знает, что ещё привязано.
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

    private static string CapturePrompt => Strings_App.Editor_Hotkey_CapturePrompt;

    // В макете перед этим стоит ⌨ (U+2328). Использовать его нельзя: у этой кодовой точки есть
    // эмодзи-представление, поэтому Windows подаёт её из Segoe UI Emoji серой картинкой,
    // игнорирующей Foreground, — ровно та ловушка, которую Tokens.axaml описывает для U+25B6.
    // Ничто из неэмодзийных диапазонов не читается как «клавиатура», а пунктирный контур и так
    // говорит «пусто», поэтому приглашение — обычный текст.
    private static string EmptyPrompt => Strings_App.Editor_Hotkey_EmptyPrompt;
    private static string RebindHint => Strings_App.Editor_Hotkey_RebindHint;

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
    /// Ловушка взведена и ждёт клавишу. Только для чтения; вид привязывает к этому янтарную
    /// заметку «хоткеи приостановлены».
    /// </summary>
    public bool IsCapturing => _capturing;

    /// <summary>Пойманное сочетание физическими кейкапами, слева направо. Пусто, когда ничего не привязано.</summary>
    public IReadOnlyList<KeycapItem> Keycaps => _keycaps;

    /// <summary>Текст, показываемый ВМЕСТО кейкапов: приглашение пустого состояния либо взведённого.</summary>
    public string PromptText => _promptText;

    /// <summary>Приписка справа: сообщение о конфликте либо «клик — перезадать».</summary>
    public string TrailingText => _trailingText;

    /// <summary><c>true</c>, когда показывается приглашение, а не кейкапы.</summary>
    public bool ShowsPrompt => _showsPrompt;

    // Своя тема, а не тема Button: шаблон здесь — полоска кейкапов, а не ContentPresenter.
    // См. Themes/Controls.axaml.
    protected override Type StyleKeyOverride => typeof(KeyBindingPicker);

    public KeyBindingPicker()
    {
        UpdateVisualState();
        Click += OnClick;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
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

        // Одни только модификаторы полезной привязки не дают — игнорируем, чтобы пользователь
        // мог дальше перебирать комбинации или наконец нажать настоящую клавишу.
        if (IsBareModifier(e.Key))
        {
            return;
        }

        var name = e.Key.ToString();
        if (!Enum.IsDefined(typeof(VirtualKey), name))
        {
            // Имя из Avalonia не совпало ни с одним известным нам VirtualKey (OemPeriod,
            // ImeProcessed и прочие) — остаёмся в режиме ловли, чтобы пользователь повторил.
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

    // Ловит нажатия XButton1/2 мыши, пока мы в режиме ловли. Левую и правую игнорируем (обычный
    // клик, который эту самую ловлю и включил, проходит здесь же — завёрнутый в обработку
    // клика у Button из Avalonia, которая отдельно поднимает OnClick, — и нам заодно нужно не
    // привязать его случайно как хоткей).
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
            // Левая и правая — пропускаем дальше, чтобы обычная обработка клика у Button
            // продолжала работать (в режим ловли изначально входят как раз левым кликом).
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
    /// Пересчитывает всё, что рисует шаблон, плюс псевдоклассы, на которые реагируют стили
    /// темы. Один метод, а не по производному свойству на состояние, — чтобы четыре
    /// визуальных состояния никогда не оказались применены наполовину.
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
            : hasBinding && !_capturing
                ? RebindHint
                : string.Empty;
        SetAndRaise(TrailingTextProperty, ref _trailingText, trailing);

        PseudoClasses.Set(":capturing", _capturing);
        PseudoClasses.Set(":unbound", !hasBinding);
        // Кричать о конфликте стоит только тогда, когда сочетание, на которое жалуются, ЕСТЬ, —
        // и никогда, пока пользователь его как раз меняет.
        PseudoClasses.Set(":conflict", hasBinding && !_capturing && Conflict is { Length: > 0 });
    }

    // Порядок у Win32 RegisterHotKey по соглашению — Ctrl+Shift+Alt+Win+клавиша; повторяем его,
    // чтобы совпадать с тем, как пользователь видит хоткеи в остальной Windows.
    private IReadOnlyList<KeycapItem> BuildKeycaps()
    {
        var caps = new List<KeycapItem>(5);
        if (CaptureModifiers)
        {
            // Имена и порядок — из HotkeyNames: их же печатает бейдж строки библиотеки, и две
            // записи одного аккорда в одном окне уже случались.
            foreach (var name in HotkeyNames.Modifiers(Modifiers))
            {
                caps.Add(new KeycapItem(name, false, caps.Count > 0));
            }
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
