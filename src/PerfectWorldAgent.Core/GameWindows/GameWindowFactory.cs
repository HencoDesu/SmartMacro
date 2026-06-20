using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Keyboard;
using PerfectWorldAgent.Native.Mouse;
using PerfectWorldAgent.ProcessMonitoring;

namespace PerfectWorldAgent.GameWindows;

// Default factory — wires input strategies into GameWindow. Activation (WM_ACTIVATEAPP
// wake-up of frozen background PW clients) is the caller's responsibility via
// IGameWindow.ActivateAsync/DeactivateAsync.
//
// Current mix (targeted experiment):
//   * Keyboard → SendMessage. PW's WndProc appears to gate keyboard input on internal
//     "active" state; synchronous delivery guarantees PW has finished processing the
//     keypress before we move on. Observed problem with PostMessage variant: 1-2 of 11
//     agents intermittently missed immunity broadcasts even with 30-50ms settle delay.
//   * Mouse → PostMessage. Clicks reach PW reliably regardless (likely no focus check
//     in PW's mouse handler), so the blocking overhead of SendMessage isn't warranted.
//
// If this proves stable, we can collapse to all-Send for consistency. If keyboard
// reliability is still an issue, the problem is inside PW (state check we can't
// influence from outside) and the next step is icon-click broadcasts instead of keys.
[SupportedOSPlatform("windows")]
public sealed class GameWindowFactory : IGameWindowFactory
{
    private readonly IOptions<ActivatingInputOptions> _activatingOptions;
    private readonly IOptions<WindowVisionOptions> _visionOptions;
    private readonly ILoggerFactory _loggerFactory;

    public GameWindowFactory(
        IOptions<ActivatingInputOptions> activatingOptions,
        IOptions<WindowVisionOptions> visionOptions,
        ILoggerFactory loggerFactory)
    {
        _activatingOptions = activatingOptions;
        _visionOptions = visionOptions;
        _loggerFactory = loggerFactory;
    }

    public IGameWindow Create(ProcessInfo info) =>
        new GameWindow(
            info,
            new SendMessageKeyboardInput(),
            new PostMessageMouseInput(),
            _activatingOptions,
            _visionOptions,
            _loggerFactory.CreateLogger<GameWindow>());
}
