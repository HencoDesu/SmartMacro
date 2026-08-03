namespace SmartMacro.Native.Hotkey;

// Зеркало HotkeyDescriptor для мышиных привязок: Id — значение, которое Win32MouseHookMonitor
// возвращает при совпадении, чтобы потребитель сопоставил его со своим смыслом. Чтобы
// привязка сработала, все модификаторы должны быть зажаты в момент нажатия кнопки мыши.
public sealed record MouseHookBinding(int Id, HotkeyModifiers Modifiers, MouseButton Button);
