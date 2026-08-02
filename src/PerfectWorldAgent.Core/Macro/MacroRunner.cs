using Microsoft.Extensions.Logging;
using PerfectWorldAgent.GameWindows;
using PerfectWorldAgent.Input;

namespace PerfectWorldAgent.Macro;

/// <summary>
/// Executes a flat <see cref="MacroAction"/> sequence against a game window:
///   * <see cref="KeyPressAction"/> → <see cref="AgentInputDispatcher.FireKeyAsync"/>
///   * <see cref="DelayAction"/>    → <see cref="Task.Delay(int, CancellationToken)"/>
///
/// Stateless — safe as a singleton in DI. Concurrency / single-flight is the caller's
/// responsibility (CharacterAgent uses <c>_operationLock</c>).
/// </summary>
public sealed partial class MacroRunner
{
    private readonly AgentInputDispatcher _input;
    private readonly ILogger<MacroRunner> _logger;

    public MacroRunner(AgentInputDispatcher input, ILogger<MacroRunner> logger)
    {
        _input = input;
        _logger = logger;
    }

    /// <summary>
    /// Runs every action in <paramref name="actions"/> sequentially. Throws
    /// <see cref="OperationCanceledException"/> if the token is cancelled mid-run.
    /// </summary>
    public async Task RunAsync(
        string macroName,
        IReadOnlyList<MacroAction> actions,
        IGameWindow window,
        string agentName,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[i];
            switch (action)
            {
                case KeyPressAction key:
                    await _input.FireKeyAsync(window, key.Key, $"Macro({macroName})", agentName).ConfigureAwait(false);
                    break;
                case DelayAction delay when delay.Ms > 0:
                    await Task.Delay(delay.Ms, cancellationToken).ConfigureAwait(false);
                    break;
                case DelayAction:
                    // Zero / negative delay = no-op; skip.
                    break;
                case ClickAction click:
                    await _input.FireClickAsync(window, click.Point, click.DoubleClick, agentName).ConfigureAwait(false);
                    break;
                default:
                    LogUnknownAction(macroName, action.GetType().Name);
                    break;
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Macro '{MacroName}': unknown action type {Type} — skipped")]
    partial void LogUnknownAction(string macroName, string type);
}
