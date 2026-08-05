using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Шаг 2: тот ли шрифт мы вообще меряем.
///
/// ⚠️ <b>Этот файл обесценивает или подтверждает всё остальное.</b> Панель просит
/// <c>fonts:Inter#Inter, $Default</c>. Если в headless первое звено не разрешается, подбор
/// молча уходит на <c>$Default</c> — на этой машине Segoe UI, — и тогда каждый замер ширины и
/// каждая строка чернил считаются по шрифту, которого пользователь не видит. Разница не
/// теоретическая: «Горячая клавиша» кеглем 12 — это 102 px в Inter и 96 px в Segoe UI, то есть
/// 6% в ту сторону, где живут все дефекты обрезки.
///
/// <b>Что именно доказано, а что нет.</b> Доказано, что тесты меряют ТОТ ЖЕ файл шрифта и ТЕМ
/// ЖЕ формирователем строк, что и работающая панель: Inter приезжает встроенной коллекцией
/// пакета <c>Avalonia.Fonts.Inter</c> (одна и та же версия для обоих), а формирует строку
/// HarfBuzz внутри Skia — <c>UseSkia()</c> здесь и <c>UsePlatformDetect()</c> у панели дают на
/// Windows один и тот же слой текста. НЕ доказано совпадение при масштабе экрана, отличном от
/// 100%: headless всегда рисует при <c>RenderScaling = 1</c>, а округление раскладки идёт по
/// сетке пикселей устройства, так что при 125% числа поедут. Автор сидит на 3840×2160 при
/// 100% — то есть ровно на том масштабе, который здесь и меряется, — но у другого человека это
/// уже допущение, а не факт.
/// </summary>
public class UiFontTests
{
    /// <summary>
    /// Единиц на em у Inter. Число неслучайное и потому годится в опознавательный знак: у Inter
    /// это 2816, у Segoe UI и Cascadia Mono — обычные 2048.
    /// </summary>
    private const ushort InterDesignEmHeight = 2816;

    [Test]
    public async Task TheBodyFontResolvesToInterItself()
    {
        var (familyName, designEm) = await Ui.RunAsync(() =>
        {
            var typeface = new Typeface(BodyFamily());
            return FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs)
                ? (glyphs.FamilyName, glyphs.Metrics.DesignEmHeight)
                : ("не разрешился", (short)0);
        });

        await Assert.That(familyName).IsEqualTo("Inter");
        await Assert.That((ushort)designEm).IsEqualTo(InterDesignEmHeight);
    }

    [Test]
    public async Task InterCarriesTheCyrillicItselfAndNothingFallsBack()
    {
        // Подписи здесь по-русски. Если бы кириллицу подавал запасной шрифт, ширины считались
        // бы по нему, а тест на «Inter разрешился» этого не заметил бы.
        var missing = await Ui.RunAsync(() =>
        {
            FontManager.Current.TryGetGlyphTypeface(new Typeface(BodyFamily()), out var glyphs);
            return "ГорячаяклвишЗпутьдмкср".Distinct()
                .Where(ch => !glyphs!.TryGetGlyph(ch, out var glyph) || glyph == 0)
                .ToArray();
        });

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task MeasuringWithInterDiffersFromMeasuringWithThePlatformDefault()
    {
        // Собственно проверка «а не подсунули ли нам запасной». Она поймала бы и потерянный
        // WithInterFont в сборщике, и опечатку в адресе коллекции — оба случая выглядят как
        // работающие тесты, потому что мерить-то есть чем.
        var (inter, fallback) = await Ui.RunAsync(() => (
            Width("Горячая клавиша", BodyFamily(), 12),
            Width("Горячая клавиша", FontFamily.Default, 12)));

        await Assert.That(inter).IsNotEqualTo(fallback);

        // Числа приколочены нарочно: они — цена деления всех остальных тестов этой папки. Едет
        // ширина строки — едет и смысл слов «не влезло». Если этот тест упал, а остальные
        // молчат, разбираться надо здесь, а не подгонять число.
        await Assert.That(inter).IsEqualTo(102d);
        await Assert.That(fallback).IsEqualTo(96d);
    }

    [Test]
    public async Task TheMonospaceFontIsTheFirstNameAsked()
    {
        // NocturneFontFamilyMono — это «Cascadia Mono, Consolas, $Default». Первое имя есть на
        // всякой современной Windows, но подставься сюда второе или третье — моноширинные
        // колонки (время прогона, hwnd, лог) поехали бы по ширине незаметно.
        var familyName = await Ui.RunAsync(() =>
        {
            FontManager.Current.TryGetGlyphTypeface(new Typeface(MonoFamily()), out var glyphs);
            return glyphs?.FamilyName ?? "не разрешился";
        });

        await Assert.That(familyName).IsEqualTo("Cascadia Mono");
    }

    // ---- служебное -------------------------------------------------------------------------

    internal static FontFamily BodyFamily() => Token("NocturneFontFamily");

    internal static FontFamily MonoFamily() => Token("NocturneFontFamilyMono");

    private static FontFamily Token(string key)
    {
        var app = Application.Current
                  ?? throw new InvalidOperationException("Вызвано вне Ui.RunAsync — приложения нет.");

        return app.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) && value is FontFamily family
            ? family
            : throw new InvalidOperationException($"В Tokens.axaml нет FontFamily по ключу {key}.");
    }

    private static double Width(string text, FontFamily family, double size)
    {
        var block = new TextBlock { Text = text, FontFamily = family, FontSize = size };
        block.Measure(Size.Infinity);
        return block.DesiredSize.Width;
    }
}
