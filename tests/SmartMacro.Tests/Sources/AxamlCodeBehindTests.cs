namespace SmartMacro.Tests.Sources;

/// <summary>
/// Код рядом с разметкой поднимает её через <c>InitializeComponent</c>, а не через
/// <c>AvaloniaXamlLoader.Load</c>.
///
/// Выглядят эти два вызова равнозначно, и равнозначными не являются: генератор имён Avalonia
/// кладёт присваивания полей <c>x:Name</c> ВНУТРЬ сгенерированного <c>InitializeComponent</c>,
/// так что голый загрузчик поднимает XAML и оставляет каждое именованное поле нулевым.
///
/// Отказ при этом злой и совершенно не похож на свою причину: первое разыменование <c>null</c>
/// попадает в <c>OnDataContextChanged</c>, а исключение оттуда обрывает распространение
/// DataContext по детям — и вся панель рисуется с молча пустыми привязками и каждым
/// <c>IsVisible</c>, вернувшимся к умолчанию. Ни сборка, ни тесты поведения этого не видят.
///
/// <b>Исключение ровно одно и опознаётся по разметке, а не по имени файла:</b> корень
/// <c>&lt;Application&gt;</c>. У объекта приложения именованных контролов не бывает, терять
/// нечего, и <c>AvaloniaXamlLoader.Load(this)</c> — штатный шаблон Avalonia. Всё остальное —
/// <c>Window</c>, <c>UserControl</c> и любой будущий корень — обязано звать
/// <c>InitializeComponent</c>, даже если <c>x:Name</c> в файле сегодня нет: именно так эта
/// ловушка и была взведена — соседние виды работали с голым загрузчиком до тех пор, пока в один
/// из них не добавили первое имя.
/// </summary>
public class AxamlCodeBehindTests
{
    private const string Loader = "AvaloniaXamlLoader.Load";
    private const string Initializer = "InitializeComponent(";

    [Test]
    public async Task EveryViewInitialisesItselfThroughTheGeneratedInitializer()
    {
        var problems = new List<string>();
        var checkedPairs = 0;

        foreach (var markup in RepositorySources.AxamlFiles)
        {
            var codeBehind = markup + ".cs";
            if (!File.Exists(codeBehind))
            {
                continue;
            }

            var root = RepositorySources.LoadMarkup(markup).Root!.Name.LocalName;
            if (root == "Application")
            {
                continue;
            }

            checkedPairs++;
            var where = RepositorySources.Relative(codeBehind);

            // Комментарии убираем: ловушка описана прозой прямо в MacrosView.axaml.cs, и без
            // очистки проверка падала бы на собственном объяснении.
            var code = RepositorySources.StripCommentsFromCSharp(File.ReadAllText(codeBehind));

            if (code.Contains(Loader, StringComparison.Ordinal))
            {
                problems.Add($"  {where}  — зовёт {Loader} (корень разметки <{root}>)");
            }

            if (!code.Contains(Initializer, StringComparison.Ordinal))
            {
                problems.Add($"  {where}  — не зовёт InitializeComponent вообще (корень <{root}>)");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "Вид поднимает свою разметку не тем способом." + NL + NL +
                "AvaloniaXamlLoader.Load и InitializeComponent выглядят равнозначно, но " +
                "присваивания полей x:Name генератор кладёт ВНУТРЬ InitializeComponent. Голый " +
                "загрузчик поднимет XAML и оставит каждое именованное поле нулевым." + NL + NL +
                "Симптом не похож на причину: первый null прилетает в OnDataContextChanged, " +
                "исключение оттуда обрывает раздачу DataContext детям, и панель рисуется с молча " +
                "пустыми привязками и каждым IsVisible по умолчанию. Ни сборка, ни тесты " +
                "поведения этого не показывают — только запуск и просмотр." + NL + NL +
                "Единственное исключение — корень <Application>: у объекта приложения " +
                "именованных контролов нет, и там загрузчик штатен. Отсутствие x:Name в файле " +
                "исключением НЕ является: ровно так ловушка и стояла — виды работали с голым " +
                "загрузчиком, пока в один из них не добавили первое имя." + NL + NL +
                "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);

        // Защита от пустого прогона: если перечисление разметки однажды перестанет что-то
        // находить, тест обязан упасть, а не молча зазеленеть.
        await Assert.That(checkedPairs).IsGreaterThan(0);
    }

    private static string NL => Environment.NewLine;
}
