using System.Collections.ObjectModel;
using System.Globalization;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Один заголовок панели библиотеки: <c>pw · 6</c>, <c>баг госта · 5</c>, <c>прочее · 11</c>.
/// </summary>
public sealed class MacroLibraryGroupViewModel
{
    internal MacroLibraryGroupViewModel(string prefix, string header, IEnumerable<MacroListItemViewModel> items)
    {
        Prefix = prefix;
        Header = header;
        Items = [.. items];
    }

    /// <summary>Общий префикс имён либо <see cref="MacroLibraryGrouping.OtherGroup"/>.</summary>
    public string Prefix { get; }

    /// <summary>Готовый заголовок: префикс плюс количество.</summary>
    public string Header { get; }

    /// <summary>Макросы группы, по алфавиту.</summary>
    public ObservableCollection<MacroListItemViewModel> Items { get; }
}

/// <summary>
/// Правило группировки библиотеки. Чистый UI — на диске о нём никто не знает.
///
/// <b>Правило:</b> префикс макроса — это текст до первого <c>-</c>, а если дефиса нет, то всё имя
/// целиком. Префиксы, общие для ДВУХ И БОЛЕЕ макросов, становятся группой с заголовком из
/// префикса в нижнем регистре и числа участников; всё остальное сваливается в «прочее».
///
/// Половину «а если дефиса нет, то всё имя» требует макет, и её легко проглядеть: в его группе
/// «баг госта · 5» лежат и <c>Баг госта-Лучник</c>, и просто <c>Баг госта</c>, так что правило,
/// отправляющее любое бездефисное имя прямиком в «прочее», разорвало бы семью надвое. Половина
/// «двух и более» — обратная сторона той же монеты: без неё каждый разовый макрос получил бы
/// собственный заголовок, и панель состояла бы из одних заголовков.
///
/// Группы идут по префиксу, «прочее» приколочено последним, как бы оно ни сортировалось.
/// </summary>
public static class MacroLibraryGrouping
{
    /// <summary>Заголовок для макросов, чей префикс ни с кем не разделён.</summary>
    public const string OtherGroup = "прочее";

    /// <summary>Текст до первого <c>-</c> либо всё имя целиком.</summary>
    public static string PrefixOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash <= 0 ? name : name[..dash];
    }

    /// <summary>Разбивает (уже упорядоченную) библиотеку на разделы с заголовками.</summary>
    public static List<MacroLibraryGroupViewModel> Build(IEnumerable<MacroListItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var all = items.ToList();
        var byPrefix = all
            .GroupBy(item => PrefixOf(item.Name), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = new List<MacroLibraryGroupViewModel>();
        var others = new List<MacroListItemViewModel>();

        foreach (var group in byPrefix)
        {
            if (group.Count() < 2)
            {
                others.AddRange(group);
                continue;
            }

            var members = group.OrderBy(item => item.Name, StringComparer.CurrentCulture).ToList();
            groups.Add(new MacroLibraryGroupViewModel(group.Key, Header(group.Key, members.Count), members));
        }

        groups.Sort((left, right) =>
            string.Compare(left.Prefix, right.Prefix, StringComparison.CurrentCultureIgnoreCase));

        if (others.Count > 0)
        {
            others.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
            groups.Add(new MacroLibraryGroupViewModel(OtherGroup, Header(OtherGroup, others.Count), others));
        }

        return groups;
    }

    private static string Header(string prefix, int count) =>
        string.Create(CultureInfo.CurrentCulture, $"{prefix.ToLowerInvariant()} · {count}");
}
