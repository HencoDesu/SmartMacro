using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Services;

/// <summary>
/// Starts a macro from the UI — the manual equivalent of pressing its hotkey (no context
/// window; the graph routes by tag selector, and the daemon seeds the cursor variable
/// itself). Same testability rationale as <see cref="IHotkeySuspension"/>: the editor VM
/// must not need a live daemon to be unit-tested.
/// </summary>
public interface IMacroLauncher
{
    /// <summary>Fire-and-forget start of <paramref name="macroName"/>. Failures are logged, never thrown.</summary>
    void RunMacro(string macroName);
}

/// <summary>
/// Sends <see cref="IpcMessageTypes.RunMacro"/> and forgets about it.
///
/// Fire-and-forget is faithful to the protocol, not a shortcut: the daemon's reply means
/// "started", never "finished" — a macro can run for hours — so there is nothing for the
/// caller to await. An unknown macro name DOES fail the request, and that lands in the log.
/// </summary>
public sealed class IpcMacroLauncher : IMacroLauncher
{
    private readonly IIpcClient _client;

    public IpcMacroLauncher(IIpcClient client) => _client = client;

    public void RunMacro(string macroName) => _ = RunAsync(macroName);

    private async Task RunAsync(string macroName)
    {
        try
        {
            await _client.RequestAsync(IpcMessageTypes.RunMacro, new RunMacroRequest(macroName)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось запустить макрос '{Macro}'", macroName);
        }
    }
}
