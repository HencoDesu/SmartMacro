using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SmartMacro.App.Converters;

// Binding-side value mappings. Immutable singletons, referenced from XAML via x:Static so
// no resource dictionary lookup is involved.

/// <summary>
/// Pulls a brush out of the Nocturne token dictionary by key.
///
/// Converters are the one place a colour cannot be written as XAML, and hard-coding one
/// here is how a design system starts to drift — so the value is looked up in
/// Themes/Tokens.axaml at first use instead. The <paramref name="fallback"/> only matters
/// before <c>Application.Current</c> exists (the designer, and unit tests), which is also
/// why a miss is not cached.
/// </summary>
internal static class NocturneBrushes
{
    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.Ordinal);

    public static IBrush Get(string key, uint fallback)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (Application.Current is { } app
                && app.TryFindResource(key, out var value)
                && value is IBrush brush)
            {
                Cache[key] = brush;
                return brush;
            }
        }

        return new SolidColorBrush(Color.FromUInt32(fallback));
    }
}

/// <summary>
/// True iff the source value is non-null / non-empty — hides the error and status labels
/// when there is nothing to say.
/// </summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public static readonly NotNullToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => false,
            string s => !string.IsNullOrEmpty(s),
            _ => true,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders an edge's target node id. The empty string is the model's <c>null</c> edge, so
/// it needs a visible name of its own — a blank drop-down row would read as "not filled in
/// yet" rather than "the run ends here".
/// </summary>
public sealed class NodeIdDisplayConverter : IValueConverter
{
    public static readonly NodeIdDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? s : "— конец —";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks one of two Nocturne tokens from a boolean, named by the converter parameter as
/// <c>"TrueKey|FalseKey"</c> — e.g. <c>"NocturneAccent400Brush|NocturneTextFaintBrush"</c>.
///
/// One parameterised converter rather than a family of one-off classes: D2 alone needs
/// "accent when live", "alternate row background" and "muted when untagged", and every one
/// of them is the same two-token choice. The keys are still real token names, so the
/// "no literal colours in markup" rule holds.
/// </summary>
public sealed class TokenBrushConverter : IValueConverter
{
    public static readonly TokenBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string spec)
        {
            return null;
        }

        var separator = spec.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var key = value is true
            ? spec[..separator]
            : spec[(separator + 1)..];
        // Fallback is deliberately the neutral mid-grey: only reachable in the designer and
        // in unit tests, where Application.Current has no resources yet.
        return NocturneBrushes.Get(key, 0xFF9397AB);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Danger for validation errors (which block the save), warning amber for the rest.</summary>
public sealed class IssueSeverityToBrushConverter : IValueConverter
{
    public static readonly IssueSeverityToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? NocturneBrushes.Get("NocturneDangerBrush", 0xFFDD8189)
            : NocturneBrushes.Get("NocturneWarningBrush", 0xFFDBB277);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Full text once a window carries at least one tag, warning amber while it is untagged.
///
/// The pre-D1 version used green-for-good; Nocturne is monochrome plus one accent, so
/// "fine" is simply normal text and only the state that wants attention is coloured.
/// </summary>
public sealed class TaggedToBrushConverter : IValueConverter
{
    public static readonly TaggedToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? NocturneBrushes.Get("NocturneTextBrush", 0xFFE9E9ED)
            : NocturneBrushes.Get("NocturneWarningBrush", 0xFFDBB277);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
