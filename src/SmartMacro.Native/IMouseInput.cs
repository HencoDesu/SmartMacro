namespace SmartMacro.Native;

// Отправляет действия мышью по координатам клиентской области целевого окна. Пространство
// компромиссов то же, что у IKeyboardInput: разные реализации под разные требования к
// фокусу и совместимости.
public interface IMouseInput
{
    /// <summary>
    /// Посылает клик левой кнопкой по заданным координатам клиентской области.
    /// </summary>
    Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Посылает двойной клик левой кнопкой по заданным координатам клиентской области.
    /// </summary>
    Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);
}
