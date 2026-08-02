using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.Native.Keyboard;
using SmartMacro.Native.Mouse;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.GameWindows;

// Default factory — wires input strategies into GameWindow and resolves the process's
// activation profile. Activation (WM_ACTIVATEAPP wake-up of frozen background PW clients)
// is the caller's responsibility via IGameWindow.ActivateAsync/DeactivateAsync; the
// parameters for it come from the ProcessProfile matching the window's process name
// (falling back to inert defaults — plain input, no wake-up dance — when no profile
// is configured).
//
// Current input mix (targeted experiment):
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
    private readonly IOptions<ProcessProfileOptions> _profileOptions;
    private readonly IOptions<WindowVisionOptions> _visionOptions;
    private readonly ILoggerFactory _loggerFactory;

    public GameWindowFactory(
        IOptions<ProcessProfileOptions> profileOptions,
        IOptions<WindowVisionOptions> visionOptions,
        ILoggerFactory loggerFactory)
    {
        _profileOptions = profileOptions;
        _visionOptions = visionOptions;
        _loggerFactory = loggerFactory;
    }

    public IGameWindow Create(ProcessInfo info)
    {
        var profile = _profileOptions.Value.FindByProcessName(info.ProcessName) ?? ProcessProfile.Inert;
        return new GameWindow(
            info,
            profile,
            new SendMessageKeyboardInput(),
            new PostMessageMouseInput(),
            _visionOptions,
            _loggerFactory.CreateLogger<GameWindow>());
    }
}
