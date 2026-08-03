using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Keyboard;

// Синхронный ввод с клавиатуры через SendMessage. Каждое WM_KEYDOWN/UP блокирует нас, пока
// WndProc целевого окна не вернёт управление, — то есть гарантирует, что PW действительно
// обработал нажатие, прежде чем мы пойдём дальше. Размен по сравнению с PostMessage:
//
//   * ХОРОШО: нет тонкостей с порядком в очереди, нет гонок «а PW уже вынул это из очереди?».
//     Если внутри WndProc у PW есть проверка «обрабатывать клавиши только когда активен», то
//     состояние активации, выставленное нашим (синхронным) WM_ACTIVATEAPP, успеет
//     распространиться до прихода клавиши.
//   * ПЛОХО: блокирует наш поток задачи на всё время работы обработчика PW. Под нагрузкой
//     (PW в середине отрисовки) отправка клавиши может занять десятки миллисекунд. PostMessage
//     же работает по принципу «отправил и забыл».
//
// Именно эту стратегию GameWindowFactory зашивает для каждого нажатия клавиши: наблюдалось,
// что post'нутые клавиши теряются на замороженных фоновых клиентах (случайные 1–2 окна из 9).
// Мышь остаётся на PostMessage: клики и так доходят до PW надёжно, а SendMessage добавил бы
// блокировку без видимого выигрыша.
[SupportedOSPlatform("windows")]
public sealed class SendMessageKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _keyHoldDuration;

    public SendMessageKeyboardInput(TimeSpan? keyHoldDuration = null)
    {
        _keyHoldDuration = keyHoldDuration ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var wParam = (IntPtr)(ushort)key;
        var downLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: false);
        var upLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: true);

        User32Native.SendMessage(hwnd, User32Native.WM_KEYDOWN, wParam, downLParam);
        await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
        User32Native.SendMessage(hwnd, User32Native.WM_KEYUP, wParam, upLParam);
    }
}
