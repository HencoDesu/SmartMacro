namespace SmartMacro.Native;

// Коды виртуальных клавиш Windows. Значения совпадают с константами VK_* из <WinUser.h>.
public enum VirtualKey : ushort
{
    Tab = 0x09,
    Enter = 0x0D,

    // Обобщённые коды модификаторов: левый и правый не различаются.
    //
    // ⚠️ Здесь стояло пояснение, что для отправки аккордов этого достаточно, — оно
    // описывало сценарий, которого в проекте нет и не будет. Аккорды через инъекцию
    // сообщений в PW НЕ РАБОТАЮТ: игра читает модификаторы через GetKeyState, а
    // межпоточный SendMessage его не обновляет. Стадия 4B удалила PressChordAsync и
    // SendChordAsync именно поэтому — чтобы не оставлять код, который выглядит рабочим и
    // молча ничего не делает в игре. Модификаторы остаются нужны для РЕГИСТРАЦИИ хоткеев
    // (RegisterHotKey), а не для их отправки.
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20,

    D0 = 0x30,
    D1,
    D2,
    D3,
    D4,
    D5,
    D6,
    D7,
    D8,
    D9,

    A = 0x41,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,

    F1 = 0x70,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13 = 0x7C,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24,

    // OEM-клавиши — знаки препинания, скобки и клавиша `~ / Ё. Имена совпадают с именами
    // из перечисления Key в Avalonia, чтобы проверка `Enum.IsDefined` в
    // KeyBindingPicker.OnKeyDown их принимала. Значения совпадают с константами Windows
    // VK_OEM_*, чтобы PostMessage/SendMessage доставляли реальное нажатие физической клавиши.
    OemSemicolon = 0xBA, // ; :
    OemPlus = 0xBB, // = +
    OemComma = 0xBC, // , <
    OemMinus = 0xBD, // - _
    OemPeriod = 0xBE, // . >
    OemQuestion = 0xBF, // / ?
    OemTilde = 0xC0, // ` ~   — та же физическая клавиша, что Ё в русской раскладке
    OemOpenBrackets = 0xDB, // [ {
    OemPipe = 0xDC, // \ |
    OemCloseBrackets = 0xDD, // ] }
    OemQuotes = 0xDE, // ' "
}
