using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Корневое пространство имён теста намеренно — по той же причине, что и у Ids: помощник нужен и
// Macros, и ViewModels, и Ipc, а C# ищет имена и во внешних пространствах, так что ни одному
// файлу не приходится его импортировать.
namespace SmartMacro.Tests;

/// <summary>
/// Сверка человеческого текста с РЕСУРСОМ, из которого он собран, — а не с формулировкой.
///
/// <b>Зачем.</b> Строки вынесли в <c>Strings.resx</c> ровно затем, чтобы владелец мог править
/// формулировки, не заходя в код. Тест, сверяющий сообщение с литералом, отменяет это: первая же
/// вычитка красит гейт, и «поправить текст» превращается в «поправить текст и двести тестов».
///
/// <b>Почему не подстановка <c>Strings.Ключ</c> вместо литерала.</b> Там, где производственный код
/// возвращает ровно этот ресурс, такое сравнение — тавтология: обе стороны становятся одним
/// выражением, и тест теряет способность падать. Ревью этого проекта уже находило один такой
/// (<c>SelectorEvaluatorTests</c>) и признало дефектом.
///
/// <b>Что проверяется вместо этого.</b> Сообщения складываются как
/// <c>string.Format(Strings.Ключ, аргументы)</c>, и содержательного в них ровно две вещи: КАКОЙ
/// ключ выбран (то есть по какой ветке пошла логика) и ЧТО подставлено (число, имя ноды, имя
/// макроса). Обе видны, если сопоставить готовый текст с форматом: <see cref="Is"/> отвечает на
/// первый вопрос, <see cref="Arg"/> и <see cref="Args"/> — на второй. Слова между подстановками
/// при этом свободны: их правка гейт не роняет, а подмена ключа или потерянный аргумент — роняет.
///
/// ⚠️ Помощник НЕ различает ключи, чьи форматы совпадают с точностью до подстановки (в файле
/// такие есть: «{0} мс» — это и <c>Editor_RunLog_DurationMs</c>, и <c>Run_Detail_Delay</c>). Там,
/// где тест доказывает выбор между близкими вариантами, к утверждению «это тот ключ» надо
/// добавлять «и не соседний» — <c>Is</c> для того и возвращает <see cref="bool"/>.
/// </summary>
internal static class Msg
{
    /// <summary>
    /// Место подстановки: <c>{0}</c>, <c>{0:F1}</c>, <c>{0,-5}</c>. Литеральное
    /// <c>{переменная}</c> из <c>Node_Tag_Watermark</c> под это намеренно не подходит.
    /// </summary>
    private static readonly Regex Placeholder =
        new(@"\{(?<n>\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);

    /// <summary>Собран ли <paramref name="text"/> из формата <paramref name="format"/>.</summary>
    public static bool Is(string? text, string format) => Match(text, format) is not null;

    /// <summary>
    /// Единственная подстановка формата. Бросает, если формат не с одним местом подстановки или
    /// текст собран не из него, — молчаливый <c>null</c> здесь превратил бы проверку аргумента в
    /// проверку «оба null».
    /// </summary>
    public static string Arg(string? text, string format)
    {
        var args = Args(text, format);
        if (args.Count != 1)
        {
            throw new InvalidOperationException(
                "У формата " + args.Count.ToString(CultureInfo.InvariantCulture) +
                " мест подстановки, а Arg берёт единственное. Формат: «" + format + "».");
        }

        return args[0];
    }

    /// <summary>
    /// Подстановки формата по порядку номеров: <c>{0}</c>, <c>{1}</c>, … Бросает, если текст
    /// собран не из этого формата.
    /// </summary>
    public static IReadOnlyList<string> Args(string? text, string format) =>
        Match(text, format)
        ?? throw new InvalidOperationException(
            "Текст собран не из этого формата." + Environment.NewLine +
            "  формат: «" + format + "»" + Environment.NewLine +
            "  текст:  «" + text + "»");

    /// <summary>
    /// Подстановки текста, склеенного ИЗ НЕСКОЛЬКИХ ресурсов подряд, — по порядку появления.
    ///
    /// Так собран, например, итог ленты лога: «{0} записей» и « · проблем: {0}» — два ключа,
    /// приписанных друг к другу, и у каждого своё <c>{0}</c>. Через <see cref="Args"/> их не
    /// разобрать (номера совпадают), а склеивать разбор по разделителю в самом тесте нельзя:
    /// разделитель лежит ВНУТРИ второго ресурса, то есть это опять сверка с формулировкой.
    /// </summary>
    public static IReadOnlyList<string> Parts(string? text, params string[] formats)
    {
        var joined = string.Concat(formats);
        return Match(text, joined, byPlaceholderNumber: false)
               ?? throw new InvalidOperationException(
                   "Текст собран не из этой цепочки форматов." + Environment.NewLine +
                   "  форматы: «" + joined + "»" + Environment.NewLine +
                   "  текст:   «" + text + "»");
    }

    /// <summary>
    /// Разбор: места подстановки становятся группами, остальное — литералом. Сопоставление
    /// заякорено с обоих концов, так что «текст, который начинается так же» не пройдёт.
    /// </summary>
    private static IReadOnlyList<string>? Match(string? text, string format, bool byPlaceholderNumber = true)
    {
        if (text is null)
        {
            return null;
        }

        var pattern = new StringBuilder("^");
        var indices = new List<int>();
        var last = 0;

        foreach (Match placeholder in Placeholder.Matches(format))
        {
            pattern.Append(Regex.Escape(format[last..placeholder.Index])).Append("(.*?)");
            indices.Add(int.Parse(placeholder.Groups["n"].Value, CultureInfo.InvariantCulture));
            last = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(format[last..])).Append('$');

        var match = Regex.Match(text, pattern.ToString(), RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var captured = indices.Select((_, i) => match.Groups[i + 1].Value).ToList();
        if (!byPlaceholderNumber)
        {
            return captured;
        }

        // По НОМЕРУ места подстановки, а не по порядку в строке: «{1} из {0}» встречается, и
        // Args()[0] обязан отдавать то, что подставили под {0}.
        var byIndex = new SortedDictionary<int, string>();
        for (var i = 0; i < indices.Count; i++)
        {
            byIndex[indices[i]] = captured[i];
        }

        return [.. byIndex.Values];
    }
}
