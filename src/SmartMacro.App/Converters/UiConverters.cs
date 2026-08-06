using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SmartMacro.Contracts.Dto;

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

// NodeIdDisplayConverter отсюда удалён: он подменял пустую строку («ребро в никуда») на
// «— конец —», пока цели рёбер были строками с id внутри. Теперь список состоит из
// NodeChoiceViewModel, и «конец прогона» — такой же полноправный элемент со своей подписью, как
// и любая нода; подменять на лету стало нечего.

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

/// <summary>
/// Цвет строки журнала по её уровню. Параметр выбирает, ЧТО красим:
///
/// <list type="bullet">
///   <item><c>badge</c> — трёхбуквенный жетон уровня;</item>
///   <item><c>rule</c> — линейка слева от строки: цветная только у предупреждений и хуже;</item>
///   <item><c>message</c> — сам текст: отладка приглушена, остальное обычным цветом.</item>
/// </list>
///
/// <b>Линейка, а не заливка.</b> В Nocturne акцент — это линия: строка проблемы отмечена
/// двухпиксельной полосой у левого края и цветным жетоном, а фон у неё тот же, что у соседей.
/// Залитые красным строки в ленте, где ошибки идут пачками, превращают экран в сплошное пятно, в
/// котором как раз и не видно, где пачка началась.
///
/// Отдельным конвертером, а не через <see cref="TokenBrushConverter"/>: там выбор из двух по
/// булеву значению, здесь — из четырёх по перечислению, и вписывать в параметр четыре ключа
/// через палку значило бы завести собственный микроязык ради одного места.
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public static readonly LogLevelBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LogLevelDto level)
        {
            return null;
        }

        var role = parameter as string ?? "badge";

        // Линейка есть только у того, на что стоит смотреть. У остальных строк её нет вовсе —
        // серая полоса вдоль всей ленты была бы просто вторым разделителем.
        if (string.Equals(role, "rule", StringComparison.Ordinal))
        {
            return level switch
            {
                LogLevelDto.Warning => Warning,
                >= LogLevelDto.Error => Danger,
                _ => Brushes.Transparent,
            };
        }

        if (string.Equals(role, "message", StringComparison.Ordinal))
        {
            // Отладка — это фон, на котором ищут остальное: она приглушена, но читаема.
            return level <= LogLevelDto.Debug
                ? NocturneBrushes.Get("NocturneTextMutedBrush", 0xFF8B8FA2)
                : NocturneBrushes.Get("NocturneTextSecondaryBrush", 0xFFCFD3E5);
        }

        return level switch
        {
            <= LogLevelDto.Debug => NocturneBrushes.Get("NocturneTextGhostBrush", 0xFF4E5265),
            LogLevelDto.Information => NocturneBrushes.Get("NocturneNeutral600Brush", 0xFF75798C),
            LogLevelDto.Warning => Warning,
            _ => Danger,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush Warning => NocturneBrushes.Get("NocturneWarningBrush", 0xFFDBB277);

    private static IBrush Danger => NocturneBrushes.Get("NocturneDangerBrush", 0xFFDD8189);
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
