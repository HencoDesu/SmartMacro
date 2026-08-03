using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;

namespace SmartMacro.Contracts.Ipc;

// The typed payloads of the catalog in IpcMessageTypes. Only the shapes that need named
// fields live here: responses that are a bare array (WindowDto[], RunningMacroDto[],
// MacroGraph[], ValidationIssueDto[]) or a bare scalar (DumpCaptures → string) are
// serialized as-is, and the argument-less requests/events carry no payload at all.

/// <summary>Payload of <see cref="IpcMessageTypes.AddTag"/>.</summary>
/// <param name="Hwnd">Handle of the window to tag, as reported in <c>WindowDto.Hwnd</c>.</param>
/// <param name="Tag">Tag to add. Free-form, case-sensitive.</param>
public sealed record AddTagRequest(long Hwnd, string Tag);

/// <summary>Payload of <see cref="IpcMessageTypes.RemoveTag"/>.</summary>
/// <param name="Hwnd">Handle of the window to untag, as reported in <c>WindowDto.Hwnd</c>.</param>
/// <param name="Tag">Tag to remove. Removing a tag the window doesn't carry is a no-op, not an error.</param>
public sealed record RemoveTagRequest(long Hwnd, string Tag);

/// <summary>Payload of <see cref="IpcMessageTypes.RunMacro"/>.</summary>
/// <param name="Name">Macro to run. Unknown names fail the request.</param>
public sealed record RunMacroRequest(string Name);

/// <summary>Payload of <see cref="IpcMessageTypes.StopMacro"/>.</summary>
/// <param name="RunId">Run to cancel, from <c>RunningMacroDto.RunId</c>. An already-finished run succeeds silently.</param>
public sealed record StopMacroRequest(Guid RunId);

/// <summary>Payload of <see cref="IpcMessageTypes.SaveMacro"/>.</summary>
/// <param name="Macro">The graph to persist. Its <see cref="MacroGraph.Name"/> is the file name.</param>
public sealed record SaveMacroRequest(MacroGraph Macro);

/// <summary>Payload of <see cref="IpcMessageTypes.DeleteMacro"/>.</summary>
/// <param name="Name">Macro to delete. Deleting a macro that isn't there is a no-op, not an error.</param>
public sealed record DeleteMacroRequest(string Name);

/// <summary>Payload of <see cref="IpcMessageTypes.SubscribeRunEvents"/>.</summary>
/// <param name="Enabled">
/// <c>true</c> starts the stream for THIS connection, <c>false</c> stops it. Per-connection
/// and not global: a second client that never asked must not be handed the burst, and a
/// daemon nobody is watching must not pay for the events at all.
/// </param>
public sealed record SubscribeRunEventsRequest(bool Enabled);

/// <summary>Payload of the <see cref="IpcMessageTypes.WindowClosed"/> event.</summary>
/// <param name="Hwnd">Handle of the window that went away. No other data survives it.</param>
public sealed record WindowClosedEvent(long Hwnd);

/// <summary>
/// Payload of <see cref="IpcMessageTypes.SetBreakpoints"/>. Replaces the whole set for one
/// macro — the editor always knows every breakpoint it has, so a full replacement removes an
/// entire class of "add/remove got out of order" bugs for the price of a few extra bytes.
/// </summary>
/// <param name="MacroName">Graph the breakpoints belong to. The macro need not exist yet — an unsaved draft can carry them.</param>
/// <param name="NodeIds">Nodes that should halt a walk. An EMPTY array clears the macro's breakpoints.</param>
public sealed record SetBreakpointsRequest(string MacroName, IReadOnlyList<string> NodeIds);

/// <summary>Payload of <see cref="IpcMessageTypes.DebugCommand"/>.</summary>
/// <param name="WalkId">Walk to act on, from <c>RunWalkDto.WalkId</c>. An unknown (finished) walk answers <c>Accepted = false</c>.</param>
/// <param name="Command">What to do.</param>
/// <param name="NodeId">Target node for <see cref="DebugCommand.RunToNode"/>; ignored otherwise.</param>
public sealed record DebugCommandRequest(Guid WalkId, DebugCommand Command, string? NodeId = null);
