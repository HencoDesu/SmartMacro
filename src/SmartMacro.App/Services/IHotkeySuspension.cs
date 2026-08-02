using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Services;

/// <summary>
/// Lets the macro editor switch the daemon's global hotkeys off for as long as it is open.
///
/// Win32 <c>RegisterHotKey</c> swallows presses of a chord that is already registered:
/// the owning window gets a WM_HOTKEY and nobody else sees the key at all. That owner is now
/// a different PROCESS — the daemon — which makes no difference to the problem: while the
/// hotkey-trigger picker is on screen, the very combinations the user most likely wants to
/// re-bind (the ones already bound to a macro) would never reach it. Suspending for the
/// lifetime of the dialog is still the only fix.
///
/// It stays an interface so the editor VM can be tested headlessly without a daemon.
/// </summary>
public interface IHotkeySuspension
{
    /// <summary>Unregisters every chord. Idempotent.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-registers from the current macro library. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Forwards suspend/resume to the daemon over the control pipe.
///
/// Failures are logged and swallowed: a picker that cannot suspend still works for every
/// chord that isn't already bound, whereas an exception out of the dialog's OnOpened would
/// take the editor down with it.
/// </summary>
public sealed class IpcHotkeySuspension : IHotkeySuspension
{
    private readonly IIpcClient _client;

    public IpcHotkeySuspension(IIpcClient client) => _client = client;

    public Task SuspendAsync(CancellationToken cancellationToken = default) =>
        SendAsync(IpcMessageTypes.SuspendHotkeys, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        SendAsync(IpcMessageTypes.ResumeHotkeys, cancellationToken);

    private async Task SendAsync(string type, CancellationToken cancellationToken)
    {
        try
        {
            await _client.RequestAsync(type, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось выполнить '{Request}'", type);
        }
    }
}
