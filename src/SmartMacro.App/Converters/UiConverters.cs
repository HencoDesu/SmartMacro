using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SmartMacro.App.Converters;

// Binding-side value mappings. Immutable singletons, referenced from XAML via x:Static so
// no resource dictionary lookup is involved.

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

/// <summary>Red for validation errors (which block the save), amber for warnings (which don't).</summary>
public sealed class IssueSeverityToBrushConverter : IValueConverter
{
    public static readonly IssueSeverityToBrushConverter Instance = new();

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ErrorBrush : WarningBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Greenish once a window carries at least one tag, muted amber while it is untagged.</summary>
public sealed class TaggedToBrushConverter : IValueConverter
{
    public static readonly TaggedToBrushConverter Instance = new();

    private static readonly IBrush TaggedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0x70));
    private static readonly IBrush UntaggedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TaggedBrush : UntaggedBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
