using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SmartMacro.App.ViewModels;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Обход раскладки: ПРАВИЛА по всем пяти видам, а не тесты по отдельным дефектам. Здесь их пять;
/// ещё два живут в <see cref="UiRenderTests"/> (чернила глифов и цвет) и одно в
/// <see cref="UiNotificationTests"/> — всего восемь.
///
/// Обходятся классы дефектов, которые в этой базе действительно случались и были найдены
/// глазами: подпись, не влезшая в свою колонку; строка состояния, у которой сосед отъел ширину;
/// подпись типа, въехавшая вплотную в подсказку инспектора; квадратик чекбокса, оказавшийся
/// напротив второй строки. Собрать и прогнать тесты для всех четырёх было недостаточно —
/// значения были верны, неверны были пиксели.
///
/// <b>Что доказано.</b> При ДВУХ размерах окна — 1520×840, с которым окно открывается, и
/// 1920×1040, то есть развёрнутом на обычном мониторе, — и при том наполнении, которое собирает
/// <see cref="UiScene"/>, ни одна подпись не обрезана, ни одна пара надписей не соприкасается,
/// ничто не уехало за правый край и квадратики чекбоксов стоят против первых строк.
///
/// ⚠️ <b>Чего НЕ доказано, и это важнее.</b> Про остальные размеры не доказано ничего:
/// пользователь тянет окно, а между минимумом 1100 и умолчанием 1520 лежит бесконечность. Тот
/// же обход, запущенный на меньших размерах, ПАДАЕТ, и падает по делу:
/// <list type="bullet">
///   <item><b>1280×760</b> — панель инструментов редактора переполняется на 100 px: сумма её
///     колонок 1184 при доступных 1084, звёздочка со строкой состояния схлопывается в 0 px, а
///     «Сохранить» встаёт на x=1276 при окне шириной 1280, то есть за краем. Это ровно то, что
///     волна F4 уже однажды чинила и описала словами «вытолкнуло «Сохранить» за край окна».</item>
///   <item><b>1100×620</b> (документированный минимум) — сверх того легенда канвы наезжает на
///     чип масштаба, а на экране настроек поля перекрываются на 55–173 px.</item>
/// </list>
/// Ничего из этого не чинилось: лекарство здесь — решение о том, чем в панели инструментов
/// жертвовать, а принимать его вслепую нельзя. Панель требует повышения прав, оболочка его не
/// имеет, посмотреть на результат не на чем. Чтобы включить эти размеры в гейт, достаточно
/// дописать <c>[Arguments]</c>.
///
/// Масштаб экрана здесь всегда 100%: headless рисует при <c>RenderScaling = 1</c>. Автор сидит
/// на 3840×2160 при 100%, то есть на том же, но у человека с 125% числа будут другие.
/// </summary>
public class UiLayoutTests
{
    /// <summary>
    /// Самый плотный ЗАКОННЫЙ зазор в панели сегодня — 2 px, между моноширинными колонками
    /// строки лога («12:13:21.739» и «INF»). Поэтому порог — «не касаться»: 1 px запаса и ни
    /// пикселем больше. Дефект, ради которого правило написано, имел зазор ровно 0.
    /// </summary>
    private const double MinimumGap = 0.5;

    private const double Epsilon = 0.5;

    // ---- правило 1: подпись, которой не хватило ширины ---------------------------------------

    [Test]
    [Arguments(1520.0, 840.0)]
    [Arguments(1920.0, 1040.0)]
    public async Task NoLabelIsNarrowerThanTheTextItPrints(double width, double height)
    {
        var problems = await Sweep(width, height, (mode, pieces) => pieces
            .Where(piece => piece.Control is TextBlock)
            .Where(piece => !piece.MayBeShortened)
            .Where(piece => piece.Bounds.Width + Epsilon < piece.NaturalWidth)
            .Select(piece => string.Create(CultureInfo.InvariantCulture,
                $"  {mode}: дали {piece.Bounds.Width:F0} px, нужно {piece.NaturalWidth:F0} px — {piece.Where}")));

        await Report(
            problems,
            "Подпись обрезана, и разметка этого НЕ разрешала." + NL + NL +
            "Правило простое: у кого стоит TextTrimming или TextWrapping — тот сам сказал, что " +
            "его можно укоротить (там пользовательские данные произвольной длины, и вместить их " +
            "нельзя в принципе). У кого не стоит — тот обязан влезть. Лечится либо тем, что " +
            "подписи дают место, либо тем, что разрешение выписывают явно; молча резать нельзя." +
            NL + NL +
            "Так выглядели два дефекта, найденные глазами: заголовок, не влезший в свою колонку, " +
            "и строка состояния, у которой соседняя кнопка отъела ширину.");
    }

    // ---- правило 2: укоротили до полного исчезновения ----------------------------------------

    [Test]
    [Arguments(1520.0, 840.0)]
    [Arguments(1920.0, 1040.0)]
    public async Task TextAllowedToShortenIsStillWideEnoughToSaySomething(double width, double height)
    {
        var problems = await Sweep(width, height, (mode, pieces) => pieces
            .Where(piece => piece.Control is TextBlock)
            .Where(piece => piece.MayBeShortened)
            .Where(piece => piece.Bounds.Width < MinimumVisibleWidth(piece.Control))
            .Select(piece => string.Create(CultureInfo.InvariantCulture,
                $"  {mode}: осталось {piece.Bounds.Width:F0} px, на одно «…» нужно " +
                $"{MinimumVisibleWidth(piece.Control):F0} px — {piece.Where}")));

        await Report(
            problems,
            "Строке разрешили укоротиться — и укоротили до того, что читать нечего." + NL + NL +
            "TextTrimming договаривается о многоточии, а не об исчезновении: ширина меньше " +
            "одного «…» означает, что сообщение просто не появилось, и пользователь никогда не " +
            "узнает, что оно было. Это тот же довод, по которому бейдж целей не показывает " +
            "«0 окон» при закрытой игре: виджет, который врёт молчанием, хуже отсутствующего." +
            NL + NL +
            "Обычная причина — сосед по сетке, выросший в ширину: колонка со звёздочкой отдаёт " +
            "ему всё до последнего пикселя и не жалуется.");
    }

    // ---- правило 3: две надписи в одной строке ------------------------------------------------

    [Test]
    [Arguments(1520.0, 840.0)]
    [Arguments(1920.0, 1040.0)]
    public async Task TwoLabelsOnOneLineNeverTouch(double width, double height)
    {
        var problems = await Sweep(width, height, (mode, pieces) =>
        {
            var visible = pieces
                .Where(piece => piece.Ink is { Width: > 0, Height: > 0 })
                .Where(piece => !InsideCanvas(piece.Control))
                .ToArray();

            var found = new List<string>();
            for (var i = 0; i < visible.Length; i++)
            {
                for (var j = i + 1; j < visible.Length; j++)
                {
                    var a = visible[i].Ink;
                    var b = visible[j].Ink;

                    // Только соседи ПО ГОРИЗОНТАЛИ. У строк, стоящих друг под другом,
                    // расстояние — это межстрочный интервал, и он законно бывает в один пиксель.
                    if (a.Top >= b.Bottom || b.Top >= a.Bottom)
                    {
                        continue;
                    }

                    var gap = Math.Max(a.Left - b.Right, b.Left - a.Right);
                    if (gap < MinimumGap)
                    {
                        found.Add(string.Create(CultureInfo.InvariantCulture,
                            $"  {mode}: зазор {gap:F0} px между «{visible[i].Text}» и " +
                            $"«{visible[j].Text}» ({visible[i].Where})"));
                    }
                }
            }

            return found;
        });

        await Report(
            problems,
            "Две надписи в одной строке сошлись вплотную или наехали друг на друга." + NL + NL +
            "Меряются ЧЕРНИЛА, а не границы контролов: TextBlock по умолчанию растянут на всю " +
            "ячейку, так что по границам столкновением оказалась бы любая пара соседних " +
            "колонок. Учитывается и обрезка предками — то, что уехало за край области " +
            "просмотра, на экране не сталкивается ни с чем." + NL + NL +
            "Ровно так выглядел дефект F4: подпись «Запустить под-макрос» въезжала в подсказку " +
            "инспектора «двойной клик — правка на месте» без единого пикселя зазора. Лечится " +
            "либо укорочением подписи (так и вышло — осталось «Под-макрос»), либо тем, что " +
            "надписи дают место." + NL + NL +
            "Содержимое канвы (Panel#Viewport) сюда не входит намеренно: коробки нод стоят в " +
            "координатах графа, а чип масштаба и легенда — накладки поверх них. «Две вещи над " +
            "панорамируемой поверхностью» — вопрос раскладки ГРАФА, а не виджетов.");
    }

    // ---- правило 4: ничего не уезжает за край окна -------------------------------------------

    [Test]
    [Arguments(1520.0, 840.0)]
    [Arguments(1920.0, 1040.0)]
    public async Task NothingIsPushedOffTheEdgeOfTheWindow(double width, double height)
    {
        var problems = await Sweep(width, height, (mode, pieces, window) => pieces
            .Where(piece => !InsideCanvas(piece.Control))
            .Where(piece => piece.Bounds.Right > window.Bounds.Width + Epsilon)
            .Select(piece => string.Create(CultureInfo.InvariantCulture,
                $"  {mode}: правый край {piece.Bounds.Right:F0} px при ширине окна " +
                $"{window.Bounds.Width:F0} px — {piece.Where}")));

        await Report(
            problems,
            "Контрол уехал за ПРАВЫЙ край окна — на экране его просто нет." + NL + NL +
            "Так себя ведёт сетка, у которой сумма колонок Auto больше доступной ширины: " +
            "звёздочка схлопывается в ноль, а лишнее не сжимается, а выталкивается вправо. " +
            "Правило поймало бы уже случавшееся: волна F4 записала, что напечатанные дважды " +
            "личность и причина паузы «стоили 260px и вытолкнули «Сохранить» за край окна, а " +
            "это ровно та кнопка, к которой тянешься после сеанса отладки»." + NL + NL +
            "Проверяется ТОЛЬКО правый край, и это не небрежность: инспектор и лог прокручиваются " +
            "по вертикали, так что содержимое ниже края окна там законно и нормально. По " +
            "горизонтали не прокручивается ничего — HorizontalScrollBarVisibility=\"Disabled\" " +
            "стоит явно, — поэтому вправо уехавшее не достаётся никак." + NL + NL +
            "Канва сюда не входит: её поверхность 4000×4000 и живёт внутри своей области " +
            "просмотра — «за краем» там означает «нужно панорамировать».");
    }

    // ---- правило 5: квадратик чекбокса против ПЕРВОЙ строки ----------------------------------

    [Test]
    [Arguments(1520.0, 840.0)]
    [Arguments(1920.0, 1040.0)]
    public async Task ACheckBoxSitsAgainstTheFirstLineOfItsLabel(double width, double height)
    {
        var problems = await Sweep(width, height, (mode, _, window) =>
        {
            var found = new List<string>();

            foreach (var box in UiTree.VisibleOfType<CheckBox>(window))
            {
                var mark = UiTree.VisibleOfType<Border>(box).FirstOrDefault(b => b.Name == "NormalRectangle");
                var label = UiTree.VisibleText(box).FirstOrDefault();
                if (mark is null || label.Control is null)
                {
                    continue;
                }

                var markRect = UiTree.BoundsIn(mark, box);
                var labelRect = UiTree.BoundsIn(label.Control, box);

                // Высота ОДНОЙ строки: подпись меряется без ограничения ширины, то есть в одну
                // строку, каким бы длинным ни был её текст.
                var lineHeight = UiTree.NaturalSize(label.Control).Height;
                var firstLineCentre = labelRect.Top + (lineHeight / 2);
                var drift = Math.Abs(markRect.Center.Y - firstLineCentre);

                // Не «попадает в полосу первой строки», а «стоит по её середине». Разница
                // существенная: у подписи в две строки квадратик уезжает вниз всего на половину
                // межстрочного расстояния и формально всё ещё попадает в первую строку — а
                // выглядит стоящим МЕЖДУ строками, что и было дефектом.
                if (drift > lineHeight / 3)
                {
                    found.Add(string.Create(CultureInfo.InvariantCulture,
                        $"  {mode}: «{label.Text}» — подпись занимает {labelRect.Height:F0} px при " +
                        $"{lineHeight:F0} px на строку, середина первой строки на " +
                        $"{firstLineCentre:F0} px, а центр квадратика на {markRect.Center.Y:F0} px " +
                        $"(разъезд {drift:F0} px)"));
                }
            }

            return found;
        });

        await Report(
            problems,
            "Квадратик чекбокса оказался напротив НЕ ПЕРВОЙ строки подписи." + NL + NL +
            "Тема центрирует квадратик по всему содержимому (Panel VerticalAlignment=Center в " +
            "ControlTheme для CheckBox), поэтому подпись в две строки уводит его вниз — к " +
            "тексту, к которому он не относится. Найдено глазами на экране настроек: пояснение " +
            "к галочке лежало внутри её же Content." + NL + NL +
            "Лекарство там же и применено: пояснение вынесено СОСЕДНИМ TextBlock'ом с отступом " +
            "слева, а в Content осталась одна строка.");
    }

    // ---- обход ------------------------------------------------------------------------------

    private static string NL => Environment.NewLine;

    /// <summary>Минимум, ниже которого укороченная строка перестаёт быть строкой, — одно «…».</summary>
    private static double MinimumVisibleWidth(Control control)
    {
        var probe = new TextBlock
        {
            Text = "…",
            FontFamily = control.GetValue(TextBlock.FontFamilyProperty),
            FontSize = control.GetValue(TextBlock.FontSizeProperty),
        };

        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    /// <summary>Контрол лежит внутри области просмотра канвы (граф либо накладка над ним).</summary>
    private static bool InsideCanvas(Visual visual)
    {
        for (var node = visual; node is not null; node = node.GetVisualParent())
        {
            if ((node as Control)?.Name == "Viewport")
            {
                return true;
            }
        }

        return false;
    }

    private static Task<IReadOnlyList<string>> Sweep(
        double width,
        double height,
        Func<ShellMode, IReadOnlyList<TextPiece>, IEnumerable<string>> rule) =>
        Sweep(width, height, (mode, pieces, _) => rule(mode, pieces));

    /// <summary>
    /// Поднимает панель, проходит все пять режимов и собирает то, на что жалуется правило.
    ///
    /// Режимы перебираются по одному, потому что оболочка держит все виды живыми и переключает
    /// их <c>IsVisible</c>: невидимый вид не измеряется вовсе, так что «посмотреть на все сразу»
    /// значило бы посмотреть на один.
    /// </summary>
    private static Task<IReadOnlyList<string>> Sweep(
        double width,
        double height,
        Func<ShellMode, IReadOnlyList<TextPiece>, Window, IEnumerable<string>> rule) =>
        Ui.RunAsync<IReadOnlyList<string>>(() =>
        {
            // Общая сцена: правила этого файла только смотрят, а поднять панель стоит полсекунды.
            var scene = UiScene.Shared(width, height);
            var problems = new List<string>();

            foreach (var mode in Enum.GetValues<ShellMode>())
            {
                scene.Select(mode);
                problems.AddRange(rule(mode, UiTree.VisibleText(scene.Window), scene.Window));
            }

            return problems;
        });

    private static async Task Report(IReadOnlyList<string> problems, string explanation)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(explanation + NL + NL + "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);
    }
}
