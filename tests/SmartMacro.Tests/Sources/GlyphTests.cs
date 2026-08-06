using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// «Подпись кнопки — сам символ» и цветной эмодзи вместо него.
///
/// ⚠️ <b>Проверка читает ТРИ источника, и это не расширение ради расширения.</b> Раньше сканер
/// смотрел только <c>*.axaml</c>, и ровно поэтому глиф <c>⚑</c>, живший в C#
/// (<c>SettingsViewModel.Glyph</c>), проезжал мимо неё — нашли его глазами на ревью. А вынос
/// подписей в <c>Strings.resx</c> увёл бы под тот же ковёр ВСЕ глифы разом: подпись
/// «■ Стоп» — это одна строка ресурса, и в разметке от неё не остаётся ничего. Поэтому здесь
/// разметка, ресурсы и код C#, а не одна разметка.
///
/// Комментарии не читаются ни в одном из трёх: половина ловушек этого проекта описана в
/// комментариях ИМЕНЕМ И ЗНАКОМ («D1 напоролась на U+25B6 ▶»), так что проверка по сырому тексту
/// падала бы первым делом на собственной документации. В resx это <c>&lt;comment&gt;</c>, в
/// C# — <c>//</c> и <c>///</c>, в разметке — узлы-комментарии XML.
///
/// Ловушка описана в <c>Themes/Tokens.axaml</c> и стоила трёх заходов: D1 напоролась на
/// <c>U+25B6 ▶</c> (белый треугольник внутри кнопки с акцентным контуром — какое семейство
/// шрифтов ни проси), D4 на <c>U+26A0 ⚠</c>, D5 поймала <c>U+23F8 ⏸</c> уже по опыту. Механизм
/// один: у кодовой точки есть эмодзи-представление, Windows подаёт её из Segoe UI Emoji, а
/// глифы этого шрифта полноцветные и <c>Foreground</c> игнорируют начисто. Цепочка шрифтов не
/// спасает — подмена происходит ниже неё.
///
/// Комментарий у токенов заканчивается словами «любой новый глиф сверяйте с emoji-data, прежде
/// чем отправлять его в подпись». Здесь эта сверка автоматизирована.
///
/// <b>Почему проверок две.</b> Чёрный список ловит то, про что известно наверняка, и объясняет
/// ровно эту ловушку. Белый — всё остальное: символ, которого нет ни в одном списке, никем не
/// проверен, а проверить его может только человек, посмотрев на работающую панель. Вторая
/// проверка не утверждает, что глиф плохой; она утверждает, что его никто не смотрел, — и это
/// утверждение всегда истинно, поэтому ложных срабатываний у неё не бывает по построению.
///
/// ⚠️ <b>Этот тест — ЕДИНСТВЕННАЯ защита от ловушки, и замер по растру его не заменяет.</b>
/// В <c>Ui/UiRenderTests</c> есть правило «пиксели глифа обязаны быть смесью фона и
/// Foreground», и оно честно краснеет на <c>U+23F8 ⏸</c>. Но на <c>U+25B6 ▶</c> и
/// <c>U+26A0 ⚠</c> — двух точках, на которых и наступали, — оно молчит: обе есть в самом Inter,
/// поэтому в headless подбор шрифта до Segoe UI Emoji вообще не доходит (проверено:
/// <c>TryMatchCharacter</c> отвечает «Inter»). В работающей панели подмена происходит НИЖЕ
/// цепочки шрифтов, в DirectWrite, и headless-Skia этого не воспроизводит. Табличная проверка
/// по кодовым точкам поэтому остаётся главной, а растровая — дополнением.
/// </summary>
public class GlyphTests
{
    /// <summary>
    /// Кодовые точки BMP со свойством <c>Emoji</c> из <c>emoji-data.txt</c> — те, что Windows
    /// вправе подать из Segoe UI Emoji.
    ///
    /// ⚠️ Свойство здесь именно <c>Emoji</c>, а НЕ <c>Emoji_Presentation</c>. Разница
    /// принципиальна и проверена на этом проекте: у <c>U+26A0 ⚠</c> и <c>U+25B6 ▶</c>
    /// <c>Emoji_Presentation = No</c>, то есть по стандарту они должны рисоваться текстом, — и
    /// обе точки приехали цветными. Отбор по презентации по умолчанию пропустил бы два дефекта
    /// из трёх.
    ///
    /// Диапазоны за пределами BMP сюда не внесены намеренно: всё, что живёт выше <c>U+FFFF</c>,
    /// и так не пройдёт вторую проверку («символ никем не проверен»), а перечислять полторы
    /// тысячи точек ради чуть более точной формулировки — способ занести в список опечатку.
    /// </summary>
    private static readonly (int From, int To)[] EmojiCodePoints =
    [
        (0x203C, 0x203C), // ‼
        (0x2049, 0x2049), // ⁉
        (0x2139, 0x2139), // ℹ
        (0x2194, 0x2199), // ↔ ↕ ↖ ↗ ↘ ↙  (одиночные ↑ U+2191 и ↓ U+2193 сюда НЕ входят и безопасны)
        (0x21A9, 0x21AA), // ↩ ↪
        (0x231A, 0x231B), // ⌚ ⌛
        (0x2328, 0x2328), // ⌨
        (0x23CF, 0x23CF), // ⏏
        (0x23E9, 0x23F3), // ⏩ … ⏳
        (0x23F8, 0x23FA), // ⏸ ⏹ ⏺  — ловушка D5
        (0x24C2, 0x24C2), // Ⓜ
        (0x25AA, 0x25AB), // ▪ ▫
        (0x25B6, 0x25B6), // ▶  — ловушка D1 (малый ▸ U+25B8 безопасен)
        (0x25C0, 0x25C0), // ◀  (малый ◂ U+25C2 безопасен)
        (0x25FB, 0x25FE), // ◻ ◼ ◽ ◾  (■ U+25A0 безопасен)
        (0x2600, 0x2604), // ☀ ☁ ☂ ☃ ☄
        (0x260E, 0x260E), // ☎
        (0x2611, 0x2611), // ☑
        (0x2614, 0x2615), // ☔ ☕
        (0x2618, 0x2618), // ☘
        (0x261D, 0x261D), // ☝
        (0x2620, 0x2620), // ☠
        (0x2622, 0x2623), // ☢ ☣
        (0x2626, 0x2626), // ☦
        (0x262A, 0x262A), // ☪
        (0x262E, 0x262F), // ☮ ☯
        (0x2638, 0x263A), // ☸ ☹ ☺
        (0x2640, 0x2640), // ♀
        (0x2642, 0x2642), // ♂
        (0x2648, 0x2653), // знаки зодиака
        (0x265F, 0x2660), // ♟ ♠
        (0x2663, 0x2663), // ♣
        (0x2665, 0x2666), // ♥ ♦
        (0x2668, 0x2668), // ♨
        (0x267B, 0x267B), // ♻
        (0x267E, 0x267F), // ♾ ♿
        (0x2692, 0x2697), // ⚒ ⚓ ⚔ ⚕ ⚖ ⚗
        (0x2699, 0x2699), // ⚙  — поэтому шестерёнка в рельсе нарисована Path, а не глифом
        (0x269B, 0x269C), // ⚛ ⚜
        (0x26A0, 0x26A1), // ⚠ ⚡  — ловушка D4
        (0x26A7, 0x26A7), // ⚧
        (0x26AA, 0x26AB), // ⚪ ⚫
        (0x26B0, 0x26B1), // ⚰ ⚱
        (0x26BD, 0x26BE), // ⚽ ⚾
        (0x26C4, 0x26C5), // ⛄ ⛅
        (0x26C8, 0x26C8), // ⛈
        (0x26CE, 0x26CF), // ⛎ ⛏
        (0x26D1, 0x26D1), // ⛑
        (0x26D3, 0x26D4), // ⛓ ⛔
        (0x26E9, 0x26EA), // ⛩ ⛪
        (0x26F0, 0x26F5), // ⛰ ⛱ ⛲ ⛳ ⛴ ⛵
        (0x26F7, 0x26FA), // ⛷ ⛸ ⛹ ⛺
        (0x26FD, 0x26FD), // ⛽
        (0x2702, 0x2702), // ✂
        (0x2705, 0x2705), // ✅
        (0x2708, 0x270D), // ✈ ✉ ✊ ✋ ✌ ✍
        (0x270F, 0x270F), // ✏
        (0x2712, 0x2712), // ✒
        (0x2714, 0x2714), // ✔  — сосед безопасной ✓ U+2713, и вот он уже эмодзи
        (0x2716, 0x2716), // ✖  — то же рядом с безопасной ✕ U+2715
        (0x271D, 0x271D), // ✝
        (0x2721, 0x2721), // ✡
        (0x2728, 0x2728), // ✨
        (0x2733, 0x2734), // ✳ ✴
        (0x2744, 0x2744), // ❄
        (0x2747, 0x2747), // ❇
        (0x274C, 0x274C), // ❌
        (0x274E, 0x274E), // ❎
        (0x2753, 0x2755), // ❓ ❔ ❕
        (0x2757, 0x2757), // ❗
        (0x2763, 0x2764), // ❣ ❤
        (0x2795, 0x2797), // ➕ ➖ ➗
        (0x27A1, 0x27A1), // ➡
        (0x27B0, 0x27B0), // ➰
        (0x27BF, 0x27BF), // ➿
        (0x2934, 0x2935), // ⤴ ⤵
        (0x2B05, 0x2B07), // ⬅ ⬆ ⬇
        (0x2B1B, 0x2B1C), // ⬛ ⬜
        (0x2B50, 0x2B50), // ⭐
        (0x2B55, 0x2B55), // ⭕
        (0x3030, 0x3030), // 〰
        (0x303D, 0x303D), // 〽
        (0x3297, 0x3297), // ㊗
        (0x3299, 0x3299), // ㊙
        (0xFE0F, 0xFE0F), // VARIATION SELECTOR-16 — не глиф, а приказ «нарисуй предыдущий эмодзи»
    ];

    /// <summary>
    /// Символы, про которые известно, что они рисуются ТЕКСТОМ и наследуют <c>Foreground</c>, —
    /// потому что кто-то посмотрел на них в работающей панели.
    ///
    /// Пять из них названы в комментарии у <c>Themes/Tokens.axaml</c> («▸ ■ ↑ ↓ ×» — «так же
    /// безопасны»), два — в комментарии у панели отладчика в <c>MacrosView.axaml</c>
    /// («проверено, что текстом рисуются U+25B8 и U+25A0»). Остальные уже стоят в разметке и
    /// пережили не один просмотр интерфейса.
    ///
    /// ⚠️ Наличие глифа в Segoe UI Emoji признаком НЕ является, и проверять надо не это.
    /// В <c>seguiemj.ttf</c> есть и <c>U+25B8 ▸</c>, и <c>U+25A0 ■</c>, и <c>U+00D7 ×</c> — все
    /// три рисуются текстом и слушаются <c>Foreground</c>. Подмену вызывает не то, что у шрифта
    /// эмодзи есть такой глиф, а свойство <c>Emoji</c> у кодовой точки, по которому подбор шрифта
    /// и отправляет её в эмодзи-семейство.
    ///
    /// Пополнять этот список — нормальная часть работы; проверять глазами перед пополнением —
    /// тоже.
    /// </summary>
    private const string VerifiedTextGlyphs =
        "▸" + // ▸ малый треугольник вправо — «запустить»
        "◂" + // ◂ его зеркало
        "▲" + // ▲ треугольник вверх — сортировка и свёртка
        "▾" + // ▾ малый треугольник вниз — раскрывашка
        "■" + // ■ «стоп»
        "▢" + // ▢ пустой квадрат со скруглением — значок рельсы
        "✓" + // ✓ галочка (НЕ ✔ U+2714 — та эмодзи)
        "✕" + // ✕ крестик закрытия (НЕ ✖ U+2716 — та эмодзи)
        "×" + // × знак умножения — «×3» у счётчиков
        "−" + // − настоящий минус
        "→" + // → стрелка в подписях вида «было → стало»
        "↑" + // ↑ одиночная стрелка вверх
        "↓" + // ↓ одиночная стрелка вниз
        // ⤓ стрелка вниз к черте — «экспорт» на строке библиотеки. Единственный глиф списка,
        // внесённый НЕ по просмотру панели, а по таблицам шрифтов: свойства Emoji у U+2913 нет,
        // и в seguiemj.ttf такой кодовой точки нет вовсе (проверено по cmap) — то есть подать её
        // из шрифта эмодзи Windows просто неоткуда. Рисует её Segoe UI Symbol, одноцветно.
        // Читаемость при кегле 10 таким способом не проверяется — на это нужны глаза.
        "⤓" +
        // ⚑ флажок предупреждения на карточке диагностики среды. Взят вместо ⚠ U+26A0 ровно
        // потому, что у того есть свойство Emoji (ловушка D4); у U+2691 его нет, и в
        // seguiemj.ttf такой точки нет вовсе. Жил в C# и до расширения сканера сюда не попадал.
        "⚑";

    [Test]
    public async Task AGlyphWithAnEmojiPresentationNeverReachesMarkup()
    {
        var found = Symbols().Where(symbol => IsEmoji(symbol.CodePoint)).ToArray();

        if (found.Length > 0)
        {
            Assert.Fail(
                "В разметке, ресурсах или коде — кодовая точка с эмодзи-представлением. Windows подаст такой символ из " +
                "Segoe UI Emoji, а его глифы полноцветные и Foreground игнорируют начисто — " +
                "цепочка шрифтов от этого не спасает, подмена происходит ниже неё." + NL + NL +
                "На эту ловушку наступали трижды: D1 — U+25B6 ▶ (белый треугольник в кнопке с " +
                "акцентным контуром), D4 — U+26A0 ⚠, D5 — U+23F8 ⏸. Отбирать по свойству " +
                "Emoji_Presentation бесполезно: у U+26A0 и U+25B6 оно «No», и это их не спасло. " +
                "Признак — свойство Emoji в emoji-data.txt." + NL + NL +
                "Лечится СМЕНОЙ КОДОВОЙ ТОЧКИ, а не шрифтом. Проверены и рисуются текстом: " +
                DescribeVerifiedGlyphs() + "." + NL + NL +
                Describe(found));
        }

        await Assert.That(found.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EveryOtherSymbolInMarkupIsOnTheVerifiedList()
    {
        var found = Symbols()
            .Where(symbol => !IsEmoji(symbol.CodePoint) && !IsVerified(symbol.CodePoint))
            .ToArray();

        if (found.Length > 0)
        {
            Assert.Fail(
                "В разметке, ресурсах или коде — символ, которого нет ни в таблице эмодзи, ни в списке проверенных. " +
                "Это не приговор глифу — это значит, что на него никто не смотрел." + NL + NL +
                "Собрать и прогнать тесты недостаточно: и цветной эмодзи вместо белого " +
                "треугольника, и глиф, которого нет ни в одном шрифте цепочки, собираются без " +
                "единого предупреждения. Настоящая проверка одна — посмотреть на работающую " +
                "панель." + NL + NL +
                "Что делать: сверьте точку с emoji-data.txt (свойство Emoji). Есть — берите " +
                "другую точку. Нет — запустите панель, посмотрите на глиф вплотную (рисуется ли " +
                "он текстом, слушается ли Foreground) и внесите его в VerifiedTextGlyphs с " +
                "комментарием, зачем он нужен. Список для того и существует, чтобы этот просмотр " +
                "случался ровно один раз." + NL + NL +
                Describe(found));
        }

        await Assert.That(found.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TheVerifiedListNeverOverlapsTheEmojiTable()
    {
        // Страховка от самого дешёвого способа «починить» падение — дописать проблемный глиф в
        // белый список. Тогда замолчали бы обе проверки, а дефект остался бы на экране.
        var overlap = VerifiedTextGlyphs.Where(ch => IsEmoji(ch)).ToArray();

        await Assert.That(overlap.Length).IsEqualTo(0);
    }

    // ---- сканирование ---------------------------------------------------------------------

    private static string NL => Environment.NewLine;

    /// <summary>Один найденный символ вместе с тем, где он лежит.</summary>
    private readonly record struct Symbol(string Where, int CodePoint, string Text, string Owner);

    /// <summary>Всё, что человек может увидеть: разметка, ресурсы и код.</summary>
    private static IEnumerable<Symbol> Symbols() => [.. Markup(), .. Resources(), .. Code()];

    /// <summary>Символы разметки: значения атрибутов и текст элементов; комментарии — нет.</summary>
    private static IEnumerable<Symbol> Markup()
    {
        foreach (var file in RepositorySources.AxamlFiles)
        {
            var document = RepositorySources.LoadMarkup(file);

            foreach (var attribute in document.Descendants().Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                var owner = $"атрибут {attribute.Name.LocalName} у <{attribute.Parent!.Name.LocalName}>";
                foreach (var symbol in SymbolsOf(attribute.Value, RepositorySources.Where(file, attribute), owner))
                {
                    yield return symbol;
                }
            }

            foreach (var text in document.DescendantNodes().OfType<XText>())
            {
                var owner = text.Parent is { } parent ? $"текст <{parent.Name.LocalName}>" : "текст";
                foreach (var symbol in SymbolsOf(text.Value, RepositorySources.Where(file, text), owner))
                {
                    yield return symbol;
                }
            }
        }
    }

    /// <summary>
    /// Символы значений ресурсов. <c>&lt;comment&gt;</c> НЕ читается — он для человека, который
    /// вычитывает формулировки, и в нём ловушки описаны по именам и знакам, ровно как в
    /// комментариях кода.
    /// </summary>
    private static IEnumerable<Symbol> Resources()
    {
        foreach (var file in RepositorySources.ResxFiles)
        {
            var document = XDocument.Parse(File.ReadAllText(file), LoadOptions.SetLineInfo);
            foreach (var data in document.Root?.Elements("data") ?? [])
            {
                if (data.Element("value") is not { } value)
                {
                    continue;
                }

                var key = data.Attribute("name")?.Value ?? "?";
                foreach (var symbol in SymbolsOf(value.Value, RepositorySources.Where(file, value), $"ресурс {key}"))
                {
                    yield return symbol;
                }
            }
        }
    }

    /// <summary>
    /// Символы кода C#. Комментарии вычищены
    /// (<see cref="RepositorySources.StripCommentsFromCSharp"/>), строковые литералы — нет: они
    /// и есть то, ради чего проверка сюда пришла.
    ///
    /// Границы литералов отдельно не разбираются, и это не небрежность: идентификаторы в этом
    /// дереве латиницей по соглашению, а операторы C# — ASCII, так что за пределами литералов
    /// символ категории «знак» взяться неоткуда. Если он всё же там заведётся, показать его
    /// — правильное поведение, а не ложная тревога.
    /// </summary>
    private static IEnumerable<Symbol> Code()
    {
        foreach (var file in RepositorySources.AllCSharpFiles)
        {
            var stripped = RepositorySources.StripCommentsFromCSharp(File.ReadAllText(file));
            var lines = stripped.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var where = $"{RepositorySources.Relative(file)}:{i + 1}";
                foreach (var symbol in SymbolsOf(lines[i], where, "строка кода"))
                {
                    yield return symbol;
                }
            }
        }
    }

    private static IEnumerable<Symbol> SymbolsOf(string value, string where, string owner)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsInteresting(rune))
            {
                yield return new Symbol(where, rune.Value, rune.ToString(), owner);
            }
        }
    }

    /// <summary>
    /// Что вообще считается «глифом» для этой проверки.
    ///
    /// Вся ASCII отсеивается сразу: <c>=</c>, <c>+</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>,
    /// <c>~</c>, <c>^</c>, <c>$</c> по классификации Unicode тоже символы, но живут они в
    /// привязках и селекторах, а эмодзи среди них нет ни одного.
    ///
    /// Дальше пропускаются буквы и пунктуация: подписи здесь по-русски, а «», — и … — это текст,
    /// а не значки. Знаки валюты тоже: эмодзи среди них нет. Остаются категории символов —
    /// математические, прочие и модификаторы, — и отдельно селектор варианта U+FE0F, который по
    /// категории вообще метка (Mn), но означает ровно «нарисуй предыдущий символ эмодзи».
    /// </summary>
    private static bool IsInteresting(Rune rune) =>
        rune.Value >= 0x80 &&
        (rune.Value == 0xFE0F ||
         Rune.GetUnicodeCategory(rune) is UnicodeCategory.MathSymbol
             or UnicodeCategory.OtherSymbol
             or UnicodeCategory.ModifierSymbol);

    private static bool IsEmoji(int codePoint) =>
        EmojiCodePoints.Any(range => codePoint >= range.From && codePoint <= range.To);

    private static bool IsVerified(int codePoint) =>
        VerifiedTextGlyphs.Any(ch => ch == codePoint);

    private static string Describe(IReadOnlyCollection<Symbol> found)
    {
        var report = new StringBuilder("Найдено:");
        foreach (var symbol in found)
        {
            report.Append(NL)
                .Append("  ")
                .Append(symbol.Where)
                .Append("  U+")
                .Append(symbol.CodePoint.ToString("X4", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(symbol.Text)
                .Append("  — ")
                .Append(symbol.Owner);
        }

        return report.ToString();
    }

    private static string DescribeVerifiedGlyphs() =>
        string.Join(
            ", ",
            VerifiedTextGlyphs.Select(ch => $"{ch} U+{((int)ch).ToString("X4", CultureInfo.InvariantCulture)}"));
}
