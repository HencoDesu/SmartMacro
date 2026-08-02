using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

/// <summary>
/// Everything the panel process owns, which after stage 3 is a very short list: one IPC
/// connection and three thin adapters over it.
///
/// This replaced a full <c>Microsoft.Extensions.Hosting</c> composition root. The container
/// was carrying its weight when the UI hosted the engine — a dozen singletons with a
/// resolution order that mattered — but a graph of four objects with no lifetimes to manage
/// is cheaper to read than to configure, and dropping the host packages is part of the point
/// of the split.
/// </summary>
internal sealed class AppServices : IAsyncDisposable
{
    private readonly IpcClient _client;

    public AppServices(IpcClient client, string macroFolderPath)
    {
        _client = client;
        MacroFolderPath = macroFolderPath;
        MacroLauncher = new IpcMacroLauncher(client);
        HotkeySuspension = new IpcHotkeySuspension(client);
    }

    /// <summary>The daemon connection. Everything the UI shows came through here.</summary>
    public IIpcClient Client => _client;

    /// <summary>UI-thread marshalling for daemon pushes.</summary>
    public IUiDispatcher Dispatcher { get; } = AvaloniaUiDispatcher.Instance;

    /// <summary>"Run this macro" seam for the editor.</summary>
    public IMacroLauncher MacroLauncher { get; }

    /// <summary>Suspend/resume the daemon's global hotkeys around the chord picker.</summary>
    public IHotkeySuspension HotkeySuspension { get; }

    /// <summary>
    /// Absolute path of the daemon's <c>macros/</c> folder, for the editor's "open folder"
    /// button. Resolved from where the daemon executable actually is — in the dev tree that
    /// is a sibling bin directory, not ours.
    /// </summary>
    public string MacroFolderPath { get; }

    /// <summary>
    /// Builds the whole shell — the mode rail plus both mode view-models. Deliberately a
    /// factory rather than a property: the VMs post through Avalonia's dispatcher as soon as
    /// they are constructed, so they must not exist before the framework is initialised.
    ///
    /// D2 folded the macro editor into the shell: it used to be built per dialog, and is now
    /// built once here and kept alive for the life of the window, which is what lets a
    /// half-edited graph survive a trip through another mode.
    /// </summary>
    public ShellViewModel CreateShellViewModel() =>
        new(CreateWorkspaceViewModel(), CreateMacroEditorViewModel(), MacroLauncher);

    /// <summary>Builds the windows-and-runs view-model on its own (used by the designer path).</summary>
    public WorkspaceViewModel CreateWorkspaceViewModel() => new(Client, Dispatcher);

    /// <summary>Builds the macro-editor view-model behind the «Макросы» mode.</summary>
    public MacroEditorViewModel CreateMacroEditorViewModel() =>
        new(Client, MacroLauncher, HotkeySuspension, Dispatcher, MacroFolderPath);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
