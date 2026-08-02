using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// The seam between the graph walker and the real world: input synthesis, vision, and
/// window cosmetics. The executor is written entirely against this interface so the
/// walker is unit-testable with fakes, and a future ScriptNode/Lua layer binds to the
/// same operations. W0.2b implements it over IGameWindow / AgentInputDispatcher / vision.
/// Tag operations are deliberately NOT here — they go through <c>WindowRegistry</c>
/// directly (single owner of tag state).
/// </summary>
public interface IMacroPrimitives
{
    /// <summary>Presses <paramref name="key"/> on the window.</summary>
    Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct);

    /// <summary>Left-clicks at client-space <paramref name="point"/>; <paramref name="doubleClick"/> for a double-click.</summary>
    Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct);

    /// <summary>
    /// Single-shot template match against the window (cropped to <paramref name="region"/>
    /// when given). Returns the match center, or <c>null</c> when not found.
    /// </summary>
    Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, string template, ScreenRect? region, CancellationToken ct);

    /// <summary>
    /// Polls for a template until it appears or <paramref name="timeoutMs"/> elapses.
    /// Returns the match center, or <c>null</c> on timeout.
    /// </summary>
    Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, string template, ScreenRect? region, int timeoutMs, CancellationToken ct);

    /// <summary>
    /// Matches a template set against <paramref name="region"/> of the window. Returns the
    /// best-matching template's name (= tag), or <c>null</c> when nothing clears the threshold.
    /// </summary>
    Task<string?> RecognizeAsync(IntPtr hwnd, string templateSet, ScreenRect region, CancellationToken ct);

    /// <summary>Sets the window's icon to the image at <paramref name="iconPath"/>.</summary>
    Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct);
}
