using System.Collections.ObjectModel;
using PerfectWorldAgent.App.Mvvm;
using PerfectWorldAgent.Macro;
using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.App.ViewModels;

// VM for MacrosDialog — the global combat macro editor. Each macro is shown as a row
// with name + multi-line text input where every line is "KEY DELAY_MS" (e.g. "F1 5000").
//
// On Save: parse each row's text into MacroStep[], hand to MacroLibrary.ReplaceAsync.
// Parse failures (unknown VirtualKey, bad number) populate ErrorMessage and block save.
public sealed class MacrosDialogViewModel : ObservableObject
{
    private readonly MacroLibrary _library;
    private string? _errorMessage;

    public MacrosDialogViewModel(MacroLibrary library)
    {
        _library = library;
        Rows = new ObservableCollection<MacroRowViewModel>(
            library.Macros.Select(m => new MacroRowViewModel(m.Name, SerializeSteps(m.Steps))));
    }

    public ObservableCollection<MacroRowViewModel> Rows { get; }

    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public void AddMacro()
    {
        Rows.Add(new MacroRowViewModel($"macro{Rows.Count + 1}", string.Empty));
    }

    public void RemoveMacro(MacroRowViewModel row)
    {
        Rows.Remove(row);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var parsed = new List<Macro.Macro>(Rows.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Rows)
        {
            var name = row.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                ErrorMessage = "Every macro needs a name.";
                return false;
            }
            if (!seenNames.Add(name))
            {
                ErrorMessage = $"Duplicate macro name '{name}'.";
                return false;
            }

            if (!TryParseSteps(row.StepsText ?? string.Empty, out var steps, out var parseError))
            {
                ErrorMessage = $"Macro '{name}': {parseError}";
                return false;
            }

            parsed.Add(new Macro.Macro { Name = name, Steps = steps });
        }

        try
        {
            await _library.ReplaceAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save: {ex.Message}";
            return false;
        }

        return true;
    }

    private static string SerializeSteps(IReadOnlyList<MacroStep> steps) =>
        string.Join(Environment.NewLine, steps.Select(s => $"{s.Key} {s.DelayMs}"));

    // Parses "KEY DELAY_MS" lines. Blank lines are skipped. Returns false with a
    // human-readable error message on first malformed line.
    private static bool TryParseSteps(string text, out IReadOnlyList<MacroStep> steps, out string error)
    {
        var result = new List<MacroStep>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                error = $"line {lineNumber}: expected 'KEY DELAY_MS', got '{line}'";
                steps = result;
                return false;
            }

            if (!Enum.TryParse<VirtualKey>(parts[0], ignoreCase: true, out var key))
            {
                error = $"line {lineNumber}: '{parts[0]}' is not a known VirtualKey";
                steps = result;
                return false;
            }

            if (!int.TryParse(parts[1], out var delay) || delay < 0)
            {
                error = $"line {lineNumber}: '{parts[1]}' is not a non-negative integer";
                steps = result;
                return false;
            }

            result.Add(new MacroStep(key, delay));
        }

        steps = result;
        error = string.Empty;
        return true;
    }
}

public sealed class MacroRowViewModel : ObservableObject
{
    private string _name;
    private string _stepsText;

    public MacroRowViewModel(string name, string stepsText)
    {
        _name = name;
        _stepsText = stepsText;
    }

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string StepsText { get => _stepsText; set => SetField(ref _stepsText, value); }
}
