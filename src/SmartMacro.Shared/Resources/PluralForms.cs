using System.Globalization;

namespace SmartMacro.Resources;

/// <summary>
/// Выбор формы множественного числа — один на всё дерево.
///
/// <b>Зачем это здесь, а не по месту.</b> resx множественных чисел не умеет: одна запись — одна
/// строка. Поэтому формы заводятся ЯВНЫМИ ключами (<c>..._One</c> / <c>..._Few</c> /
/// <c>..._Many</c>), а выбор между ними — код. Пока этот выбор писали по месту, он расходился:
/// в трёх view-model'ях лежали три копии разбора по <c>%10</c>/<c>%100</c>, а ещё в двух
/// согласования не было вовсе — отсюда «1 окон», «21 изменений» и «все 7 обхода». Копия номер
/// шесть будет ошибаться так же, поэтому копий больше нет.
///
/// <b>Правил два, и выбирает между ними культура.</b> Славянское (ru/uk/be) — три формы по
/// остаткам; всё остальное — две (единственное и прочее), и «прочим» служит <c>Many</c>. Так
/// английский перевод кладёт в <c>_Few</c> и <c>_Many</c> одну и ту же строку и получает
/// правильный результат, ничего здесь не трогая. Языки с иной раскладкой форм (польский,
/// арабский) добавляются сюда же — это и есть единственное место, где такое решается.
///
/// Культура берётся <see cref="CultureInfo.CurrentUICulture"/> — та же, по которой
/// <c>ResourceManager</c> выбирает сам файл ресурсов. Иначе формы и текст выбирались бы по
/// разным языкам.
/// </summary>
public static class PluralForms
{
    /// <summary>
    /// Форма, подходящая числу <paramref name="count"/>.
    ///
    /// Три аргумента — это три ЗНАЧЕНИЯ, а не ключа: звать полагается
    /// <c>PluralForms.Pick(n, Strings.X_One, Strings.X_Few, Strings.X_Many)</c>, чтобы
    /// удаление любого из трёх ключей ломало сборку, а не рисовало пустоту.
    /// </summary>
    public static string Pick(int count, string one, string few, string many) =>
        Pick(CultureInfo.CurrentUICulture, count, one, few, many);

    /// <summary>Та же выборка с явной культурой — нужна тестам и всему, что не на потоке UI.</summary>
    public static string Pick(CultureInfo culture, int count, string one, string few, string many)
    {
        // Отрицательных счётчиков в интерфейсе нет, но «−1 окно» лучше молчаливого «−1 окон».
        var n = Math.Abs(count);

        if (!IsSlavic(culture))
        {
            return n == 1 ? one : many;
        }

        return (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (2 or 3 or 4, not (12 or 13 or 14)) => few,
            _ => many,
        };
    }

    /// <summary>
    /// То же самое плюс подстановка самого числа: формы записаны как «{0} окно» / «{0} окна» /
    /// «{0} окон», и число подставляется по культуре форматирования.
    ///
    /// Форматируется по <see cref="CultureInfo.CurrentCulture"/>, а форма выбирается по
    /// <see cref="CultureInfo.CurrentUICulture"/>, и это не оплошность: первая отвечает за то,
    /// как выглядит число, вторая — за то, на каком языке идёт текст.
    /// </summary>
    public static string Format(int count, string one, string few, string many) =>
        string.Format(CultureInfo.CurrentCulture, Pick(count, one, few, many), count);

    /// <summary>Языки, у которых формы три. Сравнение по двухбуквенному коду — регион роли не играет.</summary>
    private static bool IsSlavic(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName is "ru" or "uk" or "be";
}
