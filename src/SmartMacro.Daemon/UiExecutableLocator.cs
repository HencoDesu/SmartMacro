namespace SmartMacro.Daemon;

/// <summary>
/// Resolves the path of the Avalonia panel executable that the tray's "Открыть панель" item
/// launches.
/// </summary>
/// <remarks>
/// The two executables ship side by side in one output directory, so "next to me" is the
/// whole search strategy — no registry, no PATH, no configuration. Kept as a pure function
/// over a base directory plus an injected existence probe so it can be tested without
/// touching the filesystem.
/// </remarks>
public static class UiExecutableLocator
{
    /// <summary>File name of the UI process, as produced by <c>SmartMacro.App.csproj</c>.</summary>
    public const string UiExecutableName = "SmartMacro.App.exe";

    /// <summary>
    /// The single path we look at. Exposed separately so a failed <see cref="Resolve"/> can
    /// be logged with the exact location that was probed — "not found" without a path is a
    /// useless diagnostic.
    /// </summary>
    public static string ProbePath(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        return Path.Combine(baseDirectory, UiExecutableName);
    }

    /// <summary>
    /// Returns the full path to the UI executable, or <c>null</c> when it isn't there.
    /// </summary>
    /// <param name="baseDirectory">Directory to look in — normally <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Existence probe; defaults to <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        var candidate = ProbePath(baseDirectory);
        var exists = fileExists ?? File.Exists;
        return exists(candidate) ? candidate : null;
    }
}
