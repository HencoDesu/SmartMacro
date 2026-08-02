using Avalonia.Threading;

namespace SmartMacro.App.Mvvm;

/// <summary>
/// The "get me onto the UI thread" seam. Core raises its events (window registered, tags
/// changed, macro library reloaded, runs changed) from arbitrary threadpool threads, and
/// an <c>ObservableCollection</c> may only be mutated on the UI thread — so every VM that
/// subscribes to Core has to marshal.
///
/// It exists as an interface purely for testability: a headless VM test wants those
/// handlers to run inline and synchronously, and <see cref="Dispatcher.UIThread"/> has no
/// pump outside a running Avalonia app, so posted work would simply never execute.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> for execution on the UI thread. Never blocks.</summary>
    void Post(Action action);
}

/// <summary>Production implementation — posts onto Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>
    /// Shared instance. Deliberately does not touch <see cref="Dispatcher.UIThread"/> until
    /// <see cref="Post"/> is called, so merely constructing it (as a VM's default) is safe
    /// in a process with no Avalonia application.
    /// </summary>
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Runs the action inline on the calling thread. Used by headless VM tests (and safe as a
/// design-time default) — never by the running app, where it would mutate observable
/// collections off the UI thread.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public void Post(Action action) => action();
}
