using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Keyboard;

// Асинхронный ввод через очередь сообщений. Бьёт в конкретный hwnd, фокус не требуется.
// Скорее всего сработает с кооперативными приложениями; многие игры на DirectInput/Raw Input
// его проигнорируют.
//
// ⚠️ НЕ ПОДКЛЮЧЕН и намеренно не является вариантом по умолчанию: GameWindowFactory выбирает
// SendMessageKeyboardInput, потому что наблюдалось, как post'нутые клавиши теряются на
// замороженных фоновых клиентах PW (случайные 1–2 окна из 9 — очередь просто никто не
// прокачивал). Класс оставлен как альтернативная стратегия для кооперативного, не
// замораживающегося целевого процесса; не подставляйте его для PW, не перепроверив в игре.
[SupportedOSPlatform("windows")]
public sealed class PostMessageKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _keyHoldDuration;

    public PostMessageKeyboardInput(TimeSpan? keyHoldDuration = null)
    {
        _keyHoldDuration = keyHoldDuration ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var wParam = (IntPtr)(ushort)key;
        var downLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: false);
        var upLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: true);

        Post(hwnd, User32Native.WM_KEYDOWN, wParam, downLParam);
        await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
        Post(hwnd, User32Native.WM_KEYUP, wParam, upLParam);
    }

    private static void Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!User32Native.PostMessage(hwnd, msg, wParam, lParam))
        {
            throw new InvalidOperationException($"PostMessage 0x{msg:X4} failed for hwnd 0x{hwnd:X}");
        }
    }
}
