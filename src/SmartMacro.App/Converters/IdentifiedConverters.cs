using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SmartMacro.App.Converters;

// IValueConverter — Avalonia binding-side mapping from bool IsIdentified → display text
// and accent colour. Stateless singletons, referenced from XAML via x:Static.

public sealed class IdentifiedToLabelConverter : IValueConverter
{
    public static readonly IdentifiedToLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "identified" : "unidentified";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IdentifiedToBrushConverter : IValueConverter
{
    public static readonly IdentifiedToBrushConverter Instance = new();

    // Greenish for identified, amber for awaiting — picked to be readable on dark theme.
    private static readonly IBrush IdentifiedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0x70));
    private static readonly IBrush UnidentifiedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? IdentifiedBrush : UnidentifiedBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// "show this UI element only when the boolean is false" — used to hide the Label button
// once an agent is identified.
public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? false : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Convert(value, targetType, parameter, culture);
}

// True iff the source value is non-null / non-empty — used to hide an error label when
// no error is present.
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
