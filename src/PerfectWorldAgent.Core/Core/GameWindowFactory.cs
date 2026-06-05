using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Keyboard;
using PerfectWorldAgent.Native.Mouse;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Core;

// Default factory — wires PostMessage-based input directly into GameWindow. Activation
// (WM_ACTIVATEAPP wake-up of frozen background PW clients) is now the caller's
// responsibility via IGameWindow.ActivateAsync/DeactivateAsync. The previous decorator-
// wrapper architecture (ActivatingKeyboardInput / ActivatingMouseInput) added activation
// per-call which silently fragmented input sequences and motivated overengineered API
// like PressChordThenKeyAsync.
[SupportedOSPlatform("windows")]
public sealed class GameWindowFactory : IGameWindowFactory
{
    private readonly IOptions<ActivatingInputOptions> _activatingOptions;

    public GameWindowFactory(IOptions<ActivatingInputOptions> activatingOptions)
    {
        _activatingOptions = activatingOptions;
    }

    public IGameWindow Create(ProcessInfo info) =>
        new GameWindow(info, new PostMessageKeyboardInput(), new PostMessageMouseInput(), _activatingOptions);
}
