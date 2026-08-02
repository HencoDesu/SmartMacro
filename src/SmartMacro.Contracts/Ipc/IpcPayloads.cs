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

/// <summary>Payload of the <see cref="IpcMessageTypes.WindowClosed"/> event.</summary>
/// <param name="Hwnd">Handle of the window that went away. No other data survives it.</param>
public sealed record WindowClosedEvent(long Hwnd);
