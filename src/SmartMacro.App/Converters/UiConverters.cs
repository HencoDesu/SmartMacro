using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SmartMacro.App.Converters;

// Преобразования значений на стороне binding. Неизменяемые синглтоны, на которые ссылаются из
// XAML через x:Static, так что поиск по словарю ресурсов вообще не задействован.

/// <summary>
/// Достаёт кисть из словаря токенов Nocturne по ключу.
///
/// Конвертеры — единственное место, где цвет невозможно записать как XAML, а зашить его прямо
/// здесь — это ровно то, с чего начинается расползание дизайн-системы. Поэтому значение при
/// первом обращении ищется в Themes/Tokens.axaml. Аргумент <c>fallback</c> имеет значение только
/// до того, как появился <c>Application.Current</c> (дизайнер и юнит-тесты), — по этой же причине
/// промах не кэшируется.
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
/// Истина тогда и только тогда, когда исходное значение не null и не пусто, — прячет подписи
/// ошибок и статуса, когда сказать нечего.
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
/// Рисует идентификатор ноды, куда ведёт ребро. Пустая строка — это <c>null</c>-ребро модели, и
/// ему нужно собственное видимое имя: пустая строка в выпадающем списке читалась бы как «ещё не
/// заполнено», а не как «здесь прогон заканчивается».
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
/// Выбирает один из двух токенов Nocturne по булеву значению; какие именно — задаёт параметр
/// конвертера в виде <c>"TrueKey|FalseKey"</c>, например
/// <c>"NocturneAccent400Brush|NocturneTextFaintBrush"</c>.
///
/// Один параметризованный конвертер вместо выводка одноразовых классов: одной только волне D2
/// нужны «акцент, когда живое», «фон чередующейся строки» и «приглушить, когда без тегов», и
/// каждый из трёх — тот же самый выбор из двух токенов. Ключи при этом остаются настоящими
/// именами токенов, так что правило «никаких литеральных цветов в разметке» держится.
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
        // Запасной цвет намеренно нейтральный серый: досюда добираются только дизайнер и
        // юнит-тесты, где у Application.Current ещё нет никаких ресурсов.
        return NocturneBrushes.Get(key, 0xFF9397AB);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Зачёркивание, когда истина, и ничего, когда ложь. Этим пользуется бейдж целей (D4) для окон,
/// которые селектор намеренно исключает: их зачёркивают, а не убирают из списка, потому что
/// «кто выпал и почему» — это половина того, ради чего разворот вообще нужен.
///
/// Конвертер, а не класс стиля, потому что <c>TextDecorations</c> — свойство-коллекция, булевой
/// формы у него нет, а отдавать назад надо именно статическое значение
/// <c>TextDecorations.Strikethrough</c> из самой Avalonia.
/// </summary>
public sealed class StrikethroughConverter : IValueConverter
{
    public static readonly StrikethroughConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Опасность для ошибок валидации (тех, что блокируют сохранение), янтарь предупреждения для всего остального.</summary>
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
/// Обычный цвет текста, как только у окна появился хоть один тег, и янтарь предупреждения, пока
/// тегов нет.
///
/// Версия до D1 красила «хорошо» зелёным; Nocturne же — это монохром плюс один акцент, поэтому
/// «всё в порядке» здесь просто обычный текст, а цветом выделяется только то состояние, которое
/// требует внимания.
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
