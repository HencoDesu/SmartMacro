namespace SmartMacro.Native;

// Отправляет одну клавишу (нажатие → удержание → отпускание) в целевое окно. Реализации
// отличаются механизмом доставки (PostMessage / SendMessage / SendInput) и по-разному
// разменивают требования к фокусу на совместимость с играми, которые игнорируют оконные
// сообщения.
public interface IKeyboardInput
{
    /// <summary>
    /// Отправляет одну клавишу в указанное окно парой <c>WM_KEYDOWN</c> → удержание →
    /// <c>WM_KEYUP</c>.
    /// </summary>
    Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default);
}
