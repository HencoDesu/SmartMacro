namespace SmartMacro.Native.Hotkey;

// Одна привязка горячей клавиши, которую зарегистрирует монитор. Id — то, что важно
// потребителю: монитор возвращает его обратно через HotkeyPressed, и потребитель сам
// сопоставляет id с тем смыслом, который в него вложил (например, с триггером оркестратора).
public sealed record HotkeyDescriptor(int Id, HotkeyModifiers Modifiers, VirtualKey Key);
