using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Keyboard;
using PerfectWorldAgent.Native.Mouse;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Core;

// Default factory — hard-codes the PostMessage + WM_ACTIVATEAPP-wake-up combo that we
// believe is most likely to work against a frozen PW background client. When we have a
// live game to test against we'll swap this for a config-driven composition; for now
// keeping the wiring in one place beats spreading new() calls across DI registration.
[SupportedOSPlatform("windows")]
public sealed class GameWindowFactory : IGameWindowFactory
{
    private readonly IOptions<ActivatingInputOptions> _activatingOptions;

    public GameWindowFactory(IOptions<ActivatingInputOptions> activatingOptions)
    {
        _activatingOptions = activatingOptions;
    }

    public IGameWindow Create(ProcessInfo info)
    {
        var keyboard = new ActivatingKeyboardInput(new PostMessageKeyboardInput(), _activatingOptions);
        var mouse = new ActivatingMouseInput(new PostMessageMouseInput(), _activatingOptions);
        return new GameWindow(info, keyboard, mouse, _activatingOptions);
    }
}
