namespace SmartMacro.Native;

// Значения совпадают с fsModifiers из Win32 RegisterHotKey — перечисление держим 1:1 с API.
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// Как аккорд НАЗЫВАЕТСЯ на экране. Одно место, потому что мест печати два.
///
/// ⚠️ <c>Modifiers.ToString()</c> сюда не годится, и это не вкусовщина: у флагового
/// перечисления он даёт «Alt, Control» — с запятой, в порядке объявления и словом «Control»,
/// которого Windows не пишет нигде. Ловушка живая: ловушка клавиш рисовала кейкапы
/// «Ctrl Shift F1», а бейдж строки библиотеки в это же время печатал «Control, Shift+F1» — один
/// и тот же аккорд, две разные записи в одном окне, и вторая вдобавок настолько шире, что
/// заезжала под кнопки строки.
///
/// Порядок — соглашение Win32 <c>RegisterHotKey</c> (Ctrl → Shift → Alt → Win), то есть тот же,
/// в каком пользователь видит хоткеи в остальной Windows.
/// </summary>
public static class HotkeyNames
{
    /// <summary>Имена взведённых модификаторов по порядку; пусто, когда их нет.</summary>
    public static IReadOnlyList<string> Modifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        return parts;
    }

    /// <summary>Весь аккорд одной строкой: «Ctrl+Shift+F1», «F19», «Ctrl+XButton1».</summary>
    public static string Chord(HotkeyModifiers modifiers, string key) =>
        string.Join('+', Modifiers(modifiers).Append(key));
}
