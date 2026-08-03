using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// The editor's live picture of what the daemon is managing: every tracked window with its
/// current tags.
///
/// It exists so the targets badge (mockup 1g) can answer «сколько окон сейчас попадёт»
/// without a round trip. One instance is owned by <c>MacroEditorViewModel</c> and handed to
/// every <see cref="TargetSelectorViewModel"/> of the open graph — the same pattern the
/// editor already uses for <c>NodeIdChoices</c> and <c>MacroChoices</c>, so a window
/// appearing updates every badge on the canvas at once.
///
/// <b>Why a second copy of the window list</b> (<c>WorkspaceViewModel</c> has one too): the
/// two are different shapes for different jobs. Workspace holds editable ROWS with tag
/// chips, a half-typed tag box and focus state; this holds the raw DTOs and nothing else,
/// and lives and dies with the editor. Injecting the workspace into the editor would tie two
/// modes together and drag the whole «Окна» view-model into every editor test.
///
/// Not an <c>ObservableObject</c>: nothing binds to it directly. Consumers subscribe to
/// <see cref="Changed"/> and re-derive.
/// </summary>
public sealed class WindowCatalog
{
    private IReadOnlyList<WindowDto> _windows = [];

    /// <summary>Raised after any change to the set of windows or to any window's tags.</summary>
    public event Action? Changed;

    /// <summary>Current snapshot, in the order the daemon reported it.</summary>
    public IReadOnlyList<WindowDto> Windows => _windows;

    /// <summary>How many windows the daemon is tracking right now.</summary>
    public int Count => _windows.Count;

    /// <summary>Replaces the whole snapshot — the answer to <c>GetWindows</c>.</summary>
    public void Reset(IReadOnlyList<WindowDto> windows)
    {
        _windows = windows ?? [];
        Changed?.Invoke();
    }

    /// <summary>
    /// Add-or-replace by handle. Serves both <c>WindowAppeared</c> and
    /// <c>WindowTagsChanged</c>, which carry the same full-state payload.
    /// </summary>
    public void Upsert(WindowDto window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var next = new List<WindowDto>(_windows.Count + 1);
        var replaced = false;
        foreach (var existing in _windows)
        {
            if (existing.Hwnd == window.Hwnd)
            {
                next.Add(window);
                replaced = true;
            }
            else
            {
                next.Add(existing);
            }
        }
        if (!replaced)
        {
            next.Add(window);
        }

        _windows = next;
        Changed?.Invoke();
    }

    /// <summary>Drops a window that closed. Unknown handles are a no-op.</summary>
    public void Remove(long hwnd)
    {
        var next = _windows.Where(window => window.Hwnd != hwnd).ToList();
        if (next.Count == _windows.Count)
        {
            return;
        }
        _windows = next;
        Changed?.Invoke();
    }
}
