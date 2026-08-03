using SmartMacro.Native;

namespace SmartMacro.Contracts.Dto;

/// <summary>
/// One chord the daemon has a macro bound to but could NOT register with Win32
/// <c>RegisterHotKey</c> — because some other application, or the shell itself, already
/// owns that combination system-wide.
///
/// This exists because the failure is otherwise invisible from the panel: the daemon logs a
/// warning to a file the pipe never carries, and the user is left with a hotkey that looks
/// bound in the editor and does nothing at all when pressed. It is a DIFFERENT failure from
/// the editor's own conflict check ("this chord is already a trigger of pw-immunity"), which
/// the panel answers on its own from the library — only the daemon can see this one.
///
/// Mouse chords never appear here: they ride a low-level hook rather than
/// <c>RegisterHotKey</c> and have no registration to lose.
/// </summary>
/// <param name="MacroName">Macro whose <c>HotkeyTrigger</c> was rejected.</param>
/// <param name="Modifiers">Modifier flags of the rejected chord.</param>
/// <param name="Key">Main key of the rejected chord.</param>
public sealed record HotkeyFailureDto(string MacroName, HotkeyModifiers Modifiers, VirtualKey Key);
