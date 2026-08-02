using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Agents;

// One agent per watched game-client process — now purely a WINDOW LIFETIME OWNER.
//
// Everything that used to make this class interesting (boot flow, identification, inbox
// commands, per-class keys) moved into macro graphs: the orchestrator runs those against
// window handles, and the primitives layer resolves a handle back to this window through
// WindowRegistry. What's left is the part macros can't do for themselves:
//
//   * Start()  — registers the window (handle + process name + drivable facade) in
//                WindowRegistry and starts the aliveness loop.
//   * the loop — notices when the client is gone and tears the registration down, which
//                is what removes the window from every macro's selector reach.
//   * Stop()   — cancels the loop for orderly shutdown.
//
// Identity lives entirely in the registry: "identified" just means "carries at least one
// tag". Tags are applied by RecognizeTagNode/AddTagNode, or by hand from the UI.
//
// TODO(W0.3): now that this is a lifetime shell, folding it into WindowRegistry (or a
// small WindowHost) is the natural next simplification — out of scope for W0.2b.
public sealed partial class CharacterAgent
{
    private readonly IGameWindow _window;
    private readonly string _processName;
    private readonly WindowRegistry _registry;
    private readonly ChannelWriter<AgentMessage> _outbox;
    private readonly TimeSpan _pollInterval;
    private readonly string _placeholderName;
    private readonly ILogger<CharacterAgent> _logger;

    private CancellationTokenSource? _runCts;

    public CharacterAgent(
        IGameWindow window,
        string processName,
        WindowRegistry registry,
        ChannelWriter<AgentMessage> outbox,
        IOptions<AgentOptions> options,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _processName = processName;
        _registry = registry;
        _outbox = outbox;
        _pollInterval = TimeSpan.FromSeconds(options.Value.AgentPollIntervalSeconds);
        _logger = logger;

        // Born tagless — placeholder name unique per-hwnd so it doesn't clash with
        // tagged windows until the first tag lands.
        _placeholderName = $"Unknown (hwnd=0x{window.Handle.ToInt64():X})";
    }

    /// <summary>Display name — the window's first tag, or a per-hwnd placeholder until tagged.</summary>
    public string Name => FirstTagOrNull() ?? _placeholderName;

    /// <summary>
    /// Coarse state string for UI binding, derived from <see cref="IsIdentified"/>.
    /// Transitional — W0.3 rebuilds the UI around windows + tag chips.
    /// </summary>
    public string State => IsIdentified ? "Idle" : "AwaitingIdentification";

    /// <summary>Identified = the registry holds at least one tag for this window.</summary>
    public bool IsIdentified => _registry.GetTags(Handle).Count > 0;

    /// <summary>Underlying game-window handle — the key macros address this window by.</summary>
    public IntPtr Handle => _window.Handle;

    public Task? RunningTask { get; private set; }

    /// <summary>Active capture of the current game window. Exposed for the diagnostics dump flow.</summary>
    public byte[] CaptureScreenshot() => _window.CaptureScreenshot();

    /// <summary>
    /// Registers the window in <see cref="WindowRegistry"/> — handle, process name, and
    /// the facade the macro primitives drive it through — and starts the aliveness loop.
    /// Macros triggered by this window's process must not start before this returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called on an already-running agent.</exception>
    public void Start()
    {
        if (RunningTask is not null)
        {
            throw new InvalidOperationException($"Agent '{Name}' is already started.");
        }

        _registry.Register(Handle, _processName, _window);

        _runCts = new CancellationTokenSource();
        RunningTask = Task.Run(() => RunLoopAsync(_runCts.Token));
    }

    /// <summary>
    /// Signals the run loop to stop. The loop exits, unregisters the window from
    /// <see cref="WindowRegistry"/>, writes <see cref="AgentStoppingMessage"/>, and the
    /// task completes via <see cref="RunningTask"/>.
    /// </summary>
    public void Stop()
    {
        _runCts?.Cancel();
    }

    // Window-death detection, nothing else. Ticking is cheap (one IsWindow call) and the
    // registry entry has to disappear promptly: a dead hwnd left registered would keep
    // matching tag selectors, so every fan-out would waste an activation cycle on it.
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        LogStarted(Name, _pollInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_window.IsAlive)
                {
                    LogWindowGone();
                    break;
                }

                try
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(ex);
        }
        finally
        {
            LogStopped(Name);
            // Window is gone (or we're shutting down) — the registry entry and its tags
            // die with it. Raises WindowClosed for registry subscribers.
            _registry.Unregister(Handle);
            _outbox.TryWrite(new AgentStoppingMessage(this));
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private string? FirstTagOrNull()
    {
        foreach (var tag in _registry.GetTags(Handle))
        {
            return tag;
        }
        return null;
    }
}
