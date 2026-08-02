using System.Text.Json;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// A client → daemon call. One JSON object per line (JSON Lines) on the control pipe.
///
/// <see cref="Payload"/> stays a raw <see cref="JsonElement"/> so the envelope can be read
/// without knowing the message: the dispatcher switches on <see cref="Type"/> and only
/// then materializes the typed payload (see <see cref="IpcJson.Read{T}"/>). An unknown
/// <see cref="Type"/> therefore deserializes fine — rejecting it is the dispatcher's
/// decision, not the parser's.
/// </summary>
/// <param name="Id">Correlation id, unique per connection. Echoed by <see cref="IpcResponse.Id"/>.</param>
/// <param name="Type">One of the <see cref="IpcMessageTypes"/> request constants.</param>
/// <param name="Payload">Request arguments, or <c>null</c> for the argument-less requests.</param>
public sealed record IpcRequest(int Id, string Type, JsonElement? Payload = null);

/// <summary>
/// The daemon's answer to exactly one <see cref="IpcRequest"/>.
/// <see cref="Ok"/> <c>false</c> ⇒ <see cref="Error"/> is set and <see cref="Payload"/> is
/// meaningless; <see cref="Ok"/> <c>true</c> with a <c>null</c> payload is the normal
/// shape for the "just do it" requests.
/// </summary>
/// <param name="Id">Correlation id copied from the request.</param>
/// <param name="Ok">Whether the request succeeded.</param>
/// <param name="Payload">Result data, shaped per <see cref="IpcMessageTypes"/>; <c>null</c> for ok-only replies.</param>
/// <param name="Error">Failure description when <paramref name="Ok"/> is <c>false</c>.</param>
public sealed record IpcResponse(int Id, bool Ok, JsonElement? Payload = null, string? Error = null);

/// <summary>
/// An unsolicited daemon → client push. Uncorrelated (no id) — events are broadcast to
/// every connected client.
/// </summary>
/// <param name="Type">One of the <see cref="IpcMessageTypes"/> event constants.</param>
/// <param name="Payload">Event data, or <c>null</c> for the "something changed, re-fetch" events.</param>
public sealed record IpcEvent(string Type, JsonElement? Payload = null);
